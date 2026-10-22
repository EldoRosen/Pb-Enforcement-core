using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Utils;
using Advanced_PB_Limiter.Utils.Reports;
using NLog;
using Sandbox.Game.World;
using Torch.Commands;
// ReSharper disable CommentTypo

namespace Advanced_PB_Limiter.Manager
{
    public static class ReportManager
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static ConcurrentDictionary<long, PlayerReport> LocalReports { get; } = new();
        private static readonly SemaphoreSlim ReportLock = new(1, 1);
        private static readonly ConcurrentDictionary<ulong, DateTime> LastPlayerReportRequest = new();

        // ALWAYS RUN THIS ASYNC
        public static async Task GetReport(CommandContext? context)
        {
            if (context is null)
            {
                Log.Warn("No requester context provided to create a report. Aborting...");
                return;
            }

            if (!await ReportLock.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Log.Info("Report generation already in progress... 5 second wait elapsed with no availability.  Aborting...");
                context.Respond("Report generator was busy with other requests.  Please try again in a few minutes.");
                return;
            }

            try
            {
                await GenerateLocalReportData();
                context.Respond(await FormatReport());
            }
            finally
            {
                LocalReports.Clear();
                ReportLock.Release();
            }
        }

        private static Task<string> FormatReport()
        {
            StringBuilder report = new();

            Tuple<double, string?, ulong?> hottestPb = new (0, null, null);  // ms, gridname, owner
            Tuple<double, ulong?> hottestPlayer = new (0, null);
            Tuple<double, ulong?> hottestPlayerAvg = new (0, null);
            Tuple<int, ulong?> playerWithMostPBs = new (0, null); // amount, playerSteamID
            Tuple<int, string?, ulong?> pbWithMostOffences = new (0, null, null); // offences, gridname, owner
            Tuple<int, ulong?> playerWithMostOffences = new (0, null); // amount, playerSteamID
            Tuple<int, string?, ulong?> pbWithMostRecompiles = new (0, null, null); // recompiles, gridname, owner
            int totalPbCount = 0;

            // First pass to find the hottest, most offensive, etc
            foreach (KeyValuePair<long, PlayerReport> playerReport in LocalReports)
            {
                List<PBReport> pbs = playerReport.Value.GetPBReports;

                if (pbs.Count > playerWithMostPBs.Item1)
                    playerWithMostPBs = new Tuple<int, ulong?>(pbs.Count, playerReport.Value.SteamId);

                if (playerReport.Value.TotalOffences > playerWithMostOffences.Item1)
                    playerWithMostOffences = new Tuple<int, ulong?>(playerReport.Value.TotalOffences, playerReport.Value.SteamId);

                double playersCombinedLastRunTimeMS = 0;
                double playersCombinedRunTimeMSAvg = 0;
                for (int index = 0; index < pbs.Count; index++)
                {
                    totalPbCount++;

                    if (pbs[index].LastRunTimeMS > hottestPb.Item1)
                        hottestPb = new Tuple<double, string?, ulong?>(pbs[index].LastRunTimeMS, pbs[index].GridName, pbs[index].OwnerSteamId);

                    if (pbs[index].Offences > pbWithMostOffences.Item1)
                        pbWithMostOffences = new Tuple<int, string?, ulong?>(pbs[index].Offences, pbs[index].GridName, pbs[index].OwnerSteamId);

                    if (pbs[index].Recompiles > pbWithMostRecompiles.Item1)
                        pbWithMostRecompiles = new Tuple<int, string?, ulong?>(pbs[index].Recompiles, pbs[index].GridName, pbs[index].OwnerSteamId);

                    playersCombinedLastRunTimeMS += pbs[index].LastRunTimeMS;
                    playersCombinedRunTimeMSAvg += pbs[index].RunTimeMSAvg;
                }

                double calculatedCombinedLastRunTimeMs = playersCombinedLastRunTimeMS / pbs.Count;
                if (calculatedCombinedLastRunTimeMs > hottestPlayer.Item1)
                    hottestPlayer = new Tuple<double, ulong?>(calculatedCombinedLastRunTimeMs, playerReport.Value.SteamId);

                double calculatedCombinedRunTimeMsAvg = playersCombinedRunTimeMSAvg / pbs.Count;
                if (calculatedCombinedRunTimeMsAvg > hottestPlayerAvg.Item1)
                    hottestPlayerAvg = new Tuple<double, ulong?>(calculatedCombinedRunTimeMsAvg, playerReport.Value.SteamId);
            }

            report.AppendLine(); // Blank line so report starts on a fresh line, seperate from normal chat or other messages.
            report.AppendLine(" >> PB Enforcement Core Report");
            report.AppendLine("-----------------------------");
            report.AppendLine(" >  Overall Server Stats");
            report.AppendLine($"    -  Total Players: {LocalReports.Count}");
            report.AppendLine($"    -  Total Programming Blocks Tracked: {totalPbCount}");
            report.AppendLine("");

            report.AppendLine(" >  Overall Player Stats");
            report.AppendLine("    -  Hottest Player Per Run: " + (hottestPlayer.Item2 == null ? "" : MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)hottestPlayer.Item2)));
            if (hottestPlayerAvg.Item2 is not null)
                report.AppendLine("    -  Hottest Player Per Run Avg: " + MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)hottestPlayerAvg.Item2));
            if (playerWithMostPBs.Item2 is not null)
                report.AppendLine("    -  Player With Most PBs: " + MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)playerWithMostPBs.Item2));
            if (playerWithMostOffences.Item2 is not null)
                report.AppendLine("    -  Player With Most Offences: " + MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)playerWithMostOffences.Item2));
            if (pbWithMostOffences.Item3 is not null)
                report.AppendLine("    -  Player With Most Offensive PB: - " + MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)pbWithMostOffences.Item3));
            if (pbWithMostRecompiles.Item3 is not null)
                report.AppendLine("    -  Player With Most Recompiled PB: " + MySession.Static.Players.TryGetIdentityNameFromSteamId((ulong)pbWithMostRecompiles.Item3));
            report.AppendLine("");

            report.AppendLine(" >  Per Player Stats");
            foreach (KeyValuePair<long, PlayerReport> playerReport in LocalReports)
            {
                report.AppendLine($"    -  Player Name: {playerReport.Value.PlayerName}");
                if (playerReport.Value.IsPrivileged) report.AppendLine("    -  Player is Privileged");
                report.AppendLine(MySession.Static.Players.IdentityIsNpc(playerReport.Value.PlayerId)
                    ? "    -  Player is NPC"
                    : $"    -  Player SteamID: {playerReport.Value.SteamId}");
                report.AppendLine($"    -  Player PB Count: {playerReport.Value.GetPBReports.Count}");
                report.AppendLine($"    -  Player Total Offences: {playerReport.Value.TotalOffences}");
                report.AppendLine($"    -  Player Total Recompiles: {playerReport.Value.TotalRecompiles}");
                report.AppendLine($"    -  Player Combined Runtime: {playerReport.Value.CombinedLastRunTimeMS:0.0000}");
                report.AppendLine($"    -  Player Combined Runtime Avg: {playerReport.Value.CombinedRunTimeMSAvg:0.0000}");
                report.AppendLine("    >  Per Programmable Block Stats");
                for (int index = 0; index < playerReport.Value.GetPBReports.Count; index++)
                {
                    report.AppendLine($"       -  Grid Name: {playerReport.Value.GetPBReports[index].GridName}");
                    report.AppendLine($"       -  Block Name: {playerReport.Value.GetPBReports[index].BlockName}");
                    report.AppendLine($"       -  Grid Last Runtime: {playerReport.Value.GetPBReports[index].LastRunTimeMS:0.0000}");
                    report.AppendLine($"       -  Grid Runtime Avg: {playerReport.Value.GetPBReports[index].RunTimeMSAvg:0.0000}");
                    report.AppendLine($"       -  Grid Offences: {playerReport.Value.GetPBReports[index].Offences}");
                    report.AppendLine($"       -  Grid Recompiles: {playerReport.Value.GetPBReports[index].Recompiles}");
                    report.AppendLine("");
                }
            }

            report.AppendLine("-----------------------------");
            report.AppendLine("END OF REPORT");
            return Task.FromResult(report.ToString());
        }

        private static Task GenerateLocalReportData()
        {
            List<TrackedPlayer> list = TrackingManager.GetTrackedPlayerData();
            for (int index = 0; index < list.Count; index++)
            {
                List<PBReport> pbReports = new();
                List<TrackedPBBlock> trackedBlocks = list[index].GetAllPBBlocks;
                for (int ii = 0; ii < trackedBlocks.Count; ii++)
                {
                    TrackedPBBlock tracked = trackedBlocks[ii];
                    if (tracked is null || tracked.ProgrammableBlock == null) continue;

                    PBReport pbReport = new(list[index].SteamId,
                        tracked.ProgrammableBlock.CubeGrid?.DisplayName ?? tracked.GridName,
                        tracked.ProgrammableBlock.CustomName.ToString(),
                        tracked.LastRunTimeMS,
                        tracked.RunTimeMSAvgSoFar, // the enforced average stays 0 until MaxRunsToTrack runs, which made reports read 0 for most blocks
                        tracked.Offences.Count,
                        tracked.Recompiles);

                    pbReports.Add(pbReport);
                }

                PlayerReport playerReport = new(list[index].SteamId, list[index].PlayerId, list[index].PlayerName ?? "Unknown", pbReports);
                LocalReports.TryAdd(playerReport.PlayerId, playerReport); // by identity: NPCs and unresolved owners all share Steam id 0
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Collects the current player/PB report data and hands it back without formatting it.
        /// Returns null if the report generator stays busy. ALWAYS RUN THIS ASYNC.
        /// </summary>
        public static async Task<List<PlayerReport>?> GetOffenceSnapshot()
        {
            if (!await ReportLock.WaitAsync(TimeSpan.FromSeconds(5))) return null;

            try
            {
                await GenerateLocalReportData();
                return new List<PlayerReport>(LocalReports.Values);
            }
            finally
            {
                LocalReports.Clear();
                ReportLock.Release();
            }
        }

        public static async Task PlayerStatsRequest(CommandContext context)
        {
            if (!LastPlayerReportRequest.TryGetValue(context.Player.SteamUserId, out DateTime lastRequest))
                LastPlayerReportRequest.TryAdd(context.Player.SteamUserId, DateTime.Now);
            
            if (DateTime.Now.Subtract(lastRequest).TotalSeconds < 30)
            {
                context.Respond("You can only request player stats once every 30 seconds.");
                return;
            } 
            LastPlayerReportRequest[context.Player.SteamUserId] = DateTime.Now;

            if (!await ReportLock.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                context.Respond("The server has too many reports being generated at once.  Please try again in a few minutes.");
                return;
            }

            try
            {
            Stopwatch sw = Stopwatch.StartNew();
            await GenerateLocalReportData();
            if (LocalReports.TryGetValue(context.Player.IdentityId, out PlayerReport playerReport))
            {
                StringBuilder report = new();
                report.AppendLine();
                report.AppendLine(" >> PB Enforcement Core Player Report");
                report.AppendLine("-----------------------------");
                report.AppendLine(" >  Overall Player Stats");
                report.AppendLine($"    -  PB Count: {playerReport.GetPBReports.Count}");
                report.AppendLine($"    -  Total Offences: {playerReport.TotalOffences}");
                report.AppendLine($"    -  Total Recompiles: {playerReport.TotalRecompiles}");
                report.AppendLine($"    -  Combined Runtime: {playerReport.CombinedLastRunTimeMS:0.0000}");
                report.AppendLine($"    -  Combined Runtime Avg: {playerReport.CombinedRunTimeMSAvg:0.0000}");
                report.AppendLine("");
                report.AppendLine(" >  Per Programmable Block Stats");
                for (int index = 0; index < playerReport.GetPBReports.Count; index++)
                {
                    report.AppendLine($"    -  Grid Name: {playerReport.GetPBReports[index].GridName}");
                    report.AppendLine($"    -  Block Name: {playerReport.GetPBReports[index].BlockName}");
                    report.AppendLine($"    -  Last Runtime: {playerReport.GetPBReports[index].LastRunTimeMS:0.0000}");
                    report.AppendLine($"    -  Runtime Avg: {playerReport.GetPBReports[index].RunTimeMSAvg:0.0000}");
                    report.AppendLine($"    -  Offences: {playerReport.GetPBReports[index].Offences}");
                    report.AppendLine($"    -  Recompiles: {playerReport.GetPBReports[index].Recompiles}");
                    report.AppendLine("");
                }
                report.AppendLine("-----------------------------");
                report.AppendLine("END OF REPORT");
                context.Respond(report.ToString());
                sw.Stop();
                Log.Info($"{playerReport.PlayerName} requested a player report, generated in {sw.Elapsed.TotalMilliseconds:0.0000}ms.");
                return;
            }
            
            context.Respond("No report data found for you.  This could be because you have no programmable blocks or they are not running.");
            }
            finally
            {
                LocalReports.Clear();
                ReportLock.Release();
            }
        }

        public static Task PlayerRuntimesRequest(CommandContext context)
        {
            StringBuilder sb = new();
            TrackedPlayer? player = TrackingManager.GetTrackedPlayerDataById(context.Player.IdentityId);
            
            if (player is null)
            {
                sb.AppendLine("No programmable blocks found for you.  This could be because you have no programmable blocks or they are not running.");
                context.Respond(sb.ToString());
                return Task.CompletedTask;
            }

            foreach (TrackedPBBlock pbBlock in player.GetAllPBBlocks)
            {
                sb.AppendLine($"Grid Name: {pbBlock.ProgrammableBlock!.CubeGrid?.DisplayName ?? pbBlock.GridName}");
                sb.AppendLine($"Block Name: {pbBlock.ProgrammableBlock.DisplayName}");
                foreach (double runtime in pbBlock.GetRunTimesMS)
                {
                    sb.AppendLine($" - {runtime:0.0000}ms");
                }               
                sb.AppendLine("-----------------------------");
            }
            
            context.Respond(sb.ToString());
            return Task.CompletedTask;
        }
    }
}