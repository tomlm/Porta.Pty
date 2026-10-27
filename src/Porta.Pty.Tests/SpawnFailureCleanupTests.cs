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
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A spawn that fails, because the program or the working folder is missing, leaves nothing behind in the caller's
    /// process: no zombie child and no open descriptor.
    ///
    /// <para>When the child reports a failed chdir or exec, pty_spawn collects the child itself, since the caller never
    /// learns its pid and nobody else will, and closes the controller that forkpty opened, since the caller never gets
    /// it. Without the first, every failed spawn left a zombie for the life of the process; without the second, an open
    /// pseudo terminal controller. Neither showed in SpawnFailureTests, which reads only the error.</para>
    ///
    /// <para>Descriptors are counted in the whole process, after a first failed spawn so that anything the library
    /// opens once is not counted, and the tests of this assembly run one at a time. A zombie is a child of this
    /// process in state Z that is still one a second later, so a program another test just ended and that its reaper
    /// is about to collect does not count.</para>
    /// </summary>
    [TestClass]
    public class SpawnFailureCleanupTests
    {
        private const int FailedSpawns = 16;

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 20_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Unix_AFailedSpawn_LeavesNoZombieAndNoDescriptor(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("fork and exec in a child is the Unix path; ConPTY reports CreateProcess errors itself");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            await FailToSpawn("program", useAsyncIo, cts.Token);
            var zombiesBefore = Zombies();
            var descriptorsBefore = OpenDescriptors();

            for (var i = 0; i < FailedSpawns; i++)
            {
                await FailToSpawn(i % 2 == 0 ? "program" : "folder", useAsyncIo, cts.Token);
            }

            var descriptorsAfter = OpenDescriptors();
            var newZombies = Zombies().Except(zombiesBefore).ToList();
            if (newZombies.Count > 0)
            {
                await Task.Delay(1000, cts.Token);
                newZombies = newZombies.Intersect(Zombies()).ToList();
            }

            newZombies.Should().BeEmpty(
                "{0} failed spawns should leave no child of this process uncollected (useAsyncIo={1})", FailedSpawns, useAsyncIo);
            descriptorsAfter.Should().BeLessThanOrEqualTo(
                descriptorsBefore,
                "{0} failed spawns should leave no descriptor open in this process (useAsyncIo={1})", FailedSpawns, useAsyncIo);
        }

        private static async Task FailToSpawn(string missing, bool useAsyncIo, CancellationToken cancellationToken)
        {
            var path = "/nonexistent-porta-" + Guid.NewGuid().ToString("N");
            var options = new PtyOptions
            {
                Name = "spawn-failure-cleanup",
                Cols = 80,
                Rows = 24,
                Cwd = missing == "folder" ? path : Environment.CurrentDirectory,
                App = missing == "program" ? path : "/bin/sh",
                CommandLine = missing == "program" ? Array.Empty<string>() : new[] { "-c", "exit 0" },
                VerbatimCommandLine = true,
                Environment = new Dictionary<string, string>(),
                UseAsyncIo = useAsyncIo,
            };

            IPtyConnection? started = null;
            try
            {
                started = await PtyProvider.SpawnAsync(options, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
            }

            if (started is not null)
            {
                started.Dispose();
                Assert.Fail($"SpawnAsync with the missing {missing} {path} returned a terminal (pid {started.Pid}) instead of failing (useAsyncIo={useAsyncIo}).");
            }
        }

        /// <summary>The descriptors open in this process: /proc/self/fd on Linux, /dev/fd on macOS.</summary>
        private static int OpenDescriptors() =>
            Directory.GetFileSystemEntries(Directory.Exists("/proc/self/fd") ? "/proc/self/fd" : "/dev/fd").Length;

        /// <summary>The pids of this process's children in state Z: from /proc on Linux, from ps on macOS.</summary>
        private static HashSet<int> Zombies()
        {
            var self = Environment.ProcessId;
            var zombies = new HashSet<int>();
            if (Directory.Exists("/proc/self"))
            {
                foreach (var folder in Directory.GetDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(folder), out var pid))
                    {
                        continue;
                    }

                    string stat;
                    try
                    {
                        stat = File.ReadAllText(Path.Combine(folder, "stat"));
                    }
                    catch (IOException)
                    {
                        continue;
                    }

                    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (fields[0] == "Z" && int.Parse(fields[1]) == self)
                    {
                        zombies.Add(pid);
                    }
                }

                return zombies;
            }

            var start = new ProcessStartInfo("/bin/ps", "-A -o pid= -o ppid= -o stat=") { RedirectStandardOutput = true };
            using var ps = Process.Start(start)!;
            var lines = ps.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            ps.WaitForExit();
            foreach (var line in lines)
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 3 && fields[2].StartsWith('Z') && int.Parse(fields[1]) == self)
                {
                    zombies.Add(int.Parse(fields[0]));
                }
            }

            return zombies;
        }
    }
}
