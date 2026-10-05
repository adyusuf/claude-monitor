# ADR-0002 appendix: reaching an idle session with an MCP channel

Status: **proposal, no code written.** The maintainer decides. Parts 1 and 2 of `fix/idle-session-commands`
(honest status on the web, `cm-agent install --stop-wait`) stand on their own whether or not this is approved.
Written 05/10/2026. What was measured, what was only read, and what is not known are kept apart below.

## The problem

A prompt sent from the web enters a session only at a hook: `UserPromptSubmit` (someone types) or `Stop` (a turn ends).
A session that is idle has no hook running, so the command stays "delivered" until it expires. A Claude Code *channel*
is the one documented way to wake an idle terminal session from outside.

## What a channel is (read in the Claude Code docs, 05/10/2026: Channels and Channels reference)

- An MCP server, spawned by Claude Code over stdio, that declares `capabilities.experimental["claude/channel"] = {}` and
  sends `notifications/claude/channel` with `{content, meta}`. The text arrives in the session as a `<channel source=...>`
  block and starts a turn if the session is idle. There is no acknowledgement; events are silently dropped when the
  session did not load the server as a channel or policy blocks it.
- **Research preview.** `--channels` accepts only an Anthropic-maintained allowlist (or the organisation's
  `allowedChannelPlugins`). Our plugin is on neither, so it needs `--dangerously-load-development-channels
  plugin:monitor-agent@monitor-agent-local` (or `server:<name>`), which shows a **full-screen warning dialog at every
  start** ("I am using this for local development") and a banner line while the session runs. The flags are not listed
  in `claude --help`, and the syntax "may change".
- Claude.ai Team/Enterprise: blocked until an owner sets `channelsEnabled: true`. Pro/Max without an organisation and
  Console keys: allowed. Not available on Bedrock, Vertex or Foundry.
- Sender gating is the server's job: "an ungated channel is a prompt injection vector".

## What was measured

Only the earlier measurement in the standards repository (decision log, "MCP channels to reach an idle session",
30/09/2026, Claude Code 2.1.281): a stdlib stdio server woke an idle interactive CLI session 2 of 2 times, 20 ms from
enqueue to dequeue; the desktop app passed no channel flag; a server that negotiated a newer protocol revision was
skipped. **Nothing was run for this report.** The CLI installed here is 2.1.285; the docs name 2.1.234 (permission
relay) and 2.1.211 (relay sanitising) as thresholds; the first version with `claude/channel` was not found in the docs.

## Why it does not reach the desktop app

The channel is enabled per session by a command-line flag (`--channels`, `--dangerously-load-development-channels`).
The desktop app starts its sessions itself and offers no way to pass either flag (30/09 measurement; the docs describe
the flags only for the `claude` command). Hooks keep working there, so the hook path stays the only one for the app.

## Cost to add it to `cm-agent mcp`

1. **Capability and notification.** The MCP server is built on the C# SDK (`ModelContextProtocol` 2.1.0). It needs
   `Capabilities.Experimental["claude/channel"]` and a raw `notifications/claude/channel` send. **Not verified** that
   2.1.0 allows both, and **not verified that it negotiates a protocol revision Claude Code accepts** (a server on the
   2026-07-28 revision is skipped by the v2 client). This is the first thing to measure, in a throwaway probe.
2. **Which session?** A channel notification goes to the session that spawned the server; the command is addressed to
   a `session_id`, which the MCP process is never given (`monitor_note` takes it from the model). A plausible fix, **not
   tried**: the MCP server and the hooks are both children of the same `claude` process, so a `SessionStart` hook could
   store `parent pid -> session_id` in `agent.db` and the MCP process look itself up by its parent pid. On Windows
   the parent process must be checked too.
3. **Delivery path.** The daemon already writes web commands to `agent.db`. The MCP process would poll it (as the hooks
   do) and, for a prompt addressed to its session, send the notification and mark the command applied. Roughly 150
   lines plus tests and a mutation check, if 1 and 2 hold.
4. **User effect.** Per machine: an alias or shortcut that starts `claude --dangerously-load-development-channels
   plugin:monitor-agent@monitor-agent-local`; the warning dialog at every start; an organisation owner action on Team
   and Enterprise. Sessions started without the flag keep today's behaviour.

## Security

A web prompt delivered as a channel event is **a remote command into a local session**, the same surface the hook path
already has, made reachable when nobody is at the keyboard. What it adds is that the session may then act with no one
watching, bounded only by that session's permission mode.

- **Who may send:** unchanged and enforced in the API, fail-closed: only the session's owner (global #6). The channel
  must not widen this; the agent forwards only commands the API delivered over its own authenticated stream and never
  accepts text from any other source (no local port, no HTTP listener: the agent opens none).
- **What it may do:** whatever the session's permission mode allows. Do **not** declare `claude/channel/permission`:
  that would let the web approve tool use for an unattended session. Permission prompts stay local, so an idle session
  woken by a prompt stops at the first tool that needs approval, as designed.
- **Audit:** the API already audits every command; the agent reports `applied` through the channel path exactly as
  through a hook, so one command has one audit trail whichever path took it. A channel notification has no
  acknowledgement, so "applied" would mean "written to the transport", not "processed", and the web must not claim more.
- **Injection:** the content is the owner's own prompt, but it is shown to the model inside a `<channel>` block with
  `meta` attributes; the agent sends fixed `meta` keys only (command id, session id) and no content from captured
  events, so nothing a session produced can be replayed into another.
- **Policy:** on Team/Enterprise the organisation can switch channels off; the agent must treat silence as "not
  delivered" and let the command expire, never retry in a loop.

## Recommendation

Do not build it now. The status work (part 1) removes the surprise, and `--stop-wait` (part 2) gives terminal and
desktop sessions a way to stay reachable after a turn. The channel helps only terminal sessions started with a
development flag and a dialog at every start, in a preview whose syntax may change. If it is wanted anyway, the next
step is a one-day probe answering the two open questions (1 and 2 above) before any product code; the decision after
that is the maintainer's.
