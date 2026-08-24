// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Porta.Pty.Unix
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using static Porta.Pty.Unix.NativeIo;

    /// <summary>
    /// One poll(2) loop shared by every pty in the process, so waiting for a descriptor to become
    /// readable costs no thread at all.
    /// </summary>
    /// <remarks>
    /// The point of the whole exercise. A blocking read holds a thread for as long as the session is
    /// idle, which for a terminal is nearly always -- so N sessions cost N threads, and they are
    /// thread-pool threads whether the caller used ReadAsync or not. Here one thread sits in poll()
    /// over every registered descriptor and hands each waiter a completed task.
    ///
    /// Started on first use and never stopped. That is deliberate: the thread is a background thread
    /// and costs nothing while parked in poll(), and shutting it down would introduce a race with
    /// sessions that outlive whatever triggered the shutdown. Registrations come and go; the loop
    /// does not.
    ///
    /// Waking the loop up needs care. poll() is already blocked when a new descriptor is registered,
    /// so something has to interrupt it -- the self-pipe below is that something, and it is the
    /// standard trick rather than a clever one. Without it, registering a descriptor would not take
    /// effect until the current poll() returned for an unrelated reason.
    /// </remarks>
    internal sealed class PtyPoller
    {
        private static readonly Lazy<PtyPoller> InstanceHolder = new(() => new PtyPoller(), LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly object gate = new();
        private readonly Dictionary<int, Registration> registrations = new();
        private readonly List<int> stale = new();
        private PollFd[]? cachedSet;
        private int[]? cachedOrder;
        private int version;
        private int cachedVersion = -1;
        private readonly int wakeupReadFd;
        private readonly int wakeupWriteFd;

        private PtyPoller()
        {
            var fds = new int[2];
            if (pipe(fds) != 0)
            {
                throw new InvalidOperationException("Could not create the poller wakeup pipe");
            }

            this.wakeupReadFd = fds[0];
            this.wakeupWriteFd = fds[1];

            // Both ends MUST be non-blocking, and the result was being discarded. Neither failure is
            // benign: DrainWakeup reads until read(2) reports it would block, so a blocking read end
            // parks the poller thread the first time the pipe empties and every registered session
            // stops making progress. A blocking write end lets Wake() stall a caller once the pipe
            // fills. Better to fail here, where it is one exception at first use, than to hang.
            if (!SetNonBlocking(this.wakeupReadFd) || !SetNonBlocking(this.wakeupWriteFd))
            {
                int error = Marshal.GetLastPInvokeError();
                close(this.wakeupReadFd);
                close(this.wakeupWriteFd);
                throw new InvalidOperationException(
                    $"Could not put the poller wakeup pipe into non-blocking mode (errno {error}).");
            }

            var thread = new Thread(this.Loop)
            {
                IsBackground = true,
                Name = "Porta.Pty poller",
            };
            thread.Start();
        }

        internal static PtyPoller Instance => InstanceHolder.Value;

        /// <summary>
        /// Completes once <paramref name="fd"/> is readable, or has hung up.
        /// </summary>
        /// <remarks>
        /// Hangup completes the wait rather than faulting it. The caller's next read is what turns
        /// end-of-file into a zero return, and that is the read's answer to give, not the poller's.
        /// </remarks>
        internal Task WaitReadableAsync(int fd, CancellationToken cancellationToken)
            => this.WaitAsync(fd, POLLIN, cancellationToken);

        /// <summary>
        /// Completes once <paramref name="fd"/> accepts a write.
        /// </summary>
        internal Task WaitWritableAsync(int fd, CancellationToken cancellationToken)
            => this.WaitAsync(fd, POLLOUT, cancellationToken);

        /// <summary>
        /// Drops any interest in a descriptor, completing outstanding waiters.
        /// </summary>
        /// <remarks>
        /// Called when a connection is disposed. Leaving waiters pending would keep the descriptor
        /// in the poll set after it has been closed, and poll() reports a closed descriptor as
        /// POLLNVAL every time it is called -- a busy loop rather than an error.
        /// </remarks>
        internal void Unregister(int fd)
        {
            Registration? registration;
            lock (this.gate)
            {
                if (!this.registrations.Remove(fd, out registration))
                {
                    return;
                }

                this.version++;
            }

            registration!.CompleteAll();
            this.Wake();
        }

        private Task WaitAsync(int fd, short events, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Waiter waiter;

            lock (this.gate)
            {
                if (!this.registrations.TryGetValue(fd, out var registration))
                {
                    registration = new Registration();
                    this.registrations[fd] = registration;
                    this.version++;
                }

                waiter = registration.Add(events, completion);
            }

            if (cancellationToken.CanBeCanceled)
            {
                // Cancelling has to REMOVE the waiter, not just complete its task. A cancelled waiter
                // left in place keeps the registration's interest non-zero, so the stale-pruning pass
                // never drops the descriptor and the poller watches an fd nobody is waiting on.
                //
                // And the registration is disposed once the task settles, however it settles. Reads
                // retry in a loop against one long-lived token, so a callback left attached per
                // iteration -- each holding its TaskCompletionSource -- grows without bound on the
                // hot path for as long as the caller's token lives.
                var subscription = cancellationToken.Register(() =>
                {
                    if (completion.TrySetCanceled(cancellationToken))
                    {
                        this.RemoveWaiter(fd, waiter);
                    }
                });

                completion.Task.ContinueWith(
                    static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                    subscription,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            this.Wake();
            return completion.Task;
        }

        /// <summary>
        /// Drops one waiter, and the registration with it if that was the last one.
        /// </summary>
        private void RemoveWaiter(int fd, Waiter waiter)
        {
            lock (this.gate)
            {
                if (this.registrations.TryGetValue(fd, out var registration) && registration.Remove(waiter) == 0)
                {
                    this.registrations.Remove(fd);
                    this.version++;
                }
            }

            // Rebuild the poll set without it, rather than leaving the descriptor in until something
            // else happens to wake the loop.
            this.Wake();
        }

        private void Wake()
        {
            unsafe
            {
                byte one = 1;
                write(this.wakeupWriteFd, (IntPtr)(&one), (UIntPtr)1);
            }
        }

        private void Loop()
        {
            var drain = new byte[64];

            while (true)
            {
                PollFd[] set;
                int[] fdOrder;

                lock (this.gate)
                {
                    // Drop registrations nobody is waiting on any more. Leaving them in is not
                    // merely untidy: poll reports POLLHUP, POLLERR and POLLNVAL whether or not they
                    // were asked for, so a descriptor whose child has exited returns from every
                    // poll immediately, and a set full of those is a spin at full CPU rather than a
                    // thread parked in the kernel. A registration now lives exactly as long as a
                    // waiter.
                    this.stale.Clear();

                    foreach (var pair in this.registrations)
                    {
                        if (pair.Value.InterestedEvents == 0)
                        {
                            this.stale.Add(pair.Key);
                        }
                    }

                    foreach (var fd in this.stale)
                    {
                        this.registrations.Remove(fd);
                        this.version++;
                    }

                    // Rebuilt only when the registration set actually changed. Wake() runs once per
                    // WaitAsync -- so once per read, per session -- and reallocating both arrays on
                    // every one of those was steady churn on the single shared thread, under
                    // precisely the many-session workload this exists to serve.
                    if (this.cachedSet is null || this.cachedVersion != this.version)
                    {
                        this.cachedSet = new PollFd[this.registrations.Count + 1];
                        this.cachedOrder = new int[this.registrations.Count];

                        var i = 1;
                        foreach (var pair in this.registrations)
                        {
                            this.cachedOrder[i - 1] = pair.Key;
                            this.cachedSet[i++] = new PollFd { Fd = pair.Key };
                        }

                        this.cachedVersion = this.version;
                    }

                    set = this.cachedSet;
                    fdOrder = this.cachedOrder!;

                    // Events and revents still refresh every pass: interest changes without the
                    // registration set changing, and revents is where the kernel writes its answer.
                    set[0] = new PollFd { Fd = this.wakeupReadFd, Events = POLLIN };
                    for (var i = 0; i < fdOrder.Length; i++)
                    {
                        set[i + 1].Events = this.registrations[fdOrder[i]].InterestedEvents;
                        set[i + 1].Revents = 0;
                    }
                }

                int ready = poll(set, (UIntPtr)(uint)set.Length, -1);
                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == EINTR)
                    {
                        continue;
                    }

                    // Not throwing is right -- this is a background thread and letting it escape
                    // takes the process down -- but not throwing and not TELLING anyone are
                    // separable, and only the first was intended. A persistent failure here leaves
                    // every async pty read quantised to this backoff forever with nothing to say
                    // why, and since synchronous Read and Write route through this loop too, a
                    // wedged loop hangs those callers as well.
                    Debug.WriteLine(
                        $"Porta.Pty poller: poll(2) failed with errno {Marshal.GetLastPInvokeError()}; retrying.");

                    Thread.Sleep(50);
                    continue;
                }

                if ((set[0].Revents & POLLIN) != 0)
                {
                    this.DrainWakeup(drain);
                }

                for (var i = 1; i < set.Length; i++)
                {
                    var revents = set[i].Revents;
                    if (revents == 0)
                    {
                        continue;
                    }

                    Registration? registration;
                    lock (this.gate)
                    {
                        this.registrations.TryGetValue(fdOrder[i - 1], out registration);
                    }

                    // POLLHUP, POLLERR and POLLNVAL are reported whether or not they were asked
                    // for, and every one of them means "stop waiting". Completing on them is what
                    // keeps a closed or hung-up descriptor from parking a waiter forever.
                    registration?.Complete(revents);
                }
            }
        }

        private void DrainWakeup(byte[] scratch)
        {
            unsafe
            {
                fixed (byte* p = scratch)
                {
                    while (read(this.wakeupReadFd, (IntPtr)p, (UIntPtr)(uint)scratch.Length).ToInt64() > 0)
                    {
                        // The pipe is only ever a doorbell; the bytes carry nothing.
                    }
                }
            }
        }

        private sealed class Waiter
        {
            internal Waiter(short events, TaskCompletionSource completion)
            {
                this.Events = events;
                this.Completion = completion;
            }

            internal short Events { get; }

            internal TaskCompletionSource Completion { get; }
        }

        private sealed class Registration
        {
            private readonly List<Waiter> waiters = new();
            private short interestedEvents;

            /// <summary>
            /// The union of what every waiter is waiting for.
            /// </summary>
            /// <remarks>
            /// Maintained as waiters come and go rather than recomputed. The loop reads this for
            /// every registration on every wakeup WHILE HOLDING the global lock -- the same lock
            /// each WaitAsync needs -- so walking the waiter list here made registration serialise
            /// behind the loop's bookkeeping at exactly the session counts this feature is for.
            /// </remarks>
            internal short InterestedEvents => Volatile.Read(ref this.interestedEvents);

            internal Waiter Add(short events, TaskCompletionSource completion)
            {
                var waiter = new Waiter(events, completion);
                lock (this.waiters)
                {
                    this.waiters.Add(waiter);
                    this.interestedEvents |= events;
                }

                return waiter;
            }

            /// <summary>
            /// Removes one waiter and reports how many are left.
            /// </summary>
            internal int Remove(Waiter waiter)
            {
                lock (this.waiters)
                {
                    this.waiters.Remove(waiter);
                    this.Recompute();
                    return this.waiters.Count;
                }
            }

            /// <summary>
            /// Rebuilds the mask. Only on removal, where the union can shrink.
            /// </summary>
            private void Recompute()
            {
                short events = 0;
                foreach (var waiter in this.waiters)
                {
                    events |= waiter.Events;
                }

                this.interestedEvents = events;
            }

            internal void Complete(short revents)
            {
                List<TaskCompletionSource> ready = new();
                bool fatal = (revents & (POLLHUP | POLLERR | POLLNVAL)) != 0;

                lock (this.waiters)
                {
                    for (var i = this.waiters.Count - 1; i >= 0; i--)
                    {
                        var waiter = this.waiters[i];
                        if (fatal || (waiter.Events & revents) != 0)
                        {
                            ready.Add(waiter.Completion);
                            this.waiters.RemoveAt(i);
                        }
                    }

                    this.Recompute();
                }

                foreach (var completion in ready)
                {
                    completion.TrySetResult();
                }
            }

            internal void CompleteAll()
            {
                List<TaskCompletionSource> ready;
                lock (this.waiters)
                {
                    ready = new List<TaskCompletionSource>(this.waiters.Count);
                    foreach (var waiter in this.waiters)
                    {
                        ready.Add(waiter.Completion);
                    }

                    this.waiters.Clear();
                    this.interestedEvents = 0;
                }

                foreach (var completion in ready)
                {
                    completion.TrySetResult();
                }
            }
        }
    }
}
