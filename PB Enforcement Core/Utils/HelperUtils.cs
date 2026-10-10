using System;
using System.Diagnostics;
using System.Globalization;
using static Advanced_PB_Limiter.Utils.HelperUtils;

namespace Advanced_PB_Limiter.Utils
{
    public static class HelperUtils
    {
        public static string FormatBytesToKB(long bytes)
        {
            float kilobytes = bytes / 1024f;
            string formattedKilobytes = kilobytes.ToString("N2", CultureInfo.InvariantCulture);

            return $"{formattedKilobytes} KB";
        }
    }
}

namespace ExtensionMethods
{
    public static class MyExtensions
    {
        /// <summary>
        /// Return a timespan range from the Stopwatch.GetTimeStamp() until now.
        /// </summary>
        /// <param name="ticks">A value retrieved from Stopwatch.GetTimeStamp() that is not now.</param>
        /// <returns>TimeSpan of the range between the timestamp given and now.</returns>
        public static TimeSpan TicksTillNow_TimeSpan(this long ticks)
        {
            return TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - ticks) / (double)Stopwatch.Frequency);
        }

        /// <summary>
        /// Return a timespan range between to tick values obtained from Stopwatch.GetTimeStamp().
        /// </summary>
        /// <param name="startTicks">First point in time.</param>
        /// <param name="endTicks">Last point in time.</param>
        /// <returns>TimeSpan of the range between the two points given.</returns>
        public static TimeSpan TimeElapsed(this long endTicks, long startTicks)
        {
            return TimeSpan.FromSeconds((endTicks - startTicks) / (double)Stopwatch.Frequency);
        }
    }
}