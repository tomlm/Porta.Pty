// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// <see cref="PtyOptions.InheritEnvironment"/>, <see cref="IPtyConnection.ExitSignal"/> and
    /// <see cref="IPtyConnection.HasExited"/>, and <see cref="IPtyConnection.KillAsync"/>.
    ///
    /// <para>Environment assertions compare variable NAMES only, so a failure never prints a value from
    /// the machine running the tests.</para>
    /// </summary>
    [TestClass]
    public class EnvironmentExitSignalKillTests
    {
        private const string EndMarker = "__PORTAPTY_END__";
        private const string ManagedOnlyVariable = "PORTAPTY_TEST_PARENT_ONLY";
        private const int SIGHUP = 1;
        private const int SIGKILL = 9;
        private const int SIGSEGV = 11;
        private const int ESRCH = 3;

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

        /// <summary>
        /// A background grandchild that ignores SIGHUP, and says so only once it does: a separate sh arms the
        /// trap, prints its own pid (the one sleep keeps across exec) and only then becomes the sleeper. A pid
        /// printed by the parent with $! could be read before the trap is set, letting a kill that never
        /// reached the grandchild with SIGKILL pass because SIGHUP still could.
        /// </summary>
        private const string ArmedSigHupImmuneGrandchild =
            "sh -c 'trap \"\" HUP; echo \"GRANDCHILD $$ ARMED\"; exec sleep 600' &";

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// A variable the test host was started with: in the native environ the forked child copies, not
        /// only in .NET's managed copy, which is all <see cref="Environment.SetEnvironmentVariable(string, string)"/>
        /// changes on Unix.
        /// </summary>
        private static string InheritedVariable => IsWindows ? "USERPROFILE" : "HOME";

        [TestInitialize]
        public void SetParentOnlyVariable() => Environment.SetEnvironmentVariable(ManagedOnlyVariable, "1");

        [TestCleanup]
        public void ClearParentOnlyVariable() => Environment.SetEnvironmentVariable(ManagedOnlyVariable, null);

        [TestMethod]
        public async Task InheritEnvironmentFalse_ChildSeesOnlyTheGivenVariables()
        {
            RequireInheritedVariable();

            var given = BaseCleanEnvironment();
            given["PORTAPTY_GIVEN"] = "yes";

            var names = (await RunEnvAsync(given, inherit: false)).Keys.ToList();

            names.Should().Contain("PORTAPTY_GIVEN", "a given variable must reach the child; it saw [{0}]", string.Join(", ", names));
            names.Should().NotContain(
                InheritedVariable,
                "with InheritEnvironment false the child must not keep the parent's native environment; it saw [{0}]",
                string.Join(", ", names));
            names.Should().NotContain(
                ManagedOnlyVariable,
                "with InheritEnvironment false the parent's environment must not be merged in; it saw [{0}]",
                string.Join(", ", names));

            if (!IsWindows)
            {
                // Exactly what was given, plus what the shell printing it sets for itself. TERM in
                // particular is NOT defaulted.
                var shellOwn = new[] { "PWD", "SHLVL", "OLDPWD", "_" };
                names.Except(given.Keys).Except(shellOwn).Should().BeEmpty(
                    "the child should see exactly the given variables; it saw [{0}]", string.Join(", ", names));
            }
        }

        [TestMethod]
        public async Task InheritEnvironmentFalse_EmptyValueIsSetNotUnset()
        {
            var given = BaseCleanEnvironment();
            given["PORTAPTY_EMPTY"] = string.Empty;

            var seen = await RunEnvAsync(given, inherit: false);

            seen.Keys.Should().Contain("PORTAPTY_EMPTY","an empty value must set the variable, not remove it; the child saw [{0}]", string.Join(", ", seen.Keys));
            seen["PORTAPTY_EMPTY"].Should().BeEmpty();
        }

        [TestMethod]
        public async Task InheritEnvironmentDefault_IsTrueAndKeepsTheParentsVariables()
        {
            RequireInheritedVariable();

            new PtyOptions().InheritEnvironment.Should().BeTrue("existing callers must keep inheriting");

            // Default options: nothing set but the given variable, exactly as callers did before.
            var names = (await RunEnvAsync(new Dictionary<string, string> { ["PORTAPTY_GIVEN"] = "yes" }, inherit: null)).Keys.ToList();

            names.Should().Contain(new[] { "PORTAPTY_GIVEN", InheritedVariable, ManagedOnlyVariable });
            if (!IsWindows)
            {
                names.Should().Contain("TERM", "the library still defaults TERM when inheriting");
            }
        }

        [TestMethod]
        [DataRow(SIGKILL, DisplayName = "SIGKILL")]
        [DataRow(SIGSEGV, DisplayName = "SIGSEGV")]
        public async Task Unix_ChildKilledByASignal_ReportsExitSignal(int signal)
        {
            RequireUnix();

            using var terminal = await SpawnShellAsync($"kill -{signal} $$");
            var exitedArgs = new TaskCompletionSource<PtyExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) => exitedArgs.TrySetResult(e);

            if (!terminal.HasExited)
            {
                // Subscribed after spawn, so a very fast exit can have been raised already; then
                // there is no event to wait for, but the properties below still have to be right.
                await Task.WhenAny(exitedArgs.Task, Task.Delay(TestTimeoutMs));
            }

            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("the child should die of signal {0}", signal);
            terminal.HasExited.Should().BeTrue();
            terminal.ExitSignal.Should().Be(signal);
            terminal.ExitCode.Should().Be(0, "ExitCode keeps its old value for a signal death");
            if (exitedArgs.Task.IsCompleted)
            {
                exitedArgs.Task.Result.ExitSignal.Should().Be(signal, "the exit event carries the signal too");
            }
        }

        [TestMethod]
        public async Task NormalExit_HasNoExitSignal_AndHasExitedFollowsTheProcess()
        {
            using var running = await SpawnShellAsync(IsWindows ? "ping -n 600 127.0.0.1 >nul" : "exec sleep 600");
            running.HasExited.Should().BeFalse("the child is still running");
            running.ExitSignal.Should().Be(0, "a running child has no exit signal");
            running.Kill();

            using var terminal = await SpawnShellAsync("exit 3");
            var exitedArgs = new TaskCompletionSource<PtyExitedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) => exitedArgs.TrySetResult(e);

            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue();
            terminal.HasExited.Should().BeTrue();
            terminal.ExitCode.Should().Be(3);
            terminal.ExitSignal.Should().Be(0, "a normal exit has no signal");
            if (exitedArgs.Task.IsCompleted)
            {
                exitedArgs.Task.Result.ExitSignal.Should().Be(0);
            }
        }

        [TestMethod]
        public async Task Unix_KillAsync_ChildThatDiesOfSigHup_GetsNoSigKill()
        {
            RequireUnix();

            using var terminal = await SpawnShellAsync("exec sleep 600");

            await terminal.KillAsync(grace: TimeSpan.FromMilliseconds(300));

            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue();
            terminal.ExitSignal.Should().Be(SIGHUP, "a child that dies of SIGHUP is not killed again");
        }

        /// <summary>
        /// The shell dies of SIGHUP, but the sleeper it started in the background ignores it. The shell is
        /// held uncollected until the group's SIGKILL, so its group id is still reserved and that SIGKILL
        /// still reaches the orphan. Both collection paths: the blocking watcher and the shared reaper.
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Unix_KillAsync_EndsADescendantThatIgnoresSigHup_AfterTheChildDiesOfIt(bool useAsyncIo)
        {
            RequireUnix();

            var options = ShellOptions($"{ArmedSigHupImmuneGrandchild} wait; echo {EndMarker}");
            options.UseAsyncIo = useAsyncIo;
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);
            int grandchild = await ReadGrandchildPidAsync(terminal);

            try
            {
                await terminal.KillAsync(grace: TimeSpan.FromMilliseconds(300));

                (await WaitUntilGoneAsync(grandchild)).Should().BeTrue(
                    "the grandchild (pid {0}) ignores SIGHUP, so only the group SIGKILL sent after the child died can end it; ps reports '{1}'",
                    grandchild,
                    ProcessState(grandchild));
                terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("the child's exit is still reported");
                terminal.HasExited.Should().BeTrue();
                terminal.ExitSignal.Should().Be(SIGHUP, "the child itself died of the SIGHUP, before the SIGKILL");
            }
            finally
            {
                kill(grandchild, SIGKILL);
            }
        }

        /// <summary>
        /// The shell and its background sleeper both ignore SIGHUP, so only the SIGKILL that follows the
        /// grace period, sent to the whole process group, ends both.
        /// </summary>
        [TestMethod]
        public async Task Unix_KillAsync_EndsTheChildAndItsGrandchild()
        {
            RequireUnix();

            using var terminal = await SpawnShellAsync($"trap '' HUP; sleep 600 & echo GRANDCHILD=$!; wait; echo {EndMarker}");
            int grandchild = await ReadGrandchildPidAsync(terminal);

            try
            {
                Task kill = terminal.KillAsync(grace: TimeSpan.FromMilliseconds(200));
                kill.IsCompleted.Should().BeFalse("KillAsync must not block the caller through the grace period");
                await kill;

                terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("the child ignores SIGHUP, so it should die of the SIGKILL");
                terminal.ExitSignal.Should().Be(SIGKILL);
                (await WaitUntilGoneAsync(grandchild)).Should().BeTrue(
                    "the grandchild (pid {0}) is in the child's process group and should die with it; ps reports '{1}'",
                    grandchild,
                    ProcessState(grandchild));
            }
            finally
            {
                kill(grandchild, SIGKILL);
            }
        }

        /// <summary>Control: without the tree, the same grandchild outlives the child.</summary>
        [TestMethod]
        public async Task Unix_KillAsyncWithoutTree_LeavesTheGrandchild()
        {
            RequireUnix();

            using var terminal = await SpawnShellAsync($"trap '' HUP; sleep 600 & echo GRANDCHILD=$!; wait; echo {EndMarker}");
            int grandchild = await ReadGrandchildPidAsync(terminal);

            try
            {
                await terminal.KillAsync(entireProcessTree: false, grace: TimeSpan.FromMilliseconds(200));

                terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("the child should die of the SIGKILL");
                terminal.ExitSignal.Should().Be(SIGKILL);
                IsAlive(grandchild).Should().BeTrue(
                    "entireProcessTree: false signals the child alone, so the grandchild (pid {0}) ignoring the hangup should survive; ps reports '{1}'",
                    grandchild,
                    ProcessState(grandchild));
            }
            finally
            {
                kill(grandchild, SIGKILL);
            }
        }

        /// <summary>
        /// A child alone in its group that dies of SIGHUP: the tree kill ends as soon as the group is empty.
        /// The grace period runs on a clock that never fires, so completing at all shows the grace did not
        /// end the wait.
        /// </summary>
        [TestMethod]
        public async Task Unix_KillAsync_GroupEmptyAfterSigHup_EndsWithoutWaitingTheGrace()
        {
            RequireUnix();

            using var terminal = await SpawnShellAsync("exec sleep 600");
            var clock = new FrozenTimeProvider();
            ((Unix.PtyConnection)terminal).KillTimeProvider = clock;

            Task kill = terminal.KillAsync(grace: TimeSpan.FromHours(1));
            (await Task.WhenAny(kill, Task.Delay(TestTimeoutMs))).Should().BeSameAs(kill, "nothing is left in the group once the child dies of SIGHUP, so the kill must not wait for the grace timer");
            await kill;

            clock.TimersCreated.Should().BePositive("the grace period runs on the injected clock");
            clock.Fired.Should().BeFalse();
            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue();
            terminal.ExitSignal.Should().Be(SIGHUP);
        }

        /// <summary>
        /// When the group's membership cannot be read (a failed /proc read, or a sysctl error), the kill may
        /// not treat the group as empty: the child dies of SIGHUP, but the kill still waits until the grace
        /// timer fires. The probe is injected to answer "unknown", as the native probe does on a read error.
        /// </summary>
        [TestMethod]
        public async Task Unix_KillAsync_UnreadableGroup_WaitsForTheGrace()
        {
            RequireUnix();

            using var terminal = await SpawnShellAsync("exec sleep 600");
            var connection = (Unix.PtyConnection)terminal;
            var clock = new FrozenTimeProvider();
            int probes = 0;
            connection.KillTimeProvider = clock;
            connection.GroupProbe = (_, _) =>
            {
                Interlocked.Increment(ref probes);
                return -1;
            };

            Task kill = terminal.KillAsync(grace: TimeSpan.FromHours(1));

            (await WaitUntilAsync(() => Volatile.Read(ref probes) > 0)).Should().BeTrue("the child dies of SIGHUP, after which the kill asks about its group");
            (await Task.WhenAny(kill, Task.Delay(500))).Should().NotBeSameAs(kill, "an unreadable group must not count as empty");

            clock.FireAll();
            await kill.WaitAsync(TimeSpan.FromMilliseconds(TestTimeoutMs));

            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue();
            terminal.ExitSignal.Should().Be(SIGHUP);
        }

        /// <summary>
        /// The child dies of SIGHUP but a member that ignores it is left: the kill waits for the grace timer,
        /// and its SIGKILL then ends that member.
        /// </summary>
        [TestMethod]
        public async Task Unix_KillAsync_SigHupImmuneMember_WaitsForTheGraceThenKillsIt()
        {
            RequireUnix();

            using var terminal = await SpawnShellAsync($"{ArmedSigHupImmuneGrandchild} wait; echo {EndMarker}");
            int grandchild = await ReadGrandchildPidAsync(terminal);
            var clock = new FrozenTimeProvider();
            ((Unix.PtyConnection)terminal).KillTimeProvider = clock;

            try
            {
                Task kill = terminal.KillAsync(grace: TimeSpan.FromHours(1));

                // The child is a held zombie once it has died of the SIGHUP; from then the kill is only
                // waiting on its group, where the grandchild is still alive.
                (await WaitUntilAsync(() => ProcessState(terminal.Pid).StartsWith('Z'))).Should().BeTrue("the child should die of SIGHUP and be held uncollected");
                (await Task.WhenAny(kill, Task.Delay(500))).Should().NotBeSameAs(kill, "a living member that ignores SIGHUP must keep the kill waiting for the grace");
                IsAlive(grandchild).Should().BeTrue();

                clock.FireAll();
                await kill.WaitAsync(TimeSpan.FromMilliseconds(TestTimeoutMs));

                (await WaitUntilGoneAsync(grandchild)).Should().BeTrue("the SIGKILL after the grace should end the member that ignored SIGHUP");
                terminal.WaitForExit(TestTimeoutMs).Should().BeTrue();
                terminal.ExitSignal.Should().Be(SIGHUP);
            }
            finally
            {
                kill(grandchild, SIGKILL);
            }
        }

        /// <summary>
        /// The child ignores SIGHUP and is alone in its group: a living child is itself a member, so the kill
        /// waits for the grace and SIGKILL ends the child.
        /// </summary>
        [TestMethod]
        public async Task Unix_KillAsync_ChildIgnoringSigHup_WaitsForTheGrace()
        {
            RequireUnix();

            using var terminal = await SpawnShellAsync("trap '' HUP; echo ARMED; exec sleep 600");
            await ReadArmedAsync(terminal);
            var clock = new FrozenTimeProvider();
            ((Unix.PtyConnection)terminal).KillTimeProvider = clock;

            Task kill = terminal.KillAsync(grace: TimeSpan.FromHours(1));
            (await Task.WhenAny(kill, Task.Delay(500))).Should().NotBeSameAs(kill, "a child that survives SIGHUP must get the whole grace period");
            terminal.HasExited.Should().BeFalse();

            clock.FireAll();
            await kill.WaitAsync(TimeSpan.FromMilliseconds(TestTimeoutMs));

            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue();
            terminal.ExitSignal.Should().Be(SIGKILL);
        }

        /// <summary>
        /// A child that does not lead a process group yet, as a pty child is between forkpty returning in the
        /// parent and its setsid: signalling the group finds none (ESRCH), and the child itself must still be
        /// killed. Started here with posix_spawn, which leaves it in the test host's group, so no group bears
        /// its pid; the connection is the library's own, over /dev/null.
        /// </summary>
        [TestMethod]
        public async Task Unix_KillAsync_ChildNotYetLeadingAGroup_IsStillKilled()
        {
            RequireUnix();

            var argv = new string?[] { "/bin/sh", "-c", "exec /bin/sleep 600", null };
            posix_spawn(out int pid, "/bin/sh", IntPtr.Zero, IntPtr.Zero, argv, new string?[] { null }).Should().Be(0);
            int controller = open("/dev/null", 2);
            controller.Should().BeGreaterThan(2);

            using Unix.PtyConnection connection = IsMac
                ? new Mac.PtyConnection(controller, pid, useAsyncIo: false)
                : new Linux.PtyConnection(controller, pid, useAsyncIo: false);
            try
            {
                await connection.KillAsync(grace: TimeSpan.Zero);

                connection.WaitForExit(TestTimeoutMs).Should().BeTrue("a child with no group yet must still be signalled by pid");
                connection.ExitSignal.Should().BePositive();
            }
            finally
            {
                kill(pid, SIGKILL);
            }
        }

        /// <summary>
        /// The shared reaper must take <see cref="Unix.PtyReaper.Deferred"/> as final whatever errno happens to
        /// hold. A stale EINTR left beside it used to read as "interrupted, try again", and the reaper retried
        /// the deferred child in a tight loop until its hold was released. The attempt here is the library's
        /// contract played by the test: it defers once the child has exited, leaving EINTR behind on purpose.
        /// </summary>
        [TestMethod]
        public async Task Unix_Reaper_TakesDeferredAsFinal_EvenWithAStaleEintr()
        {
            RequireUnix();
            if (!Unix.PtyReaper.Instance.IsSupported)
            {
                Assert.Inconclusive("the shared reaper needs kernel support here");
            }

            const int EINTR = 4;
            var argv = new string?[] { "/bin/sh", "-c", "exec /bin/sleep 600", null };
            posix_spawn(out int pid, "/bin/sh", IntPtr.Zero, IntPtr.Zero, argv, new string?[] { null }).Should().Be(0);
            int deferrals = 0;

            try
            {
                Unix.PtyReaper.Instance.Register(
                    pid,
                    (int p, ref int status) =>
                    {
                        if (pty_exited_noreap(p) != 1)
                        {
                            return 0;
                        }

                        Interlocked.Increment(ref deferrals);
                        Marshal.SetLastPInvokeError(EINTR);
                        return Unix.PtyReaper.Deferred;
                    },
                    _ => { });

                kill(pid, SIGKILL).Should().Be(0);
                (await WaitUntilAsync(() => Volatile.Read(ref deferrals) > 0)).Should().BeTrue("the reaper should see the exit and be told it is deferred");

                // Bounded only to give a retrying reaper the chance to show itself; a correct one stops at one.
                await Task.Delay(200);
                Volatile.Read(ref deferrals).Should().Be(1, "a deferred child is no longer the reaper's to collect, however errno reads");
            }
            finally
            {
                int status = 0;
                waitpid(pid, ref status, 0);
            }
        }

        [TestMethod]
        public async Task Windows_KillAsync_EndsTheChild()
        {
            if (!IsWindows)
            {
                Assert.Inconclusive("the Unix tests above cover the process group");
            }

            using var terminal = await SpawnShellAsync("ping -n 600 127.0.0.1 >nul");

            await terminal.KillAsync();

            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("terminating the job object ends the child");
            terminal.HasExited.Should().BeTrue();
        }

        /// <summary>
        /// The job object holds the pty child and everything it starts, so terminating it ends a grandchild
        /// too. The grandchild is a second PowerShell started with -NoNewWindow, which keeps it on the
        /// pseudoconsole and in the job.
        /// </summary>
        [TestMethod]
        public async Task Windows_KillAsync_EndsTheChildAndItsGrandchild()
        {
            RequireWindows();

            using var terminal = await SpawnWindowsGrandchildParentAsync();
            int grandchild = await ReadGrandchildPidAsync(terminal);

            try
            {
                await terminal.KillAsync(entireProcessTree: true);

                terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("terminating the job ends the child");
                (await WaitUntilGoneAsync(grandchild)).Should().BeTrue(
                    "the grandchild (pid {0}) is in the child's job object and should end with it", grandchild);
            }
            finally
            {
                KillQuietly(grandchild);
            }
        }

        /// <summary>Control: without the tree only the child is killed, and the same grandchild survives.</summary>
        [TestMethod]
        public async Task Windows_KillAsyncWithoutTree_LeavesTheGrandchild()
        {
            RequireWindows();

            using var terminal = await SpawnWindowsGrandchildParentAsync();
            int grandchild = await ReadGrandchildPidAsync(terminal);

            try
            {
                await terminal.KillAsync(entireProcessTree: false);

                terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("Kill ends the child");

                // Checked before Dispose, which closes the job (KILL_ON_JOB_CLOSE) and would end it anyway.
                IsAlive(grandchild).Should().BeTrue(
                    "entireProcessTree: false kills the child alone, so the grandchild (pid {0}) should survive", grandchild);
            }
            finally
            {
                KillQuietly(grandchild);
            }
        }

        /// <summary>macOS Kill still escalates to SIGKILL for a child that ignores SIGHUP, now from a timer.</summary>
        [TestMethod]
        public async Task Mac_Kill_StillEscalatesToSigKill()
        {
            if (!IsMac)
            {
                Assert.Inconclusive("only macOS Kill escalates to SIGKILL");
            }

            using var terminal = await SpawnShellAsync("trap '' HUP; echo ARMED; while :; do sleep 1; done");
            await ReadArmedAsync(terminal);

            terminal.Kill();

            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("the SIGKILL after the grace period should end a child that ignores SIGHUP");
            terminal.ExitSignal.Should().Be(SIGKILL);
        }

        private static void RequireUnix()
        {
            if (IsWindows)
            {
                Assert.Inconclusive("Unix signals and process groups");
            }
        }

        private static void RequireWindows()
        {
            if (!IsWindows)
            {
                Assert.Inconclusive("the Windows job object; the Unix tests cover the process group");
            }
        }

        /// <summary>A PowerShell pty child that starts a long-running PowerShell grandchild and prints its pid.</summary>
        private static async Task<IPtyConnection> SpawnWindowsGrandchildParentAsync()
        {
            const string Script =
                "$p = Start-Process -FilePath powershell.exe "
                + "-ArgumentList '-NoProfile','-Command','while ($true) { Start-Sleep -Seconds 1 }' "
                + "-NoNewWindow -PassThru; "
                + "Write-Output ('GRANDCHILD=' + $p.Id); "
                + "Wait-Process -Id $p.Id";

            var options = ShellOptions(string.Empty);
            options.App = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            options.CommandLine = new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)),
            };

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            return await PtyProvider.SpawnAsync(options, cts.Token);
        }

        private static void KillQuietly(int pid)
        {
            if (!IsWindows)
            {
                kill(pid, SIGKILL);
                return;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }

        private static void RequireInheritedVariable()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(InheritedVariable)))
            {
                Assert.Inconclusive($"the test host has no {InheritedVariable} to withhold");
            }
        }

        /// <summary>What a shell needs to run at all; the tests add to it.</summary>
        private static Dictionary<string, string> BaseCleanEnvironment()
        {
            if (IsWindows)
            {
                return new Dictionary<string, string>
                {
                    ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows",
                    ["PATH"] = Environment.SystemDirectory,
                };
            }

            return new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" };
        }

        /// <summary>Runs the platform's env listing and returns name to value, as the child saw it.</summary>
        /// <param name="inherit">The value for InheritEnvironment, or null to leave the option at its default.</param>
        private static async Task<Dictionary<string, string>> RunEnvAsync(Dictionary<string, string> environment, bool? inherit)
        {
            var options = ShellOptions(IsWindows ? $"set & echo {EndMarker}" : $"env; echo {EndMarker}");
            options.Environment = environment;
            if (inherit is { } value)
            {
                options.InheritEnvironment = value;
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using var terminal = await PtyProvider.SpawnAsync(options, cts.Token);
            string output = await ReadUntilAsync(terminal, EndMarker + "\r\n");

            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(output, @"^([A-Za-z_][A-Za-z0-9_()]*)=(.*?)\r?$", RegexOptions.Multiline))
            {
                seen[m.Groups[1].Value] = m.Groups[2].Value;
            }

            seen.Should().NotBeEmpty("the child should have printed its environment before {0}", EndMarker);
            return seen;
        }

        private static PtyOptions ShellOptions(string command)
        {
            return new PtyOptions
            {
                Name = "env-signal-kill",
                Cols = 200,
                Rows = 50,
                Cwd = Environment.CurrentDirectory,
                App = IsWindows ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh",
                CommandLine = IsWindows ? new[] { "/c", command } : new[] { "-c", command },
                VerbatimCommandLine = true,
                Environment = new Dictionary<string, string>(),
            };
        }

        private static async Task<IPtyConnection> SpawnShellAsync(string command)
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            return await PtyProvider.SpawnAsync(ShellOptions(command), cts.Token);
        }

        private static async Task<int> ReadGrandchildPidAsync(IPtyConnection terminal)
        {
            // Either the "GRANDCHILD=<pid>" a parent prints, or the "GRANDCHILD <pid> ARMED" line a grandchild
            // prints for itself once its SIGHUP trap is set; the second is read only as a complete line.
            string output = await ReadUntilAsync(terminal, "\n", after: "GRANDCHILD");
            var match = Regex.Match(output, @"GRANDCHILD(?:=(\d+)|\s+(\d+)\s+ARMED\r?\n)");
            match.Success.Should().BeTrue("the shell should print its background child's pid; it printed '{0}'", output);
            int pid = int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            IsAlive(pid).Should().BeTrue("precondition: the grandchild is running");
            return pid;
        }

        /// <summary>Waits for the complete "ARMED" line a child prints once its SIGHUP trap is set.</summary>
        private static async Task ReadArmedAsync(IPtyConnection terminal)
        {
            string output = await ReadUntilAsync(terminal, "\n", after: "ARMED");
            output.Should().MatchRegex(@"(^|\n)ARMED\r?\n", "the child should report its SIGHUP trap set before it is killed; it printed '{0}'", output);
        }

        /// <summary>Reads until <paramref name="text"/> appears (after <paramref name="after"/>, if given).</summary>
        private static async Task<string> ReadUntilAsync(IPtyConnection terminal, string text, string? after = null)
        {
            var buffer = new byte[4096];
            var output = new StringBuilder();
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < TestTimeoutMs)
            {
                var read = Task.Run(() => terminal.ReaderStream.Read(buffer, 0, buffer.Length));
                if (await Task.WhenAny(read, Task.Delay(TestTimeoutMs)) != read || await read <= 0)
                {
                    break;
                }

                output.Append(Encoding.UTF8.GetString(buffer, 0, read.Result));
                string s = output.ToString();
                int from = after is null ? 0 : s.IndexOf(after, StringComparison.Ordinal);
                if (from >= 0 && s.IndexOf(text, from, StringComparison.Ordinal) >= 0)
                {
                    break;
                }
            }

            return output.ToString();
        }

        /// <summary>Running, by process state: present and not exited (on Unix, not a zombie).</summary>
        private static bool IsAlive(int pid)
        {
            if (IsWindows)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return !process.HasExited;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            if (kill(pid, 0) == -1 && Marshal.GetLastPInvokeError() == ESRCH)
            {
                return false;
            }

            string state = ProcessState(pid);
            return state.Length > 0 && !state.StartsWith('Z');
        }

        /// <summary>Waits for a condition; the timeout bounds a hang, it is not the assertion.</summary>
        private static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < TestTimeoutMs)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return false;
        }

        /// <summary>Waits for the process to stop running; the timeout bounds a hang, it is not the assertion.</summary>
        private static async Task<bool> WaitUntilGoneAsync(int pid)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < TestTimeoutMs)
            {
                if (!IsAlive(pid))
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return false;
        }

        private static string ProcessState(int pid)
        {
            try
            {
                using var ps = Process.Start(new ProcessStartInfo("ps", $"-o stat= -p {pid}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                if (ps is null)
                {
                    return string.Empty;
                }

                string output = ps.StandardOutput.ReadToEnd().Trim();
                ps.WaitForExit(5000);
                return output;
            }
            catch
            {
                return string.Empty;
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        /// <summary>A clock whose timers fire only when the test says so.</summary>
        private sealed class FrozenTimeProvider : TimeProvider
        {
            private readonly List<FrozenTimer> timers = new();

            public int TimersCreated
            {
                get
                {
                    lock (this.timers)
                    {
                        return this.timers.Count;
                    }
                }
            }

            public bool Fired { get; private set; }

            public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                var timer = new FrozenTimer(callback, state);
                lock (this.timers)
                {
                    this.timers.Add(timer);
                }

                return timer;
            }

            public void FireAll()
            {
                FrozenTimer[] all;
                lock (this.timers)
                {
                    all = this.timers.ToArray();
                }

                this.Fired = true;
                foreach (var timer in all)
                {
                    timer.Fire();
                }
            }
        }

        private sealed class FrozenTimer : ITimer
        {
            private readonly TimerCallback callback;
            private readonly object? state;
            private bool disposed;

            public FrozenTimer(TimerCallback callback, object? state)
            {
                this.callback = callback;
                this.state = state;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !this.disposed;

            public void Fire()
            {
                if (!this.disposed)
                {
                    this.callback(this.state);
                }
            }

            public void Dispose() => this.disposed = true;

            public ValueTask DisposeAsync()
            {
                this.disposed = true;
                return ValueTask.CompletedTask;
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawn(out int pid, string path, IntPtr fileActions, IntPtr attributes, string?[] argv, string?[] envp);

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, ref int status, int options);

        [DllImport("libporta_pty", SetLastError = true)]
        private static extern int pty_exited_noreap(int pid);
    }
}
