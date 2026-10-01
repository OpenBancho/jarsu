// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;

namespace osu.Game.Online
{
    public sealed class TrustedDomainOnlineStore : OnlineStore
    {
        public static bool AllowAllDomains { get; set; } = true;

        protected override string GetLookupUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                Logger.Log($@"Blocking resource lookup from invalid URL: {url}", LoggingTarget.Network, LogLevel.Important);
                return string.Empty;
            }

            if (AllowAllDomains || uri.Host.EndsWith(@".ppy.sh", StringComparison.OrdinalIgnoreCase))
                return url;

            Logger.Log($@"Blocking resource lookup from external website: {url}", LoggingTarget.Network, LogLevel.Important);
            return string.Empty;
        }
    }
}
