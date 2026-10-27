// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A handler added to <see cref="IPtyConnection.ProcessExited"/> after the program has exited still learns how it
    /// ended, signal included.
    ///
    /// <para>The connection sets the state <see cref="IPtyConnection.WaitForExit"/> reads before it raises the event, and
    /// a caller subscribes only once the spawn has returned, so a program that ends at once can be gone before anyone
    /// listens. <see cref="IPtyConnection"/> carries only ExitCode, which is 0 for a program killed by a signal, so a caller
    /// that fell back on it reported a killed program as one that ended cleanly. The test forces
    /// that order: it waits for the exit, then subscribes.</para>
    /// </summary>
    [TestClass]
    public class LateSubscriberTests
    {
        private const int SIGKILL = 9;

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 20_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Unix_AHandlerAddedAfterTheExit_GetsTheExitAndItsSignal(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("exit by signal is a Unix wait status; ConPTY reports an exit code");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using var terminal = await PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "late-subscriber",
                    Cols = 80,
                    Rows = 24,
                    Cwd = Environment.CurrentDirectory,
                    App = "/bin/sh",
                    CommandLine = new[] { "-c", "exec /bin/sleep 300" },
                    VerbatimCommandLine = true,
                    Environment = new Dictionary<string, string>(),
                    UseAsyncIo = useAsyncIo,
                },
                cts.Token);

            kill(terminal.Pid, SIGKILL).Should().Be(0, $"kill(2) of the running program failed with errno {Marshal.GetLastPInvokeError()}");
            if (!terminal.WaitForExit(TestTimeoutMs))
            {
                Assert.Inconclusive($"the library did not report the exit of {terminal.Pid} within {TestTimeoutMs} ms");
            }

            var calls = new List<PtyExitedEventArgs>();
            terminal.ProcessExited += (_, e) => calls.Add(e);

            calls.Should().ContainSingle("a handler added once WaitForExit has returned should be called at once (useAsyncIo={0})", useAsyncIo);
            calls[0].ExitSignal.Should().Be(SIGKILL, "the program was killed with SIGKILL (useAsyncIo={0})", useAsyncIo);
            calls[0].ExitCode.Should().Be(0, "a program killed by a signal has no exit code");
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }
}
