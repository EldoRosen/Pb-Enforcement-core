using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox.Game.World;
using VRageMath;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// A gentler step before punishment: a block that just got an offence has its periodic runs (Update1/10/100)
    /// limited to a few per second for a while. Triggered runs (button, timer, other scripts, shutdown requests) are never throttled.
    /// </summary>
    public static class ThrottleManager
    {
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Throttle");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        private sealed class State
        {
            public long UntilTick;
            public long LastAllowedTick;
        }

        private static readonly ConcurrentDictionary<long, State> States = new();

        /// <summary>A block just received an offence. Starts (or extends) its throttle.</summary>
        public static void OnOffence(TrackedPlayer player, TrackedPBBlock block)
        {
            try
            {
                if (!Config.ThrottleBeforePunish) return;
                long id = block.ProgrammableBlock?.EntityId ?? 0;
                if (id == 0) return;

                long until = Stopwatch.GetTimestamp() + (long)(Math.Max(1, Config.ThrottleDurationSeconds) * (double)Stopwatch.Frequency);
                bool started = false;
                States.AddOrUpdate(id,
                    _ => { started = true; return new State { UntilTick = until }; },
                    (_, existing) => { existing.UntilTick = until; return existing; });

                if (started && Config.ThrottleNotifyOwner && player.SteamId != 0 && MySession.Static.Players.IsPlayerOnline(player.PlayerId))
                    Chat.Send(ChatMessages.Throttle(player, block), player.SteamId, Color.Orange);
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not start a throttle.");
            }
        }

        /// <summary>True when this periodic run should be skipped.</summary>
        public static bool ShouldSkipRun(long entityId)
        {
            if (!Config.ThrottleBeforePunish || States.IsEmpty) return false;
            if (!States.TryGetValue(entityId, out State? state) || state is null) return false;

            long now = Stopwatch.GetTimestamp();
            if (now > state.UntilTick)
            {
                States.TryRemove(entityId, out _);
                return false;
            }

            double minInterval = 1.0 / Math.Max(0.1, Config.ThrottleMaxRunsPerSecond);
            if ((now - state.LastAllowedTick) / (double)Stopwatch.Frequency < minInterval) return true;

            state.LastAllowedTick = now;
            return false;
        }

        public static void Reset() => States.Clear();
    }
}
