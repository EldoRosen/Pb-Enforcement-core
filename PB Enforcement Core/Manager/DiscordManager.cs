using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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

        // Discord allows 6000 characters in total across an embed.
        internal const int EmbedCharBudget = 5400;
        internal const int MaxFields = 25;
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
            JObject embed = new()
            {
                ["title"] = DiscordStyle.Title("Webhook test"),
                ["description"] = DiscordStyle.Block(
                    DiscordStyle.Row("Status", "CONNECTED"),
                    DiscordStyle.Row("Sender", sender),
                    DiscordStyle.Row("Reports", "ON REQUEST"),
                    DiscordStyle.Row("Auto alerts", Config.DiscordAutoReportEnabled ? "ON" : "OFF")),
                ["color"] = DiscordStyle.Indigo,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["footer"] = new JObject { ["text"] = FooterText("Test message") }
            };
            return await PostAsync(webhook, WrapPayload(embed)).ConfigureAwait(false);
        }

        internal static string FooterText(string tail) =>
            $"PB ENFORCEMENT CORE V{Advanced_PB_Limiter.Instance?.VersionText} // {tail.ToUpperInvariant()}";

        private static JObject BuildOffencePayload(List<PlayerReport> offenders, int trackedPlayers, int minOffences)
        {
            int totalOffences = offenders.Sum(p => p.TotalOffences);

            string description = offenders.Count == 0
                ? DiscordStyle.Block(
                    DiscordStyle.Row("Status", "ALL CLEAR"),
                    DiscordStyle.Row("Server", "THIS SERVER"),
                    DiscordStyle.Row("Tracked", $"{trackedPlayers} PLAYER(S)"))
                : DiscordStyle.Block(
                    DiscordStyle.Row("Server", "THIS SERVER"),
                    DiscordStyle.Row("Tracked", $"{trackedPlayers} PLAYER(S)"),
                    DiscordStyle.Row("Offenders", $"{offenders.Count}  (MIN {minOffences} OFFENCE{(minOffences == 1 ? "" : "S")})"),
                    DiscordStyle.Row("Offences", totalOffences.ToString()));

            string title = DiscordStyle.Title("Offence report");
            int used = title.Length + description.Length + 100; // 100 = footer allowance
            JArray fields = new();
            int shown = 0;

            foreach (PlayerReport player in offenders)
            {
                if (shown >= MaxFields) break;

                List<string> lines = new();
                if (player.SteamId != 0) lines.Add(DiscordStyle.Row("Steam", player.SteamId.ToString()));
                lines.Add(DiscordStyle.Row("Offences", player.TotalOffences.ToString()));
                lines.Add(DiscordStyle.Row("Blocks", $"{player.GetPBReports.Count}   RECOMPILES {player.TotalRecompiles}"));
                lines.Add(DiscordStyle.Row("Avg run", $"{player.CombinedRunTimeMSAvg:0.000} MS"));

                foreach (PBReport pb in player.GetPBReports.Where(b => b.Offences > 0).OrderByDescending(b => b.Offences).Take(3))
                    lines.Add(DiscordStyle.Bullet($"{pb.GridName} / {pb.BlockName}  x{pb.Offences}"));

                string name = Truncate(DiscordStyle.FieldName(player.PlayerName, player.IsPrivileged), 256);
                string body = DiscordStyle.Block(lines.ToArray());
                if (used + name.Length + body.Length > EmbedCharBudget) break;

                used += name.Length + body.Length;
                fields.Add(new JObject { ["name"] = name, ["value"] = body, ["inline"] = false });
                shown++;
            }

            string tail = offenders.Count > shown ? $"+{offenders.Count - shown} more player(s) not shown" : "Generated on request";

            JObject embed = new()
            {
                ["title"] = title,
                ["description"] = description,
                ["color"] = offenders.Count == 0 ? DiscordStyle.Green : DiscordStyle.Red,
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["footer"] = new JObject { ["text"] = FooterText(tail) }
            };
            if (fields.Count > 0) embed["fields"] = fields;

            return WrapPayload(embed);
        }

        internal static JObject WrapPayload(JObject embed)
        {
            string name = string.IsNullOrWhiteSpace(Config.DiscordBotName) ? DefaultBotName : Truncate(Config.DiscordBotName.Trim(), 80);
            return new JObject
            {
                ["username"] = name,
                // Player and grid names are user controlled: never let them ping anyone.
                ["allowed_mentions"] = new JObject { ["parse"] = new JArray() },
                ["embeds"] = new JArray { embed }
            };
        }

        internal static async Task<(bool Success, string Message)> PostAsync(Uri webhook, JObject payload)
        {
            string json = payload.ToString(Formatting.None);

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

                    // Never log the URL itself: it is a secret.
                    Log.Warn($"Discord webhook request failed with HTTP {status}.");
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
                if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>') sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        internal static string Truncate(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, Math.Max(0, max - 1)) + "…";
        }
    }
}
