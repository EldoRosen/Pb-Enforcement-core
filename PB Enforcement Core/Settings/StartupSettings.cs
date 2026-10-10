namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        private bool _firstRunCompleted;
        /// <summary>
        /// False until this version of the plugin has started once. That first start switches the plugin off, so everything can be
        /// reviewed before any script is limited. Configs written by older versions do not have this value, so they get the same
        /// first start once.
        /// </summary>
        public bool FirstRunCompleted { get => _firstRunCompleted; set => SetValue(ref _firstRunCompleted, value); }
    }
}
