// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;

namespace osu.Game.Rulesets.Scoring
{
    /// <summary>
    /// Builds the hit statistics of a hypothetical play that ended at a given accuracy, for any ruleset.
    /// Used to estimate the performance of a beatmap that has not been played at that accuracy.
    /// </summary>
    public static class AccuracyStatistics
    {
        /// <summary>
        /// The accuracy of a set of statistics: the base score of what was hit, over the base score of a perfect play.
        /// This is the same definition the score processor uses.
        /// </summary>
        public static double Compute(IReadOnlyDictionary<HitResult, int> statistics, IReadOnlyDictionary<HitResult, int> maximumStatistics, Func<HitResult, int> baseScoreOf)
        {
            long baseScore = statistics.Where(kvp => kvp.Key.AffectsAccuracy()).Sum(kvp => (long)kvp.Value * baseScoreOf(kvp.Key));
            long maxBaseScore = maximumStatistics.Where(kvp => kvp.Key.AffectsAccuracy()).Sum(kvp => (long)kvp.Value * baseScoreOf(kvp.Key));

            return maxBaseScore == 0 ? 1 : baseScore / (double)maxBaseScore;
        }

        /// <summary>
        /// Statistics of a play with no misses beyond what the accuracy demands, whose accuracy is as close to <paramref name="accuracy"/> as the hit values allow.
        /// </summary>
        /// <remarks>
        /// Perfect hits are downgraded to the next worse kind of hit (a 300 becomes a 100 in osu!, a Perfect a Great in mania), and only once a
        /// kind has been used up does the next one down get used. A ruleset that has no worse kind of hit (osu!catch) gets misses instead.
        /// Hits that are not basic ones (slider ticks, droplets, bonus) are left as they are in a perfect play, so the result is an estimate:
        /// the point is a stable, comparable number per difficulty, not a replica of any real score.
        /// </remarks>
        /// <param name="maximumStatistics">The statistics of a perfect play.</param>
        /// <param name="accuracy">The accuracy to aim for, from 0 to 1.</param>
        /// <param name="baseScoreOf">The base score of a result, as defined by the ruleset's score processor.</param>
        /// <param name="validHitResults">The results the ruleset can produce.</param>
        public static Dictionary<HitResult, int> Generate(IReadOnlyDictionary<HitResult, int> maximumStatistics, double accuracy, Func<HitResult, int> baseScoreOf,
                                                          IEnumerable<HitResult> validHitResults)
        {
            var statistics = new Dictionary<HitResult, int>(maximumStatistics);

            accuracy = Math.Clamp(accuracy, 0, 1);

            long maxBaseScore = maximumStatistics.Where(kvp => kvp.Key.AffectsAccuracy()).Sum(kvp => (long)kvp.Value * baseScoreOf(kvp.Key));

            if (maxBaseScore == 0 || accuracy >= 1)
                return statistics;

            // The best basic hit of the perfect play is where every hit starts.
            var present = maximumStatistics.Where(kvp => kvp.Value > 0 && isBasicHit(kvp.Key, baseScoreOf)).Select(kvp => kvp.Key).ToList();

            if (present.Count == 0)
                return statistics;

            HitResult top = present.OrderByDescending(baseScoreOf).First();

            // The kinds of hit to fall through, best first, one per distinct base score (a result worth the same as the one above it adds nothing),
            // ending in a miss.
            var chain = new List<HitResult> { top };

            foreach (var result in validHitResults.Where(r => isBasicHit(r, baseScoreOf) && baseScoreOf(r) < baseScoreOf(top)).OrderByDescending(baseScoreOf))
            {
                if (baseScoreOf(result) < baseScoreOf(chain[^1]))
                    chain.Add(result);
            }

            chain.Add(HitResult.Miss);

            double remaining = (1 - accuracy) * maxBaseScore;

            for (int i = 0; i < chain.Count - 1 && remaining > 0; i++)
            {
                HitResult from = chain[i];
                HitResult to = chain[i + 1];

                int loss = baseScoreOf(from) - baseScoreOf(to);

                if (loss <= 0)
                    continue;

                int move = (int)Math.Min(statistics.GetValueOrDefault(from), Math.Round(remaining / loss));

                if (move <= 0)
                    continue;

                statistics[from] -= move;
                statistics[to] = statistics.GetValueOrDefault(to) + move;

                remaining -= (double)move * loss;
            }

            return statistics;
        }

        private static bool isBasicHit(HitResult result, Func<HitResult, int> baseScoreOf) => result.IsBasic() && result.IsHit() && baseScoreOf(result) > 0;
    }
}
