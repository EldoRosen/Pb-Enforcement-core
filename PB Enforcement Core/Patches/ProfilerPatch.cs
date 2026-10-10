using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Metadata;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;
using Torch.Utils;
using Torch.Utils.Reflected;
using VRage;
using VRage.Library.Net;
using VRage.Sync;
using static Advanced_PB_Limiter.Utils.HelperUtils;

// ReSharper disable IdentifierTypo
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Advanced_PB_Limiter.Patches
{
    public static class ProfilerPatch_Torch
    {
        /// <summary>
        /// Gracefully stolen (and heavily modified by me) from the original PB Limiter, Credits to SirHamsterAlot, and Equinox
        /// </summary>
        private static readonly ConcurrentDictionary<long,MyProgrammableBlock?> _disabledProgrammableBlocks = new();
        
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Runtime Processor");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        private static bool ServerSaved = false;
        private static readonly Timer checkforSaveTimer = new(16);
        private static long lastCheckTime = 0;
        private static readonly Dictionary<long, ProfileData> _profileData = new();
        
        // Dict<EntityID, <Dict<UpdateType,Count>>
        private static readonly Dictionary<long, Dictionary<byte, int>> deferStatus  = new();
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "RunSandboxedProgramActionCore")]
        private static readonly MethodInfo? _programmableRun;
        
        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "ExecuteCode")]
        private static readonly MethodInfo? _executeCode;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "Compile")]
        private static readonly MethodInfo? _programmableRecompile;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "UpdateStorage")]
        private static readonly MethodInfo? _programmableSaveMethod;
        
        [ReflectedGetter(Name = "m_currentAssembly")]
        private static readonly Func<MyProgrammableBlock, Assembly> get_Assembly;
        
        [ReflectedGetter(Name = "m_instance")]
        private static readonly Func<MyProgrammableBlock, IMyGridProgram> get_AssemblyInstance;
        
        [ReflectedGetter(Name = "m_runtime")]
        private static readonly Func<MyProgrammableBlock, IMyGridProgramRuntimeInfo> get_Runtime;
        
        private static int Gen0CollectionCount = 0;
        private static int Gen1CollectionCount = 0;
        private static int Gen2CollectionCount = 0;
        
        /// <summary>Forgets everything remembered about the blocks of the session that just ended (their ids are gone, and the objects must not be kept alive).</summary>
        public static void Reset()
        {
            _profileData.Clear();
            deferStatus.Clear();
            _disabledProgrammableBlocks.Clear();
        }

        public static async void Init()
        {
            checkforSaveTimer.Elapsed -= CheckforSaveTimerOnElapsed;
            checkforSaveTimer.Elapsed += CheckforSaveTimerOnElapsed;
            checkforSaveTimer.Start();
            
            if (Config.AllowSelfTurnOnExploit)
                return;
            
            if (Config.RequireRecompileOnRestart)
                return;
            
            // I delay this to give the freshly started server time to stabilize.
            if (Config.DebugReporting)
                Log.Warn("Shutting down all pb's that were off during startup in 30 seconds...");
            
            await Task.Delay(30000);
            
            if (Config.DebugReporting)
                Log.Info("Shutting down all pb's that should not be running....");

            _ = Task.Run( async () =>
            {
                await Task.Delay(10000).ConfigureAwait(false);
                ShutdownPB();
            });
        }

        private static void ShutdownPB()
        {
            MySandboxGame.Static.Invoke(() =>
            {
                foreach (KeyValuePair<long, MyProgrammableBlock?> kvp in _disabledProgrammableBlocks)
                {
                    if (kvp.Value == null) continue;
                    
                    if (Config.DebugReporting)
                        Log.Warn("Shutting down: " + kvp.Value.CustomName);
                    
                    BlockActionLog.Record(kvp.Value, "Kept off", "it was switched off when the server started");
                    kvp.Value.Enabled = false;
                    _disabledProgrammableBlocks.TryRemove(kvp.Key, out _);
                }
            },"PB Enforcement Core");
        }

        private static void CheckforSaveTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (MySession.Static == null) return; // the timer outlives the game session
            if (MySession.Static.ServerSaving)
            {
                lastCheckTime = Stopwatch.GetTimestamp();
                ServerSaved = true;
                return;
            }

            if (lastCheckTime.TicksTillNow_TimeSpan().TotalSeconds > 10)
                ServerSaved = false;
        }

        public static void Patch(PatchContext ctx)
        {
            ReflectedManager.Process(typeof(ProfilerPatch_Torch));
            
            ctx.GetPattern(_programmableRun).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixProfilePb)));
            ctx.GetPattern(_programmableRun).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixProfilePb)));
            
            // ExecuteCode is called way before RunSandboxedProgramActionCore so we will use this place to determine defer actions. 
            ctx.GetPattern(_executeCode).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixExecuteCode)));
            
            ctx.GetPattern(_programmableRecompile).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixRecompilePb)));
            ctx.GetPattern(_programmableRecompile).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixRecompilePb)));
            
            ctx.GetPattern(_programmableSaveMethod).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixSaveMethod)));

            if (Config.DebugReporting)
                Log.Info("Finished Patching!");
        }
        
        /// <summary>
        /// Sim speed below 1 stretches run times; this undoes it. A sim speed of 0 (a frozen or starting server) would divide by
        /// zero and turn every run into "infinite", so it is never allowed below 0.1.
        /// </summary>
        private static double SimTimeDivisor()
        {
            double ratio = Sync.ServerSimulationRatio;
            return ratio >= 1 ? 1 : Math.Max(ratio, 0.1);
        }

        private static long _lastPatchErrorTick;

        private static void ReportPatchError(string hook, Exception e)
        {
            long now = Stopwatch.GetTimestamp();
            long last = System.Threading.Interlocked.Read(ref _lastPatchErrorTick);
            if (last != 0 && now - last < 30 * Stopwatch.Frequency) return;
            System.Threading.Interlocked.Exchange(ref _lastPatchErrorTick, now);
            Log.Error(e, $"Error inside the {hook} hook. The game was allowed to carry on as normal.");
        }

        // ReSharper disable once RedundantAssignment
        private static bool PrefixExecuteCode(MyProgrammableBlock? __instance, ref UpdateType updateSource, ref string response)
        {
            try { return PrefixExecuteCodeCore(__instance, ref updateSource, ref response); }
            catch (Exception e) { ReportPatchError(nameof(PrefixExecuteCode), e); return true; }
        }

        private static bool PrefixExecuteCodeCore(MyProgrammableBlock? __instance, ref UpdateType updateSource, ref string response)
        {
            if (!Config.Enabled) return true;
            if (__instance == null) return true;
            response = string.Empty;

            // Blacklisted scripts never run. Cached per compile, so this is a dictionary lookup for normal scripts.
            if (ScriptBlacklistManager.IsBlocked(__instance)) return false;

            // Throttled blocks: only periodic (Update1/10/100) runs are limited. Triggers, terminal runs, other scripts and shutdown requests always go through.
            if ((updateSource & (UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100)) != 0
                && ThrottleManager.ShouldSkipRun(__instance.EntityId)) return false;

            Gen0CollectionCount = GC.CollectionCount(0);
            Gen1CollectionCount = GC.CollectionCount(1);
            Gen2CollectionCount = GC.CollectionCount(2);
            
            long time = Stopwatch.GetTimestamp();
            if ((Config.DeferRunOnSimspeedRate && Sync.ServerSimulationRatio < Config.DeferRunBelowSimRate) || Config.DeferRunAlways  || Config.DisableUpdate1)
            {
                if (!deferStatus.TryGetValue(__instance.EntityId, out Dictionary<byte, int>? blockDeferStatus) || blockDeferStatus == null)
                {
                    blockDeferStatus = new Dictionary<byte, int>
                    {
                        {1,0},{10,0},{100,0}
                    };
                    deferStatus.Add(__instance.EntityId, blockDeferStatus);
                    
                    // This would only not exist on first run, which is the instantiation run and should go without intervention.
                    return true;
                }

                bool allow = false;
                if ((updateSource & UpdateType.Update1) == UpdateType.Update1)
                {
                    blockDeferStatus[1] ++;
                    if (Config.DisableUpdate1)
                    {
                        updateSource |= UpdateType.Update10;
                    }
                    else
                    {
                        if (blockDeferStatus[1] >= Config.DeferRunCount)
                        {
                            allow = true;
                            blockDeferStatus[1] = 0;
                        }
                    }
                }

                if ((updateSource & UpdateType.Update10) == UpdateType.Update10)
                {
                    blockDeferStatus[10]++;
                    if (blockDeferStatus[10] >= Config.DeferRunCount)
                    {
                        allow = true;
                        blockDeferStatus[10] = 0;
                    }
                }

                if ((updateSource & UpdateType.Update100) == UpdateType.Update100)
                {
                    blockDeferStatus[100]++;
                    if (blockDeferStatus[100] >= Config.DeferRunCount)
                    {
                        allow = true;
                        blockDeferStatus[100] = 0;
                    }
                }

                if (!allow)
                {
                    if (Config.ShowTimingsBurp)
                        Log.Info($"Defer Manager took {time.TicksTillNow_TimeSpan().TotalMilliseconds:N5}ms for {__instance.CustomName}.");
                    
                    return false;
                }
            }
                
            if (Config.ShowTimingsBurp)
                Log.Info($"Defer Manager took {time.TicksTillNow_TimeSpan().TotalMilliseconds:N5}ms for {__instance.CustomName}.");
            
            return true;
        }
        
        private static bool PrefixProfilePb(MyProgrammableBlock? __instance)
        {
            try { return PrefixProfilePbCore(__instance); }
            catch (Exception e) { ReportPatchError(nameof(PrefixProfilePb), e); return true; }
        }

        private static bool PrefixProfilePbCore(MyProgrammableBlock? __instance)
        {
            if (!Config.Enabled) return true;
            
            if (__instance == null)
                return true; // Something funky is going on so we won't track it this pass but don't block it either.
            
            if (!_profileData.ContainsKey(__instance.EntityId))
                _profileData.Add(__instance.EntityId, new ProfileData());
            
            _profileData[__instance.EntityId].TimingStart = 0;

            if (!__instance.Enabled)
            {
                if (!Config.AllowSelfTurnOnExploit)
                {
                    if (Config.RequireRecompileOnRestart)
                    {
                        if (Config.DebugReporting)
                            Log.Warn("Skipping recompile for pb: " + __instance.CustomName);

                        _profileData[__instance.EntityId].Skipped = true;
                        return false;
                    }
                    
                    if (_disabledProgrammableBlocks.TryAdd(__instance.EntityId, __instance))
                    {
                        if (Config.DebugReporting)
                            Log.Warn($"Programmable Block {__instance.CustomName} was not enabled during restart and will be disabled again shortly.");
                    }
                }
            }

            if (TrackingManager.IsMemoryCheckInProgress(__instance.EntityId))
            {
                if (Config.DebugReporting)
                    Log.Warn($"Skipping programmable block[{__instance.CustomName}] run for memory check.");
                
                _profileData[__instance.EntityId].Skipped = true;
                return false;
            }
            
            Gen0CollectionCount = GC.CollectionCount(0);
            Gen1CollectionCount = GC.CollectionCount(1);
            Gen2CollectionCount = GC.CollectionCount(2);

            _profileData[__instance.EntityId].TimingStart = Stopwatch.GetTimestamp();
            return true;
        }
        
        private static void SuffixProfilePb(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            try { SuffixProfilePbCore(ref response, __instance, ref __result); }
            catch (Exception e) { ReportPatchError(nameof(SuffixProfilePb), e); }
        }

        private static void SuffixProfilePbCore(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            if (!Config.Enabled) return;
            if (__instance == null) return; // Something funky is going on, so we won't track it this pass but don't block it either.
            if (!_profileData.ContainsKey(__instance.EntityId)) return; // the run was never set up (plugin switched on or off in between)
            
            double runtime = !Config.UseGameReportedRuntime 
                ? Stopwatch.GetTimestamp().TimeElapsed(_profileData[__instance.EntityId].TimingStart).TotalMilliseconds  
                : (get_Runtime(__instance)?.LastRunTimeMs ?? 0);
            
            long debugStamp = Stopwatch.GetTimestamp();
            
            if (_profileData[__instance.EntityId].Skipped)
            {
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
                _profileData[__instance.EntityId].Skipped = false;
                return;
            }
            
            // GC JIT stuff
            if (Gen0CollectionCount != GC.CollectionCount(0))
            {
                if (Config.DebugReporting) Log.Warn("GC Gen 1 Occured.  Not reporting runtime.");
                return;
            }
            if (Gen1CollectionCount != GC.CollectionCount(1))
            {
                if (Config.DebugReporting) Log.Warn("GC Gen 2 Occured.  Not reporting runtime.");
                return;
            }
            if (Gen2CollectionCount != GC.CollectionCount(2))
            {
                if (Config.DebugReporting) Log.Warn("GC Gen 3 Occured.  Not reporting runtime.");
                return;
            }
            
            if (!_profileData.ContainsKey(__instance.EntityId) || _profileData[__instance.EntityId] == null)
                if (Config.DebugReporting)
                {
                    Log.Warn("Profile data for PB is missing!");
                    return;
                }

            if (string.IsNullOrEmpty(response))
                response = "";
            
            if (!Enum.TryParse(__result.ToString(), true, out MyProgrammableBlock.ScriptTerminationReason scriptTerminationReason))
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
            
            if (_profileData[__instance.EntityId].TimingStart == 0)
                return;
            
            // the pb will still run up to the point of executing the script even when disabled by script termination fault, this will stop some of that.
            // also stops the limiter from checking the block.
            if (__result != MyProgrammableBlock.ScriptTerminationReason.None)
            {
                BlockActionLog.Record(__instance, "Switched off", $"the game stopped the script ({__result})", restorable: true);
                MySandboxGame.Static.Invoke(() => { __instance.Enabled = false;}, "PB Enforcement Core");
                TrackingManager.RemovePBInstanceReference(__instance.EntityId);
                if (Config.DebugReporting)
                {
                    Advanced_PB_Limiter.Log.Info($"{__instance.CustomName} : {runtime / SimTimeDivisor()} :: {runtime / SimTimeDivisor()}");
                    Log.Info("Script Termination Reason:" + __result);
                }

                return;
            }

            if (Config.UseSimTime)
                runtime /= SimTimeDivisor();
            
            if (get_AssemblyInstance(__instance) != null)
                if (!TrackingManager.IsPBInstanceReferenceValid(__instance.EntityId))
                    TrackingManager.AddPBInstanceReference(__instance.EntityId, new ProgramInfo{ ProgramReference = new WeakReference<IMyGridProgram>(get_AssemblyInstance(__instance)), LastUpdate = DateTime.Now, OwnerID = __instance.OwnerId, PB = __instance} );
            
            if (!ServerSaved)
                TrackingManager.UpdateTrackingData(__instance, runtime); 
            
            if (Config.ShowTimingsBurp)
                Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Tracked PB[{__instance.CustomName}] Runtime in: {debugStamp.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N6", CultureInfo.InvariantCulture)}ms");
        }

        private static void PrefixRecompilePb(MyProgrammableBlock? __instance, ref bool __localDisable)
        {
            try { PrefixRecompilePbCore(__instance, ref __localDisable); }
            catch (Exception e) { ReportPatchError(nameof(PrefixRecompilePb), e); }
        }

        private static void PrefixRecompilePbCore(MyProgrammableBlock? __instance, ref bool __localDisable)
        {
            if (!Config.Enabled) return;

            if (__instance is null)
            {
                Log.Warn("PB is null!");
                return;
            }

            if (__instance.Enabled == false && !Config.AllowSelfTurnOnExploit)
            {
                __instance.Enabled = false;
                __localDisable = true;
            }

            TrackingManager.RemovePBInstanceReference(__instance.EntityId);
            ScriptBlacklistManager.Invalidate(__instance.EntityId);
            
            TrackingManager.PBRecompiled(__instance);
        }

        private static void SuffixRecompilePb(MyProgrammableBlock? __instance, ref bool __localDisable)
        {
            try { SuffixRecompilePbCore(__instance, ref __localDisable); }
            catch (Exception e) { ReportPatchError(nameof(SuffixRecompilePb), e); }
        }

        private static void SuffixRecompilePbCore(MyProgrammableBlock? __instance, ref bool __localDisable)
        {
            if (__instance is null) return;
            if (__localDisable)
            {
                BlockActionLog.Record(__instance, "Kept off", "it was off when its script was recompiled");
                __instance.Enabled = false;
            }
        }
        
        private static bool PrefixSaveMethod(MyProgrammableBlock? __instance)
        {
            try { return PrefixSaveMethodCore(__instance); }
            catch (Exception e) { ReportPatchError(nameof(PrefixSaveMethod), e); return true; }
        }

        private static bool PrefixSaveMethodCore(MyProgrammableBlock? __instance)
        {
            if (!Config.Enabled) return true;

            if (__instance is null)
            {
                Log.Warn("Null programmable block instance using the Save() method.. interesting.  If this happens a lot, let SentorX know please.");
                return true;
            }

            if (__instance.Enabled) return true;

            if (!MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && !Config.AllowSelfTurnOnExploit)
            {
                MyIdentity? player = MySession.Static.Players.TryGetIdentity(__instance.OwnerId);
                if (Config.DebugReporting && player is not null)
                    Log.Warn($"{player.DisplayName} ({__instance.OwnerId}) tried to save a programmable block [{__instance.CustomName}] while it was turned off.  This is has been disabled.");
                return false;
            }
            
            if (MySession.Static.Players.IdentityIsNpc(__instance.OwnerId) && !Config.AllowNPCToAutoTurnOn) 
                return false;
            
            return true;
        }
    }
}