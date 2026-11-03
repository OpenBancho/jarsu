// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// A <see cref="TaskScheduler"/> with a single low-priority worker thread that always runs the most urgent queued task next,
    /// and that can be paused as a whole.
    /// </summary>
    /// <remarks>
    /// Urgency is asked for again every time the worker picks its next task (not once when the task is queued), so a task that was
    /// queued as unimportant becomes urgent the moment whatever it works on is needed - without having to be re-queued.
    /// Among equally urgent tasks the order is first in, first out.
    /// A task that is already running is never interrupted by the scheduler itself: pausing only stops the next one from starting,
    /// and it is up to the running task's own cancellation token to cut it short.
    /// </remarks>
    public sealed class PrioritisedTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly List<Task> queue = new List<Task>();
        private readonly Func<Task, int> priorityOf;
        private readonly ManualResetEventSlim runGate = new ManualResetEventSlim(true);
        private readonly Thread thread;

        private bool disposed;

        /// <param name="name">The name of the worker thread.</param>
        /// <param name="priorityOf">The urgency of a queued task. The task with the highest value runs first.</param>
        public PrioritisedTaskScheduler(string name, Func<Task, int> priorityOf)
        {
            this.priorityOf = priorityOf;

            thread = new Thread(run)
            {
                Name = name,
                IsBackground = true,
                // Background calculations must never compete with the game's own threads.
                Priority = ThreadPriority.BelowNormal,
            };

            thread.Start();
        }

        public override int MaximumConcurrencyLevel => 1;

        /// <summary>
        /// Whether no queued task is started. A task that is already running carries on.
        /// </summary>
        public bool Paused
        {
            get => !runGate.IsSet;
            set
            {
                if (value)
                    runGate.Reset();
                else
                    runGate.Set();
            }
        }

        /// <summary>
        /// The number of tasks waiting to be started.
        /// </summary>
        public int PendingCount
        {
            get
            {
                lock (queue)
                    return queue.Count;
            }
        }

        protected override void QueueTask(Task task)
        {
            lock (queue)
            {
                if (disposed)
                    return;

                queue.Add(task);
                Monitor.Pulse(queue);
            }
        }

        // Running a task on the caller's thread would bypass both the order and the pause.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (queue)
                return queue.ToArray();
        }

        private void run()
        {
            while (true)
            {
                // Held here for as long as the scheduler is paused. Released by Dispose too, so the thread can end.
                // (A timeout is required of every wait in this project, hence the loop.)
                while (!runGate.Wait(1000))
                {
                }

                Task next;

                lock (queue)
                {
                    while (queue.Count == 0 && !disposed)
                        Monitor.Wait(queue);

                    if (disposed)
                        return;

                    // The scheduler may have been paused while this thread waited for work: go back to waiting.
                    if (!runGate.IsSet)
                        continue;

                    int best = 0;
                    int bestPriority = priorityOfSafely(queue[0]);

                    for (int i = 1; i < queue.Count; i++)
                    {
                        int priority = priorityOfSafely(queue[i]);

                        // Strictly greater, so that equally urgent tasks keep their order.
                        if (priority > bestPriority)
                        {
                            best = i;
                            bestPriority = priority;
                        }
                    }

                    next = queue[best];
                    queue.RemoveAt(best);
                }

                TryExecuteTask(next);
            }
        }

        /// <summary>
        /// The urgency of a task. A failing urgency function must not take the worker thread (and with it the game) down: such a task is just not urgent.
        /// </summary>
        private int priorityOfSafely(Task task)
        {
            try
            {
                return priorityOf(task);
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            lock (queue)
            {
                disposed = true;
                Monitor.PulseAll(queue);
            }

            runGate.Set();
        }
    }
}
