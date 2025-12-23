// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Windows
{
    using System;
    using System.Runtime.InteropServices;
    using Vanara.PInvoke;
    using static Vanara.PInvoke.Kernel32;

    internal static class NativeMethods
    {
        public const int S_OK = 0;

        // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE value
        // This is ProcThreadAttributePseudoConsole (22) | PROC_THREAD_ATTRIBUTE_INPUT (0x20000)
        private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x20016; // 22 | 0x20000

        private static readonly Lazy<bool> IsPseudoConsoleSupportedLazy = new Lazy<bool>(
            () =>
            {
                var hLibrary = LoadLibrary("kernel32.dll");
                return !hLibrary.IsInvalid && GetProcAddress(hLibrary, "CreatePseudoConsole") != IntPtr.Zero;
            },
            isThreadSafe: true);

        internal static bool IsPseudoConsoleSupported => IsPseudoConsoleSupportedLazy.Value;

        // Extension method to initialize STARTUPINFOEX with PseudoConsole attribute
        internal static void InitAttributeListAttachedToConPTY(ref this STARTUPINFOEX startupInfo, SafeHPCON pseudoConsoleHandle)
        {
            startupInfo.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEX>();
            startupInfo.StartupInfo.dwFlags = STARTF.STARTF_USESTDHANDLES;

            const int AttributeCount = 1;
            SizeT size = SizeT.Zero;

            // Create the appropriately sized thread attribute list
            bool wasInitialized = InitializeProcThreadAttributeList(IntPtr.Zero, AttributeCount, 0, ref size);
            if (wasInitialized || size == SizeT.Zero)
            {
                throw new InvalidOperationException(
                    $"Couldn't get the size of the process attribute list for {AttributeCount} attributes",
                    new System.ComponentModel.Win32Exception());
            }

            startupInfo.lpAttributeList = Marshal.AllocHGlobal((int)size);
            if (startupInfo.lpAttributeList == IntPtr.Zero)
            {
                throw new OutOfMemoryException("Couldn't reserve space for a new process attribute list");
            }

            // Set startup info's attribute list & initialize it
            wasInitialized = InitializeProcThreadAttributeList(startupInfo.lpAttributeList, AttributeCount, 0, ref size);
            if (!wasInitialized)
            {
                throw new InvalidOperationException("Couldn't create new process attribute list", new System.ComponentModel.Win32Exception());
            }

            // Set thread attribute list's Pseudo Console to the specified ConPTY
            // Note: We use our own P/Invoke for UpdateProcThreadAttribute because:
            // 1. Vanara's PROC_THREAD_ATTRIBUTE enum doesn't include PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE (newer Win10 feature)
            // 2. Vanara's UpdateProcThreadAttribute doesn't accept IntPtr for custom attribute values
            wasInitialized = UpdateProcThreadAttributeCustom(
                startupInfo.lpAttributeList,
                0,
                new IntPtr(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE),
                pseudoConsoleHandle.DangerousGetHandle(),
                (SizeT)Marshal.SizeOf<IntPtr>(),
                IntPtr.Zero,
                IntPtr.Zero);

            if (!wasInitialized)
            {
                throw new InvalidOperationException("Couldn't update process attribute list", new System.ComponentModel.Win32Exception());
            }
        }

        internal static void FreeAttributeList(ref this STARTUPINFOEX startupInfo)
        {
            if (startupInfo.lpAttributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                Marshal.FreeHGlobal(startupInfo.lpAttributeList);
                startupInfo.lpAttributeList = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Custom P/Invoke for UpdateProcThreadAttribute.
        /// Required because Vanara's version doesn't support PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
        /// which is a newer Windows 10 feature not yet in Vanara's PROC_THREAD_ATTRIBUTE enum.
        /// </summary>
        [DllImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttributeCustom(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr Attribute,
            IntPtr lpValue,
            SizeT cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);
    }
}
