// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.ComponentModel;
    using System.Runtime.InteropServices;
    using System.Runtime.Versioning;
    using Microsoft.Win32.SafeHandles;
    // global:: is required, not stylistic. This namespace is Porta.Pty.WINDOWS, so an unqualified
    // `using Windows.Win32` binds relative to it and looks for Porta.Pty.Windows.Windows.Win32.
    // The alternative is moving the usings outside the namespace block, which is what Sylinko's fork
    // does by using file-scoped namespaces; keeping them inside preserves this file's upstream shape.
    using global::Windows.Win32;
    using global::Windows.Win32.Foundation;
    using global::Windows.Win32.System.JobObjects;

    /// <summary>
    /// Provides Job Object functionality to ensure child processes are terminated
    /// when the parent process exits, preventing zombie ConPTY sessions.
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
    internal static class JobObject
    {
        /// <summary>
        /// Creates a Job Object configured to kill all assigned processes when the job handle is closed.
        /// This ensures that if the terminal process crashes or exits unexpectedly, all child processes
        /// (including the console host and any PTY-backed console apps) are automatically terminated.
        /// </summary>
        /// <returns>A safe handle to the created job object.</returns>
        public static unsafe SafeFileHandle Create()
        {
            // Anonymous: no name, so nothing else can open it by name.
            SafeFileHandle jobHandle = PInvoke.CreateJobObject(null, (string?)null);
            if (jobHandle.IsInvalid)
            {
                throw new InvalidOperationException("Failed to create job object", new Win32Exception());
            }

            try
            {
                var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };

                // Vanara's wrapper threw on failure; CsWin32's returns BOOL, so the check is now ours
                // to make. Dropping it would be worse than it sounds: the job would exist but not kill
                // on close, and every crashed terminal would strand its console host.
                if (!PInvoke.SetInformationJobObject(
                        jobHandle,
                        JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                        &extendedInfo,
                        (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    throw new InvalidOperationException(
                        "Failed to configure the job object to kill its processes on close",
                        new Win32Exception());
                }

                return jobHandle;
            }
            catch
            {
                jobHandle.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Assigns a process to a job object.
        /// </summary>
        /// <param name="jobHandle">The job object handle.</param>
        /// <param name="processHandle">The process handle to assign.</param>
        public static void AssignProcess(SafeFileHandle jobHandle, IntPtr processHandle)
        {
            if (jobHandle == null || jobHandle.IsInvalid || jobHandle.IsClosed)
            {
                throw new ArgumentException("Invalid job object handle", nameof(jobHandle));
            }

            if (processHandle == IntPtr.Zero)
            {
                throw new ArgumentException("Invalid process handle", nameof(processHandle));
            }

            if (!PInvoke.AssignProcessToJobObject((HANDLE)jobHandle.DangerousGetHandle(), (HANDLE)processHandle))
            {
                throw new InvalidOperationException(
                    "Failed to assign process to job object",
                    new Win32Exception());
            }
        }

        /// <summary>
        /// Terminates every process in the job object.
        /// </summary>
        /// <param name="jobHandle">The job object handle.</param>
        /// <returns>True if the job was terminated, false if the handle is gone or the call failed.</returns>
        public static bool Terminate(SafeFileHandle jobHandle)
        {
            // Held for the call: a Dispose on another thread could otherwise close the handle between
            // reading it and using it, and a recycled value would terminate some other terminal's job.
            bool added = false;
            try
            {
                jobHandle.DangerousAddRef(ref added);
                return !jobHandle.IsInvalid && PInvoke.TerminateJobObject((HANDLE)jobHandle.DangerousGetHandle(), 1);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            finally
            {
                if (added)
                {
                    jobHandle.DangerousRelease();
                }
            }
        }
    }
}
