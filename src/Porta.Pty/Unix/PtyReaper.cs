// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Unix.NativeIo;

    /// <summary>
    /// Collects exit statuses for every pty child in the process from one thread.
    /// </summary>
    /// <remarks>
    /// The other half of not costing a thread per session, and the half that turned out to dominate.
    /// Making reads threadless left a watcher thread per connection sitting in a blocking waitpid,
    /// so twelve idle sessions still cost twelve threads; the reads were never the floor.
    ///
    /// waitpid(-1) would be the obvious way to write this and is the wrong one. It reaps ANY child of
    /// the process, including ones this library never spawned -- System.Diagnostics.Process on Unix
    /// reaps its own children, and a status collected here is a status it will never see, so its
    /// WaitForExit would hang on a process that had already exited. A library sharing an address
    /// space with code it does not control cannot claim every child. So this waits on the pids it was
    /// given, one at a time, with WNOHANG.
    ///
    /// The kernel reports the exit rather than being asked for it: kqueue EVFILT_PROC on macOS, a
    /// pidfd on Linux. Both are POLLABLE, so this needs no thread of its own -- the pty poll(2) loop
    /// that already exists carries the queue, and exits arrive in single-digit milliseconds instead
    /// of on a hundred-millisecond interval.
    ///
    /// pidfd_open needs Linux 5.3. There is deliberately NO polling fallback below that: of the
    /// distributions .NET 10 supports, only RHEL 8 ships an older kernel, and a fallback would be
    /// the one path CI could never exercise. UseAsyncIo refuses the spawn there instead, which
    /// leaves the default blocking path -- unchanged, and working everywhere.
    /// </remarks>
    internal sealed class PtyReaper
    {
        private const int EINTR = 4;

        /// <summary>
        /// How long to wait before resuming the watch after an unexpected failure.
        /// </summary>
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

        private static readonly Lazy<PtyReaper> InstanceHolder =
            new(() => new PtyReaper(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object gate = new();
        private readonly Dictionary<int, Entry> children = new();
        private readonly int queue;

        private PtyReaper()
        {
            this.queue = pty_exit_queue();

            if (this.queue >= 0)
            {
                // No thread at all. The queue is a pollable descriptor, so waiting on it is one
                // more registration in the poll loop that already exists.
                _ = Task.Run(this.WatchAsync);
            }
        }

        /// <summary>
        /// Attempts to collect the status of one child without blocking.
        /// </summary>
        /// <returns>The pid on success, 0 if it is still running, -1 on failure.</returns>
        internal delegate int ReapAttempt(int pid, ref int status);

        internal static PtyReaper Instance => InstanceHolder.Value;

        /// <summary>
        /// Whether this kernel can report child exits.
        /// </summary>
        internal bool IsSupported => this.queue >= 0;

        /// <summary>
        /// Watches <paramref name="pid"/> until it exits, then calls <paramref name="onExited"/> once
        /// with its raw wait status.
        /// </summary>
        internal void Register(int pid, ReapAttempt attempt, Action<int> onExited)
        {
            lock (this.gate)
            {
                this.children[pid] = new Entry(attempt, onExited);
            }

            if (pty_exit_watch(this.queue, pid) == 0)
            {
                return;
            }

            // Watching failed, and the likeliest reason by far is that the child has ALREADY
            // exited -- both kernels report that as ESRCH, indistinguishable here from anything
            // else. Collect it directly: an exit between spawning and watching is otherwise never
            // reported, and WaitForExit would wait on a process already gone.
            this.CollectIfExited(pid);

            lock (this.gate)
            {
                if (!this.children.ContainsKey(pid))
                {
                    return;
                }

                this.children.Remove(pid);
            }

            // Still running and still unwatchable, so nothing will ever reap it. There is no
            // polling fallback to fall back TO any more, and pretending otherwise would hand back
            // a connection whose WaitForExit never returns.
            throw new InvalidOperationException(
                $"Could not watch pty child {pid} for exit (errno {Marshal.GetLastPInvokeError()}).");
        }

        /// <summary>
        /// Stops watching a pid.
        /// </summary>
        /// <remarks>
        /// Deliberately NOT called from PtyConnection.Dispose. A disposed connection has signalled
        /// its child but not collected it, and dropping the watch there left a zombie for the life
        /// of the host process. Kept for a caller that genuinely knows a pid is already reaped.
        /// </remarks>
        internal void Unregister(int pid)
        {
            lock (this.gate)
            {
                this.children.Remove(pid);
            }
        }

        /// <summary>
        /// Parks on the exit queue and collects whatever it reports. Holds no thread while waiting.
        /// </summary>
        private async Task WatchAsync()
        {
            var pids = new int[64];

            while (true)
            {
                try
                {
                    await PtyPoller.Instance.WaitReadableAsync(this.queue, CancellationToken.None).ConfigureAwait(false);

                    int count = pty_exit_drain(this.queue, pids, pids.Length);
                    for (var i = 0; i < count; i++)
                    {
                        this.CollectIfExited(pids[i]);
                    }
                }
                catch
                {
                    // Nothing useful to do with a failure here, and letting it escape would end the
                    // watch for every session in the process. Pause rather than spin.
                    await Task.Delay(RetryDelay).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Reaps one pid if it has exited, and reports it.
        /// </summary>
        private void CollectIfExited(int pid)
        {
            Entry? entry;
            lock (this.gate)
            {
                if (!this.children.TryGetValue(pid, out entry))
                {
                    return;
                }
            }

            int status = 0;
            int result;
            int error = 0;
            try
            {
                result = entry!.Attempt(pid, ref status);
                error = result < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            catch
            {
                result = -1;
                error = 0;
            }

            if (result == 0 || (result < 0 && error == EINTR))
            {
                // Not collectable yet, or interrupted. Either way it stays registered.
                return;
            }

            lock (this.gate)
            {
                this.children.Remove(pid);
            }

            if (result < 0)
            {
                // ECHILD: something else collected it and there is no status to be had. Reporting
                // the zero would claim the child succeeded.
                return;
            }

            try
            {
                entry.OnExited(status);
            }
            catch
            {
                // A consumer's exit handler throwing is not ours to propagate.
            }
        }

        private sealed class Entry
        {
            internal Entry(ReapAttempt attempt, Action<int> onExited)
            {
                this.Attempt = attempt;
                this.OnExited = onExited;
            }

            internal ReapAttempt Attempt { get; }

            internal Action<int> OnExited { get; }
        }
    }
}
