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

            // Read output until we find expected text or timeout
            string output = await ReadOutputAsync(terminal, "test", TimeSpan.FromSeconds(5));

            Assert.Contains("test", output);
            
            // Command completes naturally, just wait for exit - no Kill() needed
            terminal.WaitForExit(1000);
        }

        [Fact]
        public async Task SpawnAsync_ReturnsPidGreaterThanZero()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            var options = CreateShellCommandOptions("PidTest", "echo hello");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            Assert.True(terminal.Pid > 0, "Process ID should be greater than zero");
            
            // Command completes naturally
            terminal.WaitForExit(1000);
        }

        [Fact]
        public async Task ProcessExited_EventIsFired()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var exitedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var options = CreateShellCommandOptions("ExitEventTest", "echo done");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);
            terminal.ProcessExited += (sender, e) => exitedTcs.TrySetResult(e.ExitCode);

            // Read to ensure command runs
            await ReadOutputAsync(terminal, "done", TimeSpan.FromSeconds(5));

            // Wait for the exit event or timeout
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

            terminal.WaitForExit(1000);
        }

        [Fact(Skip = "Not reliable on CI server")]
        public async Task Kill_TerminatesProcess()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            var options = CreateInteractiveShellOptions("KillTest");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            await Task.Delay(500, cts.Token);

            Assert.False(terminal.WaitForExit(100), "Process should still be running");

            terminal.Kill();

            bool exited = terminal.WaitForExit(5000);
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
            
            // Interactive shell - Kill is appropriate here since it won't exit on its own
            terminal.Kill();
            terminal.WaitForExit(1000);
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

            string output = await ReadOutputAsync(terminal, "custom_value_12345", TimeSpan.FromSeconds(5));

            Assert.Contains("custom_value_12345", output);
            
            terminal.WaitForExit(1000);
        }

        [Fact(Skip = "Not reliable on CI server")]
        public async Task WorkingDirectory_IsRespected()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            string command = IsWindows ? "cd" : "pwd";

            var options = CreateShellCommandOptions("CwdTest", command);

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            string output = await ReadOutputAsync(terminal, IsWindows ? "\\" : "/", TimeSpan.FromSeconds(5));

            Assert.True(output.Contains(Path.DirectorySeparatorChar), 
                $"Output should contain path separator. Actual output: '{output}'");
                
            terminal.WaitForExit(1000);
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
        public async Task ExitCode_IsAvailableAfterProcessExits()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            var options = CreateShellCommandOptions("ExitCodeTest", "echo success");

            using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

            await ReadOutputAsync(terminal, "success", TimeSpan.FromSeconds(5));

            Assert.True(terminal.WaitForExit(5000), "Process should exit");

            int exitCode = terminal.ExitCode;
            Assert.True(exitCode >= 0, $"Exit code should be non-negative, was {exitCode}");
        }

        /// <summary>
        /// Reads output from the terminal until the search text is found or timeout.
        /// </summary>
        private static async Task<string> ReadOutputAsync(IPtyConnection terminal, string searchText, TimeSpan timeout)
        {
            var buffer = new byte[4096];
            var output = new StringBuilder();
            var encoding = new UTF8Encoding(false);
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                try
                {
                    var readTask = Task.Run(() => terminal.ReaderStream.Read(buffer, 0, buffer.Length));

                    if (await Task.WhenAny(readTask, Task.Delay(500)) == readTask)
                    {
                        int bytesRead = readTask.Result;
                        if (bytesRead > 0)
                        {
                            output.Append(encoding.GetString(buffer, 0, bytesRead));
                            if (output.ToString().Contains(searchText))
                                break;
                        }
                    }
                }
                catch
                {
                    break;
                }
            }

            return output.ToString();
        }
    }
}
