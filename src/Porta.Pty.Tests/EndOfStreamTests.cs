// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A child that exits ends the reader's stream: a read returns 0, the same on every path.
    /// </summary>
    /// <remarks>
    /// Linux reports a pty controller whose other end has gone as EIO, macOS as a read of 0. The
    /// non-blocking stream already normalised that; the default blocking one let FileStream throw
    /// IOException("Input/output error"), so on Linux an ordinary exit arrived as a fault -- and a
    /// terminal host printed it: "Error reading from process: Input/output error", followed by
    /// "Process exited with code: 0". Seen in Iciclecreek.Avalonia.Terminal on WSL.
    /// </remarks>
    [TestClass]
    public class EndOfStreamTests
    {
        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static PtyOptions Command(string name, string command, bool useAsyncIo) => new PtyOptions
        {
            Name = name,
            Cols = 80,
            Rows = 25,
            Cwd = Environment.CurrentDirectory,
            App = "/bin/sh",
            CommandLine = new[] { "-c", command },
            VerbatimCommandLine = true,
            Environment = new Dictionary<string, string>(),
            UseAsyncIo = useAsyncIo,
        };

        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task AChildThatExitsEndsTheStreamOnASyncRead(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("EIO on a pty controller is a Unix concern.");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(
                Command("SyncEof", "echo EOF_MARKER", useAsyncIo), cts.Token);

            // On a pool thread so a read that never returns fails the test instead of hanging it.
            var drain = Task.Run(() =>
            {
                var output = new StringBuilder();
                var buffer = new byte[4096];
                int read;
                while ((read = terminal.ReaderStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }

                return output.ToString();
            });

            var finished = await Task.WhenAny(drain, Task.Delay(TestTimeoutMs));
            finished.Should().BeSameAs(drain, "the read should end when the child exits");

            // Awaited, so an IOException from the read fails the test here with its own message.
            (await drain).Should().Contain("EOF_MARKER");
        }

        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task AChildThatExitsEndsTheStreamOnAnAsyncRead(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("EIO on a pty controller is a Unix concern.");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(
                Command("AsyncEof", "echo EOF_MARKER", useAsyncIo), cts.Token);

            var output = new StringBuilder();
            var buffer = new byte[4096];
            int read;
            while ((read = await terminal.ReaderStream.ReadAsync(buffer.AsMemory(), cts.Token)) > 0)
            {
                output.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            output.ToString().Should().Contain("EOF_MARKER");
        }

        [TestMethod]
        public async Task AChildThatExitsEndsTheStreamThroughBeginRead()
        {
            // The APM pair is the route most easily broken by guarding the others: overriding it to
            // call ReadAsync once made ReadAsync, base.ReadAsync and BeginRead a cycle, which
            // overflowed the stack rather than failing a test.
            if (IsWindows)
            {
                Assert.Inconclusive("EIO on a pty controller is a Unix concern.");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(
                Command("ApmEof", "echo EOF_MARKER", useAsyncIo: false), cts.Token);

            var stream = terminal.ReaderStream;
            var output = new StringBuilder();
            var buffer = new byte[4096];
            int read;
            while ((read = await Task.Factory.FromAsync(
                stream.BeginRead, stream.EndRead, buffer, 0, buffer.Length, null)) > 0)
            {
                output.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            output.ToString().Should().Contain("EOF_MARKER");
        }

        [TestMethod]
        public async Task ReadingAfterTheEndKeepsReturningZero()
        {
            // A caller that reads once more after seeing the end -- a retry, a second consumer --
            // must get the end again, not the exception the first read was spared.
            if (IsWindows)
            {
                Assert.Inconclusive("EIO on a pty controller is a Unix concern.");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(
                Command("EofTwice", "true", useAsyncIo: false), cts.Token);

            var buffer = new byte[4096];
            var drain = Task.Run(() =>
            {
                while (terminal.ReaderStream.Read(buffer, 0, buffer.Length) > 0)
                {
                }

                return terminal.ReaderStream.Read(buffer, 0, buffer.Length);
            });

            var finished = await Task.WhenAny(drain, Task.Delay(TestTimeoutMs));
            finished.Should().BeSameAs(drain);
            (await drain).Should().Be(0);
        }
    }
}
