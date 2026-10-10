namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        private int _enforceSustainedSeconds = 10;
        /// <summary>The server must stay below the sim speed threshold this long before limits are enforced (ignores one-off hiccups such as saves).</summary>
        public int EnforceSustainedSeconds { get => _enforceSustainedSeconds; set => SetValue(ref _enforceSustainedSeconds, value); }

        private double _minLoadMSPerSecondToPunish = 5.0;
        /// <summary>Run-time offences are ignored for blocks that use less server time per second than this. 0 turns the filter off.</summary>
        public double MinLoadMSPerSecondToPunish { get => _minLoadMSPerSecondToPunish; set => SetValue(ref _minLoadMSPerSecondToPunish, value); }

        private bool _throttleBeforePunish;
        /// <summary>Slow a block that just received an offence (its periodic runs only) before anything harsher happens.</summary>
        public bool ThrottleBeforePunish { get => _throttleBeforePunish; set => SetValue(ref _throttleBeforePunish, value); }

        private double _throttleMaxRunsPerSecond = 2;
        public double ThrottleMaxRunsPerSecond { get => _throttleMaxRunsPerSecond; set => SetValue(ref _throttleMaxRunsPerSecond, value); }

        private int _throttleDurationSeconds = 60;
        /// <summary>The throttle lasts this long after the block's most recent offence.</summary>
        public int ThrottleDurationSeconds { get => _throttleDurationSeconds; set => SetValue(ref _throttleDurationSeconds, value); }
    }
}
