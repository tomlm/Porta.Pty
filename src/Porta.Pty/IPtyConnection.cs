// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.IO;

    /// <summary>
    /// Connection to a running pseudoterminal process.
    /// </summary>
    public interface IPtyConnection : IDisposable
    {
        /// <summary>
        /// Event fired when the pty process exits.
        /// </summary>
        event EventHandler<PtyExitedEventArgs>? ProcessExited;

        /// <summary>
        /// Gets the stream for reading data from the pty.
        /// </summary>
        Stream ReaderStream { get; }

        /// <summary>
        /// Gets a value indicating whether <see cref="ReaderStream"/>'s <c>ReadAsync</c> honours its
        /// cancellation token while waiting for data — returning without consuming a byte and
        /// without the stream having to be closed.
        /// </summary>
        /// <remarks>
        /// <para>True only for connections opened with <see cref="PtyOptions.UseAsyncIo"/>. A
        /// blocking-mode read can only be interrupted by closing the stream underneath it, which is
        /// exactly what a host handing the connection to a new owner cannot do — so a host that
        /// wants to stop reading without tearing the connection down needs to know which kind it
        /// holds. The stream types themselves are internal, so this is the one place the answer is
        /// visible.</para>
        /// <para>A default interface implementation, so existing implementers keep compiling; false
        /// is the safe answer, since callers fall back to blocking-read semantics.</para>
        /// </remarks>
        bool SupportsCancellableRead => false;

        /// <summary>
        /// Gets the stream for writing data to the pty.
        /// </summary>
        Stream WriterStream { get; }

        /// <summary>
        /// Gets the pty process ID.
        /// </summary>
        int Pid { get; }

        /// <summary>
        /// Gets the pty process exit code.
        /// </summary>
        /// <remarks>
        /// On Unix a process ended by a signal reports 0 here, as it always has; <see cref="ExitSignal"/>
        /// says which signal it was.
        /// </remarks>
        int ExitCode { get; }

        /// <summary>
        /// Gets the signal that ended the pty process, or 0 when it exited on its own or has not exited
        /// yet; <see cref="HasExited"/> tells those two apart. Matches
        /// <see cref="PtyExitedEventArgs.ExitSignal"/>.
        /// </summary>
        /// <remarks>
        /// Unix only: always 0 on Windows, which has no signals. A default interface implementation,
        /// so existing implementers keep compiling.
        /// </remarks>
        int ExitSignal => 0;

        /// <summary>
        /// Gets a value indicating whether the pty process has exited and its exit been reported, so that
        /// <see cref="ExitCode"/> and <see cref="ExitSignal"/> are final.
        /// </summary>
        bool HasExited => this.WaitForExit(0);

        /// <summary>
        /// Wait for the pty process to exit up to a given timeout.
        /// </summary>
        /// <param name="milliseconds">Timeout to wait for the process to exit.</param>
        /// <returns>True if the process exists within the timeout, false otherwise.</returns>
        bool WaitForExit(int milliseconds);

        /// <summary>
        /// Immediately terminates the pty process.
        /// </summary>
        void Kill();

        /// <summary>
        /// Terminates the pty process, and by default everything it started, without blocking the caller.
        /// </summary>
        /// <param name="entireProcessTree">
        /// True (the default) to end the process's descendants too. On Unix that is the pty's process
        /// group, which the process leads; a descendant that moved to a group of its own (a job-control
        /// shell's jobs) gets the hangup the shell and the terminal send it instead. The group gets SIGHUP,
        /// then SIGKILL after the grace period, which also ends a descendant that ignores SIGHUP and
        /// outlives the process: the process is kept uncollected until then, so its pid and group id
        /// cannot be reissued, and its exit is reported when the task completes. On Windows it is the job
        /// object the process was started in. False ends only the process itself.
        /// </param>
        /// <param name="grace">
        /// Unix: how long after SIGHUP before SIGKILL is sent; one second by default. With the tree it
        /// ends early once the process has exited and nothing else in its group is alive (or is waited out
        /// where membership cannot be read), and SIGKILL then goes to the group; without it, SIGKILL is
        /// skipped if the process exits sooner. Signals are only ever sent while the process is
        /// uncollected, so they cannot reach a process that reused its id. Ignored on Windows, which
        /// terminates at once.
        /// </param>
        /// <returns>
        /// A task that completes once SIGKILL has been sent to the group (with the tree), or once the
        /// process has exited or SIGKILL has been sent (without it).
        /// </returns>
        /// <remarks>
        /// A default interface implementation calling <see cref="Kill"/>, so existing implementers keep
        /// compiling.
        /// </remarks>
        System.Threading.Tasks.Task KillAsync(bool entireProcessTree = true, TimeSpan? grace = null)
        {
            this.Kill();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// Change the size of the pty.
        /// </summary>
        /// <param name="cols">The number of columns.</param>
        /// <param name="rows">The number of rows.</param>
        void Resize(int cols, int rows);
    }
}
