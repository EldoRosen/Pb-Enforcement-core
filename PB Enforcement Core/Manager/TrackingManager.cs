using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Advanced_PB_Limiter.Patches;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRageMath;
using Timer = System.Timers.Timer;

namespace Advanced_PB_Limiter.Manager
{
    public static class TrackingManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; } = new ();
        private static readonly Timer _cleanupTimer = new ();
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Tracking Manager");
        private static readonly ConcurrentDictionary<long, ProgramInfo> ProgrammablePrograms = new();
        private static readonly Timer _memoryCheckTimer = new (5000);
        private static readonly Timer _checkCombinedLimits = new (10000);
        private static int _checkCombinedLimitsRunning;
        private static readonly ConcurrentQueue<TrackingDataUpdateRequest> _trackingDataUpdateQueue = new();
        private static readonly Timer _processUpdateRequests = new(16);
        private static int _processingRunning; // 1 while the queue is being worked through (an int so the check and the set are one atomic step)
        private const int MaxQueuedUpdates = 20000; // safety net: if the queue ever backs up, the oldest samples are dropped instead of memory growing forever
        private static long _lastProcessErrorTick;
        private static int _memoryCheckRunning;

        /// <summary>Which player each tracked block was last seen with, so a block whose owner changes moves to the new owner.</summary>
        private static readonly ConcurrentDictionary<long, long> BlockOwners = new();
        /// <summary>Unowned blocks already queued to be switched off, so a block running every tick queues it once, not every run.</summary>
        private static readonly ConcurrentDictionary<long, byte> UnownedQueued = new();

        private static readonly System.Timers.ElapsedEventHandler MemoryCheckHandler = (sender, args) => CheckInstanceMemoryUsages();
        private static readonly System.Timers.ElapsedEventHandler CombinedCheckHandler = (sender, args) => CheckAllUserBlocksForCombinedLimits();
        private static readonly System.Timers.ElapsedEventHandler ProcessUpdatesHandler = (sender, args) => ProcessUpdateTrackingData();
        private static readonly System.Timers.ElapsedEventHandler CleanupPlayersHandler = (sender, args) => CleanUpOldPlayers();

        public static void Start()
        {
            DiscordAutoReporter.Start();
            ScriptBlacklistManager.Start();

            _memoryCheckTimer.Elapsed -= MemoryCheckHandler;
            _memoryCheckTimer.Elapsed += MemoryCheckHandler;
            _memoryCheckTimer.Start();

            _checkCombinedLimits.Elapsed -= CombinedCheckHandler;
            _checkCombinedLimits.Elapsed += CombinedCheckHandler;
            _checkCombinedLimits.Start();

            _processUpdateRequests.Elapsed -= ProcessUpdatesHandler;
            _processUpdateRequests.Elapsed += ProcessUpdatesHandler;
            _processUpdateRequests.Start();

            _cleanupTimer.Elapsed -= CleanupPlayersHandler;
            _cleanupTimer.Elapsed += CleanupPlayersHandler;
            // Always running: CleanUpOldPlayers does nothing while the setting is 0, so changing it later works without a restart.
            _cleanupTimer.Interval = TimeSpan.FromMinutes(1).TotalMilliseconds;
            _cleanupTimer.Start();
        }

        public static void Stop()
        {
            _memoryCheckTimer.Stop();
            _checkCombinedLimits.Stop();
            _processUpdateRequests.Stop();
            _cleanupTimer.Stop();
            foreach (TrackedPlayer tracked in PlayersTracked.Values.ToList()) tracked.Stop();
            PlayersTracked.Clear();
            ProgrammablePrograms.Clear();
            BlockOwners.Clear();
            UnownedQueued.Clear();
            ScriptBlacklistManager.Reset();
            DiscordAutoReporter.Stop();
            ThrottleManager.Reset();
            BlockActionLog.Reset();
            ServerLoadMonitor.Reset();
            PunishmentManager.Reset();
            ProfilerPatch_Torch.Reset();
            while (_trackingDataUpdateQueue.TryDequeue(out _)) { }
        }

        public static List<TrackedPlayer> GetTrackedPlayerData()
        {
            return PlayersTracked.Values.ToList();
        }

        public static TrackedPlayer? GetTrackedPlayerDataById(long playerId)
        {
            return PlayersTracked.TryGetValue(playerId, out TrackedPlayer? player)
                ? player
                : null;
        }

        /// <summary>Finds a tracked block whoever owns it.</summary>
        public static TrackedPBBlock? FindTrackedBlock(long entityId)
        {
            if (BlockOwners.TryGetValue(entityId, out long ownerId) && PlayersTracked.TryGetValue(ownerId, out TrackedPlayer? owner))
            {
                TrackedPBBlock? block = owner.GetTrackedPB(entityId);
                if (block != null) return block;
            }

            foreach (TrackedPlayer player in PlayersTracked.Values)
            {
                TrackedPBBlock? block = player.GetTrackedPB(entityId);
                if (block != null) return block;
            }
            return null;
        }

        private static void CleanUpOldPlayers()
        {
            try
            {
                // Forget the owners of blocks nobody tracks any more (deleted or long inactive).
                foreach (KeyValuePair<long, long> entry in BlockOwners.ToArray())
                {
                    if (!PlayersTracked.TryGetValue(entry.Value, out TrackedPlayer? owner) || owner.GetTrackedPB(entry.Key) == null)
                        BlockOwners.TryRemove(entry.Key, out _);
                }

                if (!Config.Enabled) return;
                if (Config.RemovePlayersWithNoPBFrequencyInMinutes <= 0) return; // 0 means "never remove"

                foreach (KeyValuePair<long, TrackedPlayer> entry in PlayersTracked.ToArray())
                {
                    TrackedPlayer tracked = entry.Value;
                    if (tracked.PBBlockCount > 0) continue;
                    if ((Stopwatch.GetTimestamp() - tracked.LastDataUpdateTick) / Stopwatch.Frequency / 60 < Config.RemovePlayersWithNoPBFrequencyInMinutes) continue;
                    RemovePlayer(entry.Key);
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not clean up old players.");
            }
        }

        public static void UpdateTrackingData(MyProgrammableBlock? pb, double runTime, long ownerId, bool gcAffected)
        {
            if (pb == null) return;
            if (_trackingDataUpdateQueue.Count >= MaxQueuedUpdates) _trackingDataUpdateQueue.TryDequeue(out _);
            _trackingDataUpdateQueue.Enqueue(new TrackingDataUpdateRequest { ProgBlock = pb, RunTime = runTime, OwnerId = ownerId, GcAffected = gcAffected });
        }

        private static void ProcessUpdateTrackingData()
        {
            if (_trackingDataUpdateQueue.IsEmpty) return;
            if (!Config.Enabled || !Advanced_PB_Limiter.GameOnline)
            {
                // Runs queued before the plugin was switched off (or the session ended) are old news: if they were kept, switching
                // the plugin back on later would judge blocks on runs from back then.
                while (_trackingDataUpdateQueue.TryDequeue(out _)) { }
                return;
            }
            if (MySession.Static?.Players == null) return; // the session is ending
            if (Interlocked.CompareExchange(ref _processingRunning, 1, 0) != 0) return; // the timer can fire again before the last pass ended

            try
            {
                while (_trackingDataUpdateQueue.TryDequeue(out TrackingDataUpdateRequest data))
                {
                    try
                    {
                        ProcessOne(data);
                    }
                    catch (Exception e)
                    {
                        // A timer swallows errors without a word. Report it (not on every tick); the rest of the queue carries on.
                        long now = Stopwatch.GetTimestamp();
                        if (_lastProcessErrorTick == 0 || now - _lastProcessErrorTick >= 30 * Stopwatch.Frequency)
                        {
                            _lastProcessErrorTick = now;
                            Log.Error(e, "Could not process a programmable block run time.");
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _processingRunning, 0);
            }
        }

        private static void ProcessOne(TrackingDataUpdateRequest data)
        {
            long trackStart = Config.ShowTimingsBurp ? Stopwatch.GetTimestamp() : 0;
            if (!Advanced_PB_Limiter.GameOnline) return; // the session ended mid-pass: a player created now would outlive it
            MyProgrammableBlock? pb = data.ProgBlock;
            if (pb == null || pb.Closed || pb.MarkedForClose) return;

            long entityId = pb.EntityId;
            long ownerId = data.OwnerId;

            if (ownerId == 0)
            {
                ForgetOwner(entityId);
                if (Config.TurnOffUnownedBlocks && UnownedQueued.TryAdd(entityId, 0))
                {
                    BlockActionLog.Record(pb, "Switched off", "the block has no owner and \"Turn off when owned by Nobody\" is on");
                    MySandboxGame.Static.Invoke(() =>
                    {
                        try
                        {
                            if (!pb.Closed && pb.OwnerId == 0) pb.Enabled = false;
                        }
                        catch (Exception e)
                        {
                            Log.Warn(e, "Could not switch off an unowned programmable block.");
                        }
                        finally
                        {
                            UnownedQueued.TryRemove(entityId, out _);
                        }
                    }, "PB Enforcement Core");
                }
                return;
            }

            if (Config.IgnoreNPCs && MySession.Static.Players.IdentityIsNpc(ownerId))
            {
                ForgetOwner(entityId);
                return;
            }

            if (data.RunTime < 0) data.RunTime = 0;

            // The block changed owner: move it, with its history, to the new owner instead of tracking it twice.
            TrackedPBBlock? moved = null;
            if (BlockOwners.TryGetValue(entityId, out long previousOwner) && previousOwner != ownerId
                && PlayersTracked.TryGetValue(previousOwner, out TrackedPlayer? previous))
                previous.RemoveBlock(entityId, out moved);
            BlockOwners[entityId] = ownerId;

            TrackedPlayer player = GetOrCreatePlayer(ownerId);
            if (moved != null) player.AdoptBlock(moved);
            player.UpdatePBBlockData(data);

            if (Config.ShowTimingsBurp)
                Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Processed PB Tracking Data[{pb.CustomName}] in: {trackStart.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
        }

        private static TrackedPlayer GetOrCreatePlayer(long ownerId)
        {
            if (PlayersTracked.TryGetValue(ownerId, out TrackedPlayer? existing) && existing != null) return existing;

            TrackedPlayer created = new(ownerId);
            if (PlayersTracked.TryAdd(ownerId, created)) return created;

            created.Stop(); // another thread added the player first: do not leave this one's timer running
            return PlayersTracked[ownerId];
        }

        /// <summary>The block is no longer tracked under any player (it became unowned, or an NPC's while NPCs are ignored).</summary>
        private static void ForgetOwner(long entityId)
        {
            if (BlockOwners.TryRemove(entityId, out long previousOwner) && PlayersTracked.TryGetValue(previousOwner, out TrackedPlayer? previous))
                previous.RemoveBlock(entityId, out _);
        }

        private static void RemovePlayer(long Id)
        {
            if (PlayersTracked.TryRemove(Id, out TrackedPlayer? removed)) removed?.Stop();
        }

        public static void PBRecompiled(MyProgrammableBlock? pb)
        {
            if (!Config.Enabled) return;
            if (pb is null) return;
            if (!Config.ClearHistoryOnRecompile) return;

            long ownerId = BlockOwners.TryGetValue(pb.EntityId, out long known) ? known : pb.OwnerId;
            if (PlayersTracked.TryGetValue(ownerId, out TrackedPlayer? player))
                player.ResetPBBlockData(pb);
        }

        /// <summary>The block using the most server time: the one to punish when a player is over a combined limit.</summary>
        private static TrackedPBBlock? HeaviestBlock(TrackedPlayer player, bool byAverage)
        {
            TrackedPBBlock? heaviest = null;
            double heaviestValue = double.MinValue;
            List<TrackedPBBlock> blocks = player.GetAllPBBlocks;
            for (int i = 0; i < blocks.Count; i++)
            {
                TrackedPBBlock? block = blocks[i];
                if (block is null || block.IsGone) continue;

                double value = block.LoadMSPerSecond > 0 ? block.LoadMSPerSecond : (byAverage ? block.RunTimeMSAvg : block.LastRunTimeMS);
                if (value <= heaviestValue) continue;
                heaviestValue = value;
                heaviest = block;
            }
            return heaviest;
        }

        /// <summary>
        /// Combined limits, every 10 seconds.
        /// "Max combined runtime" is compared with the player's average server time per tick (all their blocks' load), not with
        /// the sum of each block's last run: last runs happen on different ticks, so adding them up flagged players with many
        /// light scripts. "Max combined runtime average" is compared with the sum of the blocks' run-time averages, as before.
        /// One offence per pass at most, a chat warning only (scripts are no longer run with an argument), and after a punishment
        /// the player's combined offences are cleared and they are left alone for a while, so one breach no longer switches
        /// off their blocks one after another.
        /// </summary>
        private static void CheckAllUserBlocksForCombinedLimits()
        {
            if (!Config.Enabled) return;
            if (!Config.CheckAllUserBlocksCombined) return;
            if (Interlocked.CompareExchange(ref _checkCombinedLimitsRunning, 1, 0) != 0) return;

            try
            {
                bool enforce = ServerLoadMonitor.ShouldEnforce();
                double ticksPerSecond = 60.0 * Math.Max(0.1, Math.Min(1.0, Sync.ServerSimulationRatio));
                double offenceMaxAge = OffenceWindowSeconds(Config.MaxCombinedOffencesBeforePunishment);
                long now = Stopwatch.GetTimestamp();

                foreach (TrackedPlayer player in PlayersTracked.Values)
                {
                    try
                    {
                        long trackStart = Config.ShowTimingsBurp ? Stopwatch.GetTimestamp() : 0;
                        player.PruneCombinedOffences(offenceMaxAge);
                        if (!enforce) continue;
                        if (now < Interlocked.Read(ref player.CombinedCooldownUntilTick)) continue;

                        Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer? privilegedPlayer);
                        if (privilegedPlayer?.NoCombinedLimits == true) continue;

                        double allowedPerTick = privilegedPlayer?.CombinedRuntimeAllowance ?? Config.MaxAllBlocksCombinedRunTimeMS;
                        double allowedAverage = privilegedPlayer?.CombinedRuntimeAverageAllowance ?? Config.MaxAllBlocksCombinedRunTimeMSAvg;

                        double loadPerSecond = player.TotalLoadMSPerSecond;
                        if (Config.MinLoadMSPerSecondToPunish > 0 && loadPerSecond < Config.MinLoadMSPerSecondToPunish) continue;

                        double perTick = loadPerSecond / ticksPerSecond;
                        double averageSum = player.TotalRunTimeMSAvg;

                        PunishmentManager.PunishReason reason;
                        string limitType;
                        double limit, usage;
                        bool byAverage;
                        if (allowedPerTick > 0 && perTick > allowedPerTick)
                        {
                            reason = PunishmentManager.PunishReason.CombinedRuntimeOverLimit;
                            limitType = "combined run-time per tick";
                            limit = allowedPerTick;
                            usage = perTick;
                            byAverage = false;
                        }
                        else if (allowedAverage > 0 && averageSum > allowedAverage)
                        {
                            reason = PunishmentManager.PunishReason.CombinedAverageRuntimeOverLimit;
                            limitType = "combined average run-time";
                            limit = allowedAverage;
                            usage = averageSum;
                            byAverage = true;
                        }
                        else continue;

                        // The same spacing as single-block offences.
                        if ((now - player.LastCombinedOffenceTick) / (double)Stopwatch.Frequency <= Config.GraceAfterOffence) continue;

                        TrackedPBBlock? heaviest = HeaviestBlock(player, byAverage);
                        if (heaviest is null) continue;

                        int count = player.AddCombinedOffence();
                        int allowed = Config.MaxCombinedOffencesBeforePunishment;
                        Log.Info($"Player {player.PlayerName} ({player.SteamId}) is over the {limitType} limit: {usage:0.0000}ms, allowed {limit:0.0000}ms. Combined offences: {count} of {allowed} allowed.");

                        if (count <= allowed)
                        {
                            if (Config.WarnUserOnOffense)
                                Chat.Send(ChatMessages.OffenceWarning(player, heaviest, count, allowed, limitType, limit, usage), player.SteamId, Color.Red);
                            continue;
                        }

                        string notes = $"\nYour Limit: [{limit:0.0000}ms]\nYour Usage: [{usage:0.0000}ms]";
                        if (Config.PunishAllUserBlocksCombinedOnExcessLimits)
                        {
                            foreach (TrackedPBBlock pbBlock in player.GetAllPBBlocks)
                                if (!pbBlock.IsGone) PunishmentManager.PunishPB(player, pbBlock, reason, null, notes);
                        }
                        else
                        {
                            PunishmentManager.PunishPB(player, heaviest, reason, null, notes);
                        }

                        player.ClearCombinedOffences();
                        long cooldownTicks = (long)((Math.Max(0, Config.GraceAfterOffence) + 10) * (double)Stopwatch.Frequency);
                        Interlocked.Exchange(ref player.CombinedCooldownUntilTick, now + cooldownTicks);

                        if (Config.ShowTimingsBurp)
                            Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Checked User Combined Usage[{player.PlayerName}] in: {trackStart.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, $"Could not check the combined limits of {player.PlayerName}.");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _checkCombinedLimitsRunning, 0);
            }
        }

        /// <summary>
        /// How long an offence is remembered. At least "Remove offense after time", and long enough for the allowed offences plus
        /// one to happen with "Grace after offence" between them (with the old defaults, 4 offences 15 s apart had to fit in
        /// exactly 60 s, so any pause meant a heavy script was never punished).
        /// </summary>
        public static double OffenceWindowSeconds(int allowedOffences)
        {
            double configured = Math.Max(0, Config.OffenseDurationBeforeDeletion) * 60.0;
            double needed = (Math.Max(0, allowedOffences) + 1) * Math.Max(1, Config.GraceAfterOffence) + 30;
            return Math.Max(configured, needed);
        }

        public static bool IsPBInstanceReferenceValid(long pbEntityID)
        {
            if (!ProgrammablePrograms.TryGetValue(pbEntityID, out ProgramInfo? programmableProgram)) return false;
            if (programmableProgram == null) return false;
            if (!programmableProgram.ProgramReference.TryGetTarget(out IMyGridProgram? program)) return false;
            return program != null;
        }

        public static void AddPBInstanceReference(long entityID, ProgramInfo programInfo)
        {
            ProgrammablePrograms.AddOrUpdate(entityID, programInfo, (l, info) => programInfo);
        }

        public static void RemovePBInstanceReference(long entityId)
        {
            ProgrammablePrograms.TryRemove(entityId, out _);
        }

        /// <summary>
        /// Estimates each script's memory every 5 seconds. The scan only reads fields (it never calls the script's own code) and
        /// never stops a script from running, so it runs on a background thread without holding the game up.
        /// </summary>
        private static void CheckInstanceMemoryUsages()
        {
            if (!Config.Enabled || !Config.EnableMemoryMonitoring) return;
            if (Interlocked.CompareExchange(ref _memoryCheckRunning, 1, 0) != 0) return;

            try
            {
                foreach (KeyValuePair<long, ProgramInfo> programInfo in ProgrammablePrograms)
                {
                    try
                    {
                        MyProgrammableBlock? pb = programInfo.Value.PB;
                        if (pb is null || pb.Closed || pb.MarkedForClose
                            || !programInfo.Value.ProgramReference.TryGetTarget(out IMyGridProgram? program) || program == null)
                        {
                            RemovePBInstanceReference(programInfo.Key);
                            continue;
                        }

                        long size = GetSize.OfProgram(program, out long time, out bool complete);
                        if (Config.ShowTimingsBurp)
                            Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Programmable Block=>[{pb.CustomName}] Size=>[{size} bytes{(complete ? "" : ", scan stopped early")}] ScanTime=>[{time * 1000.0 / Stopwatch.Frequency:0.000}ms]");

                        programInfo.Value.LastUpdate = DateTime.Now;

                        TrackedPBBlock? block = FindTrackedBlock(programInfo.Key);
                        block?.updateMemoryUsage(size, Config.PBMemoryThreshold);
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, "Could not measure the memory of a programmable block.");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _memoryCheckRunning, 0);
            }
        }
    }
}
