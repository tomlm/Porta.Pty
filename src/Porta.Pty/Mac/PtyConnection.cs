// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Mac
{
    using System;
    using static Porta.Pty.Mac.NativeMethods;

    /// <summary>
    /// A connection to a pseudoterminal on MacOS machines.
    /// </summary>
    internal class PtyConnection : Unix.PtyConnection
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PtyConnection"/> class.
        /// </summary>
        /// <param name="controller">The fd of the pty controller.</param>
        /// <param name="pid">The id of the spawned process.</param>
        public PtyConnection(int controller, int pid)
            : base(controller, pid)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PtyConnection"/> class.
        /// </summary>
        /// <param name="controller">The fd of the pty controller.</param>
        /// <param name="pid">The id of the spawned process.</param>
        /// <param name="useAsyncIo">Whether async reads and writes should avoid holding a thread.</param>
        public PtyConnection(int controller, int pid, bool useAsyncIo)
            : base(controller, pid, useAsyncIo)
        {
        }

        /// <inheritdoc/>
        protected override bool Kill(int controller)
        {
            // SIGHUP first, then SIGKILL only if the child is still there after a brief grace period.
            // Both go through SignalChild, so neither reaches a pid that has been collected meanwhile. The
            // grace period runs on a timer rather than on the caller, who used to be blocked for it.
            bool result = this.SignalChild(SIGHUP);
            this.PendingGraceKill = this.KillAfterGraceAsync(TimeSpan.FromMilliseconds(100), processGroup: false);

            return result;
        }

        /// <inheritdoc/>
        protected override int SendSignal(int pid, int signal) => pty_kill(pid, signal);

        /// <inheritdoc/>
        protected override bool Resize(int controller, int cols, int rows)
        {
            return pty_resize(controller, (ushort)rows, (ushort)cols) != -1;
        }

        /// <inheritdoc/>
        protected override bool Close(int controller)
        {
            return pty_close(controller) != -1;
        }

        /// <inheritdoc/>
        protected override int WaitPidNoHang(int pid, ref int status)
        {
            return pty_waitpid(pid, ref status, WNOHANG | WUNTRACED | WCONTINUED);
        }

        /// <inheritdoc/>
        protected override bool WaitPid(int pid, ref int status)
        {
            // Use blocking waitpid - the background thread will wait for the process to exit
            return pty_waitpid(pid, ref status, 0) != -1;
        }
    }
}
