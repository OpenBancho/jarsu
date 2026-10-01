// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Game.Online
{
    public class CustomEndpointConfiguration : EndpointConfiguration
    {
        public CustomEndpointConfiguration(string url)
        {
            url = (url ?? string.Empty).Trim().TrimEnd('/');

            // Local development servers run plain HTTP, never HTTPS
            if (url.Contains("localhost", StringComparison.OrdinalIgnoreCase) || url.Contains("127.0.0.1"))
            {
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = "http://" + url.Substring(8);
                else if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    url = "http://" + url;
            }
            else
            {
                if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = "https://" + url;
            }

            WebsiteUrl = APIUrl = url;
            APIClientSecret = @"FGc9GAtyHzeQDshWP5Ah7dega8hJACAJpQtw6OXk";
            APIClientID = "5";
            SpectatorUrl = $@"{APIUrl}/signalr/spectator";
            MultiplayerUrl = $@"{APIUrl}/signalr/multiplayer";
            MetadataUrl = $@"{APIUrl}/signalr/metadata";
            BeatmapSubmissionServiceUrl = $@"{APIUrl}/beatmap-submission";
        }
    }
}
