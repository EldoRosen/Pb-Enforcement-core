using System.Text;
using System.Threading.Tasks;
using Advanced_PB_Limiter.Manager;
using Advanced_PB_Limiter.Settings;
using Torch.Commands;
using Torch.Commands.Permissions;
using VRage.Game.ModAPI;

namespace Advanced_PB_Limiter.Commands
{
    [Category("PB")]
    public class PlayerCommands : CommandModule
    {
        private Advanced_PB_LimiterConfig Config => Advanced_PB_Limiter.Instance!.Config!;

        [Command("About", "Displays information about the PB Enforcement Core.")]
        [Permission(MyPromoteLevel.None)]
        public void About()
        {
            Context.Respond("PB Enforcement Core v" + Advanced_PB_Limiter.Instance!.VersionText + " - revamped by Eldo Rosen, The Destiny Universe. Based on the original code by SentorX.");
            Context.Respond("This is a plugin that limits the runtime and memory usage of programmable blocks to prevent server lag.");
            Context.Respond("You can also generate detailed reports of your server with this plugin.");
            Context.Respond("PB Enforcement Core is open source and can be found on GitHub: https://github.com/buttheadbob/Advanced_PB_Limiter");
            Context.Respond("For any issues, please contact SentorX through his Discord server.  DM's will be ignored unless you are a server owner or admin.");
            Context.Respond("Discord Invite: https://discord.gg/rSuxGrHrrt");
        }
        
        [Command("MyStats", "Generates a report about your programmable blocks for this server.")]
        [Permission(MyPromoteLevel.None)]
        public void GenerateReport()
        {
            if (Context.Player == null)
            {
                Context.Respond("This command can only be used by players in game.");
                return;
            }
            Task.Run(()=> ReportManager.PlayerStatsRequest(Context)); // Fire and forget on another thread.
        }
        
        [Command("Limits", "Shows all limits applied to programmable blocks for this server.")]
        [Permission(MyPromoteLevel.None)]
        public void ShowLimits()
        {
            StringBuilder sb = new();
            if (Context.Player == null || !Config.PrivilegedPlayers.ContainsKey(Context.Player.SteamUserId))
            {
                sb.AppendLine();
                sb.AppendLine("Server Limits");
                sb.AppendLine("--------------");
                sb.AppendLine("Max PB Runtime: " + Config.MaxRunTimeMS + "ms");
                sb.AppendLine("Max PB Runtime Average: " + Config.MaxRunTimeMSAvg + "ms");
                sb.AppendLine("Max Runtime Per Tick (All PB's Combined): " + Config.MaxAllBlocksCombinedRunTimeMS + "ms");
                sb.AppendLine("Max PB Runtime Average (All Pb's Combined): " + Config.MaxAllBlocksCombinedRunTimeMSAvg + "ms");
                sb.AppendLine("Max Offences Before Punishment: " + Config.MaxOffencesBeforePunishment);
                sb.AppendLine("Punishment Type: " + Config.Punishment);
                sb.AppendLine("Graceful Shutdown Period: " + (Config.SendGracefulShutdownRequest ? Config.GracefulShutDownRequestDelay + " seconds." : "none (punishments are immediate)"));
                Context.Respond(sb.ToString());
                return;
            }

            sb.AppendLine();
            sb.AppendLine("You are a privileged user.  Your limits are:");
            sb.AppendLine("-----------------------");
            sb.AppendLine("Max PB Runtime: " + Config.PrivilegedPlayers[Context.Player.SteamUserId].RuntimeAllowance + "ms");
            sb.AppendLine("Max PB Runtime Average: " + Config.PrivilegedPlayers[Context.Player.SteamUserId].RuntimeAverageAllowance + "ms");
            sb.AppendLine("Combined Limits Active: " + !Config.PrivilegedPlayers[Context.Player.SteamUserId].NoCombinedLimits);
            sb.AppendLine("Max Runtime Per Tick (All PB's Combined): " + Config.PrivilegedPlayers[Context.Player.SteamUserId].CombinedRuntimeAllowance + "ms");
            sb.AppendLine("Max PB Runtime Average (All Pb's Combined): " + Config.PrivilegedPlayers[Context.Player.SteamUserId].CombinedRuntimeAverageAllowance + "ms");
            sb.AppendLine("Max Offences Before Punishment: " + Config.PrivilegedPlayers[Context.Player.SteamUserId].OffencesBeforePunishment);
            sb.AppendLine("Punishment Type: " + Config.PrivilegedPlayers[Context.Player.SteamUserId].Punishment);
            sb.AppendLine("Graceful Shutdown Period: " + (Config.SendGracefulShutdownRequest ? Config.PrivilegedPlayers[Context.Player.SteamUserId].GracefulShutDownRequestDelay + " seconds." : "none (punishments are immediate)"));
            Context.Respond(sb.ToString());
        }

        [Command("Runtimes", "Shows the last saved runtimes for each programmable block owned by the player.")]
        [Permission(MyPromoteLevel.None)]
        public void Runtimes()
        {
            if (Context.Player == null)
            {
                Context.Respond("This command can only be used by players in game.");
                return;
            }
            Task.Run(()=> ReportManager.PlayerRuntimesRequest(Context)); // Fire and forget on another thread.
        }

        [Command("Help", "Shows player commands.")]
        [Permission(MyPromoteLevel.None)]
        public void PlayerHelp()
        {
            StringBuilder sb = new();
            sb.AppendLine();
            sb.AppendLine("PB Enforcement Core Player Commands");
            sb.AppendLine("------------------------------------");
            sb.AppendLine("!PB Limits - Shows your limits applied to programmable blocks for this server.");
            sb.AppendLine("!PB MyStats - Generates a report about your programmable blocks for this server.");
            sb.AppendLine("!PB Runtimes - Shows the last saved runtimes for each of your programmable blocks.");
            sb.AppendLine("");
            sb.AppendLine("!PB About - Displays information about the PB Enforcement Core.");
            Context.Respond(sb.ToString());
        }
    }
}