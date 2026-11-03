// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Audio.Track;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics.Textures;
using osu.Framework.Lists;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Skinning;
using osu.Game.Storyboards;

namespace osu.Game.Beatmaps
{
    /// <summary>
    /// A component which performs and acts as a central cache for difficulty calculations of beatmap/ruleset/mod combinations.
    /// Currently not persisted between game sessions.
    /// </summary>
    public partial class BeatmapDifficultyCache : MemoryCachingComponent<BeatmapDifficultyCache.DifficultyCacheLookup, StarDifficulty?>
    {
        // Too many simultaneous updates can lead to stutters. One thread seems to work fine for song select display purposes.
        // It always takes the difficulty of the selected beatmap first, and it does not run at all while the local user is playing.
        private readonly PrioritisedTaskScheduler updateScheduler;

        // Timed attributes (the live performance display of gameplay and replays) are needed *during* play, so they must not wait behind the scheduler above.
        private readonly ThreadedTaskScheduler timedUpdateScheduler = new ThreadedTaskScheduler(1, nameof(BeatmapDifficultyCache) + "Timed");

        /// <summary>
        /// The beatmap the user currently has selected. Its difficulty is worked on before anything else.
        /// </summary>
        private volatile BeatmapInfo? focusedBeatmap;

        /// <summary>
        /// The calculation the worker thread is busy with right now, if any.
        /// </summary>
        private volatile DifficultyJob? runningJob;

        public BeatmapDifficultyCache()
        {
            updateScheduler = new PrioritisedTaskScheduler(nameof(BeatmapDifficultyCache), taskPriority);
        }

        /// <summary>
        /// All bindables that should be updated along with the current ruleset + mods.
        /// </summary>
        private readonly WeakList<BindableStarDifficulty> trackedBindables = new WeakList<BindableStarDifficulty>();

        /// <summary>
        /// Cancellation sources used by tracked bindables.
        /// </summary>
        private readonly List<CancellationTokenSource> linkedCancellationSources = new List<CancellationTokenSource>();

        /// <summary>
        /// Lock to be held when operating on <see cref="trackedBindables"/> or <see cref="linkedCancellationSources"/>.
        /// </summary>
        private readonly Lock bindableUpdateLock = new Lock();

        private CancellationTokenSource trackedUpdateCancellationSource = new CancellationTokenSource();

        [Resolved]
        private BeatmapManager beatmapManager { get; set; } = null!;

        [Resolved]
        private Bindable<RulesetInfo> currentRuleset { get; set; } = null!;

        [Resolved]
        private Bindable<IReadOnlyList<Mod>> currentMods { get; set; } = null!;

        [Resolved]
        private IBindable<WorkingBeatmap>? gameBeatmap { get; set; }

        [Resolved]
        private ILocalUserPlayInfo? localUserPlayInfo { get; set; }

        private IBindable<LocalUserPlayingState>? playingState;

        private ModSettingChangeTracker? modSettingChangeTracker;
        private ScheduledDelegate? debouncedModSettingsChange;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            // Nothing is calculated while the user is playing: a calculation on another core still costs frame time and input latency.
            if (localUserPlayInfo != null)
            {
                playingState = localUserPlayInfo.PlayingState.GetBoundCopy();
                playingState.BindValueChanged(_ => updatePaused(), true);
            }

            // The beatmap the user opens jumps the queue, and cuts short the background work that is in its way.
            gameBeatmap?.BindValueChanged(b => onFocusChanged(b.NewValue?.BeatmapInfo), true);

            currentRuleset.BindValueChanged(_ => Scheduler.AddOnce(updateTrackedBindables));

            currentMods.BindValueChanged(mods =>
            {
                // A change in bindable here doesn't guarantee that mods have actually changed.
                // However, we *do* want to make sure that the mod *references* are the same;
                // `SequenceEqual()` without a comparer would fall back to `IEquatable`.
                // Failing to ensure reference equality can cause setting change tracking to fail later.
                if (mods.OldValue.SequenceEqual(mods.NewValue, ReferenceEqualityComparer.Instance))
                    return;

                modSettingChangeTracker?.Dispose();

                Scheduler.AddOnce(updateTrackedBindables);

                modSettingChangeTracker = new ModSettingChangeTracker(mods.NewValue);
                modSettingChangeTracker.SettingChanged += _ =>
                {
                    lock (bindableUpdateLock)
                    {
                        debouncedModSettingsChange?.Cancel();
                        debouncedModSettingsChange = Scheduler.AddDelayed(updateTrackedBindables, 100);
                    }
                };
            }, true);
        }

        /// <summary>
        /// Notify this cache that a beatmap has been invalidated/updated.
        /// </summary>
        /// <param name="oldBeatmap">The old beatmap model.</param>
        /// <param name="newBeatmap">The updated beatmap model.</param>
        public void Invalidate(IBeatmapInfo oldBeatmap, IBeatmapInfo newBeatmap)
        {
            base.Invalidate(lookup => lookup.BeatmapInfo.Equals(oldBeatmap));

            lock (bindableUpdateLock)
            {
                bool trackedBindablesRefreshRequired = false;

                foreach (var bsd in trackedBindables.Where(bsd => bsd.BeatmapInfo.Equals(oldBeatmap)))
                {
                    bsd.BeatmapInfo = newBeatmap;
                    trackedBindablesRefreshRequired = true;
                }

                if (trackedBindablesRefreshRequired)
                    Scheduler.AddOnce(updateTrackedBindables);
            }
        }

        /// <summary>
        /// Retrieves a bindable containing the star difficulty of a <see cref="BeatmapInfo"/> that follows the currently-selected ruleset and mods.
        /// </summary>
        /// <param name="beatmapInfo">The <see cref="BeatmapInfo"/> to get the difficulty of.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> which stops updating the star difficulty for the given <see cref="BeatmapInfo"/>.</param>
        /// <param name="computationDelay">A delay in milliseconds before performing the </param>
        /// <returns>A bindable that is updated to contain the star difficulty when it becomes available. May be an approximation while in an initial calculating state.</returns>
        public IBindable<StarDifficulty> GetBindableDifficulty(IBeatmapInfo beatmapInfo, CancellationToken cancellationToken = default, int computationDelay = 0)
        {
            var bindable = new BindableStarDifficulty(beatmapInfo, cancellationToken)
            {
                // Start with an approximate known value instead of zero.
                Value = new StarDifficulty(beatmapInfo.StarRating, 0)
            };

            lock (bindableUpdateLock)
            {
                var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(trackedUpdateCancellationSource.Token, cancellationToken);
                linkedCancellationSources.Add(linkedSource);

                updateBindable(bindable, currentRuleset.Value, currentMods.Value, linkedSource, computationDelay);

                trackedBindables.Add(bindable);
            }

            return bindable;
        }

        /// <summary>
        /// Retrieves the difficulty of a <see cref="IBeatmapInfo"/>.
        /// </summary>
        /// <param name="beatmapInfo">The <see cref="IBeatmapInfo"/> to get the difficulty of.</param>
        /// <param name="rulesetInfo">The <see cref="IRulesetInfo"/> to get the difficulty with.</param>
        /// <param name="mods">The <see cref="Mod"/>s to get the difficulty with.</param>
        /// <param name="cancellationToken">An optional <see cref="CancellationToken"/> which stops computing the star difficulty.</param>
        /// <param name="computationDelay">In the case a cached lookup was not possible, a value in milliseconds of to wait until performing potentially intensive lookup.</param>
        /// <returns>
        /// The requested <see cref="StarDifficulty"/>, if non-<see langword="null"/>.
        /// A <see langword="null"/> return value indicates that the difficulty process failed or was interrupted early,
        /// and as such there is no usable star difficulty value to be returned.
        /// </returns>
        public virtual Task<StarDifficulty?> GetDifficultyAsync(IBeatmapInfo beatmapInfo, IRulesetInfo? rulesetInfo = null, IEnumerable<Mod>? mods = null,
                                                                CancellationToken cancellationToken = default, int computationDelay = 0)
        {
            // In the case that the user hasn't given us a ruleset, use the beatmap's default ruleset.
            rulesetInfo ??= beatmapInfo.Ruleset;

            var localBeatmapInfo = beatmapInfo as BeatmapInfo;
            var localRulesetInfo = rulesetInfo as RulesetInfo;

            // Difficulty can only be computed if the beatmap and ruleset are locally available.
            if (localBeatmapInfo == null || localRulesetInfo == null)
            {
                // If not, fall back to the existing star difficulty (e.g. from an online source).
                return Task.FromResult<StarDifficulty?>(new StarDifficulty(beatmapInfo.StarRating, (beatmapInfo as IBeatmapOnlineInfo)?.MaxCombo ?? 0));
            }

            return GetAsync(new DifficultyCacheLookup(localBeatmapInfo, localRulesetInfo, mods), cancellationToken, computationDelay);
        }

        protected override async Task<StarDifficulty?> ComputeValueAsync(DifficultyCacheLookup lookup, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var job = new DifficultyJob(lookup);

                var result = await Task.Factory.StartNew(state => runJob((DifficultyJob)state!, cancellationToken),
                    job,
                    cancellationToken,
                    TaskCreationOptions.HideScheduler | TaskCreationOptions.RunContinuationsAsynchronously,
                    updateScheduler).ConfigureAwait(false);

                // The calculation was cut short to make way for something more urgent (or because gameplay started), not because its
                // owner lost interest. Its result is still wanted: queue it again. It goes behind whatever interrupted it,
                // and does not start until the scheduler is allowed to run.
                if (job.WasPreempted && !cancellationToken.IsCancellationRequested)
                    continue;

                return result;
            }
        }

        protected override bool CacheNullValues => false;

        public Task<List<TimedDifficultyAttributes>> GetTimedDifficultyAttributesAsync(IWorkingBeatmap beatmap, Ruleset ruleset, Mod[] mods, CancellationToken cancellationToken = default)
        {
            return Task.Factory.StartNew(() => ruleset.CreateDifficultyCalculator(beatmap).CalculateTimed(mods, cancellationToken),
                cancellationToken,
                TaskCreationOptions.HideScheduler | TaskCreationOptions.RunContinuationsAsynchronously,
                timedUpdateScheduler);
        }

        /// <summary>
        /// Runs one calculation on the worker thread.
        /// </summary>
        private StarDifficulty? runJob(DifficultyJob job, CancellationToken cancellationToken)
        {
            if (CheckExists(job.Lookup, out var existing))
                return existing;

            using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, job.PreemptionSource.Token))
            {
                runningJob = job;

                try
                {
                    var result = computeDifficulty(job.Lookup, linkedSource.Token);

                    // computeDifficulty reports any cancellation as a null result. It was a preemption if that is what cancelled it.
                    if (result == null && job.PreemptionSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        job.WasPreempted = true;

                    return result;
                }
                finally
                {
                    runningJob = null;
                }
            }
        }

        /// <summary>
        /// How urgent a queued calculation is. Called by the scheduler each time it picks what to do next.
        /// </summary>
        private int taskPriority(Task task) => task.AsyncState is DifficultyJob job && isFocused(job.Lookup) ? 1 : 0;

        private bool isFocused(DifficultyCacheLookup lookup)
        {
            var focus = focusedBeatmap;
            return focus != null && lookup.BeatmapInfo.Equals(focus);
        }

        private void onFocusChanged(BeatmapInfo? beatmap)
        {
            focusedBeatmap = beatmap;

            if (beatmap == null)
                return;

            // Already known: nothing to hurry for.
            if (CheckExists(new DifficultyCacheLookup(beatmap, currentRuleset.Value, currentMods.Value), out _))
                return;

            // The scheduler will pick the selected beatmap next; this only stops it from waiting for a calculation of something else
            // that happens to be running right now. That calculation is queued again afterwards.
            preemptRunningJob(onlyIfNotFocused: true);
        }

        private void updatePaused()
        {
            bool playing = playingState != null && playingState.Value != LocalUserPlayingState.NotPlaying;

            updateScheduler.Paused = playing;

            // Whatever is running stops now (and is queued again for after the play), instead of finishing in the middle of it.
            if (playing)
                preemptRunningJob(onlyIfNotFocused: false);
        }

        private void preemptRunningJob(bool onlyIfNotFocused)
        {
            var job = runningJob;

            if (job == null || (onlyIfNotFocused && isFocused(job.Lookup)))
                return;

            job.PreemptionSource.Cancel();
        }

        /// <summary>
        /// Updates all tracked <see cref="BindableStarDifficulty"/> using the current ruleset and mods.
        /// </summary>
        private void updateTrackedBindables()
        {
            lock (bindableUpdateLock)
            {
                cancelTrackedBindableUpdate();

                foreach (var b in trackedBindables)
                {
                    var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(trackedUpdateCancellationSource.Token, b.CancellationToken);
                    linkedCancellationSources.Add(linkedSource);

                    updateBindable(b, currentRuleset.Value, currentMods.Value, linkedSource);
                }
            }
        }

        /// <summary>
        /// Cancels the existing update of all tracked <see cref="BindableStarDifficulty"/> via <see cref="updateTrackedBindables"/>.
        /// </summary>
        private void cancelTrackedBindableUpdate()
        {
            lock (bindableUpdateLock)
            {
                debouncedModSettingsChange?.Cancel();
                debouncedModSettingsChange = null;

                trackedUpdateCancellationSource.Cancel();
                trackedUpdateCancellationSource = new CancellationTokenSource();

                foreach (var c in linkedCancellationSources)
                    c.Dispose();

                linkedCancellationSources.Clear();
            }
        }

        /// <summary>
        /// Updates the value of a <see cref="BindableStarDifficulty"/> with a given ruleset + mods.
        /// </summary>
        /// <param name="bindable">The <see cref="BindableStarDifficulty"/> to update.</param>
        /// <param name="rulesetInfo">The <see cref="IRulesetInfo"/> to update with.</param>
        /// <param name="mods">The <see cref="Mod"/>s to update with.</param>
        /// <param name="linkedCancellationTokenSource">
        /// A cancellation token source that may be used to cancel this update.
        /// This token will be cancelled in one of two scenarios:
        /// <list type="bullet">
        /// <item>The owner of the bindable has requested the cancellation.</item>
        /// <item>An <see cref="Invalidate"/> call has been issued, and as such ongoing calculations must be aborted to avoid stale values being potentially written to bindables.</item>
        /// </list>
        /// </param>
        /// <param name="computationDelay">In the case a cached lookup was not possible, a value in milliseconds of to wait until performing potentially intensive lookup.</param>
        private void updateBindable(BindableStarDifficulty bindable, IRulesetInfo? rulesetInfo, IEnumerable<Mod>? mods, CancellationTokenSource linkedCancellationTokenSource, int computationDelay = 0)
        {
            // GetDifficultyAsync will fall back to existing data from IBeatmapInfo if not locally available
            // (contrary to GetAsync)
            GetDifficultyAsync(bindable.BeatmapInfo, rulesetInfo, mods, linkedCancellationTokenSource.Token, computationDelay)
                .ContinueWith(task =>
                    {
                        // We're on a threadpool thread, but we should exit back to the update thread so consumers can safely handle value-changed events.
                        Schedule(() =>
                        {
                            if (!linkedCancellationTokenSource.IsCancellationRequested)
                            {
                                StarDifficulty? starDifficulty = task.GetResultSafely();

                                if (starDifficulty != null)
                                    bindable.Value = starDifficulty.Value;
                            }

                            // Once the linked cancellation token source is of no remaining use to anybody, clean it up.
                            lock (bindableUpdateLock)
                            {
                                linkedCancellationSources.Remove(linkedCancellationTokenSource);
                                linkedCancellationTokenSource.Dispose();
                            }
                        });
                    },
                    // This continuation MUST run even if the antecedent `GetDifficultyAsync()` call was canceled in order to clean up `linkedCancellationTokenSource`.
                    // Due to this, `ContinueWith()` CANNOT accept `linkedCancellationTokenSource.Token` here, because if it did, then in an event of a cancellation,
                    // the continuation would never be scheduled for execution.
                    CancellationToken.None);
        }

        /// <summary>
        /// Computes the difficulty defined by a <see cref="DifficultyCacheLookup"/> key, and stores it to the timed cache.
        /// </summary>
        /// <param name="key">The <see cref="DifficultyCacheLookup"/> that defines the computation parameters.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The <see cref="StarDifficulty"/>.</returns>
        private StarDifficulty? computeDifficulty(in DifficultyCacheLookup key, CancellationToken cancellationToken = default)
        {
            // In the case that the user hasn't given us a ruleset, use the beatmap's default ruleset.
            var beatmapInfo = key.BeatmapInfo;
            var rulesetInfo = key.Ruleset;

            try
            {
                var ruleset = rulesetInfo.CreateInstance();
                Debug.Assert(ruleset != null);

                PlayableCachedWorkingBeatmap workingBeatmap = new PlayableCachedWorkingBeatmap(beatmapManager.GetWorkingBeatmap(key.BeatmapInfo));
                IBeatmap playableBeatmap = workingBeatmap.GetPlayableBeatmap(ruleset.RulesetInfo, key.OrderedMods, cancellationToken);

                var difficulty = ruleset.CreateDifficultyCalculator(workingBeatmap).Calculate(key.OrderedMods, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var performanceCalculator = ruleset.CreatePerformanceCalculator();
                if (performanceCalculator == null)
                    return new StarDifficulty(difficulty, new PerformanceAttributes());

                ScoreProcessor scoreProcessor = ruleset.CreateScoreProcessor();
                scoreProcessor.Mods.Value = key.OrderedMods;
                scoreProcessor.ApplyBeatmap(playableBeatmap);
                cancellationToken.ThrowIfCancellationRequested();

                ScoreInfo perfectScore = new ScoreInfo(key.BeatmapInfo, ruleset.RulesetInfo)
                {
                    Passed = true,
                    Accuracy = 1,
                    Mods = key.OrderedMods,
                    MaxCombo = scoreProcessor.MaximumCombo,
                    Combo = scoreProcessor.MaximumCombo,
                    TotalScore = scoreProcessor.MaximumTotalScore,
                    Statistics = scoreProcessor.MaximumStatistics,
                    MaximumStatistics = scoreProcessor.MaximumStatistics
                };

                var performance = performanceCalculator.Calculate(perfectScore, difficulty);
                cancellationToken.ThrowIfCancellationRequested();

                // The same difficulty attributes are reused: a performance calculation is cheap next to the difficulty one it follows,
                // so having all the accuracies costs next to nothing and does not need a second pass over the beatmap.
                var atAccuracy = new PerformanceAttributes?[StarDifficulty.ADDITIONAL_PERFORMANCE_ACCURACIES.Length];

                for (int i = 0; i < atAccuracy.Length; i++)
                {
                    atAccuracy[i] = calculatePerformanceAtAccuracy(performanceCalculator, scoreProcessor, ruleset, perfectScore, difficulty, StarDifficulty.ADDITIONAL_PERFORMANCE_ACCURACIES[i]);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return new StarDifficulty(difficulty, performance, atAccuracy);
            }
            catch (OperationCanceledException)
            {
                // no need to log, cancellations are expected as part of normal operation.
                return null;
            }
            catch (BeatmapInvalidForRulesetException invalidForRuleset)
            {
                if (rulesetInfo.Equals(beatmapInfo.Ruleset))
                    Logger.Error(invalidForRuleset, $"Failed to convert {beatmapInfo.OnlineID} to the beatmap's default ruleset ({beatmapInfo.Ruleset}).");

                return null;
            }
            catch (Exception unknownException)
            {
                Logger.Error(unknownException, "Failed to calculate beatmap difficulty");

                return null;
            }
        }

        /// <summary>
        /// The performance of the beatmap played at the given accuracy, or <see langword="null"/> if the ruleset cannot say.
        /// </summary>
        private static PerformanceAttributes? calculatePerformanceAtAccuracy(PerformanceCalculator calculator, ScoreProcessor scoreProcessor, Ruleset ruleset, ScoreInfo perfectScore,
                                                                             DifficultyAttributes difficulty, double accuracy)
        {
            try
            {
                var statistics = AccuracyStatistics.Generate(perfectScore.MaximumStatistics, accuracy, scoreProcessor.GetBaseScoreForResult, ruleset.GetValidHitResults());
                double actualAccuracy = AccuracyStatistics.Compute(statistics, perfectScore.MaximumStatistics, scoreProcessor.GetBaseScoreForResult);

                var score = new ScoreInfo(perfectScore.BeatmapInfo, perfectScore.Ruleset)
                {
                    Passed = true,
                    Accuracy = actualAccuracy,
                    Mods = perfectScore.Mods,
                    // Nothing is missed on the way down, so the combo stays whole.
                    MaxCombo = perfectScore.MaxCombo,
                    Combo = perfectScore.Combo,
                    TotalScore = (long)(perfectScore.TotalScore * actualAccuracy),
                    Statistics = statistics,
                    MaximumStatistics = perfectScore.MaximumStatistics
                };

                return calculator.Calculate(score, difficulty);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger.Log($"Could not estimate performance at {accuracy:P0}: {e.Message}", LoggingTarget.Runtime, LogLevel.Debug);
                return null;
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            modSettingChangeTracker?.Dispose();

            cancelTrackedBindableUpdate();
            updateScheduler.Dispose();
            timedUpdateScheduler.Dispose();
        }

        /// <summary>
        /// One queued or running calculation. Used by the scheduler to rank it, and to interrupt it.
        /// </summary>
        private sealed class DifficultyJob
        {
            public readonly DifficultyCacheLookup Lookup;

            /// <summary>
            /// Cancelled to stop this calculation without its owner having given up on it.
            /// </summary>
            public readonly CancellationTokenSource PreemptionSource = new CancellationTokenSource();

            /// <summary>
            /// Whether the calculation ended early because of <see cref="PreemptionSource"/>, and so has to be run again.
            /// </summary>
            public volatile bool WasPreempted;

            public DifficultyJob(DifficultyCacheLookup lookup)
            {
                Lookup = lookup;
            }
        }

        public readonly struct DifficultyCacheLookup : IEquatable<DifficultyCacheLookup>
        {
            public readonly BeatmapInfo BeatmapInfo;
            public readonly RulesetInfo Ruleset;
            public readonly Mod[] OrderedMods;

            public DifficultyCacheLookup(BeatmapInfo beatmapInfo, RulesetInfo? ruleset, IEnumerable<Mod>? mods)
            {
                BeatmapInfo = beatmapInfo;
                // In the case that the user hasn't given us a ruleset, use the beatmap's default ruleset.
                Ruleset = ruleset ?? BeatmapInfo.Ruleset;
                OrderedMods = mods?.OrderBy(m => m.Acronym).Select(mod => mod.DeepClone()).ToArray() ?? Array.Empty<Mod>();
            }

            public bool Equals(DifficultyCacheLookup other)
                => BeatmapInfo.Equals(other.BeatmapInfo)
                   && Ruleset.Equals(other.Ruleset)
                   && OrderedMods.SequenceEqual(other.OrderedMods);

            public override int GetHashCode()
            {
                var hashCode = new HashCode();

                hashCode.Add(BeatmapInfo.ID);
                hashCode.Add(Ruleset.ShortName);

                foreach (var mod in OrderedMods)
                    hashCode.Add(mod);

                return hashCode.ToHashCode();
            }
        }

        private class BindableStarDifficulty : Bindable<StarDifficulty>
        {
            public IBeatmapInfo BeatmapInfo;
            public readonly CancellationToken CancellationToken;

            public BindableStarDifficulty(IBeatmapInfo beatmapInfo, CancellationToken cancellationToken)
            {
                BeatmapInfo = beatmapInfo;
                CancellationToken = cancellationToken;
            }
        }

        /// <summary>
        /// A working beatmap that caches its playable representation.
        /// This is intended as single-use for when it is guaranteed that the playable beatmap can be reused.
        /// </summary>
        private class PlayableCachedWorkingBeatmap : IWorkingBeatmap
        {
            private readonly IWorkingBeatmap working;
            private IBeatmap? playable;

            public PlayableCachedWorkingBeatmap(IWorkingBeatmap working)
            {
                this.working = working;
            }

            public IBeatmap GetPlayableBeatmap(IRulesetInfo ruleset, IReadOnlyList<Mod> mods)
                => playable ??= working.GetPlayableBeatmap(ruleset, mods);

            public IBeatmap GetPlayableBeatmap(IRulesetInfo ruleset, IReadOnlyList<Mod> mods, CancellationToken cancellationToken)
                => playable ??= working.GetPlayableBeatmap(ruleset, mods, cancellationToken);

            IBeatmapInfo IWorkingBeatmap.BeatmapInfo => working.BeatmapInfo;
            bool IWorkingBeatmap.BeatmapLoaded => working.BeatmapLoaded;
            bool IWorkingBeatmap.TrackLoaded => working.TrackLoaded;
            IBeatmap IWorkingBeatmap.Beatmap => working.Beatmap;
            Texture IWorkingBeatmap.GetBackground() => working.GetBackground();
            Texture IWorkingBeatmap.GetPanelBackground() => working.GetPanelBackground();
            Waveform IWorkingBeatmap.Waveform => working.Waveform;
            Storyboard IWorkingBeatmap.Storyboard => working.Storyboard;
            ISkin IWorkingBeatmap.Skin => working.Skin;
            Track IWorkingBeatmap.Track => working.Track;
            Track IWorkingBeatmap.LoadTrack() => working.LoadTrack();
            Stream IWorkingBeatmap.GetStream(string storagePath) => working.GetStream(storagePath);
            void IWorkingBeatmap.BeginAsyncLoad() => working.BeginAsyncLoad();
            void IWorkingBeatmap.CancelAsyncLoad() => working.CancelAsyncLoad();
            void IWorkingBeatmap.PrepareTrackForPreview(bool looping, double? offsetFromPreviewPoint) => working.PrepareTrackForPreview(looping, offsetFromPreviewPoint);
        }
    }
}
