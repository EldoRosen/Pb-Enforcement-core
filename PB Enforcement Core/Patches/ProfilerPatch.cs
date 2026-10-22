using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using ExtensionMethods;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using Torch.Managers.PatchManager;
using Torch.Utils;
using Timer = System.Timers.Timer;

// ReSharper disable IdentifierTypo
#pragma warning disable CS0649 // Field is never assigned to, and will always have its default value

namespace Advanced_PB_Limiter.Patches
{
    public static class ProfilerPatch_Torch
    {
        /// <summary>
        /// Gracefully stolen (and heavily modified by me) from the original PB Limiter, Credits to SirHamsterAlot, and Equinox
        /// </summary>
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Runtime Processor");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        // "Once" counts as periodic: a script that asks for Once again on every run runs every tick, exactly like Update1, and
        // in 3.1.0/3.1.1 that slipped past throttling, deferral and "Disable Update1". A skipped Once run is asked for again
        // (RearmOnce), because the game clears the Once request before the run and the script would otherwise never run again.
        private const UpdateType PeriodicUpdates = UpdateType.Update1 | UpdateType.Update10 | UpdateType.Update100 | UpdateType.Once;

        // ---- blocks that were off when the server started (see Init / ShutdownPB)
        private const int StartupShutdownDelaySeconds = 40;
        private static readonly ConcurrentDictionary<long, MyProgrammableBlock> _disabledAtStartup = new();
        private static volatile bool _recordingDisabledAtStartup;
        private static int _sessionGeneration;

        // ---- world save detection
        private static volatile bool ServerSaved;
        private static readonly Timer checkforSaveTimer = new(16);
        private static long lastCheckTime;

        // ---- per block run state. Concurrent: the session thread clears them while the game thread may still be running scripts.
        private static readonly ConcurrentDictionary<long, ProfileData> _profileData = new();
        private static readonly ConcurrentDictionary<long, int[]> deferStatus = new(); // [Update1, Update10, Update100] counters

        /// <summary>Blocks whose run is in progress on this thread, innermost last. A script can run another one (TryRun) inside its own run.</summary>
        [ThreadStatic] private static List<ProfileData>? _runStack;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "RunSandboxedProgramActionCore")]
        private static readonly MethodInfo? _programmableRun;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "ExecuteCode")]
        private static readonly MethodInfo? _executeCode;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "Compile")]
        private static readonly MethodInfo? _programmableRecompile;

        [ReflectedMethodInfo(typeof(MyProgrammableBlock), "UpdateStorage")]
        private static readonly MethodInfo? _programmableSaveMethod;

        [ReflectedGetter(Name = "m_instance")]
        private static readonly Func<MyProgrammableBlock, IMyGridProgram> get_AssemblyInstance;

        [ReflectedGetter(Name = "m_runtime")]
        private static readonly Func<MyProgrammableBlock, IMyGridProgramRuntimeInfo> get_Runtime;

        /// <summary>A run that carried "Once" was skipped: ask for it again, so the script runs on a later tick instead of never.</summary>
        private static void RearmOnce(MyProgrammableBlock pb, UpdateType updateSource)
        {
            if ((updateSource & UpdateType.Once) == 0) return;
            IMyGridProgramRuntimeInfo? runtime = get_Runtime?.Invoke(pb);
            if (runtime != null) runtime.UpdateFrequency |= UpdateFrequency.Once;
        }

        /// <summary>Forgets everything remembered about the blocks of the session that just ended (their ids are gone, and the objects must not be kept alive).</summary>
        public static void Reset()
        {
            Interlocked.Increment(ref _sessionGeneration); // a startup shutdown still waiting for the old session must not run
            _recordingDisabledAtStartup = false;
            _profileData.Clear();
            deferStatus.Clear();
            _disabledAtStartup.Clear();
        }

        /// <summary>
        /// Called when the world starts loading. Scripts are compiled and constructed while the world loads, before the session
        /// counts as loaded, so blocks that are off have to be noted from this point (3.1.0 started too late and missed them).
        /// </summary>
        public static void BeginLoading()
        {
            _disabledAtStartup.Clear();
            _recordingDisabledAtStartup = true;
        }

        /// <summary>Called when the session has loaded.</summary>
        public static void Init()
        {
            try
            {
                checkforSaveTimer.Elapsed -= CheckforSaveTimerOnElapsed;
                checkforSaveTimer.Elapsed += CheckforSaveTimerOnElapsed;
                checkforSaveTimer.Start();

                if (Config.UseSimTime || Config.UseGameReportedRuntime)
                    Log.Info("\"Use SimTime\" and \"Use Game Time\" are no longer used: run times are always measured by the plugin and never scaled by sim speed.");

                if (Config.AllowSelfTurnOnExploit || Config.RequireRecompileOnRestart)
                {
                    _recordingDisabledAtStartup = false;
                    _disabledAtStartup.Clear();
                    return;
                }

                // Blocks that were off when the world loaded are noted from the start of loading until 40 seconds after it. Then any
                // of them that switched itself back on (a constructor or Save() turning the block on) is switched off again.
                _recordingDisabledAtStartup = true;
                int generation = Interlocked.Increment(ref _sessionGeneration);

                if (Config.DebugReporting)
                    Log.Warn($"Programmable blocks that were off at startup will be kept off in {StartupShutdownDelaySeconds} seconds.");

                Task.Delay(TimeSpan.FromSeconds(StartupShutdownDelaySeconds)).ContinueWith(_ =>
                {
                    try
                    {
                        if (generation != Volatile.Read(ref _sessionGeneration)) return; // the session ended meanwhile
                        _recordingDisabledAtStartup = false;
                        ShutdownPB();
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Could not switch off the programmable blocks that were off at startup.");
                    }
                });
            }
            catch (Exception e)
            {
                Log.Error(e, "Could not start the runtime processor.");
            }
        }

        private static void ShutdownPB()
        {
            List<MyProgrammableBlock> blocks = new(_disabledAtStartup.Values);
            _disabledAtStartup.Clear();
            if (blocks.Count == 0) return;

            MySandboxGame.Static.Invoke(() =>
            {
                foreach (MyProgrammableBlock pb in blocks)
                {
                    try
                    {
                        if (pb.Closed || pb.MarkedForClose || !pb.Enabled) continue; // gone, or still off: nothing to do

                        if (Config.DebugReporting)
                            Log.Warn("Switching off again: " + pb.CustomName);

                        BlockActionLog.Record(pb, "Kept off", "it was off when the server started and switched itself on during startup");
                        pb.Enabled = false;
                    }
                    catch (Exception e)
                    {
                        Log.Warn(e, "Could not switch off a programmable block that was off at startup.");
                    }
                }
            }, "PB Enforcement Core");
        }

        private static void CheckforSaveTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            if (MySession.Static == null) return; // the timer outlives the game session
            if (MySession.Static.ServerSaving)
            {
                Interlocked.Exchange(ref lastCheckTime, Stopwatch.GetTimestamp());
                ServerSaved = true;
                return;
            }

            if (Interlocked.Read(ref lastCheckTime).TicksTillNow_TimeSpan().TotalSeconds > 10)
                ServerSaved = false;
        }

        public static void Patch(PatchContext ctx)
        {
            ReflectedManager.Process(typeof(ProfilerPatch_Torch));

            // The punishment code uses reflected members of the replication layer. They were never processed, so they stayed
            // null and resyncing a dismounted block threw on the game thread.
            try { ReflectedManager.Process(typeof(PunishmentManager)); }
            catch (Exception e) { Log.Warn(e, "Could not look up the replication members used to resync punished blocks. Blocks will still be punished."); }

            ctx.GetPattern(_programmableRun).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixProfilePb)));
            ctx.GetPattern(_programmableRun).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixProfilePb)));

            // ExecuteCode is called before RunSandboxedProgramActionCore, so it decides whether a run happens at all.
            ctx.GetPattern(_executeCode).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixExecuteCode)));

            ctx.GetPattern(_programmableRecompile).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixRecompilePb)));
            ctx.GetPattern(_programmableRecompile).Suffixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(SuffixRecompilePb)));

            ctx.GetPattern(_programmableSaveMethod).Prefixes.Add(ReflectionUtils.StaticMethod(typeof(ProfilerPatch_Torch), nameof(PrefixSaveMethod)));

            if (Config.DebugReporting)
                Log.Info("Finished Patching!");
        }

        private static long _lastPatchErrorTick;

        private static void ReportPatchError(string hook, Exception e)
        {
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastPatchErrorTick);
            if (last != 0 && now - last < 30 * Stopwatch.Frequency) return;
            Interlocked.Exchange(ref _lastPatchErrorTick, now);
            Log.Error(e, $"Error inside the {hook} hook. The game was allowed to carry on as normal.");
        }

        // ------------------------------------------------------------------ ExecuteCode: does this run happen at all?

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

            // Blacklisted scripts never run. Cached per compile, so this is a dictionary lookup for normal scripts.
            if (ScriptBlacklistManager.IsBlocked(__instance))
            {
                response = string.Empty;
                return false;
            }

            // Only purely periodic runs (Update1/10/100) are ever slowed down. A run that also carries a trigger, terminal run,
            // another script, IGC, an antenna message or a mod call always goes through.
            UpdateType periodic = updateSource & PeriodicUpdates;
            if (periodic == 0 || (updateSource & ~PeriodicUpdates) != 0) return true;

            long time = Config.ShowTimingsBurp ? Stopwatch.GetTimestamp() : 0;

            if (ThrottleManager.ShouldSkipRun(__instance.EntityId))
            {
                RearmOnce(__instance, updateSource);
                response = string.Empty;
                return false;
            }

            int deferCount = Config.DeferRunCount;
            bool deferActive = deferCount >= 2 &&
                               (Config.DeferRunAlways || (Config.DeferRunOnSimspeedRate && Sync.ServerSimulationRatio < Config.DeferRunBelowSimRate));
            bool limitUpdate1 = Config.DisableUpdate1;
            if (!deferActive && !limitUpdate1) return true;

            int[] counters = deferStatus.GetOrAdd(__instance.EntityId, _ => new int[3]);
            bool allow = false;

            if ((periodic & (UpdateType.Update1 | UpdateType.Once)) != 0)
            {
                // "Disable Update1": an Update1 (or Once-every-tick) script runs as often as an Update10 one (once every 10 ticks).
                int every = (limitUpdate1 ? 10 : 1) * (deferActive ? deferCount : 1);
                if (++counters[0] >= every)
                {
                    counters[0] = 0;
                    allow = true;
                    if (limitUpdate1) updateSource |= UpdateType.Update10;
                }
            }

            if ((periodic & UpdateType.Update10) != 0)
            {
                if (!deferActive || ++counters[1] >= deferCount)
                {
                    counters[1] = 0;
                    allow = true;
                }
            }

            if ((periodic & UpdateType.Update100) != 0)
            {
                if (!deferActive || ++counters[2] >= deferCount)
                {
                    counters[2] = 0;
                    allow = true;
                }
            }

            if (Config.ShowTimingsBurp)
                Log.Info($"Defer Manager took {time.TicksTillNow_TimeSpan().TotalMilliseconds:N5}ms for {__instance.CustomName}.");

            if (!allow)
            {
                RearmOnce(__instance, updateSource);
                response = string.Empty;
            }
            return allow;
        }

        // ------------------------------------------------------------------ RunSandboxedProgramActionCore: measuring the run

        private static bool PrefixProfilePb(MyProgrammableBlock? __instance)
        {
            try { return PrefixProfilePbCore(__instance); }
            catch (Exception e) { ReportPatchError(nameof(PrefixProfilePb), e); return true; }
        }

        private static bool PrefixProfilePbCore(MyProgrammableBlock? __instance)
        {
            if (!Config.Enabled) return true;
            if (__instance == null) return true; // Something funky is going on so we won't track it this pass but don't block it either.

            long entityId = __instance.EntityId;
            ProfileData data = _profileData.GetOrAdd(entityId, id => new ProfileData { EntityId = id });
            data.Started = false;
            data.Skipped = false; // a skip whose suffix never ran must not swallow this run's result
            data.TimingStart = 0;
            data.ChildTicks = 0;

            if (!__instance.Enabled && !Config.AllowSelfTurnOnExploit)
            {
                if (Config.RequireRecompileOnRestart)
                {
                    if (Config.DebugReporting)
                        Log.Warn("Skipping recompile for pb: " + __instance.CustomName);

                    data.Skipped = true;
                    return false;
                }

                // Only during startup: later on, a block that is off simply is off, and remembering it would keep its grid in memory.
                if (_recordingDisabledAtStartup && _disabledAtStartup.TryAdd(entityId, __instance) && Config.DebugReporting)
                    Log.Warn($"Programmable Block {__instance.CustomName} was not enabled during restart and will be kept off.");
            }

            // Drop entries whose run already ended or never reached its suffix (another plugin cancelling the run, an exception).
            // Without this, a stale copy of this same block below it would be charged its own time and it would read 0 ms forever.
            List<ProfileData> stack = _runStack ??= new List<ProfileData>(4);
            stack.RemoveAll(entry => !entry.Started || ReferenceEquals(entry, data));
            if (stack.Count > 64) stack.Clear(); // should not happen: do not grow forever
            stack.Add(data);

            data.GcCountAtStart = GC.CollectionCount(0); // a collection of any generation also counts as a generation 0 collection
            data.Started = true;
            data.TimingStart = Stopwatch.GetTimestamp();
            return true;
        }

        private static void SuffixProfilePb(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            try { SuffixProfilePbCore(ref response, __instance, ref __result); }
            catch (Exception e) { ReportPatchError(nameof(SuffixProfilePb), e); }
        }

        private static void SuffixProfilePbCore(ref string response, MyProgrammableBlock? __instance, ref MyProgrammableBlock.ScriptTerminationReason __result)
        {
            long end = Stopwatch.GetTimestamp();
            if (__instance == null) return;
            if (!_profileData.TryGetValue(__instance.EntityId, out ProfileData? data) || data == null) return;

            if (data.Skipped)
            {
                data.Skipped = false;
                __result = MyProgrammableBlock.ScriptTerminationReason.None;
                if (response == null) response = string.Empty;
                return;
            }

            if (!data.Started) return; // the run was not set up (the plugin was switched on in between)
            data.Started = false;

            // This run's own time is worked out before anything else touches ChildTicks.
            long elapsed = Math.Max(0, end - data.TimingStart);
            long ownTicks = Math.Max(0, elapsed - data.ChildTicks);

            // Take this run off the stack, and charge its time to the block that called it (if any) as "time spent elsewhere".
            List<ProfileData>? stack = _runStack;
            if (stack != null)
            {
                int index = stack.LastIndexOf(data);
                if (index >= 0) stack.RemoveRange(index, stack.Count - index);
                if (stack.Count > 0)
                {
                    ProfileData caller = stack[stack.Count - 1];
                    if (caller.Started && !ReferenceEquals(caller, data)) caller.ChildTicks += elapsed;
                }
            }

            if (!Config.Enabled) return;

            double runtime = ownTicks * 1000.0 / Stopwatch.Frequency;
            bool gcAffected = GC.CollectionCount(0) != data.GcCountAtStart;

            if (__result != MyProgrammableBlock.ScriptTerminationReason.None)
            {
                // The game has already stopped the script and shows the error in the terminal.
                TrackingManager.RemovePBInstanceReference(__instance.EntityId);

                if (Config.TurnOffOnScriptError)
                {
                    MyProgrammableBlock pb = __instance;
                    MyProgrammableBlock.ScriptTerminationReason reason = __result;
                    BlockActionLog.Record(pb, "Switched off", $"the game stopped the script ({reason})", restorable: true);
                    MySandboxGame.Static.Invoke(() =>
                    {
                        try { pb.Enabled = false; }
                        catch (Exception e) { Log.Warn(e, "Could not switch off a block whose script stopped."); }
                    }, "PB Enforcement Core");
                }

                if (Config.DebugReporting)
                    Log.Info($"{__instance.CustomName}: script stopped by the game ({__result}) after {runtime:0.0000}ms.");
            }
            else
            {
                IMyGridProgram? program = get_AssemblyInstance(__instance);
                if (program != null && !TrackingManager.IsPBInstanceReferenceValid(__instance.EntityId))
                    TrackingManager.AddPBInstanceReference(__instance.EntityId, new ProgramInfo
                    {
                        ProgramReference = new WeakReference<IMyGridProgram>(program),
                        LastUpdate = DateTime.Now,
                        OwnerID = __instance.OwnerId,
                        PB = __instance
                    });
            }

            if (!ServerSaved)
                TrackingManager.UpdateTrackingData(__instance, runtime, __instance.OwnerId, gcAffected);

            if (Config.ShowTimingsBurp)
                Log.Info($"[{Advanced_PB_Limiter.Instance?.GetThreadName()} THREAD] Tracked PB[{__instance.CustomName}] Runtime in: {end.TicksTillNow_TimeSpan().TotalMilliseconds.ToString("N6", CultureInfo.InvariantCulture)}ms");
        }

        // ------------------------------------------------------------------ Compile and Save()

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

            // A block that is off when it is recompiled must still be off afterwards (a constructor can switch its own block on).
            if (!__instance.Enabled && !Config.AllowSelfTurnOnExploit)
                __localDisable = true;

            TrackingManager.RemovePBInstanceReference(__instance.EntityId);
            ScriptBlacklistManager.Invalidate(__instance.EntityId);
            deferStatus.TryRemove(__instance.EntityId, out _);

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
            if (__localDisable && __instance.Enabled)
            {
                BlockActionLog.Record(__instance, "Kept off", "it was off when its script was recompiled and the script switched it on");
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
            if (MySession.Static?.Players == null) return true;

            if (__instance is null)
            {
                Log.Warn("Null programmable block instance using the Save() method.. interesting.  If this happens a lot, let SentorX know please.");
                return true;
            }

            if (__instance.Enabled) return true;

            bool isNpc = MySession.Static.Players.IdentityIsNpc(__instance.OwnerId);
            if (!isNpc && !Config.AllowSelfTurnOnExploit)
            {
                MyIdentity? player = MySession.Static.Players.TryGetIdentity(__instance.OwnerId);
                if (Config.DebugReporting && player is not null)
                    Log.Warn($"{player.DisplayName} ({__instance.OwnerId}) tried to save a programmable block [{__instance.CustomName}] while it was turned off.  This is has been disabled.");
                return false;
            }

            if (isNpc && !Config.AllowNPCToAutoTurnOn)
                return false;

            return true;
        }
    }
}
