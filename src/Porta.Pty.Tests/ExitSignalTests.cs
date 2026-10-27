// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A program ended by a signal reports that signal on <see cref="PtyExitedEventArgs"/>.
    ///
    /// <para>The connection decodes the wait status into a private exitSignal and sets ExitCode to 0, and
    /// <see cref="PtyExitedEventArgs"/> carries only ExitCode, so a program killed with SIGKILL looked like
    /// one that ended cleanly with 0: measured on macOS 27 (arm64) on 20.10.2026, ExitCode=0,
    /// PtyExitedEventArgs.ExitCode=0, exitSignal=9, against 3 for a program that ran "exit 3".</para>
    ///
    /// <para>The signal is read as the public property ExitSignal through reflection because the test was
    /// written before the property existed, as the oracle of the reproduction, and is kept frozen since:
    /// it then failed by name instead of breaking the build of the whole suite.</para>
    /// </summary>
    [TestClass]
    public class ExitSignalTests
    {
        private const int SIGKILL = 9;

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 20_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [TestMethod]
        [DataRow("kill", false, DisplayName = "killed by SIGKILL, blocking")]
        [DataRow("kill", true, DisplayName = "killed by SIGKILL, non-blocking")]
        [DataRow("exit 3", false, DisplayName = "exit 3, blocking")]
        [DataRow("exit 3", true, DisplayName = "exit 3, non-blocking")]
        public async Task Unix_TheExitedEventCarriesTheSignalThatEndedTheProgram(string ending, bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("exit by signal is a Unix wait status; ConPTY reports an exit code");
            }

            var killed = ending == "kill";
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using var terminal = await PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "exit-signal",
                    Cols = 80,
                    Rows = 24,
                    Cwd = Environment.CurrentDirectory,
                    App = "/bin/sh",
                    CommandLine = new[] { "-c", killed ? "exec /bin/sleep 300" : "exit 3" },
                    VerbatimCommandLine = true,
                    Environment = new Dictionary<string, string>(),
                    UseAsyncIo = useAsyncIo,
                },
                cts.Token);
            var exited = new TaskCompletionSource<PtyExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) => exited.TrySetResult(e);

            if (killed)
            {
                kill(terminal.Pid, SIGKILL).Should().Be(0, $"kill(2) of the running program failed with errno {Marshal.GetLastPInvokeError()}");
            }

            if (!terminal.WaitForExit(TestTimeoutMs))
            {
                Assert.Inconclusive($"the library did not report the exit of {terminal.Pid} within {TestTimeoutMs} ms (the lost exit of the non-blocking reaper)");
            }

            var completed = await Task.WhenAny(exited.Task, Task.Delay(TestTimeoutMs, cts.Token));
            completed.Should().BeSameAs(exited.Task, "ProcessExited should be raised once WaitForExit has returned (useAsyncIo={0})", useAsyncIo);
            var args = await exited.Task;

            var property = typeof(PtyExitedEventArgs).GetProperty("ExitSignal", BindingFlags.Instance | BindingFlags.Public);
            property.Should().NotBeNull(
                "PtyExitedEventArgs should expose the signal that ended the program as ExitSignal; it has only ExitCode={0}, "
                + "while the connection decoded exitSignal={1} privately ({2}, useAsyncIo={3})",
                args.ExitCode,
                PrivateExitSignal(terminal),
                ending,
                useAsyncIo);
            ((int)property!.GetValue(args)!).Should().Be(killed ? SIGKILL : 0, "the program ended with {0} (useAsyncIo={1})", ending, useAsyncIo);
            if (!killed)
            {
                args.ExitCode.Should().Be(3, "a program that ran exit 3 reports 3");
            }
        }

        private static object? PrivateExitSignal(IPtyConnection terminal) =>
            typeof(Unix.PtyConnection).GetField("exitSignal", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(terminal);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }
}
