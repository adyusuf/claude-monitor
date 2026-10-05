# ADR-0002: An agent on every machine, a central API and a web app

**Status:** Accepted (03/10/2026). Replaces the local live board; supersedes ADR-0001.
**Deciders:** the maintainer.
**Data model:** [`data-model.md`](data-model.md).

## Context

Until now the product was a local live board: Claude Code's hooks wrote an event log into every project,
a Python server on `127.0.0.1` folded it into a page, and a Tauri window showed that page. It was
single-machine, single-user and Claude-only. Its last release is tagged `archive/board-final`.

The maintainer asked for (03/10/2026):

1. A background program on every machine, called **the agent**. Claude Code and other AI harnesses talk
   to it, through MCP where that is possible.
2. The harnesses hand the agent **everything they expose**; the agent forwards it to a **central API**.
3. Users **download the agent from the web** and install it. It works only after the user **registers**
   and **signs in** from that machine with an account held by the API.
4. The agent **starts with the harness**: a plugin or the MCP entry wakes it on first use. Nothing is
   registered to start at boot.
5. It runs on **macOS and Windows**.
6. **PostgreSQL** holds users, agents, sessions and everything captured.
7. Sessions are followed on the **web**, and the web can **send commands to a session**.
8. Accounts use **e-mail and password**, and also **GitHub and Google** sign-in.
9. **Team workspaces.**
10. The old application and its dependencies are **removed**; the web gets a new design.
11. Two hosted environments, **test** and **production**, each on its own host name. The names live in
    the deployment configuration, not in this public repository.

## Decision

### Components

```
harness (Claude Code first) --hooks + MCP (stdio)--> agent client --local socket--> agent daemon
agent daemon --HTTPS batches, outbound only--> central API --> PostgreSQL
web app (same origin, /api) <--> central API          central API --SSE--> agent daemon (commands)
```

| Part | Technology | Notes |
|---|---|---|
| Central API | .NET 10 Web API, EF Core + Npgsql | `/api/*` under the web's own host (global #17) |
| Database | PostgreSQL 18 | `uuidv7()` keys, monthly partitions for events |
| Web | React + Vite + TypeScript | live updates over server-sent events |
| Agent | .NET 10, one self-contained binary per OS/arch | macOS arm64/x64, Windows x64/arm64 |
| Shared contract | a .NET class library used by both API and agent | the agent protocol is versioned additively |
| e2e | Playwright (web) | optional, at the `test -> prod` gate with `GATE_RUN_E2E=1` (#33) |

### The agent

- **One binary, three roles.** `cm-agent hook <Event>` (called by a harness hook: reads the payload from stdin),
  `cm-agent mcp` (an MCP server on stdio, started by the harness) and `cm-agent daemon` (the long-running part).
  The first two are short-lived: they work through the local database and **start the daemon, detached, if it is
  not running**. One daemon per user per machine, guarded by an exclusive lock file.
- **Never blocks a session.** The hook and MCP roles fail open: on any error they print to stderr and exit 0.
  Hooks that only observe run asynchronously; only those that can carry an answer (PreToolUse, Stop,
  UserPromptSubmit, PermissionRequest, SessionEnd) are synchronous.
- **No network port.** The only channel between the hook processes and the daemon is a SQLite database in the
  agent's home directory, which only the user can read (macOS: `~/Library/Application Support/ClaudeMonitor`,
  Windows: `%USERPROFILE%\.claude-monitor`, see "Home outside AppData" below). Hooks write events and permission requests and read commands; the
  daemon uploads, relays and writes answers. Nothing listens, on macOS or Windows.
- **Home outside AppData (Windows, decided 05/10/2026).** The Claude desktop app is a packaged (MSIX) app, and every
  process it starts (hooks, `cm-agent mcp`) sees reads and writes under `%LOCALAPPDATA%` redirected to a private copy
  (`%LOCALAPPDATA%\Packages\<package>\LocalCache\Local\`). With the home at `%LOCALAPPDATA%\ClaudeMonitor`, a
  `cm-agent login` in a normal terminal wrote `agent.json` to the real folder, while the processes of a Claude session
  read the redirected, empty copy: "Not connected", no events, sessions missing on the server. `%USERPROFILE%` is not
  redirected, so the Windows default moved to `%USERPROFILE%\.claude-monitor`; macOS and `CM_AGENT_HOME` are unchanged.
  On every start, when `CM_AGENT_HOME` is not set, the new home has no `agent.json` and the old one has, the agent
  **copies** `agent.json` (never deleting or overwriting; one line in `agent.log`; a failure is logged and the agent
  goes on). Tokens are in the OS credential store, so no new login is needed. `agent.db` is not moved: it holds only
  events not yet uploaded, and copying a SQLite file a running daemon may have open risks a torn copy, so those few
  events are lost. A process inside the Claude app sees only the redirected old folder, so it migrates from that one;
  the real old folder is migrated by the first process started from a normal terminal. Plugins installed earlier keep
  running the binary in the old folder until `cm-agent install` is run again. `cm-agent status` prints `home:` so a
  split like this is visible at once.
- **Durable outbox.** Every event goes into that database first, then to the API in numbered batches. A batch is
  retried with the same number and rows until acknowledged; the API ignores a batch number it has already
  stored, so a retry never duplicates data. Events written while offline or while the daemon is down are sent later.
- **Credentials** live in the OS store (macOS Keychain, Windows Credential Manager), never in a file.
- **Updates and old agents.** Agents in the field are old clients: the protocol only grows (global #4). Each
  request carries the agent version; the API can answer "upgrade required" below a minimum version.

### Harnesses

Claude Code first, through a **plugin** that carries the hook entries and the MCP server entry, both
pointing at the installed agent binary. Hooks capture everything automatically; MCP gives the model tools
(reading commands addressed to its session, reporting its own state). MCP alone cannot replace hooks: a
model calls tools only when it chooses to. Other harnesses (Codex CLI, Gemini CLI, Cursor) get adapters
for the same binary after Claude Code works end to end; `harness_kinds` in the data model is open for them.

### What is captured

Everything the harness exposes: every hook payload in full (prompts, tool inputs and outputs, session and
subagent lifecycle) and the session transcript as it grows (assistant messages, token usage). This makes
the central database hold users' code and anything their tools printed, **secrets included**, so:

- the agent **masks known secret patterns** (API keys, tokens, private keys) before upload, on by default,
  switchable per workspace (`workspace_settings.mask_secrets`);
- an event larger than a configured size is truncated with a marker, never dropped silently;
- content is never written to an application log (only ids and sizes);
- every workspace has a **retention period** (default 90 days). Past it, events are written **one file per
  workspace per day as a zipped JSON array** and then removed from the database; the archive's path, size, count
  and SHA-256 are recorded (`event_archives`). Decided by the maintainer on 03/10/2026.

### Accounts and sign-in

- **E-mail and password** (verified e-mail; password reset by e-mail), hashed with ASP.NET Core Identity's
  password hasher. **GitHub and Google** sign-in as alternatives.
- A provider sign-in **never silently joins** an existing account: if its verified e-mail belongs to an
  existing user, that user must first sign in the usual way and link the provider from settings.
- The web session is an opaque token in an `HttpOnly`, `Secure`, `SameSite=Lax` cookie, stored hashed
  server-side so it can be revoked. Unsafe methods also require an anti-forgery header.

### Connecting an agent (device authorisation, RFC 8628)

`agent login` asks the API for a device code and shows a short user code and the web address. The user signs
in on the web, picks the workspace and approves the code. The agent then receives an access token (short-lived)
and a refresh token (rotating), both opaque and stored hashed by the API. Every machine appears on the web,
where any of them can be revoked at once.

### Workspaces and authorisation

- Every row that belongs to a team carries `workspace_id`; every API call checks membership in the API,
  **fail-closed** (global #6). Roles: owner, admin, member, viewer. People join by e-mail invitation.
- An agent sends to the one workspace chosen when it was approved; this can be changed from the web.
- Members of a workspace see its sessions. **Only the session's owner** (the user whose agent runs it) may
  send that session a command.

### Commands from the web

The web queues a command (`session_commands`); the API pushes it to the owning agent over the agent's own
outbound SSE connection (the agent has no inbound port). The agent delivers it to the session at the next hook
that can carry it (for example the Stop hook, which can hand Claude a follow-up prompt). v1 kinds: **send a
prompt** and **stop**. Every command expires, is audited, and reports back delivered / applied / failed.

**What "delivered" means, and the idle session (decided 05/10/2026).** The status ladder is queued (the API has it) →
delivered (the agent's daemon took it into its local queue) → applied (a hook handed it to the session). A command
enters a session only at a hook: `UserPromptSubmit` (someone typed in the session) or `Stop` (a turn ended). A session
that is idle, with its turn finished and nobody typing, has no hook to run, so a prompt sent to it stays "delivered"
until it expires (30 minutes). That is a property of the harness, not a fault, and the web says so instead of letting
"delivered" read as "done": each waiting command shows what it waits for and the time left, an expired one says whether
the session was idle or the agent never took it, and a session whose last event is older than
`config.idleSessionMinutes` (web) warns above the prompt box. `cm-agent status` shows the local queue
(`commands waiting: N (oldest expires HH:mm)`) and the stop wait in force.

**The Stop hook's wait stays 0 by default (product decision).** `cm-agent install --stop-wait <seconds>` (0-590) makes
a finished turn listen to the web for that long and start a new turn from a prompt that arrives; the value is saved in
`agent.json` (the hooks are rewritten, so Claude Code's hook timeout becomes the wait + 15 s) and `CM_STOP_WAIT`, when
set, wins. The default is not raised because a session that keeps waiting after its turn looks busy to the person at
the terminal (the turn has not finished) and stretches the turn Claude Code measures. Whoever wants idle sessions
reachable opts in per machine.


**Permission prompts are answered from the web too** (maintainer, 03/10/2026). The PermissionRequest hook reports
the tool call at once (`permission_requests`) and waits a configured time; the session's owner may allow or deny it
on the web, and the answer reaches the hook over the agent's stream. No answer in time means the hook gives no
decision, and Claude Code asks on the machine as usual. Only the session's owner may answer; every answer is
audited.

### The session page shows the conversation (decided 05/10/2026)

The activity of a session is read as Claude's own screen shows it, not as a list of hook events: what was asked, what
Claude said, each tool call (a script's command, a file) with its output beside it, and the questions Claude put with
their options and the one chosen. Oldest at the top, newest at the bottom; a new message pushes the rest up, and a page
the reader scrolled up stays where it is (a "new messages" button says something came). The source is the `transcript`
events the agent already captures, plus the `Notification` and `SubagentStart/Stop` hooks (one line each, so the page
sees when Claude waits for the user), joined by `tool_use_id`; nothing new is captured or stored. The API's one change is
additive: `GET /sessions/{id}/events?kind=` also takes a comma-separated list (at most 8 kinds); a single kind behaves as
before. A line the agent cut for size still names its call when the preview reaches the call's id, so that call shows
"output too large"; a call with no result shows "running" only while nothing but other calls, notes came after it, and
"no output" otherwise. A message sent from the web while the user typed one reaches Claude as hook context and is shown
as the user's message (recognised by its fixed header; the transcript's exact shape for it is not yet confirmed on a live
session). Permission requests waiting for the owner sit at the bottom of the conversation. **Lazy loading:** only the newest page (40 lines) is read at first, and
scrolling up reads the next older pages, so a session of several megabytes costs what is looked at; a live refresh reads
the newest 20 and reads back only if more arrived. The raw event list stays one click away ("Raw events"). The page
renders the text itself (a small Markdown subset built as React elements, never HTML), so captured text cannot inject
markup and no Markdown library was added.

**Answering Claude's questions and plans from the web (decided 05/10/2026).** A question Claude puts to the user
(`AskUserQuestion`) or a plan waiting for approval (`ExitPlanMode`) is a permission prompt, so it rides the existing
permission path: the `PermissionRequest` hook reports it and waits; the owner answers on the web, where the options are
buttons (one click for a single single-choice question; several questions or multi-choice are collected and sent
together, with optional typed text); the answer reaches the hook over the agent's stream and the hook returns an
`allow` whose `updatedInput` is the question tool's own input plus `answers` (question text -> the option chosen), which
is how the harness takes an answer without asking on the machine. A plan is approved (`allow`) or sent back (`deny`
with the note as the reason). The protocol grew only additively: `answers` on the answer request and on
`PermissionAnswerMessage`, an `answers` column in the agent's local database (added in place when missing). The API
accepts `answers` only for `AskUserQuestion`, only with an allow, only for questions the request carries, each at most
500 characters; the agent builds `updatedInput` from the input the hook received, never from anything the web sent, so
the web can choose among options but cannot change a call. **Fail-safe:** the buttons exist only while an open
permission request for that tool exists. If Claude Code does not raise `PermissionRequest` for these tools, nothing is
reported, no buttons appear, and the question stays readable in the conversation and answerable on the machine.
**Not confirmed on a live interactive session (decided 05/10/2026):** whether Claude Code raises the hook for these tools
and takes `updatedInput.answers`. A non-interactive run (`claude -p`) does not offer `AskUserQuestion` at all, so the
check needs an interactive session. Confirmed on a live run the same day: a prompt sent from the web at the end of a
turn (the `Stop` hook's reason) is kept in the transcript as a meta user line "Stop hook feedback: ..." and one sent
while the user typed (`additionalContext`) as a `hook_additional_context` attachment; the conversation shows both as the
user's message.

### Environments

Local development runs PostgreSQL and a mail catcher in Docker, the API and the web dev server. Test and
production run on a **Windows Server without Docker** (maintainer, 03/10/2026): each environment is an IIS site
(ASP.NET Core module, in-process) whose API also serves the built web app (one origin), with its own PostgreSQL 18
database; Cloudflare proxies both host names to the server with an Origin certificate; mail goes out through Gmail /
Google Workspace SMTP. The runbook is `docs/deploy-windows.md`. Each environment serves `/api/version` with the deployed commit; the
`test -> prod` gate checks it only when asked (`GATE_CHECK_TEST_DEPLOY=1`, #33).

### Distribution

The web offers the installers: a notarised `.pkg` for macOS (Developer ID Installer certificate in addition
to the existing Developer ID Application one) and a signed MSI for Windows (a code-signing certificate or
service). Automatic updates come after the first release.

### Published build

The agent ships as a self-contained single file, ReadyToRun, **not compressed** (`EnableCompressionInSingleFile=false`,
one R2R image per assembly). Compression is off because of a runtime fault, found 05/10/2026 on macOS (Darwin 27, .NET
10.0.6, osx-arm64): a compressed single-file bundle crashed the daemon within seconds with
`System.AccessViolationException` raised at varying places in System.Net.Http. It reproduced 3 of 3 times on the unchanged
base commit, and a ten-line program that only loops `HttpClient.GetAsync` failed 6 of 6 when published compressed and 0
of 6 uncompressed, so it is neither the agent's `setsid` P/Invoke nor its connect callback (both switched off: still
crashed). The compressed bundle crashed with or without ReadyToRun; `DOTNET_ReadyToRun=0` only hid it by making the
framework JIT everything. Uncompressed it also starts faster (`cm-agent status`, 10 runs: 79 ms against 171 ms) and
the zip a user downloads is the same size (29 MB against 36 MB); the cost is the binary on disk (81 MB against 42 MB).
`scripts/agent_smoke.py` publishes the host build and checks the daemon stays up for 10 s against a fake API; the
merge gate runs it. Re-test compression when the runtime is updated, with that script, before turning it back on.

## Options considered

| Question | Chosen | Rejected, and why |
|---|---|---|
| Agent language | .NET (same language and contract as the API, both OSes, an official MCP SDK) | Rust: a second language for one team. Go: the same. Python: needs an interpreter on every machine |
| Agent -> API transport | HTTPS batches, idempotent by batch number | WebSocket for data: harder to retry exactly once. gRPC: proxies and the web stack gain nothing |
| API -> agent (commands) | SSE held open by the agent | an inbound port on the user's machine: never |
| Hook <-> daemon | a SQLite database in the user-only home | TCP on loopback: reachable by every local user. A socket or pipe: more code per OS, and an event is lost while the daemon is down |
| Identity | own accounts (ASP.NET Core Identity hasher) + GitHub/Google | a hosted identity vendor: a new paid dependency and data processor |
| Live web | SSE | SignalR: a client library for a one-way stream. Polling: latency and load |
| Start-up | with the harness (plugin / MCP) | an OS login item or service: the maintainer chose harness-driven start |

## Consequences

- The Python board, its page, the Tauri window, their tests and their documents are removed (phase 0).
  The live hooks in the maintainer's configuration keep running the old board until the agent replaces
  them (phase 5).
- The project rules change: a .NET, PostgreSQL and React stack instead of "standard library only".
- The API is a public contract from phase 1: the backward-compatibility scan stops being an accepted gap
  and becomes a gate step; the agent protocol and the database evolve additively only.
- New secrets (database, SMTP, OAuth client secrets, signing) enter the secret inventory in `SETUP.md` as
  each one arrives; none enters the repository.
- Backups with a monthly restore drill (global #18) arrive with the hosted database.

## Phases

| Phase | Content | Done when |
|---|---|---|
| 0 | This ADR, the data model, the new project rules, removal of the old application | the gate is green on `dev` |
| 1 | API and database: migrations, accounts, workspaces, device authorisation, ingest, paged reads | a test client can enrol and send events |
| 2 | Agent: hook and MCP clients, daemon, outbox, sign-in, credential store, secret masking; macOS, then Windows | a real Claude Code session reaches the API |
| 3 | Web: accounts, workspaces, machines, live sessions, commands; the new design | a session is followed and commanded from the browser |
| 4 | Installers, signing, hosting of test and production, backups | a clean machine installs and connects |
| 5 | Cut-over: the agent replaces the old hooks on the maintainer's machine | the old board is gone |

## Open

1. How the e2e suite reads the test environment's mail (test mail to a Mailpit on the server, reachable by the gate).
2. The Windows signing route (certificate or a signing service).
3. Privacy policy, data export and account deletion for users outside the maintainer's team: the service
   stores other people's code, which brings data-protection duties (GDPR / KVKK).
