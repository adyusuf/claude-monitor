# ADR-0003: Pushing web messages into an idle session

**Status:** Accepted (05/10/2026), opt-in. Extends [ADR-0002](adr-0002-agent-platform.md), "Commands from the web".
**Decider:** the maintainer asked for it; the mechanism was chosen from what the Claude Code plugin API supports.

## Context

A prompt sent from the web reached the agent's daemon within seconds (its outbound SSE stream), but entered the
session only at a hook: `UserPromptSubmit` (someone typed) or `Stop` (a turn ended, and only with `--stop-wait`). A
session that is idle has no hook to run, so the message waited for the next local prompt. The model cannot fix this
by polling: every wake-up costs tokens. The MCP server had two tools and no way to speak first.

## Decision

**Mechanism: a Claude Code channel.** The existing `claude-monitor` MCP server (`cm-agent mcp`) declares
`capabilities.experimental["claude/channel"]` and sends `notifications/claude/channel` (`content`, `meta`) when a
message arrives. Claude Code puts it into the session as a `<channel source="claude-monitor" ...>` block and starts a
turn if the session is idle (events that arrive during a turn are delivered together at the next one). Contract:
<https://code.claude.com/docs/en/channels-reference>. Verified against Claude Code 2.1.285.

| Option | Verdict |
|---|---|
| **Channel (MCP notification)** | **Chosen.** The only mechanism that is push, native, per session and starts a turn. Its price: it is a research preview and a custom channel is not on the allowlist, so Claude Code must be started with `--dangerously-load-development-channels plugin:monitor-agent@monitor-agent-local` (a confirmation dialog), the organisation policy `channelsEnabled` still applies, and Claude Code drops events silently when the server was not loaded as a channel. |
| Plugin monitor (`monitors/monitors.json`, a background process whose stdout lines wake the session) / the `Monitor` tool | Supported and needs no flag, but it is marked experimental, runs only in interactive sessions, is not an MCP server (no session identity, no tool, no instructions) and puts each line in the conversation as a notification. Kept as the fallback if channels stay gated; the pump below is reusable for it (print `ChannelEnvelope.Content`). Not built: one mechanism, small step (global #1). |
| `UserPromptSubmit` / `Stop` hooks | The existing path; cannot fire in an idle session. Stays, as the fallback below. |
| The model polling `monitor_status` | Rejected: tokens per wake-up. |
| Typing into the terminal (keystroke injection) | Rejected: needs accessibility rights, breaks on any prompt state, unsafe. |

### The path of a message

```
web --POST--> API --SSE (agent's own outbound stream)--> daemon --SQLite--> MCP process --notifications/claude/channel--> Claude Code
```

The long-lived connection is the daemon's existing SSE stream (ADR-0002: SSE, outbound only, one per machine). The last
hop is a local file read every 500 ms by the MCP process (`CM_PUSH` on): no network, no model, no tokens. Several
sessions on one machine share one stream and one daemon; each MCP process pushes only its own session's messages.

### Which session is this MCP process?

Claude Code starts the hook processes and the MCP server of one session as children of the same process, and every
hook records `session -> parent process id` in the local database. The MCP process looks up the latest session seen
under its own parent id (after `/clear` or `/resume` the id changes, the process does not) and falls back to
`CLAUDE_CODE_SESSION_ID`, which Claude Code also sets (checked: it equals the hook payload's `session_id`; `CLAUDE_PID` was
not reliable). Windows reads the parent through `NtQueryInformationProcess`; that path has not been run (see the report).

### Untrusted input

Every web message is wrapped `<<<claude-monitor-message id=… … claude-monitor-message>>>`, labelled
(`source`, `message_id`, `session_id`, `origin="web"`, `sent_at`), capped at 8000 characters, and anything in the body
that could close the wrapper or forge a `<channel>` tag is defused. The server `instructions` tell the session to treat
the text as data and to ask the user at the terminal before any side-effecting request. This is an instruction to the
model, not a sandbox: the authority that matters is that only the session's owner can send (ADR-0002) and that the
channel is off unless the user enabled it.

### Exactly once, and no silent loss

- **Dedupe:** the message id is the primary key of the local `commands` table; a replay after a reconnect or a daemon
  restart is ignored whatever state the first copy reached. Taking a message is a checked state change in an immediate
  transaction, so a hook and the pump racing for it cannot both get it.
- **Resume:** on every connection the API sends all commands not yet applied (queued or delivered, unexpired); with the
  local dedupe that is a resume from the last delivered id without a new protocol field.
- **Reconnect:** exponential backoff (1 s doubling to 2 min); a stream silent for 75 s (the API pings every 20 s) is
  treated as dead and reopened, which a half-open connection never reports by itself.
- **No silent loss:** Claude Code does not acknowledge a channel notification, and does not say whether the server is a
  channel. A pushed message therefore stays `pushed` (the web keeps showing "delivered") until its wrapper appears in the
  session transcript, which the daemon already tails; then it is reported `applied`. If it never appears within 120 s (the
  channel was not enabled) it goes back to the queue and the hooks deliver it as before.

### Switches (additive, off by default)

- `cm-agent install --push on|off` (saved in `agent.json`, rewrites the plugin: `channels` entry in `plugin.json`) or
  `CM_PUSH=on|off` (wins). Off, the MCP server declares nothing new and nothing runs: the server, the hooks and the two
  tools are byte-for-byte what they were.
- `CM_PUSH_SCOPE=session` (default): only messages addressed to this session. `machine`: every prompt of this machine,
  whichever session it names (the label shows it); for a single-session machine whose binding cannot be found.
- The user sees it: `cm-agent install` prints the exact command that starts Claude Code with the channel; `monitor_status`
  and `cm-agent status` print whether push is on, its scope, the last message id and what waits.
- **Visibility of the listener.** There is no autonomous background agent (global #20): the daemon already existed, and the
  MCP process lives and dies with the Claude Code session. What it consumes: one local file read per 500 ms. Stop: `cm-agent
  install --push off` (and restart the session) or unset the flag.

### Authentication

No new token and no new secret. The daemon authenticates with the credential the device login already put in the OS
store (ADR-0002); the MCP process never touches the network. An environment-variable token was considered and rejected:
the refresh token rotates and cannot live in an environment variable, and a second credential path doubles the surface
for no gain. Revoke from the web's Machines page; rotation is `cm-agent logout` + `cm-agent login`.

### Status (`monitor_status`, `cm-agent status`)

The first sentence is unchanged. It is followed by: events queued locally and how old the oldest is, the last successful
upload and the last failure (an exception type name, never content), the stream state (connected since / reconnecting with the
failure count), the last message id, and the push line. Only ids, counts, times and type names.

## Consequences

- A prompt sent to an idle session starts a turn within about a second of reaching the daemon, if the user started
  Claude Code with the channel flag. Without it, behaviour is unchanged (next prompt, or `--stop-wait`).
- The feature depends on a research-preview Claude Code API; the contract may change. The envelope, the pump and the store are
  independent of the notification call, so a change touches `Mcp/ChannelHost.cs` only.
- Permission relay (`claude/channel/permission`) is not used: the PermissionRequest hook already carries those answers.

## Findings from the investigation (05/10/2026)

- **"Events waiting to be sent: 1" across turns.** `monitor_status` counted the rows of the local outbox. The `PreToolUse`
  hook of the `monitor_status` call itself writes one row less than 2 s before the tool reads the count, and the daemon
  flushes every 2 s, so a 1 at call time is the current call, not a stuck flush. A real stall looks different, and the status
  now tells them apart: it prints the age of the oldest queued event, the time of the last successful upload and the last
  upload failure (an exception type name). Measured end to end: with the daemon running the count was 0 before and after a
  turn. Two real causes of a growing count exist: no daemon (not logged in: it exits with "not connected"; or crashed, see the next point) and an upload the API
  refuses (the outbox resends the same numbered batch and the failure line says so).
- **A crash found on the way, unrelated to this change.** The published ReadyToRun macOS binary of the *unmodified* base
  commit crashes the daemon within seconds with an `AccessViolationException` inside `System.Net.Http` on this Mac (Darwin 27,
  .NET 10.0.106); `DOTNET_ReadyToRun=0` and the Debug build do not crash. A dead daemon never flushes, so the outbox only grows. Tracked
  as a separate task.
- **What is sent to the server:** everything the harness exposes, in full: every hook payload (the complete prompt text in
  `UserPromptSubmit`, tool inputs and outputs in `PreToolUse`/`PostToolUse`, session and subagent events) and every line of
  the session transcript (including the assistant's replies) plus per-message token usage. Known secret patterns are masked
  before upload (default on), an event over the workspace's size cap is cut to a marker with a 4000-character preview, and content is
  never logged. It is not "only tool calls and status" (ADR-0002, "What is captured").
- **A status that lied about idle streams.** The API sent the stream's headers only with its first message, so an idle stream looked
  unconnected until the first 20 s ping. The stream now opens with a `ready` message (additive; older agents ignore unknown names).

## Not verified

- A real Claude Code session receiving the channel message: launching an interactive session was refused by the permission
  classifier in the session that built this, so the end-to-end check used a stand-in MCP client for Claude Code. The
  contract is from the documentation, and the stand-in saw the same `notifications/claude/channel` a real one would. What
  the real TUI shows, whether the model asks before acting, and the exact transcript line that confirms a push are unverified:
  the confirmation matches the wrapper text, which is in the notification's `content` whatever Claude Code renders.
- Windows: the parent-process lookup (`NtQueryInformationProcess`) has not run; the environment variable `CLAUDE_CODE_SESSION_ID` is the fallback.
- An organisation with `channelsEnabled` off, and a Claude Code without a claude.ai login (the docs name both as conditions).
- A long soak of the reconnect loop; the idle-timeout path is unit-tested only.
