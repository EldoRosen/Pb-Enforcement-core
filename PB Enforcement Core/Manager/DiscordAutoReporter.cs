using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Newtonsoft.Json.Linq;
using NLog;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Posts offence alerts to the Discord webhook without being asked, once a player meets the configured criteria
    /// (N offences inside a rolling window). Alerts are batched and sent every few seconds, and each player is only
    /// reported once per cooldown, so the channel cannot be flooded.
    /// </summary>
    public static class DiscordAutoReporter
    {
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Discord Alerts");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        private const int FlushEverySeconds = 20;
        private const int MaxBlocksPerPlayer = 3;

        private sealed class OffenceEvent
        {
            public long Tick;
            public long BlockId;
            public string Grid = "";
            public string Block = "";
        }

        private sealed class PlayerTally
        {
            public readonly object Lock = new();
            public readonly List<OffenceEvent> Events = new();
            public long LastAlertTick; // 0 = never alerted
        }

        private sealed class BlockInfo
        {
            public string Grid = "";
            public string Block = "";
            public int Offences;
        }

        private sealed class Alert
        {
            public ulong SteamId;
            public string PlayerName = "Unknown";
            public int OffenceCount;
            public int WindowMinutes;
            public readonly Dictionary<long, BlockInfo> Blocks = new();
            public readonly List<string> Punishments = new();
            public bool Closed; // already taken for sending: later changes need a fresh alert
        }

        private static readonly ConcurrentDictionary<ulong, PlayerTally> Tallies = new();
        private static readonly ConcurrentDictionary<ulong, Alert> Pending = new();
        private static readonly ConcurrentDictionary<long, long> LastPunishAlertByBlock = new();
        private static Timer? _flushTimer;
        private static int _flushing;
        private static bool _warnedNoWebhook;

        // ---------------------------------------------------------------- lifecycle

        public static void Start()
        {
            _flushTimer?.Dispose();
            _flushTimer = new Timer(_ => { _ = FlushAsync(); }, null, TimeSpan.FromSeconds(FlushEverySeconds), TimeSpan.FromSeconds(FlushEverySeconds));
        }

        public static void Stop()
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            Tallies.Clear();
            Pending.Clear();
            LastPunishAlertByBlock.Clear();
        }

        // ---------------------------------------------------------------- events from the limiter (must never throw)

        /// <summary>A programmable block just received an offence.</summary>
        public static void RecordOffence(TrackedPlayer player, TrackedPBBlock block)
        {
            try
            {
                if (!Config.DiscordAutoReportEnabled) return;

                int window = Math.Max(1, Config.DiscordAutoWindowMinutes);
                int minOffences = Math.Max(1, Config.DiscordAutoMinOffences);
                long now = Stopwatch.GetTimestamp();
                long windowTicks = (long)(window * 60.0 * Stopwatch.Frequency);

                PlayerTally tally = Tallies.GetOrAdd(player.SteamId, _ => new PlayerTally());
                lock (tally.Lock)
                {
                    tally.Events.Add(new OffenceEvent
                    {
                        Tick = now,
                        BlockId = block.ProgrammableBlock?.EntityId ?? 0,
                        Grid = block.GridName,
                        Block = block.ProgrammableBlock?.CustomName.ToString() ?? "Programmable Block"
                    });
                    tally.Events.RemoveAll(e => now - e.Tick > windowTicks);

                    if (tally.Events.Count < minOffences) return;
                    if (!CooldownPassed(tally, now)) return;
                    tally.LastAlertTick = now;

                    string playerName = player.PlayerName ?? "Unknown";
                    int offenceCount = tally.Events.Count;
                    List<IGrouping<long, OffenceEvent>> groups = tally.Events.GroupBy(e => e.BlockId).ToList();
                    UpdateAlert(player.SteamId, alert =>
                    {
                        alert.PlayerName = playerName;
                        alert.WindowMinutes = window;
                        alert.OffenceCount = offenceCount;
                        foreach (IGrouping<long, OffenceEvent> group in groups)
                        {
                            OffenceEvent last = group.Last();
                            alert.Blocks[group.Key] = new BlockInfo { Grid = last.Grid, Block = last.Block, Offences = group.Count() };
                        }
                    });
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not record an offence for Discord alerts.");
            }
        }

        /// <summary>A programmable block was punished (this also covers instant kills).</summary>
        public static void RecordPunishment(TrackedPlayer player, TrackedPBBlock block, string reason)
        {
            try
            {
                if (!Config.DiscordAutoReportEnabled || !Config.DiscordAutoReportOnPunish) return;

                long now = Stopwatch.GetTimestamp();
                long blockId = block.ProgrammableBlock?.EntityId ?? 0;
                long cooldownTicks = CooldownTicks();

                // Each block is announced once per cooldown, so a block that keeps being re-enabled cannot spam the channel.
                if (LastPunishAlertByBlock.TryGetValue(blockId, out long lastForBlock) && now - lastForBlock < cooldownTicks) return;
                LastPunishAlertByBlock[blockId] = now;

                string blockName = block.ProgrammableBlock?.CustomName.ToString() ?? "Programmable Block";
                string playerName = player.PlayerName ?? "Unknown";
                string gridName = block.GridName;
                string punishment = $"{gridName} / {blockName}: {SplitWords(reason)}";
                UpdateAlert(player.SteamId, alert =>
                {
                    alert.PlayerName = playerName;
                    if (alert.WindowMinutes == 0) alert.WindowMinutes = Math.Max(1, Config.DiscordAutoWindowMinutes);
                    alert.Punishments.Add(punishment);
                    if (!alert.Blocks.ContainsKey(blockId))
                        alert.Blocks[blockId] = new BlockInfo { Grid = gridName, Block = blockName, Offences = 0 };
                });
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not record a punishment for Discord alerts.");
            }
        }

        /// <summary>
        /// Changes the player's pending alert. If the sender has just taken that alert, a fresh one is started instead of
        /// updating one that will never be sent.
        /// </summary>
        private static void UpdateAlert(ulong steamId, Action<Alert> change)
        {
            while (true)
            {
                Alert alert = Pending.GetOrAdd(steamId, id => new Alert { SteamId = id });
                lock (alert)
                {
                    if (!alert.Closed)
                    {
                        change(alert);
                        return;
                    }
                }

                ((ICollection<KeyValuePair<ulong, Alert>>)Pending).Remove(new KeyValuePair<ulong, Alert>(steamId, alert));
            }
        }

        private static long CooldownTicks() => (long)(Math.Max(1, Config.DiscordAutoCooldownMinutes) * 60.0 * Stopwatch.Frequency);

        private static bool CooldownPassed(PlayerTally tally, long now) => tally.LastAlertTick == 0 || now - tally.LastAlertTick >= CooldownTicks();

        private static string SplitWords(string text) => Regex.Replace(text ?? "", "(?<=[a-z])(?=[A-Z])", " ");

        // ---------------------------------------------------------------- sending

        private static async Task FlushAsync()
        {
            if (Interlocked.Exchange(ref _flushing, 1) == 1) return;

            try
            {
                if (Pending.IsEmpty) return;

                // Close each alert first (so nothing can be added to it any more), then take it out of the queue.
                List<Alert> alerts = new();
                foreach (KeyValuePair<ulong, Alert> entry in Pending.ToArray())
                {
                    lock (entry.Value) entry.Value.Closed = true;
                    ((ICollection<KeyValuePair<ulong, Alert>>)Pending).Remove(entry);
                    alerts.Add(entry.Value);
                }
                if (alerts.Count == 0 || !Config.DiscordAutoReportEnabled) return;

                if (!DiscordManager.IsValidWebhookUrl(Config.DiscordWebhookUrl, out Uri? webhook) || webhook is null)
                {
                    if (!_warnedNoWebhook)
                    {
                        _warnedNoWebhook = true;
                        Log.Warn("Automatic Discord alerts are on, but no valid webhook is set. Add one in the Discord tab.");
                    }
                    return;
                }
                _warnedNoWebhook = false;

                // Worst offenders first; extra players go into a second message instead of being dropped.
                List<Alert> ordered = alerts.OrderByDescending(a => { lock (a) return a.OffenceCount + a.Punishments.Count * 100; }).ToList();
                while (ordered.Count > 0)
                {
                    JObject payload = BuildPayload(ordered, out int used);
                    ordered.RemoveRange(0, used);

                    (bool success, string message) = await DiscordManager.PostAsync(webhook, payload).ConfigureAwait(false);
                    if (!success)
                    {
                        Log.Warn($"Automatic Discord alert was not delivered: {message}");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "Unexpected error while sending automatic Discord alerts.");
            }
            finally
            {
                Interlocked.Exchange(ref _flushing, 0);
            }
        }

        private static JObject BuildPayload(List<Alert> alerts, out int playersUsed)
        {
            DiscordCard card = new(DiscordStyle.Orange);
            card.Text(DiscordStyle.Header("🚨", "Offence alert", "automatic") + "\n" +
                      $"{DiscordStyle.Plural(alerts.Count, "player")} crossed the alert limit.");
            playersUsed = 0;

            foreach (Alert alert in alerts)
            {
                List<string> lines = new();
                lock (alert)
                {
                    lines.Add(DiscordStyle.PlayerHeading(alert.PlayerName, false));
                    lines.Add(DiscordStyle.Inline(
                        DiscordStyle.SteamLink(alert.SteamId),
                        alert.OffenceCount > 0 ? $"**{DiscordStyle.Plural(alert.OffenceCount, "offence")}** in the last {alert.WindowMinutes} min" : ""));

                    foreach (string punished in alert.Punishments.Take(3))
                        lines.Add("> ⛔ " + DiscordManager.Escape(DiscordStyle.Clean(punished)));

                    foreach (KeyValuePair<long, BlockInfo> block in alert.Blocks.OrderByDescending(b => b.Value.Offences).Take(MaxBlocksPerPlayer))
                        lines.Add(DiscordStyle.BlockLine(block.Value.Grid, block.Value.Block, block.Value.Offences, block.Key));
                }
                lines.RemoveAll(string.IsNullOrEmpty);

                string text = DiscordManager.Truncate(string.Join("\n", lines), 900);
                if (!card.TryAdd(text))
                {
                    if (playersUsed > 0) break;
                    card.Divider(large: true).Text(text); // always send at least one player, even if cut short
                }
                playersUsed++;
            }

            return DiscordManager.WrapPayload(card, DiscordStyle.Footer("Block a script: `!PB BlacklistScript <block id>`"));
        }
    }
}
