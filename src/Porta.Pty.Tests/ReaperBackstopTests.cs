// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The non-blocking reaper's backstop: macOS sometimes never delivers the kqueue NOTE_EXIT of a child
    /// that has exited (measured through this library at about 1 in 1,900 exits), which left the child a
    /// zombie and WaitForExit and ProcessExited never completing. A periodic no-hang reap of every watched
    /// child now delivers such an exit. The loss is not reproducible on demand, so the reaper's test seam
    /// drops the one child's event, and only the backstop can deliver its exit.
    /// </summary>
    [TestClass]
    public class ReaperBackstopTests
    {
        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [TestMethod]
        public async Task AsyncIo_ExitEventLost_IsDeliveredByTheBackstopOnce()
        {
            RequireUnixReaper();

            using var release = new ReleaseFile();
            using var terminal = await SpawnAsync(release.Gate + "exit 5");
            var exits = new ConcurrentQueue<int>();
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) =>
            {
                exits.Enqueue(e.ExitCode);
                exited.TrySetResult();
            };

            int pid = terminal.Pid;
            int dropped = 0;
            Unix.PtyReaper.Instance.LoseExitEvent = p =>
            {
                if (p != pid)
                {
                    return false;
                }

                Interlocked.Increment(ref dropped);
                return true;
            };

            try
            {
                // Released only now, with the handler and the seam both in place; the assertion on the
                // dropped event below makes sure the kernel's event really was the one taken away.
                release.Open();
                (await Task.WhenAny(exited.Task, Task.Delay(TestTimeoutMs))).Should().BeSameAs(
                    exited.Task, "with its exit event lost, the backstop must still deliver the exit of child {0}", pid);
                terminal.WaitForExit(0).Should().BeTrue();
                terminal.ExitCode.Should().Be(5);
                // The event can be drained after the delivery: a sweep that is due runs first in the same pass,
                // and the seam sees the event only when the drain reaches it. Every event for this child is
                // dropped, so the delivery above came from the sweep either way.
                (await WaitUntilAsync(() => Volatile.Read(ref dropped) > 0)).Should().BeTrue(
                    "the child's exit event must have arrived and been dropped, or the test proves nothing");

                // Two more sweeps, observed, each a chance to report it again, which they must not take.
                (await WaitForSweepsAsync(2)).Should().BeTrue("the reaper should keep sweeping");
                exits.Should().Equal(new[] { 5 }, "the exit is delivered exactly once");
            }
            finally
            {
                Unix.PtyReaper.Instance.LoseExitEvent = null;
            }
        }

        /// <summary>Control: with no event lost, the exit still arrives once, though the sweep looks too.</summary>
        [TestMethod]
        public async Task AsyncIo_ExitEventDelivered_IsReportedOnce()
        {
            RequireUnixReaper();

            using var release = new ReleaseFile();
            using var terminal = await SpawnAsync(release.Gate + "exit 6");
            var exits = new ConcurrentQueue<int>();
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.ProcessExited += (_, e) =>
            {
                exits.Enqueue(e.ExitCode);
                exited.TrySetResult();
            };

            release.Open();
            (await Task.WhenAny(exited.Task, Task.Delay(TestTimeoutMs))).Should().BeSameAs(exited.Task);
            (await WaitForSweepsAsync(2)).Should().BeTrue("the reaper should keep sweeping");
            exits.Should().Equal(new[] { 6 }, "the event and the sweep together deliver the exit exactly once");
        }

        /// <summary>Waits, bounded, until <paramref name="count"/> more sweeps have completed.</summary>
        private static Task<bool> WaitForSweepsAsync(int count)
        {
            int target = Unix.PtyReaper.Instance.Sweeps + count;
            return WaitUntilAsync(() => Unix.PtyReaper.Instance.Sweeps >= target);
        }

        /// <summary>Waits for a condition; the bound stops a hang, it is not the assertion.</summary>
        private static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > TestTimeoutMs)
                {
                    return false;
                }

                await Task.Delay(20);
            }

            return true;
        }

        private static void RequireUnixReaper()
        {
            if (IsWindows)
            {
                Assert.Inconclusive("the reaper is the Unix non-blocking path");
            }

            if (!Unix.PtyReaper.Instance.IsSupported)
            {
                Assert.Inconclusive("this kernel cannot report child exits");
            }
        }

        private static async Task<IPtyConnection> SpawnAsync(string command)
        {
            using var cts = new CancellationTokenSource(TestTimeoutMs);
            return await PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "reaper-backstop",
                    Cols = 80,
                    Rows = 24,
                    Cwd = Environment.CurrentDirectory,
                    App = "/bin/sh",
                    CommandLine = new[] { "-c", command },
                    VerbatimCommandLine = true,
                    Environment = new Dictionary<string, string>(),
                    UseAsyncIo = true,
                },
                cts.Token);
        }

        /// <summary>A file the child waits for before it exits, so the test decides when that is.</summary>
        private sealed class ReleaseFile : IDisposable
        {
            private readonly string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"porta-pty-release-{Guid.NewGuid():N}");

            /// <summary>Gets the shell that holds the child until the file exists.</summary>
            public string Gate => $"while [ ! -e '{this.path}' ]; do /bin/sleep 0.02; done; ";

            public void Open() => System.IO.File.WriteAllText(this.path, string.Empty);

            public void Dispose() => System.IO.File.Delete(this.path);
        }
    }
}
