using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Advanced_PB_Limiter.Settings;

namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// Builds the chat messages players receive from the texts set in Main Settings > Messages.
    /// Placeholders such as {grid} are replaced (any capitalisation); anything else in the text is left exactly as typed.
    /// </summary>
    public static class ChatMessages
    {
        private const int MaxLength = 900;
        private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        public static string OrDefault(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text!;

        public static string Render(string template, IDictionary<string, string> values)
        {
            string text = Placeholder.Replace(template ?? "", m =>
                values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out string? value) ? value : m.Value);

            return text.Length <= MaxLength ? text : text.Substring(0, MaxLength - 1) + "…";
        }

        /// <summary>Example values, used for the preview in the settings screen.</summary>
        public static Dictionary<string, string> SampleValues() => new()
        {
            ["player"] = "Alex",
            ["grid"] = "Mining Rig",
            ["block"] = "Miner PB",
            ["offence"] = "2",
            ["allowed"] = "3",
            ["limittype"] = "average run-time",
            ["limit"] = "0.4000",
            ["usage"] = "0.9100",
            ["reason"] = "excessive run time",
            ["action"] = "turned off",
            ["details"] = "Your Limit: [0.8000ms] Your Usage: [1.2000ms]"
        };

        private static string Ms(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

        private static Dictionary<string, string> BlockValues(TrackedPlayer player, TrackedPBBlock block) => new()
        {
            ["player"] = player.PlayerName ?? "Unknown",
            ["grid"] = block.GridName,
            ["block"] = block.ProgrammableBlock?.CustomName.ToString() ?? "Programmable Block"
        };

        public static string OffenceWarning(TrackedPlayer player, TrackedPBBlock block, int offence, int allowed, string limitType, double limitMs, double usageMs)
        {
            Dictionary<string, string> values = BlockValues(player, block);
            values["offence"] = offence.ToString(CultureInfo.InvariantCulture);
            values["allowed"] = allowed.ToString(CultureInfo.InvariantCulture);
            values["limittype"] = limitType;
            values["limit"] = Ms(limitMs);
            values["usage"] = Ms(usageMs);
            return Render(OrDefault(Config.OffenceWarningMessage, Advanced_PB_LimiterConfig.DefaultOffenceWarningMessage), values);
        }

        public static string Punishment(TrackedPlayer player, TrackedPBBlock block, string reason, string action, string? details)
        {
            Dictionary<string, string> values = BlockValues(player, block);
            values["reason"] = reason;
            values["action"] = action;
            values["details"] = details ?? "";
            return Render(OrDefault(Config.PunishmentMessage, Advanced_PB_LimiterConfig.DefaultPunishmentMessage), values);
        }

        public static string Throttle(TrackedPlayer player, TrackedPBBlock block)
        {
            return Render(OrDefault(Config.ThrottleMessage, Advanced_PB_LimiterConfig.DefaultThrottleMessage), BlockValues(player, block));
        }
    }
}
