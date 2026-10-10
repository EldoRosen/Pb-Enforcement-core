using System;
using System.Collections.Generic;
using System.ComponentModel;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Sandbox.Game.Entities.Blocks;

namespace Advanced_PB_Limiter.Utils.Reports
{
    /// <summary>
    /// One row of the live view's "Per Programming Block" table. The row is updated in place (INotifyPropertyChanged, written
    /// by hand) so the table keeps its scroll position, selection and sorting while it refreshes.
    /// </summary>
    public sealed class CustomBlockReport : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ulong SteamId { get; private set; }
        public string PlayerName { get; private set; } = "";
        public string GridName { get; private set; } = "";
        public string? BlockName { get; private set; }
        public double LastRunTimeMS { get; private set; }
        public double AvgMS { get; private set; }
        public double PeekRunTimeMS { get; private set; }
        public int Offences { get; private set; }
        public int Recompiles { get; private set; }
        public string MemoryUsed { get; private set; } = "";
        public MyProgrammableBlock? pbBlock { get; private set; }

        public CustomBlockReport(ulong steamId, string playerName, string gridName, string blockName, double lastRunTimeMs, double avgMs, double peekRunTimeMS, int offences, int recompiles, long memoryUsed, MyProgrammableBlock? progBlock)
        {
            Update(steamId, playerName, gridName, blockName, lastRunTimeMs, avgMs, peekRunTimeMS, offences, recompiles, memoryUsed, progBlock);
        }

        /// <summary>Copies new values into this row and tells the table about every value that changed.</summary>
        public void Update(ulong steamId, string playerName, string gridName, string blockName, double lastRunTimeMs, double avgMs, double peekRunTimeMS, int offences, int recompiles, long memoryUsed, MyProgrammableBlock? progBlock)
        {
            if (SteamId != steamId) { SteamId = steamId; Raise(nameof(SteamId)); }
            if (PlayerName != playerName) { PlayerName = playerName; Raise(nameof(PlayerName)); }
            if (GridName != gridName) { GridName = gridName; Raise(nameof(GridName)); }
            if (BlockName != blockName) { BlockName = blockName; Raise(nameof(BlockName)); }

            double last = Math.Round(lastRunTimeMs, 6);
            if (LastRunTimeMS != last) { LastRunTimeMS = last; Raise(nameof(LastRunTimeMS)); }
            double avg = Math.Round(avgMs, 6);
            if (AvgMS != avg) { AvgMS = avg; Raise(nameof(AvgMS)); }
            if (PeekRunTimeMS != peekRunTimeMS) { PeekRunTimeMS = peekRunTimeMS; Raise(nameof(PeekRunTimeMS)); }
            if (Offences != offences) { Offences = offences; Raise(nameof(Offences)); }
            if (Recompiles != recompiles) { Recompiles = recompiles; Raise(nameof(Recompiles)); }

            string memory = HelperUtils.FormatBytesToKB(memoryUsed);
            if (MemoryUsed != memory) { MemoryUsed = memory; Raise(nameof(MemoryUsed)); }
            if (!ReferenceEquals(pbBlock, progBlock)) { pbBlock = progBlock; Raise(nameof(pbBlock)); }
        }
    }

    /// <summary>One row of the live view's "Per Player" table. Updated in place, like <see cref="CustomBlockReport"/>.</summary>
    public sealed class CustomPlayerReport : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ulong SteamId { get; private set; }
        public string PlayerName { get; private set; } = "";
        public double CombinedLastRunTimeMS { get; private set; }
        public double CombinedAvgMS { get; private set; }
        public int Offences { get; private set; }
        public int PbCount { get; private set; }
        public int Recompiles { get; private set; }
        public string MemoryUsed { get; private set; } = "";
        public TrackedPlayer? PlayerTracked { get; private set; }

        public CustomPlayerReport(ulong steamId, string playerName, double combinedLastRunTimeMs, double combinedAvgMs, int offences, int pbCount, int recompiles, long memoryUsed, TrackedPlayer? playerTracked)
        {
            Update(steamId, playerName, combinedLastRunTimeMs, combinedAvgMs, offences, pbCount, recompiles, memoryUsed, playerTracked);
        }

        public void Update(ulong steamId, string playerName, double combinedLastRunTimeMs, double combinedAvgMs, int offences, int pbCount, int recompiles, long memoryUsed, TrackedPlayer? playerTracked)
        {
            if (SteamId != steamId) { SteamId = steamId; Raise(nameof(SteamId)); }
            if (PlayerName != playerName) { PlayerName = playerName; Raise(nameof(PlayerName)); }

            double last = Math.Round(combinedLastRunTimeMs, 4);
            if (CombinedLastRunTimeMS != last) { CombinedLastRunTimeMS = last; Raise(nameof(CombinedLastRunTimeMS)); }
            double avg = Math.Round(combinedAvgMs, 4);
            if (CombinedAvgMS != avg) { CombinedAvgMS = avg; Raise(nameof(CombinedAvgMS)); }
            if (Offences != offences) { Offences = offences; Raise(nameof(Offences)); }
            if (PbCount != pbCount) { PbCount = pbCount; Raise(nameof(PbCount)); }
            if (Recompiles != recompiles) { Recompiles = recompiles; Raise(nameof(Recompiles)); }

            string memory = HelperUtils.FormatBytesToKB(memoryUsed);
            if (MemoryUsed != memory) { MemoryUsed = memory; Raise(nameof(MemoryUsed)); }
            if (!ReferenceEquals(PlayerTracked, playerTracked)) { PlayerTracked = playerTracked; Raise(nameof(PlayerTracked)); }
        }
    }
    
    public sealed class PlayerReport
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public ulong SteamId { get; init; }
        public long PlayerId { get; init; }
        public string PlayerName { get; init; }
        public bool IsPrivileged { get; init; }
        private List<PBReport> PBReports { get; init;}

        public PlayerReport(ulong steamId, long playerId, string playerName, List<PBReport> pbReports)
        {
            SteamId = steamId;
            PlayerId = playerId;
            PlayerName = playerName;
            if (Config.PrivilegedPlayers.ContainsKey(steamId)) IsPrivileged = true;
            PBReports = pbReports;
        }
        
        public List<PBReport> GetPBReports => PBReports;

        public void AddPBReport(PBReport report)
        {
            PBReports.Add(report);
        }
        
        public void AddPBReportRange(IEnumerable<PBReport> reports)
        {
            PBReports.AddRange(reports);
        }

        public int TotalOffences
        {
            get
            {
                int offences = 0;
                for (int index = 0; index < PBReports.Count; index++)
                {
                    offences += PBReports[index].Offences;
                }

                return offences;
            }
        }

        public int TotalRecompiles
        {
            get
            {
                int recompiles = 0;
                for (int index = 0; index < PBReports.Count; index++)
                {
                    recompiles += PBReports[index].Recompiles;
                }

                return recompiles;
            }
        }

        public double CombinedLastRunTimeMS
        {
            get
            {
                double total = 0;
                for (int index = 0; index < PBReports.Count; index++)
                {
                    total += PBReports[index].LastRunTimeMS;
                }
                
                return total;
            }
        }

        public double CombinedRunTimeMSAvg
        {
            get
            {
                double total = 0;
                for (int index = 0; index < PBReports.Count; index++)
                {
                    total += PBReports[index].RunTimeMSAvg;
                }
                
                return total;
            }
        }
    }
    
    public record PBReport(ulong OwnerSteamId, string GridName, string BlockName, double LastRunTimeMS, double RunTimeMSAvg, int Offences, int Recompiles)
    {
        
    }
}