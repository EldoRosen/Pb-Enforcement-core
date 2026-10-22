# PB Enforcement Core: Manual

**Version 3.1.3.** Plugin file name: `PB Enforcement Core.zip`. Plugin revamped by Eldo Rosen, The Destiny Universe, based on the original code by SentorX.

A Torch plugin for Space Engineers dedicated servers. It watches how much server time every programmable block (PB) uses and steps in only when a script hurts the server.

---

## 1. Quick start (10 minutes)

1. **Install the plugin** (section 2; section 2.2 shows how to compile it yourself).
2. Start Torch, open the **Plugins** tab, tick **PB Enforcement Core**, restart if asked. A new plugin window appears with four tabs: **Main Settings**, **Privileged Users**, **Chat Responses**, **Reports**.
3. Open **Main Settings > Smart Enforcement** and check the starter values in section 13.
4. Add yourself and your admins in **Privileged Users** (section 7).
5. Open **Main Settings > Discord**, paste a channel webhook, press **Send test message** (section 9).
6. Tick **Enable** in Main Settings > Main Settings (or press **Turn on now** in the orange banner at the top of the window). **The first time this version starts, the plugin switches itself OFF** (once, even if you already had a config) so you can set everything up before it limits anything.
7. In game, run `!PB Help` as a player and `!PB AdminHelp` as an admin to check the commands work.

---

## 2. Installing and compiling

### 2.1 Installing the ready-made package

1. Stop the Torch server.
2. **Remove any older version** from the `Plugins` folder: the original Advanced PB Limiter, or an earlier zip of this plugin (for example `Advanced PB Limiter.zip`). Two versions cannot run together.
3. Copy `PB Enforcement Core.zip` into the `Plugins` folder next to `Torch.Server.exe`. Do not unzip it.
4. Start Torch and tick the plugin in the **Plugins** tab.

Updating works the same way: stop the server, replace the zip, start again. Your settings stay (they are in `PB_Enforcement.cfg`, not in the zip).

### 2.2 Compiling the plugin yourself

**What you need (once)**

| Item | Notes |
|---|---|
| Windows 10 / Server 2016 or newer | The plugin and the build both run on Windows |
| *Build Tools for Visual Studio 2022* | Free. In the installer tick the workload **.NET desktop build tools**. It includes the **.NET Framework 4.8 targeting pack** the project needs |
| A Torch server with the game files | Torch must have downloaded the dedicated server at least once, so the folder with `SpaceEngineersDedicated.exe` is filled |
| The project folder | The folder that contains `PB Enforcement Core.csproj` |

The NuGet packages the project uses are already in the project's `packages` folder, so no internet connection and no package restore are needed.

**Step 1: unpack the project.** Unzip it somewhere simple, not inside the Torch folder, for example the Desktop. After unzipping, the folder you work in is the one that holds `PB Enforcement Core.csproj`, `manifest.xml` and the `PB Enforcement Core` source folder:

```
C:\Users\Administrator\Desktop\PB_Enforcement_Core_v3_0_4\PB_Enforcement_Core
```

**Step 2: open PowerShell in that folder.**

```
cd "C:\Users\Administrator\Desktop\PB_Enforcement_Core_v3_0_4\PB_Enforcement_Core"
```

**Step 3: link the game and Torch files (once per copy of the project).** The project looks for the game and Torch DLLs in two folders next to the `.csproj`, named `GameBinaries` and `TorchBinaries`. They are folder links (junctions) to your real folders. The easy way is to double-click `Setup (run before opening plugin).bat` and answer its two questions:

1. The folder that contains `SpaceEngineersDedicated.exe`, usually `...\Torch\DedicatedServer64`.
2. The folder that contains `Torch.Server.exe`, usually `...\Torch`.

Or do the same by hand in PowerShell (change the paths to yours):

```
New-Item -ItemType Junction -Path GameBinaries -Target "C:\Path\To\Torch\DedicatedServer64"
New-Item -ItemType Junction -Path TorchBinaries -Target "C:\Path\To\Torch"
```

Check that both links work: `dir GameBinaries\Sandbox.Game.dll` and `dir TorchBinaries\Torch.dll` must both show a file. If either says "cannot find", the link points at the wrong folder.

**Step 4: build.** Run this in the same PowerShell window (it is one command; the leading `&` is needed because the path contains spaces):

```
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" "PB Enforcement Core.csproj" /p:Configuration=Release /p:Platform=x64
```

What the parts mean:

| Part | Meaning |
|---|---|
| `MSBuild.exe` | Microsoft's build program, installed with the Build Tools |
| `"PB Enforcement Core.csproj"` | The project to build (quotes because of the spaces) |
| `/p:Configuration=Release` | The optimised version for servers. Use `Debug` for a version with debug information |
| `/p:Platform=x64` | 64-bit, which the game and Torch need |

If you installed full Visual Studio instead of the Build Tools, the path differs, for example `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`.

Useful extras you can add to the end of the command:

| Add | Effect |
|---|---|
| `/t:Rebuild` | Rebuilds everything from scratch (use it after changing the junctions or when something looks stale) |
| `/v:minimal` | Much shorter output (the normal output is very long) |
| `> build.log` | Writes the output to a file you can send to someone |

**Step 5: check the result.** A good build ends with `Build succeeded.`, `0 Error(s)`. The plugin is then in two places:

- `bin\x64\Release\` holds the loose files.
- **`Build\PB Enforcement Core.zip` is the plugin package.** This is the file to install (section 2.1).

The build makes the zip by itself, with `manifest.xml` and the plugin DLL inside. A new build overwrites the old zip.

**Step 6: install and test.** Stop the server, copy `Build\PB Enforcement Core.zip` into the Torch `Plugins` folder, start Torch, and check the plugin window opens with its four tabs. Then follow the quick start in section 1.

### 2.3 Releasing a new version

When you change the code and want a new version number, change it in these places, then rebuild:

| File | What to change |
|---|---|
| `manifest.xml` | `<Version>v3.0.4</Version>` |
| `PB Enforcement Core\Properties\AssemblyInfo.cs` | `AssemblyVersion` and `AssemblyFileVersion` (for example `3.0.4.0`) |
| `README.md`, `MANUAL.md` | The version text at the top |
| `TODO.md` | Add a line describing the change |

The version shown by `!PB About`, in the Discord footer and in the Torch plugin list comes from these.

### 2.4 Build problems

| What you see | Cause and fix |
|---|---|
| `MSBuild.exe` is not recognised / path not found | The Build Tools are not installed, or are in another folder. Look in `C:\Program Files (x86)\Microsoft Visual Studio\2022\` and `C:\Program Files\Microsoft Visual Studio\2022\` for the `MSBuild\Current\Bin` folder |
| `The reference assemblies for .NETFramework,Version=v4.8 were not found` | Open the Visual Studio Installer, choose Modify on the Build Tools, and add the **.NET Framework 4.8 targeting pack** |
| `CS0006: Metadata file ... GameBinaries\...dll could not be found`, or many `CS0246 type or namespace not found` errors | `GameBinaries` or `TorchBinaries` is missing or points at the wrong folder. Redo step 3 and run the two `dir` checks |
| `error CS...` in a `.cs` file | A code error. The message names the file and line, for example `PunishmentManager.cs(247,73)`. That exact error (`CS1503 ... out Task to out ulong`) was in 3.0.2 and is fixed in 3.0.3 and later; if you see it, you are building an old copy |
| `error MC3000` or a message about a `.xaml` file | A window (XAML) error. The message names the file and line |
| `warning MSB3277` or a Torch version mismatch warning | Harmless if the build still succeeds. It means your Torch is a different version from the one the project was written for |
| Build works but the plugin does not load in Torch | Check the Torch log. A common cause is an older copy of the plugin still in `Plugins`, or a Torch/game update that changed something the plugin patches (the daily log then says which hook failed) |
| `The process cannot access the file ... Build\PB Enforcement Core.zip` | The zip is open in another program. Close it and build again |
| `Access to the path is denied` | Do not build inside a protected folder such as `Program Files`. Use the Desktop or another normal folder |
| Strange errors after changing folders or links | Delete the `bin`, `obj` and `Build` folders and build again, or add `/t:Rebuild` to the command |

If a Space Engineers or Torch update breaks the plugin, rebuild it against the new files first (the junctions always point at your current install, so just run step 4 again).

---

## 3. How it works

1. **Measuring.** Every time a PB runs, the plugin records how long it took (in milliseconds). Time spent in another PB that this one runs (TryRun) is charged to that PB, not this one. It ignores runs during a world save and the first 60 seconds after a block starts (the startup allowance). A run hit by a garbage collection still counts towards the average and load, but never on its own for a single-run offence or the instant kill.
2. **Offences.** A block gets an **offence** when one run is slower than **Max runtime** (default 0.8 ms) or its average over the last 50 runs is above **Max runtime average** (default 0.4 ms). A block can get at most one offence every 15 seconds. Each offence is forgotten after 1 minute, or after long enough for the allowed offences plus one to happen 15 seconds apart, whichever is longer.
3. **Warnings and punishment.** The owner is warned about each offence. After 3 offences, the next one triggers the punishment. The punishment is applied straight away: **Turn off**, **Damage**, or **Destroy** (damage the block to just below working integrity). With **Send shutdown request** ticked, the script is first run with `GracefulShutDown::<seconds>` and punished after the delay (default 10 seconds). A punished block starts with a clean record if it is switched back on.
4. **Instant kill.** Runs longer than **InstaKill Limit** (default 10 ms) **3 times within 10 seconds**, or a script over the memory limit (default about 10 MB) in two memory scans in a row, get the punishment type straight away with no warnings. Set a limit to 0 to switch it off.
5. **Combined limits.** Every 10 seconds the plugin checks each player's blocks together: their total server time **per tick** against **Max combined runtime** (default 3.0 ms), and the sum of their averages against **Max combined runtime average** (default 2.0 ms). Players under the 5 ms-per-second filter below are skipped. Each breach is one combined offence (at most one per check, 15 seconds apart) with a chat warning. After 3, the **heaviest** block is punished (or all of the player's blocks, if you tick that box); the player's combined offences are then cleared and they are left alone for 25 seconds.

### Smart enforcement (so players' scripts keep running)

These rules make the plugin leave harmless scripts alone:

- **Only when the server is struggling.** Limits apply only after sim speed has stayed below the threshold (default **0.9** for **10 seconds**). While the server runs well, nobody is limited. The instant-kill limits always apply.
- **Cost per second.** The plugin also measures how much server time a block uses per second. A script that is slow per run but runs rarely costs almost nothing, so run-time offences are ignored for blocks using under **5 ms per second** (set 0 to switch this off).
- **Throttle first (optional).** After an offence, the block's own periodic runs (Update1/10/100) can be limited to a few per second for a while. Buttons, timers, other scripts and shutdown requests are never throttled.

### Graceful shutdown (for script authors)

Before a punishment, the plugin runs the block with the argument `GracefulShutDown::<seconds>` (update type Script). A script can watch for it to save its state:

```csharp
if (argument.StartsWith("GracefulShutDown::")) { /* save data, stop motors, etc. */ }
```

Admins can send the same request to every script before a restart with `!PB RequestPbShutdown 30`.

---

## 4. Settings reference

Every change is saved automatically a moment after you make it (the Save buttons just save immediately). Defaults below are for a fresh install; a config you saved earlier keeps its old values.

### Main Settings tab > "Main Settings"

| Setting | Default | What it does |
|---|---|---|
| Enable | on | Switches the whole plugin on or off |
| Debug Logging | off | Extra detail in the log (large) |
| Turn off when owned by Nobody | on | Switches off PBs with no owner |
| Ignore NPC blocks | on | NPC-owned blocks are not tracked or limited |
| Auto Enable | off | Lets players' `Save()` turn their block on by itself |
| Auto Enable NPC | on | Same, for NPC-owned blocks |
| InstaKill Limit | 10 ms | Runs above this are punished at once (0 = off) |
| Instant kill runs | 3 within 10 s | How many runs over the InstaKill Limit, within how many seconds |
| Turn off on script error | off | Switch a block off when its script crashes (the game already stops the script) |
| No Recompile On Restart | off | Blocks that were off at restart need a Recompile, not Run, to start |
| Monitor Memory Usage | on | Estimates script memory every 5 s, without pausing scripts |
| Max Memory per PB | 10,240,000 bytes | Memory limit (0 = off) |
| Startup Allowance | 60 s | Run time is ignored for this long after a block starts |
| Defer Run options | off | Make blocks run less often (always, or below a sim speed). Only periodic runs; buttons, timers, other scripts and IGC always run. Needs a defer count of 2 or more |
| Disable Update1 | off | Runs Update1 scripts at Update10 speed |
| Destroy Protected PB | off | Blocks in a protected area (damage off) are stripped to unwelded instead of only switched off |

### Main Settings tab > "Player Settings"

| Setting | Default | What it does |
|---|---|---|
| Send shutdown request | off | Run the script with `GracefulShutDown::<seconds>` before punishing it |
| Graceful shutdown delay | 10 s | Wait before a punishment, only with Send shutdown request (0 = none) |
| Max runtime | 0.8 ms | Single run limit (0 = none) |
| Max runtime average | 0.4 ms | Average limit (0 = none) |
| Runs to track for averaging | 50 | How many runs make the average |
| Limit combined PBs | on | Adds up a player's blocks |
| Max combined runtime / average | 3.0 / 2.0 ms | Combined limits: ms per tick of total load / sum of averages (0 = none) |
| Punish all combined PBs | off | Off: only the heaviest block. On: all of the player's blocks |
| Allowed single block offences | 3 | Offences before punishment |
| Allowed combined block offences | 3 | Combined offences before punishment |
| Grace after offence | 15 s | Pause between offences on one block |
| Remove offence after time | 1 min | How long an offence counts |
| Clear data on PB recompile | on | Fresh start when a script is recompiled |
| Punishment type | Turn off | Turn off, Damage or Destroy |
| Damage points | 100 | Damage used by the Damage punishment |

### Main Settings tab > "Smart Enforcement"

| Setting | Default | What it does |
|---|---|---|
| Only enforce while the server is struggling | on | See section 3 (this setting lives only in this tab) |
| Struggling means sim speed below | 0.9, for 10 s | The threshold |
| Ignore scripts using less than | 5 ms per second | Cost filter (0 = off) |
| Slow a script down after an offence | off | The optional throttle |
| Throttle: runs per second / duration | 2 runs/s, 60 s | How hard and how long |

### Other tabs

**Discord** (section 9), **Script Blacklist** (section 8), **Logging** (section 11), **Shared Settings** (section 10). Chat messages are in their own top-level tab, **Chat Responses** (section 5).

---

## 5. Players: what they see

Players get a chat warning when a block collects an offence, a shutdown notice before a punishment, and a message when a block is punished, slowed down, or blocked.

**You can write these messages yourself** in the **Chat Responses** tab (it also holds the on/off switches for the warning, punishment and slowed-down messages): the offence warning, the punishment message and the "slowed down" message, plus the sender name shown in chat. Words in curly brackets are filled in automatically, and a grey example line under each box shows how the message will look. An empty box uses the default text, and each message can be switched off. The message for blacklisted scripts is in Main Settings > Script Blacklist.

| Message | Words you can use |
|---|---|
| Offence warning | `{player}` `{grid}` `{block}` `{offence}` `{allowed}` `{limitType}` `{limit}` `{usage}` |
| Punishment | `{player}` `{grid}` `{block}` `{reason}` `{action}` `{details}` |
| Slowed down | `{player}` `{grid}` `{block}` |
| Blacklisted script | `{block}` `{grid}` `{script}` |

Example offence warning: `Hey {player}, your script on {grid} used {usage}ms (limit {limit}ms). Warning {offence} of {allowed}.`

Players can use `!PB Limits`, `!PB MyStats` and `!PB Runtimes` to check their own usage.

---

## 6. Commands

Every command starts with `!PB`.

### Players

| Command | What it does |
|---|---|
| `!PB Help` | Lists the player commands |
| `!PB Limits` | Shows the limits that apply to you |
| `!PB MyStats` | Report on your programmable blocks |
| `!PB Runtimes` | Last run time of each of your blocks |
| `!PB About` | Version and credits |

### Staff (Admin or higher)

| Command | What it does |
|---|---|
| `!PB AdminHelp` / `!PB DetailedAdminHelp` | Lists the staff commands |
| `!PB GetReport` | Full report for the server |
| `!PB TopScripts [count]` | Heaviest scripts with owner, block id and short fingerprint |
| `!PB RecentActions [count]` | When and why the plugin switched off, damaged or blocked blocks |
| `!PB RestoreBlocks` | Switches back on blocks the plugin only turned off |
| `!PB BlacklistScript <block id> ["name"]` | Blocks the script in that block |
| `!PB BlacklistSignature "<text>" ["name"]` | Blocks any script containing the text (8+ characters) |
| `!PB UnblacklistScript "<name>"` | Removes a blacklist entry |
| `!PB ListBlacklist` | Shows the blacklist |
| `!PB TestBlacklist <block id>` | Explains why a block is or is not blocked |
| `!PB ScriptFingerprint <block id>` | Shows a block's script fingerprint |
| `!PB SendOffenceReport` | Posts the offence report to Discord |
| `!PB TestDiscordWebhook` | Sends a test message to Discord |
| `!PB RequestPbShutdown [seconds]` | Sends the graceful shutdown request to all tracked scripts |
| `!PB CreatePrivilegedUser ...` | Creates or updates a privileged user |
| `!PB Disable` / `!PB Enable` | Switches the plugin off or on |
| `!PB Debug true/false` | Debug logging |

### Server owner only

`!PB AllowStaffCommands` and `!PB DenyStaffCommands` switch the staff commands on or off.

---

## 7. Privileged users

Open the **Privileged Users** tab to give someone their own limits (admins, trusted builders, event scripts). Fill in the fields and press **Add/Update Privileged User**; select a row and press **Load Privileged Player Data** to edit it, or **Remove Privileged User**.

| Field | Meaning |
|---|---|
| Player name, SteamID | Who it applies to |
| Max Runtime / Max Avg Runtime Allowed | Their single and average limits (0 = no limit) |
| No Combined Limits | Skip the combined check for this player |
| Max Combined Runtime / Avg Allowed | Their combined limits |
| Startup Time | Their own startup allowance |
| Graceful Shutdown | Their shutdown delay |
| Offences Before Punishment | Default 6 |
| Punishment, Damage Amount | Their own punishment |

---

## 8. Script blacklist

Use it for scripts you never want on the server.

**How a script is matched**

- **Fingerprint:** a SHA-256 of the whole script. Matches one exact version.
- **Text signature:** a unique piece of text (8+ characters). Still matches after the author updates the script. Do not use common text such as `public Program()`, or every script will match.

**Blocking a script**

1. Run `!PB TopScripts` to find the heavy scripts and their block ids (the ids are also written to the log whenever a script is punished).
2. Run `!PB BlacklistScript <block id> "Script name"`.
3. Within about 10 seconds the block is switched off and its owner gets your message. Check with `!PB TestBlacklist <block id>`.

You can also edit the list in **Main Settings > Script Blacklist** (Name, Fingerprint, Signature, Note) and press **Apply and save**. The message to players can be changed there; `{block}`, `{grid}` and `{script}` are filled in. Untick **Block blacklisted scripts** to switch the whole feature off.

Blocked blocks are switched off, not destroyed, and keep being switched off if the player turns them back on.

---

## 9. Discord

1. In Discord: Channel settings > Integrations > Webhooks > New webhook > Copy webhook URL.
2. In **Main Settings > Discord**, paste the URL, then press **Send test message**.
3. **Full report:** the **Send offence report** button or `!PB SendOffenceReport` posts the worst offenders (players with at least the number of offences set in "Only list players with at least this many offences").
4. **Automatic alerts:** tick **Send automatic offence alerts** and set the trigger, for example *3 offences within 10 minutes*, a cooldown (default 30 minutes), and whether a punished block always raises an alert. Each alert shows the player, offence counts, punishments and the block ids, so you can act with `!PB BlacklistScript <id>`.

**How the messages look.** Discord does not allow a webhook to choose its own font, so the messages are styled with what Discord does allow: capital-letter headings (`PB ENFORCEMENT CORE // OFFENCE REPORT`), fixed-width blocks with aligned columns for the numbers, and the same colours as the plugin window (red for offenders, green for all clear, orange for automatic alerts, indigo for tests). Every message ends with the plugin name and version.

The webhook URL is kept private: it is not written to the log, and alerts cannot ping anyone.

---

## 10. Sharing settings between servers

If you run several servers, you can keep one set of settings. The settings live in a local SQLite database file (default `C:/PBLimiter/settings.db`, set in **Main Settings > Shared Settings**).

1. On the server whose settings you want to share: open **Main Settings > Shared Settings**, tick **Share settings between servers**, check the database file path, and press **Push now**.
2. On every other server: tick **Share settings between servers**, use the same database file, and press **Pull now** (or just restart it).
3. From then on, each server **pulls** the shared settings when it starts and whenever its settings window opens. With **Push every change to the database automatically** ticked, any change you make on one server is written to the database too, and the others pick it up the next time they start or open their settings window.

**What is shared:** all limits and options, the chat messages, the privileged players and the script blacklist.
**What is never shared** (it belongs to one server): **Enable**, the **Discord webhook**, the staff-commands switch, and the sharing settings themselves. So pulling can never switch a server on or off.

Good to know:
- The newest push wins. A pull replaces this server's shared settings with what is in the database.
- The database must be on a drive every server can reach on the same machine; do not put it on a network share.
- It uses the SQLite library that comes with Windows 10 and Windows Server 2016 or newer, so nothing else needs installing.
- The database is a normal SQLite file. Each setting is one row with the time and the server that last wrote it.

---

## 11. Logs

Plugin messages go to their own daily file: `Logs\PBEnforcementCore-yyyy-MM-dd.log`, in the `Logs` folder next to `Torch.Server.exe`. A new file starts each day. Routine messages are kept out of the main Torch log, and warnings and errors appear in both. Both options, and how many days to keep, are in **Main Settings > Logging**.

---

## 12. Troubleshooting

| Problem | What to do |
|---|---|
| Blocks went offline | Run `!PB RecentActions` to see why, then `!PB RestoreBlocks`. Common causes: blocks with no owner, a player over a combined limit, a blacklisted script, or the game stopping a faulty script |
| A block will not respond after being switched off | Open its terminal, press Edit, then OK, to recompile it |
| Buttons on a block do nothing | Run `!PB Disable`, try again, then `!PB Enable`. If it works only while disabled, send the "Error inside the" lines from the daily log |
| Blacklist does nothing | Run `!PB TestBlacklist <block id>`. "Cannot be read" means the game keeps the script somewhere unexpected; the log lists the places |
| Nothing is ever punished | Smart Enforcement only acts while the server is struggling. Lower the sim speed threshold, or untick the option, to test |
| The Reports tab / Live View is empty | Nothing is measured while the plugin is switched OFF (it starts off the first time): turn it on first. Then start the Live View with **Enable Live View** (it is always off after a restart). A block only appears once it has run, and drops off the list after 30 seconds without running. The line under the buttons says what the view is doing (plugin off, nothing measured yet, or how many players and blocks are shown). If the view stops updating, look for "The live view could not be refreshed" in the daily log |
| Settings did not stick | Changes save by themselves a moment after you make them. If one still does not stick, look for "Automatic save of the settings failed" in the daily log |
| Plugin does nothing after a fresh install | The first start of this version switches it OFF once. Tick **Enable** or press **Turn on now** |

---

## 13. Basic setup example

A good starting point for a public server of around 20 to 50 players. Open **Main Settings** and set:

| Where | Setting | Value |
|---|---|---|
| Smart Enforcement | Only enforce while the server is struggling | on, below 0.9 for 10 s |
| Smart Enforcement | Ignore scripts using less than | 5 ms per second |
| Smart Enforcement | Throttle after an offence | off at first, try on later |
| Player Settings | Punish all combined PBs | off |
| Player Settings | Max runtime / average | 0.8 ms / 0.4 ms |
| Player Settings | Combined runtime / average | 3.0 / 2.0 ms |
| Player Settings | Allowed offences (single / combined) | 3 / 3 |
| Player Settings | Punishment type | Turn off |
| Player Settings | Graceful shutdown delay | 10 s |
| Main Settings | InstaKill Limit | 10 ms |
| Main Settings | Max Memory per PB | 10,240,000 bytes |
| Main Settings | Startup Allowance | 60 s |
| Main Settings | Turn off when owned by Nobody | off if you use unowned PBs for events, on otherwise |
| Discord | Automatic alerts | on: 3 offences in 10 minutes, cooldown 30 minutes, always alert on punishment |
| Logging | Daily log file | on, keep 30 days |

Then:

1. **Privileged Users:** add each admin with *No Combined Limits* ticked and higher runtime limits.
2. **Test it:** put a deliberately heavy script in a PB. While sim speed is healthy nothing should happen. Run `!PB TopScripts` to find the block id, and `!PB Disable` / `!PB Enable` if you want to compare.
3. **Blacklist test:** run `!PB BlacklistScript <block id> "Test"`, wait about 10 seconds, check `!PB TestBlacklist <block id>`, then `!PB UnblacklistScript "Test"`.
4. **Tighten or loosen later:** if players complain, raise **Max runtime average** or the 5 ms-per-second filter. If lag continues, lower the limits or tick **Slow a script down after an offence**.

---

## 14. Quick answers

- **Will it turn off my players' scripts all the time?** No. With the starter setup, only scripts that cost real server time while the server is struggling are limited.
- **Can a player be warned without being punished?** Yes. Each offence gets a warning; punishment starts only after the allowed offences.
- **How do I undo a punishment?** The player turns the block back on (recompiling if needed). Admins can use `!PB RestoreBlocks` for blocks that were only turned off.
- **Does it work with Nexus?** No. All Nexus support was removed; the plugin works on a single server.
- **Where are the settings saved?** In `PB_Enforcement.cfg` in your Torch instance folder. If an older `PB_Enforcement_CoreConfig.cfg` or `Advanced_PB_LimiterConfig.cfg` is there, its settings are carried over the first time this version starts (the old file is kept as a backup).

---

*Plugin revamped by Eldo Rosen – The Destiny Universe, based on the legendary original code by SentorX.*
