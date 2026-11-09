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
    using Porta.Pty.Windows;

    /// <summary>
    /// How the Windows spawn path turns an app and an argument array into a CreateProcess command line:
    /// MSVC quoting, PATHEXT resolution, and batch files run through cmd.exe with cmd-safe escaping.
    ///
    /// <para>The formatting functions are pure, so their exact output is asserted on every platform; the
    /// round trip through CommandLineToArgvW, the file search and the batch run need Windows.</para>
    /// </summary>
    [TestClass]
    public class WindowsCommandLineTests
    {
        private const string ComSpec = @"C:\Windows\System32\cmd.exe";

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>Every argument shape, with the command-line form the MSVC rules require for it.</summary>
        public static IEnumerable<object[]> QuotingCases => new[]
        {
            new object[] { string.Empty, "\"\"" },
            new object[] { "plain", "plain" },
            new object[] { "with space", "\"with space\"" },
            new object[] { "tab\there", "\"tab\there\"" },
            new object[] { "a\"b", "\"a\\\"b\"" },
            new object[] { "\"", "\"\\\"\"" },
            new object[] { @"C:\dir\file", @"C:\dir\file" },
            new object[] { @"\", @"\" },
            new object[] { "a\\\"b", "\"a\\\\\\\"b\"" },
            new object[] { "a\\\\\"b", "\"a\\\\\\\\\\\"b\"" },
            new object[] { @"a b\", "\"a b\\\\\"" },
            new object[] { @"a b\\", "\"a b\\\\\\\\\"" },
            new object[] { @"C:\Program Files\", "\"C:\\Program Files\\\\\"" },
            new object[] { "a\\b c", "\"a\\b c\"" },
        };

        [TestMethod]
        [DynamicData(nameof(QuotingCases))]
        public void QuoteArgument_ProducesTheMsvcForm(string arg, string expected)
        {
            WindowsArguments.QuoteArgument(arg).Should().Be(expected);
        }

        /// <summary>The reported case: each argument reaches the program as given, not quoted with doubled quotes.</summary>
        [TestMethod]
        public void Format_LeavesPlainArgumentsAloneAndKeepsEmptyOnes()
        {
            WindowsArguments.Format("/d", "/c", "set").Should().Be("/d /c set");
            WindowsArguments.Format("a", string.Empty, "b").Should().Be("a \"\" b");
        }

        /// <summary>Windows: CommandLineToArgvW reads every quoted shape back to exactly the original argv.</summary>
        [TestMethod]
        public void Format_RoundTripsThroughCommandLineToArgvW()
        {
            RequireWindows();

            string[] args = QuotingCases.Select(c => (string)c[0]).ToArray();
            string commandLine = @"C:\x.exe " + WindowsArguments.Format(args);

            ParseArgv(commandLine).Skip(1).Should().Equal(args, "the command line was: {0}", commandLine);
        }

        [TestMethod]
        public void BuildCommandLine_ForAnExecutable_QuotesTheAppOnlyForWhitespace()
        {
            WindowsArguments.BuildCommandLine(ComSpec, new[] { "/d", "/c", "set" }, verbatim: false, ComSpec)
                .Should().Be((ComSpec, ComSpec + " /d /c set"));
            WindowsArguments.BuildCommandLine(@"C:\Program Files\x.exe", new[] { string.Empty }, verbatim: false, ComSpec)
                .CommandLine.Should().Be("\"C:\\Program Files\\x.exe\" \"\"");
            WindowsArguments.BuildCommandLine(@"C:\x.exe", Array.Empty<string>(), verbatim: false, ComSpec)
                .CommandLine.Should().Be(@"C:\x.exe");
        }

        [TestMethod]
        public void BuildCommandLine_Verbatim_IsPassedOnUnchanged()
        {
            WindowsArguments.BuildCommandLine(ComSpec, new[] { "/c", "echo \"hi\" & ver" }, verbatim: true, ComSpec)
                .CommandLine.Should().Be(ComSpec + " /c echo \"hi\" & ver");

            // A batch file too: a verbatim command line is the caller's to make safe.
            WindowsArguments.BuildCommandLine(@"C:\my dir\run.cmd", new[] { "a&b" }, verbatim: true, ComSpec)
                .Should().Be((@"C:\my dir\run.cmd", "\"C:\\my dir\\run.cmd\" a&b"));
        }

        [TestMethod]
        public void BuildCommandLine_ForABatchFile_RunsItThroughComSpecWithEscapedArguments()
        {
            var (program, commandLine) = WindowsArguments.BuildCommandLine(
                @"C:\my dir\run.cmd", new[] { "plain", "a b", "x&y|z", string.Empty }, verbatim: false, ComSpec);

            program.Should().Be(ComSpec);
            commandLine.Should().Be(ComSpec + " /e:ON /v:OFF /d /s /c \"\"C:\\my dir\\run.cmd\" plain \"a b\" \"x&y|z\" \"\"\"");
        }

        [TestMethod]
        [DataRow("plain", "plain")]
        [DataRow(@"C:\dir\file.txt", @"C:\dir\file.txt")]
        [DataRow("", "\"\"")]
        [DataRow("a b", "\"a b\"")]
        [DataRow("a&b", "\"a&b\"")]
        [DataRow("a|b<c>d^e(f)", "\"a|b<c>d^e(f)\"")]
        [DataRow("a,b;c=d", "\"a,b;c=d\"")]
        [DataRow("a\"b", "\"a\"\"b\"")]
        [DataRow("a\\\"b", "\"a\\\\\"\"b\"")]
        public void QuoteBatchArgument_QuotesEverythingCmdWouldParse(string arg, string expected)
        {
            WindowsArguments.QuoteBatchArgument(arg).Should().Be(expected);
        }

        [TestMethod]
        [DataRow("%PATH%", DisplayName = "percent")]
        [DataRow("100%", DisplayName = "trailing percent")]
        [DataRow("a\nb", DisplayName = "newline")]
        [DataRow("a\rb", DisplayName = "carriage return")]
        [DataRow("a\0b", DisplayName = "NUL")]
        [DataRow(@"C:\dir\", DisplayName = "trailing backslash")]
        public void QuoteBatchArgument_RefusesWhatCmdCannotBeGivenSafely(string arg)
        {
            Action quote = () => WindowsArguments.QuoteBatchArgument(arg);
            quote.Should().Throw<ArgumentException>().WithMessage("*cannot be passed through cmd.exe safely*");
        }

        [TestMethod]
        [DataRow(@"C:\100%\run.bat", DisplayName = "percent")]
        [DataRow("C:\\a\" & calc & \"\\run.cmd", DisplayName = "quote")]
        public void BuildCommandLine_RefusesABatchPathCmdCannotBeGivenSafely(string script)
        {
            Action build = () => WindowsArguments.BuildCommandLine(script, Array.Empty<string>(), verbatim: false, ComSpec);
            build.Should().Throw<ArgumentException>().WithMessage("*cannot be passed through cmd.exe safely*");
        }

        /// <summary>
        /// An existing file is taken as it is before any PATHEXT extension is tried, extension or not:
        /// CreateProcess runs an extensionless executable, and trying tool.exe first would launch a
        /// different program. Plain file lookup, so it runs on every platform.
        /// </summary>
        [TestMethod]
        public void FindWithExtensions_TakesAnExistingExtensionlessFileFirst()
        {
            using var dir = new TempDir();
            string tool = Path.Combine(dir.Path, "tool");
            File.WriteAllText(tool, "pe");
            File.WriteAllText(tool + ".EXE", "other");

            Find(tool, new[] { ".EXE" }).Should().Be(tool);
            Find(Path.Combine(dir.Path, "missing"), new[] { ".EXE" }).Should().BeNull();
        }

        /// <summary>
        /// Windows: an extensionless executable on PATH, outside the child's working directory, resolves
        /// to itself ahead of a same-name tool.exe, and runs. The executable is a copy of cmd.exe under the
        /// name "tool".
        /// </summary>
        [TestMethod]
        public async Task Windows_ExtensionlessExecutableOnPath_ResolvesAndRuns()
        {
            RequireWindows();

            using var tools = new TempDir();
            using var childCwd = new TempDir();
            string tool = Path.Combine(tools.Path, "tool");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), tool);

            // A same-name sibling that is not a program at all: choosing it instead would fail the spawn.
            File.WriteAllText(tool + ".exe", "not an executable");

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = tools.Path,
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            };
            ResolveApp("tool", childCwd.Path, env).Should().Be(tool);

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using var terminal = await Porta.Pty.PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "extensionless",
                    Cols = 120,
                    Rows = 25,
                    Cwd = childCwd.Path,
                    App = "tool",
                    CommandLine = new[] { "/d", "/c", "echo", "extensionless-ok" },
                    Environment = new Dictionary<string, string> { ["PATH"] = tools.Path },
                },
                cts.Token);

            (await ReadUntilAsync(terminal, "extensionless-ok")).Should().Contain("extensionless-ok");
            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue();
        }

        [TestMethod]
        [DataRow(@"C:\x\run.cmd", true)]
        [DataRow(@"C:\x\run.BAT", true)]
        [DataRow(@"C:\x\run.cmd.", true)]
        [DataRow(@"C:\x\run.exe", false)]
        [DataRow(@"C:\x\cmd", false)]
        public void IsBatchFile_ByExtension(string path, bool expected)
        {
            WindowsArguments.IsBatchFile(path).Should().Be(expected);
        }

        /// <summary>Windows: PATHEXT is tried in its own order, and a name with an extension is taken as it is.</summary>
        [TestMethod]
        public void GetAppOnPath_FollowsPathExtOrder()
        {
            RequireWindows();

            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "tool.bat"), "@echo bat");
            File.WriteAllText(Path.Combine(dir.Path, "tool.cmd"), "@echo cmd");

            string Resolve(string app, string pathExt) => ResolveApp(app, dir.Path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = dir.Path,
                ["PATHEXT"] = pathExt,
            });

            Resolve("tool", ".BAT;.CMD").Should().Be(Path.Combine(dir.Path, "tool") + ".BAT");
            Resolve("tool", ".CMD;.BAT").Should().Be(Path.Combine(dir.Path, "tool") + ".CMD");
            Resolve("tool.cmd", ".BAT;.CMD").Should().Be(Path.Combine(dir.Path, "tool.cmd"), "a name with an extension is used as it is");
        }

        /// <summary>
        /// Windows: a path with a directory resolves against the child's working directory, not this
        /// process's; and a bare name is not looked for in the working directory.
        /// </summary>
        [TestMethod]
        public void GetAppOnPath_RelativePathUsesTheChildsDirectory_AndTheCwdIsNotSearched()
        {
            RequireWindows();

            using var childCwd = new TempDir();
            using var emptyPath = new TempDir();
            Directory.CreateDirectory(Path.Combine(childCwd.Path, "sub"));
            File.WriteAllText(Path.Combine(childCwd.Path, "sub", "tool.cmd"), "@echo sub");
            File.WriteAllText(Path.Combine(childCwd.Path, "only-here.cmd"), "@echo here");
            Environment.CurrentDirectory.Should().NotBe(childCwd.Path);

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = emptyPath.Path,
                ["PATHEXT"] = ".CMD",
            };

            ResolveApp(@"sub\tool", childCwd.Path, env).Should().Be(Path.Combine(childCwd.Path, @"sub\tool") + ".CMD");

            // Not found on the search path, so it falls back to the child's directory as before, without
            // an extension found there: the working directory is not searched.
            ResolveApp("only-here", childCwd.Path, env).Should().Be(Path.Combine(childCwd.Path, "only-here"));
        }

        /// <summary>
        /// Windows, end to end: a batch file under a path with spaces receives each argument exactly, cmd's
        /// metacharacters included, and nothing runs that was not asked for.
        /// </summary>
        [TestMethod]
        public async Task Windows_BatchFile_ReceivesArgumentsExactly()
        {
            RequireWindows();

            using var dir = new TempDir("porta pty batch ");
            string script = Path.Combine(dir.Path, "echo args.cmd");
            string marker = Path.Combine(dir.Path, "injected.txt");

            // Each argument is read with %~n inside a quoted SET, where cmd takes metacharacters literally, and
            // printed through delayed expansion, whose result cmd does not parse again.
            var lines = new List<string> { "@echo off" };
            for (int i = 1; i <= 6; i++)
            {
                lines.Add($"set \"A=%~{i}\"");
                lines.Add("setlocal EnableDelayedExpansion");
                lines.Add($"echo(ARG{i}=[!A!]");
                lines.Add("endlocal");
            }

            lines.Add("echo(__END__");
            File.WriteAllText(script, string.Join("\r\n", lines) + "\r\n");

            string[] args =
            {
                "plain",
                "with space",
                "a&b|c<d>e^f(g)",
                string.Empty,
                "x,y;z=w",
                "&echo pwned>injected.txt",
            };

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            using var terminal = await Porta.Pty.PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "batch-args",
                    Cols = 200,
                    Rows = 30,
                    Cwd = dir.Path,
                    App = script,
                    CommandLine = args,
                    Environment = new Dictionary<string, string>(),
                },
                cts.Token);

            string output = await ReadUntilAsync(terminal, "__END__");
            var received = Regex.Matches(output, @"ARG(\d)=\[(.*?)\]\r?\n")
                .ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Groups[2].Value);

            received.Should().HaveCount(args.Length, "the script printed: {0}", output);
            for (int i = 0; i < args.Length; i++)
            {
                received[i + 1].Should().Be(args[i], "argument {0} must arrive as given; the script printed: {1}", i + 1, output);
            }

            // Once cmd has exited: it has run everything it was going to, and it no longer holds the
            // directory as its working directory, which would stop the directory being removed.
            terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("the batch file should run to its end");
            File.Exists(marker).Should().BeFalse("an argument must never run as a command");
        }

        /// <summary>Windows: spawning a batch file with an argument cmd cannot be given safely is refused, not run.</summary>
        [TestMethod]
        public async Task Windows_BatchFile_UnsafeArgument_IsRefused()
        {
            RequireWindows();

            using var dir = new TempDir();
            string script = Path.Combine(dir.Path, "run.cmd");
            File.WriteAllText(script, "@echo ran\r\n");

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            Func<Task> spawn = () => Porta.Pty.PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "batch-refused",
                    Cols = 80,
                    Rows = 25,
                    Cwd = dir.Path,
                    App = script,
                    CommandLine = new[] { "%USERPROFILE%" },
                    Environment = new Dictionary<string, string>(),
                },
                cts.Token);

            await spawn.Should().ThrowAsync<ArgumentException>().WithMessage("*cannot be passed through cmd.exe safely*");
        }

        private static void RequireWindows()
        {
            if (!IsWindows)
            {
                Assert.Inconclusive("Windows command-line parsing and file search");
            }
        }

#pragma warning disable CA1416 // Guarded by RequireWindows in every caller.
        private static string ResolveApp(string app, string cwd, IDictionary<string, string> env) =>
            Porta.Pty.Windows.PtyProvider.GetAppOnPath(app, cwd, env);

        // File lookup only, which works on any platform; the annotation is the class's, not this method's.
        private static string? Find(string candidate, string[] pathExtensions) =>
            Porta.Pty.Windows.PtyProvider.FindWithExtensions(candidate, pathExtensions);
#pragma warning restore CA1416

        private static string[] ParseArgv(string commandLine)
        {
            IntPtr argv = CommandLineToArgvW(commandLine, out int count);
            if (argv == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CommandLineToArgvW failed with {Marshal.GetLastPInvokeError()}");
            }

            try
            {
                var result = new string[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
                }

                return result;
            }
            finally
            {
                LocalFree(argv);
            }
        }

        private static async Task<string> ReadUntilAsync(IPtyConnection terminal, string text)
        {
            var buffer = new byte[4096];
            var output = new StringBuilder();
            var deadline = Stopwatch.StartNew();
            while (deadline.ElapsedMilliseconds < TestTimeoutMs && !output.ToString().Contains(text))
            {
                var read = Task.Run(() => terminal.ReaderStream.Read(buffer, 0, buffer.Length));
                if (await Task.WhenAny(read, Task.Delay(TestTimeoutMs)) != read || await read <= 0)
                {
                    break;
                }

                output.Append(Encoding.UTF8.GetString(buffer, 0, read.Result));
            }

            return output.ToString();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        /// <summary>A scratch directory under the run's temp root, removed afterwards.</summary>
        private sealed class TempDir : IDisposable
        {
            public TempDir(string prefix = "porta-pty-")
            {
                this.Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(this.Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(this.Path, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
