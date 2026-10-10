using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Timers;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Advanced_PB_Limiter.Utils
{
    public sealed class TrackedPlayer
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static Logger Log => LogManager.GetLogger("PB Enforcement Core - Tracked Player: ");
        public string? PlayerName { get; private set; }
        public long PlayerId { get; private set; }
        private ConcurrentDictionary<long, TrackedPBBlock> PBBlocks { get; } = new();
        public ulong SteamId { get; private set; }
        public long LastDataUpdateTick { get; set; }
        private readonly Timer _cleanupTimer = new (Math.Max(1, Config.RemoveInactivePBsAfterSeconds) * 1000);
        private int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
        public int Offences = 0;
        public List<long> CombinedOffences = new();
        
        public TrackedPlayer(long playerId)
        {
            PlayerName = MySession.Static.Players.TryGetIdentity(playerId)?.DisplayName ?? "Unknown";
            PlayerId = playerId;
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPBBlocks();
            _cleanupTimer.Start();
            SteamId = MySession.Static.Players.TryGetSteamId(playerId);
        }
        
        public int PBBlockCount => PBBlocks.Count;

        public TrackedPBBlock? GetTrackedPB(long entityId)
        {
            return PBBlocks.TryGetValue(entityId, out TrackedPBBlock? trackedPBBlock) 
                ? trackedPBBlock 
                : null;
        }
        
        public void UpdatePBBlockData(TrackingDataUpdateRequest data)
        {
            if (data.ProgBlock == null) return;

            // The name and Steam id are read once when the player is first tracked. If the game did not know them yet, look again
            // (a Steam id of 0 would hide a privileged player from the privileged list).
            if (SteamId == 0) SteamId = MySession.Static.Players.TryGetSteamId(PlayerId);
            if (string.IsNullOrEmpty(PlayerName) || PlayerName == "Unknown")
                PlayerName = MySession.Static.Players.TryGetIdentity(PlayerId)?.DisplayName ?? "Unknown";
            
            LastDataUpdateTick = Stopwatch.GetTimestamp();
            if (!PBBlocks.TryGetValue(data.ProgBlock.EntityId, out TrackedPBBlock? trackedPBBlock) || trackedPBBlock == null)
            {
                trackedPBBlock = new TrackedPBBlock(data.ProgBlock.CubeGrid.DisplayName, data.ProgBlock);
                PBBlocks.TryAdd(data.ProgBlock.EntityId, trackedPBBlock);
                return;
            }

            if (trackedPBBlock.IsUnderGracePeriod(MySession.Static.Players.TryGetSteamId(data.ProgBlock.OwnerId)))
                return;
            
            trackedPBBlock.AddRuntimeData(data.RunTime, SteamId);
            PunishmentManager.CheckForPunishment(this, trackedPBBlock.ProgrammableBlock!.EntityId, data.RunTime);
        }
        
        public List<TrackedPBBlock> GetAllPBBlocks
        {
            get
            {
                List<TrackedPBBlock> total = new();
                foreach (TrackedPBBlock pbBlock in PBBlocks.Values)
                {
                    if (pbBlock?.ProgrammableBlock is null) continue;
                    total.Add(pbBlock);
                }

                return total;
            }
        }
        
        public void GetAllPBBlocksLastRunTimeMS(Span<double> runtimeTimeMS)
        {
            int index = 0;
            foreach (KeyValuePair<long, TrackedPBBlock> kvp in PBBlocks)
            {
                if (index >= runtimeTimeMS.Length) break; // a block was added after the caller sized the buffer
                runtimeTimeMS[index++] = kvp.Value.LastRunTimeMS;
            }
        }
        
        public void GetAllPBBlocksMSAvg(Span<double> runtimeTimeMSAvg)
        {
            int index = 0;
            foreach (KeyValuePair<long, TrackedPBBlock> kvp in PBBlocks)
            {
                if (index >= runtimeTimeMSAvg.Length) break;
                runtimeTimeMSAvg[index++] = kvp.Value.RunTimeMSAvg;
            }
        }
        
        private void CleanUpOldPBBlocks()
        {
            try
            {
                foreach (KeyValuePair<long, TrackedPBBlock> entry in PBBlocks)
                {
                    TrackedPBBlock pbBlock = entry.Value;
                    bool gone = pbBlock.ProgrammableBlock is null;
                    bool inactive = (Stopwatch.GetTimestamp() - pbBlock.LastUpdateTick) / Stopwatch.Frequency > Config.RemoveInactivePBsAfterSeconds;
                    if (gone || inactive) PBBlocks.TryRemove(entry.Key, out _);
                }

                // Update cleanup interval if it has changed
                if (_lastKnownCleanupInterval == Config.RemoveInactivePBsAfterSeconds) return;
                _cleanupTimer.Interval = Math.Max(1, Config.RemoveInactivePBsAfterSeconds) * 1000;
                _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not clean up old programmable blocks.");
            }
        }

        /// <summary>Stops this player's cleanup timer. Call when the player is removed, or the timer keeps running forever.</summary>
        public void Stop()
        {
            _cleanupTimer.Stop();
            _cleanupTimer.Dispose();
        }
        
        public void ResetPBBlockData(MyProgrammableBlock pbBlock)
        {
            if (!PBBlocks.TryGetValue(pbBlock.EntityId, out TrackedPBBlock trackedPBBlock))
            {
                // Log.Warn("Unable to find PB Block to reset!");
                // "Check Code" button in the pb interface will cause this to happen, probably compiles it in a fake or temp pb which isnt in the list. 
                return;
            }

            if (trackedPBBlock.ProgrammableBlock is null)
            {
                Log.Warn("PB Block is null to reset!");
                return;
            }
            
            PBBlocks[trackedPBBlock.ProgrammableBlock.EntityId].ClearRunTimes();
            PBBlocks[trackedPBBlock.ProgrammableBlock.EntityId].ResetMemoryUsage();
        }
    }
}