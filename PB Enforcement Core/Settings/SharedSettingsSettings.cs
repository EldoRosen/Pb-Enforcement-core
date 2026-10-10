namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        public const string DefaultSharedSettingsDatabasePath = "C:/PBLimiter/settings.db";

        private bool _sharedSettingsEnabled;
        /// <summary>Share the plugin settings with other servers through a local SQLite database.</summary>
        public bool SharedSettingsEnabled { get => _sharedSettingsEnabled; set => SetValue(ref _sharedSettingsEnabled, value); }

        private bool _sharedSettingsAutoPush = true;
        /// <summary>While sharing is on, write every settings change to the database as well.</summary>
        public bool SharedSettingsAutoPush { get => _sharedSettingsAutoPush; set => SetValue(ref _sharedSettingsAutoPush, value); }

        private string _sharedSettingsDatabasePath = DefaultSharedSettingsDatabasePath;
        /// <summary>Stored exactly as typed. An empty path falls back to the default when the database is used.</summary>
        public string SharedSettingsDatabasePath { get => _sharedSettingsDatabasePath; set => SetValue(ref _sharedSettingsDatabasePath, value ?? ""); }
    }
}
