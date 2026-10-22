namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        public const string DefaultSharedSettingsDatabasePath = "C:/PBLimiter/settings.db";

        private bool _sharedSettingsEnabled;
        /// <summary>Share the plugin settings with other servers through a local SQLite database.</summary>
        public bool SharedSettingsEnabled { get => _sharedSettingsEnabled; set => SetValue(ref _sharedSettingsEnabled, value); }

        private bool _sharedSettingsAutoPush = true;
        /// <summary>While sharing is on, send every settings change made here to the other servers straight away.</summary>
        public bool SharedSettingsAutoPush { get => _sharedSettingsAutoPush; set => SetValue(ref _sharedSettingsAutoPush, value); }

        private bool _sharedSettingsAutoPull = true;
        /// <summary>While sharing is on, apply changes made on the other servers straight away (checked every few seconds).</summary>
        public bool SharedSettingsAutoPull { get => _sharedSettingsAutoPull; set => SetValue(ref _sharedSettingsAutoPull, value); }

        private string _sharedSettingsDatabasePath = DefaultSharedSettingsDatabasePath;
        /// <summary>Stored exactly as typed. An empty path falls back to the default when the database is used.</summary>
        public string SharedSettingsDatabasePath { get => _sharedSettingsDatabasePath; set => SetValue(ref _sharedSettingsDatabasePath, value ?? ""); }
    }
}
