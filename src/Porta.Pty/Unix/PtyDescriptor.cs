// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.Threading;

    /// <summary>
    /// The pty controller descriptor, closed only once every in-flight syscall against it has
    /// finished.
    /// </summary>
    /// <remarks>
    /// Exists to close a use-after-close window that a disposed FLAG cannot. The streams do not own
    /// this descriptor -- the connection does, and closes it -- so a sequence of "check disposed,
    /// then read(fd)" is a time-of-check/time-of-use race: if the close lands between the two and
    /// anything in the process opens anything at all, the number may already have been reissued.
    /// Reading a stranger leaks their data; WRITING one puts pty input into an unrelated file, which
    /// is silent corruption of state outside this library.
    ///
    /// A flag can only narrow that window, never close it. Counting the transfers in flight and
    /// making the close wait for them closes it: after Close returns, no syscall against this
    /// descriptor is running or can start.
    ///
    /// The wait is bounded and short by construction. Reads and writes take a reference only on the
    /// non-blocking path, where read(2) and write(2) return immediately rather than parking in the
    /// kernel; on the blocking path PtyStream reads and writes the raw number. Resize takes one on
    /// both paths, for an ioctl that returns at once.
    /// </remarks>
    internal sealed class PtyDescriptor
    {
        private readonly int fd;
        private int activeTransfers;
        private int closed;

        internal PtyDescriptor(int fd) => this.fd = fd;

        /// <summary>
        /// The raw descriptor number.
        /// </summary>
        /// <remarks>
        /// Resize takes a reference with <see cref="TryAcquire"/> while it uses it, on both paths, and
        /// so do reads and writes on the non-blocking path; the blocking PtyStream uses the raw number.
        /// After the close the number may already belong to another terminal, and an ioctl on it
        /// resized that one.
        /// </remarks>
        internal int Raw => this.fd;

        /// <summary>
        /// Takes a reference for the duration of one syscall. False once the descriptor is closed.
        /// </summary>
        /// <remarks>
        /// The flag is re-read AFTER incrementing, which is the part that makes this correct: a
        /// close that begins between the first read and the increment would otherwise proceed while
        /// this transfer is already counted out.
        /// </remarks>
        internal bool TryAcquire()
        {
            Interlocked.Increment(ref this.activeTransfers);

            if (Volatile.Read(ref this.closed) != 0)
            {
                Interlocked.Decrement(ref this.activeTransfers);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Releases a reference taken by <see cref="TryAcquire"/>.
        /// </summary>
        internal void Release() => Interlocked.Decrement(ref this.activeTransfers);

        /// <summary>
        /// Closes the descriptor once no transfer is running, using the platform's close.
        /// </summary>
        internal void Close(Func<int, bool> close)
        {
            if (Interlocked.Exchange(ref this.closed, 1) != 0)
            {
                return;
            }

            var spin = default(SpinWait);
            while (Volatile.Read(ref this.activeTransfers) > 0)
            {
                spin.SpinOnce();
            }

            close(this.fd);
        }
    }
}
