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
| e2e | Playwright (web) | at the `test -> prod` gate (#33) |

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
  Windows: `%LOCALAPPDATA%\ClaudeMonitor`). Hooks write events and permission requests and read commands; the
  daemon uploads, relays and writes answers. Nothing listens, on macOS or Windows.
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

**Permission prompts are answered from the web too** (maintainer, 03/10/2026). The PermissionRequest hook reports
the tool call at once (`permission_requests`) and waits a configured time; the session's owner may allow or deny it
on the web, and the answer reaches the hook over the agent's stream. No answer in time means the hook gives no
decision, and Claude Code asks on the machine as usual. Only the session's owner may answer; every answer is
audited.

### Environments

Local development runs PostgreSQL and a mail catcher in Docker, the API and the web dev server. Test and
production run on a **Windows Server without Docker** (maintainer, 03/10/2026): each environment is an IIS site
(ASP.NET Core module, in-process) whose API also serves the built web app (one origin), with its own PostgreSQL 18
database; Cloudflare proxies both host names to the server with an Origin certificate; mail goes out through Gmail /
Google Workspace SMTP. The runbook is `docs/deploy-windows.md`. Each environment serves `/api/version` with the deployed commit, which the
`test -> prod` gate checks (#33).

### Distribution

The web offers the installers: a notarised `.pkg` for macOS (Developer ID Installer certificate in addition
to the existing Developer ID Application one) and a signed MSI for Windows (a code-signing certificate or
service). Automatic updates come after the first release.

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
