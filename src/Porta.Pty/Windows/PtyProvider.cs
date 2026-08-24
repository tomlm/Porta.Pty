// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Win32.SafeHandles;
    // global:: is required, not stylistic. This namespace is Porta.Pty.WINDOWS, so an unqualified
    // `using Windows.Win32` binds relative to it and looks for Porta.Pty.Windows.Windows.Win32.
    using global::Windows.Win32;
    using global::Windows.Win32.Foundation;
    using global::Windows.Win32.Storage.FileSystem;
    using global::Windows.Win32.System.Pipes;
    using global::Windows.Win32.System.Threading;
    using static Porta.Pty.Windows.NativeMethods;

    /// <summary>
    /// Provides a pty connection for windows machines using PseudoConsole.
    /// </summary>
    /// <remarks>
    /// Windows-only, and specifically Windows 10 1809 or later: ConPTY does not exist before that, and
    /// <see cref="NativeMethods.IsPseudoConsoleSupported"/> is the runtime gate that says so with a
    /// PlatformNotSupportedException naming the version.
    ///
    /// The VERSION in the annotation is not decoration. CsWin32's generated entry points carry their
    /// own floors (windows5.1.2600 for the job-object calls, windows6.0.6000 for the attribute-list
    /// ones), and a bare "windows" annotation does not satisfy them -- the platform-compatibility
    /// analyzer reported 22 warnings saying exactly that. Stating the real minimum satisfies all of
    /// them truthfully, where suppressing would have hidden a genuine question about which Windows
    /// versions this library supports.
    /// </remarks>
    [SupportedOSPlatform("windows10.0.17763")]
    internal class PtyProvider : IPtyProvider
    {
        /// <inheritdoc/>
        public Task<IPtyConnection> StartTerminalAsync(
            PtyOptions options,
            TraceSource trace,
            CancellationToken cancellationToken)
        {
            if (!IsPseudoConsoleSupported)
            {
                throw new PlatformNotSupportedException(
                    "PseudoConsole (ConPTY) is not supported on this version of Windows. " +
                    "Windows 10 version 1809 (October 2018 Update) or later is required.");
            }

            return StartPseudoConsoleAsync(options, trace, cancellationToken);
        }

        private static string GetAppOnPath(string app, string cwd, IDictionary<string, string> env)
        {
            bool isWow64 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") != null;

            // %WINDIR% was trusted to exist. Where it does not, every Path.Combine below threw
            // ArgumentNullException from the FIRST line of app resolution -- a failure that names
            // neither the app being resolved nor the missing variable. SpecialFolder.Windows asks the
            // OS the same question without going through the environment.
            var windir = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(windir))
            {
                windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            }
            var sysnativePath = Path.Combine(windir, "Sysnative");
            var sysnativePathWithSlash = sysnativePath + Path.DirectorySeparatorChar;
            var system32Path = Path.Combine(windir, "System32");
            var system32PathWithSlash = system32Path + Path.DirectorySeparatorChar;

            try
            {
                // If we have an absolute path then we take it.
                if (Path.IsPathRooted(app))
                {
                    if (isWow64)
                    {
                        // If path is on system32, check sysnative first
                        if (app.StartsWith(system32PathWithSlash, StringComparison.OrdinalIgnoreCase))
                        {
                            var sysnativeApp = Path.Combine(sysnativePath, app.Substring(system32PathWithSlash.Length));
                            if (File.Exists(sysnativeApp))
                            {
                                return sysnativeApp;
                            }
                        }
                    }
                    else if (app.StartsWith(sysnativePathWithSlash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Change Sysnative to System32 if the OS is Windows but NOT WoW64. It's
                        // safe to assume that this was used by accident as Sysnative does not
                        // exist and will break in non-WoW64 environments.
                        return Path.Combine(system32Path, app.Substring(sysnativePathWithSlash.Length));
                    }

                    return app;
                }

                if (Path.GetDirectoryName(app) != string.Empty)
                {
                    // We have a directory and the directory is relative. Make the path absolute
                    // to the current working directory.
                    return Path.Combine(cwd, app);
                }
            }
            catch (ArgumentException)
            {
                throw new ArgumentException($"Invalid terminal app path '{app}'");
            }
            catch (PathTooLongException)
            {
                throw new ArgumentException($"Terminal app path '{app}' is too long");
            }

            string? pathEnvironment = (env != null && env.TryGetValue("PATH", out string? p) ? p : null)
                ?? Environment.GetEnvironmentVariable("PATH");

            if (string.IsNullOrWhiteSpace(pathEnvironment))
            {
                // No PATH environment. Make path absolute to the cwd
                return Path.Combine(cwd, app);
            }

            var paths = new List<string>(pathEnvironment.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
            if (isWow64)
            {
                // On Wow64, if %PATH% contains %WINDIR%\System32 but does not have %WINDIR%\Sysnative, add it before System32.
                var indexOfSystem32 = paths.FindIndex(entry =>
                    string.Equals(entry, system32Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry, system32PathWithSlash, StringComparison.OrdinalIgnoreCase));

                var indexOfSysnative = paths.FindIndex(entry =>
                    string.Equals(entry, sysnativePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry, sysnativePathWithSlash, StringComparison.OrdinalIgnoreCase));

                if (indexOfSystem32 >= 0 && indexOfSysnative == -1)
                {
                    paths.Insert(indexOfSystem32, sysnativePath);
                }
            }

            // We have a simple file name. We get the path variable from the env
            // and try to find the executable on the path.
            foreach (string pathEntry in paths)
            {
                bool isPathEntryRooted;
                try
                {
                    isPathEntryRooted = Path.IsPathRooted(pathEntry);
                }
                catch (ArgumentException)
                {
                    // Ignore invalid entry on %PATH%
                    continue;
                }

                // The path entry is absolute.
                string fullPath = isPathEntryRooted ? Path.Combine(pathEntry, app) : Path.Combine(cwd, pathEntry, app);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }

                var withExtension = fullPath + ".com";
                if (File.Exists(withExtension))
                {
                    return withExtension;
                }

                withExtension = fullPath + ".exe";
                if (File.Exists(withExtension))
                {
                    return withExtension;
                }
            }

            // Not found on PATH. Make path absolute to the cwd
            return Path.Combine(cwd, app);
        }

        private static string GetEnvironmentString(IDictionary<string, string> environment)
        {
            string[] keys = new string[environment.Count];
            environment.Keys.CopyTo(keys, 0);

            string[] values = new string[environment.Count];
            environment.Values.CopyTo(values, 0);

            // Sort both by the keys
            // Windows 2000 requires the environment block to be sorted by the key.
            Array.Sort(keys, values, StringComparer.OrdinalIgnoreCase);

            // Create a list of null terminated "key=val" strings
            var result = new StringBuilder();
            for (int i = 0; i < environment.Count; ++i)
            {
                result.Append(keys[i]);
                result.Append('=');
                result.Append(values[i]);
                result.Append('\0');
            }

            // An extra null at the end indicates end of list.
            result.Append('\0');

            return result.ToString();
        }

        private unsafe Task<IPtyConnection> StartPseudoConsoleAsync(
           PtyOptions options,
           TraceSource trace,
           CancellationToken cancellationToken)
        {
            // Create a Job Object to ensure child processes are killed when the terminal exits.
            // This prevents zombie ConPTY sessions by using JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
            SafeFileHandle jobObjectHandle = JobObject.Create();

            // Declared out here so the catch can close it. Vanara's SafeHPCON had a finalizer, which
            // covered this by accident; PseudoConsole is a plain IDisposable, so the failure path has
            // to say so. Leaking one strands a conhost.exe (or OpenConsole.exe) per failed spawn.
            PseudoConsole? pseudoConsole = null;
            SafeFileHandle? inPipePseudoConsoleSide = null;
            SafeFileHandle? inPipeOurSide = null;
            SafeFileHandle? outPipeOurSide = null;
            SafeFileHandle? outPipePseudoConsoleSide = null;
            SafeFileHandle? processHandle = null;
            SafeFileHandle? mainThreadHandle = null;

            try
            {
                if (options.UseAsyncIo)
                {
                    // CreatePipe hands back synchronous, NON-overlapped handles, and FileStream with
                    // isAsync: true over a non-overlapped handle is invalid -- the very first
                    // overlapped ReadFile would fail. So the async path builds each pipe by hand:
                    // CreateNamedPipe with FILE_FLAG_OVERLAPPED for OUR end, CreateFile for the
                    // ConPTY end. The ConPTY end stays synchronous on purpose; conhost services it
                    // with its own machinery and never sees our overlapped flag. This is the same
                    // construction System.Diagnostics.Process uses for async redirected stdio.
                    (inPipeOurSide, inPipePseudoConsoleSide) = CreateOverlappedPipe(ourSideReads: false);
                    (outPipeOurSide, outPipePseudoConsoleSide) = CreateOverlappedPipe(ourSideReads: true);
                }
                else
                {
                    if (!PInvoke.CreatePipe(out inPipePseudoConsoleSide, out inPipeOurSide, null, 0))
                    {
                        throw new InvalidOperationException("Could not create an anonymous pipe", new Win32Exception());
                    }

                    if (!PInvoke.CreatePipe(out outPipeOurSide, out outPipePseudoConsoleSide, null, 0))
                    {
                        throw new InvalidOperationException("Could not create an anonymous pipe", new Win32Exception());
                    }
                }

                // Either ConPTY implementation, chosen at runtime by PORTAPTY_CONPTY. See PseudoConsole.
                pseudoConsole = PseudoConsole.Create(
                    (short)options.Cols,
                    (short)options.Rows,
                    inPipePseudoConsoleSide.DangerousGetHandle(),
                    outPipePseudoConsoleSide.DangerousGetHandle());

                // IMPORTANT: Close the pseudoconsole side of the pipes after CreatePseudoConsole
                // The pseudoconsole now owns these handles, and keeping them open on our side
                // can cause input/output buffering issues.
                inPipePseudoConsoleSide.Dispose();
                outPipePseudoConsoleSide.Dispose();

                // Prepare the StartupInfoEx structure attached to the ConPTY.
                var startupInfo = new STARTUPINFOEXW();
                startupInfo.InitAttributeListAttachedToConPTY(pseudoConsole.Handle);
                
                try
                {
                    string app = GetAppOnPath(options.App, options.Cwd, options.Environment);
                    string arguments = options.VerbatimCommandLine ?
                        WindowsArguments.FormatVerbatim(options.CommandLine) :
                        WindowsArguments.Format(options.CommandLine);

                    var commandLine = new StringBuilder(app.Length + arguments.Length + 4);
                    bool quoteApp = app.Contains(" ") && !app.StartsWith("\"") && !app.EndsWith("\"");
                    if (quoteApp)
                    {
                        commandLine.Append('"').Append(app).Append('"');
                    }
                    else
                    {
                        commandLine.Append(app);
                    }

                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        commandLine.Append(' ');
                        commandLine.Append(arguments);
                    }

                    trace.TraceInformation(
                        $"Starting terminal process '{app}' with command line {commandLine} "
                        + $"via {pseudoConsole.Implementation}");

                    int pid = 0;
                    bool success = false;
                    
                    {
                        // Was a CER too; see the note on CreatePseudoConsole above.
                        // Build the environment block from the options
                        string environmentBlock = GetEnvironmentString(options.Environment);
                        
                        // Pin the environment string and get a pointer to it
                        var environmentHandle = GCHandle.Alloc(Encoding.Unicode.GetBytes(environmentBlock), GCHandleType.Pinned);
                        try
                        {
                            // Call the Win32 CreateProcess
                            var processInfoRaw = default(PROCESS_INFORMATION);
                            success = CreateProcessW(
                                null,   // lpApplicationName
                                commandLine.ToString(),
                                IntPtr.Zero,   // lpProcessAttributes
                                IntPtr.Zero,   // lpThreadAttributes
                                false,  // bInheritHandles VERY IMPORTANT that this is false
                                // CREATE_SUSPENDED so the child cannot outrun its assignment to the job
                                // object. Without it, CreateProcessW returns with the process ALREADY
                                // RUNNING, and a short-lived command can be gone before
                                // AssignProcessToJobObject executes -- which then fails, because a
                                // process that has exited cannot be assigned to a job. Observed on a
                                // 4-vCPU Windows runner at 96 concurrent spawns: "Failed to assign
                                // process to job object", with the run reporting 95 of 96 delivered
                                // and one that never started at all.
                                //
                                // It is contention-shaped, so it gets worse exactly when a machine is
                                // busy, and it fails the SPAWN rather than losing output -- a caller
                                // sees an exception where it expected a process.
                                //
                                // The thread is resumed immediately after the assignment below. This is
                                // the documented ordering for job assignment, and it is why the main
                                // thread handle is kept rather than closed here.
                                (uint)(PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT
                                    | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT
                                    | PROCESS_CREATION_FLAGS.CREATE_SUSPENDED), // dwCreationFlags
                                environmentHandle.AddrOfPinnedObject(),   // lpEnvironment - pass the environment block
                                options.Cwd,
                                ref startupInfo,
                                out processInfoRaw);

                            if (success)
                            {
                                // PROCESS_INFORMATION carries raw HANDLEs; wrap them so disposal is
                                // ordinary. ownsHandle: true, matching what the Vanara handles did.
                                var hProcessPtr = (IntPtr)processInfoRaw.hProcess.Value;
                                var hThreadPtr = (IntPtr)processInfoRaw.hThread.Value;

                                processHandle = new SafeFileHandle(hProcessPtr, ownsHandle: true);
                                mainThreadHandle = new SafeFileHandle(hThreadPtr, ownsHandle: true);
                                pid = (int)processInfoRaw.dwProcessId;

                                // Assign the process to the job object immediately after creation.
                                // This ensures the process and any children it spawns will be terminated
                                // when the job handle is closed (e.g., when our terminal crashes).
                                //
                                // The child is SUSPENDED here and stays that way until ResumeThread
                                // below, so this can no longer lose a race against a fast command. If
                                // either step throws, the suspended child is killed rather than left
                                // frozen forever: it is not in the job yet, so closing the job would not
                                // reap it, and it would sit in the process table until the box reboots.
                                try
                                {
                                    JobObject.AssignProcess(jobObjectHandle, hProcessPtr);

                                    if (PInvoke.ResumeThread((HANDLE)hThreadPtr) == unchecked((uint)-1))
                                    {
                                        throw new InvalidOperationException(
                                            "Could not resume the terminal process after assigning it to "
                                            + "the job object",
                                            new Win32Exception());
                                    }
                                }
                                catch
                                {
                                    try { PInvoke.TerminateProcess((HANDLE)hProcessPtr, 1); }
                                    catch (Exception) { /* nothing left to try */ }
                                    throw;
                                }
                            }
                        }
                        finally
                        {
                            environmentHandle.Free();
                        }
                    }

                    if (!success)
                    {
                        var errorCode = Marshal.GetLastWin32Error();
                        var exception = new Win32Exception(errorCode);
                        throw new InvalidOperationException($"Could not start terminal process {commandLine.ToString()}: {exception.Message}", exception);
                    }

                    var connectionOptions = new PseudoConsoleConnection.PseudoConsoleConnectionHandles(
                        inPipeOurSide,
                        outPipeOurSide,
                        pseudoConsole,
                        processHandle!,
                        pid,
                        mainThreadHandle!,
                        jobObjectHandle,
                        options.UseAsyncIo);

                    var result = new PseudoConsoleConnection(connectionOptions);
                    AnswerDeviceAttributes(result, pseudoConsole);
                    return Task.FromResult<IPtyConnection>(result);
                }
                finally
                {
                    startupInfo.FreeAttributeList();
                }
            }
            catch
            {
                // If anything fails, make sure to dispose the pseudoconsole and the job object
                pseudoConsole?.Dispose();
                inPipePseudoConsoleSide?.Dispose();
                inPipeOurSide?.Dispose();
                outPipeOurSide?.Dispose();
                outPipePseudoConsoleSide?.Dispose();
                mainThreadHandle?.Dispose();
                processHandle?.Dispose();
                jobObjectHandle?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Creates a pipe whose LOCAL end is opened overlapped, so a FileStream over it can be
        /// isAsync: true and reads are serviced by the I/O completion port with no thread pending.
        /// </summary>
        /// <param name="ourSideReads">True for the child's stdout pipe (we hold the read end);
        /// false for the child's stdin pipe (we hold the write end).</param>
        /// <returns>Our overlapped end, and the synchronous end to hand to CreatePseudoConsole.</returns>
        /// <remarks>
        /// A named pipe with a unique GUID name, because anonymous pipes cannot be overlapped --
        /// CreatePipe has no flags parameter at all. The client connect needs no ConnectNamedPipe
        /// first; a CreateFile against a listening instance succeeds immediately, which is the same
        /// behaviour System.Diagnostics.Process relies on for async redirected stdio.
        /// </remarks>
        private static (SafeFileHandle OurSide, SafeFileHandle PseudoConsoleSide) CreateOverlappedPipe(bool ourSideReads)
        {
            string pipeName = $@"\\.\pipe\porta-pty-{Guid.NewGuid():N}";
            FILE_FLAGS_AND_ATTRIBUTES access = ourSideReads
                ? FILE_FLAGS_AND_ATTRIBUTES.PIPE_ACCESS_INBOUND
                : FILE_FLAGS_AND_ATTRIBUTES.PIPE_ACCESS_OUTBOUND;

            SafeFileHandle ourSide = PInvoke.CreateNamedPipe(
                pipeName,
                access
                    | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED
                    // The GUID makes a collision negligible; this flag is what makes a SQUATTER
                    // loud. If anything already owns the name, creation fails here rather than
                    // quietly sharing a pipe with a stranger.
                    | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_FIRST_PIPE_INSTANCE,
                NAMED_PIPE_MODE.PIPE_TYPE_BYTE
                    | NAMED_PIPE_MODE.PIPE_READMODE_BYTE
                    | NAMED_PIPE_MODE.PIPE_WAIT
                    | NAMED_PIPE_MODE.PIPE_REJECT_REMOTE_CLIENTS,
                1,
                // 4096 each way, NOT zero. Zero is legal and means "no quota", which for an
                // overlapped writer makes the first write wait for a reader rather than complete
                // immediately -- the opposite of what this pipe is for. 4096 is the size
                // CreatePipeEx uses for the same reason.
                4096,
                4096,
                0,
                null);

            if (ourSide.IsInvalid)
            {
                int errorCode = Marshal.GetLastWin32Error();
                ourSide.Dispose();
                throw new InvalidOperationException(
                    "Could not create an overlapped named pipe",
                    new Win32Exception(errorCode));
            }

            try
            {
                SafeFileHandle pseudoConsoleSide = PInvoke.CreateFile(
                    pipeName,
                    (uint)(ourSideReads
                        ? GENERIC_ACCESS_RIGHTS.GENERIC_WRITE
                        : GENERIC_ACCESS_RIGHTS.GENERIC_READ),
                    FILE_SHARE_MODE.FILE_SHARE_NONE,
                    null,
                    FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                    FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                    null!);

                if (pseudoConsoleSide.IsInvalid)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    pseudoConsoleSide.Dispose();
                    throw new InvalidOperationException(
                        "Could not open the ConPTY side of an overlapped named pipe",
                        new Win32Exception(errorCode));
                }

                return (ourSide, pseudoConsoleSide);
            }
            catch
            {
                ourSide.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Answers the pseudoconsole's startup question, so that consumers do not have to.
        ///
        /// <para>ConPTY's <c>VtIo::StartIfNeeded</c> emits, on startup, a cursor-position report
        /// request, a Primary Device Attributes query (<c>ESC[c</c> — "what terminal are you?"), and
        /// then calls <c>WaitUntilDA1(3000)</c>: it BLOCKS for up to three seconds waiting for the DA1
        /// response, and on timeout gives up with <c>StartupFailed</c> and continues anyway.</para>
        ///
        /// <para>A consumer that only READS a PTY never answers, so it pays that three seconds on every
        /// pseudoconsole. Measured here, sequential spawns: <c>[3016,3012,3011,3013,3019]</c> without
        /// the answer, <c>[15,9,9,8,8]</c> with it. That is the entire difference between out-of-band
        /// ConPTY looking unusable and it being faster than in-box.</para>
        ///
        /// <para>Sent UNCONDITIONALLY and immediately, not in response to observing <c>ESC[c</c>.
        /// Reacting to the query was measured and does not work — it races the handshake and usually
        /// arrives after <c>WaitUntilDA1</c> has already given up.</para>
        ///
        /// <para>Only on the out-of-band path, because only it asks. In-box ConPTY emits no query, so
        /// the same bytes would not be consumed by a handshake and would reach the child as keyboard
        /// input instead.</para>
        ///
        /// <para>Never fatal. If this write fails the terminal still works; it simply pays the timeout
        /// it would have paid anyway.</para>
        /// </summary>
        private static void AnswerDeviceAttributes(IPtyConnection connection, PseudoConsole pseudoConsole)
        {
            // Whether it ASKS, not whether it was selected: conpty.dll with no OpenConsole.exe beside
            // it falls back to conhost and asks nothing, and an answer to a question nobody asked is
            // just bytes on the child's stdin. PseudoConsoleConnection hides the query on the same
            // condition -- see AsksStartupDeviceAttributes.
            if (!pseudoConsole.AsksStartupDeviceAttributes)
            {
                return;
            }

            // "VT100 with Advanced Video Option". What it claims matters far less than that it is a
            // well-formed DA1 response and that it arrives.
            var reply = Encoding.ASCII.GetBytes("\u001b[?1;2c");

            try
            {
                connection.WriterStream.Write(reply, 0, reply.Length);
                connection.WriterStream.Flush();
            }
            catch (Exception)
            {
                // The terminal still works; it just pays the three seconds.
            }
        }

        /// <summary>
        /// Hand-written rather than generated, and it has to be.
        ///
        /// <para>CsWin32 projects CreateProcess with a <c>STARTUPINFOW*</c>, and this call needs a
        /// <c>STARTUPINFOEXW</c> — the extended form whose first field IS a STARTUPINFOW, which is what
        /// makes EXTENDED_STARTUPINFO_PRESENT work. Passing the extended struct through the generated
        /// signature would mean casting the pointer at every call site, which is the same unsafe act
        /// with less of a place to explain itself.</para>
        ///
        /// <para><c>lpCommandLine</c> is deliberately a <c>string</c>: CreateProcessW may WRITE to that
        /// buffer, and the marshaller hands it a copy. Passing a pinned managed string directly would
        /// let Windows mutate an interned literal.</para>
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEXW lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);
    }
}
