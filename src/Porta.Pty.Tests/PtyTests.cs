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
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class PtyTests
    {
        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

        [Fact]
        public async Task EchoTest_ReturnsExpectedOutput()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            // Determine the appropriate shell and echo command for the platform
            string app;
            string[] commandLine;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                app = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                commandLine = new[] { "/c", "echo test" };
            }
            else
            {
                app = "/bin/sh";
                commandLine = new[] { "-c", "echo test" };
            }

            var options = new PtyOptions
            {
                Name = "EchoTest",
                Cols = 80,
                Rows = 25,
                Cwd = Environment.CurrentDirectory,
                App = app,
                CommandLine = commandLine,
                Environment = new Dictionary<string, string>()
            };

            // Spawn the terminal
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            var processExitedTcs = new TaskCompletionSource<int>();
            terminal.ProcessExited += (sender, e) => processExitedTcs.TrySetResult(e.ExitCode);

            // Read output from the terminal
            var outputBuilder = new StringBuilder();
            var buffer = new byte[4096];

            // Read until process exits or we find our expected output
            var readTask = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    int bytesRead = await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                    if (bytesRead == 0)
                        break;

                    string chunk = encoding.GetString(buffer, 0, bytesRead);
                    outputBuilder.Append(chunk);

                    // Check if we've received the expected output
                    if (outputBuilder.ToString().Contains("test"))
                        break;
                }
            }, cts.Token);

            // Wait for reading to complete or timeout
            await readTask;

            // Verify the output contains "test"
            string output = outputBuilder.ToString();
            Assert.Contains("test", output);

            // Wait for process to exit
            terminal.Kill();
            Assert.True(terminal.WaitForExit(TestTimeoutMs));
        }
    }
}
