namespace Advanced_PB_Limiter.Utils
{
    /// <summary>
    /// One blocked script. A script is blocked when its fingerprint matches Hash, or when it contains Signature.
    /// </summary>
    public class BlacklistedScript
    {
        /// <summary>Label shown in logs, lists and messages, e.g. "Auto Miner v3".</summary>
        public string Name { get; set; } = "";

        /// <summary>SHA-256 fingerprint of the whole script (at least the first 12 characters). Exact match for one version of a script.</summary>
        public string Hash { get; set; } = "";

        /// <summary>A unique piece of text from the script (at least 8 characters). Still matches after the author updates the script.</summary>
        public string Signature { get; set; } = "";

        public string Note { get; set; } = "";

        public BlacklistedScript() { }
    }
}
