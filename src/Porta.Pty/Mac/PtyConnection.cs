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
        protected override bool KillCore(int fd)
        {
            // Kill the entire process group by passing negative PID
            // This ensures all child processes spawned by the shell are also terminated
            // First try SIGHUP (standard terminal hangup signal)
            kill(-this.Pid, SIGHUP);
            
            // Give a brief moment for graceful shutdown
            Thread.Sleep(50);
            
            // Then send SIGKILL to ensure termination (cannot be caught or ignored)
            // Also kill the process group
            return kill(-this.Pid, SIGKILL) != -1 || kill(this.Pid, SIGKILL) != -1;
        }

        /// <inheritdoc/>
        protected override bool ResizeCore(int fd, int cols, int rows)
        {
            var size = new WinSize((ushort)rows, (ushort)cols);
            return ioctl(fd, TIOCSWINSZ, ref size) != -1;
        }

        /// <inheritdoc/>
        protected override bool WaitPid(int pid, ref int status)
        {
            // On macOS, use a polling approach with WNOHANG to avoid blocking indefinitely
            // This is more reliable on ARM64 macOS where blocking waitpid can sometimes hang
            const int maxAttempts = 600; // 60 seconds max (100ms * 600)
            
            for (int i = 0; i < maxAttempts; i++)
            {
                int result = waitpid(pid, ref status, WNOHANG);
                
                if (result == pid)
                {
                    // Process has exited
                    return true;
                }
                else if (result == -1)
                {
                    // Error occurred (e.g., ECHILD - no child process)
                    // This can happen if the process was already reaped
                    return false;
                }
                // result == 0 means process is still running, keep polling
                
                Thread.Sleep(100);
            }
            
            // Timeout - process didn't exit in time
            return false;
        }
    }
}
