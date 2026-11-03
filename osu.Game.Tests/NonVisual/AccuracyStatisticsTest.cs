// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Game.Rulesets.Scoring;

namespace osu.Game.Tests.NonVisual
{
    [TestFixture]
    public class AccuracyStatisticsTest
    {
        // The base scores of osu!stable/lazer scoring.
        private static int osuBase(HitResult result) => result switch
        {
            HitResult.Great => 300,
            HitResult.Ok => 100,
            HitResult.Meh => 50,
            HitResult.SmallTickHit => 10,
            HitResult.LargeTickHit => 30,
            HitResult.SliderTailHit => 150,
            _ => 0
        };

        // osu!mania: Perfect is worth a little more than Great, then Good, Ok, Meh.
        private static int maniaBase(HitResult result) => result switch
        {
            HitResult.Perfect => 305,
            HitResult.Great => 300,
            HitResult.Good => 200,
            HitResult.Ok => 100,
            HitResult.Meh => 50,
            _ => 0
        };

        private static readonly HitResult[] osu_results = { HitResult.Great, HitResult.Ok, HitResult.Meh, HitResult.Miss, HitResult.SmallTickHit, HitResult.LargeTickHit, HitResult.SliderTailHit };
        private static readonly HitResult[] mania_results = { HitResult.Perfect, HitResult.Great, HitResult.Good, HitResult.Ok, HitResult.Meh, HitResult.Miss };

        [TestCase(0.95, 925, 75)]
        [TestCase(0.90, 850, 150)]
        [TestCase(0.975, 962, 38)]
        public void TestOsuPlainCirclesBecomeOks(double accuracy, int expectedGreat, int expectedOk)
        {
            var max = new Dictionary<HitResult, int> { [HitResult.Great] = 1000 };

            var stats = AccuracyStatistics.Generate(max, accuracy, osuBase, osu_results);

            Assert.That(stats[HitResult.Great], Is.EqualTo(expectedGreat).Within(1));
            Assert.That(stats[HitResult.Ok], Is.EqualTo(expectedOk).Within(1));
            Assert.That(stats.GetValueOrDefault(HitResult.Miss), Is.Zero, "accuracy is lowered with 100s, not misses");
            Assert.That(stats.Values.Sum(), Is.EqualTo(1000), "the number of hits does not change");
            Assert.That(AccuracyStatistics.Compute(stats, max, osuBase), Is.EqualTo(accuracy).Within(0.001));
        }

        [TestCase(0.95)]
        [TestCase(0.90)]
        public void TestSliderTicksCountTowardsAccuracyAndStayIntact(double accuracy)
        {
            var max = new Dictionary<HitResult, int>
            {
                [HitResult.Great] = 600,
                [HitResult.SliderTailHit] = 200,
                [HitResult.LargeTickHit] = 400,
                [HitResult.SmallTickHit] = 100,
            };

            var stats = AccuracyStatistics.Generate(max, accuracy, osuBase, osu_results);

            Assert.That(stats[HitResult.SliderTailHit], Is.EqualTo(200));
            Assert.That(stats[HitResult.LargeTickHit], Is.EqualTo(400));
            Assert.That(stats[HitResult.SmallTickHit], Is.EqualTo(100));
            Assert.That(AccuracyStatistics.Compute(stats, max, osuBase), Is.EqualTo(accuracy).Within(0.002));
        }

        [Test]
        public void TestPerfectAccuracyChangesNothing()
        {
            var max = new Dictionary<HitResult, int> { [HitResult.Great] = 500, [HitResult.SliderTailHit] = 100 };

            var stats = AccuracyStatistics.Generate(max, 1, osuBase, osu_results);

            Assert.That(stats, Is.EquivalentTo(max));
        }

        [Test]
        public void TestMaximumStatisticsAreNotModified()
        {
            var max = new Dictionary<HitResult, int> { [HitResult.Great] = 1000 };

            AccuracyStatistics.Generate(max, 0.9, osuBase, osu_results);

            Assert.That(max[HitResult.Great], Is.EqualTo(1000));
            Assert.That(max.Count, Is.EqualTo(1));
        }

        [Test]
        public void TestRulesetWithoutALowerHitFallsBackToMisses()
        {
            // osu!catch style: one kind of basic hit, so lost accuracy can only be misses.
            var max = new Dictionary<HitResult, int> { [HitResult.Great] = 1000 };

            var stats = AccuracyStatistics.Generate(max, 0.95, osuBase, new[] { HitResult.Great, HitResult.Miss });

            Assert.That(stats[HitResult.Great], Is.EqualTo(950).Within(1));
            Assert.That(stats[HitResult.Miss], Is.EqualTo(50).Within(1));
            Assert.That(AccuracyStatistics.Compute(stats, max, osuBase), Is.EqualTo(0.95).Within(0.001));
        }

        [TestCase(0.95)]
        [TestCase(0.90)]
        public void TestManiaWalksDownThroughSeveralKindsOfHit(double accuracy)
        {
            var max = new Dictionary<HitResult, int> { [HitResult.Perfect] = 1000 };

            var stats = AccuracyStatistics.Generate(max, accuracy, maniaBase, mania_results);

            Assert.That(stats.Values.All(v => v >= 0), Is.True, "no negative counts");
            Assert.That(stats.Values.Sum(), Is.EqualTo(1000));
            Assert.That(AccuracyStatistics.Compute(stats, max, maniaBase), Is.EqualTo(accuracy).Within(0.01));
        }

        [Test]
        public void TestNothingToDowngradeLeavesStatisticsAlone()
        {
            var stats = AccuracyStatistics.Generate(new Dictionary<HitResult, int>(), 0.9, osuBase, osu_results);

            Assert.That(stats, Is.Empty);
        }

        [Test]
        public void TestAccuracyIsClamped()
        {
            var max = new Dictionary<HitResult, int> { [HitResult.Great] = 100 };

            Assert.DoesNotThrow(() => AccuracyStatistics.Generate(max, -3, osuBase, osu_results));
            Assert.DoesNotThrow(() => AccuracyStatistics.Generate(max, 7, osuBase, osu_results));
            Assert.That(AccuracyStatistics.Generate(max, 7, osuBase, osu_results), Is.EquivalentTo(max));

            var floor = AccuracyStatistics.Generate(max, -3, osuBase, osu_results);
            Assert.That(floor.Values.All(v => v >= 0), Is.True);
            Assert.That(AccuracyStatistics.Compute(floor, max, osuBase), Is.EqualTo(0).Within(0.01), "an accuracy below zero is treated as zero");
        }

        [Test]
        public void TestComputeOfAnEmptyMaximumIsPerfect()
        {
            Assert.That(AccuracyStatistics.Compute(new Dictionary<HitResult, int>(), new Dictionary<HitResult, int>(), osuBase), Is.EqualTo(1));
            Assert.That(Math.Abs(1 - AccuracyStatistics.Compute(new Dictionary<HitResult, int> { [HitResult.Great] = 5 }, new Dictionary<HitResult, int> { [HitResult.Great] = 5 }, osuBase)), Is.LessThan(1e-9));
        }
    }
}
