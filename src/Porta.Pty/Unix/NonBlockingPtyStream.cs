// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Unix.NativeIo;

    /// <summary>
    /// A stream over a non-blocking pty descriptor, whose async reads and writes wait on the shared
    /// <see cref="PtyPoller"/> rather than on a thread.
    /// </summary>
    /// <remarks>
    /// Written against raw read(2)/write(2) rather than FileStream because the two are incompatible:
    /// FileStream on a non-blocking descriptor sees EAGAIN and surfaces it as an IOException, having
    /// no idea that the right response is to wait for readiness and try again. Keeping FileStream
    /// would have meant keeping the descriptor blocking, which is the thing being removed.
    ///
    /// Unbuffered, matching the blocking stream and the Windows side.
    /// </remarks>
    internal sealed class NonBlockingPtyStream : Stream
    {
        private readonly PtyDescriptor descriptor;
        private readonly FileAccess access;
        private int disposed;

        internal NonBlockingPtyStream(PtyDescriptor descriptor, FileAccess access)
        {
            this.descriptor = descriptor;
            this.access = access;
        }

        public override bool CanRead => this.access.HasFlag(FileAccess.Read);

        public override bool CanWrite => this.access.HasFlag(FileAccess.Write);

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => this.ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.ThrowIfDisposed();

                int read = this.TryTransfer(buffer, offset, count, reading: true, out int error);
                if (read >= 0)
                {
                    return read;
                }

                if (error == EAgain)
                {
                    await this.WaitReadableAsync(cancellationToken).ConfigureAwait(false);

                    // Re-checked after the wait, not only before it. Dispose completes pending
                    // waiters through Unregister, so without this the loop woke, saw EAGAIN again,
                    // and registered itself once more -- spinning on a descriptor the connection had
                    // already closed, forever.
                    this.ThrowIfDisposed();
                    continue;
                }

                if (error == EINTR)
                {
                    continue;
                }

                // EIO on a pty controller is the normal way the kernel reports that the other end
                // has gone. Reporting end of stream is what every caller means by it, and throwing
                // here would make an ordinary exit look like a fault.
                if (error == EIoError)
                {
                    return 0;
                }

                throw new IOException($"Reading from the pty failed with errno {error}");
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
            => this.WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);

            // A pty accepts writes up to the line discipline's buffer and no further, so a large
            // write is normally a partial one. Looping is not an edge case here.
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.ThrowIfDisposed();

                int written = this.TryTransfer(buffer, offset, count, reading: false, out int error);
                if (written >= 0)
                {
                    offset += written;
                    count -= written;
                    continue;
                }

                if (error == EAgain)
                {
                    await this.WaitWritableAsync(cancellationToken).ConfigureAwait(false);
                    this.ThrowIfDisposed();
                    continue;
                }

                if (error == EINTR)
                {
                    continue;
                }

                throw new IOException($"Writing to the pty failed with errno {error}");
            }
        }

        /// <summary>
        /// Nothing to do: this stream holds no buffer, which is the point.
        /// </summary>
        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                // The descriptor belongs to the connection, which closes it. Only the poller's
                // interest is ours to drop -- and it has to be dropped, because poll() reports a
                // closed descriptor as POLLNVAL on every pass, which is a spin rather than an error.
                PtyPoller.Instance.Unregister(this.descriptor.Raw);
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Registers interest in this descriptor becoming readable, under the reference count.
        /// </summary>
        /// <remarks>
        /// The REGISTRATION is what needs the reference, not the await. Reading the raw descriptor
        /// and registering it outside the count meant a wait could be armed after Close had already
        /// returned -- and since the stream's own Unregister had run by then, a reused descriptor
        /// number that never becomes readable left the read pending forever, completing neither
        /// with data nor with ObjectDisposedException. That is precisely the invariant
        /// PtyDescriptor exists to provide, so registering outside it undid the point.
        ///
        /// The reference is released before awaiting, because holding it across the wait would
        /// block Close for as long as the session is idle -- which is most of its life.
        /// </remarks>
        private Task WaitReadableAsync(CancellationToken cancellationToken)
            => this.Register(PtyPoller.Instance.WaitReadableAsync, cancellationToken);

        /// <summary>
        /// Registers interest in this descriptor accepting a write, under the reference count.
        /// </summary>
        private Task WaitWritableAsync(CancellationToken cancellationToken)
            => this.Register(PtyPoller.Instance.WaitWritableAsync, cancellationToken);

        private Task Register(Func<int, CancellationToken, Task> wait, CancellationToken cancellationToken)
        {
            if (!this.descriptor.TryAcquire())
            {
                throw new ObjectDisposedException(nameof(NonBlockingPtyStream));
            }

            try
            {
                return wait(this.descriptor.Raw, cancellationToken);
            }
            finally
            {
                this.descriptor.Release();
            }
        }

        private const int EIoError = 5;

        /// <summary>
        /// Refuses I/O once the connection has closed the descriptor.
        /// </summary>
        /// <remarks>
        /// This stream does not OWN the descriptor -- the connection does, and closes it -- so
        /// nothing stopped a read issued afterwards from calling read(2) on a number the process may
        /// since have handed to an unrelated file. The disposed flag existed and was never read.
        /// </remarks>
        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref this.disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(NonBlockingPtyStream));
            }
        }

        private unsafe int TryTransfer(byte[] buffer, int offset, int count, bool reading, out int error)
        {
            error = 0;

            // Reference-counted for the duration of the syscall, not merely flag-checked before it.
            // The connection owns this descriptor and closes it; without the reference, a close
            // landing between the check and the syscall could see the number reissued to something
            // else, and this would then read or WRITE that instead. See PtyDescriptor.
            if (!this.descriptor.TryAcquire())
            {
                throw new ObjectDisposedException(nameof(NonBlockingPtyStream));
            }

            try
            {
                fixed (byte* p = buffer)
                {
                    IntPtr result = reading
                        ? read(this.descriptor.Raw, (IntPtr)(p + offset), (UIntPtr)(uint)count)
                        : write(this.descriptor.Raw, (IntPtr)(p + offset), (UIntPtr)(uint)count);

                    long value = result.ToInt64();
                    if (value >= 0)
                    {
                        return (int)value;
                    }

                    error = Marshal.GetLastPInvokeError();
                    return -1;
                }
            }
            finally
            {
                this.descriptor.Release();
            }
        }
    }
}
