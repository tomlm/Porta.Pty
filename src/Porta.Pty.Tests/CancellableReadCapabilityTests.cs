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

        private static PtyOptions ShellOptions(string name, bool useAsyncIo) => new PtyOptions
        {
            Name = name,
            Cols = 80,
            Rows = 25,
            Cwd = Environment.CurrentDirectory,
            App = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            CommandLine = OperatingSystem.IsWindows()
                ? Array.Empty<string>()
                : new[] { "-c", "while :; do sleep 1; done" },
            VerbatimCommandLine = !OperatingSystem.IsWindows(),
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
        /// This is the whole point. If cancellation consumed the bytes in flight, or worked only by
        /// closing the stream, a handover would still lose data -- the capability would be
        /// advertising semantics it does not have.
        /// </remarks>
        [TestMethod]
        public async Task Cancelling_a_pending_read_consumes_nothing()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var options = ShellOptions("CapCancel", useAsyncIo: true);
            options.App = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
            if (!OperatingSystem.IsWindows())
            {
                // Quiet for long enough to park a read, then speak once.
                options.CommandLine = new[] { "-c", "sleep 2; printf AFTERCANCEL; sleep 30" };
            }

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            var buffer = new byte[4096];

            // Park a read while the child is silent, then cancel it.
            using var readCts = new CancellationTokenSource();
            var pending = terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
            await Task.Delay(250, cts.Token);
            readCts.Cancel();

            Func<Task> awaiting = async () => await pending;
            await awaiting.Should().ThrowAsync<OperationCanceledException>(
                "the read was parked waiting for data, which is exactly where cancellation must land");

            if (OperatingSystem.IsWindows())
            {
                return;   // the payload half needs the sh script; the cancellation half ran
            }

            // The stream survived, and the bytes that arrive later are intact.
            var got = string.Empty;
            var deadline = DateTime.UtcNow.AddSeconds(15);
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
