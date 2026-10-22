# PB Enforcement Core 3.1.4 — Discord server name, new Discord cards, live shared settings

Compiled against the Torch and Space Engineers DLLs (all code except the WPF window, which only builds on Windows).
Install over any 3.x; settings carry over. **Every server that shares settings must run 3.1.4.**

## Discord

- **Server name** (Main Settings > Discord). Shown on every message, so servers that post to one channel can be told
  apart. Empty = the dedicated server's own name. Never shared between servers.
- Messages are now **components v2 cards**: a coloured edge, a heading with the server name, a line of key numbers, one
  section per player separated by dividers (Steam profile link, offences, worst blocks as a list, block ID on alerts),
  and a footer with a time each reader sees in their own time zone.
- The sender name is "*Sender name* · *Server name*", so the server also shows in notifications.
- When Discord refuses a message, its answer (what was wrong) is written to the log. The webhook URL is still never logged.
- `[`, `]`, `#`, `<` and `@` in player and grid names are escaped, so they cannot form links, headings or mentions.
- One message holds about 10 to 15 players (Discord's limit for cards); the footer says how many were left out.

## Shared settings are live

- The config implements **INotifyPropertyChanged by hand** (`Settings/ConfigChangeNotification.cs`) instead of
  inheriting Torch's `ViewModel`. Every real change raises one notification; setting the same value raises nothing.
- Every change (any box, tick, privileged player, blacklist entry, or a command that changes a setting) is sent to the
  database about a second later with a revision number. Every server checks for newer revisions every 3 seconds and
  applies the ones written by other servers.
- A value received from another server is never sent back, and a change made here that is not sent yet is never
  overwritten by an incoming one. If two servers change the same setting at the same moment, the last one written wins.
- Switching sharing on takes the values already in the database first, then adds only the settings it does not have.
- New switches: **Send changes made on this server straight away** and **Apply changes made on the other servers
  straight away** (both on).
- A live status box shows the connection, the servers connected right now (each reports in every 30 s) and the last
  change received.
- The script blacklist table refreshes when another server or a command changes the list.
- The database path box applies the path once you leave the box, not on every key.
- Databases from older versions get the revision column and the `servers` table added on their own.

## Build

- **Build.bat**: asks for the Torch folder once (remembered in `torch-path.txt`), makes the `GameBinaries` and
  `TorchBinaries` links itself, finds MSBuild, builds, and writes the output to `build.log`. `Build.bat change` asks
  for the folder again, `Build.bat debug` makes a Debug build.

## Test

- [ ] `!PB TestDiscordWebhook` with and without a Server name: the card shows the right server
- [ ] An offence report and an automatic alert render as cards
- [ ] Two servers on one database: change a limit on A, B shows it within about 5 s; the status box lists both servers
- [ ] Add a privileged player and a blacklist entry on B; A gets both
