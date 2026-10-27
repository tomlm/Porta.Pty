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
    /// A program that does not exist, or a working folder that does not exist, fails SpawnAsync with the reason.
    ///
    /// <para>The forked child runs chdir and execve and, when either fails, ends with _exit(errno), so the
    /// caller got a running terminal whose program "exited with 2" (ENOENT) and no error at all: measured on
    /// macOS 27 (arm64) on 20.10.2026, /nonexistent/prog and the folder /nonexistent-dir both gave
    /// threw=false, exit code 2, while /usr/bin/true gave 0. A program may itself exit with 2, so the
    /// caller could not tell the two apart.</para>
    ///
    /// <para>The reason is the system's text for ENOENT ("No such file or directory", the same on macOS and
    /// glibc); a missing program is also named, since the spawn error already names the program.</para>
    /// </summary>
    [TestClass]
    public class SpawnFailureTests
    {
        private const string NotFound = "No such file or directory";

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 20_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [TestMethod]
        [DataRow("program", false, DisplayName = "missing program, blocking")]
        [DataRow("program", true, DisplayName = "missing program, non-blocking")]
        [DataRow("folder", false, DisplayName = "missing folder, blocking")]
        [DataRow("folder", true, DisplayName = "missing folder, non-blocking")]
        public async Task Unix_AMissingProgramOrFolder_FailsTheSpawnWithTheReason(string missing, bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("fork and exec in a child is the Unix path; ConPTY reports CreateProcess errors itself");
            }

            var path = "/nonexistent-porta-" + Guid.NewGuid().ToString("N");
            var options = new PtyOptions
            {
                Name = "spawn-failure",
                Cols = 80,
                Rows = 24,
                Cwd = missing == "folder" ? path : Environment.CurrentDirectory,
                App = missing == "program" ? path : "/bin/sh",
                CommandLine = missing == "program" ? Array.Empty<string>() : new[] { "-c", "exit 0" },
                VerbatimCommandLine = true,
                Environment = new Dictionary<string, string>(),
                UseAsyncIo = useAsyncIo,
            };

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            Exception? refused = null;
            IPtyConnection? started = null;
            try
            {
                started = await PtyProvider.SpawnAsync(options, cts.Token);
            }
            catch (Exception error)
            {
                refused = error;
            }

            if (started is not null)
            {
                var exited = started.WaitForExit(TestTimeoutMs);
                var code = started.ExitCode;
                started.Dispose();
                Assert.Fail($"SpawnAsync with the missing {missing} {path} returned a terminal (pid {started.Pid}) instead of failing; "
                    + $"its program {(exited ? $"exited with {code}" : "did not exit")} (useAsyncIo={useAsyncIo}).");
            }

            refused!.Message.Should().Contain(NotFound, "the spawn error should say why it failed (useAsyncIo={0})", useAsyncIo);
            if (missing == "program")
            {
                refused.Message.Should().Contain(path, "the spawn error should name the program it could not start");
            }
        }
    }
}
