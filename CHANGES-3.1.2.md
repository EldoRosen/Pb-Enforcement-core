# PB Enforcement Core 3.1.2 — third bug check

A third review, aimed at what the first two did not cover: how each kind of script run is classified, who may use which
command, start-up threading, and the admin tools. Not compiled yet (no game DLLs here); build as in MANUAL.md section 2.2.
Install over any 3.x; settings carry over.

## Fixed

| Severity | Where | Problem | Fix |
| --- | --- | --- | --- |
| High | `ProfilerPatch.cs` (run classification) | A script that sets `Runtime.UpdateFrequency = UpdateFrequency.Once` on every run runs every tick, like Update1, but "Once" was treated as a trigger. Such scripts slipped past throttling, deferral and "Disable Update1" entirely. | "Once" now counts as a periodic run. When such a run is skipped it is requested again (the game clears the request before the run), so the script is slowed down, never stopped for good. |
| High | `StaffCommands.cs` | `!PB Disable`, `!PB Enable`, `!PB Debug` and `!PB RequestPbShutdown` ignored the owner's "Deny Staff Commands" switch, so an admin who had been denied could still switch enforcement off. | Every command that changes something now checks the switch. The server owner and the Torch console are always allowed, so the owner cannot lock themselves out. |
| Medium | `PB_Enforcement_Core.cs` (`UI_Dispatcher`) | Before the settings window had been opened, the plugin treated its start-up thread as the window's thread. Every blacklist change from a command (`!PB BlacklistScript`, shared-settings pulls) then waited 2 seconds for a thread that never answers. | It stays empty until the window exists, so changes apply at once. |
| Medium | `!PB CreatePrivilegedUser` | New privileged players got a startup allowance of 0 seconds (stricter than normal players) and the default damage amount was not copied. | They get the server's startup allowance and damage amount; edit them in the Privileged Users tab if needed. |
| Low | `!PB TopScripts` | Ranked by the enforced average, which stays 0 until a block has 50 runs, so most scripts showed 0. | Ranked by server time used (ms per second), with the average so far shown next to it. |
| Low | `ThrottleManager.cs` | Throttles of blocks that never ran again (deleted, switched off) were kept until the session ended. | Expired throttles are cleared once more than 500 are held. |

## Behaviour to know about

- With **Disable Update1** on, a one-off "Once" request can now wait up to 10 ticks (a sixth of a second) before it runs.
  Scripts that use Once for a single action are not otherwise affected.
- With **Deny Staff Commands**, admins can still use the read-only commands (reports, `TopScripts`, `RecentActions`,
  `ListBlacklist`, `TestBlacklist`, `ScriptFingerprint`, `TestDiscordWebhook`).

## Checked and found sound

Discord formatting (backticks and line breaks in player names are neutralised, mentions are disabled), the C# 9 support
file, nullable settings, chat message rendering, report generation and the player commands.

## Test (in addition to the earlier checklists)

- [ ] A script that sets `UpdateFrequency.Once` at the end of every run: with Disable Update1 on it runs about 6 times a second, and it keeps running
- [ ] `!PB DenyStaffCommands` as owner, then `!PB Disable` as an admin: refused. The same from the Torch console: works
- [ ] With the settings window never opened, `!PB BlacklistScript <id>` answers immediately

## 3.1.3

A regression check of every command and every live-view control against 3.0.41 found nothing that 3.1.x broke: every
button is wired to an existing handler, every table column binds to a real property, and every command still reaches the
same code. One display fix:

- `!PB GetReport`, `!PB MyStats` and the Discord offence report showed the enforced average, which is 0 until a block has
  run 50 times (83 s for an Update100 script), so most blocks read 0. They now show the average so far, like the live view.
