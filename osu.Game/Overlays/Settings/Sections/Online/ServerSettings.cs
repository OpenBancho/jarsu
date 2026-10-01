// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;

namespace osu.Game.Overlays.Settings.Sections.Online
{
    public partial class ServerSettings : SettingsSubsection
    {
        protected override LocalisableString Header => "DevServer";

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            Children = new Drawable[]
            {
                new SettingsItemV2(new FormTextBox
                {
                    Caption = "DevServer URL",
                    HintText = "e.g. http://localhost:8000 (NOT https) or https://lazer.domain.com. Restart required to apply.",
                    PlaceholderText = "Leave empty for official osu! servers",
                    Current = config.GetBindable<string>(OsuSetting.DevServerUrl)
                })
            };
        }
    }
}
