using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Advanced_PB_Limiter.Utils;
using Sandbox;
using Sandbox.ModAPI.Ingame;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Commands
{
    [Category("PB")]
    public class StaffCommands : CommandModule
    {
        private Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        /// <summary>
        /// Commands that change something obey the owner's "Deny Staff Commands" switch. The server owner and the Torch console
        /// are always allowed, so the owner can never lock themselves out. (Before 3.1.2, Enable, Disable, Debug and
        /// RequestPbShutdown ignored the switch, so a denied admin could still switch enforcement off.)
        /// </summary>
        private bool StaffCommandAllowed()
        {
            if (Config.AllowStaffCommands) return true;
            if (Context.Player == null || Context.Player.PromoteLevel >= MyPromoteLevel.Owner) return true;
            Context.Respond("Staff commands are disabled in the config.  Please enable them to use this command.");
            return false;
        }

        [Command("requestpbshutdown", "Send the shutdown request to all pb scripts that monitor for it.  add the amount of seconds until restart if desired.  Eg: !PB requestpbshutdown 10")]
        [Permission(MyPromoteLevel.Admin)]
        public void RequestPbShutdown(int seconds = 1)
        {
            if (!StaffCommandAllowed()) return;
            MySandboxGame.Static.Invoke(() => // Not sure if this actually needs to be forced on the game thread.
            {
                foreach (TrackedPlayer? tplayer in TrackingManager.PlayersTracked.Values)
                {
                    if (tplayer == null) continue;
                    foreach (TrackedPBBlock? pbBlock in tplayer.GetAllPBBlocks)
                    {
                        // One block failing (or already removed) must not stop the rest, nor throw on the game thread.
                        try
                        {
                            if (pbBlock is null || pbBlock.IsGone) continue;
                            pbBlock.ProgrammableBlock?.Run($"GracefulShutDown::{seconds}", UpdateType.Script);
                        }
                        catch (System.Exception e)
                        {
                            NLog.LogManager.GetCurrentClassLogger().Warn(e, "Could not send the shutdown request to a programmable block.");
                        }
                    }
                }
            }, "Advanced_PB_Limiter");
        }
        
        [Command("debug", "Only for debugging, not regular use for servers.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Debug(bool debug)
        {
            if (!StaffCommandAllowed()) return;
            Config.DebugReporting = debug;
            Context.Respond("Debug mode is now " + (debug ? "enabled." : "disabled."));
        }
        
        [Command("GetReport", "Generates a full report of this server.")]
        [Permission(MyPromoteLevel.Admin)]
        public void GenerateReport()
        {
            Task.Run(()=> ReportManager.GetReport(Context)); // Fire and forget on another thread.
        }
        
        private bool TryGetProgrammableBlock(long entityId, out Sandbox.Game.Entities.Blocks.MyProgrammableBlock? pb)
        {
            pb = null;
            if (!Sandbox.Game.Entities.MyEntities.TryGetEntityById(entityId, out VRage.Game.Entity.MyEntity entity)) return false;
            pb = entity as Sandbox.Game.Entities.Blocks.MyProgrammableBlock;
            return pb != null;
        }

        [Command("BlacklistScript", "Blocks the script that is in a programmable block (by its fingerprint).  Eg: !PB BlacklistScript 123456789 \"Auto Miner v3\"")]
        [Permission(MyPromoteLevel.Admin)]
        public void BlacklistScript(long blockEntityId, string name = "")
        {
            if (!StaffCommandAllowed()) return;

            if (!TryGetProgrammableBlock(blockEntityId, out var pb) || pb == null)
            {
                Context.Respond("No programmable block was found with that entity id.  The id is shown in the server log next to punishments.");
                return;
            }

            string? fingerprint = ScriptBlacklistManager.GetFingerprint(pb);
            if (fingerprint == null)
            {
                Context.Respond("That block has no script in it (or the script could not be read).");
                return;
            }

            BlacklistedScript entry = new()
            {
                Name = string.IsNullOrWhiteSpace(name) ? pb.CustomName.ToString() : name,
                Hash = fingerprint,
                Note = $"Added by {Context.Player?.DisplayName ?? "console"} from block {blockEntityId}"
            };

            ScriptBlacklistManager.AddEntry(entry, out string message);
            Context.Respond(message);
        }

        [Command("BlacklistSignature", "Blocks every script containing this text (survives script updates, min 8 characters).  Eg: !PB BlacklistSignature \"class AutoMinerV3\" \"Auto Miner v3\"")]
        [Permission(MyPromoteLevel.Admin)]
        public void BlacklistSignature(string signature, string name = "")
        {
            if (!StaffCommandAllowed()) return;

            BlacklistedScript entry = new()
            {
                Name = string.IsNullOrWhiteSpace(name) ? signature : name,
                Signature = signature,
                Note = $"Added by {Context.Player?.DisplayName ?? "console"}"
            };

            ScriptBlacklistManager.AddEntry(entry, out string message);
            Context.Respond(message);
        }

        [Command("UnblacklistScript", "Removes a script from the blacklist by its name.  Eg: !PB UnblacklistScript \"Auto Miner v3\"")]
        [Permission(MyPromoteLevel.Admin)]
        public void UnblacklistScript(string name)
        {
            if (!StaffCommandAllowed()) return;

            int removed = ScriptBlacklistManager.RemoveByName(name);
            Context.Respond(removed > 0 ? $"Removed {removed} entr{(removed == 1 ? "y" : "ies")} named \"{name}\"." : $"Nothing on the blacklist is named \"{name}\".");
        }

        [Command("ListBlacklist", "Lists the blacklisted scripts.")]
        [Permission(MyPromoteLevel.Admin)]
        public void ListBlacklist()
        {
            var entries = Config.ScriptBlacklist;
            if (entries.Count == 0)
            {
                Context.Respond("The script blacklist is empty.");
                return;
            }

            System.Text.StringBuilder sb = new();
            sb.AppendLine($"Blacklisted scripts ({entries.Count}){(Config.ScriptBlacklistEnabled ? "" : " - blocking is currently DISABLED")}:");
            foreach (BlacklistedScript entry in entries)
            {
                string hash = string.IsNullOrEmpty(entry.Hash) ? "" : $" hash {entry.Hash.Substring(0, System.Math.Min(12, entry.Hash.Length))}...";
                string signature = string.IsNullOrEmpty(entry.Signature) ? "" : " text match";
                sb.AppendLine($"- {entry.Name}:{hash}{signature}");
            }
            Context.Respond(sb.ToString());
        }

        [Command("RecentActions", "Shows when and why the plugin recently switched off, damaged or blocked programmable blocks.  Eg: !PB RecentActions 10")]
        [Permission(MyPromoteLevel.Admin)]
        public void RecentActions(int count = 10)
        {
            Context.Respond(BlockActionLog.Describe(count));
        }

        [Command("RestoreBlocks", "Switches back on the programmable blocks the plugin turned off since the server started.  Eg: !PB RestoreBlocks")]
        [Permission(MyPromoteLevel.Admin)]
        public void RestoreBlocks()
        {
            if (!StaffCommandAllowed()) return;

            CommandContext context = Context;
            BlockActionLog.RestoreAll((restored, skipped) =>
                context.Respond($"Switched back on {restored} programmable block(s). {skipped} were skipped (already on, removed, or could not be restored). Blocks that were damaged, blacklisted or unowned are not restored. Use !PB RecentActions to see why blocks were switched off. If a restored block does not respond, open its terminal and use Edit, then OK, to recompile it."));
        }

        [Command("TestBlacklist", "Explains why a programmable block is or is not being blocked by the script blacklist.  Eg: !PB TestBlacklist 123456789")]
        [Permission(MyPromoteLevel.Admin)]
        public void TestBlacklist(long blockEntityId)
        {
            if (!TryGetProgrammableBlock(blockEntityId, out var pb) || pb == null)
            {
                Context.Respond("No programmable block was found with that entity id.");
                return;
            }

            Context.Respond(ScriptBlacklistManager.Diagnose(pb));
        }

        [Command("TopScripts", "Lists the programmable blocks using the most server time, with block id and script fingerprint, so you can blacklist them.  Eg: !PB TopScripts 10")]
        [Permission(MyPromoteLevel.Admin)]
        public void TopScripts(int count = 10)
        {
            count = System.Math.Max(1, System.Math.Min(count, 25));

            var rows = new List<(string Player, TrackedPBBlock Block)>();
            foreach (TrackedPlayer player in TrackingManager.GetTrackedPlayerData())
            {
                List<TrackedPBBlock> blocks = player.GetAllPBBlocks;
                for (int i = 0; i < blocks.Count; i++)
                    if (blocks[i]?.ProgrammableBlock != null)
                        rows.Add((player.PlayerName ?? "Unknown", blocks[i]));
            }

            if (rows.Count == 0)
            {
                Context.Respond("No programmable blocks are being tracked yet.");
                return;
            }

            System.Text.StringBuilder sb = new();
            sb.AppendLine($"Top {System.Math.Min(count, rows.Count)} programmable blocks by server time used (load):");
            int rank = 1;
            foreach (var row in rows.OrderByDescending(r => r.Block.LoadMSPerSecond).ThenByDescending(r => r.Block.RunTimeMSAvgSoFar).Take(count))
            {
                var pb = row.Block.ProgrammableBlock!;
                string fingerprint = ScriptBlacklistManager.GetFingerprint(pb) ?? "no script";
                if (fingerprint.Length > 12) fingerprint = fingerprint.Substring(0, 12);
                sb.AppendLine($"{rank++}. {row.Player} | {row.Block.GridName} >> {pb.CustomName} | id {pb.EntityId} | load {row.Block.LoadMSPerSecond:0.000} ms/s | avg {row.Block.RunTimeMSAvgSoFar:0.0000} ms | {fingerprint}");
            }
            sb.AppendLine("Use !PB BlacklistScript <id> to block one of these scripts.");
            Context.Respond(sb.ToString());
        }

        [Command("ScriptFingerprint", "Shows the fingerprint of the script in a programmable block.  Eg: !PB ScriptFingerprint 123456789")]
        [Permission(MyPromoteLevel.Admin)]
        public void ScriptFingerprint(long blockEntityId)
        {
            if (!TryGetProgrammableBlock(blockEntityId, out var pb) || pb == null)
            {
                Context.Respond("No programmable block was found with that entity id.");
                return;
            }

            string? fingerprint = ScriptBlacklistManager.GetFingerprint(pb);
            Context.Respond(fingerprint == null ? "That block has no script in it (or the script could not be read)." : $"Script fingerprint: {fingerprint}");
        }

        [Command("SendOffenceReport", "Posts the offence report to the configured Discord webhook.  Eg: !PB SendOffenceReport")]
        [Permission(MyPromoteLevel.Admin)]
        public void SendOffenceReport()
        {
            if (!StaffCommandAllowed()) return;

            Context.Respond("Sending the offence report to Discord...");
            CommandContext context = Context;
            Task.Run(async () =>
            {
                (bool success, string message) = await DiscordManager.SendOffenceReportAsync();
                context.Respond(message);
            });
        }

        [Command("TestDiscordWebhook", "Sends a test message to the configured Discord webhook.")]
        [Permission(MyPromoteLevel.Admin)]
        public void TestDiscordWebhook()
        {
            CommandContext context = Context;
            Task.Run(async () =>
            {
                (bool success, string message) = await DiscordManager.SendTestAsync();
                context.Respond(message);
            });
        }

        [Command("Disable", "Disables the PB Enforcement Core.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Disable()
        {
            if (!StaffCommandAllowed()) return;
            Config.Enabled = false;
            Context.Respond("PB Enforcement Core is now disabled.");
        }
        
        [Command("Enable", "Enables the PB Enforcement Core.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Enable()
        {
            if (!StaffCommandAllowed()) return;
            Config.Enabled = true;
            Context.Respond("PB Enforcement Core is now enabled.");
        }
        
        [Command("AllowStaffCommands", "Allows staff commands to be used. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void AllowStaffCommands()
        {
            Config.AllowStaffCommands = true;
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Staff commands are now enabled.");
        }
        
        [Command("DenyStaffCommands", "Disables staff commands from being used. Only the server owner can use this command.")]
        [Permission(MyPromoteLevel.Owner)]
        public void DenyStaffCommands()
        {
            Config.AllowStaffCommands = false;
            Advanced_PB_Limiter.Instance!.Save();
            Context.Respond("Staff commands are now disabled.");
        }

        [Command("CreatePrivilegedUser", "Creates a new privileged user or overwrites an existing one.", "SteamId, MaxRunTimeMS, MaxRunTimeMSAvg, CombinedLimits, MaxAllBlocksCombinedRunTimeMS, MaxAllBlocksCombinedRunTimeMSAvg, OffencesBeforePunishment, Punishment, GracefulShutDownRequestDelay")]
        [Permission(MyPromoteLevel.Admin)]
        public void CreatePrivilegedUser(ulong steamId, double maxRunTimeMS, double maxRunTimeMSAvg, bool combinedLimits, double maxAllBlocksCombinedRunTimeMS, double maxAllBlocksCombinedRunTimeMSAvg, int offencesBeforePunishment, Enums.Punishment punishment, int gracefulShutDownRequestDelay)
        {
            if (!StaffCommandAllowed()) return;
            
            if (Config.PrivilegedPlayers.ContainsKey(steamId))
            {
                Config.PrivilegedPlayers[steamId].RuntimeAllowance = maxRunTimeMS;
                Config.PrivilegedPlayers[steamId].RuntimeAverageAllowance = maxRunTimeMSAvg;
                Config.PrivilegedPlayers[steamId].NoCombinedLimits = !combinedLimits;
                Config.PrivilegedPlayers[steamId].CombinedRuntimeAllowance = maxAllBlocksCombinedRunTimeMS;
                Config.PrivilegedPlayers[steamId].CombinedRuntimeAverageAllowance = maxAllBlocksCombinedRunTimeMSAvg;
                Config.PrivilegedPlayers[steamId].OffencesBeforePunishment = offencesBeforePunishment;
                Config.PrivilegedPlayers[steamId].Punishment = punishment;
                Config.PrivilegedPlayers[steamId].GracefulShutDownRequestDelay = gracefulShutDownRequestDelay;
                Context.Respond("Privileged user updated.");
            }
            else
            {
                Config.PrivilegedPlayers.Add(steamId, new PrivilegedPlayer
                {
                    SteamId = steamId,
                    RuntimeAllowance = maxRunTimeMS,
                    RuntimeAverageAllowance = maxRunTimeMSAvg,
                    NoCombinedLimits = !combinedLimits,
                    CombinedRuntimeAllowance = maxAllBlocksCombinedRunTimeMS,
                    CombinedRuntimeAverageAllowance = maxAllBlocksCombinedRunTimeMSAvg,
                    OffencesBeforePunishment = offencesBeforePunishment,
                    Punishment = punishment,
                    GracefulShutDownRequestDelay = gracefulShutDownRequestDelay,
                    StartupAllowance = Config.StartupAllowance, // was left at 0: no startup allowance at all for privileged players
                    DamageAmount = Config.PunishDamageAmount
                });
                Context.Respond("Privileged user created.");
            }

            Advanced_PB_Limiter.Instance!.Save();
        }

        [Command("DetailedAdminHelp", "Gives a list of all staff commands and their usage.")]
        [Permission(MyPromoteLevel.Admin)]
        public void DetailedHelp()
        {
            System.Text.StringBuilder sb = new();
            sb.AppendLine();
            sb.AppendLine("PB Enforcement Core Staff Commands (all start with !PB):");
            sb.AppendLine("!PB GetReport - Generates a full report of this server.");
            sb.AppendLine("!PB TopScripts [count] - Heaviest scripts with block id and fingerprint.");
            sb.AppendLine("!PB RecentActions [count] - Why the plugin recently switched off or blocked programmable blocks.");
            sb.AppendLine("!PB RestoreBlocks - Switches back on the blocks the plugin turned off.");
            sb.AppendLine("!PB BlacklistScript <block id> [\"name\"] - Blocks the script in a block.");
            sb.AppendLine("!PB BlacklistSignature \"<text>\" [\"name\"] - Blocks any script containing the text.");
            sb.AppendLine("!PB UnblacklistScript \"<name>\" - Removes a script from the blacklist.");
            sb.AppendLine("!PB ListBlacklist - Lists the blacklisted scripts.");
            sb.AppendLine("!PB TestBlacklist <block id> - Explains why a block is or is not blocked.");
            sb.AppendLine("!PB ScriptFingerprint <block id> - Shows the fingerprint of a block's script.");
            sb.AppendLine("!PB SendOffenceReport - Posts the offence report to Discord.");
            sb.AppendLine("!PB TestDiscordWebhook - Sends a test message to Discord.");
            sb.AppendLine("!PB RequestPbShutdown [seconds] - Asks scripts to shut down gracefully.");
            sb.AppendLine("!PB CreatePrivilegedUser <values> - Creates or overwrites a privileged user.");
            sb.AppendLine("!PB Disable / !PB Enable - Turns the PB Enforcement Core off or on.");
            sb.AppendLine("!PB AllowStaffCommands / !PB DenyStaffCommands - Server owner only.");
            sb.AppendLine("!PB Debug <true|false> - Debug logging.");
            Context.Respond(sb.ToString());
        }
        
        [Command("AdminHelp", "Gives a list of all staff commands.")]
        [Permission(MyPromoteLevel.Admin)]
        public void Help()
        {
            Context.Respond("PB Enforcement Core Staff Commands (all start with !PB): GetReport, TopScripts, RecentActions, RestoreBlocks, BlacklistScript, BlacklistSignature, UnblacklistScript, ListBlacklist, TestBlacklist, ScriptFingerprint, SendOffenceReport, TestDiscordWebhook, RequestPbShutdown, CreatePrivilegedUser, Disable, Enable, AllowStaffCommands, DenyStaffCommands, Debug, DetailedAdminHelp.");
        }
    }
}
