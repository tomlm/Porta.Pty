// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The edges of "no signal once the child is collected" that the first tests did not hold: a child
    /// collected by someone else, a signal racing the collection, the macOS grace period between SIGHUP
    /// and SIGKILL, and the controller's close-on-exec flag for processes started outside Porta.
    ///
    /// <para>The connections are the library's own Mac and Linux connections; only SendSignal, the one
    /// place a signal leaves the library, is overridden, to record each signal with the result of kill(2),
    /// and the two waits are wrapped, to count them and to act right after a collection.
    /// Children are started by the test with posix_spawn and the controller is /dev/null, since no
    /// terminal is needed to collect or signal a child.</para>
    /// </summary>
    [TestClass]
    public class CollectionRaceTests
    {
        private const int SIGHUP = 1;
        private const int SIGKILL = 9;
        private const int SIGTERM = 15;
        private const int ESRCH = 3;
        private const int O_RDWR = 2;
        private const int WNOHANG = 1;

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        private static int SIGSTOP => IsMac ? 17 : 19;

        private static int SIGCONT => IsMac ? 19 : 18;

        /// <summary>
        /// A child collected by a third party (here the test's own waitpid, as a host with SIGCHLD ignored
        /// or another reaper would) before the connection watches it: the watcher meets ECHILD, and from then
        /// on the pid is free, so Kill and Dispose must not signal it.
        /// </summary>
        [TestMethod]
        [DataRow("Kill", false, DisplayName = "Kill, blocking")]
        [DataRow("Kill", true, DisplayName = "Kill, non-blocking")]
        [DataRow("Dispose", false, DisplayName = "Dispose, blocking")]
        [DataRow("Dispose", true, DisplayName = "Dispose, non-blocking")]
        public void Unix_AfterAThirdPartyCollectedTheChild_KillAndDisposeSendNoSignal(string call, bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("pid reuse after waitpid is a Unix concern");
            }

            var stray = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var pid = SpawnChild("exec /bin/sleep 600");
                kill(pid, SIGKILL).Should().Be(0);
                var status = 0;
                waitpid(pid, ref status, 0).Should().Be(pid, "the third party should collect the child");

                using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: false);

                // The watcher learns of the third party's collection on its own thread. There is no public
                // signal for it, so the test waits, at most 2 s, for the connection's own record of it;
                // a library that never records it is then measured as it is.
                probe.WaitUntilMarkedCollected(2000);

                if (call == "Kill")
                {
                    probe.Connection.Kill();
                }

                probe.Connection.Dispose();

                if (!probe.Sent.IsEmpty)
                {
                    stray.Add($"iteration {i}: pid {pid} sent [{string.Join(", ", probe.Sent.Select(s => s.Signal))}]");
                }
            }

            stray.Should().BeEmpty(
                "{0} must not signal a pid a third party already collected, since it may belong to another process by now (useAsyncIo={1})",
                call,
                useAsyncIo);
        }

        /// <summary>
        /// A signal racing the collection must never reach a freed pid. A thread calls SignalChild with
        /// signal 0 (kill(2)'s existence check, harmless) without pause while the test kills the child;
        /// every call that finds the pid gone (ESRCH) is a signal that would have hit a freed, possibly
        /// reused, pid. Holds only when the collection and the signal take the same lock and the flag
        /// is checked under it.
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public void Unix_SignalsRacingTheCollection_NeverReachAFreedPid(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("pid reuse after waitpid is a Unix concern");
            }

            const int iterations = 200;
            var strays = 0;
            var calls = 0L;
            var lost = 0;
            for (var i = 0; i < iterations; i++)
            {
                var pid = SpawnChild("exec /bin/sleep 600");
                using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: true);

                var stop = 0;
                var hammered = 0L;
                var hammer = new Thread(() =>
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        probe.Signal(0);
                        Interlocked.Increment(ref hammered);
                    }
                });
                hammer.Start();

                kill(pid, SIGKILL).Should().Be(0);
                var exited = probe.Connection.WaitForExit(TestTimeoutMs);

                // A few more calls after the exit is reported, so a library that stops checking the flag
                // is caught even when the collection itself won every race.
                var after = Interlocked.Read(ref hammered) + 20;
                var deadline = Stopwatch.StartNew();
                while (exited && Interlocked.Read(ref hammered) < after && deadline.ElapsedMilliseconds < 1000)
                {
                    Thread.Yield();
                }

                Volatile.Write(ref stop, 1);
                hammer.Join();
                calls += Interlocked.Read(ref hammered);

                if (!exited)
                {
                    // The exit the non-blocking reaper sometimes loses: the child is still ours,
                    // a zombie, so nothing recorded above reached a freed pid; it is collected here.
                    lost++;
                    var status = 0;
                    waitpid(pid, ref status, 0);
                    continue;
                }

                strays += probe.Sent.Count(s => s.Result == -1 && s.Errno == ESRCH);
            }

            strays.Should().Be(
                0,
                "no signal may reach the pid once it is collected (useAsyncIo={0}; {1} SignalChild calls over {2} children, {3} exits lost by the reaper)",
                useAsyncIo,
                calls,
                iterations - lost,
                lost);
        }

        /// <summary>
        /// A child stopped and continued before it exits is collected once, under the signal lock, and the
        /// library does not poll while it is stopped. Darwin's waitid(WEXITED | WNOWAIT) returns as soon as the
        /// child stops, and again on every call until a waitpid with WUNTRACED takes the stop (measured on
        /// macOS 27: more than two million returns in half a second). The blocking watcher fell through to a
        /// plain blocking waitpid there, outside the lock, and collected the later exit before the collected
        /// flag was set.
        ///
        /// <para>Right after the wait that collects the exit, another thread signals the child through
        /// SignalChild (signal 0), and the collecting thread gives it 200 ms. Under the lock that signal waits
        /// for the flag and is never sent; outside it, it reaches the freed pid and kill(2) reports ESRCH. The
        /// no-hang waits made while the child is stopped are counted. Linux's waitid does not return for a
        /// stop, so there this checks that a stop and a continue change nothing.</para>
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public void Unix_AChildStoppedAndContinued_IsCollectedOnceUnderTheLock_WithoutPollingWhileStopped(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("stopping a child with SIGSTOP is a Unix concern");
            }

            var release = Path.Combine(Path.GetTempPath(), $"porta-pty-release-{Guid.NewGuid():N}");
            var pid = SpawnChild($"while [ ! -e '{release}' ]; do /bin/sleep 0.02; done; exit 7");
            using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: true);
            var exits = new ConcurrentQueue<int>();
            probe.Connection.ProcessExited += (_, e) => exits.Enqueue(e.ExitCode);
            var signallers = new ConcurrentQueue<Thread>();
            probe.Waits.AfterCollect = () =>
            {
                var signaller = new Thread(() => probe.Signal(0));
                signaller.Start();
                signaller.Join(200);
                signallers.Enqueue(signaller);
            };

            try
            {
                StopAndWait(pid);

                // Counted from the confirmed stop: a stop lost and sent again (see StopAndWait) leaves an
                // extra stop report for the library to take, which is not the polling this measures.
                var beforeStop = probe.Waits.NoHangWaits;
                Thread.Sleep(500);
                var whileStopped = probe.Waits.NoHangWaits - beforeStop;

                kill(pid, SIGCONT).Should().Be(0, $"SIGCONT to child {pid} failed with errno {Marshal.GetLastPInvokeError()}");
                WaitUntil(() => !IsStopped(pid), $"child {pid} did not run again after SIGCONT");
                File.WriteAllText(release, string.Empty);

                if (!probe.Connection.WaitForExit(TestTimeoutMs))
                {
                    var status = 0;
                    if (useAsyncIo && waitpid(pid, ref status, WNOHANG) == pid)
                    {
                        Assert.Inconclusive("the library did not report the exit (the lost exit of the non-blocking reaper)");
                    }

                    Assert.Fail($"the library did not report the exit of child {pid} within {TestTimeoutMs} ms (useAsyncIo={useAsyncIo})");
                }

                // Room for a second report, which must not come.
                Thread.Sleep(200);
                foreach (var signaller in signallers)
                {
                    signaller.Join();
                }

                using var scope = new AwesomeAssertions.Execution.AssertionScope();
                exits.Should().Equal(new[] { 7 }, "the child should be reported once, with its exit code 7 (useAsyncIo={0})", useAsyncIo);
                probe.Connection.ExitCode.Should().Be(7, "a stop or continue report is not the exit (useAsyncIo={0})", useAsyncIo);
                probe.Sent.Should().BeEmpty(
                    "no signal may leave once the child is collected (useAsyncIo={0}); sent [{1}]",
                    useAsyncIo,
                    string.Join(", ", probe.Sent.Select(s => $"{s.Signal} -> {s.Result}/errno {s.Errno}")));
                whileStopped.Should().BeLessThanOrEqualTo(
                    2,
                    "the library should take the stop once and wait, not poll, while the child is stopped (useAsyncIo={0})",
                    useAsyncIo);
            }
            finally
            {
                if (!probe.Connection.WaitForExit(0))
                {
                    // Failed before the exit was reported: the child is not collected, so the pid is still its own.
                    kill(pid, SIGKILL);
                    probe.Connection.WaitForExit(TestTimeoutMs);
                }

                File.Delete(release);
            }
        }

        /// <summary>
        /// The stop helper the test above depends on: a SIGSTOP that never takes effect is sent again until
        /// the child is seen stopped. The first SIGSTOP is swallowed, as a lost one is, so the child is never
        /// stopped by it; only a re-send that really reaches kill(2) can stop it.
        /// </summary>
        [TestMethod]
        public void Unix_StopAndWait_SendsSigStopAgainWhenTheFirstIsLost()
        {
            if (IsWindows)
            {
                Assert.Inconclusive("SIGSTOP is a Unix concern");
            }

            var pid = SpawnChild("exec /bin/sleep 600");
            try
            {
                int attempts = 0;
                int delivered = 0;
                StopAndWait(pid, (p, signal) =>
                {
                    if (++attempts == 1)
                    {
                        return 0; // lost: reported sent, never delivered
                    }

                    delivered++;
                    return kill(p, signal);
                });

                delivered.Should().BePositive("the lost first SIGSTOP has to be made good by a re-send that reaches kill(2)");
                IsStopped(pid).Should().BeTrue("the helper returns only once the child is stopped");
            }
            finally
            {
                kill(pid, SIGKILL);
                var status = 0;
                waitpid(pid, ref status, 0);
            }
        }

        /// <summary>
        /// macOS: Kill of a child that dies of SIGHUP returns once the child is collected, not after the
        /// fixed 100 ms grace period. The fastest of five is compared with half the grace period.
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public void Mac_KillOfAChildThatDiesOfSigHup_DoesNotWaitTheGracePeriod(bool useAsyncIo)
        {
            if (!IsMac)
            {
                Assert.Inconclusive("the 100 ms grace period between SIGHUP and SIGKILL exists only on macOS");
            }

            var durations = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var pid = SpawnChild("exec /bin/sleep 600");
                using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: true);

                var clock = Stopwatch.StartNew();
                probe.Connection.Kill();
                var elapsed = clock.Elapsed.TotalMilliseconds;

                if (probe.Connection.WaitForExit(TestTimeoutMs))
                {
                    durations.Add(elapsed);
                }
                else
                {
                    var status = 0;
                    waitpid(pid, ref status, 0);
                }
            }

            if (durations.Count == 0)
            {
                Assert.Inconclusive("the library reported the exit of none of the 5 children");
            }

            durations.Min().Should().BeLessThan(
                50,
                "Kill should return as soon as the child has died of SIGHUP (useAsyncIo={0}); took [{1}] ms",
                useAsyncIo,
                string.Join(", ", durations.Select(d => d.ToString("F1"))));
        }

        /// <summary>
        /// macOS: a child that dies of SIGHUP is not sent SIGKILL even when its exit is reported late. Kill's
        /// 100 ms grace used to take "not reported yet" for "still running" and send SIGKILL to the exited
        /// child; a lost kqueue event, reported by the reaper's sweep up to a second on, did exactly that.
        /// Here the report is held back until the grace decision has been made: the reaper's seams drop this
        /// child's event and keep the sweep off it until Kill's grace step has finished, so at that decision
        /// the exit is never reported, and only asking the kernel whether the child has exited can spare it.
        /// </summary>
        [TestMethod]
        public void Mac_KillOfAChildThatDiesOfSigHup_SendsNoSigKillWhenItsExitIsReportedLate()
        {
            if (!IsMac)
            {
                Assert.Inconclusive("only macOS follows SIGHUP with SIGKILL");
            }

            var pid = SpawnChild("exec /bin/sleep 600");
            WaitUntilRunning(pid, "/bin/sleep");
            var reaper = Unix.PtyReaper.Instance;
            reaper.LoseExitEvent = p => p == pid;
            reaper.HoldSweep = p => p == pid;
            using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo: true, forward: true);
            try
            {
                probe.Connection.Kill();

                // The grace decision is made with the exit still unreported: no event, no sweep.
                var grace = probe.Connection.PendingGraceKill;
                grace.Should().NotBeNull("Kill on macOS starts a grace step");
                grace!.Wait(TestTimeoutMs).Should().BeTrue("Kill's grace step should finish");
                probe.Connection.WaitForExit(0).Should().BeFalse("the exit must not have been reported before the grace decision");

                reaper.HoldSweep = null;
                probe.Connection.WaitForExit(TestTimeoutMs).Should().BeTrue("the reaper's sweep reports the exit its lost event did not");
                probe.Sent.Select(s => s.Signal).Should().Equal(
                    new[] { SIGHUP },
                    "the child died of SIGHUP, so the grace period must not send it SIGKILL however late the exit is reported");
                probe.Connection.ExitSignal.Should().Be(SIGHUP);
            }
            finally
            {
                reaper.LoseExitEvent = null;
                reaper.HoldSweep = null;
                if (!probe.Connection.WaitForExit(0))
                {
                    // Not collected, so the pid is still the child's own.
                    kill(pid, SIGKILL);
                }
            }
        }

        /// <summary>
        /// macOS: SIGKILL is the fallback for a child that ignores SIGHUP, and only for it. A child that
        /// dies of SIGHUP receives SIGHUP alone; a child that ignores it receives SIGHUP, then SIGKILL, and dies.
        /// </summary>
        [TestMethod]
        [DataRow(false, false, DisplayName = "dies of SIGHUP, blocking")]
        [DataRow(false, true, DisplayName = "dies of SIGHUP, non-blocking")]
        [DataRow(true, false, DisplayName = "ignores SIGHUP, blocking")]
        [DataRow(true, true, DisplayName = "ignores SIGHUP, non-blocking")]
        public void Mac_SigKillIsSentOnlyToAChildThatIgnoresSigHup(bool ignoresSigHup, bool useAsyncIo)
        {
            if (!IsMac)
            {
                Assert.Inconclusive("only macOS follows SIGHUP with SIGKILL");
            }

            var expected = ignoresSigHup ? new[] { SIGHUP, SIGKILL } : new[] { SIGHUP };
            var wrong = new List<string>();
            var lost = 0;
            for (var i = 0; i < 5; i++)
            {
                var pid = SpawnChild(ignoresSigHup ? "trap '' HUP; exec /bin/sleep 600" : "exec /bin/sleep 600");

                // The ignored SIGHUP survives the exec, so once the child runs /bin/sleep it is in place;
                // a SIGHUP sent before the shell reached its trap would test nothing.
                WaitUntilRunning(pid, "/bin/sleep");
                using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: true);

                probe.Connection.Kill();

                if (!probe.Connection.WaitForExit(TestTimeoutMs))
                {
                    // A dead child the library did not report is the exit the non-blocking reaper sometimes
                    // loses: the iteration is not counted. A child still running is a failure.
                    var status = 0;
                    if (waitpid(pid, ref status, WNOHANG) == pid)
                    {
                        lost++;
                        continue;
                    }

                    kill(pid, SIGKILL);
                    waitpid(pid, ref status, 0);
                    wrong.Add($"iteration {i}: child {pid} still running {TestTimeoutMs} ms after Kill");
                    continue;
                }

                var sent = probe.Sent.Select(s => s.Signal).ToArray();
                if (!sent.SequenceEqual(expected))
                {
                    wrong.Add($"iteration {i}: sent [{string.Join(", ", sent)}]");
                }
            }

            if (lost == 5)
            {
                Assert.Inconclusive("the library reported the exit of none of the 5 children (the lost exit of the non-blocking reaper)");
            }

            wrong.Should().BeEmpty(
                "Kill of a child that {0} SIGHUP should send exactly [{1}] (useAsyncIo={2}; {3} exits lost by the reaper)",
                ignoresSigHup ? "ignores" : "dies of",
                string.Join(", ", expected),
                useAsyncIo,
                lost);
        }

        /// <summary>
        /// Guards the macOS platform kill step against sending SIGHUP or SIGKILL straight to the pid instead
        /// of through SignalChild; no other test fails when it does. The step is called directly,
        /// past the public Kill's own check, so only SignalChild's check stands between it and a free pid.
        /// Once before the step: the test ends the child and waits until the library has collected it; the
        /// step must send nothing. Once during the grace period: the collected flag is set as SIGHUP leaves,
        /// with the exit event still unset, so the step waits its 100 ms and reaches SIGKILL with the child
        /// collected; only SIGHUP may have been sent. Nothing is sent for real (forward: false).
        /// </summary>
        [TestMethod]
        [DataRow(true, false, DisplayName = "collected before the step, blocking")]
        [DataRow(true, true, DisplayName = "collected before the step, non-blocking")]
        [DataRow(false, false, DisplayName = "collected during the grace period, blocking")]
        [DataRow(false, true, DisplayName = "collected during the grace period, non-blocking")]
        public void Mac_ThePlatformKillStep_SignalsNoCollectedChild(bool collectedBefore, bool useAsyncIo)
        {
            if (!IsMac)
            {
                Assert.Inconclusive("the SIGHUP then SIGKILL kill step is the macOS one");
            }

            var pid = SpawnChild("exec /bin/sleep 600");
            WaitUntilRunning(pid, "/bin/sleep");
            var sent = new ConcurrentQueue<Sent>();
            using var mac = new RecordingMacConnection(OpenDevNull(), pid, useAsyncIo, forward: false, sent);
            var reaped = typeof(Unix.PtyConnection).GetField("reaped", BindingFlags.Instance | BindingFlags.NonPublic);
            reaped.Should().NotBeNull("Unix.PtyConnection should keep its collected flag in a field named reaped");

            try
            {
                if (collectedBefore)
                {
                    kill(pid, SIGTERM).Should().Be(0, $"kill(2) of the running child failed with errno {Marshal.GetLastPInvokeError()}");
                    if (!mac.WaitForExit(TestTimeoutMs))
                    {
                        Assert.Inconclusive($"the library did not collect child {pid} within {TestTimeoutMs} ms (the lost exit of the non-blocking reaper)");
                    }
                }
                else
                {
                    mac.AfterSignal = signal =>
                    {
                        if (signal == SIGHUP)
                        {
                            reaped!.SetValue(mac, true);
                        }
                    };
                }

                mac.PlatformKill();

                var signals = sent.Select(s => s.Signal).ToArray();
                signals.Should().Equal(
                    collectedBefore ? Array.Empty<int>() : new[] { SIGHUP },
                    "the platform kill step must not signal a child the library has collected ({0}, useAsyncIo={1}); it sent [{2}]",
                    collectedBefore ? "collected before the step" : "collected during the grace period",
                    useAsyncIo,
                    string.Join(", ", signals));
            }
            finally
            {
                if (!collectedBefore)
                {
                    // Still running and not collected (the flag was set by the test), so the pid is still the child's.
                    kill(pid, SIGKILL);
                    mac.WaitForExit(TestTimeoutMs);
                }
            }
        }

        /// <summary>
        /// The controller is close-on-exec from the moment pty_spawn returns, so a process the host starts
        /// outside Porta (Process.Start) does not inherit terminal A's controller. Closing descriptors in
        /// Porta's own children cannot cover these. The terminals each child holds above stderr are listed
        /// with shell builtins before and after terminal A is spawned; A must add none.
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Unix_TheControllerIsCloseOnExec_SoAProcessStartedOutsidePortaDoesNotInheritIt(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("close-on-exec is a Unix concern");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var before = TerminalsSeenByAProcessStart();

            using var terminalA = await PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "A",
                    Cols = 80,
                    Rows = 25,
                    Cwd = Environment.CurrentDirectory,
                    App = "/bin/cat",
                    CommandLine = Array.Empty<string>(),
                    Environment = new Dictionary<string, string>(),
                    UseAsyncIo = useAsyncIo,
                },
                cts.Token);

            var added = TerminalsSeenByAProcessStart().Except(before).ToArray();

            added.Should().BeEmpty(
                "a process started outside Porta should not inherit terminal A's controller (useAsyncIo={0}), but it held terminal descriptors [{1}]",
                useAsyncIo,
                string.Join(", ", added));
        }

        private static int[] TerminalsSeenByAProcessStart()
        {
            var start = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("n=3; while [ $n -lt 1024 ]; do if [ -t $n ]; then printf '%s ' $n; fi; n=$((n+1)); done; echo");

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TestTimeoutMs).Should().BeTrue("the listing shell should exit");

            return output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        }

        private static int SpawnChild(string command)
        {
            var argv = new string?[] { "/bin/sh", "-c", command, null };
            var envp = new string?[] { null };
            var rc = posix_spawn(out var pid, "/bin/sh", IntPtr.Zero, IntPtr.Zero, argv, envp);
            rc.Should().Be(0, "posix_spawn of /bin/sh should succeed");
            pid.Should().BePositive();
            return pid;
        }

        private static void WaitUntilRunning(int pid, string path)
        {
            var buffer = new byte[4096];
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < TestTimeoutMs)
            {
                var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
                if (length > 0 && System.Text.Encoding.UTF8.GetString(buffer, 0, length) == path)
                {
                    return;
                }

                Thread.Sleep(1);
            }

            Assert.Fail($"child {pid} did not exec {path} within {TestTimeoutMs} ms");
        }

        /// <summary>
        /// Sends SIGSTOP and waits until the child is seen stopped, sending it again on each look that finds
        /// the child still running.
        /// </summary>
        /// <remarks>
        /// macOS sometimes loses a SIGSTOP sent to a running process: the process carries on, unstopped,
        /// and nothing stops it again. Measured in a standalone C harness, the same posix_spawn'd shell loop
        /// lost about 1 SIGSTOP in 200 to 400 while its parent was waiting on it (waitpid with WUNTRACED, as
        /// the library's blocking watcher does), and the process went on using CPU as if never signalled;
        /// none were lost without the waiter. That is what failed this test on a CI runner ("did not stop
        /// after SIGSTOP"). A SIGSTOP to a process that is already stopped does nothing, so sending it again
        /// is safe and converges.
        /// </remarks>
        /// <param name="pid">The child.</param>
        /// <param name="send">How a SIGSTOP is sent; the default is kill(2). A seam for the test of this helper.</param>
        private static void StopAndWait(int pid, Func<int, int, int>? send = null)
        {
            send ??= kill;
            send(pid, SIGSTOP).Should().Be(0, $"SIGSTOP to child {pid} failed with errno {Marshal.GetLastPInvokeError()}");
            WaitUntil(
                () =>
                {
                    if (IsStopped(pid))
                    {
                        return true;
                    }

                    send(pid, SIGSTOP);
                    return false;
                },
                $"child {pid} did not stop after SIGSTOP");
        }

        private static void WaitUntil(Func<bool> condition, string failure)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > TestTimeoutMs)
                {
                    Assert.Fail($"{failure} within {TestTimeoutMs} ms");
                }

                Thread.Sleep(5);
            }
        }

        /// <summary>Whether the process is stopped: SSTOP in its BSD info on macOS, state T in /proc on Linux.</summary>
        private static bool IsStopped(int pid)
        {
            if (IsMac)
            {
                const int PROC_PIDTBSDINFO = 3;
                const int SizeOfProcBsdInfo = 136;
                const uint SSTOP = 4;
                var info = new byte[SizeOfProcBsdInfo];
                return proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, info, info.Length) == info.Length
                    && BitConverter.ToUInt32(info, 4) == SSTOP;
            }

            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[stat.LastIndexOf(')') + 2] is 'T' or 't';
        }

        private static int OpenDevNull()
        {
            var fd = open("/dev/null", O_RDWR);
            fd.Should().BeGreaterThan(2, $"open(/dev/null) failed with errno {Marshal.GetLastPInvokeError()}");
            return fd;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawn(out int pid, string path, IntPtr fileActions, IntPtr attributes, string?[] argv, string?[] envp);

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        [DllImport("libc", SetLastError = true)]
        private static extern int proc_pidpath(int pid, byte[] buffer, uint size);

        [DllImport("libc", SetLastError = true)]
        private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int size);

        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, ref int status, int options);

        private readonly record struct Sent(int Signal, int Result, int Errno);

        /// <summary>Counts the library's no-hang waits, and runs a hook right after a wait that collected the child.</summary>
        private sealed class WaitRecord
        {
            private int noHangWaits;

            /// <summary>Called on the collecting thread, before the library has marked the child collected.</summary>
            public Action? AfterCollect { get; set; }

            public int NoHangWaits => Volatile.Read(ref this.noHangWaits);

            public int NoHang(int pid, int result, int status)
            {
                Interlocked.Increment(ref this.noHangWaits);
                this.Waited(result == pid, status);
                return result;
            }

            public bool Blocking(bool collected, int status)
            {
                this.Waited(collected, status);
                return collected;
            }

            private void Waited(bool reported, int status)
            {
                // Low bits 0x7f are a stop or continue report, which collects nothing.
                if (!reported || (status & 0x7f) == 0x7f || this.AfterCollect is not { } hook)
                {
                    return;
                }

                var errno = Marshal.GetLastPInvokeError();
                hook();
                Marshal.SetLastPInvokeError(errno);
            }
        }

        /// <summary>The library's own platform connection, with every signal it sends recorded.</summary>
        private sealed class Probe : IDisposable
        {
            private readonly Func<int, bool> signal;

            private Probe(Unix.PtyConnection connection, ConcurrentQueue<Sent> sent, Func<int, bool> signal, WaitRecord waits)
            {
                this.Connection = connection;
                this.Sent = sent;
                this.signal = signal;
                this.Waits = waits;
            }

            public Unix.PtyConnection Connection { get; }

            public ConcurrentQueue<Sent> Sent { get; }

            public WaitRecord Waits { get; }

            /// <summary>
            /// Records each signal; when <paramref name="forward"/> is false nothing is sent, so a signal the
            /// library should not have sent cannot reach a reused pid on the developer's machine.
            /// </summary>
            public static Probe Create(int controller, int pid, bool useAsyncIo, bool forward)
            {
                var sent = new ConcurrentQueue<Sent>();
                if (IsMac)
                {
                    var mac = new RecordingMacConnection(controller, pid, useAsyncIo, forward, sent);
                    return new Probe(mac, sent, mac.Signal, mac.Waits);
                }

                var linux = new RecordingLinuxConnection(controller, pid, useAsyncIo, forward, sent);
                return new Probe(linux, sent, linux.Signal, linux.Waits);
            }

            public bool Signal(int signalNumber) => this.signal(signalNumber);

            /// <summary>Waits for the connection's private collected flag; returns whether it was set.</summary>
            public bool WaitUntilMarkedCollected(int timeoutMs)
            {
                var field = typeof(Unix.PtyConnection).GetField("reaped", BindingFlags.Instance | BindingFlags.NonPublic);
                field.Should().NotBeNull("Unix.PtyConnection should keep its collected flag in a field named reaped");

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    if ((bool)field!.GetValue(this.Connection)!)
                    {
                        return true;
                    }

                    Thread.Sleep(5);
                }

                return false;
            }

            public void Dispose() => this.Connection.Dispose();
        }

        private sealed class RecordingMacConnection : Mac.PtyConnection
        {
            private readonly bool forward;
            private readonly ConcurrentQueue<Sent> sent;

            public RecordingMacConnection(int controller, int pid, bool useAsyncIo, bool forward, ConcurrentQueue<Sent> sent)
                : base(controller, pid, useAsyncIo)
            {
                this.forward = forward;
                this.sent = sent;
            }

            public bool Signal(int signal) => this.SignalChild(signal);

            /// <summary>Called with each signal once it is recorded, on the thread that sent it.</summary>
            public Action<int>? AfterSignal { get; set; }

            public WaitRecord Waits { get; } = new WaitRecord();

            /// <summary>The platform kill step itself, without the public Kill's check before it.</summary>
            public bool PlatformKill() => this.Kill(0);

            protected override int WaitPidNoHang(int pid, ref int status)
            {
                var result = base.WaitPidNoHang(pid, ref status);
                return this.Waits.NoHang(pid, result, status);
            }

            protected override bool WaitPid(int pid, ref int status)
            {
                var collected = base.WaitPid(pid, ref status);
                return this.Waits.Blocking(collected, status);
            }

            protected override int SendSignal(int pid, int signal)
            {
                var result = this.forward ? base.SendSignal(pid, signal) : 0;
                var errno = result == -1 ? Marshal.GetLastPInvokeError() : 0;
                this.sent.Enqueue(new Sent(signal, result, errno));
                this.AfterSignal?.Invoke(signal);
                if (result == -1)
                {
                    Marshal.SetLastPInvokeError(errno);
                }

                return result;
            }
        }

        private sealed class RecordingLinuxConnection : Linux.PtyConnection
        {
            private readonly bool forward;
            private readonly ConcurrentQueue<Sent> sent;

            public RecordingLinuxConnection(int controller, int pid, bool useAsyncIo, bool forward, ConcurrentQueue<Sent> sent)
                : base(controller, pid, useAsyncIo)
            {
                this.forward = forward;
                this.sent = sent;
            }

            public bool Signal(int signal) => this.SignalChild(signal);

            public WaitRecord Waits { get; } = new WaitRecord();

            protected override int WaitPidNoHang(int pid, ref int status)
            {
                var result = base.WaitPidNoHang(pid, ref status);
                return this.Waits.NoHang(pid, result, status);
            }

            protected override bool WaitPid(int pid, ref int status)
            {
                var collected = base.WaitPid(pid, ref status);
                return this.Waits.Blocking(collected, status);
            }

            protected override int SendSignal(int pid, int signal)
            {
                var result = this.forward ? base.SendSignal(pid, signal) : 0;
                var errno = result == -1 ? Marshal.GetLastPInvokeError() : 0;
                this.sent.Enqueue(new Sent(signal, result, errno));
                if (result == -1)
                {
                    Marshal.SetLastPInvokeError(errno);
                }

                return result;
            }
        }
    }
}
