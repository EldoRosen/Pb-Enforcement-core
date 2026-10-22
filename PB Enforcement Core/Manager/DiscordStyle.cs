using System;
using System.Collections.Generic;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Newtonsoft.Json.Linq;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// The look of every Discord message. Messages use Discord's newer layout (components v2): one card with a coloured
    /// edge, a heading with the server name (so several servers can share one channel), dividers between parts, and a
    /// small footer with a time that each reader sees in their own time zone.
    /// </summary>
    internal static class DiscordStyle
    {
        public const int Blurple = 0x5865F2;
        public const int Red = 0xED4245;
        public const int Green = 0x57F287;
        public const int Orange = 0xF0A030;

        private const string FallbackServerName = "Unnamed server";

        /// <summary>
        /// The name shown on every message: the one set in the Discord tab, else the dedicated server's name, else the world name.
        /// </summary>
        public static string ServerName
        {
            get
            {
                string? configured = Advanced_PB_Limiter.Instance?.Config?.DiscordServerName;
                if (!string.IsNullOrWhiteSpace(configured)) return Clean(configured);

                try
                {
                    string? dedicated = MyAPIGateway.Utilities?.ConfigDedicated?.ServerName;
                    if (!string.IsNullOrWhiteSpace(dedicated)) return Clean(dedicated);

                    string? world = MySession.Static?.Name;
                    if (!string.IsNullOrWhiteSpace(world)) return Clean(world);
                }
                catch { /* the game may not be fully loaded yet */ }

                return FallbackServerName;
            }
        }

        /// <summary>The top of every card: icon and title as a heading, the server on the line below, then an optional note.</summary>
        public static string Header(string icon, string title, string? note = null) =>
            $"## {icon}  {title}\n🖥️  **{DiscordManager.Escape(ServerName)}**" + (string.IsNullOrEmpty(note) ? "" : "  ·  " + note);

        /// <summary>The bottom line: plugin version, server, and the time (shown in each reader's own time zone).</summary>
        public static string Footer(string? tail = null)
        {
            long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string line = $"-# PB Enforcement Core v{Advanced_PB_Limiter.Instance?.VersionText}  ·  {DiscordManager.Escape(ServerName)}  ·  <t:{unix}:f>";
            return string.IsNullOrEmpty(tail) ? line : line + "  ·  " + tail;
        }

        /// <summary>A stat shown as "label value", e.g. "Offenders `3`". Several are joined on one line with Inline.</summary>
        public static string Stat(string label, string value) => $"**{label}**  `{value}`";

        public static string PlayerHeading(string playerName, bool privileged) =>
            "### 👤  " + DiscordManager.Escape(Clean(playerName)) + (privileged ? "  ⭐" : "");

        /// <summary>The Steam id as a link to the profile, or nothing when unknown.</summary>
        public static string SteamLink(ulong steamId) =>
            steamId == 0 ? "" : $"[`{steamId}`](https://steamcommunity.com/profiles/{steamId})";

        /// <summary>One programmable block as a list item: grid › block, with the offence count and entity id when known.</summary>
        public static string BlockLine(string grid, string block, int offences, long entityId = 0)
        {
            string line = $"- 🧱 **{DiscordManager.Escape(Clean(grid))}** › {DiscordManager.Escape(Clean(block))}";
            if (offences > 0) line += $"  `×{offences}`";
            if (entityId != 0) line += $"  ·  ID `{entityId}`";
            return line;
        }

        /// <summary>Joins the parts that are not empty with a middle dot.</summary>
        public static string Inline(params string[] parts)
        {
            List<string> kept = new();
            foreach (string part in parts)
                if (!string.IsNullOrEmpty(part)) kept.Add(part);
            return string.Join("  ·  ", kept);
        }

        public static string Plural(int count, string word) => $"{count} {word}{(count == 1 ? "" : "s")}";

        public static string Clean(string? text) =>
            (text ?? "").Replace("`", "'").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    /// <summary>
    /// One Discord card (a components-v2 container) that keeps itself inside Discord's limits: at most 40 components and
    /// 4000 characters of text per message. TryAdd says no when a part would not fit, so callers can stop adding players.
    /// </summary>
    internal sealed class DiscordCard
    {
        private const int MaxComponents = 40;
        private const int MaxTextCharacters = 4000;
        private const int ReservedComponents = 2;    // the divider and footer added by ToComponents
        private const int ReservedCharacters = 260;  // the footer text

        private const int TextDisplay = 10;
        private const int Separator = 14;
        private const int Container = 17;
        private const int SpacingSmall = 1;
        private const int SpacingLarge = 2;

        private readonly int _accentColor;
        private readonly JArray _items = new();
        private int _components = 1; // the container itself
        private int _characters;

        public DiscordCard(int accentColor)
        {
            _accentColor = accentColor;
        }

        /// <summary>Whether a text block, and a divider before it if asked, would still fit.</summary>
        public bool Fits(string markdown, bool withDivider = false) =>
            _components + (withDivider ? 2 : 1) + ReservedComponents <= MaxComponents &&
            _characters + markdown.Length + ReservedCharacters <= MaxTextCharacters;

        public DiscordCard Text(string markdown)
        {
            markdown = DiscordManager.Truncate(markdown, MaxTextCharacters - ReservedCharacters - _characters);
            _items.Add(new JObject { ["type"] = TextDisplay, ["content"] = markdown });
            _components++;
            _characters += markdown.Length;
            return this;
        }

        /// <summary>A line between parts. Large gives more room around it, for the main sections.</summary>
        public DiscordCard Divider(bool large = false)
        {
            _items.Add(new JObject { ["type"] = Separator, ["divider"] = true, ["spacing"] = large ? SpacingLarge : SpacingSmall });
            _components++;
            return this;
        }

        /// <summary>Adds the text (with a divider before it) only if it fits. Returns whether it was added.</summary>
        public bool TryAdd(string markdown, bool dividerBefore = true)
        {
            if (!Fits(markdown, dividerBefore)) return false;
            if (dividerBefore) Divider();
            Text(markdown);
            return true;
        }

        /// <summary>The finished card with the footer, as the "components" of a webhook message.</summary>
        public JArray ToComponents(string footer)
        {
            JArray items = new(_items)
            {
                new JObject { ["type"] = Separator, ["divider"] = true, ["spacing"] = SpacingLarge },
                new JObject { ["type"] = TextDisplay, ["content"] = DiscordManager.Truncate(footer, ReservedCharacters) }
            };
            return new JArray { new JObject { ["type"] = Container, ["accent_color"] = _accentColor, ["components"] = items } };
        }
    }
}
