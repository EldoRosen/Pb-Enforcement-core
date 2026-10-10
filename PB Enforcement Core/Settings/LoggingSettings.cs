namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        private bool _pluginLogToFile = true;
        /// <summary>Write every plugin message to its own daily file in the Logs folder.</summary>
        public bool PluginLogToFile { get => _pluginLogToFile; set => SetValue(ref _pluginLogToFile, value); }

        private bool _pluginLogHideInfoInMainLog = true;
        /// <summary>Keep routine (info and lower) plugin messages out of the main Torch log and console. Warnings and errors still appear in both.</summary>
        public bool PluginLogHideInfoInMainLog { get => _pluginLogHideInfoInMainLog; set => SetValue(ref _pluginLogHideInfoInMainLog, value); }

        private int _pluginLogKeepDays = 30;
        /// <summary>Daily plugin log files older than this are deleted.</summary>
        public int PluginLogKeepDays { get => _pluginLogKeepDays; set => SetValue(ref _pluginLogKeepDays, value); }
    }
}
