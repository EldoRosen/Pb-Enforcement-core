# PB Enforcement Core 3.1.1 — second bug check

A second, deeper review of 3.1.0 (including the files only skimmed the first time: Discord, SQLite, shared settings, the
other settings tabs and the commands). Like 3.1.0, this was written without the game DLLs, so it has not been compiled yet;
build it as described in MANUAL.md section 2.2. Install over 3.1.0 or 3.0.x; settings carry over.

## Fixed

| Severity | Where | Problem in 3.1.0 | Fix |
| --- | --- | --- | --- |
| High | `ProfilerPatch.cs` run stack | If a run never reached its end hook (another plugin cancelling it, an exception), a stale copy of that block stayed on the run stack. The next time the block ran it was charged its own time as "time in another block", so it read 0 ms for the rest of the session and could never be punished. | Stale entries are removed when a block starts a run, a block is never charged to itself, and its own time is worked out before anything else is changed. |
| High | Startup "kept off" protection | 3.1.0 only started noting blocks that were off once the session counted as loaded, but scripts are constructed while the world loads. Blocks that switched themselves on during loading (the exploit this protects against) were missed. | Noting starts when the world begins loading (`TorchSessionState.Loading`) and ends 40 s after it has loaded, as in 3.0. |
| Medium | `ProfilerPatch.cs` | A "skipped" flag left over from a cancelled run made the block's next real run report as clean and unmeasured, hiding a script error. | The flag is cleared at the start of every run. |
| Medium | `TrackingManager.cs` | Runs queued while the plugin was switched off were kept (up to 20,000) and judged when it was switched back on, so a block could be punished for old runs. | The queue is emptied while the plugin is off or the server is stopping. |
| Medium | `TrackingManager.cs` | A player could be created just after the session ended; their cleanup timer then ran into the next session. | Nothing is tracked once the session is ending. |
| Medium | `TrackedPlayer.cs` | With "Keep inactive pb duration" set very low, slow scripts were dropped between runs and got a fresh startup allowance every time, the original C1 bug in another form. | Blocks are kept at least 30 s after their last run, whatever the setting. |
| Medium | `!PB RequestPbShutdown`, script blacklist, `!PB RestoreBlocks` | Three game-thread actions had no error handling; one failing block could throw on the game thread. | Each is wrapped, and one block failing no longer stops the others. |
| Low | `GetSizeOfObject.cs` | An enormous array in a script could make the memory scanner's work list grow far past its budget before the budget was checked. | Pushing stops at the object budget; arrays of structs are only followed when one-dimensional. |

## Checked and found sound

Discord sending (webhook validation, rate limits, no mentions), the SQLite client and shared settings push/pull, the
Chat Responses, Privileged Users and Main Settings tabs, the blacklist editor, the plugin log file, and the auto-saver.

## Test (in addition to the 3.1.0 checklist)

- [ ] Switch a PB off, put `Me.Enabled = true;` in its constructor, restart the server: 40 s after loading it is off again and `!PB RecentActions` says "Kept off"
- [ ] A PB that runs another PB (`TryRun`) every tick: both show their own, non-zero run times in the live view
- [ ] Switch the plugin off for a few minutes with heavy scripts running, switch it back on: no offences appear straight away
