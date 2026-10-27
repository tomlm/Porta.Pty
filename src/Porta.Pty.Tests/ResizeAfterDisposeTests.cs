// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Resize of a terminal that has been disposed must not reach the terminal opened after it.
    ///
    /// <para>Dispose closes the controller, and the next terminal is handed the same descriptor number
    /// (the lowest free one). Resize ran ioctl(TIOCSWINSZ) on the raw number it kept, outside the counted
    /// path that reads, writes and the close go through, so it resized the other terminal in silence:
    /// measured on macOS 27 (arm64) on 20.10.2026, fdA=43, fdB=43, no exception, B started at 24x80 and
    /// then reported "11 33".</para>
    ///
    /// <para>Terminal B prints its size, waits for a line, and prints its size again; the line is written
    /// only after the disposed terminal's Resize has returned, so the second size is the one B has after it.
    /// Whether that Resize throws is left open; only the other terminal is judged.</para>
    /// </summary>
    [TestClass]
    public class ResizeAfterDisposeTests
    {
        private const string Ready = "READY-B";
        private const string Done = "DONE-B";

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 20_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Unix_ResizeOfADisposedTerminal_DoesNotReachTheTerminalOpenedAfterIt(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("descriptor numbers reissued after close are a Unix concern; ConPTY resizes by handle");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var closed = await PtyProvider.SpawnAsync(Options("closed", "/bin/sleep", new[] { "300" }, useAsyncIo), cts.Token);
            var closedNumber = ControllerNumber(closed);
            closed.Dispose();

            using var next = await PtyProvider.SpawnAsync(
                Options("next", "/bin/sh", new[] { "-c", $"stty size; echo {Ready}; read line; stty size; echo {Done}" }, useAsyncIo),
                cts.Token);
            var output = new Pump(next);
            (await output.WaitForAsync(Ready, TestTimeoutMs)).Should().BeTrue("terminal B should start; its output was: {0}", output.Text);

            if (ControllerNumber(next) != closedNumber)
            {
                Assert.Inconclusive($"terminal B got descriptor {ControllerNumber(next)}, not the closed terminal's {closedNumber}: nothing shared to measure");
            }

            Exception? resize = null;
            try
            {
                closed.Resize(33, 11);
            }
            catch (Exception error)
            {
                resize = error;
            }

            var line = Encoding.ASCII.GetBytes("\n");
            await next.WriterStream.WriteAsync(line, 0, line.Length, cts.Token);
            await next.WriterStream.FlushAsync(cts.Token);
            (await output.WaitForAsync(Done, TestTimeoutMs)).Should().BeTrue("terminal B should print its size again; its output was: {0}", output.Text);

            var after = output.Text.Substring(output.Text.IndexOf(Ready, StringComparison.Ordinal));
            after.Should().Contain(
                "24 80",
                "Resize(33, 11) of the disposed terminal (descriptor {0}, {1}) must leave terminal B, which got that descriptor number, at 24 rows by 80 columns (useAsyncIo={2}); B printed: {3}",
                closedNumber,
                resize is null ? "no exception" : resize.GetType().Name,
                useAsyncIo,
                output.Text);
        }

        private static PtyOptions Options(string name, string app, string[] commandLine, bool useAsyncIo) => new PtyOptions
        {
            Name = name,
            Cols = 80,
            Rows = 24,
            Cwd = Environment.CurrentDirectory,
            App = app,
            CommandLine = commandLine,
            VerbatimCommandLine = true,
            Environment = new Dictionary<string, string>(),
            UseAsyncIo = useAsyncIo,
        };

        /// <summary>The controller's descriptor number, as the connection keeps it.</summary>
        private static int ControllerNumber(IPtyConnection connection)
        {
            var field = typeof(Unix.PtyConnection).GetField("controller", BindingFlags.Instance | BindingFlags.NonPublic);
            field.Should().NotBeNull("Unix.PtyConnection should keep the controller's number in a field named controller");
            return (int)field!.GetValue(connection)!;
        }

        /// <summary>Reads a terminal's output on a pool thread until it ends, so a wait can time out.</summary>
        private sealed class Pump
        {
            private readonly StringBuilder text = new StringBuilder();

            public Pump(IPtyConnection terminal)
            {
                _ = Task.Run(() =>
                {
                    var buffer = new byte[4096];
                    try
                    {
                        int read;
                        while ((read = terminal.ReaderStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            lock (this.text)
                            {
                                this.text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // The stream ends with an error on some platforms once the child is gone.
                    }
                });
            }

            public string Text
            {
                get
                {
                    lock (this.text)
                    {
                        return this.text.ToString();
                    }
                }
            }

            public async Task<bool> WaitForAsync(string marker, int timeoutMs)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    if (this.Text.Contains(marker, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    await Task.Delay(20);
                }

                return this.Text.Contains(marker, StringComparison.Ordinal);
            }
        }
    }
}
