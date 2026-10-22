namespace Advanced_PB_Limiter.Settings
{
    /// <summary>Settings added in 3.1.0 by the bug-fix pass (see CHANGES-3.1.0.md).</summary>
    public partial class Advanced_PB_LimiterConfig
    {
        private bool _turnOffOnScriptError;
        /// <summary>
        /// Switch a block off when its script crashes or hits the instruction limit. The game already stops the script itself
        /// (it shows the error in the terminal), so this is off by default.
        /// </summary>
        public bool TurnOffOnScriptError { get => _turnOffOnScriptError; set => SetValue(ref _turnOffOnScriptError, value); }

        private bool _sendGracefulShutdownRequest;
        /// <summary>
        /// Run the script with the argument "GracefulShutDown::&lt;seconds&gt;" before it is punished. Only useful for scripts written to
        /// listen for it; any other script receives it as an ordinary argument, so this is off by default.
        /// </summary>
        public bool SendGracefulShutdownRequest { get => _sendGracefulShutdownRequest; set => SetValue(ref _sendGracefulShutdownRequest, value); }

        private int _instantKillSamples = 3;
        /// <summary>How many runs over the instant-kill limit, inside <see cref="InstantKillWindowSeconds"/>, before a block is punished.</summary>
        public int InstantKillSamples { get => _instantKillSamples; set => SetValue(ref _instantKillSamples, value); }

        private int _instantKillWindowSeconds = 10;
        public int InstantKillWindowSeconds { get => _instantKillWindowSeconds; set => SetValue(ref _instantKillWindowSeconds, value); }
    }
}
