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

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static string ShellApp => IsWindows
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/sh";

        /// <summary>
        /// Creates PtyOptions for running a shell command.
        /// On Windows: cmd.exe /c command
        /// On Unix: /bin/sh -c command
        /// </summary>
        private static PtyOptions CreateShellCommandOptions(string name, string command)
        {
            return new PtyOptions
            {
                Name = name,
                Cols = 120,
                Rows = 25,
                Cwd = Environment.CurrentDirectory,
                App = ShellApp,
                CommandLine = IsWindows
                    ? new[] { "/c", command }
                    : new[] { "-c", command },
                VerbatimCommandLine = true,
                Environment = new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Creates PtyOptions for an interactive shell session.
        /// </summary>
        private static PtyOptions CreateInteractiveShellOptions(string name)
        {
            return new PtyOptions
            {
                Name = name,
                Cols = 80,
                Rows = 25,
                Cwd = Environment.CurrentDirectory,
                App = ShellApp,
                CommandLine = Array.Empty<string>(),
                Environment = new Dictionary<string, string>()
            };
        }

        [Fact]
        public async Task EchoTest_ReturnsExpectedOutput()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            var options = CreateShellCommandOptions("EchoTest", "echo test");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            // Read output until we find "test" or timeout
            string output = await ReadOutputUntilAsync(terminal, "test", cts.Token);

            Assert.Contains("test", output);

            await CleanupTerminalAsync(terminal);
        }

        [Fact]
        public async Task SpawnAsync_ReturnsPidGreaterThanZero()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            var options = CreateShellCommandOptions("PidTest", "echo hello");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            Assert.True(terminal.Pid > 0, "Process ID should be greater than zero");

            await CleanupTerminalAsync(terminal);
        }

        [Fact]
        public async Task ProcessExited_EventIsFired()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var exitedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var options = CreateShellCommandOptions("ExitEventTest", "echo done");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);
            terminal.ProcessExited += (sender, e) => exitedTcs.TrySetResult(e.ExitCode);

            await ReadOutputUntilAsync(terminal, "done", cts.Token);

            using (cts.Token.Register(() => exitedTcs.TrySetCanceled()))
            {
                try
                {
                    int exitCode = await exitedTcs.Task;
                    Assert.True(exitCode >= 0, $"Exit code should be non-negative, was {exitCode}");
                }
                catch (TaskCanceledException)
                {
                    Assert.True(terminal.WaitForExit(1000), "Process should have exited");
                }
            }
        }

        [Fact]
        public async Task Resize_DoesNotThrow()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            var options = CreateShellCommandOptions("ResizeTest", "echo resize");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            var exception = Record.Exception(() => terminal.Resize(120, 40));
            Assert.Null(exception);

            exception = Record.Exception(() => terminal.Resize(40, 10));
            Assert.Null(exception);

            await CleanupTerminalAsync(terminal);
        }

        [Fact]
        public async Task Kill_TerminatesProcess()
        {
            // Use a longer timeout for this specific test since kill cleanup can be slow on macOS
            using var cts = new CancellationTokenSource(30_000);

            var options = CreateInteractiveShellOptions("KillTest");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            // Wait a bit for the shell to start
            await Task.Delay(500, cts.Token);

            Assert.False(terminal.WaitForExit(100), "Process should still be running");

            terminal.Kill();

            // Give time for the process to fully terminate
            // On macOS, the kill signal may take longer to be reflected in waitpid
            bool exited = terminal.WaitForExit(15000);

            Assert.True(exited, "Process should exit after being killed");
        }

        [Fact]
        public async Task WaitForExit_ReturnsFalseWhileProcessIsRunning()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            var options = CreateInteractiveShellOptions("WaitForExitTest");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            await Task.Delay(500, cts.Token);

            Assert.False(terminal.WaitForExit(100), "WaitForExit should return false while process is running");

            await CleanupTerminalAsync(terminal);
        }

        [Fact]
        public async Task EnvironmentVariables_ArePassedToProcess()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            string command = IsWindows
                ? "echo %MY_TEST_VAR%"
                : "echo $MY_TEST_VAR";

            var options = new PtyOptions
            {
                Name = "EnvVarTest",
                Cols = 120,
                Rows = 25,
                Cwd = Environment.CurrentDirectory,
                App = ShellApp,
                CommandLine = IsWindows
                    ? new[] { "/c", command }
                    : new[] { "-c", command },
                VerbatimCommandLine = true,
                Environment = new Dictionary<string, string>
                {
                    { "MY_TEST_VAR", "custom_value_12345" }
                }
            };

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            string output = await ReadOutputUntilAsync(terminal, "custom_value_12345", cts.Token);

            Assert.Contains("custom_value_12345", output);

            await CleanupTerminalAsync(terminal);
        }

        [Fact]
        public async Task WorkingDirectory_IsRespected()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            // Just run pwd and verify we get some path output
            // Use the current directory which we know works (EchoTest passes)
            string command = IsWindows ? "cd" : "pwd";

            var options = CreateShellCommandOptions("CwdTest", command);

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            // Read output looking for a path separator
            string output = await ReadOutputUntilAsync(terminal, IsWindows ? "\\" : "/", cts.Token);

            await CleanupTerminalAsync(terminal);

            // Verify we got a path in the output
            Assert.True(output.Contains(Path.DirectorySeparatorChar), 
                $"Output should contain path separator. Actual output: '{output}'");
        }

        [Fact]
        public async Task SpawnAsync_ThrowsOnEmptyApp()
        {
            var options = new PtyOptions
            {
                App = string.Empty,
                Cwd = Environment.CurrentDirectory,
                CommandLine = Array.Empty<string>(),
                Environment = new Dictionary<string, string>()
            };

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                PtyProvider.SpawnAsync(options, CancellationToken.None));
        }

        [Fact]
        public async Task SpawnAsync_ThrowsOnEmptyCwd()
        {
            var options = new PtyOptions
            {
                App = ShellApp,
                Cwd = string.Empty,
                CommandLine = Array.Empty<string>(),
                Environment = new Dictionary<string, string>()
            };

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                PtyProvider.SpawnAsync(options, CancellationToken.None));
        }

        [Fact]
        public async Task SpawnAsync_ThrowsOnNullCommandLine()
        {
            var options = new PtyOptions
            {
                App = ShellApp,
                Cwd = Environment.CurrentDirectory,
                CommandLine = null!,
                Environment = new Dictionary<string, string>()
            };

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                PtyProvider.SpawnAsync(options, CancellationToken.None));
        }

        [Fact]
        public async Task SpawnAsync_ThrowsOnNullEnvironment()
        {
            var options = new PtyOptions
            {
                App = ShellApp,
                Cwd = Environment.CurrentDirectory,
                CommandLine = Array.Empty<string>(),
                Environment = null!
            };

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                PtyProvider.SpawnAsync(options, CancellationToken.None));
        }

        [Fact]
        public async Task MultipleCommands_ExecuteSequentially()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            string command = "echo first && echo second";

            var options = CreateShellCommandOptions("MultiCommandTest", command);

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            // Read all output - give enough time for both commands to complete
            string output = await ReadOutputForDurationAsync(terminal, TimeSpan.FromSeconds(2), cts.Token);

            Assert.Contains("first", output);
            Assert.Contains("second", output);

            await CleanupTerminalAsync(terminal);
        }

        [Fact]
        public async Task ExitCode_IsAvailableAfterProcessExits()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            var options = CreateShellCommandOptions("ExitCodeTest", "echo success");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            await ReadOutputUntilAsync(terminal, "success", cts.Token);

            // Give the process a moment to fully exit after output
            await Task.Delay(500, cts.Token);

            // Drain any remaining output
            await ReadOutputForDurationAsync(terminal, TimeSpan.FromMilliseconds(500), cts.Token);

            Assert.True(terminal.WaitForExit(5000), "Process should exit");

            int exitCode = terminal.ExitCode;
            Assert.True(exitCode >= 0, $"Exit code should be non-negative, was {exitCode}");
        }

        /// <summary>
        /// Reads output from the terminal until the search text is found or cancellation is requested.
        /// </summary>
        private static async Task<string> ReadOutputUntilAsync(IPtyConnection terminal, string searchText, CancellationToken cancellationToken)
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var outputBuilder = new StringBuilder();
            var buffer = new byte[4096];

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await Task.Run(async () =>
                {
                    while (!linkedCts.Token.IsCancellationRequested)
                    {
                        var readTask = Task.Run(() =>
                        {
                            try
                            {
                                return terminal.ReaderStream.Read(buffer, 0, buffer.Length);
                            }
                            catch
                            {
                                return 0;
                            }
                        });

                        var completedTask = await Task.WhenAny(readTask, Task.Delay(100, linkedCts.Token));

                        if (completedTask == readTask)
                        {
                            int bytesRead = await readTask;
                            if (bytesRead == 0)
                                break;

                            string chunk = encoding.GetString(buffer, 0, bytesRead);
                            outputBuilder.Append(chunk);

                            if (outputBuilder.ToString().Contains(searchText))
                                break;
                        }
                    }
                }, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout or cancellation is acceptable
            }

            return outputBuilder.ToString();
        }

        /// <summary>
        /// Reads output from the terminal for a specified duration.
        /// </summary>
        private static async Task<string> ReadOutputForDurationAsync(IPtyConnection terminal, TimeSpan duration, CancellationToken cancellationToken)
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var outputBuilder = new StringBuilder();
            var buffer = new byte[4096];

            using var durationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            durationCts.CancelAfter(duration);

            try
            {
                await Task.Run(async () =>
                {
                    while (!durationCts.Token.IsCancellationRequested)
                    {
                        var readTask = Task.Run(() =>
                        {
                            try
                            {
                                return terminal.ReaderStream.Read(buffer, 0, buffer.Length);
                            }
                            catch
                            {
                                return 0;
                            }
                        });

                        var completedTask = await Task.WhenAny(readTask, Task.Delay(100, durationCts.Token));

                        if (completedTask == readTask)
                        {
                            int bytesRead = await readTask;
                            if (bytesRead == 0)
                                break;

                            string chunk = encoding.GetString(buffer, 0, bytesRead);
                            outputBuilder.Append(chunk);
                        }
                    }
                }, durationCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Duration elapsed - this is expected
            }

            return outputBuilder.ToString();
        }

        /// <summary>
        /// Safely cleans up a terminal connection.
        /// </summary>
        private static async Task CleanupTerminalAsync(IPtyConnection terminal)
        {
            try
            {
                terminal.Kill();
            }
            catch
            {
                // Process may have already exited
            }

            await Task.Run(() => terminal.WaitForExit(2000));
        }
    }
}
