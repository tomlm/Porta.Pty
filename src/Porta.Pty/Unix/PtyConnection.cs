// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A connection to a Unix-style pseudoterminal.
    /// </summary>
    internal abstract class PtyConnection : IPtyConnection
    {
        private const int EINTR = 4;
        private const int ECHILD = 10;
        private const int ESRCH = 3;
        private const int SIGHUP = 1;
        private const int SIGKILL = 9;

        /// <summary>How long <see cref="KillAsync"/> waits after SIGHUP before SIGKILL, unless told otherwise.</summary>
        private static readonly TimeSpan DefaultKillGrace = TimeSpan.FromSeconds(1);

        /// <summary>How often a tree kill re-reads its group while descendants outlive the child.</summary>
        private static readonly TimeSpan GroupRecheckInterval = TimeSpan.FromMilliseconds(20);

        /// <summary>The low seven bits of the wait status of a stopped or continued child, on Linux and macOS.</summary>
        private const int StoppedOrContinued = 0x7f;

        private readonly bool useAsyncIo;
        private readonly int controller;
        private readonly PtyDescriptor descriptor;
        private readonly int pid;
        private readonly ManualResetEvent terminalProcessTerminatedEvent = new ManualResetEvent(false);
        private readonly TaskCompletionSource exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Serialises collecting the child with signalling it. While the child is not collected its pid
        /// cannot be reissued (at worst it is a zombie), so a signal sent under this lock with
        /// <see cref="reaped"/> still false can only reach the child.
        /// </summary>
        private readonly object signalGate = new object();
        private bool reaped;
        private int exitCode;
        private int exitSignal;
        private volatile bool hasExited;

        /// <summary>
        /// While above zero (a tree kill in progress), an exited child is left uncollected, so its pid -- and
        /// with it the id of the process group it leads -- cannot be reissued and the group can still be
        /// signalled safely. Guarded by <see cref="signalGate"/>.
        /// </summary>
        private int reapHolds;

        /// <summary>The child exited during a hold; whoever releases the last hold collects it.</summary>
        private bool reapDeferred;

        /// <summary>Completed when the child exits while held, so a tree kill can look at what is left of its group.</summary>
        private readonly TaskCompletionSource heldExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
        public event EventHandler<PtyExitedEventArgs>? ProcessExited;

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
        public int ExitSignal => this.exitSignal;

        /// <inheritdoc/>
        public bool HasExited => this.hasExited;

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
        public async Task KillAsync(bool entireProcessTree = true, TimeSpan? grace = null)
        {
            TimeSpan wait = grace ?? DefaultKillGrace;
            if (wait < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(grace), wait, "The grace period cannot be negative.");
            }

            if (!entireProcessTree)
            {
                this.SendSigHup(processGroup: false);
                await this.KillAfterGraceAsync(wait, processGroup: false).ConfigureAwait(false);
                return;
            }

            // forkpty makes the child a session and process group leader, so its pid is also the id of the
            // group its descendants start in. The child is held uncollected from here to the SIGKILL: a
            // descendant that ignores SIGHUP can outlive a child that dies of it, and once the child is
            // collected its group id is free to be reissued, so the group could no longer be signalled
            // safely. A zombie child keeps it reserved. The SIGKILL is always sent; the wait before it ends
            // early once the child has exited and nothing else in its group is alive.
            lock (this.signalGate)
            {
                if (this.reaped)
                {
                    return;
                }

                this.reapHolds++;
            }

            try
            {
                this.SendSigHup(processGroup: true);
                await this.WaitForGraceOrEmptyGroupAsync(wait).ConfigureAwait(false);
                this.SignalChild(SIGKILL, processGroup: true);
            }
            finally
            {
                this.ReleaseReapHold();
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
        protected bool SignalChild(int signal) => this.SignalChild(signal, processGroup: false);

        /// <summary>
        /// <see cref="SignalChild(int)"/>, to the child's whole process group when <paramref name="processGroup"/>
        /// is set. Equally safe: while the child is uncollected its pid, and so its group id, cannot be reissued.
        /// </summary>
        /// <param name="signal">The signal number.</param>
        /// <param name="processGroup">Whether to signal the group the child leads rather than the child alone.</param>
        /// <returns>True when the signal was sent or there was nothing left to signal.</returns>
        protected bool SignalChild(int signal, bool processGroup)
        {
            lock (this.signalGate)
            {
                if (this.reaped)
                {
                    return true;
                }

                if (!processGroup)
                {
                    return this.SendSignal(this.pid, signal) != -1;
                }

                if (this.SendSignal(-this.pid, signal) != -1)
                {
                    return true;
                }

                // ESRCH for the group while the child is uncollected means it has not called setsid yet
                // (forkpty can return in the parent first). It cannot have started anything by then, so
                // the child alone is the whole tree.
                return Marshal.GetLastPInvokeError() == ESRCH && this.SendSignal(this.pid, signal) != -1;
            }
        }

        /// <summary>
        /// Gets the grace-period step a Kill started and did not wait for (macOS), so a test can wait for its
        /// decision.
        /// </summary>
        internal Task? PendingGraceKill { get; private protected set; }

        /// <summary>
        /// Waits up to <paramref name="grace"/> for the child to exit without holding a thread, and sends
        /// SIGKILL if it has not.
        /// </summary>
        /// <param name="grace">How long to wait.</param>
        /// <param name="processGroup">Whether SIGKILL goes to the child's whole process group.</param>
        /// <returns>A task that completes once the child has exited or SIGKILL has been sent.</returns>
        protected async Task KillAfterGraceAsync(TimeSpan grace, bool processGroup)
        {
            Task exit = this.exited.Task;
            if (exit != await Task.WhenAny(exit, Task.Delay(grace)).ConfigureAwait(false))
            {
                // Asked of the kernel, not of the report: an exit can be reported late (one whose kqueue
                // event macOS lost comes from the reaper's sweep, up to a second on), and a child that has
                // already exited, of the SIGHUP or otherwise, is not sent SIGKILL.
                if (pty_exited_noreap(this.pid) != 1)
                {
                    this.SignalChild(SIGKILL, processGroup);
                }
            }
        }

        /// <summary>Blocks until the pid has exited, leaving it uncollected (waitid with WNOWAIT).</summary>
        [DllImport("libporta_pty", SetLastError = true)]
        private static extern int pty_wait_exit_noreap(int pid);

        /// <summary>1 if the pid has exited (not merely stopped), 0 if not, -1 on failure; collects nothing.</summary>
        [DllImport("libporta_pty", SetLastError = true)]
        private static extern int pty_exited_noreap(int pid);

        /// <summary>1 if the group has a living member other than excludePid, 0 if not, -1 if it cannot tell.</summary>
        [DllImport("libporta_pty", SetLastError = true)]
        private static extern int pty_group_has_live_members(int pgid, int excludePid);

        /// <summary>
        /// Collects the child if it has exited, marking it reaped under the signal lock. A stop or continue
        /// report is taken as well and returned as 0, since the child is still there: Darwin's waitid
        /// returns for a stopped child until its stop is taken (see <see cref="ChildWatcherThreadProc"/>).
        /// </summary>
        private int ReapNoHang(int pid, ref int status)
        {
            lock (this.signalGate)
            {
                if (this.reapHolds > 0)
                {
                    // Probed rather than collected: waitpid here would free the pid, and the group id
                    // with it, in the middle of the tree kill that is about to signal that group.
                    int exited = pty_exited_noreap(pid);
                    if (exited == 1)
                    {
                        this.reapDeferred = true;
                        this.heldExit.TrySetResult();
                        return PtyReaper.Deferred;
                    }

                    if (exited == 0)
                    {
                        return 0;
                    }
                }

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
        /// Gets or sets the clock the tree kill's grace period runs on. A seam for tests, which hand it a
        /// timer that never fires to show the grace was not what ended the wait.
        /// </summary>
        internal TimeProvider KillTimeProvider { get; set; } = TimeProvider.System;

        /// <summary>
        /// Gets or sets the group-membership probe: (pgid, excluded pid) to 1 for a living member, 0 for
        /// none, -1 for unknown. A seam for tests, which inject the unknown answer a failed /proc read gives.
        /// </summary>
        internal Func<int, int, int> GroupProbe { get; set; } = pty_group_has_live_members;

        /// <summary>
        /// Waits out the grace period, or less: once the held child has exited and its group has no other
        /// living member, there is nothing left for the SIGKILL to wait for. Until the child exits it is a
        /// living member itself, so a child that ignores SIGHUP still gets the whole grace period. When
        /// membership cannot be read, the whole grace period is waited.
        /// </summary>
        private async Task WaitForGraceOrEmptyGroupAsync(TimeSpan grace)
        {
            using var cancel = new CancellationTokenSource();
            Task elapsed = Task.Delay(grace, this.KillTimeProvider, cancel.Token);
            try
            {
                if (await Task.WhenAny(elapsed, this.heldExit.Task).ConfigureAwait(false) == elapsed)
                {
                    return;
                }

                // The child is a held zombie now. Whatever else is in its group (descendants finishing
                // their own SIGHUP handling) is re-read until it is gone or the grace runs out.
                while (true)
                {
                    int live = this.GroupProbe(this.pid, this.pid);
                    if (live == 0)
                    {
                        return;
                    }

                    Task next = live == 1 ? Task.Delay(GroupRecheckInterval) : elapsed;
                    if (await Task.WhenAny(elapsed, next).ConfigureAwait(false) == elapsed)
                    {
                        return;
                    }
                }
            }
            finally
            {
                cancel.Cancel();
            }
        }

        /// <summary>The SIGHUP that opens <see cref="KillAsync"/>; a child already gone is not an error.</summary>
        private void SendSigHup(bool processGroup)
        {
            if (!this.SignalChild(SIGHUP, processGroup))
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno != ESRCH)
                {
                    throw new InvalidOperationException($"Killing terminal failed with error {errno}");
                }
            }
        }

        /// <summary>
        /// Ends one reap hold. The last one collects a child that exited meanwhile, which the watcher or
        /// reaper handed back, and reports its exit exactly as they would have.
        /// </summary>
        private void ReleaseReapHold()
        {
            int status = 0;
            bool collected = false;
            lock (this.signalGate)
            {
                if (--this.reapHolds > 0 || !this.reapDeferred)
                {
                    return;
                }

                this.reapDeferred = false;
                int result;
                do
                {
                    result = this.WaitPidNoHang(this.pid, ref status);
                }
                while ((result == -1 && Marshal.GetLastPInvokeError() == EINTR)
                    || (result == this.pid && (status & StoppedOrContinued) == StoppedOrContinued));

                if (result == this.pid)
                {
                    this.reaped = true;
                    collected = true;
                }
                else if (result == -1)
                {
                    // ECHILD: collected elsewhere, so there is no status to report.
                    this.reaped = true;
                }
            }

            if (collected)
            {
                this.OnChildExited(status);
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

                if (result == PtyReaper.Deferred)
                {
                    // The tree kill holding the child collects and reports it when it finishes.
                    return;
                }

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
            this.hasExited = true;
            this.terminalProcessTerminatedEvent.Set();
            this.exited.TrySetResult();
            this.ProcessExited?.Invoke(this, new PtyExitedEventArgs(this.exitCode, this.exitSignal));
        }
    }
}
