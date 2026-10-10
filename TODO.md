# PB Enforcement Core: TODO

## Done in this revamp
- [x] Version 3.0.4 (bug check). Fixed: a block that was punished kept its old run times, so the combined check (every 10 seconds) found the same numbers again and punished the same block over and over (repeated chat messages, Discord alerts and damage); its run times are now cleared when it is punished. A privileged player's own punishment type and damage amount were stored and shown (!PB Limits, the window) but never used; they now apply. After a graceful shutdown delay the punishment type was dropped; it is now passed on. A punishment was counted for Discord alerts again every time the combined check asked during the delay; it is counted once. The combined check allocated its buffers inside the per-player loop (stack memory only comes back when the method ends), which could overflow the stack on a big server; moved into its own method. Changing a script blacklist from a command could freeze the command forever when the settings window was not open (it waited for a window thread that was not running); it now waits at most 2 seconds. Reports were keyed by Steam id, so all NPC or not-yet-known owners (Steam id 0) hid each other; they are keyed by identity now, and a block that was just removed no longer stops a whole report. A player's name and Steam id are looked up again if the game did not know them the first time (a Steam id of 0 hid privileged players). The player-cleanup timer did not start when the cleanup setting was 0 at startup, so raising it later did nothing until a restart. !PB RestoreBlocks missed a block that was punished again within a minute of the last restore. Version text no longer shows a double v (!PB About, Discord footer); !PB Limits for privileged players had a missing colon. Run-time statistics of a block are now changed under a lock (a punishment on another thread could leave the running sum off by one sample)
- [x] Version 3.0.3 (build fix). Compile error CS1503 in the graceful-shutdown delay: the lambda parameter was a single `_`, which C# treats as a real variable (type Task), so `TryRemove(entityId, out _)` no longer discarded its output; the parameter is renamed so `out _` is a true discard
- [x] Version 3.0.2 (live view and bug check). Live view: both tables now sit in a layout that scrolls by itself (before, the Per Player table was pushed off the window and rows below the edge could not be reached); rows update in place, so scroll position, selection, sorting and the Turn Off buttons no longer reset every refresh; the refresh runs on the window's own timer (no overlapping refreshes, no stacked timer handlers), pauses while the tab is hidden, and shows a status line (plugin off / nothing measured yet / players and blocks shown); one unreadable block no longer stops the whole refresh; refresh rate limited to 0.25-60 s and the first refresh happens at once; "Avg MS" shows the average so far instead of 0 until 50 runs are collected (limits still use the full average); "Combined Avg MS" is now the sum of the blocks' averages, the number the combined limit is checked against (it was a mean); players are listed by identity, so NPC owners (steam id 0) no longer hide each other
- [x] Bug check 3.0.2: the run-time queue stopped processing at the first unowned or NPC block (return instead of continue), so on a busy server data arrived late or the queue grew without limit; it now skips only that block, has an atomic start guard, a size cap and an error report. The graceful-shutdown mark was removed right after it was set, so a block could be punished again during the delay; it now stays until the punishment is done (errors in the delayed punishment are logged). A redundant offence check that let privileged players with a lower allowance off too easily was removed. The "No data to save" report timer typo (started twice) fixed. Block memory (profile data, defer counters) is cleared when a session ends
- [x] Full option check: every setting is wired to the right label and used in the code. Fixed: the punishment type drop-down (nothing was selected, it was 40 pixels wide and ignored changes made elsewhere, such as pulled shared settings), the missing "Ignore NPC blocks" option, a sim speed of 0 turning every run time into infinity with Use SimTime on, numbers typed with a comma in comma-decimal regions, three help texts that no longer matched, and a shared-settings call that .NET Framework does not have. The staff-commands switch is no longer shared between servers
- [x] Live view: the refresh took a fresh list of each player's blocks for every value, so a block dropping off mid-refresh made it fail silently (timers swallow errors); it now uses one snapshot and logs a failure. Starting the live view while the plugin is switched off offers to turn it on. Button colours fixed for the white window
- [x] Shared settings between servers through a local SQLite database (path set in the UI, default C:/PBLimiter/settings.db): pull on server start and when the settings window opens, push now / pull now buttons, optional automatic push on every change. Uses winsqlite3.dll from Windows (no extra files). Enable, the Discord webhook and the sharing settings are never shared
- [x] Settings screens: each setting now appears in exactly one place (sim speed enforcement only in Smart Enforcement, the warn-on-offence switch only in Chat Responses, punish-all-combined only in Player Settings). The "allowed combined block offences" box was wired to the single-block setting by mistake and now edits the combined setting
- [x] Save buttons removed (settings save automatically); Script Blacklist keeps "Apply changes" because its table is edited as a draft
- [x] First start switches the plugin off once, also for an existing config (new FirstRunCompleted setting)
- [x] First run: a new config starts with the plugin switched OFF (warning in the log, orange "Turn on now" banner in the window); existing configs keep their setting
- [x] Automatic saving: any settings change (INotifyPropertyChanged on the config, on the privileged players list, and a hand-written INotifyPropertyChanged on each privileged player) saves the config about 1.5 seconds later, and once more at shutdown
- [x] Discord messages restyled: capital-letter headings, fixed-width aligned blocks, plugin colours, name and version in the footer (Discord cannot change a webhook's font)
- [x] The plugin is now called PB Enforcement Core everywhere (Torch plugin name, chat sender, log names, the daily log file PBEnforcementCore-yyyy-MM-dd.log, Discord titles, commands, manual). Config file is now PB_Enforcement.cfg, with settings carried over from the older names
- [x] Chat messages moved to their own top-level "Chat Responses" tab (with a Save button)
- [x] Custom chat messages: offence warning, punishment and throttle messages and the chat sender name are editable (Main Settings > Messages) with {placeholders}, example previews and reset buttons; each can be switched off. Text boxes keep what you type (an empty one falls back to the default when sent), including the blacklist message
- [x] Version 3.0.1. Files renamed to "PB Enforcement Core": project, solution, source folder, plugin DLL and zip, and the three main code files. The config file is now PB_Enforcement_CoreConfig.cfg and settings from the old file are carried over automatically. The display name "PB Enforcement Core" and the internal code names are unchanged
- [x] Every game hook (run, compile, save) is wrapped so an error in the plugin can never break the programmable block buttons; the error is logged once per 30 seconds. Two possible crashes in the run hook were fixed, and the compile and save hooks now do nothing while the plugin is switched off
- [x] Block action record: every switch-off/damage/blacklist action is logged with its reason; new commands !PB RecentActions and !PB RestoreBlocks
- [x] "Switch off all of a player's blocks when over a combined limit" now defaults to off (heaviest block only) and has a tick box in Smart Enforcement
- [x] Bug review fixes: idle players are removed correctly (it looked them up by list position and failed after an hour), privileged players' "no combined limits" setting was read backwards, a limit of 0 now means "no limit" for the average and combined limits, the combined average used last-run times instead of averages, cleanup of old blocks crashed on a deleted block, timers are stopped and no longer stack up on a restart, unowned blocks were switched off from the wrong thread, a report error could lock the report generator until restart, the block list in reports was rebuilt on every access, a lost Discord alert race, numbers with a comma failed in the privileged users screen, time measurement no longer assumes a 10 MHz clock, chat messages to an unknown player can no longer go to everyone
- [x] Own daily plugin log file (Logs\PBEnforcementCore-yyyy-MM-dd.log), routine messages hidden from the main Torch log (warnings and errors still shown), old files deleted after 30 days; new "Logging" tab
- [x] Smart enforcement: soft limits only apply while the server is struggling (sim speed below 0.9 for 10 s by default), run-time offences ignore scripts using under 5 ms of server time per second, optional throttle before punishment, heaviest block punished for combined limits (it used to be a random one, and could never pick the last block)
- [x] New "Smart Enforcement" tab in Main Settings
- [x] Script blacklist made more robust: scripts are also checked every 10 seconds even if a block never runs, a block whose script could not be read is checked again next time instead of being remembered as clean, and a clear error lists the game's string members if the script text cannot be found
- [x] New commands !PB TestBlacklist <block id> (explains the result and shows run-check counters) and !PB TopScripts [count] (heaviest scripts with id and fingerprint)
- [x] Automatic Discord alerts: enable switch, offences-within-minutes criteria, per-player cooldown, always alert on punishment; alerts are batched every 20 seconds and include the block id for !PB BlacklistScript
- [x] Removed all Nexus code (V2/V3 connection, cross-server messages, config pushes, Nexus tab and buttons, related commands). The plugin now works on a single server
- [x] Fixed the report generator staying locked after a player with no data ran !PB MyStats
- [x] Rebrand all user-facing text to "PB Enforcement Core" (chat author, command replies, reports, log names, manifest name)
- [x] Light (white) theme for every settings tab: tabs, buttons, data grids, inputs
- [x] Shared button styles (default, primary, danger) replacing neon colours
- [x] Removed the unused Faction Settings tab and the Tiered Run Groups tab, with their config and data classes
- [x] Commands: !PB BlacklistScript <block id> [name], !PB BlacklistSignature "<text>" [name], !PB UnblacklistScript <name>, !PB ListBlacklist, !PB ScriptFingerprint <block id>
- [x] Punishment log lines now include the script fingerprint and block id so a punished script can be blacklisted in one command
- [x] Cross-server config push, privileged-user push, and cluster-wide reports moved to V3 mod messages
- [x] Chat commands: !PB SendOffenceReport [true] and !PB TestDiscordWebhook
- [x] Fixed cluster-wide reports dropping a player's PBs on other servers when the player also had PBs on this one

## Needs a build and in-game check
- [ ] 3.0.4: Let a player go over a combined limit with the default settings (punish the heaviest block only) and confirm the heaviest block is punished once, not again every 10 seconds
- [ ] 3.0.4: Give a privileged player a different punishment type than the server default, trigger an offence, and confirm their type is used
- [ ] 3.0.4: Run !PB About and check the Discord footer: the version should read 3.0.4 with a single v
- [ ] 3.0.2: Open Reports, press Enable Live View while a few programmable blocks run: both tables should scroll, rows should update without jumping, sorting by a column should hold, and the status line should show the player and block counts
- [ ] 3.0.2: Let a block get an offence with a graceful shutdown delay set and confirm it is punished once (one chat message, one log line), not once per run during the delay
- [ ] Build the plugin in Visual Studio and fix any XAML/compile errors
- [ ] Open every tab in Torch UI and check contrast, spacing, and column widths
- [ ] Create a test webhook, run !PB TestDiscordWebhook, then !PB SendOffenceReport and !PB SendOffenceReport true; check the embed looks right with many offenders
- [ ] Script blacklist: run !PB ScriptFingerprint on a test block; confirm it prints a fingerprint (if the log says the script text cannot be found, tell me your game version)
- [ ] Script blacklist: blacklist a test script, recompile it or turn it on, and confirm it is switched off and the player sees the message
- [ ] Script blacklist: test a text signature, and test !PB UnblacklistScript then re-enable the block
- [ ] Automatic alerts: tick the box in the Discord tab, set 2 offences in 5 minutes, trigger it with a heavy test script, and confirm one alert arrives and no repeat inside the cooldown
- [ ] Confirm punishment and warning chat messages show the new name in game
- [ ] Test PushConfig, PushPrivilegedUserData, and a cluster-wide GetReport across two or more servers
- [ ] Confirm the plugin still loads from existing configs (namespace, GUID, and config file name were left unchanged)

## Branding
- [ ] Decide whether to rename the config file (Advanced_PB_LimiterConfig.cfg) with a migration step
- [ ] Decide whether to rename namespaces, assembly name, and repo (breaking change for existing installs)
- [ ] Update the About command (author line, Discord invite, GitHub link)
- [ ] Add an icon and screenshots to the README

## UI
- [ ] Group settings into cards with section headings
- [ ] Add tooltips and short help text to every setting
- [ ] Inline validation for numeric fields
- [ ] Live dashboard: top offenders, current server load, recent punishments
- [ ] Search and filter in Reports and Privileged Users grids

## Features
- [ ] Only act when the server is actually struggling (turn on the existing sim-speed threshold by default) and score scripts by cost per second, not just single-run time
- [ ] Throttle heavy scripts before punishing them, and punish the heaviest block instead of a random one
- [ ] Optional allowlist of known-good scripts
- [ ] Show script fingerprints in the Discord offence report
- [ ] Extra chat commands (list offenders, add/remove privileged user, reload config)
- [ ] Reporting: scheduled report export and history
- [ ] Discord webhook alerts for offences and punishments

## Quality
- [ ] Unit tests for limit and offence calculations
- [ ] Config schema versioning and safe defaults
- [ ] Clean up logging (consistent names, debug levels)
- [ ] Release notes and version bump
