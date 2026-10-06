# ADR-0005: The agent may update Claude Code (only when idle, never restarting a session)

**Status:** Accepted (06/10/2026). Companion of [ADR-0004](adr-0004-agent-self-update.md) (the agent updating itself).
**Decider:** the maintainer asked for "update Claude, restart it and run it again"; after the findings below they chose the
**update-only** form: Claude Code is updated, no session is ended or restarted.

## Context

Keeping Claude Code current across machines is useful, and an agent already runs on every machine. It is also risky: Claude
Code runs the people's sessions, and an update at the wrong moment, or a restart, loses unwritten work. The request had three
parts (update, restart, resume). A spike on the maintainer's Mac (06/10/2026) measured what each can be:

- `claude update` (alias `upgrade`) exists and takes no options: **there is no dry run**, every call is a real attempt. It was
  not run during the spike (it would have changed the maintainer's CLI).
- Claude Code writes `<config dir>/sessions/<pid>.json` for each running session: pid, session id, working folder,
  `entrypoint` (`claude-desktop` for the desktop app), `status` (`idle`, `busy`, `waiting`) and the epoch-millisecond time the
  status last changed. That makes "every session is idle" observable without a hook. The format is Claude Code's own and
  undocumented, so every doubt must read as "not idle".
- All four live sessions on that Mac were **desktop-app** sessions. The desktop app bundles its own Claude Code (2.1.288 there)
  and is updated by the app; the `claude` on the PATH (npm global, 2.1.285 there) is a different program that `claude update`
  changes. A desktop session cannot be re-attached after a restart.
- `claude --resume <id>` exists, but a background daemon cannot put a restarted session back into the terminal the person was
  using; the most it could do is open a new terminal window. That was judged not worth killing terminal sessions for.

## Decision

1. **The only action is the official command.** The agent runs `claude update` and nothing else touches Claude Code: it never
   downloads, replaces or deletes a Claude binary, and it never ends, restarts or resumes a session. Running sessions keep
   the version they started with until their owner restarts them.
2. **Two consents, default off.** The workspace setting `claudeUpdate` (admin, audited) **and** the machine's own
   (`cm-agent config claude-update on|off`, `CM_CLAUDE_UPDATE` wins) must both be on. Unread or unknown is off.
3. **Only installs that update themselves.** The `claude` found on the PATH is resolved to where it really lives and
   classified: an npm install (`node_modules/@anthropic-ai/claude-code`, or the Windows `claude.cmd` shim beside it) or a
   native installer install (`~/.local/share/claude`, `~/.claude/local`). The desktop app (`.app`, `WindowsApps`) and a
   package-manager install (`Caskroom`) are **never** touched; anything not positively recognised is left alone.
4. **Only when every session is idle.** Every live session (a file whose pid is running) must have `status` exactly `idle` for at
   least `ClaudeIdleFor` (10 min). A missing folder, an unreadable or odd file, an unknown status or a missing time means "not
   idle". No sessions at all counts as idle.
5. **A countdown, announced, cancellable.** Before `claude update` runs there is a countdown (`ClaudeCountdown`, 5 min). It is
   announced in agent.log, in `cm-agent status`, and as a desktop notification on macOS (no notifier was built or tested for
   Windows: there the log and `status` carry it). `cm-agent claude-update cancel` stops it and snoozes the attempt for a day.
   During the countdown consent and idleness are checked again every second; a session turning busy or the setting being
   switched off stands the attempt down.
6. **Rare and logged.** One attempt per `ClaudeUpdateEvery` (24 h; a failed one is retried after the update retry delay, a
   cancelled one after a day). The versions before and after (`claude --version`), the result and the reason go to agent.log,
   `claude-update-state.json` and `cm-agent status`.

## Consequences

- With everything at its default nothing changes: no process is started, no notification shown.
- Because `claude update` has no dry run, an enabled machine gets the countdown notice once a day even when Claude Code is
  already current.
- Not verified: the native-installer paths and the Windows shim (no such install on the maintainer's Mac), the Windows
  session-file location and format, and what `claude update` does on an npm install when run without a terminal.
- Restart-and-resume was considered and rejected for now (see Context). If it is wanted later it needs CLI-only sessions, a
  terminal to open, and an explicit per-machine switch of its own.
