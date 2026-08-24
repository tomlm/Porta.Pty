// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Options for spawning a new pty process.
    /// </summary>
    public class PtyOptions
    {
        /// <summary>
        /// Gets or sets the terminal name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the number of initial rows.
        /// </summary>
        public int Rows { get; set; }

        /// <summary>
        /// Gets or sets the number of initial columns.
        /// </summary>
        public int Cols { get; set; }

        /// <summary>
        /// Gets or sets the working directory for the spawned process.
        /// </summary>
        public string Cwd { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the path to the process to be spawned.
        /// </summary>
        public string App { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the command line arguments to the process.
        /// </summary>
        public string[] CommandLine { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets or sets a value indicating whether command line arguments must be quoted.
        /// <c>false</c>, the default, means that the arguments must be quoted and quotes inside escaped then concatenated with spaces.
        /// <c>true</c> means that the arguments must not be quoted and just concatenated with spaces.
        /// </summary>
        public bool VerbatimCommandLine { get; set; }

        /// <summary>
        /// Gets or sets the process' environment variables.
        /// </summary>
        public IDictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Gets or sets a value indicating whether reads and writes should complete without holding
        /// a thread. Off by default.
        /// </summary>
        /// <remarks>
        /// The same guarantee on every platform, which is the only thing that makes it worth being
        /// one option rather than two: with this set, awaiting ReadAsync on an idle session occupies
        /// no thread, so a process can hold many sessions open at a cost that does not scale with
        /// how many of them are quiet.
        ///
        /// How that is achieved differs. Unix puts the controller into non-blocking mode and shares
        /// one poll(2) loop, plus one reaper in place of a waitpid thread per child. Windows uses
        /// overlapped pipes and the I/O completion port. What a caller can rely on does not differ,
        /// and both are implemented -- an earlier revision of this documentation promised the
        /// guarantee on Windows before it was true.
        ///
        /// Opt-in because it changes the I/O path underneath every existing consumer. The default
        /// path is unchanged: a blocking descriptor, and ReadAsync serviced by the thread pool.
        ///
        /// Needs Linux 5.3 or newer, for the pidfd_open used to watch a child exit; spawning with
        /// this set throws PlatformNotSupportedException on anything older rather than quietly
        /// falling back to something slower. Of the distributions .NET 10 supports, only RHEL 8
        /// ships an older kernel. macOS and Windows have no such floor.
        ///
        /// Synchronous Read and Write keep working either way, and still block the calling thread.
        /// This is about what ASYNC costs, not about removing the sync API.
        /// </remarks>
        public bool UseAsyncIo { get; set; }
    }
}
