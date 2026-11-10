// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    [TestClass]
    [DoNotParallelize]
    public class MacStartupTests
    {
        [TestMethod]
        public async Task EsrchWithUnreadOutput_ReturnsConnectionAndReportsActualExitOnce()
        {
            if (!OperatingSystem.IsMacOS())
            {
                Assert.Inconclusive("macOS can defer child exit until terminal output is drained");
            }

            for (int iteration = 0; iteration < 5; iteration++)
            {
                string ready = Path.Combine(Path.GetTempPath(), $"porta-startup-{Guid.NewGuid():N}");
                int pid = 0;
                int watches = 0;
                IPtyConnection? terminal = null;
                var exits = new ConcurrentQueue<int>();
                var provider = new Mac.PtyProvider { BeforeConnection = (_, child) => pid = child };
                Unix.PtyReaper.Instance.WatchError = p =>
                {
                    if (p != pid)
                    {
                        return null;
                    }

                    Interlocked.Increment(ref watches);
                    SpinWait.SpinUntil(() => File.Exists(ready), 5000).Should().BeTrue();
                    return 3; // ESRCH, including the retry after waitpid returns zero.
                };
                Unix.PtyReaper.Instance.HoldSweep = p => p == pid;
                try
                {
                    terminal = await provider.StartTerminalAsync(
                        Options($"printf 'startup-output'; touch '{ready}'; exit 23"),
                        new System.Diagnostics.TraceSource("startup-test"), CancellationToken.None);
                    terminal.ProcessExited += (_, e) => exits.Enqueue(e.ExitCode);
                    watches.Should().Be(2, "both watch attempts fail while the child has unread output");
                    using var reader = new StreamReader(terminal.ReaderStream);
                    (await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)))
                        .Should().Be("startup-output");
                    Unix.PtyReaper.Instance.HoldSweep = null;
                    (await Task.Run(() => terminal.WaitForExit(10000))).Should().BeTrue();
                    terminal.ExitCode.Should().Be(23);
                    int sweep = Unix.PtyReaper.Instance.Sweeps;
                    (await WaitUntilAsync(() => Unix.PtyReaper.Instance.Sweeps >= sweep + 2)).Should().BeTrue();
                    exits.Should().Equal(23);
                }
                finally
                {
                    Unix.PtyReaper.Instance.WatchError = null;
                    Unix.PtyReaper.Instance.HoldSweep = null;
                    terminal?.Dispose();
                    // If construction failed, the provider owns cleanup; never signal a collected pid.
                    File.Delete(ready);
                }
            }
        }

        [TestMethod]
        [DataRow(3, 24)]
        [DataRow(24, 3)]
        [DataRow(24, 24)]
        public void UnrelatedWatchErrors_StillThrowWithRetryError(int firstError, int retryError)
        {
            RequireUnixReaper();

            const int pid = int.MaxValue;
            int watches = 0;
            int attempts = 0;
            var reaper = Unix.PtyReaper.Instance;
            reaper.HoldSweep = p => p == pid;
            reaper.WatchError = p => p == pid ? (++watches == 1 ? firstError : retryError) : null;
            try
            {
                Action register = () => reaper.Register(pid, (int child, ref int status) =>
                {
                    attempts++;
                    Marshal.SetLastPInvokeError(10); // waitpid must not erase the watch error.
                    return 0;
                }, _ => Assert.Fail("a still-running child has no exit status"));
                register.Should().Throw<InvalidOperationException>().WithMessage($"*errno {retryError}*");
                watches.Should().Be(2);
                attempts.Should().Be(1);
            }
            finally
            {
                reaper.Unregister(pid);
                reaper.WatchError = null;
                reaper.HoldSweep = null;
            }
        }

        [TestMethod]
        public void FailedRearm_AfterConcurrentRemoval_DoesNotFailStartup()
        {
            RequireUnixReaper();

            const int pid = int.MaxValue;
            var reaper = Unix.PtyReaper.Instance;
            reaper.HoldSweep = p => p == pid;
            reaper.WatchError = p => p == pid ? 24 : null;
            try
            {
                Action register = () => reaper.Register(pid, (int child, ref int status) =>
                {
                    reaper.Unregister(child);
                    return 0;
                }, _ => Assert.Fail("removed entry must not report an exit"));
                register.Should().NotThrow("another collector already removed this registration");
            }
            finally
            {
                reaper.Unregister(pid);
                reaper.WatchError = null;
                reaper.HoldSweep = null;
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void FailedWatch_CollectedOrDeferred_DoesNotRearm(bool deferred)
        {
            RequireUnixReaper();

            const int pid = int.MaxValue;
            int watches = 0;
            int reports = 0;
            var reaper = Unix.PtyReaper.Instance;
            reaper.HoldSweep = p => p == pid;
            reaper.WatchError = p =>
            {
                if (p != pid)
                {
                    return null;
                }

                watches++;
                return 3;
            };
            try
            {
                reaper.Register(pid, (int child, ref int status) =>
                {
                    status = 23 << 8;
                    return deferred ? Unix.PtyReaper.Deferred : child;
                }, status =>
                {
                    status.Should().Be(23 << 8);
                    reports++;
                });
                watches.Should().Be(1);
                reports.Should().Be(deferred ? 0 : 1);
            }
            finally
            {
                reaper.Unregister(pid);
                reaper.WatchError = null;
                reaper.HoldSweep = null;
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task UnexpectedStartupFailure_WithUnreadOutput_ClosesBeforeWaiting(bool interruptWait)
        {
            if (!OperatingSystem.IsMacOS())
            {
                Assert.Inconclusive("macOS terminal close unblocks exit with unread output");
            }

            string ready = Path.Combine(Path.GetTempPath(), $"porta-cleanup-{Guid.NewGuid():N}");
            int pid = 0;
            int fd = -1;
            var expected = new InvalidOperationException("injected startup failure");
            var provider = new Mac.PtyProvider
            {
                BeforeConnection = (controller, child) =>
                {
                    fd = controller;
                    pid = child;
                    SpinWait.SpinUntil(() => File.Exists(ready), 5000).Should().BeTrue();
                    throw expected;
                },
            };
            int waits = 0;
            bool controllerClosed = false;
            provider.WaitForCleanup = (int child, ref int status, int options) =>
            {
                waits++;
                int descriptorState = fcntl(fd, 1);
                int descriptorError = Marshal.GetLastPInvokeError();
                controllerClosed = descriptorState == -1 && descriptorError == 9;
                controllerClosed.Should().BeTrue("the controller must be closed before cleanup waits");
                options.Should().Be(0, "production cleanup requests a blocking wait");
                if (interruptWait && waits == 1)
                {
                    Marshal.SetLastPInvokeError(4);
                    return -1;
                }

                // Exercise real collection without allowing a regression to block the test runner.
                return WaitForChildBounded(child, ref status);
            };
            try
            {
                Func<Task> start = () => provider.StartTerminalAsync(
                    Options($"printf 'unread-output'; touch '{ready}'; exit 31"),
                    new System.Diagnostics.TraceSource("startup-test"), CancellationToken.None);
                (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(expected);
                waits.Should().Be(interruptWait ? 2 : 1);
                int closed = fcntl(fd, 1);
                int closeError = Marshal.GetLastPInvokeError();
                closed.Should().Be(-1, "cleanup owns and closes the terminal descriptor");
                closeError.Should().Be(9); // EBADF
                int status = 0;
                int collected = waitpid(pid, ref status, 1);
                int waitError = Marshal.GetLastPInvokeError();
                collected.Should().Be(-1, "cleanup collected its child");
                waitError.Should().Be(10); // ECHILD
            }
            finally
            {
                provider.BeforeConnection = null;
                // This test's controller is still ours only when cleanup never closed it.
                if (fd >= 0 && !controllerClosed)
                {
                    close(fd);
                }

                if (pid > 0)
                {
                    int status = 0;
                    if (waitpid(pid, ref status, 1) == 0)
                    {
                        kill(pid, 9);
                        WaitForChildBounded(pid, ref status);
                    }
                }

                File.Delete(ready);
            }
        }

        private static int WaitForChildBounded(int pid, ref int status)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                int result = waitpid(pid, ref status, 1);
                int error = Marshal.GetLastPInvokeError();
                if (result > 0 || (result < 0 && error != 4))
                {
                    Marshal.SetLastPInvokeError(error);
                    return result;
                }

                clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the test's child must be collected within the bound");
                Thread.Sleep(10);
            }
        }

        private static void RequireUnixReaper()
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("Unix reaper registration");
            }

            if (!Unix.PtyReaper.Instance.IsSupported)
            {
                Assert.Inconclusive("this kernel cannot report child exits");
            }
        }

        private static async Task<bool> WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while (!condition())
                {
                    await Task.Delay(20, timeout.Token);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static PtyOptions Options(string command) =>
            new PtyOptions
            {
                Name = "mac-startup",
                Cols = 80,
                Rows = 24,
                Cwd = Environment.CurrentDirectory,
                App = "/bin/sh",
                CommandLine = new[] { "-c", command },
                VerbatimCommandLine = true,
                Environment = new Dictionary<string, string>(),
                UseAsyncIo = true,
            };

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int fcntl(int fd, int command);

        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, ref int status, int options);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);
    }
}
