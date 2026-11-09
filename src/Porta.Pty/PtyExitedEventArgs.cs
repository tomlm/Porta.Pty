// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;

    /// <summary>
    /// Event arguments that encapsulate data about the pty process exit.
    /// </summary>
    public class PtyExitedEventArgs : EventArgs
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PtyExitedEventArgs"/> class.
        /// </summary>
        /// <param name="exitCode">Exit code of the pty process.</param>
        internal PtyExitedEventArgs(int exitCode)
            : this(exitCode, exitSignal: 0)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PtyExitedEventArgs"/> class.
        /// </summary>
        /// <param name="exitCode">Exit code of the pty process; 0 when a signal ended it.</param>
        /// <param name="exitSignal">The signal that ended the pty process, or 0 when it exited.</param>
        internal PtyExitedEventArgs(int exitCode, int exitSignal)
        {
            this.ExitCode = exitCode;
            this.ExitSignal = exitSignal;
        }

        /// <summary>
        /// Gets or sets the exit code of the pty process.
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// Gets the signal that ended the pty process, or 0 when it exited on its own. On Unix a
        /// program killed by a signal has no exit code, so <see cref="ExitCode"/> is 0 then and only
        /// this tells it from a program that exited with 0 (SIGKILL is 9). Always 0 on Windows.
        /// </summary>
        public int ExitSignal { get; }
    }
}
