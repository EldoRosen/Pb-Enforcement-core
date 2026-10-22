using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
using Sandbox.Game.SessionComponents;
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

namespace Advanced_PB_Limiter.Manager
{
    public static class PunishmentManager
    {
        // Filled in by ReflectedManager.Process(typeof(PunishmentManager)), called from ProfilerPatch_Torch.Patch.
        [ReflectedGetter(Name = "m_clientStates")]
        private static Func<MyReplicationServer, IDictionary>? _clientStates;
        private const string CLIENT_DATA_TYPE_NAME = "VRage.Network.MyClient, VRage";
        [ReflectedGetter(TypeName = CLIENT_DATA_TYPE_NAME, Name = "Replicables")]
        private static Func<object, MyConcurrentDictionary<IMyReplicable, MyReplicableClientData>>? _replicables;
        [ReflectedMethod(Name = "RemoveForClient", OverrideTypeNames = new[] { null, CLIENT_DATA_TYPE_NAME, null })]
        private static Action<MyReplicationServer, IMyReplicable, object, bool>? _removeForClient;
        [ReflectedMethod(Name = "ForceReplicable")]
        private static Action<MyReplicationServer, IMyReplicable, Endpoint>? _forceReplicable;

        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static readonly FieldInfo? NeedsInstantiationField = typeof(MyProgrammableBlock).GetField("m_needsInstantiation", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo? TerminateMethod = typeof(MyProgrammableBlock).GetMethod("OnProgramTermination", BindingFlags.NonPublic | BindingFlags.Instance);
        private static ConcurrentDictionary<long, ulong> GracefulShutdownsInProgress { get; } = new(); // Key: EntityId, Value: SteamId

        public enum PunishReason
        {
            SingleRuntimeOverLimit,
            AverageRuntimeOverLimit,
            CombinedRuntimeOverLimit,
            CombinedAverageRuntimeOverLimit,
            ExtremeUsage
        }

        public static void Reset() => GracefulShutdownsInProgress.Clear();

        public static void CheckForPunishment(TrackedPlayer player, long pbEntityId, double lastRunTimeMS, bool gcAffected)
        {
            if (!Config.Enabled) return;
            if (GracefulShutdownsInProgress.ContainsKey(pbEntityId)) return;

            TrackedPBBlock? trackedPbBlock = player.GetTrackedPB(pbEntityId);
            if (trackedPbBlock is null || trackedPbBlock.IsGone) return;

            if (trackedPbBlock.IsUnderGracePeriod(player.SteamId)) return;

            // Instant kill by run time: several runs over the limit within a few seconds. One slow run is not enough: a single
            // measurement can spike from JIT compiling, the OS pausing the thread, or a garbage collection.
            if (Config.InstantKillThreshold > 0 && lastRunTimeMS > Config.InstantKillThreshold && !gcAffected)
            {
                int needed = Math.Max(1, Config.InstantKillSamples);
                int window = Math.Max(1, Config.InstantKillWindowSeconds);
                int hits = trackedPbBlock.RegisterExtremeSample(window);
                if (hits >= needed)
                {
                    PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, null, $"[{lastRunTimeMS:0.0000}ms; {hits} runs over {Config.InstantKillThreshold:0.##}ms within {window}s]");
                    Log.Warn($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.GridName} with a run-time of {lastRunTimeMS:0.0000}ms ({hits} runs over the limit within {window}s)");
                    return;
                }
            }

            // Instant kill by memory: two scans in a row over the limit.
            if (Config.EnableMemoryMonitoring && Config.PBMemoryThreshold > 0 && trackedPbBlock.memoryUsage > Config.PBMemoryThreshold
                && trackedPbBlock.ConsecutiveMemoryScansOver >= 2)
            {
                string usage = HelperUtils.FormatBytesToKB(trackedPbBlock.memoryUsage);
                PunishPB(player, trackedPbBlock, PunishReason.ExtremeUsage, null, $"[{usage}]");
                Log.Warn($"INSTANT-KILL TRIGGERED: {player.PlayerName} on grid {trackedPbBlock.GridName} with a memory usage of {usage}");
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

            // Forget offences older than the offence window.
            double windowSeconds = TrackingManager.OffenceWindowSeconds(allowedOffenses);
            while (trackedPbBlock.Offences.TryPeek(out long oldest)
                   && oldest.TicksTillNow_TimeSpan().TotalSeconds > windowSeconds
                   && trackedPbBlock.Offences.TryDequeue(out _))
            {
            }

            // Check if enough time since last offense has passed.
            if (trackedPbBlock.LastOffenceTick.TicksTillNow_TimeSpan().TotalSeconds <= Config.GraceAfterOffence)
                return;

            // A script that uses very little server time per second cannot be hurting the server, however long one run takes.
            if (Config.MinLoadMSPerSecondToPunish > 0 && trackedPbBlock.LoadMSPerSecond < Config.MinLoadMSPerSecondToPunish)
                return;

            // Single run (never on a run that a garbage collection made longer)
            if (!gcAffected && allowedRunTime != 0 && lastRunTimeMS > allowedRunTime)
            {
                RegisterOffence(player, trackedPbBlock, allowedOffenses, "run-time", allowedRunTime, lastRunTimeMS,
                    PunishReason.SingleRuntimeOverLimit, -1);
                return; // only one offence per run
            }

            // Average
            double average = trackedPbBlock.RunTimeMSAvg;
            if (allowedRunTimeAvg != 0 && average > allowedRunTimeAvg)
            {
                RegisterOffence(player, trackedPbBlock, allowedOffenses, "average run-time", allowedRunTimeAvg, average,
                    PunishReason.AverageRuntimeOverLimit, -2);
            }
        }

        private static void RegisterOffence(TrackedPlayer player, TrackedPBBlock block, int allowedOffenses, string limitType,
            double limit, double usage, PunishReason reason, int shutdownCode)
        {
            block.Offences.Enqueue(Stopwatch.GetTimestamp());
            block.LastOffenceTick = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref player.Offences);
            DiscordAutoReporter.RecordOffence(player, block);
            ThrottleManager.OnOffence(player, block);

            int count = block.Offences.Count;
            Log.Info($"PB Block: {block.ProgrammableBlock?.EntityId} for [{block.GridName} >> {block.ProgrammableBlock?.CustomName}] has exceeded the {limitType} limit.  Usage: {usage:0.0000}ms  Allowed: {limit:0.0000}ms  Offenses: {count}");

            if (count <= allowedOffenses)
            {
                if (Config.WarnUserOnOffense)
                    Chat.Send(ChatMessages.OffenceWarning(player, block, count, allowedOffenses, limitType, limit, usage), player.SteamId, Color.Red);

                if (count == allowedOffenses && Config.SendGracefulShutdownRequest)
                    RunShutdownRequest(block.ProgrammableBlock, shutdownCode);
                return;
            }

            string notes = $"\nYour Limit: [{limit:0.0000}ms]\nYour Usage: [{usage:0.0000}ms]";
            PunishPB(player, block, reason, null, notes);
        }

        /// <summary>Runs the script with "GracefulShutDown::&lt;code&gt;" on the game thread (only when that option is on).</summary>
        private static void RunShutdownRequest(MyProgrammableBlock? pb, int code)
        {
            if (pb is null) return;
            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    if (!pb.Closed && pb.Enabled) pb.Run($"GracefulShutDown::{code}", UpdateType.Script);
                }
                catch (Exception e)
                {
                    Log.Warn(e, "Could not send the shutdown request to a programmable block.");
                }
            }, "PB Enforcement Core");
        }

        public static void PunishPB(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, Punishment? punishment = null, string? notes = null)
        {
            if (trackedPbBlock.ProgrammableBlock is null || trackedPbBlock.IsGone)
            {
                Log.Warn($"Attempted to punish a programmable block that no longer exists: [{trackedPbBlock.GridName}]");
                return;
            }

            int gracePeriodSeconds = Config.GracefulShutDownRequestDelay;
            if (Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer privilegedPlayer))
                gracePeriodSeconds = privilegedPlayer.GracefulShutDownRequestDelay;

            // The delay only exists to let a script react to the shutdown request; without the request there is nothing to wait for.
            if (!Config.SendGracefulShutdownRequest) gracePeriodSeconds = 0;

            if (reason == PunishReason.ExtremeUsage || gracePeriodSeconds <= 0)
            {
                DiscordAutoReporter.RecordPunishment(player, trackedPbBlock, reason.ToString());
                PerformPunish(trackedPbBlock, player, reason, punishment, notes);
                return;
            }

            // The block stays marked as "shutting down" for the whole delay, so it cannot be punished a second time meanwhile.
            long entityId = trackedPbBlock.ProgrammableBlock.EntityId;
            if (!GracefulShutdownsInProgress.TryAdd(entityId, player.SteamId)) return;

            // Announced once per punishment, not again every time the combined check asks while the delay is still running.
            DiscordAutoReporter.RecordPunishment(player, trackedPbBlock, reason.ToString());
            RunShutdownRequest(trackedPbBlock.ProgrammableBlock, gracePeriodSeconds);

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
            MyProgrammableBlock? pb = trackedPbBlock.ProgrammableBlock;
            if (pb is null || trackedPbBlock.IsGone)
            {
                Log.Warn("Attempted to punish a programmable block that no longer exists.  Grid: " + trackedPbBlock.GridName);
                return;
            }

            Log.Trace($"Punishing PB Block: {pb.EntityId} on grid {trackedPbBlock.GridName} for {reason}. {notes}");
            trackedPbBlock.LastOffenceTick = Stopwatch.GetTimestamp();

            string? fingerprint = ScriptBlacklistManager.GetFingerprint(pb);
            if (fingerprint != null)
                Log.Info($"Script fingerprint for [{trackedPbBlock.GridName} >> {pb.CustomName}] (block {pb.EntityId}): {fingerprint}  Use !PB BlacklistScript {pb.EntityId} to block this script.");

            // A privileged player's own punishment type applies to them.
            punishment ??= Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer? privilegedPlayer)
                ? privilegedPlayer.Punishment
                : Config.Punishment;

            BlockActionLog.Record(pb, $"Punished ({punishment})", $"{reason}. {notes}", restorable: punishment == Punishment.TurnOff);

            // The numbers and offences that got the block punished must not count again: the block starts with a clean record
            // if it is switched back on.
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

        /// <summary>Switches the block off and stops its script (game thread only).</summary>
        private static void StopBlock(MyProgrammableBlock pb)
        {
            pb.Enabled = false;
            NeedsInstantiationField?.SetValue(pb, false);
            TerminateMethod?.Invoke(pb, new object[] { MyProgrammableBlock.ScriptTerminationReason.InstructionOverflow });
            pb.Enabled = false;
        }

        /// <summary>True when the block cannot be damaged where it is (a safe zone, or a mod's protected area, with damage off).</summary>
        private static bool IsProtected(MyProgrammableBlock pb)
        {
            try
            {
                if (pb.CubeGrid != null && !MySessionComponentSafeZones.IsActionAllowed(pb.CubeGrid, MySafeZoneAction.Damage))
                    return true;
            }
            catch (Exception e)
            {
                Log.Debug(e, "Safe zone check failed; falling back to the safe zone scan.");
            }

            // The original check, kept as a second opinion (the game's check above did not always agree with zones in testing).
            BoundingBoxD box = pb.SlimBlock.WorldAABB;
            List<MyEntity> found = MyEntities.GetEntitiesInAABB(ref box);
            try
            {
                foreach (MyEntity entity in found)
                    if (entity is MySafeZone zone && (zone.AllowedActions & MySafeZoneAction.Damage) != MySafeZoneAction.Damage)
                        return true;
            }
            finally
            {
                found.Clear();
            }
            return false;
        }

        /// <summary>Damage that leaves the block just below the integrity it needs to work.</summary>
        private static void DamageBelowFunctional(MySlimBlock slim)
        {
            float target = slim.MaxIntegrity * slim.BlockDefinition.CriticalIntegrityRatio * 0.9f;
            float damage = slim.Integrity - target;
            if (damage <= 0) return; // already below working integrity
            slim.DoDamage(damage, MyDamageType.Unknown, true, null, 0);
        }

        /// <summary>Removes all components from a protected block, then tells clients so they see it as unbuilt (game thread only).</summary>
        private static void Dismount(MyProgrammableBlock pb)
        {
            string blockDisplayName = pb.CustomName.ToString(); // need this for resync!
            pb.Enabled = false; // Turn off because it won't need a recompile and restarts when rebuilt by player.
            pb.SlimBlock.FullyDismount(new MyInventory());
            PunishReSync(blockDisplayName);
        }

        private static void PerformPunishment_TurnOff(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, "turned off", notes), player.SteamId, Color.Red);
            MyProgrammableBlock? pb = trackedPbBlock.ProgrammableBlock;
            if (pb is null) return;

            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    if (pb.Closed) return;
                    StopBlock(pb);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error while switching off a programmable block");
                }
            }, "PB Enforcement Core");
        }

        private static void PerformPunishment_Destroy(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, "destroyed", notes), player.SteamId, Color.Red);
            MyProgrammableBlock? pb = trackedPbBlock.ProgrammableBlock;
            if (pb is null) return;

            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    if (pb.Closed) return;
                    StopBlock(pb);

                    if (IsProtected(pb))
                    {
                        // It cannot be damaged here. Strip it only if the admin asked for that; otherwise it stays switched off.
                        if (Config.KillProtectedBlock) Dismount(pb);
                        return;
                    }

                    DamageBelowFunctional(pb.SlimBlock);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error while destroying a programmable block");
                }
            }, "PB Enforcement Core");
        }

        private static void PerformPunishment_Damage(TrackedPlayer player, TrackedPBBlock trackedPbBlock, PunishReason reason, string? notes = null)
        {
            Chat.Send(CreateUserMessage(player, trackedPbBlock, reason, "damaged", notes), player.SteamId, Color.Red);
            MyProgrammableBlock? pb = trackedPbBlock.ProgrammableBlock;
            if (pb is null) return;

            int damageAmount = Config.PrivilegedPlayers.TryGetValue(player.SteamId, out PrivilegedPlayer? privilegedPlayer)
                ? privilegedPlayer.DamageAmount
                : Config.PunishDamageAmount;

            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    if (pb.Closed) return;
                    NeedsInstantiationField?.SetValue(pb, false);

                    if (IsProtected(pb))
                    {
                        // Damage would do nothing here: strip the block if the admin asked for that, otherwise switch it off.
                        if (Config.KillProtectedBlock) Dismount(pb);
                        else StopBlock(pb);
                        return;
                    }

                    if (damageAmount > 0)
                        pb.SlimBlock.DoDamage(damageAmount, MyDamageType.Unknown, true, null, 0);
                }
                catch (Exception e)
                {
                    Log.Error(e, "Error while damaging a programmable block");
                }
            }, "PB Enforcement Core");
        }

        private static string CreateUserMessage(TrackedPlayer player, TrackedPBBlock pbBlock, PunishReason reason, string action, string? message = null)
        {
            if (!Config.SendPunishmentMessage) return "";
            if (MySession.Static?.Players == null) return "";

            if (player.SteamId == 0 || MySession.Static.Players.IdentityIsNpc(player.PlayerId) || !MySession.Static.Players.IsPlayerOnline(player.PlayerId))
            {
                Log.Info($"No punishment message was sent (player offline, NPC or unknown): SteamID[{player.SteamId}]  GridName[{pbBlock.GridName}]");
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

        /// <summary>Makes clients reload a dismounted block so they see it unbuilt (game thread only). Skipped if the reflected members are missing.</summary>
        private static void PunishReSync(string blockDisplayName)
        {
            if (_clientStates == null || _replicables == null || _removeForClient == null || _forceReplicable == null)
            {
                Log.Warn("Could not resync a dismounted block to players (replication members not found). Players see it correctly after re-logging.");
                return;
            }

            // Update state to all players, by Jim and slight modification by Senx for better performance.
            if (MyMultiplayer.ReplicationLayer is not MyReplicationServer replicationServer) return;
            IDictionary clientDataDict = _clientStates.Invoke(replicationServer);

            foreach (MyPlayer onlinePlayer in MySession.Static.Players.GetOnlinePlayers())
            {
                try
                {
                    Endpoint playerEndpoint = new (onlinePlayer.Client.SteamUserId, 0);
                    if (!clientDataDict.Contains(playerEndpoint)) continue; // this player has no replication state yet: the others still need the resync
                    object clientData = clientDataDict[playerEndpoint];

                    MyConcurrentDictionary<IMyReplicable, MyReplicableClientData> clientReplicables = _replicables.Invoke(clientData);

                    List<IMyReplicable> replicableList = new (clientReplicables.Count);
                    foreach (KeyValuePair<IMyReplicable, MyReplicableClientData> pair in clientReplicables)
                    {
                        string? instanceName = pair.Key.InstanceName;
                        if (string.IsNullOrEmpty(instanceName)) continue;
                        if (!instanceName.StartsWith("MyProgrammableBlock", StringComparison.Ordinal)) continue;
                        if (!instanceName.EndsWith(blockDisplayName, StringComparison.Ordinal)) continue;
                        replicableList.Add(pair.Key);
                    }

                    foreach (IMyReplicable replicable in replicableList)
                    {
                        _removeForClient.Invoke(replicationServer, replicable, clientData, true);
                        _forceReplicable.Invoke(replicationServer, replicable, playerEndpoint);
                    }
                }
                catch (Exception e)
                {
                    Log.Warn(e, $"Could not resync a dismounted block to {onlinePlayer.DisplayName}.");
                }
            }
        }
    }
}
