using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using Torch.Utils;
using VRage.Collections;
using VRage.Game;
using VRage.Game.ObjectBuilders.Components;
using VRage.Network;
using VRage.Replication;
using VRageMath;
using static Advanced_PB_Limiter.Utils.Enums;
using Sandbox.Game.Replication;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Sync;

namespace Advanced_PB_Limiter.Manager
{
    public static class PunishmentManager
    {
        [ReflectedGetter(Name = "m_clientStates")]
        private static Func<MyReplicationServer, IDictionary> _clientStates;
        private const string CLIENT_DATA_TYPE_NAME = "VRage.Network.MyClient, VRage";
        [ReflectedGetter(TypeName = CLIENT_DATA_TYPE_NAME, Name = "Replicables")]
        private static Func<object, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>> _replicables;
        [ReflectedMethod(Name = "RemoveForClient", OverrideTypeNames = new[] { null, CLIENT_DATA_TYPE_NAME, null })]
        private static Action<MyReplicationServer, IMyReplicable, object, bool> _removeForClient;
        [ReflectedMethod(Name = "ForceReplicable")]
        private static Action<MyReplicationServer, IMyReplicable, Endpoint> _forceReplicable;
        
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static FieldInfo? needsInstansiationField;
        private static MethodInfo? Terminate;
        private static ConcurrentDictionary<long, ulong> GracefulShutdownsInProgress { get; } = new(); // Key: EntityId, Value: SteamId
        public enum PunishReason
        {
            SingleRuntimeOverLimit,
            AverageRuntimeOverLimit,
            CombinedRuntimeOverLimit,
            CombinedAverageRuntimeOverLimit,
            ExtremeUsage
        }
        
        public static void CheckForPunishment(TrackedPlayer player, long pbEntityId, double lastRunTimeMS)
        {
            if (!Config.Enabled) return;
            if (GracefulShutdownsInProgress.TryGetValue(pbEntityId, out _)) return;
            
            TrackedPBBlock? trackedPbBlock = player.GetTrackedPB(pbEntityId);
            if (trackedPbBlock is null)
            {
                Log.Error("Null TrackedPBBlock!");
                return;
            }

            if (trackedPbBlock.IsUnderGracePeriod(player.SteamId)) return;
            
            // InstaKill by run-time
            if (Config.InstantKillThreshold > 0 && lastRunTimeMS > Config.InstantKillThreshold)
            {
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, $"[{lastRunTimeMS:0.0000}ms]");
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} with a run-time of {lastRunTimeMS:0.0000}ms");
                return;
            }
            
            // InstaKill by memory
            if (Config.PBMemoryThreshold != 0 && Config.PBMemoryThreshold < trackedPbBlock.memoryUsage)
            {
                string usage = HelperUtils.FormatBytesToKB(trackedPbBlock.memoryUsage);
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, Punishment.Destroy, $"[{usage}]");
                Log.Error($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.ProgrammableBlock?.CubeGrid.DisplayName} with a memory usage of {usage}");
                return;
            }

            // Everything below is a "soft" limit: only enforced while the server is actually struggling (if that option is on).
            if (!ServerLoadMonitor.ShouldEnforce()) return;

            double allowedRunTime = Config.MaxRunTimeMS;
            double allowedRunTimeAvg = Config.MaxRunTimeMSAvg;
            int allowedOffenses = Config.MaxOffencesBeforePunishment;

            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer privilegedPlayer))
            {
                allowedRunTime = privilegedPlayer.RuntimeAllowance;
                allowedRunTimeAvg = privilegedPlayer.RuntimeAverageAllowance;
                allowedOffenses = privilegedPlayer.OffencesBeforePunishment;
            }
            
            // Clean up old offenses
            while (true)
            {
                if (trackedPbBlock.Offences.TryPeek(out long lastOffense_cleanup))
                {                           // Limit is in minutes
                    if (lastOffense_cleanup.TicksTillNow_TimeSpan().TotalMinutes > Config.OffenseDurationBeforeDeletion)
                        if (trackedPbBlock.Offences.TryDequeue(out _)) continue;
                }

                break;
            }

            // Check if enough time since last offense has passed.
            if (trackedPbBlock.LastOffenceTick.TicksTillNow_TimeSpan().TotalSeconds <= Config.GraceAfterOffence)
                return;

            // A script that uses very little server time per second cannot be hurting the server, however long one run takes.
            if (Config.MinLoadMSPerSecondToPunish > 0 && trackedPbBlock.LoadMSPerSecond < Config.MinLoadMSPerSecondToPunish)
                return;
            
            // Check for runtime offense
            if (allowedRunTime != 0 && lastRunTimeMS > allowedRunTime)
            {
                trackedPbBlock.Offences.Enqueue(Stopwatch.GetTimestamp());
                trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref player.Offences);
                DiscordAutoReporter.RecordOffence(player, trackedPbBlock);
                ThrottleManager.OnOffence(player, trackedPbBlock);
                Log.Info($"PB Block: {trackedPbBlock.ProgrammableBlock?.EntityId} for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}] has exceeded the run-time limit.  Last Run-Time: {lastRunTimeMS:0.0000}ms  Allowed-Run-Time: {allowedRunTime:0.0000}ms  Offenses: {trackedPbBlock.Offences.Count}");
                
                if (trackedPbBlock.Offences.Count <= allowedOffenses)
                {
                    if (Config.WarnUserOnOffense)
                        Chat.Send(ChatMessages.OffenceWarning(player, trackedPbBlock, trackedPbBlock.Offences.Count, allowedOffenses, "run-time", allowedRunTime, lastRunTimeMS), player.SteamId, Color.Red);

                    if (trackedPbBlock.Offences.Count == allowedOffenses)
                    {
                        MySandboxGame.Static.Invoke(() =>
                        {
                            trackedPbBlock.ProgrammableBlock?.Run($"GracefulShutDown::-1", UpdateType.Script);
                        }, "Advanced_PB_Limiter");
                    }
                    return;
                }
                
                StringBuilder sb = new();
                sb.AppendLine();
                sb.AppendLine($"Your Limit: [{allowedRunTime:0.0000}ms]");
                sb.AppendLine($"Your Usage: [{lastRunTimeMS:0.0000}ms]");
                PunishPB(player, trackedPbBlock, PunishReason.SingleRuntimeOverLimit, null, sb.ToString());
                return; // Block is punished, no need to punish again with average run-time violations.
            }
            
            // check for block average runtime offense
            if (allowedRunTimeAvg != 0 && trackedPbBlock.RunTimeMSAvg > allowedRunTimeAvg)
            {
                trackedPbBlock.Offences.Enqueue(Stopwatch.GetTimestamp());
                trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref player.Offences);
                DiscordAutoReporter.RecordOffence(player, trackedPbBlock);
                ThrottleManager.OnOffence(player, trackedPbBlock);
                Log.Info($"PB Block: {trackedPbBlock.ProgrammableBlock?.EntityId} for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}] has exceeded the average-run-time limit.  Allowed Average-Run-Time: {allowedRunTimeAvg:0.0000}ms   Last Run-Time: {lastRunTimeMS:0.0000}ms   Average-Run-Time: {trackedPbBlock.RunTimeMSAvg:0.0000}ms  Offenses: {trackedPbBlock.Offences.Count}");
                
                if (trackedPbBlock.Offences.Count <= allowedOffenses)
                {
                    if (Config.WarnUserOnOffense)
                        Chat.Send(ChatMessages.OffenceWarning(player, trackedPbBlock, trackedPbBlock.Offences.Count, allowedOffenses, "average run-time", allowedRunTimeAvg, trackedPbBlock.RunTimeMSAvg), player.SteamId, Color.Red);
                    
                    if (trackedPbBlock.Offences.Count == allowedOffenses)
                    {
                        MySandboxGame.Static.Invoke(() =>
                        {
                            trackedPbBlock.ProgrammableBlock?.Run($"GracefulShutDown::-2", UpdateType.Script);
                        }, "Advanced_PB_Limiter");
                    }
                    return;
                }
                
                StringBuilder sb = new();
                sb.AppendLine();
                sb.AppendLine($"Your Limit: [{allowedRunTimeAvg:0.0000}ms]");
                sb.AppendLine($"Your Usage: [{trackedPbBlock.RunTimeMSAvg:0.0000}ms]");
                PunishPB(player, trackedPbBlock, PunishReason.AverageRuntimeOverLimit, null, sb.ToString());
            }
        }
        
        public static void PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn($"Attempted to punish a programmable block that was null! for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock?.CustomName}]");
                return;
            }
            
            int gracePeriodSeconds = Config.GracefulShutDownRequestDelay;
            
            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer privilegedPlayer))
                gracePeriodSeconds = privilegedPlayer.GracefulShutDownRequestDelay;
            
            if (reason == PunishReason.ExtremeUsage || gracePeriodSeconds == 0)
            {
                DiscordAutoReporter.RecordPunishment(player, trackedPbBlock, reason.ToString());
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return;
            }

            // The block stays marked as "shutting down" for the whole delay, so it cannot be punished a second time meanwhile.
            // (It used to be un-marked right away, before the delay even started, so the mark did nothing.)
            long entityId = trackedPbBlock.ProgrammableBlock.EntityId;
            if (!GracefulShutdownsInProgress.TryAdd(entityId, player.SteamId)) return;

            // Announced once per punishment, not again every time the combined check asks while the delay is still running.
            DiscordAutoReporter.RecordPunishment(player, trackedPbBlock, reason.ToString());
            
            MySandboxGame.Static.Invoke(() =>
            {
                trackedPbBlock.ProgrammableBlock.Run($"GracefulShutDown::{gracePeriodSeconds}", UpdateType.Script);
            }, "Advanced_PB_Limiter");
            
            Task.Delay(gracePeriodSeconds * 1000).ContinueWith(delayTask =>
            {
                try
                {
                    PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Could not punish a programmable block after its graceful shutdown delay.");
                }
                finally
                {
                    GracefulShutdownsInProgress.TryRemove(entityId, out _);
                }
            });
        }

        private static void PerformPunish(TrackedPBBlock trackedPbBlock, TrackedPlayer player, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null)
            {
                Log.Warn("Attempted to punish a programmable block that was null!  Grid: " + trackedPbBlock.GridName);
                return;
            }
            
            Log.Trace($"Punishing PB Block: {trackedPbBlock.ProgrammableBlock.EntityId} on grid {trackedPbBlock.ProgrammableBlock.CubeGrid.DisplayName} for {reason}. {notes}");
            trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();

            string? fingerprint = ScriptBlacklistManager.GetFingerprint(trackedPbBlock.ProgrammableBlock);
            if (fingerprint != null)
                Log.Info($"Script fingerprint for [{trackedPbBlock.GridName} >> {trackedPbBlock.ProgrammableBlock.CustomName}] (block {trackedPbBlock.ProgrammableBlock.EntityId}): {fingerprint}  Use !PB BlacklistScript {trackedPbBlock.ProgrammableBlock.EntityId} to block this script.");

            // A privileged player's own punishment type applies to them (it was stored and shown, but never used).
            punishment ??= Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer? privilegedPlayer)
                ? privilegedPlayer.Punishment
                : Config.Punishment;

            BlockActionLog.Record(trackedPbBlock.ProgrammableBlock, $"Punished ({punishment})", $"{reason}. {notes}", restorable: punishment == Punishment.TurnOff);

            // The numbers that got the block punished must not count again: they used to stay, so the combined check
            // (every 10 seconds) kept finding the same stale values and punished the same block over and over.
            trackedPbBlock.ResetAfterPunishment();
            
            switch (punishment)
            {
                case Punishment.TurnOff:
                    PerformPunishment_TurnOff(player, trackedPbBlock, reason, notes);
                    return;
                
                case Punishment.Destroy:
                    PerformPunishment_Destroy(player, trackedPbBlock, reason, notes);
                    return;
                
                case Punishment.Damage:
                    PerformPunishment_Damage(player, trackedPbBlock, reason, notes);
                    return;
            }
        }

        private static void PerformPunishment_TurnOff(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, "turned off", notes), player.SteamId, Color.Red);
            // Reflect only when needed.
            needsInstansiationField = typeof(MyProgrammableBlock).GetField("m_needsInstantiation", BindingFlags.NonPublic | BindingFlags.Instance);
            Terminate = typeof(MyProgrammableBlock).GetMethod("OnProgramTermination", BindingFlags.NonPublic | BindingFlags.Instance);
            
            // Turn off the block
            if (trackedPbBlock.ProgrammableBlock is not null)
                MySandboxGame.Static.Invoke(() =>
                {
                    trackedPbBlock.ProgrammableBlock.Enabled = false;
                    needsInstansiationField?.SetValue(trackedPbBlock.ProgrammableBlock, false);
                    Terminate?.Invoke(trackedPbBlock.ProgrammableBlock, new object[] {  MyProgrammableBlock.ScriptTerminationReason.InstructionOverflow});
                    trackedPbBlock.ProgrammableBlock.Enabled = false;
                }, "Advanced_PB_Limiter");    
        }
        
        private static void PerformPunishment_Destroy(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, "destroyed", notes), player.SteamId, Color.Red);
            // Reflect only when needed.
            needsInstansiationField = typeof(MyProgrammableBlock).GetField("m_needsInstantiation", BindingFlags.NonPublic | BindingFlags.Instance);
            Terminate = typeof(MyProgrammableBlock).GetMethod("OnProgramTermination", BindingFlags.NonPublic | BindingFlags.Instance);
            
            // Destroy the block
            if (trackedPbBlock.ProgrammableBlock is not null)
            {
                MySandboxGame.Static.Invoke(() =>
                {
                    try
                    {
                        trackedPbBlock.ProgrammableBlock.Enabled = false;
                        needsInstansiationField?.SetValue(trackedPbBlock.ProgrammableBlock, false);
                        Terminate?.Invoke(trackedPbBlock.ProgrammableBlock, new object[] {  MyProgrammableBlock.ScriptTerminationReason.InstructionOverflow});
                        
                        // Check for SafeZone
                        // Yes, I tried the other method, MySessionComponentSafeZones... but it doesn't always return correctly. 

                        BoundingBoxD blockBoundingBoxD = trackedPbBlock.ProgrammableBlock.SlimBlock.WorldAABB;
                        List<MyEntity>? foundEntities = MyEntities.GetEntitiesInAABB(ref blockBoundingBoxD);
                        MySafeZone? safeZone = null;
                        foreach (MyEntity foundEntity in foundEntities)
                        {
                            if (foundEntity is not MySafeZone foundZone) continue;
                            safeZone = foundZone;
                            break;
                        }

                        if (safeZone is not null && (safeZone.AllowedActions & MySafeZoneAction.Damage) != MySafeZoneAction.Damage)
                        {
                            string blockDisplayName = trackedPbBlock.ProgrammableBlock.CustomName.ToString(); // need this for resync!
                            trackedPbBlock.ProgrammableBlock.Enabled = false; // Turn off because it won't need a recompile and restarts when rebuilt by player.
                            trackedPbBlock.ProgrammableBlock.SlimBlock.FullyDismount(new MyInventory());
                            PunishReSync(blockDisplayName);
                        }
                        else
                            trackedPbBlock.ProgrammableBlock.SlimBlock.DoDamage(trackedPbBlock.ProgrammableBlock.SlimBlock.BuildIntegrity - 100, MyDamageType.Unknown, true, null, 0);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Error while destroying a programmable block");
                    }

                }, "Advanced_PB_Limiter");
            }
        }
        
        private static void PerformPunishment_Damage(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, "damaged", notes), player.SteamId, Color.Red);
            // Reflect only when needed.
            needsInstansiationField = typeof(MyProgrammableBlock).GetField("m_needsInstantiation", BindingFlags.NonPublic | BindingFlags.Instance);
            Terminate = typeof(MyProgrammableBlock).GetMethod("OnProgramTermination", BindingFlags.NonPublic | BindingFlags.Instance);
            
            int damageAmount = Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer? privilegedPlayer)
                ? privilegedPlayer.DamageAmount
                : Config.PunishDamageAmount;

            // Damage the block
            if (trackedPbBlock.ProgrammableBlock is not null)
                MySandboxGame.Static.Invoke(() =>
                {
                    needsInstansiationField?.SetValue(trackedPbBlock.ProgrammableBlock, false);

                    BoundingBoxD blockBoundingBoxD = trackedPbBlock.ProgrammableBlock.SlimBlock.WorldAABB;
                    List<MyEntity>? foundEntities = MyEntities.GetEntitiesInAABB(ref blockBoundingBoxD);
                    MySafeZone? safeZone = null;
                    foreach (MyEntity foundEntity in foundEntities)
                    {
                        if (foundEntity is not MySafeZone foundZone) continue;
                        safeZone = foundZone;
                        break;
                    }
                    
                    if ((safeZone is not null && (safeZone.AllowedActions & MySafeZoneAction.Damage) != MySafeZoneAction.Damage) || Config.KillProtectedBlock)
                    {
                        string blockDisplayName = trackedPbBlock.ProgrammableBlock.CustomName.ToString(); // need this for resync!
                        trackedPbBlock.ProgrammableBlock.Enabled = false; // Turn off because it won't need a recompile and restarts when rebuilt by player.
                        trackedPbBlock.ProgrammableBlock.SlimBlock.FullyDismount(new MyInventory());
                        PunishReSync(blockDisplayName);
                        return;
                    }
                    
                    trackedPbBlock.ProgrammableBlock.SlimBlock.DoDamage(damageAmount, MyDamageType.Unknown, true, null, 0);
                }, "Advanced_PB_Limiter");
        }
        
        private static string CreateUserMessage(TrackedPlayer player, TrackedPBBlock pbBlock, PunishReason reason, string action, string? message = null)
        {
            if (!Config.SendPunishmentMessage) return "";

            if (player.SteamId == 0 || MySession.Static.Players.IdentityIsNpc(player.PlayerId) || !MySession.Static.Players.IsPlayerOnline(player.PlayerId))
            {
                Log.Warn($"No punishment message was sent: SteamID[{player.SteamId}]  IsNPC[{MySession.Static.Players.IdentityIsNpc(player.PlayerId)}]  IsOnline[{MySession.Static.Players.IsPlayerOnline(player.PlayerId)}]  GridName[{pbBlock.GridName}]");
                return "";
            }

            string reasonText = reason switch
            {
                PunishReason.AverageRuntimeOverLimit => "excessive average run time",
                PunishReason.CombinedAverageRuntimeOverLimit => "excessive combined average run time",
                PunishReason.SingleRuntimeOverLimit => "excessive run time",
                PunishReason.CombinedRuntimeOverLimit => "excessive combined run time",
                PunishReason.ExtremeUsage => "extreme usage",
                _ => "an unknown reason, please check with server admins"
            };

            return ChatMessages.Punishment(player, pbBlock, reasonText, action, message);
        }

        private static void PunishReSync(string blockDisplayName)
        {
            // Update state to all players, by Jim and slight modification by Senx for better performance.
            MyReplicationServer? replicationServer = (MyReplicationServer)MyMultiplayer.ReplicationLayer;
            foreach (MyPlayer onlinePlayer in MySession.Static.Players.GetOnlinePlayers())
            {
                Endpoint playerEndpoint = new (onlinePlayer.Client.SteamUserId, 0);
                IDictionary? clientDataDict = _clientStates.Invoke(replicationServer);
                object clientData;
                try
                {
                    clientData = clientDataDict[playerEndpoint];
                }
                catch
                {
                    return;
                }
                    
                MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>? clientReplicables = _replicables.Invoke(clientData);

                List<IMyReplicable> replicableList = new (clientReplicables.Count);
                foreach (KeyValuePair<IMyReplicable, MyReplicableClientData> pair in clientReplicables)
                {
                    ReadOnlySpan<char> instanceName = pair.Key.InstanceName.AsSpan();
                    if (instanceName.IsEmpty) continue;
                    if (!instanceName.StartsWith("MyP".AsSpan())) continue; // Quick and Dirty
                    if (!instanceName.StartsWith("MyProgrammableBlock".AsSpan())) continue;
                    if (!instanceName.EndsWith(blockDisplayName.AsSpan())) continue;
                    replicableList.Add(pair.Key);
                }

                foreach (IMyReplicable? replicable in replicableList)
                {
                    _removeForClient.Invoke(replicationServer, replicable, clientData, true);
                    _forceReplicable.Invoke(replicationServer, replicable, playerEndpoint);
                }
            }
        }
    }
}