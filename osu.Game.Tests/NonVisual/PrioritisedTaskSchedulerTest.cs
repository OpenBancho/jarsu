// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using osu.Game.Beatmaps;

namespace osu.Game.Tests.NonVisual
{
    [TestFixture]
    public class PrioritisedTaskSchedulerTest
    {
        private const int timeout = 5000;

        private readonly ConcurrentDictionary<string, int> priorities = new ConcurrentDictionary<string, int>();
        private readonly ConcurrentQueue<string> ran = new ConcurrentQueue<string>();

        private PrioritisedTaskScheduler scheduler = null!;

        [SetUp]
        public void SetUp()
        {
            priorities.Clear();
            ran.Clear();

            // The state of a task is its name; its urgency is looked up when the scheduler picks.
            scheduler = new PrioritisedTaskScheduler("test", task => task.AsyncState is string name ? priorities.GetValueOrDefault(name, 0) : 0);
        }

        [TearDown]
        public void TearDown() => scheduler.Dispose();

        private Task queue(string name) =>
            Task.Factory.StartNew(state => ran.Enqueue((string)state!), name, CancellationToken.None, TaskCreationOptions.None, scheduler);

        [Test]
        public void TestMostUrgentRunsFirst()
        {
            scheduler.Paused = true;

            priorities["background"] = 0;
            priorities["selected"] = 1;

            var tasks = new[] { queue("background"), queue("selected") };

            scheduler.Paused = false;
            Task.WaitAll(tasks, timeout);

            Assert.That(ran, Is.EqualTo(new[] { "selected", "background" }));
        }

        [Test]
        public void TestEquallyUrgentKeepTheirOrder()
        {
            scheduler.Paused = true;

            var tasks = new[] { queue("a"), queue("b"), queue("c"), queue("d") };

            scheduler.Paused = false;
            Task.WaitAll(tasks, timeout);

            Assert.That(ran, Is.EqualTo(new[] { "a", "b", "c", "d" }));
        }

        [Test]
        public void TestUrgencyIsDecidedWhenPicking_NotWhenQueued()
        {
            scheduler.Paused = true;

            var tasks = new[] { queue("first"), queue("second"), queue("third") };

            // "third" was queued as unimportant, and only became the selected beatmap afterwards.
            priorities["third"] = 1;

            scheduler.Paused = false;
            Task.WaitAll(tasks, timeout);

            Assert.That(ran, Is.EqualTo(new[] { "third", "first", "second" }));
        }

        [Test]
        public void TestPausedSchedulerStartsNothingUntilResumed()
        {
            scheduler.Paused = true;

            var task = queue("a");

            Assert.That(task.Wait(300), Is.False, "task must not start while paused");
            Assert.That(ran, Is.Empty);
            Assert.That(scheduler.PendingCount, Is.EqualTo(1));

            scheduler.Paused = false;

            Assert.That(task.Wait(timeout), Is.True, "task must start once resumed");
            Assert.That(ran, Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void TestPausingDoesNotInterruptARunningTask_ButStopsTheNextOne()
        {
            var started = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();

            var running = Task.Factory.StartNew(() =>
            {
                started.Set();
                release.Wait(timeout);
                ran.Enqueue("running");
            }, CancellationToken.None, TaskCreationOptions.None, scheduler);

            Assert.That(started.Wait(timeout), Is.True);

            scheduler.Paused = true;

            var next = queue("next");
            release.Set();

            Assert.That(running.Wait(timeout), Is.True, "the task that was already running finishes");
            Assert.That(next.Wait(300), Is.False, "but nothing new starts while paused");

            scheduler.Paused = false;

            Assert.That(next.Wait(timeout), Is.True);
            Assert.That(ran, Is.EqualTo(new[] { "running", "next" }));
        }

        [Test]
        public void TestCancelledQueuedTaskDoesNotRunAndDoesNotBlockOthers()
        {
            scheduler.Paused = true;

            using var cts = new CancellationTokenSource();

            var cancelled = Task.Factory.StartNew(state => ran.Enqueue((string)state!), "cancelled", cts.Token, TaskCreationOptions.None, scheduler);
            var other = queue("other");

            cts.Cancel();
            scheduler.Paused = false;

            Assert.That(other.Wait(timeout), Is.True);
            Assert.That(ran, Is.EqualTo(new[] { "other" }));
            Assert.That(cancelled.IsCanceled, Is.True);
        }

        [Test]
        public void TestFailingPriorityFunctionDoesNotKillTheWorker()
        {
            using var throwing = new PrioritisedTaskScheduler("test-throwing", _ => throw new InvalidOperationException("boom"));

            var task = Task.Factory.StartNew(() => ran.Enqueue("survived"), CancellationToken.None, TaskCreationOptions.None, throwing);

            Assert.That(task.Wait(timeout), Is.True);
            Assert.That(ran, Is.EqualTo(new[] { "survived" }));
        }

        [Test]
        public void TestDisposeEndsTheWorkerEvenWhilePaused()
        {
            scheduler.Paused = true;
            queue("never");

            Assert.DoesNotThrow(() => scheduler.Dispose());
        }

        [Test]
        public void TestTasksRunOneAtATime()
        {
            int concurrent = 0;
            int maxConcurrent = 0;

            var tasks = Enumerable.Range(0, 20).Select(_ => Task.Factory.StartNew(() =>
            {
                int now = Interlocked.Increment(ref concurrent);
                int seen;
                while (now > (seen = Volatile.Read(ref maxConcurrent)) && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen)
                {
                }

                Thread.Sleep(2);
                Interlocked.Decrement(ref concurrent);
            }, CancellationToken.None, TaskCreationOptions.None, scheduler)).ToArray();

            Task.WaitAll(tasks, timeout);

            Assert.That(maxConcurrent, Is.EqualTo(1));
        }
    }
}
