// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32.SafeHandles;
    using static Porta.Pty.Unix.NativeIo;

    /// <summary>
    /// A stream connected to a pty.
    /// </summary>
    /// <remarks>
    /// Every read reports the child's exit as end of stream, a read of 0, on Linux as on macOS.
    ///
    /// Linux reports a controller whose other end has gone as EIO; macOS returns 0. FileStream
    /// turns EIO into IOException("Input/output error"), so on Linux an ordinary exit arrived as a
    /// fault, and a terminal host printed it as one: "Error reading from process: Input/output
    /// error", then "Process exited with code: 0". <see cref="NonBlockingPtyStream"/> already
    /// returns 0 for EIO; this brings the default path into line with it.
    ///
    /// The errno cannot be read from the exception -- on Unix a FileStream IOException carries
    /// COR_E_IO for every errno, measured, see AsyncIoTests -- so the descriptor is asked instead:
    /// a failed read on a controller that poll reports as hung up is the end of the stream. Any
    /// other failure is thrown as before. Asked only after a read has failed, so the cost lands on
    /// the one read that ends the stream and on no other.
    /// </remarks>
    internal sealed class PtyStream : FileStream
    {
        private readonly int fd;

        /// <summary>
        /// Initializes a new instance of the <see cref="PtyStream"/> class.
        /// </summary>
        /// <param name="fd">The fd to connect the stream to.</param>
        /// <param name="fileAccess">The access permissions to set on the fd.</param>
        /// <remarks>
        /// UNBUFFERED, matching the Windows connection's pipes. A FileStream write buffer sits in
        /// front of the pty and holds a write until it fills or something flushes, so
        /// WriterStream.Write("echo hi\n") reached the child on Windows and did nothing at all on
        /// Linux and macOS -- the shell simply sat at its prompt. Nothing reported an error, because
        /// buffering a write is not one.
        ///
        /// The 1024 came in with the original Microsoft-derived code and stayed; the Windows side was
        /// later rewritten to bufferSize: 0 for the same class of problem (see the comment in
        /// PseudoConsoleConnection) and Unix was never brought along.
        ///
        /// Reads lose their buffer too, which is the right trade here rather than a cost worth
        /// bearing: a terminal reads whatever a program just wrote, in the size it was written, and
        /// wants it now.
        /// </remarks>
        public PtyStream(int fd, FileAccess fileAccess)
            : base(new SafeFileHandle((IntPtr)fd, ownsHandle: false), fileAccess, bufferSize: 0, isAsync: false)
        {
            this.fd = fd;
        }

        /// <inheritdoc/>
        public override bool CanSeek => false;

        // Every read entry point is guarded itself, not only the one the terminal host calls today.
        // Which of them FileStream sends back through the others is a detail of its internal
        // strategy -- for a derived type like this one, some are and some are not -- and a guard
        // that depended on that routing would break silently when it changed.
        //
        // BeginRead and EndRead are deliberately NOT overridden. Stream's BeginRead already ends
        // in Read, which is guarded; overriding it to call ReadAsync made a cycle instead --
        // ReadAsync, base.ReadAsync, BeginRead, ReadAsync -- measured as a stack overflow.

        /// <inheritdoc/>
        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return base.Read(buffer, offset, count);
            }
            catch (IOException) when (this.IsHungUp())
            {
                return 0;
            }
        }

        /// <inheritdoc/>
        public override int Read(Span<byte> buffer)
        {
            try
            {
                return base.Read(buffer);
            }
            catch (IOException) when (this.IsHungUp())
            {
                return 0;
            }
        }

        /// <inheritdoc/>
        public override int ReadByte()
        {
            try
            {
                return base.ReadByte();
            }
            catch (IOException) when (this.IsHungUp())
            {
                return -1;
            }
        }

        /// <inheritdoc/>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (this.IsHungUp())
            {
                return 0;
            }
        }

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (this.IsHungUp())
            {
                return 0;
            }
        }

        /// <summary>
        /// Whether the other end of the pty has gone: every descriptor on the child's side closed.
        /// </summary>
        /// <remarks>
        /// A zero timeout, so this answers from the descriptor's current state and never waits. A
        /// descriptor already closed by the connection reports POLLNVAL rather than POLLHUP, so a
        /// read that failed because of a close is still thrown, as it was before.
        ///
        /// An interrupted poll is asked again, as <see cref="PtyPoller"/> does. A signal landing
        /// between the failed read and this probe returns -1 with EINTR, and taking that as "not
        /// hung up" would let the EIO escape and turn an ordinary exit back into a fault.
        /// </remarks>
        private bool IsHungUp()
        {
            var fds = new[] { new PollFd { Fd = this.fd, Events = POLLIN } };
            int ready;
            while ((ready = poll(fds, (UIntPtr)1, 0)) < 0 && Marshal.GetLastPInvokeError() == EINTR)
            {
            }

            return ready == 1 && (fds[0].Revents & POLLHUP) != 0;
        }
    }
}
