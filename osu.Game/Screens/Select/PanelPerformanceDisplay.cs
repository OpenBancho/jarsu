// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Overlays;
using osuTK;

namespace osu.Game.Screens.Select
{
    /// <summary>
    /// The performance of a difficulty at 100%, 95% and 90% accuracy, as shown on a song select panel after the author.
    /// It shows nothing until the difficulty has been calculated, so a difficulty that was never calculated does not show a made-up number.
    /// </summary>
    public partial class PanelPerformanceDisplay : FillFlowContainer, IHasTooltip
    {
        private static readonly double[] accuracies = { 1.0, 0.95, 0.90 };

        private Entry[] entries = Array.Empty<Entry>();

        public LocalisableString TooltipText => "Performance at 100% / 95% / 90% accuracy with the selected mods. Estimates: a play with no misses beyond what the accuracy demands.";

        public PanelPerformanceDisplay()
        {
            Direction = FillDirection.Horizontal;
            AutoSizeAxes = Axes.Both;
            Spacing = new Vector2(10, 0);
            Alpha = 0;
        }

        [BackgroundDependencyLoader]
        private void load(OverlayColourProvider colourProvider)
        {
            entries = new Entry[accuracies.Length];

            for (int i = 0; i < entries.Length; i++)
                Add(entries[i] = new Entry($"{accuracies[i]:P0}", colourProvider));
        }

        /// <summary>
        /// Shows the performance recorded in a difficulty calculation.
        /// </summary>
        public void SetDifficulty(StarDifficulty difficulty)
        {
            double? perfect = difficulty.PerformanceAttributes?.Total;

            if (!isValid(perfect))
            {
                this.FadeOut(100);
                return;
            }

            // The first entry is the perfect play, the rest follow StarDifficulty.ADDITIONAL_PERFORMANCE_ACCURACIES.
            entries[0].SetPerformance(perfect);

            for (int i = 1; i < entries.Length; i++)
                entries[i].SetPerformance(difficulty.PerformanceAttributesAtAccuracy is { } atAccuracy && i - 1 < atAccuracy.Length ? atAccuracy[i - 1]?.Total : null);

            this.FadeIn(200);
        }

        private static bool isValid(double? performance) => performance is { } value && double.IsFinite(value) && value > 0;

        private partial class Entry : FillFlowContainer
        {
            private readonly OsuSpriteText value;

            public Entry(string accuracy, OverlayColourProvider colourProvider)
            {
                Direction = FillDirection.Horizontal;
                AutoSizeAxes = Axes.Both;
                Spacing = new Vector2(3, 0);
                Alpha = 0;

                Children = new Drawable[]
                {
                    new OsuSpriteText
                    {
                        Text = accuracy,
                        Colour = colourProvider.Content2.Opacity(0.7f),
                        Font = OsuFont.Style.Caption2.With(weight: FontWeight.SemiBold),
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                    },
                    value = new OsuSpriteText
                    {
                        Colour = colourProvider.Content2,
                        Font = OsuFont.Style.Caption1.With(weight: FontWeight.SemiBold),
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                    },
                };
            }

            public void SetPerformance(double? performance)
            {
                if (!isValid(performance))
                {
                    Alpha = 0;
                    return;
                }

                value.Text = LocalisableString.Interpolate($"{performance!.Value:N0}pp");
                Alpha = 1;
            }
        }
    }
}
