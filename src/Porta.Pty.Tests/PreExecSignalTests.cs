// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    /// <summary>
    /// A signal sent to the child inside its fork→exec window must land on the CHILD.
    ///
    /// <para>The child of forkpty is a memory copy of the .NET host, CoreCLR's signal handlers included,
    /// until it execs. CoreCLR's SIGTERM/SIGINT/SIGQUIT handlers re-raise the signal with a pid cached
    /// at runtime start — the PARENT's pid, in a forked child. So before the native child reset its
    /// dispositions, a caller that killed the child within a few milliseconds of spawning it killed
    /// ITSELF: the child forwarded the SIGTERM to the process that spawned it. Seen in the wild as a
    /// test host dying with exit 143 one CI run in three on a fast box, where a Stop issued right after
    /// Start reliably landed inside the ~6ms window.</para>
    ///
    /// <para>This test is the sequence: spawn, then SIGTERM the pid immediately, many times, so that some
    /// deliveries land pre-exec. The assertion is survival — with the bug, the test host itself dies.</para>
    /// </summary>
    [TestClass]
    public class PreExecSignalTests
    {
        private const int SIGTERM = 15;

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        [TestMethod]
        public async Task Unix_SignalDeliveredInTheForkExecWindow_KillsTheChildNotTheParent()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Inconclusive("fork/exec is a Unix concern");
            }

            using var cts = new CancellationTokenSource(Debugger.IsAttached ? 300_000 : 60_000);
            var ourPid = Environment.ProcessId;

            // Enough iterations that some SIGTERMs arrive before the child has exec'd. 300 spawns of
            // /bin/sh run in a few seconds; the window is milliseconds wide on a fast machine and wider
            // on a loaded one, so a handful of hits per run is typical either way.
            //
            // The child sleeps far longer than WaitForExit waits, so the only way it exits in time is
            // the SIGTERM — a regression that dropped the signal cannot pass by the sleep running out.
            for (var i = 0; i < 300; i++)
            {
                var options = new PtyOptions
                {
                    Name = "pre-exec-signal",
                    App = "/bin/sh",
                    CommandLine = new[] { "-c", "sleep 600" },
                    Cwd = Environment.CurrentDirectory,
                    Rows = 24,
                    Cols = 80,
                };

                using IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);

                // A pid of 0 or below would make kill() signal our own group or every process we may
                // signal — a self-inflicted version of the very failure this test exists to catch.
                terminal.Pid.Should().BePositive($"iteration {i}: the spawn must report a real child pid");
                terminal.Pid.Should().NotBe(ourPid);

                // No delay: this is the Stop-right-after-Start that hits the window.
                var rc = kill(terminal.Pid, SIGTERM);
                rc.Should().Be(0, $"iteration {i}: kill(2) failed with errno {Marshal.GetLastPInvokeError()}");

                // The child must die of it — either as a pre-exec copy taking SIGTERM's default action,
                // or as sh. Never the parent: reaching the next iteration IS the assertion.
                terminal.WaitForExit(5000).Should().BeTrue($"iteration {i}: the child should have died of the SIGTERM");
            }

            Environment.ProcessId.Should().Be(ourPid);
        }
    }
}
