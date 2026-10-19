# ADR-0013 (proposed): languages across the system, and starting and stopping Claude Code from the web

Status: **proposed 19/10/2026.** Part A is partly built (the web, the API mails); the rest of A and all of B are design only. Builds on ADR-0005
(remote work, fixed agent actions), ADR-0006 (Claude Code is never ended, restarted or resumed by the agent), ADR-0008 (machine roles, project
folders, asks) and ADR-0011 (the names aipod and Podium).

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

**What "open Claude" can mean.** Three models; they need a spike on a real machine (the flags of the installed Claude Code decide):

| | How | Good for | Cost |
|---|---|---|---|
| M1 background worker | the agent starts `claude` without a terminal (non-interactive, streamed input) in a chosen folder, as the user who runs the agent; the web drives it with prompts; the plugin's hooks report it like any session | servers and machines nobody sits at (the Windows server of this project) | the person has no terminal on it; permission questions go to the web |
| M2 visible terminal | the agent opens a terminal window running `claude` in the folder | a desktop someone is at | needs a logged-in desktop session; not for servers |
| M3 Claude Code's own remote control | whatever Claude Code offers for attaching to a running session | the best fit if it exists | unknown: needs the spike |

Recommendation: M1 first. "Close" means ending **only a worker the agent started** (a pid file with the session id); a session the person
started in their own terminal or desktop app is never ended by aipod.

**Choosing where a session starts.** *Folder:* only a project the machine has reported (the web sends a project key, the agent maps it to the path).
*Environment:* needs the maintainer's meaning: either the machine's environment class (dev, test, production; then it is the choice of machine and
production and database machines are refused by default, as for asks) or Claude's own settings (permission mode, model). *Repository:* a clone is
code from the network on the machine, and a repository can carry git hooks and configuration that run code. So: phase 4, only from an allowlist an
admin keeps per workspace, cloned into a folder aipod manages, with hooks off (`core.hooksPath` empty), no recursive submodules, and the new folder
then becomes a reported project like any other.

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

**Phases.** P0 spike (the flags and the hooks of the installed Claude Code on macOS and Windows; whether the hooks fire for a background session;
what a stop does) · P1 start and stop a worker in a known project, on demand, owner only · P2 the new-session form and the project picker · P3
automatic `on_message` · P4 repository allowlist · P5 the visible-terminal variant for desktops. Tests: a fake `claude` program for everything but
P0; the real thing only on a test machine.

## Open questions for the maintainer

1. M1, M2 or M3 (or: do the spike first and decide on its result)? 2. What "environment" means in "choose the environment". 3. Repositories: not at all,
only from an admin allowlist, or any? 4. Automatic mode: who may turn it on (the machine's owner, or also a workspace admin)? 5. Production and
database machines: no remote start at all (proposed), or only with a fresh two-step code? 6. The permission mode of a worker nobody watches (proposed:
`default`, so every risky step waits for a person on the web). 7. The cost of a worker is the machine owner's: is that acceptable?
