// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Net.Http;
using Newtonsoft.Json;
using osu.Framework.IO.Network;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Scoring;

namespace osu.Game.Online.Rooms
{
    public abstract class SubmitScoreRequest : APIRequest<MultiplayerScore>
    {
        public readonly SoloScoreInfo Score;

        protected readonly long ScoreId;

        protected SubmitScoreRequest(ScoreInfo scoreInfo, long scoreId)
        {
            Score = SoloScoreInfo.ForSubmission(scoreInfo);
            ScoreId = scoreId;
        }

        protected virtual int BeatmapId => Score.BeatmapID;

        protected override WebRequest CreateWebRequest()
        {
            var req = base.CreateWebRequest();

            req.ContentType = "application/json";
            req.Method = HttpMethod.Put;
            req.Timeout = 30000;

            string payload = JsonConvert.SerializeObject(Score, new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            });

#if STRICT_CLIENT
            string? blockReason = osu.Game.Security.AntiCheatClient.GetScoreSubmissionBlockReason();

            if (blockReason != null)
                throw new System.Security.SecurityException($"Score submission blocked: {blockReason}");

            osu.Game.Security.AntiCheatClient.ReportScore(ScoreId, BeatmapId, payload);
#endif

            req.AddRaw(payload);

            return req;
        }
    }
}
