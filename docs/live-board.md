# Live board — design reference

A local page that shows, while Claude works, which task is where, which agent is
running on it, what finished — and lets the user **remove a task** or **switch an
agent off**. It is an **extra view**: the status table in the reply stays mandatory
(the working method's status-table rule). Nothing is published; the page is served on
`127.0.0.1` only.

Code: `scripts/board/` (auto-start, CLI, hooks, server, registry, state folding,
cost parsing, session views, API, icons, browser app) · tests: `scripts/tests/test_board*.py`,
`scripts/tests/*.test.js`. How to install and run it: `README.md`, `SETUP.md`.

**One page for every project:** `http://127.0.0.1:8765`, one tab per project. It
comes up by itself when a Claude session starts in any project that enables the board.

## 1. How it works

| Piece | What it does | Who drives it |
|---|---|---|
| Hooks (`board_hook.py`) | Record every `Agent` start and finish; deny an agent that is switched off, a call for a removed task, or a role outside the declared mode set; tell Claude about board changes | Claude Code, automatically |
| CLI (`board.py`) | Writes the plan: mode + role set, one row per task, the semantic status (`done`, `waiting`, `failed`) | Claude, per the `board-plan` skill |
| Auto-start (`board_ensure.py`) | On `SessionStart`: registers the project, starts the server if nothing answers, **replaces one that runs older code** (`/api/info` reports the build the server started with, `board_config.code_build`; the auto-start compares it with the files on disk, stops only a process whose command line names `board_server.py`, under a lock), says where the board is — never blocks a session | Claude Code, automatically |
| Registry (`board_registry.py`) | The machine-wide list of boards: `~/.cache/claude-board/projects.json` (id, name, path — runtime data, outside every repository) | the auto-start |
| Server (`board_server.py`) | ONE server for every registered project: the tabs, each project's state, the user's controls (same-origin JSON only, validated) | the auto-start |
| Cost (`board_cost.py`) | Parses Claude Code transcripts incrementally to read tokens and cost (prices from `board_config.py`); caches to avoid re-parsing | automatic, once per API call |
| Sessions (`board_sessions.py`) | Computes per-session/per-task cost, context use, projections (estimates carrying their basis) | automatic, once per API request |
| Controls (`board_api.py`) | Validates and applies user controls: task queue, skill run, mode switch, fail-closed; writes `.claude/mode` for selections | the user, via the page |
| App (`board_app.py`, `board_open.py`) | Web app manifest, drawn icons (no binary files), service worker, and the command to open as a Chrome app window | on-demand, or via browser Install menu |
| Page (`board.html`, `board_ui.js`) | Polls every 1.5 s; Turkish first, English switch; dates `dd/mm/yyyy` | the user's browser |

Data lives in `<main checkout>/.claude/board/` — the **main** checkout, so every
worktree of the project writes to one board. `events.jsonl` is append-only;
`control.json` holds the user's controls. Both are created `0600` and are runtime
data: add `.claude/board/` to the project's `.gitignore`.

Measured against Claude Code 2.1.281 (read from the binary, not the docs, whose
summary was wrong twice): the tool is `Agent`; a background call's `PostToolUse`
carries `tool_response.status == "async_launched"` and `agentId`, which binds the
call to its `SubagentStop.agent_id`; `UserPromptSubmit` carries `prompt`.

## 2. Enabling it in a project

**One command:** from the repository, `python3 ~/.claude/scripts/board/board.py enable` does steps 1 and 2 below
and lists the project on the page. It is idempotent (a hook already there is left alone, everything else in
`settings.json` is kept, a file that is not valid JSON is never touched); commit the two files it changed.
Run it in the checkout whose files you will commit: from a linked worktree it writes THAT worktree's
`settings.json` and `.gitignore` (never the main checkout's) and lists the main checkout's board. Settings a
worktree does not have yet can be given locally without a commit through `.claude/settings.local.json` (git-ignored).
The steps below are what it does, for a project that wants to do it by hand.

**Every repository and folder, with nothing per project:** `python3 ~/.claude/scripts/board/board.py enable --user`
wires the same hook block into the USER settings (`~/.claude/settings.json`; the old file is copied to
`~/.claude/backups/` first, other settings and hooks are kept, a file that is not valid JSON is never touched).
From then on a session started in ANY repository or folder lists it on the page, and a project that also
enabled the hooks itself still fires each hook once (identical command strings from two settings sources are
de-duplicated — measured 01/10/2026). Two guards come with it: the home directory and `/` are never a board
(`board_config.boardable`; a board in `~` would land in `~/.claude`), and a board created by a hook alone is
hidden from `git status` through the repository's own `.git/info/exclude` (local, nothing to commit; a project
that ignores `.claude/board/` already is left alone).

**A project is listed as soon as its board is written** — by `board.py plan|add|set` or by any hook event —
not only by the `SessionStart` hook (`board_registry.register_if_missing`; not when `BOARD_DIR` overrides the
directory). Seen live 30/09/2026: ryan had ten tasks on a board and no `.claude/settings.json`, so no page
listed it. Without the hooks the page shows the tasks but no sessions, agents or costs: those need step 1.

1. Merge this block into the project's `.claude/settings.json` (committed):

```json
{
  "hooks": {
    "SessionStart": [{ "hooks": [{ "type": "command", "command": "python3 \"$HOME/.claude/scripts/board/board_ensure.py\" || true" }] }],
    "PreToolUse": [{ "matcher": "Agent", "hooks": [{ "type": "command", "command": "python3 \"$HOME/.claude/scripts/board/board_hook.py\" || true" }] }],
    "PostToolUse": [{ "matcher": "*", "hooks": [{ "type": "command", "command": "python3 \"$HOME/.claude/scripts/board/board_hook.py\" || true" }] }],
    "SubagentStart": [{ "hooks": [{ "type": "command", "command": "python3 \"$HOME/.claude/scripts/board/board_hook.py\" || true" }] }],
    "SubagentStop": [{ "hooks": [{ "type": "command", "command": "python3 \"$HOME/.claude/scripts/board/board_hook.py\" || true" }] }],
    "Stop": [{ "hooks": [{ "type": "command", "command": "python3 \"$HOME/.claude/scripts/board/board_hook.py\" || true", "timeout": 900 }] }],
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "python3 \"$HOME/.claude/scripts/board/board_hook.py\" || true" }] }]
  }
}
```

   `|| true` is deliberate: a missing or broken hook must never block a session
   (a missing file exits 2, which Claude Code would read as a BLOCK). The hook
   itself also logs its errors to stderr and lets the call through.
2. Add `.claude/board/` to `.gitignore`.
3. The skill ships in the `adyusuf` plugin (`/adyusuf:board-plan`) — no link of its own.
4. Open `http://127.0.0.1:8765`. A running session picked up a newly added
   `.claude/settings.json` without a restart (measured 29/09/2026, Claude Code 2.1.281:
   its Stop hook delivered a board decision in the same session), but `SessionStart` —
   the auto-start — only runs when a session starts. If the board stays silent, start a
   new session. The server is started by the `SessionStart` hook and keeps
   running after the session ends; after a reboot the next session starts it again.
   By hand, if ever needed: `python3 ~/.claude/scripts/board/board_ensure.py < /dev/null`.
   `BOARD_DIR`, `BOARD_HOST`, `BOARD_PORT`, `BOARD_REGISTRY` override the defaults
   (single source: `scripts/board/board_config.py`). The server answers only requests whose `Host` is a loopback
   name with its own port (DNS rebinding), and refuses to start on a non-loopback `BOARD_HOST` unless
   `BOARD_ALLOW_REMOTE=1` is set; with that opt-in the board is exposed with no authentication. The server's own log is
   `~/.cache/claude-board/server.log`.
5. If the auto-start reports an **older** board server on the port (one project per
   server, before 29/09/2026), stop that process once; the next session starts the
   one-for-all server.

## 2a. Decisions asked on the board

When a task needs the user's answer, Claude puts the question on the board:
`board.py set T-n --status needs_decision --note "<question>" [--options "a|b"]`.
The page shows the choices (without options: **Continue / Reject**) and a note field.

| When the user clicks | How it reaches Claude |
|---|---|
| While Claude is working | as a reminder after its next tool call |
| After Claude ended its turn with an open question | the `Stop` hook waits up to `BOARD_DECISION_WAIT` seconds (default **180**, `0` = no wait, capped at 840 — the Stop hook's `timeout` is 900) and, on a click, keeps the turn going with the decision |
| After the wait ran out | at the start of the next turn |

While the Stop hook waits, the session shows as busy; a message typed in the chat
queues until the wait ends. Only a decision prolongs a turn; other board changes wait
for the next one. In modes C/D/E a subagent's tool call never consumes a notice meant
for the orchestrator.

## 2b. The Merge column

Each task can carry its commit(s): `board.py set T-n --commit <sha>[,<sha>]`. The page then
shows, per task, whether **every** one of them is in `origin/dev`, `origin/test` and
`origin/prod` — read live from the project's git (`scripts/board/board_merge.py`; the branch
names and the remote are in `board_config.py`). A branch that does not exist is left out; an
unknown commit counts as not merged. It is **as of the project's last `git fetch`** — the
board never fetches; branch tips are re-read at most every 10 s.

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

## 2d. Sending work to a session

**Queuing a task:** the user writes a task on the board (text field, max 1000 chars),
picks a session and taps **Send**. The control enters `control.json`'s `changes` list and
the hook delivers it via `PostToolUse` `additionalContext`:

| Session state | Delivery | Notes |
|---|---|---|
| Busy (turn in progress) | After the session's next main-thread tool call | same turn; subagent tool calls never consume it |
| Idle (turn ended) | Only inside the Stop hook's wait window (while a needs_decision question is unanswered, ≤180 s) or when the user types a new message | other sessions never see the notice |
| At Stop | The Stop hook blocks (`{"decision":"block","reason":…}`) and the turn continues with the queued task | no wait needed; the turn keeps going |

**Running a skill:** the page's skill picker sends `POST /api/control` `run_skill` to the
server (not `board.py`). The control text becomes `"invoke the /<skill> skill"` and
delivery follows the same rules as queued tasks.

**Switching mode:** the user picks a mode letter (A–E) and confirms. The control writes
`<project root>/.claude/mode` with the new letter and records a `mode_set` event with
`"by": "board"` in the event log. This **selection is the approval of the mode choice** — no chat
approval needed. The mode-change notice goes to every session and asks Claude (not the user)
to re-declare the plan: `board.py plan --mode <X> --roles <that mode's role set>`.

**Validation (fail-closed):** every control is validated server-side before writing:
- `queue_task`: the target session must exist in the folded state
- `run_skill`: the skill must be in the list built from `~/.claude/skills/` (plain
  `<dir>/SKILL.md` and plugins' `<name>/skills/<dir>/SKILL.md` and
  `<name>/commands/<name>.md`)
- `set_mode`: the mode letter must match A–E
- Text and JSON fields are checked for control characters; queued task text is limited to
  1000 chars

**Unreached sessions:** `control.json` keeps the last 50 changes. A session that never runs
a hook again (e.g. it crashes before the next tool call) can have its queued tasks fall out
of the list. A queued item is bound to ONE session id: another or a new session never receives
it. This is **by design**: the control file is a delivery channel, not permanent storage; the
user re-sends from the board. The panel lists what is still undelivered per session ("queued").

**Irreversible work:** a queued task or mode switch are not approval. A task that says
"deploy to prod" or "run DROP" still needs explicit chat approval — the board is a local
file channel, not an approval channel. Link: the existing §3 rule "A board decision steers
the work; it is not an approval."

## 2e. App mode

The board is a web app: it can be installed via the browser's **Install** menu to run as its
own window, or opened via `python3 ~/.claude/scripts/board/board_open.py` which:
1. Ensures the server is up (via `board_ensure.py`, no blocking)
2. Tries to open it in a Chrome app window (`--app=<url>`; no tabs, no address bar)
3. Falls back to the default browser if Chrome is not found or the command fails

**Web app manifest** (`/manifest.webmanifest`): name, short name, theme colour, background
colour, scope, display mode, and icons.

**Icons:** drawn at 192×192 and 512×512 PNG (via `board_app.py`), and as SVG. They show the
theme colour (blue) with three white columns (the board's three-column layout). **No binary
files in the repository** — icons are generated on request from the server.

**Service worker** (`/sw.js`): caches nothing. The board is ephemeral and always fresh; a
stale cache would be worse than a reload.

## 2f. Task ids and where the CLI writes (T-28, 30/09/2026)

Several sessions write to one board, and ids were typed by hand: T-25 was taken by three
sessions and T-26 by two, so a later `add` silently replaced an earlier task's title. And
`board.py` finds the board from the working directory's repository, so a `set` typed in another
project's tree created a stray `.claude/board/` there.

- `board.py add auto "<title>"` reads the log and appends **under an exclusive lock**
  (`tasks.lock`), so parallel sessions cannot draw the same number; it prints the id.
- `add T-n` with an id that already exists is **refused** (exit 2) and writes nothing.
- A repository with no board (no `events.jsonl`) is **refused** for `plan`/`add`/`set` unless
  `--init` is given; `list` there prints nothing and creates nothing. A repository whose hooks
  are wired already has a board (the first hook event creates it), so nothing changes for it.

## 3. Permanent rules

- ⚠️ **The board is an extra view, never the report.** The table in the reply
  stays the deliverable, and its figures come from `board.py list`, not memory.
- ⚠️ **No published board.** Artifact dashboards and hosted pages stay out of the
  flow (the working method's status-table rule); the board binds to localhost only.
- ⚠️ **`agent_done` is not `done`.** The hooks mark that an agent returned; only the
  orchestrator closes a task, after auditing it.
- ⚠️ **A removed task or a switched-off agent is obeyed, never routed around.** No
  retrying a denied call under another role. A running agent for a removed task
  is stopped (`TaskStop`) — the hook cannot stop it, only block new calls.
- ⚠️ **A board decision steers the work; it is not an approval.** Anything that needs
  the user's explicit approval in the chat — `test`/`prod` promotion, a deploy,
  deleting data, sending anything outward — still needs it there. The board is a local
  file channel; a click on it never stands in for that approval.
- **The role set on the board is copied from the mode file**, not from memory; the
  hook denies anything outside it.
- **Progress percentages are not shown.** They cannot be measured; states and
  elapsed time can (the working method's status-table rule).
- **Cost and projections are measured or labelled.** An unpriced model shows
  **"cannot be measured"** in the cost column, never a guess. A projection always
  carries its basis (the rate and ETA it rests on) so the figure is not opaque.

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

## 2h. Table order, finished rows and pages

The three tables (Sessions, Tasks, Agent activity) are ordered, filtered and paged in the page itself
(`board_ui_lists.js`; the server sends everything and nothing about the order is stored).

- **Order: active first, then newest first.** Tasks: needs a decision, running, waiting / agent done,
  planned, any status the page does not know, done, removed; newest `updated` (else `started`) first
  inside a group, the higher id on a tie. Agents: starting / running, failed / denied / other, done;
  newest `started` first. Sessions: busy, unknown, idle; newest `since` first.
- **Finished rows are hidden at first.** Tasks: done and removed; agents: done. A checkbox above the
  table shows them again ("show done and removed tasks", "show finished agents") and a note under the
  table says how many are hidden. The stat tiles and the "all done" banner still count every task, and
  the Agent-activity **total row** is measured by the server over every agent, so it is the same on
  every page and with the box ticked or not. Sessions have no finished state, so only order and pages.
- **Pages of 30 rows,** previous / next buttons under each table and "Page p / n · N rows". A page that
  no longer exists (the table shrank while you were on it) is held on the last one; a project switch,
  a ticked box and the "channel only" box all go back to page 1. The page choice lives in the open page
  only (not stored), like the box states.
- **A table whose HTML did not change is not rewritten** on the 1.5 s poll, so a focused pager button
  keeps its focus. A task row is still not redrawn while a note is being typed in it.
