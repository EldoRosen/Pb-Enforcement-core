using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Remembers every time the plugin switched off, damaged or blocked a programmable block, and why. Each entry is also
    /// written to the log as a warning, so "why is my block off?" always has an answer.
    /// </summary>
    public static class BlockActionLog
    {
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Block Actions");
        private const int MaxEntries = 300;

        private sealed class Entry
        {
            public DateTime Time;
            public long BlockId;
            public string Grid = "";
            public string Block = "";
            public string Owner = "";
            public string Action = "";
            public string Reason = "";
        }

        private static readonly object Gate = new();
        private static readonly List<Entry> Entries = new();
        private static readonly Dictionary<long, MyProgrammableBlock> Restorable = new();

        /// <summary>
        /// Notes that the plugin did something to a block. Set restorable when switching the block back on is safe
        /// (it was only switched off, not damaged, blacklisted or unowned).
        /// </summary>
        public static void Record(MyProgrammableBlock? pb, string action, string reason, bool restorable = false)
        {
            if (pb is null) return;

            try
            {
                string grid = pb.CubeGrid?.DisplayName ?? "unknown grid";
                string block = pb.CustomName?.ToString() ?? "Programmable Block";
                string owner = MySession.Static?.Players?.TryGetIdentity(pb.OwnerId)?.DisplayName ?? (pb.OwnerId == 0 ? "nobody" : pb.OwnerId.ToString());
                reason = (reason ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                DateTime now = DateTime.Now;

                lock (Gate)
                {
                    // Remembered even when the entry below is a repeat, or RestoreBlocks would miss a block punished again within a minute.
                    if (restorable) Restorable[pb.EntityId] = pb;

                    // The same thing happening to the same block again within a minute is one event.
                    for (int i = Entries.Count - 1; i >= 0 && i >= Entries.Count - 25; i--)
                    {
                        Entry previous = Entries[i];
                        if (previous.BlockId == pb.EntityId && previous.Action == action && (now - previous.Time).TotalSeconds < 60) return;
                    }

                    Entries.Add(new Entry { Time = now, BlockId = pb.EntityId, Grid = grid, Block = block, Owner = owner, Action = action, Reason = reason });
                    if (Entries.Count > MaxEntries) Entries.RemoveAt(0);
                }

                Log.Warn($"[{grid} >> {block}] (id {pb.EntityId}, owner {owner}): {action}. {reason}");
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not record a block action.");
            }
        }

        /// <summary>The latest entries, newest first, as chat text.</summary>
        public static string Describe(int count)
        {
            count = Math.Max(1, Math.Min(count, 25));
            List<Entry> latest;
            lock (Gate) latest = Entries.AsEnumerable().Reverse().Take(count).ToList();

            if (latest.Count == 0) return "The plugin has not switched off, damaged or blocked any programmable block since the server started.";

            StringBuilder sb = new();
            sb.AppendLine($"Latest {latest.Count} actions on programmable blocks (newest first):");
            foreach (Entry e in latest)
                sb.AppendLine($"{e.Time:HH:mm:ss} | {e.Owner} | {e.Grid} >> {e.Block} (id {e.BlockId}) | {e.Action}: {e.Reason}");
            return sb.ToString();
        }

        /// <summary>
        /// Switches back on the blocks the plugin only turned off. Runs on the game thread and reports the result through done(restored, skipped).
        /// </summary>
        public static void RestoreAll(Action<int, int> done)
        {
            List<MyProgrammableBlock> blocks;
            lock (Gate)
            {
                blocks = Restorable.Values.ToList();
                Restorable.Clear();
            }

            MySandboxGame.Static.Invoke(() =>
            {
                int restored = 0, skipped = 0;
                foreach (MyProgrammableBlock pb in blocks)
                {
                    try
                    {
                        if (pb.Closed || pb.Enabled) { skipped++; continue; }

                        // Start from a clean slate, or the same offences would switch it straight off again.
                        TrackingManager.GetTrackedPlayerDataById(pb.OwnerId)?.ResetPBBlockData(pb);
                        pb.Enabled = true;
                        restored++;
                    }
                    catch (Exception e)
                    {
                        skipped++;
                        Log.Warn(e, "Could not switch a block back on.");
                    }
                }

                done(restored, skipped);
            }, "PB Enforcement Core");
        }

        public static void Reset()
        {
            lock (Gate)
            {
                Entries.Clear();
                Restorable.Clear();
            }
        }
    }
}
