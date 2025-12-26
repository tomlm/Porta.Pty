// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Mac
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Mac.NativeMethods;

    /// <summary>
    /// Provides a pty connection for MacOS machines.
    /// </summary>
    internal class PtyProvider : Unix.PtyProvider
    {
        /// <inheritdoc/>
        public override Task<IPtyConnection> StartTerminalAsync(PtyOptions options, TraceSource trace, CancellationToken cancellationToken)
        {
            var winSize = new PtyWinSize((ushort)options.Rows, (ushort)options.Cols);

            string?[] terminalArgs = GetExecvpArgs(options);

            // Convert environment dictionary to "KEY=VALUE" string array for native code
            string?[]? envp = null;
            if (options.Environment != null && options.Environment.Count > 0)
            {
                envp = options.Environment
                    .Select(kvp => $"{kvp.Key}={kvp.Value}")
                    .Concat(new string?[] { null }) // NULL-terminated
                    .ToArray();
            }

            var controlCharacters = new Dictionary<TermSpecialControlCharacter, sbyte>
            {
                { TermSpecialControlCharacter.VEOF, 4 },
                { TermSpecialControlCharacter.VEOL, -1 },
                { TermSpecialControlCharacter.VEOL2, -1 },
                { TermSpecialControlCharacter.VERASE, 0x7f },
                { TermSpecialControlCharacter.VWERASE, 23 },
                { TermSpecialControlCharacter.VKILL, 21 },
                { TermSpecialControlCharacter.VREPRINT, 18 },
                { TermSpecialControlCharacter.VINTR, 3 },
                { TermSpecialControlCharacter.VQUIT, 0x1c },
                { TermSpecialControlCharacter.VSUSP, 26 },
                { TermSpecialControlCharacter.VSTART, 17 },
                { TermSpecialControlCharacter.VSTOP, 19 },
                { TermSpecialControlCharacter.VLNEXT, 22 },
                { TermSpecialControlCharacter.VDISCARD, 15 },
                { TermSpecialControlCharacter.VMIN, 1 },
                { TermSpecialControlCharacter.VTIME, 0 },
                { TermSpecialControlCharacter.VDSUSP, 25 },
                { TermSpecialControlCharacter.VSTATUS, 20 },
            };

            var term = new PtyTermios(
                inputFlag: TermInputFlag.ICRNL | TermInputFlag.IXON | TermInputFlag.IXANY | TermInputFlag.IMAXBEL | TermInputFlag.BRKINT | TermInputFlag.IUTF8,
                outputFlag: TermOuptutFlag.OPOST | TermOuptutFlag.ONLCR,
                controlFlag: TermConrolFlag.CREAD | TermConrolFlag.CS8 | TermConrolFlag.HUPCL,
                localFlag: TermLocalFlag.ICANON | TermLocalFlag.ISIG | TermLocalFlag.IEXTEN | TermLocalFlag.ECHO | TermLocalFlag.ECHOE | TermLocalFlag.ECHOK | TermLocalFlag.ECHOKE | TermLocalFlag.ECHOCTL,
                speed: TermSpeed.B38400,
                controlCharacters: controlCharacters);

            // Use native shim to spawn process - this avoids W^X issues
            // by performing fork+exec entirely in native code
            var result = pty_spawn(
                options.App,
                terminalArgs,
                envp,
                options.Cwd,
                ref term,
                ref winSize);

            if (result.Pid == -1)
            {
                throw new InvalidOperationException(
                    $"pty_spawn failed with error {result.Error}: {GetErrorMessage(result.Error)}");
            }

            return Task.FromResult<IPtyConnection>(new PtyConnection(result.MasterFd, result.Pid));
        }

        private static string GetErrorMessage(int errno)
        {
            // Common errno values
            return errno switch
            {
                1 => "EPERM (Operation not permitted)",
                2 => "ENOENT (No such file or directory)",
                12 => "ENOMEM (Cannot allocate memory)",
                13 => "EACCES (Permission denied)",
                _ => $"errno {errno}"
            };
        }
    }
}
