# Live board — sessions, cost and channels

Part of the design reference: [`live-board.md`](live-board.md) is the entry point (how it works, enabling,
decisions, the Merge column, sending work, app mode, task ids, rules, the desktop window, table order and
the status question). This file holds the sections about **sessions and what they cost**; the section
numbers (2c, 4, 5) are the same as when they lived in `live-board.md`, so older references still hold.

## 2c. Sessions, cost and context

**Cost:** measured from Claude Code transcripts (`board_cost.py`), **never estimated**.
The sole source of prices is `board_config.py` (pricing table cached 25/09/2026 from the
claude-api skill); a model not listed there shows **"cannot be measured"** in the UI, never
a guess. Tokens are read incrementally — each call parses only the bytes added since the
previous read, and a half-written last line waits for the next read. Subagent transcripts
may record output_tokens at stream start, so agent cost may be undercounted; the board
shows this as a note when subagents are active.

The hook records the paths to Claude Code's own transcript files (`transcript_path` on
`UserPromptSubmit`/`Stop`, `agent_transcript_path` on `SubagentStop`). Subagent transcripts
are found in `<session-transcript-path-without-.jsonl>/subagents/agent-<id>.jsonl`. The cost
cache holds one `Transcript` object per path (thread-safe) and updates in place.

**Agents:** every row of the Agent activity table carries its own tokens (in / out / cache
read / cache write) and cost, read from that agent's transcript (the `SubagentStop` path, else
`<session>/subagents/agent-<id>.jsonl`); a running agent shows its cost so far, read
incrementally. A row whose agent id or transcript is not known yet says "cannot be measured
yet" and is left out of the total row, which counts the measured agents and names how many
are not. The roles panel adds each role's cost. A resumed agent (`SendMessage`) or a Workflow
agent never passes through the Agent tool's `PreToolUse`, so `SubagentStart` (with a non-empty
`agent_type`) opens its row and `SubagentStop` closes it; an agent that already has a row from the
Agent tool runs again in that row (no duplicate), and an untyped internal subagent is ignored.
Projects enabled before 30/09/2026 add the `SubagentStart` line to their settings block to get
these rows; without it everything else keeps working.

**Todo list:** the sessions panel shows each session's own todo progress ("todo 2/5 · what is
running now"). The main session's todo tools are recorded by the existing `PostToolUse` `*` hook,
with shapes read from the CLI binary, not guessed: `TodoWrite` (`{todos:[{content,status,activeForm}]}`,
the whole list each time → `todo_sync`), `TaskCreate` (result `{task:{id,subject}}` or the text
"Task #<id> created successfully" → `todo_add`) and `TaskUpdate` (`{taskId,status,subject?,activeForm?}`,
status `pending|in_progress|completed|deleted` → `todo_update`). The latest list replaces the previous
one, an empty list clears it, a subagent's call is ignored, and anything else — a payload without an id,
an update for a task the board never saw created, an undocumented status — records nothing (no todo
line, never an invented one). Not verified live: this repository's sessions have no `TaskCreate` tool, so
the result shape is checked against the binary's source and the tests, not against a real call.

**A task's orchestrator cost (no agent linked):** a main-thread `Bash` call that runs `board.py add|set T-n` links
that session to the task (`task_session`; a subagent's call and `board.py add auto`, whose id is not known yet,
do not). The task's Cost cell then adds `orchestrator ≈ $x` — the session's **main** transcript inside the task's
time window (first touch to now while the task is `running`, to the last touch otherwise), labelled an estimate
with its basis and "N other tasks in the same window" when windows of the same session overlap, because the
session may have done other work in those hours. The session's subagents are not counted there (their cost
reaches the task through `board.py set T-n --agent`, counting them twice would overstate it); a model without a
price makes the figure "cannot be measured", never zero. Tasks written before this existed have no linked
session and keep showing "no agent linked".

**Session names and unmeasured sessions:** the sessions table shows each session's name above its id.
The name is read from the session's own transcript — the last `custom-title` entry (`customTitle`, set by
the user or the app), else the last `agent-name` entry (`agentName`) — cut to 80 characters; a session
without either shows the id alone. A session whose events never carried a transcript path (recorded
before the hooks sent it, or a client that does not) is looked up as `<session-id>.jsonl` under
`~/.claude/projects/*/` (`BOARD_TRANSCRIPTS_ROOT` overrides the root; the id is checked against the
session-id pattern before it is globbed, and a miss is not retried for 30 s). A session whose transcript
does not exist at all (deleted, or the client keeps none there) stays "cannot be measured": the board
says so instead of guessing.

**Context:** the last main-thread call's input + cache read + cache write tokens, set against
the model's window size. The board shows **"context warn" at ≥80%** — a UX reminder, never a
block. Compacting the context (`/compact`) is the user's own command in Claude Code; neither
Claude nor a hook can trigger it, and the board shows a warning, never a fake button.

**Sessions:** visible on the board for up to 24 hours after their last activity; state is
**busy** (from `UserPromptSubmit` to `Stop`) or **idle**. Sessions with no transcript file
yet show orchestration cost as **"cannot be measured"**; the agents row counts only measured
subagent files.

**Projections** (task and session): each is an **ESTIMATE** carrying its basis.
- **Task:** spent by its agents + (spent ÷ elapsed hours from the earlier of task start or
  first agent message) × the task's own remaining ETA minutes. Agents that ran before the
  task was marked running are real; their cost must not inflate the rate.
- **Session:** spent + $/h over the last 3600 s × remaining ETA hours of open tasks (those
  with an ETA; tasks without are counted and named, not included in the estimate).

| What is measured | Signal | When updated |
|---|---|---|
| Tokens (input, output, cache read, cache write 5m, cache write 1h) | `message.usage` in transcript | parsed by `/api/state` (page polls every 1.5 s) |
| Cost per model | pricing table in `board_config.py` | on every `/api/state`; only the bytes added since the last read are parsed |
| Task spent cost | all agents' transcripts by `agent_links` or [T-n] tag | per-session view, once per /api/state |
| Task projection | task start/first agent msg, ETA, spent, rate | estimated, per-session view |
| Session total cost | orchestration + all subagents | per-session view, once per /api/state |
| Session projection | $/h over last 3600 s, open task ETA | estimated, per-session view |
| Context use | main thread's last call's input+cache | per-session view, once per /api/state |

## 4. Cost (measured 29/09/2026)

The hooks and the server cost **no tokens** while silent. What enters the context:

| Item | Size | ≈ tokens (chars/4, estimate) | When |
|---|---|---|---|
| Skill description | 186 chars | ~46 | listed in every session |
| `SKILL.md` body | 2,380 chars | ~595 | loaded when planning |
| `board.py add` call | 191 chars | ~48 | once per task |
| `board.py set` call | 80 chars | ~20 | per status change |
| Board-change reminder (2 changes) | 203 chars | ~51 | when the user changes a control |
| Queued task notice (measured 30/09/2026) | 303 chars (with text "add a changelog entry for T-23") | ~76 | when a task is queued for the session |
| Mode-change notice (measured 30/09/2026) | 346 chars (mode C) | ~87 | when mode is switched on the board |
| Deny reason | 115 chars | ~29 | per denied agent call |
| Decision reminder (one decision with a note) | 253 chars | ~63 | per decision |
| Auto-start line (`SessionStart`) | 69 chars | ~17 | once per session |

A 7-task hour ≈ 1,800–2,000 new tokens (estimate); every added token is then
re-read from cache on later calls. Hook latency: **65 ms median** per tool call
(20 runs, no-op `PostToolUse`).

## 5. Channels — pushing a task into an IDLE session (measured 30/09/2026, T-24 phase 1)

Hooks reach a BUSY session only (`PostToolUse` mid-turn, `Stop` at turn end). An idle
session can be reached through an MCP **channel**: a stdio server that declares
`capabilities.experimental["claude/channel"] = {}` and sends
`notifications/claude/channel` with `params: {content: string, meta?: {key: string}}`
(meta keys must match `^[a-zA-Z_][a-zA-Z0-9_]*$`, others are dropped).

- **Measured (Claude Code 2.1.281, interactive CLI):** a minimal stdlib server pushed a
  message into an idle session; it was enqueued and dequeued within 20 ms, arrived as a
  user message `<channel source="<server>" <meta…>>text</channel>` (`origin.kind:
  channel`) and the session started a turn and acted on it — 2 of 2 runs.
- **How to start such a session:** `claude --mcp-config <file> --dangerously-load-development-channels server:<name>`
  (`--channels` alone only accepts marketplace plugins on the approved list). The flag
  shows a confirmation dialog at every start; the user must accept it.
- **Org opt-in:** on claude.ai Teams/Enterprise the managed setting `channelsEnabled: true`
  is required (default off). Not needed on the measured account (no managed settings).
- ⚠️ **Not reachable:** the Claude desktop app (Code tab) starts its sessions without any
  `--channels` flag and has no setting for it, so those sessions cannot receive a channel
  push (not tested live — inferred from the session argv and the app bundle).
- ⚠️ **A session whose login has expired wakes but fails** ("Login expired"): delivery is
  not the same as the session acting.
- **Stays hook-based:** hooks are deterministic, cost no tokens while silent and can block;
  a channel message is only text the model may or may not follow. Channels close the
  idle-session gap only — they never replace the hooks.
- **Fail-closed rule for any board channel server:** it pushes only text the board queued
  for a registered session — never arbitrary text.
