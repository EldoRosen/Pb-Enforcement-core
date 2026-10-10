using System.Collections.Generic;
using Advanced_PB_Limiter.Utils;

namespace Advanced_PB_Limiter.Settings
{
    public partial class Advanced_PB_LimiterConfig
    {
        public const string DefaultScriptBlacklistMessage =
            "The script on programmable block [{block}] (grid [{grid}]) has been blocked because it is known to affect server performance. " +
            "The block has been switched off. Please remove this script, or contact a server administrator for more information.";

        private bool _scriptBlacklistEnabled = true;
        public bool ScriptBlacklistEnabled { get => _scriptBlacklistEnabled; set => SetValue(ref _scriptBlacklistEnabled, value); }

        private string _scriptBlacklistMessage = DefaultScriptBlacklistMessage;
        /// <summary>Sent to the block owner. {block}, {grid} and {script} are replaced.</summary>
        public string ScriptBlacklistMessage { get => _scriptBlacklistMessage; set => SetValue(ref _scriptBlacklistMessage, value ?? ""); }

        private List<BlacklistedScript> _scriptBlacklist = new();
        /// <summary>Treated as read-only: to change it, build a new list and assign it (see ScriptBlacklistManager).</summary>
        public List<BlacklistedScript> ScriptBlacklist { get => _scriptBlacklist; set => SetValue(ref _scriptBlacklist, value ?? new List<BlacklistedScript>()); }
    }
}
