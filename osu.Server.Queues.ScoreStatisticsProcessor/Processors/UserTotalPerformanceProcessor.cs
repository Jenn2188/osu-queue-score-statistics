// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Computes the total performance points for users.
    /// </summary>
    public class UserTotalPerformanceProcessor : IProcessor
    {
        private BeatmapStore? beatmapStore;
        private BuildStore? buildStore;

        private long lastStoreRefresh;

        // This processor needs to run after the score's PP value has been processed.
        public int Order => ScorePerformanceProcessor.ORDER + 1;

        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => true;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(score.RulesetID);

            int warnings = conn.QuerySingleOrDefault<int>($"SELECT `user_warnings` FROM {dbInfo.UsersTable} WHERE `user_id` = @UserId", new
            {
                UserId = userStats.user_id
            }, transaction);

            if (warnings > 0)
                return;

            UpdateUserStatsAsync(userStats, score.RulesetID, conn, transaction).Wait();
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        /// <summary>
        /// Updates a user's stats with their total PP/accuracy.
        /// </summary>
        /// <remarks>
        /// This does not insert the new stats values into the database.
        /// </remarks>
        /// <param name="rulesetId">The ruleset for which to update the total PP.</param>
        /// <param name="userStats">An existing <see cref="UserStats"/> object to update with.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task UpdateUserStatsAsync(UserStats userStats, int rulesetId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var dbInfo = LegacyDatabaseHelper.GetRulesetSpecifics(rulesetId);
            long currentTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(rulesetId);

            // TODO: needs some kind of expiry mechanism for approved status changes.
            beatmapStore ??= await BeatmapStore.CreateAsync(connection, transaction);

            if (buildStore == null || currentTimestamp - lastStoreRefresh > 60)
            {
                buildStore = await BuildStore.CreateAsync(connection, transaction);
                lastStoreRefresh = currentTimestamp;
            }

            List<SoloScore> scores = (await connection.QueryAsync<SoloScore>(
                "SELECT beatmap_id, pp, accuracy FROM scores WHERE "
                + "`user_id` = @UserId AND "
                + "`ruleset_id` = @RulesetId AND"
                + " `pp` IS NOT NULL AND "
                + "`preserve` = 1 AND "
                + "`pass` = 1 AND "
                + "`ranked` = 1", new
                {
                    UserId = userStats.user_id,
                    RulesetId = rulesetId
                }, transaction: transaction)).ToList();

            Dictionary<uint, Beatmap?> beatmaps = new Dictionary<uint, Beatmap?>();

            foreach (var score in scores)
            {
                if (beatmaps.ContainsKey(score.beatmap_id))
                    continue;

                beatmaps[score.beatmap_id] = await beatmapStore.GetBeatmapAsync(score.beatmap_id, connection, transaction);
            }

            // Filter out invalid scores.
            scores.RemoveAll(s =>
            {
                // Score must have a valid pp.
                if (s.pp == null)
                    return true;

                // Score must be a pass (safeguard - should be redundant with preserve flag).
                if (!s.passed)
                    return true;

                // Beatmap must exist.
                if (!beatmaps.TryGetValue(s.beatmap_id, out var beatmap) || beatmap == null)
                    return true;

                // Given beatmap needs to be allowed to give performance.
                if (!beatmapStore.IsBeatmapValidForPerformance(beatmap, s.ruleset_id))
                    return true;

                // Legacy scores are always valid.
                if (s.legacy_score_id != null)
                    return false;

                // Some older lazer scores don't have build IDs.
                if (s.build_id == null)
                    return true;

                // Performance needs to be allowed for the build.
                if (buildStore.GetBuild(s.build_id.Value)?.allow_performance != true)
                    return true;

                return !ScorePerformanceProcessor.AllModsValidForPerformance(s.ToScoreInfo(), s.ScoreData.Mods.Select(m => m.ToMod(ruleset)).ToArray());
            });

            SoloScore[] groupedScores = scores
                                        // Group by beatmap ID.
                                        .GroupBy(i => i.beatmap_id)
                                        // Extract the maximum PP for each beatmap.
                                        .Select(g => g.OrderByDescending(i => i.pp).First())
                                        // And order the beatmaps by decreasing value.
                                        .OrderByDescending(i => i.pp)
                                        .ToArray();

            // Build the diminishing sum
            double factor = 1;
            double totalPp = 0;
            double totalAccuracy = 0;

            foreach (var score in groupedScores)
            {
                totalPp += score.pp!.Value * factor;
                totalAccuracy += score.accuracy * factor;
                factor *= 0.95;
            }

            // This weird factor is to keep legacy compatibility with the diminishing bonus of 0.25 by 0.9994 each score.
            totalPp += (417.0 - 1.0 / 3.0) * (1.0 - Math.Pow(0.9994, groupedScores.Length));

            // We want our accuracy to be normalized.
            if (groupedScores.Length > 0)
            {
                // We want the percentage, not a factor in [0, 1], hence we divide 20 by 100.
                totalAccuracy *= 100.0 / (20 * (1 - Math.Pow(0.95, groupedScores.Length)));
            }

            userStats.rank_score = (float)totalPp;
            userStats.rank_score_index = (await connection.QuerySingleAsync<int>($"SELECT COUNT(*) FROM {dbInfo.UserStatsTable} WHERE rank_score > {totalPp}", transaction: transaction)) + 1;
            userStats.accuracy_new = (float)totalAccuracy;
        }
    }
}
