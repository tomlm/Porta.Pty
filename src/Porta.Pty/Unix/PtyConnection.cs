// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// A connection to a Unix-style pseudoterminal.
    /// </summary>
    internal abstract class PtyConnection : IPtyConnection
    {
        private const int EINTR = 4;
        private const int ECHILD = 10;
        private const int ESRCH = 3;

        /// <summary>The low seven bits of the wait status of a stopped or continued child, on Linux and macOS.</summary>
        private const int StoppedOrContinued = 0x7f;

        private readonly bool useAsyncIo;
        private readonly int controller;
        private readonly PtyDescriptor descriptor;
        private readonly int pid;
        private readonly ManualResetEvent terminalProcessTerminatedEvent = new ManualResetEvent(false);

        /// <summary>
        /// Serialises collecting the child with signalling it. While the child is not collected its pid
        /// cannot be reissued (at worst it is a zombie), so a signal sent under this lock with
        /// <see cref="reaped"/> still false can only reach the child.
        /// </summary>
        private readonly object signalGate = new object();

        /// <summary>Guards <see cref="processExited"/> and <see cref="exitedArgs"/> together.</summary>
        private readonly object exitGate = new object();
        private EventHandler<PtyExitedEventArgs>? processExited;
        private PtyExitedEventArgs? exitedArgs;
        private bool reaped;
        private int exitCode;
        private int exitSignal;
        private bool isDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PtyConnection"/> class.
        /// </summary>
        /// <param name="controller">The fd of the pty controller.</param>
        /// <param name="pid">The id of the spawned process.</param>
        public PtyConnection(int controller, int pid)
            : this(controller, pid, useAsyncIo: false)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PtyConnection"/> class.
        /// </summary>
        /// <param name="controller">The fd of the pty controller.</param>
        /// <param name="pid">The id of the spawned process.</param>
        /// <param name="useAsyncIo">Whether async reads and writes should avoid holding a thread.</param>
        public PtyConnection(int controller, int pid, bool useAsyncIo)
        {
            if (useAsyncIo)
            {
                if (!NativeIo.SetNonBlocking(controller))
                {
                    // Throws rather than falling back to the blocking pair. The fallback looked like
                    // the careful choice and was the opposite: UseAsyncIo publicly guarantees that a
                    // pending read holds no thread, the shared reaper still made the option appear
                    // enabled, and a caller holding hundreds of sessions had no way to discover it
                    // had silently gone back to a thread apiece. A spawn that cannot honour what was
                    // asked for should say so.
                    throw new InvalidOperationException(
                        "Could not put the pty controller into non-blocking mode, which "
                        + $"{nameof(PtyOptions.UseAsyncIo)} requires (errno "
                        + $"{Marshal.GetLastPInvokeError()}).");
                }

                if (!PtyReaper.Instance.IsSupported)
                {
                    // Refused rather than quietly polling instead. The exit notification this option
                    // depends on needs Linux 5.3 for pidfd_open; of the distributions .NET 10
                    // supports, only RHEL 8 is older. Carrying a polling fallback for that would mean
                    // shipping a second implementation that CI can never exercise, so the option
                    // says no and the default blocking path -- unchanged, and working everywhere --
                    // remains available.
                    throw new PlatformNotSupportedException(
                        $"{nameof(PtyOptions.UseAsyncIo)} needs kernel support for watching a child "
                        + "process exit, which on Linux means pidfd_open and so kernel 5.3 or newer.");
                }

                // Both streams share the one descriptor, so the mode is a property of the connection
                // rather than of either stream.
                this.descriptor = new PtyDescriptor(controller);
                this.ReaderStream = new NonBlockingPtyStream(this.descriptor, FileAccess.Read);
                this.WriterStream = new NonBlockingPtyStream(this.descriptor, FileAccess.Write);
            }
            else
            {
                this.descriptor = new PtyDescriptor(controller);
                this.ReaderStream = new PtyStream(controller, FileAccess.Read);
                this.WriterStream = new PtyStream(controller, FileAccess.Write);
            }

            this.useAsyncIo = useAsyncIo;
            this.controller = controller;
            this.pid = pid;
            if (useAsyncIo)
            {
                // One reaper for the whole process instead of a thread apiece. This is the half that
                // actually moves the number: making reads threadless still left a watcher per
                // connection blocked in waitpid.
                PtyReaper.Instance.Register(pid, this.ReapNoHang, this.OnChildExited);
            }
            else
            {
                var childWatcherThread = new Thread(this.ChildWatcherThreadProc)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Lowest,
                    Name = $"Watcher thread for child process {pid}",
                };

                childWatcherThread.Start();
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A handler added after the program has exited is called at once, on the caller's thread, with
        /// the same arguments, so an exception it throws reaches the caller that adds it. The exit can be
        /// reported before the caller that spawned the program has had a chance to subscribe, and
        /// <see cref="IPtyConnection"/> carries only the exit code, 0 for a program killed by a signal,
        /// so a late subscriber would otherwise lose <see cref="PtyExitedEventArgs.ExitSignal"/>
        /// (LateSubscriberTests).
        /// </remarks>
        public event EventHandler<PtyExitedEventArgs>? ProcessExited
        {
            add
            {
                PtyExitedEventArgs? already;
                lock (this.exitGate)
                {
                    already = this.exitedArgs;
                    if (already is null)
                    {
                        this.processExited += value;
                    }
                }

                if (already is not null)
                {
                    value?.Invoke(this, already);
                }
            }

            remove
            {
                lock (this.exitGate)
                {
                    this.processExited -= value;
                }
            }
        }

        /// <inheritdoc/>
        public Stream ReaderStream { get; }

        /// <inheritdoc/>
        public bool SupportsCancellableRead => this.useAsyncIo;

        /// <inheritdoc/>
        public Stream WriterStream { get; }

        /// <inheritdoc/>
        public int Pid => this.pid;

        /// <inheritdoc/>
        public int ExitCode => this.exitCode;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            this.isDisposed = true;

            this.ReaderStream?.Dispose();
            this.WriterStream?.Dispose();

            // Try to kill the process, but don't throw if it already exited
            this.TryKill();

            // ...and then CLOSE the controller fd, which nothing used to do.
            //
            // PtyStream wraps the fd with ownsHandle: false -- deliberately, because ReaderStream and
            // WriterStream are two streams over the SAME fd and letting either own it would make
            // disposing both a double close. The consequence was that disposing them closed neither,
            // and pty_close, which exists in the shim and in both platforms' NativeMethods, had no
            // callers anywhere in the library. Every pseudoterminal ever opened leaked its fd for the
            // life of the process.
            //
            // It surfaces as pty_spawn failing with ENXIO ("no pty devices available") after enough
            // terminals have come and gone -- which reads as a system limit rather than as a leak, and
            // reads that way most convincingly in exactly the long-lived process that leaks the most.
            // Found because moving this suite into a single MTP process ran four 24-spawn tests
            // back to back and the third started failing; each test alone had always been fine.
            this.TryClose();

            // Deliberately NOT unregistering from the reaper. TryKill above signals a child that is
            // not collected yet (SIGHUP, and on macOS SIGKILL if it outlives the grace period), but a
            // signalled child is not a collected one -- it stays a zombie until somebody waits on it.
            // Dropping the registration here meant nothing ever did, so a disposed connection leaked
            // a process table entry for the life of the host.
            //
            // The blocking path does not have that problem because its watcher thread stays in
            // waitpid until the child is collected, and keeps doing so after Dispose. Leaving the
            // registration in place is what matches it: the reaper collects the status on its next
            // pass and drops the entry itself.
            //
            // The churn test hid this, because it calls Kill and WaitForExit before Dispose, which
            // gives the reaper time to collect first.
        }

        /// <inheritdoc/>
        public void Kill()
        {
            if (Volatile.Read(ref this.reaped))
            {
                return;
            }

            if (!this.Kill(this.controller))
            {
                int errno = Marshal.GetLastWin32Error();
                // ESRCH means the process doesn't exist (already exited) - that's OK
                if (errno != ESRCH)
                {
                    throw new InvalidOperationException($"Killing terminal failed with error {errno}");
                }
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Goes through the counted path of the descriptor, like reads and writes: once the connection
        /// is disposed, the number it kept may already belong to the next terminal, and an ioctl on it
        /// would resize that one instead.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">The pseudoterminal is already closed.</exception>
        public void Resize(int cols, int rows)
        {
            if (!this.descriptor.TryAcquire())
            {
                throw new ObjectDisposedException(
                    nameof(PtyConnection),
                    $"The pseudoterminal of process {this.pid} is closed, so it cannot be resized.");
            }

            try
            {
                if (!this.Resize(this.descriptor.Raw, cols, rows))
                {
                    throw new InvalidOperationException($"Resizing terminal failed with error {Marshal.GetLastWin32Error()}");
                }
            }
            finally
            {
                this.descriptor.Release();
            }
        }

        /// <inheritdoc/>
        public bool WaitForExit(int milliseconds)
        {
            return this.terminalProcessTerminatedEvent.WaitOne(milliseconds);
        }

        /// <summary>
        /// OS-specific implementation of the pty-resize function.
        /// </summary>
        /// <param name="controller">The fd of the pty controller.</param>
        /// <param name="cols">The number of columns to resize to.</param>
        /// <param name="rows">The number of rows to resize to.</param>
        /// <returns>True if the function suceeded to resize the pty, false otherwise.</returns>
        protected abstract bool Resize(int controller, int cols, int rows);

        /// <summary>
        /// Kills the terminal process.
        /// </summary>
        /// <param name="controller">The fd of the pty controller.</param>
        /// <returns>True if the function succeeded in killing the process, false otherwise.</returns>
        protected abstract bool Kill(int controller);

        /// <summary>
        /// OS-specific implementation of closing the pty controller fd.
        /// </summary>
        /// <param name="controller">The fd of the pty controller.</param>
        /// <returns>True if the fd was closed, false otherwise.</returns>
        protected abstract bool Close(int controller);

        /// <summary>
        /// OS-specific waitpid that does not block, with WUNTRACED and WCONTINUED, for the shared reaper
        /// and the blocking watcher.
        /// </summary>
        /// <param name="pid">The process id to check.</param>
        /// <param name="status">The status of the process, when it has one.</param>
        /// <returns>
        /// The pid once it has exited, stopped or continued (the status tells which), 0 while there is
        /// nothing to report, -1 on failure.
        /// </returns>
        protected abstract int WaitPidNoHang(int pid, ref int status);

        /// <summary>
        /// OS-specific implementation of waiting on the given process id.
        /// </summary>
        /// <param name="pid">The process id to wait on.</param>
        /// <param name="status">The status of the process.</param>
        /// <returns>True if the function succeeded to get the status of the process, false otherwise.</returns>
        protected abstract bool WaitPid(int pid, ref int status);

        /// <summary>OS-specific kill(2) by pid; only <see cref="SignalChild"/> calls it.</summary>
        /// <param name="pid">The process id to signal.</param>
        /// <param name="signal">The signal number.</param>
        /// <returns>0 on success, -1 on failure with errno set.</returns>
        protected abstract int SendSignal(int pid, int signal);

        /// <summary>
        /// Sends a signal to the child unless it has already been collected, under the same lock that
        /// collecting it takes, so the pid cannot have been handed to another process meanwhile.
        /// </summary>
        /// <param name="signal">The signal number.</param>
        /// <returns>True when the signal was sent or there was nothing left to signal.</returns>
        protected bool SignalChild(int signal)
        {
            lock (this.signalGate)
            {
                if (this.reaped)
                {
                    return true;
                }

                return this.SendSignal(this.pid, signal) != -1;
            }
        }

        /// <summary>Blocks until the pid has exited, leaving it uncollected (waitid with WNOWAIT).</summary>
        [DllImport("libporta_pty", SetLastError = true)]
        private static extern int pty_wait_exit_noreap(int pid);

        /// <summary>
        /// Collects the child if it has exited, marking it reaped under the signal lock. A stop or continue
        /// report is taken as well and returned as 0, since the child is still there: Darwin's waitid
        /// returns for a stopped child until its stop is taken (see <see cref="ChildWatcherThreadProc"/>).
        /// </summary>
        private int ReapNoHang(int pid, ref int status)
        {
            lock (this.signalGate)
            {
                int result = this.WaitPidNoHang(pid, ref status);
                if (result == pid && (status & StoppedOrContinued) == StoppedOrContinued)
                {
                    return 0;
                }

                if (result == pid || (result == -1 && Marshal.GetLastPInvokeError() == ECHILD))
                {
                    this.reaped = true;
                }

                return result;
            }
        }

        /// <summary>
        /// Attempts to kill the process without throwing an exception.
        /// </summary>
        private void TryKill()
        {
            try
            {
                this.Kill();
            }
            catch
            {
                // Ignore errors during cleanup - process may have already exited
            }
        }

        /// <summary>Closes the controller fd without throwing; it may already be gone.</summary>
        private void TryClose()
        {
            try
            {
                // Through the descriptor, so the close waits for any transfer already inside a
                // read(2) or write(2) to finish. Closing underneath one would let the number be
                // reissued while a syscall is still using it.
                this.descriptor.Close(this.Close);
            }
            catch
            {
                // Ignore errors during cleanup.
            }
        }

        private void ChildWatcherThreadProc()
        {
            Debug.WriteLine($"Waiting on {this.pid}");

            int status = 0;

            // Wait without collecting, then collect under the signal lock, so no signal can be sent
            // between waitpid releasing the pid and the connection learning that it did.
            //
            // Darwin's waitid returns for a stopped child too, even with only WEXITED, and again on every
            // call until a waitpid with WUNTRACED takes the stop. ReapNoHang takes it under the lock and
            // returns 0, and the loop waits again: one pass per stop, none for the continue (measured on
            // macOS 27). Falling through to the blocking waitpid below would collect the later exit outside
            // the lock.
            while (pty_wait_exit_noreap(this.pid) == 0)
            {
                int result;
                do
                {
                    result = this.ReapNoHang(this.pid, ref status);
                }
                while (result == -1 && Marshal.GetLastPInvokeError() == EINTR);

                if (result == this.pid)
                {
                    this.OnChildExited(status);
                    return;
                }

                if (result == -1)
                {
                    if (Marshal.GetLastPInvokeError() == ECHILD)
                    {
                        this.MarkReaped();
                        return;
                    }

                    break;
                }

                // Paces the loop in case waitid ever keeps returning with no stop left to take.
                Thread.Sleep(10);
            }

            // Only after waitid itself failed, or waitpid failed in a way the loop above does not expect.
            if (!this.WaitPid(this.pid, ref status))
            {
                int errno = Marshal.GetLastWin32Error();
                Debug.WriteLine($"Wait failed with {errno}");
                if (errno == EINTR)
                {
                    this.ChildWatcherThreadProc();
                }
                else if (errno == ECHILD)
                {
                    // Collected by someone else: the pid is free and may already be another process's.
                    this.MarkReaped();
                }
                else
                {
                    // TODO: log that waitpid(3) failed with error {Marshal.GetLastWin32Error()}
                }

                return;
            }

            Debug.WriteLine($"Wait succeeded");
            this.MarkReaped();
            this.OnChildExited(status);
        }

        /// <summary>
        /// Records that the child is collected, under the lock <see cref="SignalChild"/> takes, so no signal
        /// leaves for its pid from here on.
        /// </summary>
        private void MarkReaped()
        {
            lock (this.signalGate)
            {
                this.reaped = true;
            }
        }

        /// <summary>
        /// Records an exit status and tells anyone waiting. Shared by the per-connection watcher and
        /// the process-wide reaper, so the two paths cannot drift.
        /// </summary>
        private void OnChildExited(int status)
        {
            const int SignalMask = 127;
            const int ExitCodeMask = 255;

            this.exitSignal = status & SignalMask;
            this.exitCode = this.exitSignal == 0 ? (status >> 8) & ExitCodeMask : 0;
            var args = new PtyExitedEventArgs(this.exitCode, this.exitSignal);
            EventHandler<PtyExitedEventArgs>? handlers;
            lock (this.exitGate)
            {
                this.exitedArgs = args;
                handlers = this.processExited;
                this.processExited = null;
            }

            this.terminalProcessTerminatedEvent.Set();
            handlers?.Invoke(this, args);
        }
    }
}
