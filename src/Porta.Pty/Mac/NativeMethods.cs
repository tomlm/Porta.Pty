// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Mac
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;

    /// <summary>
    /// Defines native types and methods for interop with Mac OS system APIs.
    /// </summary>
    internal static class NativeMethods
    {
        internal const int STDIN_FILENO = 0;

        internal const ulong TIOCSWINSZ = 0x8008_7467;
        internal const int SIGHUP = 1;
        internal const int SIGTERM = 15;
        internal const int SIGKILL = 9;

        // waitpid options
        internal const int WNOHANG = 1;

        private const string LibPortaPty = "libporta_pty";

        public enum TermSpeed : uint
        {
            B38400 = 38400,
        }

        [Flags]
        public enum TermInputFlag : uint
        {
            /// <summary>
            /// Map BREAK to SIGINTR
            /// </summary>
            BRKINT = 0x00000002,

            /// <summary>
            /// Map CR to NL (ala CRMOD)
            /// </summary>
            ICRNL = 0x00000100,

            /// <summary>
            /// Enable output flow control
            /// </summary>
            IXON = 0x00000200,

            /// <summary>
            /// Any char will restart after stop
            /// </summary>
            IXANY = 0x00000800,

            /// <summary>
            /// Ring bell on input queue full
            /// </summary>
            IMAXBEL = 0x00002000,

            /// <summary>
            /// Maintain state for UTF-8 VERASE
            /// </summary>
            IUTF8 = 0x00004000,
        }

        [Flags]
        public enum TermOuptutFlag : uint
        {
            /// <summary>
            /// No output processing
            /// </summary>
            NONE = 0,

            /// <summary>
            /// Enable following output processing
            /// </summary>
            OPOST = 0x00000001,

            /// <summary>
            /// Map NL to CR-NL (ala CRMOD)
            /// </summary>
            ONLCR = 0x00000002,

            /// <summary>
            /// Map CR to NL
            /// </summary>
            OCRNL = 0x00000010,

            /// <summary>
            /// Don't output CR
            /// </summary>
            ONLRET = 0x00000040,
        }

        [Flags]
        public enum TermConrolFlag : uint
        {
            /// <summary>
            /// 8 bits
            /// </summary>
            CS8 = 0x00000300,

            /// <summary>
            /// Enable receiver
            /// </summary>
            CREAD = 0x00000800,

            /// <summary>
            /// Hang up on last close
            /// </summary>
            HUPCL = 0x00004000,
        }

        [Flags]
        public enum TermLocalFlag : uint
        {
            /// <summary>
            /// Visual erase for line kill
            /// </summary>
            ECHOKE = 0x00000001,

            /// <summary>
            /// Visually erase chars
            /// </summary>
            ECHOE = 0x00000002,

            /// <summary>
            /// Echo NL after line kill
            /// </summary>
            ECHOK = 0x00000004,

            /// <summary>
            /// Enable echoing
            /// </summary>
            ECHO = 0x00000008,

            /// <summary>
            /// Echo control chars as ^(Char)
            /// </summary>
            ECHOCTL = 0x00000040,

            /// <summary>
            /// Enable signals INTR, QUIT, [D]SUSP
            /// </summary>
            ISIG = 0x00000080,

            /// <summary>
            /// Canonicalize input lines
            /// </summary>
            ICANON = 0x00000100,

            /// <summary>
            /// Enable DISCARD and LNEXT
            /// </summary>
            IEXTEN = 0x00000400,
        }

        public enum TermSpecialControlCharacter
        {
            VEOF = 0,
            VEOL = 1,
            VEOL2 = 2,
            VERASE = 3,
            VWERASE = 4,
            VKILL = 5,
            VREPRINT = 6,
            VINTR = 8,
            VQUIT = 9,
            VSUSP = 10,
            VDSUSP = 11,
            VSTART = 12,
            VSTOP = 13,
            VLNEXT = 14,
            VDISCARD = 15,
            VMIN = 16,
            VTIME = 17,
            VSTATUS = 18,
        }

        /// <summary>
        /// Result structure from pty_spawn.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PtySpawnResult
        {
            public int MasterFd;
            public int Pid;
            public int Error;
        }

        /// <summary>
        /// Terminal settings for pty_spawn.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PtyTermios
        {
            public uint IFlag;
            public uint OFlag;
            public uint CFlag;
            public uint LFlag;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] CC;

            public uint ISpeed;
            public uint OSpeed;

            public PtyTermios(
                TermInputFlag inputFlag,
                TermOuptutFlag outputFlag,
                TermConrolFlag controlFlag,
                TermLocalFlag localFlag,
                TermSpeed speed,
                IDictionary<TermSpecialControlCharacter, sbyte> controlCharacters)
            {
                this.IFlag = (uint)inputFlag;
                this.OFlag = (uint)outputFlag;
                this.CFlag = (uint)controlFlag;
                this.LFlag = (uint)localFlag;
                this.CC = new byte[32];
                foreach (var kvp in controlCharacters)
                {
                    this.CC[(int)kvp.Key] = (byte)kvp.Value;
                }

                this.ISpeed = (uint)speed;
                this.OSpeed = (uint)speed;
            }
        }

        /// <summary>
        /// Window size for pty_spawn.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct PtyWinSize
        {
            public ushort Rows;
            public ushort Cols;
            public ushort XPixel;
            public ushort YPixel;

            public PtyWinSize(ushort rows, ushort cols)
            {
                this.Rows = rows;
                this.Cols = cols;
                this.XPixel = 0;
                this.YPixel = 0;
            }
        }

        /// <summary>
        /// Spawns a new process with a pseudo-terminal using the native shim.
        /// This avoids W^X issues by performing fork+exec entirely in native code.
        /// </summary>
        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern PtySpawnResult pty_spawn(
            [MarshalAs(UnmanagedType.LPStr)] string file,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[]? envp,
            [MarshalAs(UnmanagedType.LPStr)] string? workingDir,
            ref PtyTermios termios,
            ref PtyWinSize winsize);

        /// <summary>
        /// Resizes the PTY window.
        /// </summary>
        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_resize(int masterFd, ushort rows, ushort cols);

        /// <summary>
        /// Sends a signal to the child process.
        /// </summary>
        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_kill(int pid, int signal);

        /// <summary>
        /// Waits for the child process to exit.
        /// </summary>
        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_waitpid(int pid, ref int status, int options);

        /// <summary>
        /// Closes the PTY master file descriptor.
        /// </summary>
        [DllImport(LibPortaPty, SetLastError = true)]
        internal static extern int pty_close(int masterFd);

        /// <summary>
        /// Gets the last error code from the native library.
        /// </summary>
        [DllImport(LibPortaPty)]
        internal static extern int pty_get_errno();
    }
}
