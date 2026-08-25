// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using Microsoft.Win32.SafeHandles;

    /// <summary>
    /// A connection to a pseudoterminal spawned by native windows APIs.
    /// </summary>
    /// <remarks>
    /// Windows-only, and specifically Windows 10 1809 or later: ConPTY does not exist before that, and
    /// <see cref="NativeMethods.IsPseudoConsoleSupported"/> is the runtime gate that says so with a
    /// PlatformNotSupportedException naming the version.
    ///
    /// The VERSION in the annotation is not decoration. CsWin32's generated entry points carry their
    /// own floors (windows5.1.2600 for the job-object calls, windows6.0.6000 for the attribute-list
    /// ones), and a bare "windows" annotation does not satisfy them -- the platform-compatibility
    /// analyzer reported 22 warnings saying exactly that. Stating the real minimum satisfies all of
    /// them truthfully, where suppressing would have hidden a genuine question about which Windows
    /// versions this library supports.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.17763")]
    internal sealed class PseudoConsoleConnection : IPtyConnection
    {
        private readonly Process process;
        private readonly object disposeLock = new object();
        private PseudoConsoleConnectionHandles? handles;
        private bool isDisposed;
        private bool pseudoConsoleClosed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PseudoConsoleConnection"/> class.
        /// </summary>
        /// <param name="handles">The set of handles associated with the pseudoconsole.</param>
        public PseudoConsoleConnection(PseudoConsoleConnectionHandles handles)
        {
            // Use FileStream with the pipe handles for direct access
            // This avoids the buffering issues that can occur with AnonymousPipeClientStream
            Stream reader = new FileStream(
                new SafeFileHandle(handles.OutPipeOurSide.DangerousGetHandle(), ownsHandle: false),
                System.IO.FileAccess.Read,
                bufferSize: 0,  // No buffering
                isAsync: handles.UseAsyncIo);

            // Paired with PtyProvider.AnswerDeviceAttributes, and gated on the same condition it is:
            // out-of-band ConPTY is the only one that asks, we answer for the consumer, and so the
            // consumer must not see the question. A consumer that is a terminal emulator would
            // otherwise answer it too, and the second answer reaches the child as keyboard input.
            // Keep the two in step: whoever stops answering must stop hiding the query as well.
            this.ReaderStream = handles.PseudoConsoleHandle.AsksStartupDeviceAttributes
                ? new StartupDa1FilterStream(reader)
                : reader;


            this.WriterStream = new FileStream(
                new SafeFileHandle(handles.InPipeOurSide.DangerousGetHandle(), ownsHandle: false),
                System.IO.FileAccess.Write,
                bufferSize: 0,  // No buffering - writes go directly to pipe
                isAsync: handles.UseAsyncIo);

            this.handles = handles;
            this.Pid = handles.Pid;
            this.process = Process.GetProcessById(this.Pid);
            this.process.Exited += this.Process_Exited;
            this.process.EnableRaisingEvents = true;
        }

        /// <inheritdoc/>
        public event EventHandler<PtyExitedEventArgs>? ProcessExited;

        /// <inheritdoc/>
        public Stream ReaderStream { get; }

        /// <inheritdoc/>
        public Stream WriterStream { get; }

        /// <inheritdoc/>
        public int Pid { get; }

        /// <inheritdoc/>
        public int ExitCode => this.process.ExitCode;

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (this.disposeLock)
            {
                if (this.isDisposed)
                {
                    return;
                }

                this.isDisposed = true;
            }

            // Unsubscribe from events first to prevent callbacks during disposal
            this.process.Exited -= this.Process_Exited;

            // ConPTY cleanup order (per Microsoft documentation):
            // 1. Close the PseudoConsole handle - signals conhost to shut down
            // 2. Close the pipes - allows pending I/O to complete
            // 3. Close process/thread handles
            // 4. Close job object last - terminates any remaining processes

            if (this.handles != null)
            {
                // Step 1: Close the pseudo console first (calls ClosePseudoConsole)
                // This signals conhost.exe to shut down gracefully
                this.handles.PseudoConsoleHandle?.Dispose();

                // Step 2: Close the pipes
                // Close our side of the pipes - this will cause any pending reads to complete
                this.handles.InPipeOurSide?.Dispose();
                this.handles.OutPipeOurSide?.Dispose();

                // Step 3: Close process and thread handles
                this.handles.MainThreadHandle?.Dispose();
                this.handles.ProcessHandle?.Dispose();

                // Step 4: Dispose the job object last - this will terminate any remaining
                // child processes due to JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                this.handles.JobObjectHandle?.Dispose();

                this.handles = null;
            }

            // Dispose streams (they don't own the underlying handles)
            this.ReaderStream?.Dispose();
            this.WriterStream?.Dispose();

            // Dispose the Process object
            this.process?.Dispose();
        }

        /// <inheritdoc/>
        public void Kill()
        {
            this.process.Kill();
        }

        /// <inheritdoc/>
        public void Resize(int cols, int rows)
        {
            var handles = this.handles;
            if (handles == null || this.isDisposed)
            {
                throw new ObjectDisposedException(nameof(PseudoConsoleConnection));
            }

            lock (this.disposeLock)
            {
                if (this.pseudoConsoleClosed)
                {
                    // The child has exited and the pseudoconsole went with it, but this CONNECTION
                    // is still alive and undisposed -- so throwing ObjectDisposedException here
                    // would fault a caller who has disposed nothing. A terminal that resizes on
                    // every window change hits this the moment a shell exits, before the pane is
                    // torn down. There is nothing left to resize, so there is nothing to do.
                    return;
                }

                handles.PseudoConsoleHandle.Resize(cols, rows);
            }
        }

        /// <inheritdoc/>
        public bool WaitForExit(int milliseconds)
        {
            return this.process.WaitForExit(milliseconds);
        }

        private void Process_Exited(object? sender, EventArgs e)
        {
            // Check if we're disposed to avoid raising events during/after disposal
            if (this.isDisposed)
            {
                return;
            }

            lock (this.disposeLock)
            {
                if (!this.isDisposed && this.handles?.UseAsyncIo == true)
                {
                    // Close the pseudoconsole as soon as the child is gone, so a pending overlapped
                    // read completes instead of pending forever. Conhost holds the pipe ends OPEN
                    // after the child exits, until ClosePseudoConsole -- measured directly: child
                    // exit reported, read still pending 30 seconds later. Data conhost already wrote
                    // is not lost; a pipe whose writer has gone drains what is buffered and THEN
                    // reports broken, which FileStream surfaces as the 0-byte read the contract
                    // requires. Closed BEFORE raising the event so a handler can drain through EOF.
                    //
                    // Async path only. The blocking path has always behaved this way and stays
                    // untouched. Under the same lock Dispose uses for its flag, so the two callers
                    // of PseudoConsole.Dispose cannot interleave: its idempotence guard is a plain
                    // bool, safe only when calls are serialized.
                    this.handles.PseudoConsoleHandle.Dispose();
                    this.pseudoConsoleClosed = true;
                }
            }

            this.ProcessExited?.Invoke(this, new PtyExitedEventArgs(this.process.ExitCode));
        }

        /// <summary>
        /// handles to resources creates when a pseudoconsole is spawned.
        /// </summary>
        internal sealed class PseudoConsoleConnectionHandles
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="PseudoConsoleConnectionHandles"/> class.
            /// </summary>
            /// <param name="inPipeOurSide">the input pipe on the local side (we write to this).</param>
            /// <param name="outPipeOurSide">the output pipe on the local side (we read from this).</param>
            /// <param name="pseudoConsoleHandle">the handle to the pseudoconsole.</param>
            /// <param name="processHandle">the handle to the spawned process.</param>
            /// <param name="pid">the process ID.</param>
            /// <param name="mainThreadHandle">the handle to the main thread.</param>
            /// <param name="jobObjectHandle">the handle to the job object that manages process lifetime.</param>
            /// <param name="useAsyncIo">whether the local pipe handles support overlapped I/O.</param>
            public PseudoConsoleConnectionHandles(
                SafeFileHandle inPipeOurSide,
                SafeFileHandle outPipeOurSide,
                PseudoConsole pseudoConsoleHandle,
                SafeFileHandle processHandle,
                int pid,
                SafeFileHandle mainThreadHandle,
                SafeFileHandle jobObjectHandle,
                bool useAsyncIo)
            {
                this.InPipeOurSide = inPipeOurSide;
                this.OutPipeOurSide = outPipeOurSide;
                this.PseudoConsoleHandle = pseudoConsoleHandle;
                this.ProcessHandle = processHandle;
                this.Pid = pid;
                this.MainThreadHandle = mainThreadHandle;
                this.JobObjectHandle = jobObjectHandle;
                this.UseAsyncIo = useAsyncIo;
            }

            /// <summary>
            /// Gets the input pipe on the local side (we write to this to send to console).
            /// </summary>
            internal SafeFileHandle InPipeOurSide { get; }

            /// <summary>
            /// Gets the output pipe on the local side (we read from this to get console output).
            /// </summary>
            internal SafeFileHandle OutPipeOurSide { get; }

            /// <summary>
            /// Gets the handle to the pseudoconsole.
            /// </summary>
            internal PseudoConsole PseudoConsoleHandle { get; }

            /// <summary>
            /// Gets the handle to the spawned process.
            /// </summary>
            internal SafeFileHandle ProcessHandle { get; }

            /// <summary>
            /// Gets the process ID.
            /// </summary>
            internal int Pid { get; }

            /// <summary>
            /// Gets the handle to the main thread.
            /// </summary>
            internal SafeFileHandle MainThreadHandle { get; }

            /// <summary>
            /// Gets the handle to the job object that manages process lifetime.
            /// When this handle is closed, all processes assigned to the job are terminated.
            /// </summary>
            internal SafeFileHandle JobObjectHandle { get; }

            /// <summary>
            /// Gets a value indicating whether the local pipe handles support overlapped I/O.
            /// </summary>
            internal bool UseAsyncIo { get; }
        }
    }
}
