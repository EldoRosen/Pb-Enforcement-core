# PB Enforcement Core 3.1.0 — bug-fix release

Fixes every issue in the "PB Enforcement Core 3.0.41 — Bug Review & Fix Plan" doc. IDs below match that doc.

## Before you install

This build was **not compiled** when it was written (the game and Torch DLLs are not part of the source zip). Build it in
Visual Studio as usual. If the compiler complains, these are the game APIs that are new to this code and the likeliest cause:

| File | API | If it doesn't compile |
| --- | --- | --- |
| `Manager/PunishmentManager.cs` `IsProtected` | `MySessionComponentSafeZones.IsActionAllowed(MyEntity, MySafeZoneAction)` | Delete that `try` block; the safe-zone scan below it still works |
| `Manager/PunishmentManager.cs` `DamageBelowFunctional` | `MySlimBlock.Integrity`, `MaxIntegrity`, `BlockDefinition.CriticalIntegrityRatio` | Replace the body with the old `slim.DoDamage(slim.BuildIntegrity - 100, ...)` |
| `Utils/ChatUtils.cs` | `MySandboxGame.Static.UpdateThread` | Already used in `PB_Enforcement_Core.cs`, so it should be fine |

Existing config files load unchanged. "Use SimTime" and "Use Game Time" are still read but do nothing; the plugin logs one line about it at startup.

## Measurement

- **C1** Every run now marks the block active (`TrackedPBBlock.Touch`), including runs inside the startup allowance. Blocks are no longer deleted and re-created with a fresh allowance every 30–60 s, so normal players' blocks are measured and limited again. Cleanup also removes deleted blocks (`IsGone`).
- **C2** Run times are no longer divided by sim speed. The setting is retired (row replaced in the window).
- **H3** A run hit by a garbage collection still counts for averages and load; it is only excluded from the single-run and instant-kill checks. The script-error check now runs first.
- **M1** Run time is always the plugin's own stopwatch around the script call ("Use Game Time" retired), so there is no question of reading the previous run's value.
- **M2** Time a script spends running another PB (`TryRun`) is charged to that PB, not the caller.
- **M11** Per-block run state uses concurrent dictionaries.

## Punishment

- **C3** Instant kill needs 3 runs over the limit within 10 s (new settings *Instant kill runs*), and uses the configured punishment instead of always Destroy. Memory instant kill needs two scans in a row over the limit.
- **C4** Combined check rewritten: total load **per tick** against *Max combined runtime*, sum of enforced averages against *Max combined runtime average*, players under the min-load filter skipped, at most one offence per check spaced by *Grace after offence*, chat warning only (no more `GracefulShutDown::-3` runs on every block), offences cleared and a 25 s cooldown after a punishment.
- **H1** Replication members are now processed, every game-thread action is wrapped in try/catch, *Destroy Protected PB* only strips blocks that really are in a protected area (it used to strip every punished block), and the resync no longer stops at the first player without state.
- **H5** Chat messages are sent from the game thread.
- **M5** Offences are cleared when a block is punished.
- **M6** Offences are remembered for at least `(allowed + 1) × grace + 30 s`, so 4 offences 15 s apart always fit.
- **M9** The graceful shutdown request is opt-in (*Send shutdown request*, off). Without it, punishments are immediate.
- **M10** Destroy damages to just below working integrity (never a negative amount). Safe zones are checked with the game's own check plus the old scan.

## Hardening

- **H2** Memory scanner rewritten: fields only, never calls script code, no static fields (their static constructors are script code), own stack instead of recursion, visited set, 250,000-object and 100 ms budget. Scripts are no longer skipped while scanned.
- **H4** Deferral only touches purely periodic runs; buttons, timers, terminal runs, other scripts and IGC always run. *Disable Update1* runs Update1 scripts every 10 ticks. A defer count below 2 means off.
- **M3** "Off at startup" is only recorded during the first 40 s, and only blocks that switched themselves back on are switched off. Nothing is kept afterwards.
- **M4** Deleted blocks are dropped from tracking, the restore list and the memory list.
- **M7** A block whose owner changes moves, with its history, to the new owner.
- **M8** Switching a block off on a script error is a setting (*Turn off on script error*, off).
- **L1** Session start/stop is wrapped in try/catch; nothing is `async void` any more.
- **L2** An unowned block is queued to be switched off once, not on every run.
- **L3** Version 3.1.0 in the manifest and assembly.

## Live view (Reports tab)

- **LV2** Live sorting replaces the full refresh, so rows and buttons are no longer rebuilt twice a second.
- **LV3** Turn Off / Turn All Off use `Click`, Turn All Off asks first, both are recorded (`!PB RecentActions`, `!PB RestoreBlocks`), a **State** column shows On/Off and the button greys out when the block is off.
- **LV4** *Regenerate Data and Save To File* regenerates first, and both save buttons catch errors instead of risking Torch.
- **LV5** The save dialog opens in front of the plugin window with a default file name.
- **LV6** Report text rewritten: one line per row, correct hottest-block line, no NaN, works with the server stopped.
- **LV7** Columns: *Avg so far*, *Avg (enforced)*, *Load ms/s*, *Peak MS (since compile)*; the player table shows *Sum of last runs*, *Combined Avg (enforced)* and *Load ms/s*.
- **LV8** The live view stops and says so when the server stops; a new refresh rate applies on Enter or when the box loses focus.
- **LV9** A block being renamed mid-refresh keeps its row.

## Test before going live

- [ ] Build succeeds; the plugin loads and logs "Finished Patching!" with debug on
- [ ] A normal player's Update1 script at about 1.5 ms shows numbers in the live view after 60–90 s, and keeps its row
- [ ] The same script is warned 3 times and switched off on the 4th offence while sim is below 0.9
- [ ] Recorded run times do not change when sim drops
- [ ] One 15 ms spike does nothing; three within 10 s apply the configured punishment
- [ ] A player with 8 light Update100 scripts is not flagged by the combined check
- [ ] Damage and Destroy punishments inside and outside a no-damage safe zone: no errors in the log
- [ ] With deferral on, a button panel and a timer still run the script and IGC messages arrive
- [ ] Change a block's owner: offences follow the new owner
- [ ] Live view: sort by Last MS with live view on, click Turn Off: the block goes off first time and the row shows Off
- [ ] Live view: both save buttons, including with the report file open in another program
- [ ] One full 6-hour restart cycle with no plugin errors in the Torch log
