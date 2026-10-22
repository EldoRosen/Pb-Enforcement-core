﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Newtonsoft.Json;
using NLog;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Shares the plugin settings live between servers through a local SQLite database. Each setting is one row (name,
    /// value, revision, when, and which server wrote it).
    ///
    /// Sending: every change raises the config's INotifyPropertyChanged (or a privileged player's), SettingsAutoSaver
    /// passes it on, and the changed settings are written a moment later with a new revision number.
    /// Receiving: every few seconds each server reads the rows with a revision newer than the last one it saw, and applies
    /// those written by other servers.
    ///
    /// LastSynced holds each setting as it was when this server last sent or received it. A setting whose value here
    /// differs from that is a local edit not sent yet: it is sent, and an incoming value never overwrites it. A value
    /// that came in from another server equals LastSynced, so it is never echoed back.
    ///
    /// Settings that describe THIS server (see LocalOnly) are never shared.
    /// </summary>
    public static class SharedSettingsManager
    {
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Shared Settings");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;
        private static readonly object Gate = new();

        private const string PrivilegedKey = "PrivilegedPlayers";
        private const string BlacklistKey = "ScriptBlacklist";

        private const int PollEveryMilliseconds = 3000;
        private const int SendDelayMilliseconds = 750; // typing in a box sends once, after a short pause
        private const int HeartbeatEverySeconds = 30;
        private const int ConnectedWithinSeconds = 90;

        /// <summary>Settings that belong to this one server and are never written to or read from the database.</summary>
        private static readonly HashSet<string> LocalOnly = new(StringComparer.Ordinal)
        {
            nameof(Advanced_PB_LimiterConfig.Enabled),                    // each server is switched on by its own admin
            nameof(Advanced_PB_LimiterConfig.FirstRunCompleted),
            nameof(Advanced_PB_LimiterConfig.AllowStaffCommands),         // a permission the server owner sets per server
            nameof(Advanced_PB_LimiterConfig.DiscordWebhookUrl),          // a private link; servers may post to different channels
            nameof(Advanced_PB_LimiterConfig.DiscordServerName),          // tells the servers apart in a shared channel
            nameof(Advanced_PB_LimiterConfig.ShowTimingsBurp),
            nameof(Advanced_PB_LimiterConfig.SharedSettingsEnabled),
            nameof(Advanced_PB_LimiterConfig.SharedSettingsAutoPush),
            nameof(Advanced_PB_LimiterConfig.SharedSettingsAutoPull),
            nameof(Advanced_PB_LimiterConfig.SharedSettingsDatabasePath)
        };

        private static readonly Dictionary<string, string> LastSynced = new(StringComparer.Ordinal);
        private static long _lastRevision;
        private static volatile bool _live;
        private static Timer? _pollTimer;
        private static Timer? _sendTimer;
        private static int _polling;
        private static DateTime _lastHeartbeatUtc = DateTime.MinValue;
        private static bool _warnedPollFailure;

        /// <summary>Raised after settings here were changed by a pull or a live update, so open screens can refresh.</summary>
        public static event Action? Pulled;

        /// <summary>Raised when Status changes (from any thread).</summary>
        public static event Action? StatusChanged;

        /// <summary>One line for the Shared Settings tab: connected or not, which servers, and the last change received.</summary>
        public static string Status { get; private set; } = "Sharing is off.";

        private static string Me => Advanced_PB_Limiter.Instance?.ServerIdentity ?? Environment.MachineName;

        private static string DatabasePath
        {
            get
            {
                string path = Config.SharedSettingsDatabasePath;
                return string.IsNullOrWhiteSpace(path) ? Advanced_PB_LimiterConfig.DefaultSharedSettingsDatabasePath : path.Trim();
            }
        }

        // ---------------------------------------------------------------- lifecycle

        /// <summary>Called once at plugin start. Starts live sharing if it is switched on, and follows the switch from then on.</summary>
        public static void Initialize()
        {
            Config.PropertyChanged += OnConfigPropertyChanged;
            SettingsAutoSaver.Changed += OnSettingChanged;
            if (Config.SharedSettingsEnabled) StartLive("server start");
        }

        private static void OnConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(Advanced_PB_LimiterConfig.SharedSettingsEnabled):
                    if (Config.SharedSettingsEnabled) ThreadPool.QueueUserWorkItem(_ => StartLive("sharing switched on"));
                    else StopLive("Sharing is off.");
                    break;

                case nameof(Advanced_PB_LimiterConfig.SharedSettingsDatabasePath):
                    if (Config.SharedSettingsEnabled) ThreadPool.QueueUserWorkItem(_ => StartLive("database path changed"));
                    break;

                case nameof(Advanced_PB_LimiterConfig.SharedSettingsAutoPush):
                    if (Config.SharedSettingsAutoPush) ScheduleSend(); // send what was held back while it was off
                    break;

                case nameof(Advanced_PB_LimiterConfig.SharedSettingsAutoPull):
                    // Read every row again on the next poll, to take in what was skipped while it was off.
                    if (Config.SharedSettingsAutoPull) Interlocked.Exchange(ref _lastRevision, 0);
                    break;
            }
        }

        /// <summary>Any setting changed here (raised through INotifyPropertyChanged): send it to the other servers shortly.</summary>
        private static void OnSettingChanged(string? propertyName)
        {
            if (propertyName != null && LocalOnly.Contains(propertyName)) return;
            ScheduleSend();
        }

        private static void ScheduleSend()
        {
            // No lock here: this runs inside property setters, possibly on the window's thread while a live update holds Gate.
            if (!_live || !Config.SharedSettingsAutoPush) return;
            try { _sendTimer?.Change(SendDelayMilliseconds, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* sharing was just stopped */ }
        }

        /// <summary>
        /// Connects to the database and brings this server in line with it: the shared values are applied here, and any
        /// setting the database does not have yet is added. Then sending and receiving run on their own.
        /// </summary>
        private static void StartLive(string reason)
        {
            lock (Gate)
            {
                StopTimers();
                _live = false;

                string path = DatabasePath;
                try
                {
                    string? folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                    int applied, added;
                    using (SqliteDatabase db = SqliteDatabase.Open(path, true))
                    {
                        EnsureSchema(db);
                        Dictionary<string, string> shared = ReadAll(db);
                        applied = shared.Count == 0 ? 0 : Apply(shared);

                        // Everything is now in sync except the settings the database does not have yet: add those.
                        Dictionary<string, string> local = Collect();
                        LastSynced.Clear();
                        Dictionary<string, string> missing = local.Where(s => !shared.ContainsKey(s.Key)).ToDictionary(s => s.Key, s => s.Value);
                        added = Write(db, missing);
                        foreach (KeyValuePair<string, string> setting in local) LastSynced[setting.Key] = setting.Value;

                        _lastRevision = MaxRevision(db);
                        Heartbeat(db);
                    }

                    _live = true;
                    _warnedPollFailure = false;
                    _pollTimer = new Timer(_ => Poll(), null, PollEveryMilliseconds, PollEveryMilliseconds);
                    _sendTimer = new Timer(_ => SendChanges(), null, Timeout.Infinite, Timeout.Infinite);

                    Log.Info($"Live sharing started with {path} ({reason}): {applied} setting(s) taken from the database, {added} added to it.");
                    if (applied > 0) Pulled?.Invoke();
                    UpdateStatus(null);
                }
                catch (Exception e)
                {
                    (_, string message) = Failed("connect to", reason, e);
                    SetStatus("🔴 Not connected. " + message);
                }
            }
        }

        private static void StopLive(string status)
        {
            lock (Gate)
            {
                _live = false;
                StopTimers();
            }
            SetStatus(status);
        }

        private static void StopTimers()
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            _sendTimer?.Dispose();
            _sendTimer = null;
        }

        // ---------------------------------------------------------------- live sending and receiving

        /// <summary>Writes every setting changed here since it was last sent or received.</summary>
        private static void SendChanges()
        {
            lock (Gate)
            {
                if (!_live || !Config.SharedSettingsAutoPush) return;

                try
                {
                    Dictionary<string, string> changed = LocalEdits(Collect());
                    if (changed.Count == 0) return;

                    using (SqliteDatabase db = SqliteDatabase.Open(DatabasePath, true))
                        Write(db, changed);

                    foreach (KeyValuePair<string, string> setting in changed) LastSynced[setting.Key] = setting.Value;
                    Log.Info($"Sent {changed.Count} changed setting(s) to the other servers: {string.Join(", ", changed.Keys)}.");
                }
                catch (Exception e)
                {
                    (_, string message) = Failed("send", "setting changed", e);
                    SetStatus("🟠 Could not send the last change. " + message);
                    _sendTimer?.Change(PollEveryMilliseconds * 2, Timeout.Infinite); // try again in a moment
                }
            }
        }

        /// <summary>Reads the rows written since the last poll and applies the ones from other servers.</summary>
        private static void Poll()
        {
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;

            try
            {
                lock (Gate)
                {
                    if (!_live) return;

                    List<string[]> rows;
                    bool heartbeatDue = (DateTime.UtcNow - _lastHeartbeatUtc).TotalSeconds >= HeartbeatEverySeconds;
                    using (SqliteDatabase db = SqliteDatabase.Open(DatabasePath, heartbeatDue))
                    {
                        rows = db.Query("SELECT key, value, updated_by, rev FROM settings WHERE rev > CAST(?1 AS INTEGER) ORDER BY rev",
                            4, _lastRevision.ToString(CultureInfo.InvariantCulture));
                        if (heartbeatDue)
                        {
                            Heartbeat(db);
                            UpdateStatus(null, db);
                        }
                    }
                    _warnedPollFailure = false;
                    if (rows.Count == 0) return;

                    long newest = rows.Max(r => long.TryParse(r[3], out long rev) ? rev : 0);
                    if (!Config.SharedSettingsAutoPull)
                    {
                        _lastRevision = newest;
                        return;
                    }

                    // An edit made here that is not sent yet wins over an incoming value; it is sent next.
                    Dictionary<string, string> edits = LocalEdits(Collect());
                    Dictionary<string, string> incoming = new(StringComparer.Ordinal);
                    HashSet<string> from = new();
                    foreach (string[] row in rows)
                    {
                        if (row[2] == Me || edits.ContainsKey(row[0])) continue;
                        incoming[row[0]] = row[1];
                        from.Add(row[2]);
                    }

                    int changed = incoming.Count == 0 ? 0 : Apply(incoming);
                    foreach (KeyValuePair<string, string> setting in incoming)
                        LastSynced[setting.Key] = Canonical(setting.Key, setting.Value);
                    _lastRevision = newest;

                    if (changed > 0)
                    {
                        string who = string.Join(", ", from.Select(ShortName));
                        Log.Info($"Received {changed} setting change(s) from {who}: {string.Join(", ", incoming.Keys)}.");
                        UpdateStatus($"Last change received {DateTime.Now:HH:mm:ss} from {who}.");
                        Pulled?.Invoke();
                    }
                }
            }
            catch (Exception e)
            {
                if (!_warnedPollFailure)
                {
                    _warnedPollFailure = true;
                    (_, string message) = Failed("read", "live update", e);
                    SetStatus("🟠 Connection problem, retrying. " + message);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        /// <summary>The settings whose value here differs from what was last sent or received.</summary>
        private static Dictionary<string, string> LocalEdits(Dictionary<string, string> local)
        {
            Dictionary<string, string> edits = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> setting in local)
                if (!LastSynced.TryGetValue(setting.Key, out string? synced) || synced != setting.Value)
                    edits[setting.Key] = setting.Value;
            return edits;
        }

        // ---------------------------------------------------------------- status

        private static string _lastReceived = "";

        private static void UpdateStatus(string? lastReceived, SqliteDatabase? db = null)
        {
            if (lastReceived != null) _lastReceived = lastReceived;

            List<string> servers = new();
            try
            {
                if (db != null) servers = ConnectedServers(db);
                else
                {
                    using SqliteDatabase read = SqliteDatabase.Open(DatabasePath, false);
                    servers = ConnectedServers(read);
                }
            }
            catch (Exception) { /* the status line is best effort */ }

            string list = servers.Count == 0 ? "only this server" : $"{servers.Count} server(s): {string.Join(", ", servers)}";
            SetStatus($"🟢 Live. Changes are shared within a few seconds. Connected: {list}. {_lastReceived}".Trim());
        }

        private static void SetStatus(string status)
        {
            Status = status;
            StatusChanged?.Invoke();
        }

        private static string ShortName(string identity)
        {
            int bar = identity.IndexOf(" | ", StringComparison.Ordinal);
            return bar > 0 ? identity.Substring(0, bar) : identity;
        }

        // ---------------------------------------------------------------- manual pull and push (buttons)

        /// <summary>Applies all shared settings to this server, including any edits here that were not sent yet.</summary>
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
                    foreach (KeyValuePair<string, string> setting in values)
                        LastSynced[setting.Key] = Canonical(setting.Key, setting.Value);
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

        /// <summary>Writes every shared setting of this server that differs from the database.</summary>
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
                    int written;

                    using (SqliteDatabase db = SqliteDatabase.Open(path, true))
                    {
                        EnsureSchema(db);
                        Dictionary<string, string> existing = ReadAll(db);
                        written = Write(db, local.Where(s => !existing.TryGetValue(s.Key, out string? current) || current != s.Value)
                            .ToDictionary(s => s.Key, s => s.Value));
                    }

                    foreach (KeyValuePair<string, string> setting in local) LastSynced[setting.Key] = setting.Value;

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

        // ---------------------------------------------------------------- the database

        private static void EnsureSchema(SqliteDatabase db)
        {
            db.Execute("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL, updated_utc TEXT NOT NULL, updated_by TEXT NOT NULL, rev INTEGER NOT NULL DEFAULT 0)");
            db.Execute("CREATE TABLE IF NOT EXISTS servers (identity TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL, last_seen_utc TEXT NOT NULL)");

            // Databases made by 3.0.5 and older have no revision column.
            bool hasRevision = db.Query("PRAGMA table_info(settings)", 2).Any(column => column[1] == "rev");
            if (!hasRevision) db.Execute("ALTER TABLE settings ADD COLUMN rev INTEGER NOT NULL DEFAULT 0");
        }

        private static Dictionary<string, string> ReadAll(SqliteDatabase db)
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (string[] row in db.Query("SELECT key, value FROM settings", 2))
                values[row[0]] = row[1];
            return values;
        }

        private static long MaxRevision(SqliteDatabase db)
        {
            List<string[]> rows = db.Query("SELECT COALESCE(MAX(rev), 0) FROM settings", 1);
            return rows.Count > 0 && long.TryParse(rows[0][0], out long rev) ? rev : 0;
        }

        /// <summary>Writes the settings in one transaction, all with the same new revision number. Returns how many.</summary>
        private static int Write(SqliteDatabase db, Dictionary<string, string> settings)
        {
            if (settings.Count == 0) return 0;

            db.Execute("BEGIN IMMEDIATE"); // one server writes at a time
            try
            {
                string revision = (MaxRevision(db) + 1).ToString(CultureInfo.InvariantCulture);
                string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (KeyValuePair<string, string> setting in settings)
                    db.Execute("INSERT OR REPLACE INTO settings (key, value, updated_utc, updated_by, rev) VALUES (?1, ?2, ?3, ?4, CAST(?5 AS INTEGER))",
                        setting.Key, setting.Value, now, Me, revision);
                db.Execute("COMMIT");
                return settings.Count;
            }
            catch
            {
                try { db.Execute("ROLLBACK"); } catch (Exception) { /* nothing more to undo */ }
                throw;
            }
        }

        /// <summary>Tells the other servers this one is connected (shown in their Shared Settings tab).</summary>
        private static void Heartbeat(SqliteDatabase db)
        {
            db.Execute("INSERT OR REPLACE INTO servers (identity, name, last_seen_utc) VALUES (?1, ?2, ?3)",
                Me, DiscordStyle.ServerName, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            _lastHeartbeatUtc = DateTime.UtcNow;
        }

        private static List<string> ConnectedServers(SqliteDatabase db)
        {
            List<string> names = new();
            foreach (string[] row in db.Query("SELECT name, last_seen_utc FROM servers ORDER BY name", 2))
            {
                if (!DateTime.TryParse(row[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime seen)) continue;
                if ((DateTime.UtcNow - seen.ToUniversalTime()).TotalSeconds <= ConnectedWithinSeconds) names.Add(row[0]);
            }
            return names;
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

        private static string Format(object? value) => value switch
        {
            null => "",
            string text => text,
            bool flag => flag ? "true" : "false",
            Enum item => item.ToString(),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

        private static string PlayersJson(IEnumerable<PrivilegedPlayer> players) =>
            JsonConvert.SerializeObject(players.OrderBy(p => p.SteamId).ToList());

        private static Dictionary<string, string> Collect()
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (PropertyInfo property in SharedProperties())
                values[property.Name] = Format(property.GetValue(Config));

            values[BlacklistKey] = JsonConvert.SerializeObject(Config.ScriptBlacklist);
            values[PrivilegedKey] = PlayersJson(Config.PrivilegedPlayers.Values);
            return values;
        }

        /// <summary>A shared value written the way Collect writes it, so values that mean the same compare equal.</summary>
        private static string Canonical(string key, string text)
        {
            try
            {
                if (key == BlacklistKey) return JsonConvert.SerializeObject(JsonConvert.DeserializeObject<List<BlacklistedScript>>(text) ?? new List<BlacklistedScript>());
                if (key == PrivilegedKey) return PlayersJson(JsonConvert.DeserializeObject<List<PrivilegedPlayer>>(text) ?? new List<PrivilegedPlayer>());

                PropertyInfo? property = typeof(Advanced_PB_LimiterConfig).GetProperty(key, BindingFlags.Instance | BindingFlags.Public);
                object? parsed = property is null ? null : Parse(property.PropertyType, text);
                return parsed is null ? text : Format(parsed);
            }
            catch (Exception)
            {
                return text;
            }
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
                if (players != null && PlayersJson(players) != PlayersJson(Config.PrivilegedPlayers.Values))
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
