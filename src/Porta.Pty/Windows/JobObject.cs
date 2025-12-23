// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using Vanara.PInvoke;
    using static Vanara.PInvoke.Kernel32;

    /// <summary>
    /// Provides Job Object functionality to ensure child processes are terminated
    /// when the parent process exits, preventing zombie ConPTY sessions.
    /// </summary>
    internal static class JobObject
    {
        /// <summary>
        /// Creates a Job Object configured to kill all assigned processes when the job handle is closed.
        /// This ensures that if the terminal process crashes or exits unexpectedly, all child processes
        /// (including conhost.exe and any PTY-backed console apps) are automatically terminated.
        /// </summary>
        /// <returns>A safe handle to the created job object.</returns>
        public static SafeHJOB Create()
        {
            // Create an anonymous job object
            SafeHJOB jobHandle = CreateJobObject(null, null);
            if (jobHandle.IsInvalid)
            {
                throw new InvalidOperationException(
                    "Failed to create job object",
                    new System.ComponentModel.Win32Exception());
            }

            try
            {
                // Configure the job to kill all processes when the job handle is closed
                var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOBOBJECT_LIMIT_FLAGS.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                // Vanara's SetInformationJobObject throws Win32Exception on failure
                SetInformationJobObject(jobHandle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, extendedInfo);

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
        public static void AssignProcess(SafeHJOB jobHandle, IntPtr processHandle)
        {
            if (jobHandle == null || jobHandle.IsInvalid || jobHandle.IsClosed)
            {
                throw new ArgumentException("Invalid job object handle", nameof(jobHandle));
            }

            if (processHandle == IntPtr.Zero)
            {
                throw new ArgumentException("Invalid process handle", nameof(processHandle));
            }

            if (!AssignProcessToJobObject(jobHandle, processHandle))
            {
                throw new InvalidOperationException(
                    "Failed to assign process to job object",
                    new System.ComponentModel.Win32Exception());
            }
        }
    }
}
