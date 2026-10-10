using System;
using System.Diagnostics;
using System.Threading;
using Advanced_PB_Limiter.Settings;
using Sandbox.Game.Multiplayer;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Decides whether script limits should be enforced right now. With "only enforce when the server is struggling"
    /// on, nobody is limited while the server runs well; limits start once sim speed has stayed below the threshold
    /// for a few seconds, and stop again as soon as it recovers.
    /// </summary>
    public static class ServerLoadMonitor
    {
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static long _belowSinceTick; // 0 = the server is currently fine

        public static bool ShouldEnforce()
        {
            if (!Config.EnforceOnlyBelowSimThreshold) return true;

            if (Sync.ServerSimulationRatio >= Config.EnforceOnlyBelowSimRate)
            {
                Interlocked.Exchange(ref _belowSinceTick, 0);
                return false;
            }

            long now = Stopwatch.GetTimestamp();
            long since = Interlocked.CompareExchange(ref _belowSinceTick, now, 0);
            if (since == 0) since = now;

            double sustained = Math.Max(0, Config.EnforceSustainedSeconds);
            return (now - since) >= sustained * Stopwatch.Frequency;
        }

        public static void Reset() => Interlocked.Exchange(ref _belowSinceTick, 0);
    }
}
