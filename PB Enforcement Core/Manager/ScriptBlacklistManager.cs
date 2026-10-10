using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using NLog;
using Sandbox;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.World;
using VRageMath;

namespace Advanced_PB_Limiter.Manager
{
    /// <summary>
    /// Blocks scripts an admin has blacklisted. A block is checked once per compile; blocked blocks are switched off and never run.
    /// </summary>
    public static class ScriptBlacklistManager
    {
        private static readonly Logger Log = LogManager.GetLogger("PB Enforcement Core => Script Blacklist");
        private static Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        public const int MinHashLength = 12;
        public const int MinSignatureLength = 8;
        private const int NotifyEverySeconds = 300;

        private sealed class CheckResult
        {
            public bool Blocked;
            public bool Unreadable; // the script text could not be read, so nothing was decided
            public string EntryName = "";
            public long LastNotifyTick;
        }

        private static readonly ConcurrentDictionary<long, CheckResult> Results = new();
        private static readonly object EditLock = new();

        private static PropertyInfo? _scriptProperty;
        private static FieldInfo? _scriptField;
        private static bool _reflectionResolved;

        private static long _runChecks;
        private static long _evaluations;
        private static long _blocksStopped;
        private static Timer? _sweepTimer;
        private const int SweepEverySeconds = 10;

        // ---------------------------------------------------------------- lifecycle

        public static void Start()
        {
            Log.Info($"Script blacklist is {(Config.ScriptBlacklistEnabled ? "ON" : "OFF")} with {Config.ScriptBlacklist.Count} entr{(Config.ScriptBlacklist.Count == 1 ? "y" : "ies")}.");
            _sweepTimer?.Dispose();
            _sweepTimer = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(SweepEverySeconds), TimeSpan.FromSeconds(SweepEverySeconds));
        }

        /// <summary>
        /// Second line of defence that does not depend on a block running: every few seconds, every tracked programmable
        /// block is checked, so a blacklisted script is switched off even if the run hook is never reached.
        /// </summary>
        private static void Sweep()
        {
            try
            {
                if (!Config.Enabled || !Config.ScriptBlacklistEnabled || Config.ScriptBlacklist.Count == 0) return;

                foreach (TrackedPlayer player in TrackingManager.GetTrackedPlayerData())
                {
                    List<TrackedPBBlock> blocks = player.GetAllPBBlocks;
                    for (int i = 0; i < blocks.Count; i++)
                    {
                        MyProgrammableBlock? pb = blocks[i]?.ProgrammableBlock;
                        if (pb is null) continue;
                        IsBlocked(pb, false);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn(e, "Script blacklist sweep failed.");
            }
        }

        // ---------------------------------------------------------------- checking

        /// <summary>
        /// Called every time a programmable block is about to run. Cheap after the first call for a compiled script.
        /// Returns true when the block must NOT run.
        /// </summary>
        public static bool IsBlocked(MyProgrammableBlock pb, bool fromRunHook = true)
        {
            if (fromRunHook) Interlocked.Increment(ref _runChecks);
            if (!Config.ScriptBlacklistEnabled) return false;

            if (!Results.TryGetValue(pb.EntityId, out CheckResult? result))
            {
                result = Evaluate(pb);

                // If the script could not be read, do not remember the block as "clean": look again next time.
                if (result.Unreadable) return false;

                Results[pb.EntityId] = result;
                Interlocked.Increment(ref _evaluations);

                if (result.Blocked)
                {
                    Interlocked.Increment(ref _blocksStopped);
                    Log.Warn($"Blocked script \"{result.EntryName}\" on [{pb.CubeGrid?.DisplayName} >> {pb.CustomName}] (id {pb.EntityId}, owner {pb.OwnerId}).");
                }
            }

            if (!result.Blocked) return false;

            Enforce(pb, result);
            return true;
        }

        /// <summary>The block was recompiled, so its script may have changed.</summary>
        public static void Invalidate(long entityId) => Results.TryRemove(entityId, out _);

        /// <summary>Forget every result so all blocks are checked again the next time they run (use after editing the list).</summary>
        public static void Rescan() => Results.Clear();

        public static void Reset()
        {
            _sweepTimer?.Dispose();
            _sweepTimer = null;
            Results.Clear();
        }

        private static CheckResult Evaluate(MyProgrammableBlock pb)
        {
            CheckResult result = new();
            List<BlacklistedScript> entries = Config.ScriptBlacklist;
            if (entries.Count == 0) return result;

            string? script = GetScriptText(pb);
            if (string.IsNullOrEmpty(script))
            {
                result.Unreadable = true;
                return result;
            }

            string normalized = Normalize(script!);
            string hash = Sha256Hex(normalized);

            foreach (BlacklistedScript entry in entries)
            {
                if (entry is null) continue;
                if (!Matches(entry, normalized, hash)) continue;

                result.Blocked = true;
                result.EntryName = string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed script" : entry.Name.Trim();
                return result;
            }

            return result;
        }

        private static bool Matches(BlacklistedScript entry, string normalizedScript, string scriptHash)
        {
            string entryHash = (entry.Hash ?? "").Trim();
            if (entryHash.Length >= MinHashLength && scriptHash.StartsWith(entryHash, StringComparison.OrdinalIgnoreCase))
                return true;

            string signature = Normalize(entry.Signature ?? "");
            // A tiny signature would match almost every script, so short ones are ignored.
            return signature.Length >= MinSignatureLength && normalizedScript.IndexOf(signature, StringComparison.Ordinal) >= 0;
        }

        private static void Enforce(MyProgrammableBlock pb, CheckResult result)
        {
            if (pb.Enabled)
            {
                BlockActionLog.Record(pb, "Switched off", $"it runs the blacklisted script \"{result.EntryName}\"");
                MySandboxGame.Static.Invoke(() => { pb.Enabled = false; }, "PB Enforcement Core");
            }

            Notify(pb, result);
        }

        private static void Notify(MyProgrammableBlock pb, CheckResult result)
        {
            long now = Stopwatch.GetTimestamp();
            if (result.LastNotifyTick != 0 && (now - result.LastNotifyTick) / Stopwatch.Frequency < NotifyEverySeconds) return;
            result.LastNotifyTick = now;

            long ownerId = pb.OwnerId;
            if (ownerId == 0 || MySession.Static.Players.IdentityIsNpc(ownerId)) return;
            if (!MySession.Static.Players.IsPlayerOnline(ownerId)) return;

            ulong steamId = MySession.Static.Players.TryGetSteamId(ownerId);
            if (steamId == 0) return;

            Chat.Send(FormatMessage(pb, result.EntryName), steamId, Color.Red);
        }

        private static string FormatMessage(MyProgrammableBlock pb, string scriptName)
        {
            string template = string.IsNullOrWhiteSpace(Config.ScriptBlacklistMessage)
                ? Advanced_PB_LimiterConfig.DefaultScriptBlacklistMessage
                : Config.ScriptBlacklistMessage;

            return template
                .Replace("{block}", pb.CustomName?.ToString() ?? "Programmable Block")
                .Replace("{grid}", pb.CubeGrid?.DisplayName ?? "Unknown grid")
                .Replace("{script}", scriptName);
        }

        // ---------------------------------------------------------------- script text and fingerprint

        private static void ResolveScriptReader()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = typeof(MyProgrammableBlock);

            PropertyInfo? property = type.GetProperty("ProgramData", flags);
            if (property != null && (!property.CanRead || property.PropertyType != typeof(string) || property.GetIndexParameters().Length != 0)) property = null;

            FieldInfo? field = type.GetField("m_programData", flags);
            if (field != null && field.FieldType != typeof(string)) field = null;

            if (property is null && field is null)
            {
                // The game may have renamed it: take a string member that mentions "program" (never a name or argument).
                bool Looks(string name) => name.IndexOf("program", StringComparison.OrdinalIgnoreCase) >= 0
                                           && name.IndexOf("name", StringComparison.OrdinalIgnoreCase) < 0
                                           && name.IndexOf("arg", StringComparison.OrdinalIgnoreCase) < 0;

                property = type.GetProperties(flags).FirstOrDefault(p => p.PropertyType == typeof(string) && p.CanRead && p.GetIndexParameters().Length == 0 && Looks(p.Name));
                field = type.GetFields(flags).FirstOrDefault(f => f.FieldType == typeof(string) && Looks(f.Name));
            }

            _scriptProperty = property;
            _scriptField = field;
            _reflectionResolved = true;

            if (property != null || field != null)
            {
                Log.Info($"Script blacklist reads scripts from {(property != null ? "property " + property.Name : "field " + field!.Name)}.");
                return;
            }

            IEnumerable<string> stringMembers = type.GetProperties(flags).Where(p => p.PropertyType == typeof(string)).Select(p => "property " + p.Name)
                .Concat(type.GetFields(flags).Where(f => f.FieldType == typeof(string)).Select(f => "field " + f.Name));
            Log.Error("Could not find where MyProgrammableBlock keeps its script, so the script blacklist cannot work. String members found: " + string.Join(", ", stringMembers));
        }

        private static string? GetScriptText(MyProgrammableBlock pb)
        {
            try
            {
                if (!_reflectionResolved) ResolveScriptReader();

                if (_scriptProperty != null) return (string?)_scriptProperty.GetValue(pb);
                if (_scriptField != null) return (string?)_scriptField.GetValue(pb);
            }
            catch (Exception e)
            {
                Log.Warn(e, "Could not read the script from a programmable block.");
                return null;
            }

            return null;
        }

        /// <summary>A plain-text report for admins: why is (or is not) this block being blocked?</summary>
        public static string Diagnose(MyProgrammableBlock pb)
        {
            StringBuilder sb = new();
            sb.AppendLine($"Script blacklist check for [{pb.CubeGrid?.DisplayName} >> {pb.CustomName}] (id {pb.EntityId})");
            sb.AppendLine($"Blacklist is {(Config.ScriptBlacklistEnabled ? "ON" : "OFF")} with {Config.ScriptBlacklist.Count} entr{(Config.ScriptBlacklist.Count == 1 ? "y" : "ies")}.");

            string? script = GetScriptText(pb);
            if (string.IsNullOrEmpty(script))
            {
                sb.AppendLine(_scriptProperty is null && _scriptField is null
                    ? "Script text: CANNOT BE READ. This game version keeps the script somewhere unexpected. Check the server log for the list of members."
                    : "Script text: the block has no script in it.");
            }
            else
            {
                string normalized = Normalize(script!);
                string hash = Sha256Hex(normalized);
                sb.AppendLine($"Script text: found, {normalized.Length} characters.");
                sb.AppendLine($"Fingerprint: {hash}");

                List<string> matches = new();
                foreach (BlacklistedScript entry in Config.ScriptBlacklist)
                {
                    string entryHash = (entry.Hash ?? "").Trim();
                    bool byHash = entryHash.Length >= MinHashLength && hash.StartsWith(entryHash, StringComparison.OrdinalIgnoreCase);
                    string signature = Normalize(entry.Signature ?? "");
                    bool byText = signature.Length >= MinSignatureLength && normalized.IndexOf(signature, StringComparison.Ordinal) >= 0;
                    if (byHash || byText) matches.Add($"{entry.Name} ({(byHash ? "fingerprint" : "text")} match)");
                }
                sb.AppendLine(matches.Count == 0 ? "Matches: none, this script is not on the blacklist." : "Matches: " + string.Join(", ", matches));
            }

            sb.AppendLine(Results.TryGetValue(pb.EntityId, out CheckResult? cached)
                ? $"Last decision: {(cached.Blocked ? "BLOCKED as \"" + cached.EntryName + "\"" : "allowed")}."
                : "Last decision: not checked yet (it is checked the next time the block runs, or within a few seconds).");
            sb.AppendLine($"Block is currently {(pb.Enabled ? "ON" : "OFF")}.");
            sb.AppendLine($"Since the server started: {Interlocked.Read(ref _runChecks)} run checks, {Interlocked.Read(ref _evaluations)} scripts checked, {Interlocked.Read(ref _blocksStopped)} scripts blocked.");
            return sb.ToString();
        }

        /// <summary>SHA-256 fingerprint of the script currently in a block, or null when it cannot be read.</summary>
        public static string? GetFingerprint(MyProgrammableBlock pb)
        {
            string? script = GetScriptText(pb);
            return string.IsNullOrEmpty(script) ? null : Sha256Hex(Normalize(script!));
        }

        /// <summary>Line endings and surrounding whitespace never change the fingerprint.</summary>
        private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

        private static string Sha256Hex(string text)
        {
            using SHA256 sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            StringBuilder sb = new(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ---------------------------------------------------------------- editing the list

        public static bool Validate(BlacklistedScript entry, out string error)
        {
            string hash = (entry.Hash ?? "").Trim();
            string signature = (entry.Signature ?? "").Trim();

            if (hash.Length == 0 && signature.Length == 0)
            {
                error = "Enter a fingerprint, a text signature, or both.";
                return false;
            }
            if (hash.Length > 0 && (hash.Length < MinHashLength || hash.Length > 64 || !hash.All(Uri.IsHexDigit)))
            {
                error = $"The fingerprint must be {MinHashLength} to 64 hexadecimal characters.";
                return false;
            }
            if (signature.Length > 0 && Normalize(signature).Length < MinSignatureLength)
            {
                error = $"The text signature must be at least {MinSignatureLength} characters, otherwise it could block unrelated scripts.";
                return false;
            }

            error = "";
            return true;
        }

        /// <summary>Adds one entry and saves. Returns false (with a reason) if it is invalid or already listed.</summary>
        public static bool AddEntry(BlacklistedScript entry, out string message)
        {
            entry.Name = string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed script" : entry.Name.Trim();
            entry.Hash = (entry.Hash ?? "").Trim().ToLowerInvariant();
            entry.Signature = (entry.Signature ?? "").Trim();
            entry.Note = (entry.Note ?? "").Trim();

            if (!Validate(entry, out message)) return false;

            lock (EditLock)
            {
                foreach (BlacklistedScript existing in Config.ScriptBlacklist)
                {
                    bool sameHash = entry.Hash.Length > 0 && string.Equals(existing.Hash?.Trim(), entry.Hash, StringComparison.OrdinalIgnoreCase);
                    bool sameSignature = entry.Signature.Length > 0 && string.Equals(existing.Signature?.Trim(), entry.Signature, StringComparison.Ordinal);
                    if (!sameHash && !sameSignature) continue;

                    message = $"Already blacklisted as \"{existing.Name}\".";
                    return false;
                }

                List<BlacklistedScript> updated = new(Config.ScriptBlacklist) { entry };
                Commit(updated);
            }

            message = $"Blacklisted \"{entry.Name}\".";
            return true;
        }

        /// <summary>Removes every entry with this name (case-insensitive). Returns how many were removed.</summary>
        public static int RemoveByName(string name)
        {
            lock (EditLock)
            {
                List<BlacklistedScript> updated = Config.ScriptBlacklist
                    .Where(e => !string.Equals(e.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                int removed = Config.ScriptBlacklist.Count - updated.Count;
                if (removed > 0) Commit(updated);
                return removed;
            }
        }

        public static void ReplaceAll(List<BlacklistedScript> entries)
        {
            lock (EditLock) Commit(entries);
        }

        private static void Commit(List<BlacklistedScript> entries)
        {
            void Apply() => Config.ScriptBlacklist = entries;

            // The list is shown in the settings window, so it is changed on the window's thread when there is one. If that thread
            // is not running (no window open, headless server), Invoke would wait forever and freeze the command, so give it two
            // seconds and then apply the change right here.
            System.Windows.Threading.Dispatcher? dispatcher = Advanced_PB_Limiter.UI_Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
            {
                System.Windows.Threading.DispatcherOperation operation = dispatcher.InvokeAsync(Apply);
                if (operation.Wait(TimeSpan.FromSeconds(2)) != System.Windows.Threading.DispatcherOperationStatus.Completed)
                {
                    operation.Abort();
                    Apply();
                }
            }
            else
                Apply();

            Rescan();
            Advanced_PB_Limiter.Instance!.Save();
        }
    }
}
