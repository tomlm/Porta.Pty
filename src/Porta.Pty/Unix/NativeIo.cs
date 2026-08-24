// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.Runtime.InteropServices;

    /// <summary>
    /// The libc calls needed to drive a pty descriptor without blocking a thread on it.
    /// </summary>
    /// <remarks>
    /// libc rather than the shim: every one of these is plain POSIX, so there is no native build
    /// story here. Three of the constants genuinely differ between Linux and macOS and are resolved
    /// at runtime rather than compiled in -- getting O_NONBLOCK wrong does not fail loudly, it sets
    /// some other flag and leaves the descriptor blocking.
    /// </remarks>
    internal static class NativeIo
    {
        // On the library name: "libc" is deliberate. The runtime probes libc.so and hands it to
        // dlopen, which resolves it through ld.so.cache to the real libc.so.6 -- it never tries to
        // load the linker script at /usr/lib/libc.so, because it does not open a path. Verified
        // rather than assumed: the Linux CI leg exercises every one of these calls and passes.
        //
        // musl is the open question. Alpine has no libc.so under that name, and this has never been
        // run there. If musl is ever a target, the fix is NativeLibrary.SetDllImportResolver rather
        // than a different literal, since no single name covers both.

        internal const short POLLIN = 0x001;
        internal const short POLLOUT = 0x004;
        internal const short POLLERR = 0x008;
        internal const short POLLHUP = 0x010;
        internal const short POLLNVAL = 0x020;

        internal const int EINTR = 4;

        private static readonly bool IsMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// EAGAIN, which is also EWOULDBLOCK on both platforms. 35 on macOS, 11 on Linux.
        /// </summary>
        internal static int EAgain => IsMac ? 35 : 11;

        [StructLayout(LayoutKind.Sequential)]
        internal struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [DllImport("libc", SetLastError = true)]
        internal static extern int poll([In, Out] PollFd[] fds, UIntPtr nfds, int timeout);

        [DllImport("libc", SetLastError = true)]
        internal static extern IntPtr read(int fd, IntPtr buf, UIntPtr count);

        [DllImport("libc", SetLastError = true)]
        internal static extern IntPtr write(int fd, IntPtr buf, UIntPtr count);

        [DllImport("libc", SetLastError = true)]
        internal static extern int pipe([Out] int[] fds);

        [DllImport("libc", SetLastError = true)]
        internal static extern int close(int fd);

        /// <summary>
        /// Puts a descriptor into non-blocking mode.
        /// </summary>
        /// <remarks>
        /// Through the shim, NOT through a libc P/Invoke, because fcntl is VARIADIC --
        /// int fcntl(int, int, ...) -- and on Apple ARM64 variadic arguments are passed on the stack
        /// while fixed ones are passed in registers. A P/Invoke declaring three fixed ints puts the
        /// third in a register, the callee reads the stack, and F_SETFL applies whatever was there.
        /// It then returns 0, so the caller is told the descriptor is non-blocking when it is not.
        /// Asking for O_RDWR|O_NONBLOCK (0x6) was observed to produce flags of 0x400042.
        ///
        /// It happens to work on Linux, where both conventions pass integers in registers. So the
        /// tempting version of this -- no native code, just fcntl from C# -- is silently wrong on
        /// exactly one platform, and green on the CI leg that would have caught it.
        ///
        /// O_NONBLOCK also lives on the open file DESCRIPTION rather than the descriptor, so it is
        /// shared with anything dup'd from it. Harmless here: the child holds the other end of the
        /// pty, which is a separate description.
        /// </remarks>
        internal static bool SetNonBlocking(int fd) => pty_set_nonblocking(fd) != -1;

        [DllImport("libporta_pty", SetLastError = true)]
        private static extern int pty_set_nonblocking(int fd);

        /// <summary>
        /// Creates a pollable descriptor that reports child exits, or -1 when the kernel cannot.
        /// </summary>
        /// <remarks>
        /// -1 is not a failure to handle as one: it means this kernel has no such mechanism --
        /// Linux below 5.3 has no pidfd_open -- and the caller should keep polling waitpid instead.
        /// </remarks>
        [DllImport("libporta_pty", SetLastError = true)]
        internal static extern int pty_exit_queue();

        /// <summary>
        /// Starts watching one pid. Returns 0, or -1 including when the child has already exited.
        /// </summary>
        [DllImport("libporta_pty", SetLastError = true)]
        internal static extern int pty_exit_watch(int queue, int pid);

        /// <summary>
        /// Collects the pids that have exited, without blocking. Returns how many were written.
        /// </summary>
        [DllImport("libporta_pty", SetLastError = true)]
        internal static extern int pty_exit_drain(int queue, [Out] int[] pids, int max);
    }
}
