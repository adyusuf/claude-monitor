# ADR-0012: the name aipod, and the tray app with its status window

Status: accepted 19/10/2026 (the maintainer chose the order: a compatible rename first, then the tray). Builds on ADR-0002 (agent platform), ADR-0004 (self-update), ADR-0007 (plugin, inbox).

## Context

The agent has been called `cm-agent` and the product Claude Monitor. The maintainer wants one name, **aipod**, everywhere, and a small
program that shows the agent's state in the system tray on Windows and macOS: a tooltip with the status, a right-click menu with the
commands (reconnect, log in, disconnect, updates, ...), two lights (orange: a message arrived; green: a message left) that blink, a
status window, and the version with whether an update is waiting.

## Decision: the name

- Everything a person reads says **aipod**: usage lines, messages, log and status lines, set-up documents, the web app's product name and
  title. ADRs are decision records and keep their original words.
- **Old names keep working** (rule: additive only). The installed file is still `cm-agent[.exe]`, because the plugin's hooks, the
  services, the updater and the release zips use it; `aipod[.exe]` is made beside it at install and at every daemon start (a relative link,
  a copy on Windows). `AIPOD_*` variables win over `CM_*` ones (`CM_SERVER` is also read as `AIPOD_SERVER`); runs may set neither prefix.
  The daemon-kill guard accepts both process names.
- **Not changed in this step, on purpose** (each would orphan existing installs and needs its own migration): the plugin id and folder
  (`monitor-agent`) and the MCP tool names (`monitor_*`), the agent's home folder (`~/.claude-monitor`), the service, account, unit and
  launchd names and their install paths, the release file names and the update manifest format (`cm-agent-<os>-<arch>.zip`,
  `cm-agent-update/1`), the .NET namespaces and project names, and the wire string the web parses ("Messages sent to this session from
  Claude Monitor (the web):"). Flipping these is a later, separately tested step.

## Decision: the tray

- **Avalonia 12.1.4** (MIT, net10.0, Avalonia Team; released 09/10/2026, repository active; no known vulnerabilities by `dotnet list package
  --vulnerable`). Approved by the maintainer on 19/10/2026. Why: .NET has no cross-platform tray; Avalonia gives the tray icon, the menu
  and the window on both systems from one C# code base. Alternatives: two native programs (WinForms `NotifyIcon` and a Swift menu-bar app,
  no dependency but two code bases), Tauri (removed in ADR-0002). Cost: ~111 MB self-contained (uncompressed, like the agent); the
  `Avalonia.BuildServices` package may send anonymous build telemetry, switched off with `AVALONIA_TELEMETRY_OPTOUT=1` (CI and the
  scripts set it). Tests use `Avalonia.Headless` (same family, MIT); `Avalonia.Headless.XUnit` is not used because it needs xunit v3.
- `src/Aipod.Tray` builds `aipod-tray`. It **opens no network port**: it reads `agent.db`, `agent.json` and the update state, and it runs the
  `aipod` command line (`restart`, `connect --server`, `logout`, `update [--check]`) for what it is asked to do. One tray per person
  (`tray.lock` in the agent home). No dock icon on macOS.
- **The lights.** The daemon keeps two counters in `agent.db` (`leds.in`, `leds.out`; `LocalStore.Bump`). *In* goes up when a web command
  or an ask from another machine is received for the first time (a replay on reconnect does not count). *Out* goes up when a message is
  written into a session (inbox push, or a hook took it), when an ask is written to a session, when this machine sends an ask, and when
  a session answers one. The tray turns a counter that went up into a blink of 3 s (300 ms lit, 300 ms dark, starting lit); the first reading
  never blinks. Icon: two discs side by side, orange left, green right, dim when idle, drawn by the tray itself (a small PNG writer).
- **Start at login:** `aipod-tray --install-autostart` / `--remove-autostart` (a LaunchAgent on macOS, the user's Run key on Windows).
  `aipod-tray --status` prints what the window shows, without a screen.
- Coverage: the tray is a fourth codebase (`tests/Aipod.Tray.Tests`, measured separately by `scripts/coverage.sh`, threshold 80 %).

## Not done / not verified

- Windows: the tray, the Run-key autostart and the `aipod.exe` copy were not run on Windows (no Windows machine here). macOS: the icon, the
  blinking lights and the published single file were run; the menu and the tooltip were checked by tests, not by hand.
- Packaging: signing, notarization, an installer or an app bundle for the tray, and publishing it with the release pipeline are not set up.
- A service-mode install keeps its database in the service's home; the tray reads the current user's home, so it shows only what it can read.
- Flipping the names listed above; a machine-addressed web message when no session exists (see the ADR-0008 amendment).
