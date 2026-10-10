using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Newtonsoft.Json;
using NLog;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Shares the plugin settings between servers through a local SQLite database. Each setting is one row (name, value,
    /// when, and which server wrote it). Pushing writes only what changed; pulling applies every shared setting here.
    /// Settings that describe THIS server (see LocalOnly) are never shared.
    /// </summary>
    public static class SharedSettingsManager
    {
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Shared Settings");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static readonly object Gate = new();

        private const string PrivilegedKey = "PrivilegedPlayers";
        private const string BlacklistKey = "ScriptBlacklist";

        /// <summary>Settings that belong to this one server and are never written to or read from the database.</summary>
        private static readonly HashSet<string> LocalOnly = new(StringComparer.Ordinal)
        {
            nameof(Advanced_PB_LimiterConfig.Enabled),                    // each server is switched on by its own admin
            nameof(Advanced_PB_LimiterConfig.FirstRunCompleted),
            nameof(Advanced_PB_LimiterConfig.AllowStaffCommands),         // a permission the server owner sets per server
            nameof(Advanced_PB_LimiterConfig.DiscordWebhookUrl),          // a private link; servers may post to different channels
            nameof(Advanced_PB_LimiterConfig.ShowTimingsBurp),
            nameof(Advanced_PB_LimiterConfig.SharedSettingsEnabled),
            nameof(Advanced_PB_LimiterConfig.SharedSettingsAutoPush),
            nameof(Advanced_PB_LimiterConfig.SharedSettingsDatabasePath)
        };

        /// <summary>Raised after a pull has changed settings here, so open screens can refresh.</summary>
        public static event Action? Pulled;

        private static string DatabasePath
        {
            get
            {
                string path = Config.SharedSettingsDatabasePath;
                return string.IsNullOrWhiteSpace(path) ? Advanced_PB_LimiterConfig.DefaultSharedSettingsDatabasePath : path.Trim();
            }
        }

        // ---------------------------------------------------------------- pull

        /// <summary>Applies the shared settings to this server. Returns whether it worked, and a message to show.</summary>
        public static (bool Success, string Message) Pull(string reason)
        {
            lock (Gate)
            {
                string path = DatabasePath;
                try
                {
                    if (!File.Exists(path))
                        return (false, $"There is no shared settings database at {path} yet. Push from a server first.");

                    Dictionary<string, string> values;
                    using (SqliteDatabase db = SqliteDatabase.Open(path, false))
                        values = ReadAll(db);

                    if (values.Count == 0)
                        return (false, "The shared settings database is empty. Push from a server first.");

                    int changed = Apply(values);
                    if (changed > 0) Pulled?.Invoke();

                    string message = $"Pulled {values.Count} shared setting(s) from {path}: {changed} changed on this server.";
                    Log.Info($"{message} (reason: {reason})");
                    return (true, message);
                }
                catch (Exception e)
                {
                    return Failed("pull", reason, e);
                }
            }
        }

        // ---------------------------------------------------------------- push

        /// <summary>Writes this server's shared settings to the database (only the ones that differ).</summary>
        public static (bool Success, string Message) Push(string reason)
        {
            lock (Gate)
            {
                string path = DatabasePath;
                try
                {
                    string? folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                    Dictionary<string, string> local = Collect();
                    int written = 0;

                    using (SqliteDatabase db = SqliteDatabase.Open(path, true))
                    {
                        db.Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL, updated_utc TEXT NOT NULL, updated_by TEXT NOT NULL)");
                        db.Execute("BEGIN IMMEDIATE"); // one server writes at a time
                        try
                        {
                            Dictionary<string, string> existing = ReadAll(db);
                            string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                            string by = Advanced_PB_Limiter.Instance?.ServerIdentity ?? Environment.MachineName;

                            foreach (KeyValuePair<string, string> setting in local)
                            {
                                if (existing.TryGetValue(setting.Key, out string? current) && current == setting.Value) continue;
                                db.Execute("INSERT OR REPLACE INTO settings (key, value, updated_utc, updated_by) VALUES (?1, ?2, ?3, ?4)", setting.Key, setting.Value, now, by);
                                written++;
                            }

                            db.Execute("COMMIT");
                        }
                        catch
                        {
                            try { db.Execute("ROLLBACK"); } catch (Exception) { /* nothing more to undo */ }
                            throw;
                        }
                    }

                    string message = $"Pushed {written} changed setting(s) ({local.Count} shared in total) to {path}.";
                    if (written > 0) Log.Info($"{message} (reason: {reason})");
                    return (true, message);
                }
                catch (Exception e)
                {
                    return Failed("push", reason, e);
                }
            }
        }

        /// <summary>Called after the config has been saved: pushes the change when sharing and automatic push are on.</summary>
        public static void PushIfAutomatic()
        {
            if (!Config.SharedSettingsEnabled || !Config.SharedSettingsAutoPush) return;
            Push("settings changed");
        }

        private static (bool, string) Failed(string action, string reason, Exception e)
        {
            string message = e switch
            {
                DllNotFoundException or EntryPointNotFoundException =>
                    "The SQLite library that comes with Windows (winsqlite3.dll) was not found. Sharing settings needs Windows 10 or Windows Server 2016 or newer.",
                SqliteException sqlite => $"The shared settings database reported: {sqlite.Message}",
                UnauthorizedAccessException => $"This server is not allowed to use {DatabasePath}. Check the folder permissions.",
                _ => $"Could not {action} the shared settings: {e.Message}"
            };

            Log.Warn($"{message} (reason: {reason})");
            if (e is not (DllNotFoundException or EntryPointNotFoundException or SqliteException or UnauthorizedAccessException))
                Log.Warn(e, "Shared settings error details.");
            return (false, message);
        }

        // ---------------------------------------------------------------- reading and writing the database

        private static Dictionary<string, string> ReadAll(SqliteDatabase db)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (string[] row in db.Query("SELECT key, value FROM settings", 2))
                values[row[0]] = row[1];
            return values;
        }

        // ---------------------------------------------------------------- which settings are shared, and how they become text

        private static IEnumerable<PropertyInfo> SharedProperties()
        {
            foreach (PropertyInfo property in typeof(Advanced_PB_LimiterConfig).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                if (LocalOnly.Contains(property.Name) || property.Name == PrivilegedKey || property.Name == BlacklistKey) continue;

                Type type = property.PropertyType;
                if (type.IsPrimitive || type.IsEnum || type == typeof(string)) yield return property;
            }
        }

        private static Dictionary<string, string> Collect()
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);

            foreach (PropertyInfo property in SharedProperties())
            {
                object? value = property.GetValue(Config);
                values[property.Name] = value switch
                {
                    null => "",
                    string text => text,
                    bool flag => flag ? "true" : "false",
                    Enum item => item.ToString(),
                    IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? ""
                };
            }

            values[BlacklistKey] = JsonConvert.SerializeObject(Config.ScriptBlacklist);
            values[PrivilegedKey] = JsonConvert.SerializeObject(Config.PrivilegedPlayers.Values.ToList());
            return values;
        }

        private static object? Parse(Type type, string text)
        {
            try
            {
                if (type == typeof(string)) return text;
                if (type == typeof(bool)) return text.Equals("true", StringComparison.OrdinalIgnoreCase) ? true : text.Equals("false", StringComparison.OrdinalIgnoreCase) ? false : null;
                if (type.IsEnum) return Enum.Parse(type, text, true);
                return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null; // an unreadable value is skipped, not applied
            }
        }

        /// <summary>Applies the shared values to this server. Returns how many settings changed.</summary>
        private static int Apply(Dictionary<string, string> values)
        {
            int changed = 0;

            foreach (PropertyInfo property in SharedProperties())
            {
                if (!values.TryGetValue(property.Name, out string? text)) continue;

                object? parsed = Parse(property.PropertyType, text);
                if (parsed is null)
                {
                    Log.Warn($"Skipped the shared setting {property.Name}: the value \"{text}\" could not be read.");
                    continue;
                }

                if (Equals(property.GetValue(Config), parsed)) continue;
                property.SetValue(Config, parsed);
                changed++;
            }

            if (values.TryGetValue(BlacklistKey, out string? blacklistJson))
            {
                List<BlacklistedScript>? list = SafeDeserialize<List<BlacklistedScript>>(blacklistJson, BlacklistKey);
                if (list != null && JsonConvert.SerializeObject(list) != JsonConvert.SerializeObject(Config.ScriptBlacklist))
                {
                    ScriptBlacklistManager.ReplaceAll(list);
                    changed++;
                }
            }

            if (values.TryGetValue(PrivilegedKey, out string? playersJson))
            {
                List<PrivilegedPlayer>? players = SafeDeserialize<List<PrivilegedPlayer>>(playersJson, PrivilegedKey);
                if (players != null && JsonConvert.SerializeObject(players) != JsonConvert.SerializeObject(Config.PrivilegedPlayers.Values.ToList()))
                {
                    foreach (ulong id in Config.PrivilegedPlayers.Keys.ToList()) Config.PrivilegedPlayers.Remove(id);
                    foreach (PrivilegedPlayer player in players) Config.PrivilegedPlayers[player.SteamId] = player;
                    changed++;
                }
            }

            return changed;
        }

        private static T? SafeDeserialize<T>(string json, string name) where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (Exception e)
            {
                Log.Warn(e, $"Skipped the shared setting {name}: it could not be read.");
                return null;
            }
        }
    }
}
