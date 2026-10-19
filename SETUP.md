# Setup

A clean machine is set up by following this file. The design is [ADR-0002](docs/adr-0002-agent-platform.md); each
phase that adds a codebase adds its tools, variables and secrets here in the same change.

## Prerequisites

| Tool | Why | Check |
|---|---|---|
| git | the repository and the gate | `git --version` |
| .NET 10 SDK | the API and the agent (build, tests, format) | `dotnet --version` |
| `dotnet-ef` 10 | creating migrations (`dotnet tool install -g dotnet-ef`) | `dotnet ef --version` |
| Docker | local PostgreSQL and Mailpit; the API's tests start their own PostgreSQL 18 | `docker info` |
| Python 3.9+ | the gate tools and their tests | `python3 --version` |
| gitleaks, ShellCheck, CodeQL CLI | the secret scan, and SAST | `gitleaks version`, `shellcheck --version`, `codeql version` |

## Install

```bash
git clone https://github.com/adyusuf/claude-monitor.git ~/ClaudeCode/claude-monitor
```

## Commit hooks

This repository is public, so a commit is checked before it exists: the `CLAUDE.md` size budget, a
`gitleaks` scan of the staged content, and a **real-project-name check** over the staged file names,
added lines and the commit message. Install once, in the main checkout (not in a linked worktree,
where `.git` is a file and the installer cannot write):

```bash
bash scripts/pre-commit.sh --install
```

The hooks are symlinks to `scripts/` of that checkout, so they run whatever branch the main checkout
has. The name check needs a local, git-ignored map of the names to look for. Point it at yours:

```bash
ln -s <your>/project-nicknames.tsv docs/project-nicknames.tsv   # or: export REAL_NAMES_MAP=<path>
```

Without a map the check prints "NOT RUN" and passes; `REAL_NAMES_STRICT=1` makes that a failure. The
format of the map is described in `scripts/real-name-check.sh`. A deliberate exception is
`git commit --no-verify`, and it is the maintainer's call.

## Development

```bash
docker compose -f deploy/dev-services.yml up -d          # PostgreSQL 18 on 127.0.0.1:55432, Mailpit on :1025 (UI :8025)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/ClaudeMonitor.Api -- --migrate   # apply migrations
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/ClaudeMonitor.Api                # the API
dotnet test                                                # all .NET tests (Docker must be running)
bash scripts/quick-check.sh [--tests]                      # the fast local look before a merge (not the gate)
bash scripts/merge-gate.sh dev                             # the whole gate locally, when you want it (CI runs it for you)
```

In Development every variable has a default (`.env.example`); sign-up mails land in Mailpit
(http://localhost:8025). A new migration: `dotnet ef migrations add <Name> -o Data/Migrations --project
src/ClaudeMonitor.Api`. Migrations only add (global #4); `scripts/backcompat_scan.py` blocks a drop, rename or
retype, an edited merged migration, a removed route and a removed or reordered contract field.

## CI

`.github/workflows/ci.yml` runs `bash scripts/gate-core.sh dev` on every push to `dev`, `test` and `prod`
and on every pull request: the same script and the same thresholds as a local `scripts/merge-gate.sh dev`,
never a second rule set. **CI owns the full gate** (this repository is public, so its minutes are free); locally only
`scripts/quick-check.sh` is the habit: the gate's own format, build, shellcheck and secret-scan commands, the script tests when
a script changed, and with `--tests` only the test projects the change reaches. It is a first look: a missing tool is
reported as SKIPPED and the run is not green. Before a merge the maintainer is asked whether CI and e2e will run; what is not
run is recorded on the merge commit as `Gate-Waived: <gates> (neden: <reason>)`. The helpers: `scripts/ci-status.sh <branch>
[commit] [--workflow ci|e2e]` (is the workflow green for that commit; only completed + success exits 0) and
`scripts/waived-gates.sh <source> <target>` (the waived gates between two refs, exit 1 when any). Details:
[docs/release.md](docs/release.md#promotion). Third-party actions are pinned to a commit SHA and gitleaks is
checksum-verified. To change a version, change it there and in the Prerequisites table above.

A gate run collects coverage while the unit tests run and the coverage step judges those files, so no test runs twice
(`scripts/merge-gate.conf`, `tests/coverage.props`, `web/vite.config.ts`); `bash scripts/coverage.sh` on its own still runs
the tests itself.

## The agent (aipod)

```bash
dotnet run --project src/ClaudeMonitor.Agent -- version                # from source
dotnet publish src/ClaudeMonitor.Agent -c Release -r osx-arm64 -o out/osx-arm64   # one self-contained binary (also osx-x64, win-x64, win-arm64, linux-x64, linux-arm64)
out/osx-arm64/cm-agent login --server http://localhost:5080            # shows a code; approve it on the web (/device)
out/osx-arm64/cm-agent install                                         # copies itself to the agent home and registers the Claude Code plugin
out/osx-arm64/cm-agent status
```

`install` registers a local plugin marketplace (`monitor-agent-local`) whose hooks and MCP server run the
installed binary, so Claude Code starts the agent with its sessions; nothing is registered with the OS. Without
the `claude` CLI on `PATH` it prints the two `claude plugin` commands to run. The agent's tokens are in the macOS
Keychain / Windows Credential Manager (service `claude-monitor-agent`); on Linux, which has no credential store, in
user-only (0600) `cred-*` files in the agent home. Its log is `agent.log` in the agent home.

A prompt sent from the web enters a session only when something is typed in it or a turn ends; an idle session takes
it at neither, so it stays "delivered" until it expires. `aipod install --stop-wait 120` makes a finished turn wait
up to 120 s (0-590, default 0) for a prompt from the web and start a new turn from it; the value is saved in
`agent.json` and the hooks are rewritten (hook timeout = wait + 15 s), so re-run `install` after changing it.
`aipod status` prints `commands waiting: N (oldest expires HH:mm)` and `stop wait: N s`. Reasons for the default of 0:
ADR-0002, "Commands from the web".

### Installing as a Claude Code plugin (ADR-0007)

The recommended way to get the agent onto a machine: nothing is downloaded or run by hand.

```bash
claude plugin install monitor-agent --marketplace <owner>/<repo>#<branch>    # the web's "Get the agent" page shows the exact command
```

Claude Code asks for the server address (the plugin's one option; it is the address you open in the browser). The next session
start installs the agent in the background (`plugin/monitor-agent/scripts/ensure.sh`: downloads `<server>/downloads/<zip>`, checks
it against the SHA-256 the plugin pins in `agent-release.txt`, puts it in the plugin's data directory); then, in Claude Code,
`/monitor-agent:connect` shows a code to approve on the web, `/monitor-agent:status` shows how the install and the connection
are doing, `/monitor-agent:disconnect` signs the machine out. The first session after installing reports nothing (the binary is
not there yet); the transcript is read later, so nothing of it is lost. **Uninstalling or disabling the plugin stops the daemon within
30 s**: Claude Code runs nothing at uninstall, so the daemon watches for its data directory to disappear, for the plugin to drop out of Claude Code's plugin list, and for a `false` in
`enabledPlugins` where the plugin is installed (everywhere it is installed: a plugin switched off in one project's settings only does not stop it). The agent home, the login and the credential store stay: installing the plugin again continues from them.
`claude plugin uninstall --keep-data` keeps the data directory, but the plugin leaves Claude Code's plugin list, which the daemon also reads (when that list has an
unknown shape it falls back to the data directory and the settings alone). The old local plugin (`aipod install`, `monitor-agent-local`) reports
every event a second time if it stays enabled: `/monitor-agent:connect` removes it, and the bootstrap says so while it is there.

The plugin is now called `aipod` (ADR-0013 section 1): `claude plugin marketplace add <site>/downloads/aipod-marketplace.json`, `claude plugin install aipod@aipod-releases`,
then `/aipod:connect` (which removes the old `monitor-agent` plugin; the skills are `/aipod:connect|status|disconnect`). The `monitor-agent` plugin stays as a bridge: it goes
quiet once `aipod` is enabled, and `aipod` goes quiet while a pre-bridge (0.5.0 or older) `monitor-agent` is still enabled, so no event is reported twice. Everything above applies to either name.

A maintainer renders the plugin's manifest, hooks and MCP file (for both plugins) from the one list of hooks in the agent with
`dotnet run --project src/ClaudeMonitor.Agent -- plugin-files .` and commits them (a test fails when they differ). `--stop-wait` works for a
plugin-started agent from the plugin version whose wrapper exports `CM_PLUGIN_STOP_WAIT_MAX` (its Stop hook then allows 600 s; an older version waits for nothing). Limits: the plugin has no channel entry (push through the
inbox needs none); Windows needs Git for Windows (Claude Code requires it) and has not been run (ADR-0007, "Risks").

### Pushing web messages into an idle session

On after the first `aipod install`, through the **inbox** (the maintainer's decision of 17/10/2026, after the channel proved late in the desktop app): nothing was saved about push yet, so the install saves `inbox`, which needs no start-up flag and works in the terminal, the desktop app and VS Code. The channel is the other choice (`aipod install --push channel`) and needs Claude Code started with the flag `install` prints (below); the desktop app and VS Code cannot pass it, so there a message sent through the channel only arrives at the next prompt. A machine that saved `channel` before (0.4.1) switches with `aipod config push inbox`. A later choice is kept as it is: `aipod install --push off` or `aipod config push off` turns it off and a reinstall does not turn it back on; `CM_PUSH` set in the environment is never overridden. Service agents have no sessions, so it changes nothing for them. `aipod config push inbox` (or `aipod install --push inbox`, or the plugin's *push* option set to `inbox`) makes a prompt
sent from the web start a turn in an idle session within about a second, **with no start-up flag**: every Claude Code session (terminal, desktop app, VS Code) has an *inbox socket*, its hooks learn
where it is (`CLAUDE_CODE_MESSAGING_SOCKET`), and the daemon writes the prompt there (ADR-0007). The text is wrapped, labelled as
coming from the web and capped; the session is told to treat it as data and to ask you before any request with side effects. A
session that refuses messages (`crossSessionInbound: refuse`) or has none is not an error: the prompt goes back to the hooks.
`aipod status` shows `Push into the session: on through the inbox socket`, whether this session's inbox is known and the last
failure. The older **channel** (`--push on`, also `--push channel`, below) stays exactly as it was: `on`, `CM_PUSH=on` and a push saved before the
inbox existed all still mean the channel. A prompt written to a session that never shows it in the transcript is **not written again**: after
10 minutes (`InboxConfirmWait`) it is left to the hooks.

**A short notice instead of the whole text (ADR-0007, A2), the default.** The inbox gets one line, `📩 claude-monitor web message <id> ...`,
that holds nothing the web wrote. `aipod config push-format full` (or `aipod install --push-format full`, or `CM_INBOX_FORMAT=full`) brings back the whole text. The session reads the message with the `monitor_inbox_read` tool (the same notice, wrapper and defused text as before, folded in the UI) and begins its reply with `💬 <message>`. A message nobody reads shows in `aipod status` as "not read yet". The text of an applied message is forgotten after 7 days.

### Answering a session's question from the web

A question a session puts to you can be answered on the session's page, in the conversation. Claude's own question tool (`AskUserQuestion`) and plan approval were already answerable there (the answer goes in through the permission hook).
The Claude desktop app's "next step" box (its `wrap_up` call: a recap and one question with buttons) is the app's own window and reaches nothing else, so the page now shows it as a card in the conversation: the question, its buttons and a box to type an answer.
Anyone who may command the session can answer the newest open question; the answer is sent as an ordinary message from the web (the same as a click in the app sends), by the transport the machine uses (`aipod status`). The card closes when the session hears from the user after it. A recap without a question stays an ordinary tool call. Nothing is added to the agent: the card is built from the transcript the agent already reports.

### What the session page shows

The conversation formats Markdown in everything it shows (Claude's replies and thoughts, what was typed, the next-step question): headings, **bold**, *italic*, links (http, https and mailto only), nested and task lists, quotes, tables and fenced code with its language. A message sent from the web appears in the conversation at once, with where it stands (queued, delivered or pushed, expired, cancelled; the same words as the history below) and a cancel button, until the session shows it as its own line; the box to type in sits under the conversation.
The side boxes follow what the machine reports: **Tasks** from `TodoWrite`, `TaskCreate` / `TaskUpdate` and the desktop app's own `update_plan` (the steps are `plan-0`, `plan-1`, ... and `wrap_up` checks off the running one); **Subagents** from the SubagentStart / SubagentStop hooks and, when those do not arrive, from the `Agent` (older: `Task`) call itself. Sessions recorded before this change keep the boxes they had; new events fill them.

The **Sessions** list is grouped by machine and, within a machine, by branch (sessions with no branch under "No branch reported"). The machines keep a fixed order (by name) so a card never moves; inside a machine the branches and the sessions are ordered by their most recent activity, newest first. While the mouse (or the keyboard focus) is on a machine's card, what it shows goes on updating but nothing in it changes place; a new session appears after the others until the pointer leaves, then the card sorts again.
In the conversation, a stretch of tool calls in a row, even a single call ("1 tool call"), is one closed line: how many calls and of which tools (`Bash ×3`, `Write ×2`, ...), and how many failed or are still running. Opening it lists every call, and each call opens to its input and output; a message, a question or a command sent from the web ends a stretch. A question Claude asked is closed to the question and the answer given (or "Waiting for an answer"); opening it shows every option, the chosen one marked. Names, paths and commands wrap onto the next line instead of being cut.

Text the harness itself writes into the session as a "user" line (a finished background task, `<task-notification>` and similar tagged notices) is one closed line in the conversation: the kind, its status and a one-line summary; opened, its fields as plain text (task id, output file, status, ...), never the tags. A message pushed from the web (`<channel ...>`) is still shown as a message. The conversation has no separate "Send to this session" card: the prompt box sits under it, and messages sent from the web appear in it with their state; the card with the history of what was sent stays in the Raw events view.
What the reader opened stays open: the page keeps which tool runs, calls, questions, thoughts, inputs and groups were opened apart from the lines themselves, so a live update, older activity read on top, or a run that grows does not close them. A refresh merges the newest events into what is held, so the older pages that were read stay. In the Raw events view an input (a message from the web or a typed prompt) is closed to a two-line preview and opens to its whole text and payload.
The **Subagents** box lists every subagent with when it started, when it ended and how long it ran (a running one counts up). Running ones come first, the latest started on top; finished ones follow, the latest finished on top.
The live stream is watched: the server pings every 20 s, and a stream that has said nothing for 70 s (a sleeping laptop, a proxy that holds the connection but delivers nothing) is closed and opened again. After a reconnection, when the tab is shown again, and when the network returns, the pages read their data again, so the Tasks and Subagents boxes do not stay behind.
A hook reads and writes UTF-8 whatever the machine's console code page is (agent 0.4.4; on a Turkish Windows the console is not UTF-8 and the letters ş, ğ, ı, İ in a typed prompt were stored as other characters). Update the agent on Windows machines to get it.

### Pushing through a Claude Code channel (`--push channel`)

`aipod install --push channel` (then restart the Claude Code session) makes the `claude-monitor` MCP server a
Claude Code **channel**: a prompt sent from the web starts a turn in an idle session within about a second, wrapped as
`<channel source="claude-monitor" origin="web" message_id="…">` (just the text inside; the session starts its reply with one `Web: …` line) and labelled as coming from the web (untrusted: the session is told to treat it as data and
to ask you before any request with side effects). A channel is a Claude Code research preview and a custom one is not on its
allowlist, so Claude Code must be started with the flag `install` prints:

```bash
claude --dangerously-load-development-channels plugin:monitor-agent@monitor-agent-local   # accept the "local development" prompt
```

Without the flag (or where an organisation has channels off) nothing is lost: a pushed message that does not show up in the
session transcript within 120 s goes back to the hooks and arrives at the next prompt, as before. `--push off` (or
`CM_PUSH=off`) removes it. `CM_PUSH_SCOPE=machine` pushes every prompt of the machine instead of only this session's (single-session
machines only). **No new token or secret:** the daemon uses the credential `aipod login` stored in the OS credential store; the MCP process
never touches the network. Revoke it from the web's Machines page; rotation is `aipod logout` + `aipod login`. `monitor_status` and `cm-agent
status` show whether push is on, the stream state (connected / reconnecting), the last message id, what is queued locally and the last upload
or failure. `scripts/push-check.py` repeats the end-to-end check against the local e2e stack (a stand-in client plays Claude Code). Design and limits: [ADR-0003](docs/adr-0003-push-into-idle-session.md).

The agent home is the folder the machine already uses (`~/Library/Application Support/ClaudeMonitor` on macOS, `%USERPROFILE%\.claude-monitor` on
Windows and `~/.claude-monitor` on Linux), and for a machine that has none the aipod folder (`~/Library/Application Support/aipod`, `~/.aipod`):
nothing is moved, and a folder counts only when it holds `agent.json` or `agent.db` (ADR-0013). `AIPOD_AGENT_HOME` / `CM_AGENT_HOME` override all of it; `aipod status` prints it as `home:`. It is deliberately **not** under
`%LOCALAPPDATA%`: the Claude desktop app is a packaged (MSIX) app, and everything it starts sees `%LOCALAPPDATA%`
redirected to a private copy, so a login made in a normal terminal would be invisible to its sessions
(ADR-0002, "Home outside AppData").

**Upgrading a Windows machine** that used the old default (`%LOCALAPPDATA%\ClaudeMonitor`): the first start of the new
`aipod` copies `agent.json` to the new home (no new login; `agent.db` is not moved, so events not yet uploaded are
lost). The old plugin keeps running the old binary until you run `aipod install` again with the new one; do that
from a normal terminal, then check that `aipod status` and `monitor_status` in a new Claude session show the same
`home:`. The old folder is left in place and can be deleted afterwards.

### Machine roles, the tool inventory and the session-start block (ADR-0008)

- **Role.** A workspace admin sets each machine's **environment** (dev, test, stage, preprod, production, other) and **function**
  (development, application, web, database, ci, storage, other) on the machine's page; "not set" never means test. Lowering a machine
  away from production or database asks for a recent sign-in. The machine may say what it is (`aipod config role <environment|none>
  <function|none>`; a service agent: `environment` and `function` in its admin-owned `exec.json`); a claim only makes the role stricter.
- **What the agent reports.** Whether Claude Code is installed and how many sessions are idle, and a tool inventory (git, dotnet, node,
  npm, python3, pwsh, docker, psql, java, claude and the shell interpreters) probed by absolute path, each marked trusted or not; the
  agent runs `--version` only for a trusted or self-owned binary. `CM_PRESENCE_PROBE_EVERY` sets the probe interval.
- **The block every session gets.** The plugin's `SessionStart` hooks now include a synchronous `hook SessionStart --context` entry: it
  prints the workspace's machines (role, online, Claude, tools) with the rules for treating them, from a cache the daemon refreshes every
  `CM_MACHINE_CACHE_EVERY` seconds. It is empty while remote runs are off for the workspace or this is the only machine;
  `CM_MACHINES_CONTEXT=off` turns it off on one machine. The hook reads the local database only and always exits 0.
- **Tags and the note.** A workspace admin sets a machine's tags (`key=value`, at most ten) and a runbook note on its page; both are untrusted
  text for Claude, which gets the tags as labels in the machines list and the note only through `monitor_machine`, marked as written by an admin.
- **Listening services (off by default).** The machine's owner turns on "Report listening ports" on its page (a recent sign-in or a code is
  needed). The agent then reads the TCP ports it listens on, with the process name where the system shows it (on Linux for the account's own
  processes), and sends them; only the owner and admins see them. Turning it off drops what was stored. Nothing is started to read them.
- **A machine without Claude Code (standalone agent).** `aipod install --standalone --server https://<your Claude Monitor address>` (run as your own user, never
  as root or administrator) logs in first (approve the code on the web), copies the binary into the agent home and then starts the agent at your login: a
  LaunchAgent that runs an application bundle on macOS (no window, no Dock icon), a logon task that runs a windowless copy of the program on Windows, a
  `systemd --user` unit on Linux (without `loginctl enable-linger` it runs only while you are logged in; the installer says so). `uninstall --standalone`
  removes the autostart and keeps the data. The daemon of such an agent does not exit when it has no login: it waits and reads its identity again every 30
  seconds. It reports `claude=none`; another machine's ask to it is refused (`no_claude`), but it is a normal target for runs. From it, `aipod machines`,
  `machine <x>`, `metrics <x>`, `alerts`, `jobs`, `job-run <id>`, `run --machine <x> (--script <file> --interpreter <i> | --argv ...)`, `result <id> --wait 30` and
  `ask --machine <x> "<question>"` reach the other machines without the web; an approval is still the owner's, on the web.
- **Asks across your machines.** A Claude session can ask a Claude session on ANOTHER machine of the same user a question (`monitor_ask`, then
  `monitor_ask_result`; the other session answers with `monitor_answer`). It works only when: the push through the inbox is on on the target (`cm-agent
  install --push inbox`), its owner turned "Allow asks" on its machine page (a recent sign-in is needed), an admin set its environment, it is not a
  production or database machine, a session there has been idle for 30 seconds and the workspace allows remote runs. An ask goes to one idle session
  (the one working in the same project first); nothing is queued for later. On macOS and Linux the message is written without the session's key, so Claude
  Code holds it for the person at that terminal; on Windows it goes only to a session in default or plan mode. A machine asked a moment ago may not ask on,
  a machine that was asked may not ask back, and an agent is limited to 30 asks an hour (5 per session in 10 minutes). `CM_ASKS=off` turns asks off on one
  machine. The question and the answer are masked, never logged, and deleted after the retention period.
- **Script runs.** `monitor_run_script` asks a machine to run a script (sh, bash, python or powershell). It needs exec level `shell` on the target
  without a ceiling, an environment an admin set on the machine's page (a machine's own claim does not count) and a trusted interpreter in its
  inventory. The owner approves the exact text on the web (every line numbered, non-ASCII made visible, Allow only after the script was
  scrolled to its end and three seconds later); a production or database machine also needs the approver's two-step code, and an approver
  without two-step sign-in cannot approve there. A script never matches a grant or a job. A script run may also carry up to 8 small text files (64 KiB each, 256 KiB together, relative paths of letters, digits, dot, underscore and dash, at most 4 levels) that are written next to it, and a text for its standard input (64 KiB); the owner reads each one on the approval card (Allow waits until every box was read to its end) and the approval covers them all. `aipod run --script main.py --interpreter python --file helper.py=./helper.py --stdin ./input.txt` and `monitor_run_script` send them. A Python script with files runs with `-E -s -B` (its own folder on the module path) instead of `-I -B`. Binary files and archives are not supported (ADR-0010). The agent writes it to `<home>/runs/<run>/script.<ext>`
  (private, never executable), starts it with the interpreter's safe flags and the scratch folder as working directory, with an empty
  environment and `PATH=/usr/bin:/bin:/usr/sbin:/sbin` (set your own PATH inside the script for a user-installed tool), and removes the folder
  afterwards. The workspace's "Output limit for script runs" (1 to 64 MB, default 8) is frozen on each run; `CM_EXEC_SCRIPT_OUTPUT_CEILING`
  caps it on one machine. `monitor_run_result` says "output complete" or exactly what is missing and why. On a production or database
  machine a shell command, an argv run of an interpreter, and every grant or job approval need the approver's two-step code too (a recent
  sign-in does not replace it), and an approver without two-step sign-in cannot approve there; a plain argv run of another program does not
  (ADR-0008 §9.13).
- **Projects.** Every project a session works in is listed on the machine's page. For a machine without sessions, or for a folder no session
  visits, list the folder: `aipod config projects add <folder>` (interactive or standalone agent) or `"projects": ["/srv/shop"]` in the
  admin-owned `exec.json` (service agent). The agent reads the branch, the commit and the tag from the `.git` files without running git and
  sends them with an HMAC of the path (the key is generated here and kept in the credential store; the path never leaves the machine).
  `CM_PROJECT_FACTS_EVERY` sets how often they are read again. A run may name a `project` and a `kind` (`test`, `build`, `deploy`, `other`);
  its result, and for a `test` run the passed, failed and skipped counts of dotnet test, vitest, jest and pytest, appear under that project.

## The tray app (aipod-tray)

`aipod-tray` puts the agent's state in the system tray (Windows and macOS; ADR-0012): the tooltip shows the version, the state and whether an
update is waiting; the right-click menu has Reconnect, Log in, Disconnect, Check for updates, Update, Open the web app and Show the log; the
status window (menu: "Open the status window") shows the same with the agent's own status lines and buttons. Two lights sit side by side in the
icon: orange blinks when a message arrives, green when one leaves or reaches a session.

```bash
dotnet build src/Aipod.Tray                    # to try it: dotnet src/Aipod.Tray/bin/Debug/net10.0/aipod-tray.dll
AVALONIA_TELEMETRY_OPTOUT=1 dotnet publish src/Aipod.Tray -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out
aipod-tray --status               # prints what the window shows, no screen needed
aipod-tray --install-autostart    # start at login (macOS LaunchAgent, Windows Run key); --remove-autostart undoes it
```

It reads the agent's files in the same home as the agent (`CM_AGENT_HOME` / `AIPOD_AGENT_HOME`) and runs the `aipod` command line for
the commands; it opens no port. Not covered yet: signing and notarization, an installer, a service-mode install's database.

## Service agent (boot service, remote work)

`aipod install --service` registers the daemon to start at boot with no Claude session, so other machines of a
workspace can be reached (ADR-0005). It is opt-in and needs root / an elevated prompt; the interactive agent above is
unchanged. The step-by-step runbook is [docs/remote-work-setup.md](docs/remote-work-setup.md). What the install lays
down, per OS:

| | Linux (systemd) | macOS (LaunchDaemon) | Windows Server (service) |
|---|---|---|---|
| Service account | `aipod` (system, no login shell) | `_cmagent` (hidden role account) | `NT SERVICE\cm-agent` (virtual) |
| Binary (admin-owned) | `/usr/local/lib/cm-agent/cm-agent` | `/Library/Application Support/ClaudeMonitor/bin/cm-agent` | `%ProgramFiles%\ClaudeMonitor\cm-agent.exe` |
| Service home (account only) | `/var/lib/cm-agent` | `/Library/Application Support/ClaudeMonitor/home` | `%ProgramData%\ClaudeMonitor\service` |
| Exec policy file (admin-owned) | `/etc/cm-agent/exec.json` | `/Library/Application Support/ClaudeMonitor/exec.json` | `%ProgramFiles%\ClaudeMonitor\exec.json` |
| Unit / definition | `/etc/systemd/system/cm-agent.service` | `/Library/LaunchDaemons/com.claudemonitor.agent.plist` | service `aipod` (`sc query aipod`) |
| Login code (while not connected) | `<home>/login-code.txt`, `journalctl -u aipod` | `<home>/login-code.txt` | `<home>\login-code.txt` |

**The names (ADR-0013, tier 2).** The table above is what services installed before the rename have. A fresh `install --service` now uses the aipod names: service and unit `aipod` (`/etc/systemd/system/aipod.service`), accounts `aipod` / `_aipod` / `NT SERVICE\aipod`,
launchd label `aipod.agent` (`/Library/LaunchDaemons/aipod.agent.plist`), folders `/usr/local/lib/aipod`, `/var/lib/aipod`, `/Library/Application Support/aipod/{bin,home}`, `%ProgramFiles%\aipod`, `%ProgramData%\aipod\service`, the exec policy in
`/etc/aipod/exec.json`, `/Library/Application Support/aipod/exec.json`, `%ProgramFiles%\aipod\exec.json`, and the sudoers file `aipod-run`; the binary file inside keeps the name `cm-agent`. A service that already exists keeps its old names when it is
upgraded with `install --service`, and `sudo aipod service migrate` moves it: `--dry-run` shows the plan; the real run stops the old service, copies its home and policy, installs the new service, waits up to 90 s for it to reach the server (Linux, macOS) and only then **disables** the old
one, which stays for 7 days; `--undo` goes back (the data the new service kept up comes back too), `--finish` removes the old service, account, folders, policy and sudoers file (before 7 days only with `--now`). A failure before the disable puts everything back and starts the old service. On
**Windows** the tokens are encrypted for the old service account and the new account cannot read them: the machine logs in again and an administrator approves a code. The agent reads the exec policy from the new place first and the old one second.

The service units set `CM_SERVICE=1`, `CM_AGENT_HOME=<service home>` and the single-file extraction folder
(`DOTNET_BUNDLE_EXTRACT_BASE_DIR=<home>/.net`); nobody sets them by hand. `aipod uninstall --service` removes the
service and **keeps** the home (the login), the exec policy and the binary.

**The exec policy file** is what decides how much a remote run may do on that machine. The service account cannot
write it (on Unix it must be owned by root and not group- or world-writable, as must its folder, and neither may be a
link; on Windows the service must not be able to open it for writing) and **anything doubtful means `off`**: a missing,
unreadable, invalid or writable file grants nothing. `aipod install --service --exec off|argv|shell` writes it
(default `off`; `shell` is a separate, stronger opt-in); an admin may edit it afterwards and restart the service:

```json
{
  "level": "argv",
  "allowedExecutables": ["/usr/bin/journalctl", "/usr/bin/tail"],
  "allowedRoots": ["/var/log", "/srv/app/releases"]
}
```

`level` is `off`, `argv` or `shell`. `allowedExecutables` and `allowedRoots` are the optional local **ceiling**: a
list of absolute executables a run may start and of absolute folders a path argument must stay under (Windows paths
likewise, e.g. `C:\Program Files\Git\cmd\git.exe`). A file with a ceiling never allows shell runs (shell text cannot be
held to it). An empty or missing list adds no restriction of its own; the
web grants and the agent's own checks (never a shell or interpreter in a grant, no `/etc`, `/home`, the agent's home)
apply either way. A relative path makes the whole file invalid, hence `off`. The API can never raise this level.

### Job schedules, signed approvals and a separate run account (ADR-0010)

- **Schedules.** An admin turns on "Allow schedules" in the workspace's remote work settings (off by default). The owner of a machine then starts a schedule for an approved job without a placeholder in its command, on a machine that is not production or a
  database: every 15 minutes to 7 days, or once a day at a UTC time. At most 5 per machine, never overlapping its own last run, no catch-up after downtime, paused after three failed runs in a row, stoppable on the machine's page at any time.
- **Signed approvals.** Each owner makes a key on their account page (the browser makes it; its private half never leaves the browser) and registers it. On a machine that should trust it, the admin runs `aipod approvers add <public key> --name <name>`
  (copy the key from the account page and compare the fingerprint it prints), then `aipod config approvals required`. From then on the machine runs only what a pinned key signed; it takes no job, grant or schedule (each run needs its own signature),
  and it refuses everything until a key is pinned. `aipod approvers list|remove <fingerprint>` and `aipod status` show what is pinned. The file is `approvers.json` beside `exec.json` on a service (root-owned: the agent distrusts it otherwise) and in the
  agent's home for an interactive one. Off by default; nothing changes for a machine that does not turn it on.
- **A separate run account (Linux and macOS service agents).** `sudo aipod install --service --server <url> --exec argv --run-account [<name>]` (default `cm-run` on Linux, `_cmrun` on macOS) creates a non-login account, adds the service account to its
  group, makes `<home>/run-work` (setgid, shared by the two accounts) and writes `/etc/sudoers.d/cm-agent-run` (checked with `visudo -cf` first) so that only the service account may start commands, and only as the run account. Remote runs then start as that account, with
  `run-work` as their home and working directory. `uninstall --service` removes the sudoers file and keeps the accounts. Windows is refused. Not verified on a real host (ADR-0010 §5).

## Two-step sign-in (authenticator app)

Some actions ask for a 6-digit code from an authenticator app (allowing asks, restart and update of an agent, grant and job approvals; always on a production or database machine). The web has a step-by-step page for setting the app up at `/two-step` (sidebar: **Authenticator app**; every code field links to it): install any authenticator app on a phone, press **Set up**, scan the QR code (or type the setup key shown under it), enter the first code, and keep the recovery codes. The QR code is drawn in the browser by `qrcode-generator` 2.0.4 (MIT, no dependencies); the setup key is never sent to another host.

## Updating the agent

Design, trust model and limits: [ADR-0004](docs/adr-0004-agent-self-update.md). **Off by default**: an agent never asks the server for an update
unless a person runs the command below or turns the setting on.

```bash
aipod update --check                  # look only: prints "update available: <version>" or why none is taken
aipod update                          # install the newest signed build of this agent's channel
aipod config auto-update off|check|on # what the daemon may do by itself (default off); no argument prints the setting
```

- `aipod update` and `--check` are your own decision and ignore both settings below. The agent must be logged in (the build comes from its own
  server) and installed (`aipod install`); the installed binary in the agent home is what is replaced. Exit code 0 = installed or up to date, 1 = refused or failed, 2 = usage.
- **Machine setting:** `aipod config auto-update off|check|on` (saved in `agent.json`) or `CM_AUTO_UPDATE` (wins when set). `check` looks every 6 h and reports; `on` also installs.
- **Workspace setting** `agentUpdate` (`off` default, `check`, `on`): the cap an owner or admin sets in the workspace settings on the web. **The lower of the two applies.**
  While the effective mode is `off` the daemon makes no update call at all.
- `aipod status` shows `auto-update: <effective> (this machine: <m>, workspace: <w>)`, `update available: <version>`, `update: <from> -> <to> is being checked` while a new
  daemon is on probation, and `last update check: <code> (<reason>) <dd/mm/yyyy HH:mm>`. After a refused update the code says why (for example `bad-signature`, `channel`,
  `downgrade`, `hash-mismatch`, `no-key`); after a rolled-back one `status` also prints `update <version> was rolled back and is not retried by itself`. The same lines are in `agent.log`.
- **Replacement:** the new binary is staged as `cm-agent.new`, the old one is kept as `cm-agent.prev` (one version back), the daemon is stopped through a `daemon.stop`
  file and started again from the new binary. If it does not report its version and get an answer from the API within 90 s, the previous binary is put back automatically.
- **MCP processes** (one per Claude Code session) keep running the old version until their session ends; they are not killed. Hooks use the new binary at once.
- **From the web** (agent 0.5.0 and later, [ADR-0005](docs/adr-0005-remote-work.md) "Agent actions from the web"): the machine page has an **Agent actions** card with four buttons for the machine's owner: **Status** (what `aipod status` prints), **Check for update**, **Update** (the same as `aipod update`) and **Restart** (the same as `aipod restart`). It is a fixed set, not a way to run commands: it needs no exec level and no workspace switch. Restart and Update ask for a recent sign-in (a fresh two-step code on a production or database machine). An action the agent has not taken within 10 minutes is dropped; an agent older than 0.5.0 ignores it, so update that one by hand once. A service agent cannot replace its own binary, so Update there reports the refusal and an administrator upgrades it.
- **The standards repository's `install.py`** may run `aipod update` instead of rebuilding when an agent is already installed (it only needs the exit code); that is a
  choice of that script, nothing here depends on it.

### Releasing an agent build (maintainer, macOS)

1. **Once per channel,** generate the signing key. The private half goes to your Keychain (service `cm-agent-update-<channel>`) and is never written to a file; the public half is printed:
   `python3 scripts/sign_manifest.py keygen --channel test` and `... --channel prod`. Save the printed line as `deploy/update-keys/<channel>.pub` and commit it (it is public by design).
2. Build: `AGENT_UPDATE_CHANNEL=test|prod bash scripts/build-agent.sh` (optional `AGENT_MIN_SUPPORTED=x.y.z`, default = this version; `AGENT_SITE_URL=https://<site>` also writes `marketplace.json` and `monitor-agent-plugin.zip` so the site serves the plugin itself (with `VITE_PLUGIN_FROM_SERVER=1` in the web build); `AGENT_SIGN_IDENTITY` and `AGENT_NOTARY_PROFILE` as before).
   The channel's public key is built into the agent; `out/downloads` gets the six zips (macOS, Windows, Linux), `SHA256SUMS` and a signed `manifest.json`. Without `AGENT_UPDATE_CHANNEL` the agent has no key, cannot update itself, and no manifest is written.
3. Deploy copies `downloads/` as before (`docs/deploy-windows.md`). **The server offers whatever its web root holds**, so deploy test builds to test and prod builds to prod.
   **Or publish them on a download host** (an R2 bucket behind a public domain): `AGENT_R2_BUCKET=<bucket> bash scripts/publish_agent_downloads.sh test|prod` uploads the build to `daemons/<channel>/` (and keeps it under `daemons/<version>/<channel>/`),
   and the server's `monitor.env` gets `MONITOR_DOWNLOADS_URL=https://<downloads host>/daemons/<channel>`. The update check then reads `manifest.json` there and offers addresses there; `/downloads/<file>` on the server redirects to the host (`docs/release.md`, "Download host").
4. Agents already installed at that channel take it when a person runs `aipod update` or when `auto-update` allows it.

## Updating Claude Code (optional, off by default)

Design and limits: [ADR-0006](docs/adr-0006-claude-code-update.md). The agent can run Claude Code's own `claude update`, and nothing else:
it never downloads or replaces a Claude binary and never ends or restarts a session (running sessions keep their version until their
owner restarts them). It needs BOTH switches, and a CLI install (npm or native; the desktop app updates itself and is never touched):

```bash
aipod config claude-update on         # this machine; the workspace admin must also tick "Allow agents to update Claude Code"
aipod claude-update cancel            # stop a countdown that is running (not tried again for a day)
aipod status                          # shows claude-update: on/off, a running countdown, the last result and versions
```

It runs only when every Claude session on the machine has been idle for 10 minutes, after a 5-minute countdown announced in `agent.log`,
`aipod status` and (macOS only) a desktop notification. `claude update` has no dry run, so an enabled machine gets this notice about
once a day even when Claude Code is current. Not verified on this repository's machines: the native-installer paths, Windows, and the
Windows notification (not built).

## Browser tests (e2e)

Playwright (`e2e/`, Chromium and WebKit) drives the real web app and API. It is optional (global #33): the
`test -> prod` gate runs it only with `GATE_RUN_E2E=1`, or run it by hand. With `E2E_BASE_URL` set it runs against the test environment (its mail must reach a Mailpit at
`E2E_MAILPIT_URL`); without it, `e2e/serve-local.sh` starts its own PostgreSQL and Mailpit (`e2e/services.yml`,
throw-away), builds the web app and runs the API on `E2E_PORT`.

```bash
cd e2e && npm ci && npx playwright install chromium webkit   # once
npx playwright test
```

## Server (test and production)

Windows Server with IIS, the .NET 10 Hosting Bundle and PostgreSQL 18, behind Cloudflare: the whole runbook (set-up,
release, backups, restore drill, rollback) is [docs/deploy-windows.md](docs/deploy-windows.md).

## Environment variables

`.env.example` lists every variable with its default and what it is for. The API reads them only in
`src/ClaudeMonitor.Api/Config/ApiConfig.cs`.

## Secret and token inventory

The repository holds **no secrets**. The API's live in the server's environment (a root-only environment file
read by the service, never committed); development uses the local defaults in `.env.example`, which are not
secrets.

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| `MONITOR_DB` password | the API's PostgreSQL role | created with the database (`deploy/`) | server environment file | the maintainer | yearly, or on any leak: `ALTER ROLE ... PASSWORD`, update the file, restart |
| `MONITOR_SMTP_PASSWORD` | an app password of the sending Google account | Google Account, Security, App passwords (2-step verification on) | server environment file, or the `Smtp` node of the server-only `appsettings.Production.json` (git-ignored; `build-release.sh` refuses to package it) | the maintainer | revoke and create a new one yearly or on any leak |
| `MONITOR_GITHUB_CLIENT_SECRET` | the GitHub OAuth app's secret; callback `https://<host>/api/auth/callback/github` | GitHub, Settings, Developer settings, OAuth Apps (one app per environment) | server environment file | the maintainer | generate a new secret, deploy, delete the old one |
| `MONITOR_MFA_KEY` | seals every user's two-step sign-in secret in the database; lost or changed, every user's authenticator stops working | generated by `deploy/windows/setup-server.ps1` | server environment file **and** the maintainer's password manager | the maintainer | only with a re-seal of the stored secrets (no rotation tool yet): treat as long-lived; on a leak, users turn two-step sign-in off and on again |
| `MONITOR_BACKUP_KEY` | the key the backups are encrypted with; without it no backup restores | generated by `deploy/windows/setup-server.ps1` | server environment file **and** the maintainer's password manager | the maintainer | never silently: a new key only for new backups; keep the old one until its backups expire |
| the Cloudflare Origin certificate and key | TLS between Cloudflare and the server | Cloudflare, SSL/TLS, Origin Server | the server's `LocalMachine\My` store (key not exportable) | the maintainer | before it expires (15 years), or on any leak: revoke in Cloudflare, issue a new one |
| the PostgreSQL `postgres` password | the database superuser (set-up and restore drills only) | chosen at the PostgreSQL install | the maintainer's password manager only | the maintainer | yearly |
| `MONITOR_GOOGLE_CLIENT_SECRET` | the Google OAuth client's secret; redirect `https://<host>/api/auth/callback/google` | Google Cloud Console, APIs and Services, Credentials (one client per environment) | server environment file | the maintainer | add a new secret, deploy, disable the old one |

A **service agent** (ADR-0005) holds one more secret, on each machine that runs one. It is never in the repository,
never in a log and never leaves the machine except in the `Authorization` header to the API:

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| a service agent's credentials (`cred-access-token`, `cred-refresh-token`) | the access and refresh tokens of that machine's service agent, issued when its login code is approved on the web | `aipod` service's own first login (the code is in its log and `login-code.txt`, approved on the web) | Linux and macOS: 0600 files `cred-*` in the service home (`/var/lib/cm-agent`, `/Library/Application Support/ClaudeMonitor/home`), refused if their owner or mode is wrong; Windows: DPAPI **CurrentUser** blobs `cred-*.bin` in `%ProgramData%\ClaudeMonitor\service` (never LocalMachine scope) | the machine's admin | on any doubt: revoke the agent on the web (Machines page), then log the service in again. Refresh tokens rotate on every use, so there is nothing else to rotate on a schedule |

A remote run that executes as the service account can read that account's files, these tokens included
(ADR-0005, "Consequences"); that is why the exec level is local, admin-owned and `off` by default.

Already present on the maintainer's machine, for the macOS installer of phase 4 (they live in the macOS

Already present on the maintainer's machine, for the macOS installer of phase 4, and the two update-signing keys
of ADR-0004, created by `keygen` at the first release of each channel (they live in the macOS
keychain, never in the repository, the shell environment or CI):

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| the `claude-monitor-notary` keychain profile | the stored App Store Connect API credentials `notarytool` uses | created by `xcrun notarytool store-credentials` | the macOS keychain | the maintainer | revoke the API key in App Store Connect and store a new profile |
| the Developer ID Application certificate | signs the agent binary | Apple Developer account, Certificates | the macOS keychain (its name is not secret) | the maintainer | when it expires or is revoked |
| `cm-agent-update-test` signing key | the ECDSA P-256 private key that signs `test` agent builds for self-update (ADR-0004) | `python3 scripts/sign_manifest.py keygen --channel test`, once | the maintainer's macOS keychain, service `cm-agent-update-test` (never a file, the repository, CI or a server); the public half is `deploy/update-keys/test.pub` | the maintainer | new key + a new build installed by hand on every `test` agent: installed agents trust only the key built into them. `keygen` never overwrites an existing key |
| `cm-agent-update-prod` signing key | the same, for `prod` agent builds | `python3 scripts/sign_manifest.py keygen --channel prod`, once | the maintainer's macOS keychain, service `cm-agent-update-prod`; public half `deploy/update-keys/prod.pub` | the maintainer | as above; **lost or leaked, installed prod agents cannot be updated and must be reinstalled by hand** |

## Backup

Nothing to back up yet: the code is in git and there is no runtime data. The hosted database arrives in
phase 4 with backups and a monthly restore drill (global #18), documented here then.
