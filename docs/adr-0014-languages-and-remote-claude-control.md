# ADR-0014 (proposed): languages across the system, and starting and stopping Claude Code from the web

Status: **proposed 19/10/2026; the spike of Part B ran the same day on macOS (results below); P1 of Part B is built (see "P1 as built").** Part A is partly built (the web, the API mails); the rest of A and P2 onward of B are design only. Builds on ADR-0005
(remote work, fixed agent actions), ADR-0006 (Claude Code is never ended, restarted or resumed by the agent), ADR-0008 (machine roles, project
folders, asks) and ADR-0012 (the names aipod and Podium).

## Part A: every surface in tr, en and de

**Built.** The web (Podium) has English, Turkish and German; a test keeps all three on the same keys and placeholders; the language menu comes from
one list; a stored or browser language tag picks the language. The API's mails (verify, reset, invite) exist in all three and are chosen from the
`Accept-Language` header by preference; the web now sends the language picked in its menu on every call, so a mail comes in the language the person
sees. The authenticator label is Podium.

**What stays English, on purpose.** (1) Everything the *model* reads: tool names and descriptions, notices, envelopes, the machines block (a notice in
the person's language would not make the model more careful, and quoting stays exact). (2) Logs and the outcome lines of fixed actions (operators read
them; they are never captured text). (3) Wire strings: API error codes (they are keys the web already translates), the markers the tailer and the web
match. (4) The repository's documents (global rule: English). Dates stay `dd/mm/yyyy` in every language (global rule 12).

**Still to do (design).**
- *The agent's own words for the person at the terminal:* usage lines, `status`, `install`/`login`/`connect` messages, errors. One catalog class per
  assembly (`Strings`: a typed key, a table of en, tr and de), the language from `AIPOD_LANG`, else the operating system's UI culture, else English;
  a test that every key has all three and the same placeholders (the rule the web test already enforces). The tray's menu and window use the same
  catalog. Existing tests that read English messages keep passing because English is the default; new tests cover tr and de.
- *The user's language as a setting* (`users.language`, additive, nullable) so a mail to someone who is not signed in can still follow their
  account; until then the header is the only source.
- Size: medium (about 300 strings in the agent and tray). Not started.

## Part B: starting and stopping Claude Code, and opening sessions, from the web

**What was asked.** When aipod is up on a machine, the person wants to open and close Claude there remotely, either automatically or on demand; and
when Claude is open, to start a new session and choose the environment, the folder or the repository.

**What exists.** A machine's agent sees the sessions of Claude Code (`<config dir>/sessions/<pid>.json`: pid, folder, status idle/busy/waiting); the
web already follows sessions (through the hooks), sends prompts to them, and answers their permission questions; ADR-0008 gives each machine a
role (environment and function) and a list of project folders the agent reports (never a free path from the web); ADR-0005 has fixed agent actions
(restart, update, status) that carry no text, are owner-only and need a recent sign-in or a two-step code. **ADR-0006 decided that the agent never
ends, restarts or resumes a session.** This part changes that only for sessions the agent started itself.

**What "open Claude" can mean.** The spike (below) found that the installed Claude Code (2.1.294) already has background sessions of its own:
`claude --bg` starts a session in the background under Claude Code's own daemon and prints its id; `claude agents --json [--all]` lists sessions
(interactive and background) with pid, folder, name, status and state; `claude stop <id>` ends one cleanly (its SessionEnd hook runs, the conversation
is kept); `claude attach`, `logs`, `respawn` and `rm` exist too. That replaces the three models considered before (a worker the agent builds, a visible
terminal, an unknown remote control): **aipod does not build a worker; it calls Claude Code's own commands** (`claude --bg`, `claude agents --json`,
`claude stop`), the same way ADR-0006 calls `claude update`. "Close" ends **only a session aipod started** (its id is written to a file when it starts);
a session the person started themselves, background or not, is never touched.

**Spike results (macOS, Claude Code 2.1.294, 19/10/2026; isolated settings so nothing reached the real server).**
- `claude --bg [--name n] [--permission-mode m] ["prompt"]` returns in about 2 s with a short id (`34a6ea9d`), which is the first eight characters of the
  full session id (`claude agents --json` prints both). Without a prompt the session starts **idle** ("send a prompt to start"); with one it runs it.
- The session is a **full interactive session inside a terminal host** under Claude Code's daemon (`claude daemon run`, a pty host per session); `claude logs`
  prints raw terminal frames (escape sequences), not text: not parseable and not to be used. aipod keeps reading sessions the way it does today (hooks and the
  transcript). The daemon and its processes disappear when the last background session is removed.
- **Hooks fire in a background session** (SessionStart, UserPromptSubmit, PreToolUse, Stop, SessionEnd, PermissionRequest), with the usual payload, and the
  **messaging socket and token are in the hooks' environment** (`CLAUDE_CODE_MESSAGING_SOCKET`, `..._TOKEN`). So everything aipod does for a session works
  unchanged: the web sees it, a prompt written to its inbox socket reaches it while idle (measured: UserPromptSubmit fired for the pushed line), and a
  permission question reaches the PermissionRequest hook (measured: a hook that denied stopped a `touch` outside the project), which is how the web answers permissions today.
- **Trust is per exact folder and cannot be given remotely.** `--bg` in a folder the person never trusted prints "Workspace not trusted. Run `claude` in <dir> once and
  accept the trust prompt" and starts nothing; trust in a parent does not cover its children. This is the right gate: a remote start can only open a folder the person
  already trusted by hand. The agent can read the flag (`hasTrustDialogAccepted` in Claude's own `~/.claude.json`, read-only) to offer only folders that will work.
- `claude stop <id>` leaves `state: stopped`; `claude --bg --resume <short id>` starts a **copy** (a new id), while the full session id continues the same conversation.
- The session runs with the **person's own Claude login and plan** (the spike ran on a Max plan): the cost is theirs.
- `--permission-mode` takes `acceptEdits | auto | bypassPermissions | manual | dontAsk | plan`; a session started with `manual` reported `default` in its hooks.
- **Not verified:** Windows (the daemon uses Unix sockets on macOS; whether and how `--bg` works on Windows, the platform of the server this project runs on, needs a
  test on that machine); a logged-out Claude; what happens to a background session when the person logs out of the machine.

**Choosing where a session starts.** *Folder:* only a project the machine has reported **and** Claude Code trusts (the web sends a project key, the agent maps it to the path and checks the trust flag).
*Environment:* decided by the maintainer (19/10/2026): it is the machine's environment class (dev, test, production). So choosing the environment is choosing
among the machines of that class, and **production and database machines get no remote start at all** (as for asks). *Repository:* decided: none. No clone, no
repository field; a folder has to exist and be trusted already.

**Keys (fail-closed, additive).** New closed action kinds with no free text: `claude_start`, `claude_stop`, `session_start`. The fields they carry
are enums and keys: a project key, a permission mode from a closed list (default `default`, never bypass), and optionally a first prompt, which is
text from the web and is wrapped, labelled and capped like any message from the web. What applies, as for the fixed actions: membership at create,
delivery and report; only the machine's owner; a sign-in within the window or a two-step code, always a fresh code on a production or database
machine (and there, by default, no remote start at all); a workspace switch **and** a machine opt-in, both off by default (as push and asks); a cap
on agent-started sessions per machine; a rate limit; audit rows (kind, machine, requester, project key; never the prompt). Not on a service-mode
agent (no user home, no Claude login). The worker uses the Claude login of the person whose agent it is: nothing secret moves, and the cost is
theirs (the page says so).

**Automatic mode.** Per machine, set by its owner: `off` (default), `on_message` (when a message from the web or an ask arrives and no session can
take it, the daemon starts a worker in the project the message names; this closes the gap where a message found no Claude open), `always` (one
worker is kept running). Both are bounded: backoff after a failed start, a start budget per hour, never a second worker for the same project. The
agent reports why it started (message, policy, person) on the session.

**Contracts and data (additive).** The action kinds join the closed set (a CHECK-constraint migration); new nullable columns for the policy on
`agents`; a small table of agent-started workers (agent, session id, project key, started by, times, status). The stream, the hooks and the session
list are unchanged; the session list marks agent-started sessions. An old agent ignores an action it does not know; the page says it needs the
newer version.

**The page.** On the machine's page, a Claude card: what runs (agent-started and other sessions), Start and Stop, "New session" (project from
the machine's list, permission mode, optional first prompt), and the automatic-mode choice; in the three languages of Part A.

**Phases.** P0 spike: done on macOS (above); **Windows still to do** · P1 start and stop on demand for a reported, trusted folder on a dev or test machine, owner
only, through `claude --bg` and `claude stop` · P2 the new-session form and the project picker · P3 automatic `on_message` · (P4, the repository allowlist, is
dropped by decision; P5, the visible terminal, is not needed: `claude attach` exists for a person at the machine). Tests: a fake `claude` program for
everything but the real check on a test machine.

## Decisions of the maintainer (19/10/2026) and what is still open

Decided: the environment is the machine's class (dev, test, production); no repositories; no remote start on production or database machines.
Still open: 1. Windows: a test of `claude --bg` on the Windows server (the spike ran on macOS only). 2. Automatic mode: who may turn it on (the machine's owner
only, as proposed, or also a workspace admin). 3. The permission mode of a session nobody watches (proposed: `manual`, so every risky step waits for a person on
the web; never bypass, auto or dontAsk). 4. The cost of a session is the machine owner's plan: acceptable? 5. What the page says for a folder that is not trusted
yet ("open Claude in that folder once at the machine and accept the trust prompt").

## P1 as built (19/10/2026): start and stop on demand

What shipped, on the branch with the rest of this work (not released: the agent version is still 0.5.0, the web expects 0.6.0 for these actions and says so):

- **Contracts (additive).** Two kinds join the closed set of agent actions: `claude_start`, `claude_stop` (`AgentActionKinds`). The request, the message to the agent,
  the report and the view gain optional fields: `projectKey`, `permissionMode` (`manual | plan | acceptEdits`, default `manual`), `sessionRef` (the 8-character short
  id `claude --bg` prints). Nothing is free text. A start needs a recent sign-in or a two-step code like restart and update; a stop does not.
- **API.** Migration `ClaudeControl` (additive): `agents.accepts_claude_control` (+ set_at, set_by), `agent_actions.project_key | permission_mode | session_ref`, and the
  `ck_agent_actions_kind` CHECK widened. `PUT /api/agents/{id}/claude` is the owner's switch (re-authentication to turn it ON, audited, turning it off expires the
  open Claude actions, a machine moved to another workspace forgets it). Creating a Claude action is refused unless: the workspace's remote-work switch is on, the
  owner's switch is on, the agent is not a service agent, an admin SET the environment and it is `dev` or `test` and no claim or database function makes it strict,
  and Claude Code is on the machine. A stop must name a session an earlier SUCCESSFUL start of the same machine returned. Only a successful start may name a session in
  its report. The audit row of a Claude action carries the kind, the project key, the mode and the session id, never text.
- **Agent.** `ClaudeControl` runs Claude Code's own commands with fixed argument shapes: `claude --bg --name aipod-<label> --permission-mode <mode>` in the project's folder
  and `claude stop <id>`. The folder comes from a project the machine has reported (a folder a session visited, or a listed one). It refuses a service agent and a machine
  that claims production or database, keeps at most 3 sessions started from the web running (`ClaudeWorkersMax`), and records each start in `claude-workers.json`
  (user-only home): a stop touches only a session in that file. Claude Code's own "Workspace not trusted" refusal is reported with its next step. An unreadable file
  counts as none of ours, so nothing is stopped on a guess.
- **Web.** A Claude card on the machine page (owner only): the switch (with the code field when asked), a project and a mode (three choices) and a Start button, the
  sessions started from the page with a Stop button each, and the recent Claude actions as text. It shows no start on a machine that is not dev or test, and says why;
  the actions card no longer lists the Claude actions a second time. In Turkish, English and German.
- **Decisions applied.** Environment = the machine's class; no repositories; no start on production or database machines. The workspace-level switch is the existing
  remote-work switch (`remote_runs_enabled`), not a new one.

**P2 as built (19/10/2026): the first message.** The project picker and the permission mode were already in P1. P2 adds the optional first message of a start: the API takes `prompt`
(trimmed, at most 2000 characters, no control characters except new lines and tabs; refused on a stop), keeps it only until the agent takes the action, and never lists or audits it. The agent
gives `claude --bg` one more argument: the same notice and fence as an inbox message (labelled as from the web, to be treated as data, defused, capped at the push limit), so the argument always
begins with the notice and never with the web's own text. Podium shows a text box on the Claude card. Migration `ActionFirstPrompt`.

Not built (P3 onward): the automatic `on_message` mode, a per-project trust pre-check in the page, and any change to how the session list marks agent-started sessions.

## Windows: what still has to be tried (not done; this was developed on macOS)

The server this project runs on is Windows, and none of P1 has been run there. The agent compiles for Windows and the tests run on macOS only. On the Windows machine, as
the user that runs the agent, with Claude Code installed and a project folder you have already opened once in `claude` (accepting the trust prompt):

1. `claude --version` (this feature needs a version that has `--bg`; the spike used 2.1.294) and `claude agents --json`.
2. In that folder: `claude --bg --name aipod-test --permission-mode manual` and note the printed 8-character id. Does it print `backgrounded · <id> · aipod-test`? (The agent
   parses exactly that line.) If it prints something else, or fails, copy the text.
3. `claude agents --json --all`: is the session listed with `kind: background`? `claude stop <id>`: does it say `stopped <id>` and exit 0?
4. With the aipod plugin installed, start a session as in 2 and look at the web: does it appear, and does a prompt sent from the web reach it while idle? (On Windows the
   inbox is a named pipe: check that `CLAUDE_CODE_MESSAGING_SOCKET` is a `\\.\pipe\...` name in a hook of that session.)
5. In an untrusted folder, step 2 must fail with "Workspace not trusted ... accept the trust prompt" and exit non-zero (the agent reports it as "does not trust that folder yet").
6. Does the daemon (not a terminal) start it? On the Windows server, set the machine to environment `dev` or `test`, switch Claude control on for it on the web, and press Start in
   the Claude card; `claude-workers.json` in the agent home should then list the session. Watch for: the `claude` on the service or scheduled-task PATH, the user profile the
   daemon runs under (it needs the same Claude login), and a console window flashing (the agent starts `claude` hidden).
7. Also check where Claude Code keeps its trust list on Windows (`~/.claude.json`, key format of folder paths: slashes) in case a later version of the page wants to pre-check trust.

Anything that differs goes into this section before P2.
