# PB Enforcement Core (v3.1.4)  

## Programmable block runtime and memory limiter for Torch (originally Advanced PB Limiter, a full replacement for the PB Limiter by HaE).  

### What changed in 3.1.x
A large bug-fix release: blocks are now actually measured (they were stuck in their startup allowance), lag no longer inflates run times, the instant kill needs repeated slow runs, the combined check no longer cascades, the memory check no longer runs script code, and every live view button works. See [CHANGES-3.1.0.md](CHANGES-3.1.0.md), [CHANGES-3.1.1.md](CHANGES-3.1.1.md) and [CHANGES-3.1.2.md](CHANGES-3.1.2.md).

### Custom chat messages
The warning, punishment and "slowed down" messages players receive, and the chat sender name, can be written by you in the **Chat Responses** tab, with automatic words such as `{grid}` and `{limit}` and an example preview.

### First run and saving
The first time this version starts, the plugin switches itself **off** once so you can review the settings before it limits anything (an orange banner offers **Turn on now**). Every setting change, including privileged players, is saved to `PB_Enforcement.cfg` automatically a moment after it is made.

### Shared settings
Settings can be shared between servers through a local SQLite database (default `C:/PBLimiter/settings.db`, chosen in **Main Settings > Shared Settings**). Sharing is live: a change made on one server reaches all the others within a few seconds, and the Shared Settings tab shows which servers are connected. Enable, the Discord webhook and server name, and the sharing settings themselves are never shared.

### Manual
See [MANUAL.md](MANUAL.md) for installation, how the plugin works, every setting and command, and a basic setup example.

### Commands
Every command starts with `!PB`, for example `!PB Help` (players) or `!PB AdminHelp` (staff).

### Plugin log file
All plugin messages go to their own daily file, `Logs\PBEnforcementCore-yyyy-MM-dd.log` (next to Torch.Server.exe), and routine messages are kept out of the main Torch log; warnings and errors still show in both. Change this in **Main Settings > Logging**. Old files are deleted after 30 days by default.

### Why is a block off?
Every time the plugin switches off, damages or blocks a programmable block it records why (and writes a warning to the log). `!PB RecentActions` shows the latest ones; `!PB RestoreBlocks` switches back on the blocks that were only turned off.

### Smart enforcement
The limiter is built to leave players' scripts alone unless they hurt the server (**Main Settings > Smart Enforcement**):
- Limits only apply while sim speed has stayed below a threshold (default 0.9 for 10 seconds). Extreme usage (the instant-kill limits) is always stopped.
- Scripts are judged by how much server time they use per second, so a script that is slow per run but runs rarely is not punished.
- Optionally, a script that gets an offence is slowed down first (periodic runs only) instead of going straight to punishment.
- When a player is over a combined limit, their heaviest block is punished, not a random one.

### Script blacklist
Block known-bad scripts from running on your servers. Add them in **Main Settings > Script Blacklist**, or in game with `!PB BlacklistScript <block entity id>` (blocks that exact script by fingerprint) or `!PB BlacklistSignature "<unique text>"` (blocks any script containing that text, so it survives script updates). Blocked blocks are switched off and the owner receives a configurable message. Use `!PB ListBlacklist`, `!PB UnblacklistScript <name>` `!PB ScriptFingerprint <block id>`, `!PB TopScripts [count]` (the heaviest scripts with their block ids) and `!PB TestBlacklist <block id>` (explains why a block is or is not blocked) to manage and check the list.

### Discord offence reports
Open the **Discord** tab, paste a channel webhook, and use the buttons, or run `!PB SendOffenceReport`. `!PB TestDiscordWebhook` sends a test message. Set a **Server name** so you can tell servers apart when several post to the same channel. The same tab can also post **automatic alerts** when a player reaches a set number of offences within a time window (with a per-player cooldown, and an option to always alert when a block is punished). Reports are sent only on request.

### Features  
- Realtime time tracking only.
- Privileged Users with higher or lower limits.
- Limits per programming block and limits for all programming blocks owned by a user as a group.
- Overlimit groups can be shutdown entirely or a random programming block shutdown.
- Punishments include ( Damage, Destroy, and Turn Off ).
- Graceful Shutdowns

  ### Graceful Shutdowns
  This will send a run argument to the programming block.
  The argument is `GracefulShutDown::seconds` with seconds being the amount of time the script has to shutdown before any punishment is incurred.
  The punishment will always be applied after the time is up, regardless!
  Scripters can add a simple check with the following example:
```
if (updateSource == UpdateType.Script)
{
    // Parse the command and its arguments
    if (argument.StartsWith("GracefulShutDown::"))
    {
        var args = argument.Split(new[] { "::" }, StringSplitOptions.None);
        if (args.Length > 1)
        {
            int gracePeriodSeconds;
            if (int.TryParse(args[1], out gracePeriodSeconds))
            {
                // Now you have the gracePeriodSeconds parsed
                // You can implement your graceful shutdown logic here
            }
            else
            {
                // Handle the case where the seconds part is not a valid integer
            }
        }
    }
}
  ```

### TODO

See [TODO.md](TODO.md) for the full roadmap.
