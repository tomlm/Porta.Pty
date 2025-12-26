// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Mac
{
    using System.Threading;
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

        /// <inheritdoc/>
        protected override bool Kill(int controller)
        {
            // First try SIGHUP (standard terminal hangup signal)
            // This is the proper signal for terminal processes
            bool result = pty_kill(this.Pid, SIGHUP) != -1;
            
            // Give a brief moment for graceful shutdown
            Thread.Sleep(100);
            
            // Then send SIGKILL to ensure termination (cannot be caught or ignored)
            pty_kill(this.Pid, SIGKILL);
            
            return result;
        }

        /// <inheritdoc/>
        protected override bool Resize(int controller, int cols, int rows)
        {
            return pty_resize(controller, (ushort)rows, (ushort)cols) != -1;
        }

        /// <inheritdoc/>
        protected override bool WaitPid(int pid, ref int status)
        {
            // Use blocking waitpid - the background thread will wait for the process to exit
            return pty_waitpid(pid, ref status, 0) != -1;
        }
    }
}
