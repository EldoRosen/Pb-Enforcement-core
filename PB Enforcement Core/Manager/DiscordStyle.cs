using System.Linq;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// The look of every Discord message. Discord does not let a webhook choose a font, so the style comes from what it
    /// does allow: capital-letter headings, fixed-width blocks with aligned columns, and the plugin's colours.
    /// </summary>
    internal static class DiscordStyle
    {
        public const int Indigo = 0x4338CA; // the accent colour of the plugin window
        public const int Red = 0xDC2626;
        public const int Green = 0x2ECC71;
        public const int Orange = 0xF97316;

        private const int LabelWidth = 13;
        private const int LineWidth = 56;

        public static string Title(string section) => "PB ENFORCEMENT CORE // " + section.ToUpperInvariant();

        /// <summary>One aligned line: LABEL padded to a fixed width, then the value.</summary>
        public static string Row(string label, string value) => (label.ToUpperInvariant() + " ").PadRight(LabelWidth) + value;

        /// <summary>A line that continues the previous row (same column as the values).</summary>
        public static string Continued(string value) => new string(' ', LabelWidth) + value;

        /// <summary>A bullet line, for blocks and similar lists.</summary>
        public static string Bullet(string value) => "> " + value;

        /// <summary>Fixed-width block: Discord draws it in a monospace font, so columns line up.</summary>
        public static string Block(params string[] lines)
        {
            // Backticks would end the block early, and a line break inside a value would break the layout.
            string text = string.Join("\n", lines.Select(line => Fit(Clean(line))));
            return "```\n" + text + "\n```";
        }

        public static string Clean(string? text) =>
            (text ?? "").Replace("`", "'").Replace("\r", " ").Replace("\n", " ").Trim();

        private static string Fit(string line) =>
            line.Length <= LineWidth ? line : line.Substring(0, LineWidth - 1) + "…";

        public static string FieldName(string playerName, bool privileged) =>
            "▸ " + DiscordManager.Escape(playerName) + (privileged ? "  [PRIVILEGED]" : "");
    }
}
