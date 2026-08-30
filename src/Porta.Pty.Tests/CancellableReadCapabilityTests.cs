// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using AwesomeAssertions;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// <see cref="IPtyConnection.SupportsCancellableRead"/>: the one place a host can learn whether
    /// stopping a read requires closing the stream.
    /// </summary>
    /// <remarks>
    /// A host that hands a live connection to a new owner must stop reading WITHOUT closing the
    /// stream -- the stream is the thing being handed over. Only the async-IO streams can do that;
    /// a blocking read is interruptible by teardown alone. The capability exists because the stream
    /// types are internal and a host cannot ask them directly.
    /// </remarks>
    [TestClass]
    public class CancellableReadCapabilityTests
    {
        private const int TestTimeoutMs = 30_000;

        /// <summary>
        /// The shape every passing Windows test in this suite uses: the FULL PATH to cmd.exe and a
        /// verbatim ["/c", command]. A bare "cmd.exe" with composed quoting died at spawn under
        /// ConPTY, which surfaced as EOF on the very first read.
        /// </summary>
        private static PtyOptions ShellOptions(string name, bool useAsyncIo, string? unixCommand = null, string? windowsCommand = null) => new PtyOptions
        {
            Name = name,
            Cols = 80,
            Rows = 25,
            Cwd = Environment.CurrentDirectory,
            App = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
                : "/bin/sh",
            CommandLine = OperatingSystem.IsWindows()
                ? new[] { "/c", windowsCommand ?? "ping -n 30 127.0.0.1 >NUL" }
                : new[] { "-c", unixCommand ?? "while :; do sleep 1; done" },
            VerbatimCommandLine = true,
            Environment = new Dictionary<string, string>(),
            UseAsyncIo = useAsyncIo,
        };

        [TestMethod]
        public async Task A_blocking_connection_says_so()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(ShellOptions("CapBlocking", useAsyncIo: false), cts.Token);

            terminal.SupportsCancellableRead.Should().BeFalse(
                "a blocking read can only be interrupted by closing the stream, and a caller must know that");
        }

        [TestMethod]
        public async Task An_async_connection_says_so()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(ShellOptions("CapAsync", useAsyncIo: true), cts.Token);

            terminal.SupportsCancellableRead.Should().BeTrue();
        }

        /// <summary>
        /// The guarantee the capability advertises, exercised: a cancelled read returns without the
        /// stream being closed, and the data that arrives afterwards is still there to be read.
        /// </summary>
        /// <remarks>
        /// <para>This is the whole point. If cancellation consumed the bytes in flight, or worked
        /// only by closing the stream, a handover would still lose data — the capability would be
        /// advertising semantics it does not have.</para>
        /// <para>The read is parked only AFTER the initial output has been drained, because "a
        /// silent child" is not a thing a fresh pty gives you: ConPTY writes an initialization
        /// preamble the moment the console exists, before the child says anything at all, so a read
        /// taken at spawn completes instantly with bytes and the cancellation never lands. That is
        /// exactly how the first version of this test failed on Windows while passing on macOS,
        /// whose ptys start quiet.</para>
        /// </remarks>
        [TestMethod]
        public async Task Cancelling_a_pending_read_consumes_nothing()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            // Quiet for a while, speak the marker once, then stay alive so EOF cannot race the test.
            var options = ShellOptions(
                "CapCancel",
                useAsyncIo: true,
                unixCommand: "sleep 3; printf AFTERCANCEL; sleep 30",
                windowsCommand: "ping -n 4 127.0.0.1 >NUL & echo AFTERCANCEL& ping -n 31 127.0.0.1 >NUL");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);
            var buffer = new byte[4096];

            // Drain whatever the pty says on its own -- the ConPTY preamble, a shell banner --
            // until it has been quiet for half a second. Each drain read is itself a cancellable
            // read with a short deadline, which is fair: the machinery under test is also the only
            // machinery that CAN do this without a dedicated thread.
            while (true)
            {
                using var quiet = new CancellationTokenSource(500);
                try
                {
                    var drained = await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, quiet.Token);
                    drained.Should().BeGreaterThan(0, "EOF here means the child died before the test began");
                }
                catch (OperationCanceledException)
                {
                    break;   // half a second of silence: NOW the pty is quiet
                }
            }

            // Park a read in the silence, then cancel it.
            using var readCts = new CancellationTokenSource();
            var pending = terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
            await Task.Delay(250, cts.Token);
            readCts.Cancel();

            Func<Task> awaiting = async () => await pending;
            await awaiting.Should().ThrowAsync<OperationCanceledException>(
                "the read was parked waiting for data, which is exactly where cancellation must land");

            // The stream survived, and the bytes that arrive later are intact.
            var got = string.Empty;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && !got.Contains("AFTERCANCEL"))
            {
                var read = await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0)
                {
                    break;
                }

                got += System.Text.Encoding.UTF8.GetString(buffer, 0, read);
            }

            got.Should().Contain("AFTERCANCEL",
                "cancellation must not have consumed or corrupted the stream");
        }
    }
}
