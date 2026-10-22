using Newtonsoft.Json;

namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        private string _discordWebhookUrl = "";
        /// <summary>
        /// Discord channel webhook. Marked JsonIgnore so it is never written into shared or exported config data.
        /// </summary>
        [JsonIgnore]
        public string DiscordWebhookUrl { get => _discordWebhookUrl; set => SetValue(ref _discordWebhookUrl, value ?? ""); }

        private string _discordBotName = "PB Enforcement Core";
        public string DiscordBotName { get => _discordBotName; set => SetValue(ref _discordBotName, value ?? ""); }

        private string _discordServerName = "";
        /// <summary>
        /// Shown on top of every Discord message, so servers that share one channel can be told apart.
        /// Empty = the dedicated server's own name. Never shared, because it belongs to this server.
        /// </summary>
        public string DiscordServerName { get => _discordServerName; set => SetValue(ref _discordServerName, value ?? ""); }

        private int _discordMinOffences = 1;
        public int DiscordMinOffences { get => _discordMinOffences; set => SetValue(ref _discordMinOffences, value); }

        // ---- automatic alerts
        private bool _discordAutoReportEnabled;
        /// <summary>Post an alert on its own when a player meets the offence criteria below.</summary>
        public bool DiscordAutoReportEnabled { get => _discordAutoReportEnabled; set => SetValue(ref _discordAutoReportEnabled, value); }

        private int _discordAutoMinOffences = 3;
        /// <summary>Offences a player needs inside the time window to trigger an alert.</summary>
        public int DiscordAutoMinOffences { get => _discordAutoMinOffences; set => SetValue(ref _discordAutoMinOffences, value); }

        private int _discordAutoWindowMinutes = 10;
        public int DiscordAutoWindowMinutes { get => _discordAutoWindowMinutes; set => SetValue(ref _discordAutoWindowMinutes, value); }

        private int _discordAutoCooldownMinutes = 30;
        /// <summary>The same player is not reported again until this many minutes have passed.</summary>
        public int DiscordAutoCooldownMinutes { get => _discordAutoCooldownMinutes; set => SetValue(ref _discordAutoCooldownMinutes, value); }

        private bool _discordAutoReportOnPunish = true;
        /// <summary>A punished block (including instant kills) is always reported, whatever the offence count.</summary>
        public bool DiscordAutoReportOnPunish { get => _discordAutoReportOnPunish; set => SetValue(ref _discordAutoReportOnPunish, value); }
    }
}
