using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using NLog;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using Timer = System.Timers.Timer;

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

        // ---- combined-limit offences (only touched by the combined check, guarded anyway)
        private readonly List<long> _combinedOffences = new();
        private long _lastCombinedOffenceTick;
        /// <summary>After a combined punishment the player is left alone until this tick, so the next check does not punish the next block straight away.</summary>
        public long CombinedCooldownUntilTick;

        public TrackedPlayer(long playerId)
        {
            PlayerId = playerId;
            PlayerName = MySession.Static?.Players?.TryGetIdentity(playerId)?.DisplayName ?? "Unknown";
            SteamId = MySession.Static?.Players?.TryGetSteamId(playerId) ?? 0;
            LastDataUpdateTick = Stopwatch.GetTimestamp();
            _cleanupTimer.Elapsed += (sender, args) => CleanUpOldPBBlocks();
            _cleanupTimer.Start();
        }

        public int PBBlockCount => PBBlocks.Count;

        public TrackedPBBlock? GetTrackedPB(long entityId)
        {
            return PBBlocks.TryGetValue(entityId, out TrackedPBBlock? trackedPBBlock)
                ? trackedPBBlock
                : null;
        }

        /// <summary>Takes a block away from this player (its owner changed). Returns it, with its history, so the new owner can take it over.</summary>
        public bool RemoveBlock(long entityId, out TrackedPBBlock? block) => PBBlocks.TryRemove(entityId, out block);

        /// <summary>Takes over a block, with its history, from its previous owner.</summary>
        public void AdoptBlock(TrackedPBBlock block)
        {
            if (block.ProgrammableBlock is null) return;
            PBBlocks[block.ProgrammableBlock.EntityId] = block;
        }

        public void UpdatePBBlockData(TrackingDataUpdateRequest data)
        {
            if (data.ProgBlock == null) return;

            // The name and Steam id are read once when the player is first tracked. If the game did not know them yet, look again
            // (a Steam id of 0 would hide a privileged player from the privileged list).
            if (SteamId == 0) SteamId = MySession.Static?.Players?.TryGetSteamId(PlayerId) ?? 0;
            if (string.IsNullOrEmpty(PlayerName) || PlayerName == "Unknown")
                PlayerName = MySession.Static?.Players?.TryGetIdentity(PlayerId)?.DisplayName ?? "Unknown";

            LastDataUpdateTick = Stopwatch.GetTimestamp();
            if (!PBBlocks.TryGetValue(data.ProgBlock.EntityId, out TrackedPBBlock? trackedPBBlock) || trackedPBBlock == null)
            {
                trackedPBBlock = new TrackedPBBlock(data.ProgBlock.CubeGrid?.DisplayName ?? "Unknown grid", data.ProgBlock);
                trackedPBBlock = PBBlocks.GetOrAdd(data.ProgBlock.EntityId, trackedPBBlock);
            }

            // Every run counts as activity, even inside the startup allowance (see TrackedPBBlock.Touch).
            trackedPBBlock.Touch();

            if (trackedPBBlock.IsUnderGracePeriod(SteamId))
                return;

            trackedPBBlock.AddRuntimeData(data.RunTime, SteamId);
            PunishmentManager.CheckForPunishment(this, data.ProgBlock.EntityId, data.RunTime, data.GcAffected);
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

        /// <summary>Server time all of this player's blocks use, in milliseconds per second.</summary>
        public double TotalLoadMSPerSecond
        {
            get
            {
                double total = 0;
                foreach (TrackedPBBlock block in PBBlocks.Values) total += block.LoadMSPerSecond;
                return total;
            }
        }

        /// <summary>Sum of the blocks' enforced averages (<see cref="TrackedPBBlock.RunTimeMSAvg"/>).</summary>
        public double TotalRunTimeMSAvg
        {
            get
            {
                double total = 0;
                foreach (TrackedPBBlock block in PBBlocks.Values) total += block.RunTimeMSAvg;
                return total;
            }
        }

        // ---- combined offences

        public long LastCombinedOffenceTick => Interlocked.Read(ref _lastCombinedOffenceTick);

        /// <summary>Adds a combined offence and returns how many the player now has.</summary>
        public int AddCombinedOffence()
        {
            long now = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _lastCombinedOffenceTick, now);
            lock (_combinedOffences)
            {
                _combinedOffences.Add(now);
                return _combinedOffences.Count;
            }
        }

        public void PruneCombinedOffences(double maxAgeSeconds)
        {
            long oldest = Stopwatch.GetTimestamp() - (long)(maxAgeSeconds * Stopwatch.Frequency);
            lock (_combinedOffences) _combinedOffences.RemoveAll(tick => tick < oldest);
        }

        public void ClearCombinedOffences()
        {
            lock (_combinedOffences) _combinedOffences.Clear();
        }

        public int CombinedOffenceCount
        {
            get { lock (_combinedOffences) return _combinedOffences.Count; }
        }

        private void CleanUpOldPBBlocks()
        {
            try
            {
                long now = Stopwatch.GetTimestamp();
                // Never shorter than 30 s: an Update100 script runs every 1.7 s (every 5 s or so at low sim speed). Dropping it between
                // runs would re-create it with a fresh startup allowance each time, and it would never be measured.
                double inactiveSeconds = Math.Max(30, Config.RemoveInactivePBsAfterSeconds);
                foreach (KeyValuePair<long, TrackedPBBlock> entry in PBBlocks)
                {
                    TrackedPBBlock pbBlock = entry.Value;
                    bool inactive = (now - pbBlock.LastUpdateTick) / (double)Stopwatch.Frequency > inactiveSeconds;
                    if (pbBlock.IsGone || inactive) PBBlocks.TryRemove(entry.Key, out _);
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
            // "Check Code" in the terminal compiles in a temporary block that is not tracked: nothing to reset then.
            if (!PBBlocks.TryGetValue(pbBlock.EntityId, out TrackedPBBlock trackedPBBlock)) return;

            trackedPBBlock.ClearRunTimes();
            trackedPBBlock.ResetMemoryUsage();
        }
    }
}
