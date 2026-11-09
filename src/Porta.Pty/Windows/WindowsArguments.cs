// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;

    /// <summary>
    /// Helper class for formatting windows arguments when passing them to winpty and conpty.
    /// </summary>
    internal static class WindowsArguments
    {
        /// <summary>The ASCII characters a batch argument may hold without being quoted, as Rust's std has it.</summary>
        private const string BatchUnquoted = "#$*+-./:?@\\_";

        /// <summary>
        /// Quotes each argument as the MSVC runtime and CommandLineToArgvW parse it back, and joins them.
        /// </summary>
        /// <param name="args">The command line arguments to format.</param>
        /// <returns>A space-delimited command line that parses back to exactly <paramref name="args"/>.</returns>
        public static string Format(params string[]? args) =>
            string.Join(" ", (args ?? Enumerable.Empty<string>()).Select(QuoteArgument));

        /// <summary>
        /// Joins the arguments together witout modification.
        /// </summary>
        /// <param name="args">The command line arguments to format.</param>
        /// <returns>A space-delimited list of command line arguments.</returns>
        public static string FormatVerbatim(params string[]? args) =>
            string.Join(" ", args ?? Enumerable.Empty<string>());

        /// <summary>
        /// Builds the CreateProcess command line for an app that has already been resolved to a path.
        /// </summary>
        /// <param name="app">The resolved app path.</param>
        /// <param name="args">The arguments, not including the app.</param>
        /// <param name="verbatim">Whether the arguments are joined as given rather than quoted.</param>
        /// <param name="comSpec">The command interpreter that runs a batch file.</param>
        /// <returns>The command line, and the program it starts (the interpreter, for a batch file).</returns>
        /// <remarks>
        /// A .cmd or .bat file is not an executable: CreateProcess hands it to cmd.exe, which parses the
        /// arguments again by its own rules, so quoting them for the MSVC runtime let an argument such as
        /// <c>a&amp;calc</c> run a second command (the "BatBadBut" class, CVE-2024-24576 in Rust's std).
        /// Batch files are therefore run explicitly as <c>%ComSpec% /e:ON /v:OFF /d /s /c ""script" args"</c>,
        /// with each argument escaped for cmd as Rust's std does (<see cref="QuoteBatchArgument"/>), and an
        /// argument that cannot be passed through cmd safely is refused, as is a script path holding <c>%</c> or
        /// a quote. Verbatim command lines are the
        /// caller's to make safe and are passed on unchanged.
        /// </remarks>
        public static (string Program, string CommandLine) BuildCommandLine(string app, string[]? args, bool verbatim, string comSpec)
        {
            args ??= Array.Empty<string>();
            if (!verbatim && IsBatchFile(app))
            {
                if (app.IndexOfAny(new[] { '%', '"' }) >= 0)
                {
                    throw new ArgumentException(
                        $"The batch file path '{app}' contains '%' or '\"', which cmd.exe would expand or end its quoting at; it cannot be passed through cmd.exe safely.",
                        nameof(app));
                }

                var batch = new StringBuilder();
                batch.Append(QuoteProgram(comSpec)).Append(" /e:ON /v:OFF /d /s /c \"\"").Append(app).Append('"');
                foreach (string arg in args)
                {
                    batch.Append(' ').Append(QuoteBatchArgument(arg));
                }

                batch.Append('"');
                return (comSpec, batch.ToString());
            }

            string arguments = verbatim ? FormatVerbatim(args) : Format(args);
            var commandLine = new StringBuilder(QuoteProgram(app));
            if (verbatim ? !string.IsNullOrWhiteSpace(arguments) : args.Length > 0)
            {
                commandLine.Append(' ').Append(arguments);
            }

            return (app, commandLine.ToString());
        }

        /// <summary>
        /// Quotes one argument so that CommandLineToArgvW and the MSVC runtime read back exactly it.
        /// </summary>
        /// <param name="arg">The argument.</param>
        /// <returns>The argument as it goes on the command line.</returns>
        /// <remarks>
        /// Quoted only when it must be: when empty (an unquoted empty argument is dropped) or when it holds
        /// whitespace or a quote. Inside quotes, backslashes are literal except before a quote, so a run of
        /// them is doubled before an embedded quote (which is then escaped) and before the closing quote.
        /// </remarks>
        public static string QuoteArgument(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return arg;
            }

            var quoted = new StringBuilder(arg.Length + 2);
            quoted.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    quoted.Append('\\', (backslashes * 2) + 1);
                }
                else
                {
                    quoted.Append('\\', backslashes);
                }

                quoted.Append(c);
                backslashes = 0;
            }

            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        /// <summary>
        /// Escapes one argument of a batch file for cmd.exe, as Rust's std does, refusing what cmd cannot be
        /// given safely.
        /// </summary>
        /// <param name="arg">The argument.</param>
        /// <returns>The argument as it goes on cmd's command line.</returns>
        /// <exception cref="ArgumentException">
        /// The argument holds <c>%</c> (cmd expands it as a variable even inside quotes), a carriage return or
        /// newline (cmd ends the command there), or a NUL; or it ends in a backslash, which would escape the
        /// closing quote for a script that reads it as <c>"%~1"</c>.
        /// </exception>
        /// <remarks>
        /// Quoted whenever it is empty or holds any ASCII character other than a letter, a digit or one of
        /// <c>#$*+-./:?@\_</c>, or any control character, so that cmd's metacharacters (<c>&amp;|&lt;&gt;^()</c>
        /// and the rest) are inside quotes, where cmd takes them literally. An embedded quote is doubled, with
        /// the backslashes before it doubled too.
        /// </remarks>
        public static string QuoteBatchArgument(string arg)
        {
            if (arg.IndexOfAny(new[] { '%', '\r', '\n', '\0' }) >= 0 || arg.EndsWith('\\'))
            {
                throw new ArgumentException(
                    $"The batch file argument '{arg}' cannot be passed through cmd.exe safely: "
                    + "it contains '%', a newline or a NUL, or it ends with a backslash.",
                    nameof(arg));
            }

            bool quote = arg.Length == 0;
            foreach (char c in arg)
            {
                if ((c < 128 && !char.IsAsciiLetterOrDigit(c) && BatchUnquoted.IndexOf(c) < 0) || char.IsControl(c))
                {
                    quote = true;
                    break;
                }
            }

            if (!quote)
            {
                return arg;
            }

            var quoted = new StringBuilder(arg.Length + 2);
            quoted.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    quoted.Append(c);
                    continue;
                }

                if (c == '"')
                {
                    quoted.Append('\\', backslashes).Append('"');
                }

                quoted.Append(c);
                backslashes = 0;
            }

            quoted.Append('"');
            return quoted.ToString();
        }

        /// <summary>Whether the path names a batch file, which CreateProcess would run through cmd.exe.</summary>
        /// <param name="path">The resolved app path.</param>
        /// <returns>True for a .cmd or .bat file.</returns>
        public static bool IsBatchFile(string path)
        {
            string extension = Path.GetExtension(path.TrimEnd(' ', '.'));
            return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The program as the first token of a command line: quoted when it holds whitespace, never escaped,
        /// since that token is read up to the next quote with no escapes at all.
        /// </summary>
        private static string QuoteProgram(string program) =>
            program.IndexOfAny(new[] { ' ', '\t' }) >= 0 && !program.StartsWith('"') && !program.EndsWith('"')
                ? "\"" + program + "\""
                : program;
    }
}
