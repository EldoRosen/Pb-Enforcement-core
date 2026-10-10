using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Patches;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using VRage.Game.VisualScripting.Utils;
using VRage.ModAPI;
using Timer = System.Timers.Timer;

namespace Advanced_PB_Limiter.Manager
{
    public static class TrackingManager
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        public static ConcurrentDictionary<long,TrackedPlayer> PlayersTracked { get; } = new ();
        private static readonly Timer _cleanupTimer = new ();
        private static readonly int _lastKnownCleanupInterval = Config.RemoveInactivePBsAfterSeconds;
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Tracking Manager");
        private static readonly ConcurrentDictionary<long, ProgramInfo> ProgrammablePrograms = new();
        private static readonly Timer _memoryCheckTimer = new (5000);
        private static readonly Timer _checkCombinedLimits = new (10000);
        private static bool _checkCombinedLimitsRunning;
        private static ConcurrentQueue<TrackingDataUpdateRequest> _trackingDataUpdateQueue = new();
        private static readonly Timer _processUpdateRequests = new(16);
        private static int _processingRunning; // 1 while the queue is being worked through (an int so the check and the set are one atomic step)
        private const int MaxQueuedUpdates = 20000; // safety net: if the queue ever backs up, the oldest samples are dropped instead of memory growing forever
        private static long _lastProcessErrorTick;
        private static bool _memoryCheckRunning = false;
        private static Dictionary<long, int> _pbDeferTracker = new();

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
            ScriptBlacklistManager.Reset();
            DiscordAutoReporter.Stop();
            ThrottleManager.Reset();
            BlockActionLog.Reset();
            ServerLoadMonitor.Reset();
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

        private static void CleanUpOldPlayers()
        {
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

        public static void UpdateTrackingData(MyProgrammableBlock? pb, double runTime)
        {
            if (_trackingDataUpdateQueue.Count >= MaxQueuedUpdates) _trackingDataUpdateQueue.TryDequeue(out _);
            _trackingDataUpdateQueue.Enqueue(new TrackingDataUpdateRequest{ProgBlock = pb, RunTime = runTime});
        }
        
        private static void ProcessUpdateTrackingData()
        {
            if (!Config.Enabled) return;
            if (_trackingDataUpdateQueue.IsEmpty) return;
            if (Interlocked.CompareExchange(ref _processingRunning, 1, 0) != 0) return; // the timer can fire again before the last pass ended

            try
            {
                while (_trackingDataUpdateQueue.TryDequeue(out TrackingDataUpdateRequest data))
                {
                    long _trackTime = Stopwatch.GetTimestamp();
                
                    if (data.ProgBlock == null) continue;
            
                    if (data.ProgBlock.OwnerId == 0)
                    {
                        if (Config.TurnOffUnownedBlocks)
                        {
                            BlockActionLog.Record(data.ProgBlock, "Switched off", "the block has no owner and \"Turn off unowned blocks\" is on");
                            MySandboxGame.Static.Invoke(()=>
                            {
                                data.ProgBlock.Enabled = false;
                            }, "PB Enforcement Core");
                        }
                        continue; // this block is skipped; the rest of the queue still has to be worked through
                    } // Track un-owned blocks? something to think about...
        
                    if (Config.IgnoreNPCs)
                        if (MySession.Static.Players.IdentityIsNpc(data.ProgBlock.OwnerId)) continue;

                    if (data.RunTime < 0)
                        data.RunTime = 0;
            
                    if (!PlayersTracked.TryGetValue(data.ProgBlock.OwnerId, out TrackedPlayer? player) || player == null)
                    {
                        player = new TrackedPlayer(data.ProgBlock.OwnerId);
                        PlayersTracked.TryAdd(data.ProgBlock.OwnerId, player);
                    }
            
                    player.UpdatePBBlockData(data);

                    if (Config.ShowTimingsBurp)
                        Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Processed PB Tracking Data[{data.ProgBlock.CustomName}] in: {_trackTime.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
                }
            }
            catch (Exception e)
            {
                // A timer swallows errors without a word. Report it (not on every tick); the next pass carries on with the rest of the queue.
                long now = Stopwatch.GetTimestamp();
                if (_lastProcessErrorTick == 0 || now - _lastProcessErrorTick >= 30 * Stopwatch.Frequency)
                {
                    _lastProcessErrorTick = now;
                    Log.Error(e, "Could not process the programmable block run times.");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _processingRunning, 0);
            }
        }
        
        private static void RemovePlayer(long Id)
        {
            if (PlayersTracked.TryRemove(Id, out TrackedPlayer? removed)) removed?.Stop();
        }
        
        public static void PBRecompiled(MyProgrammableBlock? pb)
        {
            if (!Config.Enabled) return;
            if (pb is null) return;

            if (!PlayersTracked.TryGetValue(pb.OwnerId, out TrackedPlayer? player)) return;
            
            if (Config.ClearHistoryOnRecompile)
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
                if (block is null) continue;

                double value = block.LoadMSPerSecond > 0 ? block.LoadMSPerSecond : (byAverage ? block.RunTimeMSAvg : block.LastRunTimeMS);
                if (value <= heaviestValue) continue;
                heaviestValue = value;
                heaviest = block;
            }
            return heaviest;
        }

        private static void CheckAllUserBlocksForCombinedLimits()
        {
            if (!Config.Enabled) return;
            
            if (!Config.CheckAllUserBlocksCombined) return;
            if (!ServerLoadMonitor.ShouldEnforce()) return;
            
            if (_checkCombinedLimitsRunning) return;

            try
            {
                _checkCombinedLimitsRunning = true;
                
                foreach(KeyValuePair<long, TrackedPlayer> trackedPlayer in PlayersTracked)
                {
                   long _trackStart = Stopwatch.GetTimestamp();
                    
                    // clear old combined warnings
                    for (int index = trackedPlayer.Value.CombinedOffences.Count - 1; index >= 0; index--)
                    {
                        if (trackedPlayer.Value.CombinedOffences[index].TicksTillNow_TimeSpan().TotalMinutes >= Config.OffenseDurationBeforeDeletion)
                        {
                            trackedPlayer.Value.CombinedOffences.Remove(trackedPlayer.Value.CombinedOffences[index]);
                        }
                    }

                    if (Config.PrivilegedPlayers.TryGetValue(trackedPlayer.Value.SteamId, out PrivilegedPlayer? privilegedPlayer))
                        if (privilegedPlayer.NoCombinedLimits) continue;

                    double CombinedRuntimeAllowance = privilegedPlayer?.CombinedRuntimeAllowance ?? Config.MaxAllBlocksCombinedRunTimeMS;
                    double CombinedRuntimeAverageAllowance = privilegedPlayer?.CombinedRuntimeAverageAllowance ?? Config.MaxAllBlocksCombinedRunTimeMSAvg;

                    GetCombinedTotals(trackedPlayer.Value, out double totalMS, out double totalMSAvg);
                    
                    if (CombinedRuntimeAllowance != 0 && totalMS > CombinedRuntimeAllowance)
                    {
                        trackedPlayer.Value.CombinedOffences.Add(Stopwatch.GetTimestamp());
                        
                        if (trackedPlayer.Value.CombinedOffences.Count <= Config.MaxCombinedOffencesBeforePunishment)
                        {
                            foreach (TrackedPBBlock? pbblocks in trackedPlayer.Value.GetAllPBBlocks)
                            {
                                MySandboxGame.Static.Invoke(() =>
                                {
                                    pbblocks.ProgrammableBlock?.Run("GracefulShutDown::-3", UpdateType.Script);
                                }, "Advanced_PB_Limiter");
                            }
                            continue;
                        }
                        
                        if (Config.PunishAllUserBlocksCombinedOnExcessLimits)
                        {
                            foreach (TrackedPBBlock pbBlock in trackedPlayer.Value.GetAllPBBlocks)
                                PunishmentManager.PunishPB(trackedPlayer.Value, pbBlock, PunishmentManager.PunishReason.CombinedRuntimeOverLimit);
                        }
                        else
                        {
                            TrackedPBBlock? heaviest = HeaviestBlock(trackedPlayer.Value, false);
                            if (heaviest != null)
                                PunishmentManager.PunishPB(trackedPlayer.Value, heaviest, PunishmentManager.PunishReason.CombinedRuntimeOverLimit);
                        }
                    }
                    
                    if (CombinedRuntimeAverageAllowance != 0 && totalMSAvg > CombinedRuntimeAverageAllowance)
                    {
                        trackedPlayer.Value.CombinedOffences.Add(Stopwatch.GetTimestamp());

                        if (trackedPlayer.Value.CombinedOffences.Count <= Config.MaxCombinedOffencesBeforePunishment)
                        {
                            foreach (TrackedPBBlock? pbblocks in trackedPlayer.Value.GetAllPBBlocks)
                            {
                                MySandboxGame.Static.Invoke(() =>
                                {
                                    pbblocks.ProgrammableBlock?.Run("GracefulShutDown::-3", UpdateType.Script);
                                }, "Advanced_PB_Limiter");
                            }
                            continue;
                        }
                        
                        if (Config.PunishAllUserBlocksCombinedOnExcessLimits)
                        {
                            foreach (TrackedPBBlock pbBlock in trackedPlayer.Value.GetAllPBBlocks)
                                PunishmentManager.PunishPB(trackedPlayer.Value, pbBlock, PunishmentManager.PunishReason.CombinedAverageRuntimeOverLimit);
                        }
                        else
                        {
                            TrackedPBBlock? heaviest = HeaviestBlock(trackedPlayer.Value, true);
                            if (heaviest != null)
                                PunishmentManager.PunishPB(trackedPlayer.Value, heaviest, PunishmentManager.PunishReason.CombinedAverageRuntimeOverLimit);
                        }
                    }
                    
                    if (Config.ShowTimingsBurp)
                        Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Checked User Combined Usage[{trackedPlayer.Value.PlayerName}] in: {_trackStart.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N4", CultureInfo.InvariantCulture)}ms");
                }
            }
            finally
            {
                _checkCombinedLimitsRunning = false;
            }
        }

        /// <summary>
        /// Adds up a player's last run times and averages. The buffers live in their own method on purpose: stackalloc memory is only
        /// given back when the method ends, so inside the per-player loop it piled up and could overflow the stack on a big server.
        /// </summary>
        private static void GetCombinedTotals(TrackedPlayer player, out double totalMS, out double totalMSAvg)
        {
            totalMS = 0;
            totalMSAvg = 0;

            int count = player.PBBlockCount;
            if (count == 0) return;

            Span<double> runtimes = count <= 256 ? stackalloc double[count] : new double[count];
            player.GetAllPBBlocksLastRunTimeMS(runtimes);
            for (int index = runtimes.Length - 1; index >= 0; index--) totalMS += runtimes[index];

            Span<double> averages = count <= 256 ? stackalloc double[count] : new double[count];
            player.GetAllPBBlocksMSAvg(averages);
            for (int index = averages.Length - 1; index >= 0; index--) totalMSAvg += averages[index];
        }

        public static bool IsPBInstanceReferenceValid(long pbEntityID)
        {
            if (!ProgrammablePrograms.TryGetValue(pbEntityID, out ProgramInfo? programmableProgram)) return false;
            if (programmableProgram == null) return false;
            if (!programmableProgram.ProgramReference.TryGetTarget(out IMyGridProgram? program)) return false;
            return program != null;
        }

        public static bool IsMemoryCheckInProgress(long entityID)
        {
            if (!ProgrammablePrograms.TryGetValue(entityID, out ProgramInfo? programmableProgram)) return false;

            return Volatile.Read(ref programmableProgram.IsChecking) == 1;
        }

        public static void AddPBInstanceReference(long entityID, ProgramInfo programInfo)
        {
            ProgrammablePrograms.AddOrUpdate(entityID, programInfo, (l, info) => info);
        }

        public static void RemovePBInstanceReference(long entityId)
        {
            ProgrammablePrograms.TryRemove(entityId, out _);
        }

        private static void CheckInstanceMemoryUsages()
        {
            if (!Config.EnableMemoryMonitoring) return;
            if (_memoryCheckRunning) return;
            _memoryCheckRunning = true;

            try
            {
                foreach (KeyValuePair<long,ProgramInfo> programInfo in ProgrammablePrograms)
                {
                    if (Interlocked.CompareExchange(ref programInfo.Value.IsChecking, 1, 0) != 0)
                        continue;

                    try
                    {
                        if (!programInfo.Value.ProgramReference.TryGetTarget(out IMyGridProgram? program))
                        {
                            RemovePBInstanceReference(programInfo.Key);
                            continue;
                        }

                        if (program == null)
                        {
                            RemovePBInstanceReference(programInfo.Key);
                            continue;
                        }
                    
                        GetSize.OfAssembly(program, out long size, out long time);
                        if (Config.ShowTimingsBurp)
                            Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Programmable Block=>[{programInfo.Value.PB.CustomName}] Size=>[{size} bytes] ScanTime=>[{TimeSpan.FromTicks(time).TotalMilliseconds}ms]");
                    
                        programInfo.Value.LastUpdate = DateTime.Now;

                        PlayersTracked.TryGetValue(programInfo.Value.OwnerID, out TrackedPlayer? trackedPlayer);
                        TrackedPBBlock? block = null;
                    
                        if (trackedPlayer == null)
                        {
                            foreach (TrackedPlayer player in PlayersTracked.Values)
                            {
                                block = player.GetTrackedPB(programInfo.Value.PB.EntityId);
                                if (block != null)
                                    break;
                            }
                        }
                        else
                        {
                            block = trackedPlayer.GetTrackedPB(programInfo.Key);
                        }
                    
                        if (block == null) continue;
                    
                        block.updateMemoryUsage(size);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref programInfo.Value.IsChecking, 0);
                    }
                }
            }
            finally
            {
                _memoryCheckRunning = false;
            }
        }
    }
}
     