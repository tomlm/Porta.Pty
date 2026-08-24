// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Covers <see cref="PtyOptions.UseAsyncIo"/>.
    /// </summary>
    /// <remarks>
    /// The assertion that matters is the thread count one. Everything else here would pass just as
    /// well against the blocking path, because the blocking path is CORRECT -- it is only expensive.
    /// A suite that checked reads still work would have said nothing about whether the option does
    /// anything at all.
    /// </remarks>
    [TestClass]
    public class AsyncIoTests
    {
        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 60_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static string Prompt => IsWindows ? ">" : "$";

        private static byte[] Command(string command) =>
            Encoding.UTF8.GetBytes(command + (IsWindows ? "\r\n" : "\n"));

        private static PtyOptions Shell(string name, bool useAsyncIo)
        {
            return new PtyOptions
            {
                Name = name,
                Cols = 120,
                Rows = 25,
                Cwd = Environment.CurrentDirectory,
                App = IsWindows
                    ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
                    : "/bin/sh",
                CommandLine = Array.Empty<string>(),
                Environment = new Dictionary<string, string>(),
                UseAsyncIo = useAsyncIo,
            };
        }

        [TestMethod]
        public async Task AsyncIo_RoundTripsACommand()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("AsyncRoundTrip", useAsyncIo: true), cts.Token);

            byte[] command = Command("echo ASYNC_MARKER");
            await terminal.WriterStream.WriteAsync(command, 0, command.Length, cts.Token);

            string output = await ReadUntilAsync(terminal, "ASYNC_MARKER", TimeSpan.FromSeconds(15));
            output.Should().Contain("ASYNC_MARKER");

            terminal.Kill();
            terminal.WaitForExit(5000);
        }

        [TestMethod]
        public async Task AsyncIo_ReportsEndOfStream_WhenTheChildExits()
        {
            // The read has to end by itself. A pending read that never completes is the failure mode
            // that turns "no thread held" into "session leaked".
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("AsyncEof", useAsyncIo: true), cts.Token);

            byte[] command = Command("exit");
            await terminal.WriterStream.WriteAsync(command, 0, command.Length, cts.Token);

            var buffer = new byte[4096];
            var stopwatch = Stopwatch.StartNew();
            int last = -1;
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                last = await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (last == 0)
                {
                    break;
                }
            }

            last.Should().Be(0, "a read must reach end of stream once the child is gone");
        }

        [TestMethod]
        public async Task AsyncIo_CancelsAPendingRead()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("AsyncCancel", useAsyncIo: true), cts.Token);

            await ReadUntilAsync(terminal, Prompt, TimeSpan.FromSeconds(5));

            using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var buffer = new byte[4096];

            Func<Task> read = async () => await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);

            await read.Should().ThrowAsync<OperationCanceledException>(
                "a caller polling many idle sessions needs to be able to stop waiting on one");

            terminal.Kill();
            terminal.WaitForExit(5000);
        }

        [TestMethod]
        public async Task AsyncIo_ReportsTheRealExitCode()
        {
            // Unix's shared reaper and Windows's registered process wait both have to preserve the
            // real exit code. Always reporting 0 would satisfy every other test here.

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("AsyncExitCode", useAsyncIo: true), cts.Token);

            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

            // Drain while waiting. An undrained pty eventually stops the child mid-write, and a
            // child stopped mid-write never reaches exit -- which looks exactly like a reaper that
            // never fired.
            var drain = ReadUntilAsync(terminal, "\u0000never\u0000", TimeSpan.FromSeconds(15));

            byte[] command = Command("exit 42");
            await terminal.WriterStream.WriteAsync(command, 0, command.Length, cts.Token);

            var reported = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));
            reported.Should().Be(exited.Task, "the child watch has to raise ProcessExited, not merely notice the exit");
            await drain;

            (await exited.Task).Should().Be(42, "the event must carry the child's real exit code");
            terminal.ExitCode.Should().Be(42);
            terminal.WaitForExit(5000).Should().BeTrue("WaitForExit must be satisfied by the shared reaper too");
        }

        [TestMethod]
        public async Task AsyncIo_ReportsExitCode_ForABlockingConnectionToo()
        {
            // The default path still uses its own watcher thread. Both routes decode the status, so
            // both are checked, or a change to one could silently diverge from the other.
            if (IsWindows)
            {
                Assert.Inconclusive("Unix-only: compares the two Unix waitpid routes.");
                return;
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("BlockingExitCode", useAsyncIo: false), cts.Token);

            var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) => exited.TrySetResult(e.ExitCode);

            var drain = ReadUntilAsync(terminal, "\u0000never\u0000", TimeSpan.FromSeconds(15));

            byte[] command = Command("exit 42");
            terminal.WriterStream.Write(command, 0, command.Length);
            terminal.WriterStream.Flush();

            var reported = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));
            reported.Should().Be(exited.Task);
            await drain;
            (await exited.Task).Should().Be(42);
        }

        [TestMethod]
        public async Task AsyncIo_ReportsExitPromptly_NotOnAPollingInterval()
        {
            // The reason the kernel path exists. The reaper used to ask waitpid every 100ms, and
            // since its callback is what raises ProcessExited, that interval was latency a caller
            // could feel -- roughly 50ms on average, up to 100.
            //
            // The bound is deliberately far below what polling would produce and far above what was
            // measured (3-6ms on macOS), so it fails if the kernel path silently stops being used
            // without being sensitive to a slow agent.
            if (IsWindows)
            {
                Assert.Inconclusive("Unix-only: Windows exit reporting was never on an interval.");
                return;
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);

            // Warm the shared machinery so the first sample is not measuring its creation.
            using (var warm = await PtyProvider.SpawnAsync(ExitImmediately("Warm"), cts.Token))
            {
                warm.WaitForExit(5000);
            }

            var samples = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using IPtyConnection terminal = await PtyProvider.SpawnAsync(ExitImmediately($"Latency{i}"), cts.Token);

                var stopwatch = Stopwatch.StartNew();
                terminal.ProcessExited += (_, _) => exited.TrySetResult();

                // Drained, so the child is never held up writing.
                var drain = ReadUntilAsync(terminal, "\u0000never\u0000", TimeSpan.FromSeconds(5));

                await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
                stopwatch.Stop();

                exited.Task.IsCompletedSuccessfully.Should().BeTrue("the exit has to be reported at all");
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                await drain;
            }

            samples.Sort();
            var median = samples[samples.Count / 2];
            Console.WriteLine($"exit latency ms: {string.Join(", ", samples.ConvertAll(v => v.ToString("F1")))}");

            median.Should().BeLessThan(
                40,
                "exit is reported by the kernel now, not discovered on a 100ms poll");
        }

        private static PtyOptions ExitImmediately(string name)
        {
            var options = Shell(name, useAsyncIo: true);
            options.CommandLine = IsWindows ? new[] { "/c", "exit 7" } : new[] { "-c", "exit 7" };
            options.VerbatimCommandLine = true;
            return options;
        }

        [TestMethod]
        public async Task AsyncIo_RefusesIo_AfterDispose()
        {
            // The disposed flag was written and never read, so a read issued after Dispose still
            // called read(2) -- on a descriptor the connection had closed, and whose NUMBER the
            // process may since have handed to an unrelated file.
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell("DisposedIo", useAsyncIo: true), cts.Token);
            await ReadUntilAsync(terminal, Prompt, TimeSpan.FromSeconds(5));

            var reader = terminal.ReaderStream;
            var writer = terminal.WriterStream;
            terminal.Dispose();

            var buffer = new byte[64];
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => reader.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
                () => writer.WriteAsync(buffer, 0, buffer.Length, CancellationToken.None));
        }

        [TestMethod]
        public async Task AsyncIo_ReapsTheChild_WhenDisposedWithoutWaiting()
        {
            if (IsWindows)
            {
                Assert.Inconclusive("Unix-only: exercises SIGHUP/SIGKILL handling and zombie state.");
                return;
            }

            // Dispose signals the child but does not collect it, and a signalled child is not a
            // reaped one -- it stays a zombie until somebody waits on it. Dropping the reaper
            // registration here meant nothing ever did.
            //
            // Deliberately no Kill/WaitForExit before Dispose. That pair is what the churn test does,
            // and it hides this: it gives the reaper time to collect the status first.
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            // A child that IGNORES SIGHUP. Kill sends SIGHUP, sleeps 100ms, then SIGKILL -- and with
            // an ordinary shell that first signal ends it, so the reaper collects the status during
            // the sleep and the bug cannot show. Ignoring SIGHUP moves the death to the SIGKILL at
            // the very end of Dispose, microseconds before the registration would be dropped.
            var options = new PtyOptions
            {
                Name = "DisposeReap",
                Cols = 120,
                Rows = 25,
                Cwd = Environment.CurrentDirectory,
                App = "/bin/sh",
                CommandLine = new[] { "-c", "trap '' HUP; while :; do sleep 1; done" },
                VerbatimCommandLine = true,
                Environment = new Dictionary<string, string>(),
                UseAsyncIo = true,
            };

            IPtyConnection terminal = await PtyProvider.SpawnAsync(options, cts.Token);
            int pid = terminal.Pid;
            string state = "?";

            try
            {
                await Task.Delay(500, cts.Token);
                ProcessState(pid).Should().NotBeEmpty("precondition: the child is running before dispose");
                terminal.Dispose();

                // Linux Dispose sends only SIGHUP, which this child ignores. Force its death after
                // disposal so this test exercises whether the retained reaper registration collects
                // a child that exits after the connection is already gone.
                if (ProcessState(pid).Length > 0)
                {
                    ForceKill(pid);
                }

                var stopwatch = Stopwatch.StartNew();
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    state = ProcessState(pid);
                    if (state.Length == 0)
                    {
                        break;
                    }

                    await Task.Delay(100, cts.Token);
                }

                // Asserts NOT-A-ZOMBIE rather than gone, because those are two different properties
                // A zombie is what the reaper bug produced, and 'Z' is what this catches. Verified
                // by reintroducing the bug on macOS: state came back "Z".
                state.Should().NotStartWith(
                    "Z",
                    "a disposed connection must not leave its child unreaped; ps reports state '{0}'", state);
            }
            finally
            {
                terminal.Dispose();
                if (ProcessState(pid).Length > 0)
                {
                    ForceKill(pid);
                }
            }
        }

        private static void ForceKill(int pid)
        {
            try
            {
                var startInfo = new ProcessStartInfo("kill");
                startInfo.ArgumentList.Add("-9");
                startInfo.ArgumentList.Add(pid.ToString());
                using var kill = Process.Start(startInfo);
                kill?.WaitForExit(5000);
            }
            catch
            {
                // Best effort cleanup for a test process.
            }
        }

        /// <summary>
        /// The process state ps reports for a pid, or empty when the pid is gone.
        /// </summary>
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

        [TestMethod]
        public async Task AsyncIo_SurvivesChurn_WithoutLeakingDescriptorsThreadsOrCpu()
        {
            // Three leaks are possible here and none of them announces itself. Descriptors: this
            // repo has already seen pty_spawn start failing with ENXIO after enough sessions came
            // and went, which reads as a system limit rather than a leak. Threads: a shared poller
            // and reaper are only shared if nothing per-session sneaks back in. CPU: a registration
            // left in the poll set after its child exits returns from every poll immediately,
            // because POLLHUP is reported whether or not it was asked for, and the loop spins.
            const int Rounds = 40;

            await WarmUpSharedThreadsAsync();
            using var cts = new CancellationTokenSource(TestTimeoutMs);

            int threadsBefore = Process.GetCurrentProcess().Threads.Count;

            for (var i = 0; i < Rounds; i++)
            {
                using IPtyConnection terminal = await PtyProvider.SpawnAsync(Shell($"Churn{i}", useAsyncIo: true), cts.Token);
                await ReadUntilAsync(terminal, Prompt, TimeSpan.FromSeconds(5));
                terminal.Kill();
                terminal.WaitForExit(2000);
            }

            // Idle window only. Starting the clock before the churn measured the spawn work as well,
            // which on a multi-core agent can put processor time above wall time all by itself -- so
            // the bound could fail with no spin at all, and pass while hiding one.
            TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            await Task.Delay(2000, cts.Token);

            wall.Stop();
            int threadsAfter = Process.GetCurrentProcess().Threads.Count;
            TimeSpan cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;

            double cpuRatio = (cpuAfter - cpuBefore).TotalMilliseconds / wall.Elapsed.TotalMilliseconds;
            Console.WriteLine(
                $"churn over {Rounds} sessions: threads {threadsBefore} -> {threadsAfter}, " +
                $"cpu {(cpuAfter - cpuBefore).TotalMilliseconds:F0}ms over {wall.Elapsed.TotalMilliseconds:F0}ms wall (ratio {cpuRatio:F2})");

            (threadsAfter - threadsBefore).Should().BeLessThan(
                5,
                "{0} sessions came and went; nothing should have accumulated", Rounds);

            cpuRatio.Should().BeLessThan(
                1.0,
                "a poll loop spinning on a hung-up descriptor would burn a core continuously");
        }

        [TestMethod]
        public async Task AsyncIo_ThreadCostDoesNotScaleWithSessionCount()
        {
            // The property worth pinning, and the one a comparison against blocking I/O does not
            // state: the cost is CONSTANT process-wide infrastructure rather than a smaller
            // per-session number. Measured at two sizes because a per-session cost of one thread and
            // a constant cost of two are indistinguishable at a single size.
            await WarmUpSharedThreadsAsync();

            IdleCost small = await MeasureIdleCostAsync(6, useAsyncIo: true);
            IdleCost large = await MeasureIdleCostAsync(24, useAsyncIo: true);

            Console.WriteLine(
                $"async idle cost: 6 sessions=+{small.Threads} threads/{small.Workers} workers, " +
                $"24 sessions=+{large.Threads} threads/{large.Workers} workers");

            large.Threads.Should().BeLessThanOrEqualTo(
                small.Threads + 2,
                "quadrupling the session count must not multiply the process-wide I/O/watch infrastructure");
            large.Threads.Should().BeLessThan(
                6,
                "24 idle sessions should cost a handful of threads at most, not one apiece");
            large.Workers.Should().BeLessThanOrEqualTo(
                small.Workers + 2,
                "cached thread-pool threads must not hide one occupied worker per session");
            large.Workers.Should().BeLessThan(
                6,
                "24 pending reads should occupy a handful of workers at most, not one apiece");
        }

        [TestMethod]
        public async Task AsyncIo_CostsFewerThreadsPerIdleSession_ThanBlockingIo()
        {
            const int Sessions = 12;

            await WarmUpSharedThreadsAsync();

            IdleCost blocking = await MeasureIdleCostAsync(Sessions, useAsyncIo: false);
            IdleCost asyncIo = await MeasureIdleCostAsync(Sessions, useAsyncIo: true);

            Console.WriteLine(
                $"idle cost for {Sessions} sessions: " +
                $"blocking=+{blocking.Threads} threads/{blocking.Workers} workers " +
                $"asyncIo=+{asyncIo.Threads} threads/{asyncIo.Workers} workers");

            asyncIo.Threads.Should().BeLessThan(
                blocking.Threads,
                "neither the reads nor the child watch should hold a thread per session");
            asyncIo.Workers.Should().BeLessThan(
                blocking.Workers,
                "IOCP reads should not occupy cached thread-pool workers while idle");
        }

        /// <summary>
        /// Starts the platform's shared async infrastructure before anything is measured.
        /// </summary>
        /// <remarks>
        /// Unix creates its poller and reaper on first use; Windows initializes IOCP and process-wait
        /// infrastructure lazily. Without this the first measurement includes one-time startup cost.
        /// </remarks>
        private static async Task WarmUpSharedThreadsAsync()
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using IPtyConnection warm = await PtyProvider.SpawnAsync(Shell("WarmUp", useAsyncIo: true), cts.Token);
            await ReadUntilAsync(warm, Prompt, TimeSpan.FromSeconds(5));
            warm.Kill();
            warm.WaitForExit(5000);
        }

        private static async Task<IdleCost> MeasureIdleCostAsync(int sessions, bool useAsyncIo)
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var connections = new List<IPtyConnection>();

            try
            {
                // Settle first: the pool grows and shrinks on its own, and a measurement taken while
                // it is still reacting to the previous phase is noise.
                await Task.Delay(1500, cts.Token);
                int before = Process.GetCurrentProcess().Threads.Count;
                ThreadPool.GetAvailableThreads(out int workersBefore, out _);

                for (var i = 0; i < sessions; i++)
                {
                    connections.Add(await PtyProvider.SpawnAsync(Shell($"Idle{i}", useAsyncIo), cts.Token));
                }

                await Task.WhenAll(connections.Select(c => ReadUntilAsync(c, Prompt, TimeSpan.FromSeconds(5))));

                var pending = connections
                    .Select(c => c.ReaderStream.ReadAsync(new byte[256], 0, 256, cts.Token))
                    .ToArray();

                await Task.Delay(2500, cts.Token);
                int during = Process.GetCurrentProcess().Threads.Count;
                ThreadPool.GetAvailableThreads(out int workersDuring, out _);

                foreach (var connection in connections)
                {
                    connection.Kill();
                }

                // The blocking Windows path is not promised to reach EOF on Kill alone, so close its
                // pipes now. Async descriptors must remain open until their pending reads observe
                // child exit; closing first races those reads and turns EOF into EBADF on Linux.
                if (!useAsyncIo && IsWindows)
                {
                    foreach (var connection in connections)
                    {
                        connection.Dispose();
                    }
                }

                Task pendingReads = Task.WhenAll(pending);
                Task completed = await Task.WhenAny(pendingReads, Task.Delay(5000, cts.Token));
                if (completed != pendingReads)
                {
                    throw new TimeoutException("Pending PTY reads did not settle after Kill.");
                }

                try
                {
                    await pendingReads;
                }
                catch (IOException) when (!useAsyncIo && !IsWindows)
                {
                    // A blocking Linux controller reports child exit as EIO rather than EOF.
                }

                return new IdleCost(during - before, Math.Max(0, workersBefore - workersDuring));
            }
            finally
            {
                foreach (var connection in connections)
                {
                    connection.Dispose();
                }
            }
        }

        private readonly record struct IdleCost(int Threads, int Workers);

        private static async Task<string> ReadUntilAsync(IPtyConnection terminal, string needle, TimeSpan timeout)
        {
            var buffer = new byte[4096];
            var output = new StringBuilder();
            var encoding = new UTF8Encoding(false);
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                int read;
                try
                {
                    read = await terminal.ReaderStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (IOException)
                {
                    // End of stream. Reading a pty controller after the child exits gives EIO on
                    // Linux and 0 on macOS, so the default blocking stream THROWS on one platform
                    // and returns cleanly on the other for the same event. NonBlockingPtyStream
                    // normalises EIO to 0; the default path does not, and this is that difference
                    // showing through. Not this branch's to fix.
                    //
                    // This used to be filtered to `ex.HResult == 5`, which never matched anything:
                    // on Unix a FileStream IOException carries COR_E_IO, 0x80131620, for every
                    // errno -- measured, not assumed. There is no portable way to recover the errno
                    // from a managed IOException, so the filter is gone rather than left looking
                    // precise while being inert.
                    //
                    // Swallowing every IOException here is acceptable only because this is a DRAIN:
                    // it reads until a marker appears or the timeout expires, and the assertions
                    // that follow still fail if the marker never arrived. It would not be
                    // acceptable in a helper whose return value something depended on.
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                output.Append(encoding.GetString(buffer, 0, read));
                if (output.ToString().Contains(needle))
                {
                    break;
                }
            }

            return output.ToString();
        }
    }
}
