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

        private readonly int controller;
        private readonly PtyDescriptor descriptor;
        private readonly int pid;
        private readonly ManualResetEvent terminalProcessTerminatedEvent = new ManualResetEvent(false);
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

            this.controller = controller;
            this.pid = pid;
            if (useAsyncIo)
            {
                // One reaper for the whole process instead of a thread apiece. This is the half that
                // actually moves the number: making reads threadless still left a watcher per
                // connection blocked in waitpid.
                PtyReaper.Instance.Register(pid, this.WaitPidNoHang, this.OnChildExited);
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
        public event EventHandler<PtyExitedEventArgs>? ProcessExited;

        /// <inheritdoc/>
        public Stream ReaderStream { get; }

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

            // Deliberately NOT unregistering from the reaper. TryKill above sends SIGHUP and then
            // SIGKILL, but a signalled child is not a collected one -- it stays a zombie until
            // somebody waits on it. Dropping the registration here meant nothing ever did, so a
            // disposed connection leaked a process table entry for the life of the host.
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
        public void Resize(int cols, int rows)
        {
            if (!this.Resize(this.controller, cols, rows))
            {
                throw new InvalidOperationException($"Resizing terminal failed with error {Marshal.GetLastWin32Error()}");
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
        /// OS-specific waitpid that does not block, for the shared reaper.
        /// </summary>
        /// <param name="pid">The process id to check.</param>
        /// <param name="status">The status of the process, when it has one.</param>
        /// <returns>The pid once it has exited, 0 while it is still running, -1 on failure.</returns>
        protected abstract int WaitPidNoHang(int pid, ref int status);

        /// <summary>
        /// OS-specific implementation of waiting on the given process id.
        /// </summary>
        /// <param name="pid">The process id to wait on.</param>
        /// <param name="status">The status of the process.</param>
        /// <returns>True if the function succeeded to get the status of the process, false otherwise.</returns>
        protected abstract bool WaitPid(int pid, ref int status);

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
                    // waitpid is already handled elsewhere.
                    // Not an error.
                }
                else
                {
                    // TODO: log that waitpid(3) failed with error {Marshal.GetLastWin32Error()}
                }

                return;
            }

            Debug.WriteLine($"Wait succeeded");
            this.OnChildExited(status);
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
            this.terminalProcessTerminatedEvent.Set();
            this.ProcessExited?.Invoke(this, new PtyExitedEventArgs(this.exitCode));
        }
    }
}
