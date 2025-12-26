// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Linux
{
    using System;
    using System.Runtime.InteropServices;

    internal static class NativeMethods
    {
        internal const int STDIN_FILENO = 0;

        internal const ulong TIOCSWINSZ = 0x5414;
        internal const int SIGHUP = 1;
        internal const int SIGTERM = 15;
        internal const int SIGKILL = 9;

        // waitpid options
        internal const int WNOHANG = 1;

        private const string LibPortaPty = "libporta_pty";

        public enum TermSpeed : uint
        {
            B38400 = 0x0F,
        }

        [Flags]
        public enum TermInputFlag : uint
        {
            BRKINT = 0x2,
            ICRNL = 0x100,
            IXON = 0x400,
            IXANY = 0x800,
            IMAXBEL = 0x2000,
            IUTF8 = 0x4000,
        }

        [Flags]
        public enum TermOuptutFlag : uint
        {
            OPOST = 1,
            ONLCR = 4,
        }

        [Flags]
        public enum TermConrolFlag : uint
        {
            CS8 = 0x30,
            CREAD = 0x80,
            HUPCL = 0x400,
        }

        [Flags]
        public enum TermLocalFlag : uint
        {
            ECHOKE = 0x800,
            ECHOE = 0x10,
            ECHOK = 0x20,
            ECHO = 0x8,
            ECHOCTL = 0x200,
            ISIG = 0x1,
            ICANON = 0x2,
            IEXTEN = 0x8000,
        }

        public enum TermSpecialControlCharacter
        {
            VEOF = 4,
            VEOL = 11,
            VEOL2 = 16,
            VERASE = 2,
            VWERASE = 14,
            VKILL = 3,
            VREPRINT = 12,
            VINTR = 0,
            VQUIT = 1,
            VSUSP = 10,
            VSTART = 8,
            VSTOP = 9,
            VLNEXT = 15,
            VDISCARD = 13,
            VMIN = 6,
            VTIME = 5,
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
                System.Collections.Generic.IDictionary<TermSpecialControlCharacter, sbyte> controlCharacters)
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
