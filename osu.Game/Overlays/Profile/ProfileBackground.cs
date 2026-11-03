// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Graphics;
using osuTK.Graphics;

namespace osu.Game.Overlays.Profile
{
    /// <summary>
    /// The picture a supporter chose as the background of their profile page on the website, drawn behind the whole
    /// profile overlay and darkened so the text on top stays readable.
    /// </summary>
    public partial class ProfileBackground : CompositeDrawable
    {
        private Drawable? current;
        private string? requestedUrl;

        public ProfileBackground()
        {
            RelativeSizeAxes = Axes.Both;
        }

        /// <summary>
        /// Shows the picture at <paramref name="url"/>, replacing the previous one. A null or empty url clears it.
        /// </summary>
        public void SetUrl(string? url)
        {
            if (url == requestedUrl)
                return;

            requestedUrl = url;

            current?.FadeOut(200).Expire();
            current = null;

            if (string.IsNullOrEmpty(url))
                return;

            string toLoad = url;

            LoadComponentAsync(new Picture(toLoad), picture =>
            {
                // A different profile may have been opened while this one was downloading.
                if (requestedUrl != toLoad)
                {
                    picture.Dispose();
                    return;
                }

                current = picture;
                AddInternal(picture);
                picture.FadeInFromZero(400);
            });
        }

        [LongRunningLoad]
        private partial class Picture : CompositeDrawable
        {
            private readonly string url;

            public Picture(string url)
            {
                this.url = url;

                RelativeSizeAxes = Axes.Both;
            }

            [BackgroundDependencyLoader]
            private void load(OnlineAssetCachingStore textures)
            {
                InternalChildren = new Drawable[]
                {
                    new Sprite
                    {
                        RelativeSizeAxes = Axes.Both,
                        Texture = textures.Get(url),
                        FillMode = FillMode.Fill,
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre
                    },
                    new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = ColourInfo.GradientVertical(Color4.Black.Opacity(0.55f), Color4.Black.Opacity(0.8f))
                    }
                };
            }
        }
    }
}
