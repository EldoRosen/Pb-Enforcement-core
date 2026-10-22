using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Advanced_PB_Limiter.Utils.Reports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Sends offence reports to a Discord channel webhook. Only ever runs on request.
    /// </summary>
    public static class DiscordManager
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        private const string DefaultBotName = "PB Enforcement Core";
        private static readonly string[] AllowedHosts = { "discord.com", "discordapp.com", "canary.discord.com", "ptb.discord.com" };

        /// <summary>
        /// Only real Discord webhook links are accepted, so the config cannot be used to make the server call arbitrary hosts.
        /// </summary>
        public static bool IsValidWebhookUrl(string? url, out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out Uri? parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (Array.IndexOf(AllowedHosts, parsed.Host.ToLowerInvariant()) < 0) return false;
            if (!parsed.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal)) return false;
            uri = parsed;
            return true;
        }

        private const string NoWebhookMessage = "No valid Discord webhook is set. Add one in the Discord tab (it looks like https://discord.com/api/webhooks/...).";

        public static async Task<(bool Success, string Message)> SendOffenceReportAsync()
        {
            if (!IsValidWebhookUrl(Config.DiscordWebhookUrl, out Uri? webhook) || webhook is null)
                return (false, NoWebhookMessage);

            List<PlayerReport>? snapshot;
            try
            {
                snapshot = await ReportManager.GetOffenceSnapshot().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not collect the offence report data.");
                return (false, "Could not collect the report data. See the log for details.");
            }

            if (snapshot is null)
                return (false, "The report generator is busy. Try again in a minute.");

            int minOffences = Math.Max(1, Config.DiscordMinOffences);
            List<PlayerReport> offenders = snapshot
                .Where(p => p.TotalOffences >= minOffences)
                .OrderByDescending(p => p.TotalOffences)
                .ToList();

            return await PostAsync(webhook, BuildOffencePayload(offenders, snapshot.Count, minOffences)).ConfigureAwait(false);
        }

        public static async Task<(bool Success, string Message)> SendTestAsync()
        {
            if (!IsValidWebhookUrl(Config.DiscordWebhookUrl, out Uri? webhook) || webhook is null)
                return (false, NoWebhookMessage);

            string sender = string.IsNullOrWhiteSpace(Config.DiscordBotName) ? DefaultBotName : Config.DiscordBotName.Trim();
            DiscordCard card = new(DiscordStyle.Green);
            card.Text(DiscordStyle.Header("✅", "Webhook connected") + "\nReports and alerts from this server will appear in this channel.");
            card.Divider(large: true);
            card.Text(DiscordStyle.Inline(
                DiscordStyle.Stat("Sender", Clean(sender)),
                DiscordStyle.Stat("Full reports", "on request"),
                DiscordStyle.Stat("Auto alerts", Config.DiscordAutoReportEnabled ? "on" : "off"),
                DiscordStyle.Stat("Shared settings", Config.SharedSettingsEnabled ? "live" : "off")));
            return await PostAsync(webhook, WrapPayload(card, DiscordStyle.Footer("Test message"))).ConfigureAwait(false);
        }

        private static JObject BuildOffencePayload(List<PlayerReport> offenders, int trackedPlayers, int minOffences)
        {
            int totalOffences = offenders.Sum(p => p.TotalOffences);
            bool clear = offenders.Count == 0;

            DiscordCard card = new(clear ? DiscordStyle.Green : DiscordStyle.Red);
            card.Text(DiscordStyle.Header(clear ? "✅" : "📋", "Offence report", "requested by staff") + "\n" + (clear
                ? $"All clear: no player has {DiscordStyle.Plural(minOffences, "offence")} or more."
                : $"Players with at least **{DiscordStyle.Plural(minOffences, "offence")}**, worst first."));
            card.Divider(large: true);
            card.Text(DiscordStyle.Inline(
                DiscordStyle.Stat("Tracked players", trackedPlayers.ToString()),
                DiscordStyle.Stat("Offenders", offenders.Count.ToString()),
                DiscordStyle.Stat("Offences", totalOffences.ToString())));

            int shown = 0;
            bool first = true;
            foreach (PlayerReport player in offenders)
            {
                List<string> lines = new()
                {
                    DiscordStyle.PlayerHeading(player.PlayerName, player.IsPrivileged),
                    DiscordStyle.Inline(
                        DiscordStyle.SteamLink(player.SteamId),
                        $"**{DiscordStyle.Plural(player.TotalOffences, "offence")}**",
                        DiscordStyle.Plural(player.GetPBReports.Count, "block"),
                        DiscordStyle.Plural(player.TotalRecompiles, "recompile"),
                        $"avg `{player.CombinedRunTimeMSAvg:0.000} ms`")
                };

                foreach (PBReport pb in player.GetPBReports.Where(b => b.Offences > 0).OrderByDescending(b => b.Offences).Take(3))
                    lines.Add(DiscordStyle.BlockLine(pb.GridName, pb.BlockName, pb.Offences));

                string text = Truncate(string.Join("\n", lines), 900);
                if (first)
                {
                    if (!card.Fits(text, withDivider: true)) break;
                    card.Divider(large: true);
                    card.Text(text);
                    first = false;
                }
                else if (!card.TryAdd(text)) break;

                shown++;
            }

            string? tail = offenders.Count > shown ? $"+{offenders.Count - shown} more not shown" : null;
            return WrapPayload(card, DiscordStyle.Footer(tail));
        }

        private static readonly Regex ReservedNameWords = new("discord|clyde", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The message itself: sender name "Bot · Server" (so the server shows in notifications too), no pings, and the
        /// card as components v2.
        /// </summary>
        internal static JObject WrapPayload(DiscordCard card, string footer)
        {
            string bot = string.IsNullOrWhiteSpace(Config.DiscordBotName) ? DefaultBotName : Config.DiscordBotName.Trim();
            string name = Truncate($"{bot} · {DiscordStyle.ServerName}", 80);
            // Discord refuses a webhook name that contains these words.
            name = ReservedNameWords.Replace(name, m => m.Value.Substring(0, m.Value.Length - 1) + "_");

            return new JObject
            {
                ["username"] = name,
                // Player and grid names are user controlled: never let them ping anyone.
                ["allowed_mentions"] = new JObject { ["parse"] = new JArray() },
                ["flags"] = ComponentsV2Flag,
                ["components"] = card.ToComponents(footer)
            };
        }

        private const int ComponentsV2Flag = 1 << 15;

        /// <summary>A webhook only accepts components when asked with ?with_components=true (any query it already has is kept).</summary>
        private static Uri WithComponents(Uri webhook)
        {
            UriBuilder builder = new(webhook);
            string query = builder.Query.TrimStart('?');
            builder.Query = (query.Length == 0 ? "" : query + "&") + "with_components=true";
            return builder.Uri;
        }

        internal static async Task<(bool Success, string Message)> PostAsync(Uri webhook, JObject payload)
        {
            string json = payload.ToString(Formatting.None);
            webhook = WithComponents(webhook);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using StringContent content = new(json, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await Http.PostAsync(webhook, content).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                        return (true, "Sent to Discord.");

                    int status = (int)response.StatusCode;
                    if (status == 429 && attempt == 0)
                    {
                        double wait = 2;
                        try
                        {
                            JObject body = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                            wait = Math.Min(10, body.Value<double?>("retry_after") ?? 2);
                        }
                        catch { /* keep the default wait */ }

                        await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false);
                        continue;
                    }

                    // Never log the URL itself: it is a secret. Discord's own answer says what was wrong with the message.
                    string answer = "";
                    try { answer = Truncate(await response.Content.ReadAsStringAsync().ConfigureAwait(false), 500); }
                    catch { /* no answer to show */ }
                    Log.Warn($"Discord webhook request failed with HTTP {status}. {answer}");
                    string reason = response.StatusCode switch
                    {
                        HttpStatusCode.NotFound => "the webhook no longer exists (404)",
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"the webhook was refused ({status})",
                        HttpStatusCode.BadRequest => "the message was rejected as invalid (400)",
                        _ => $"HTTP {status}"
                    };
                    return (false, $"Discord rejected the message: {reason}.");
                }
                catch (TaskCanceledException)
                {
                    return (false, "Discord did not answer in time. Try again in a moment.");
                }
                catch (HttpRequestException e)
                {
                    Log.Warn($"Could not reach Discord: {e.Message}");
                    return (false, "Could not reach Discord. Check the server's internet access.");
                }
                catch (Exception e)
                {
                    Log.Warn(e, "Unexpected error while sending to Discord.");
                    return (false, "Unexpected error while sending to Discord. See the log for details.");
                }
            }

            return (false, "Discord is rate limiting this webhook. Try again shortly.");
        }

        internal static string Escape(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "-";
            StringBuilder sb = new(text!.Length + 8);
            foreach (char c in text)
            {
                if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '[' or ']' or '#' or '<' or '@') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Text for inside a `code` span: backticks and line breaks removed.</summary>
        internal static string Clean(string? text) => DiscordStyle.Clean(text);

        internal static string Truncate(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, Math.Max(0, max - 1)) + "…";
        }
    }
}
