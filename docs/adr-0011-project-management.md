# ADR-0011: Project management in the web app

**Status:** Phases 1 to 3 are on `dev` (19/10/2026); Phase 4a (§6) is on `dev`. Later phases are a plan, not a decision (§7a plans the machine and Claude work).
**Extends:** [ADR-0002](adr-0002-agent-platform.md) (one API for every client), [data-model.md](data-model.md).
**Asked for (19/10/2026):** after sign-in a user sees what is theirs: projects (with applications, backlog lists, sprints and issues), the
machines and the tools and Claude sessions on them, the AI pods of the account, the user and system administration for admins, and
their own profile.

## 1. Naming: `repos` and `projects`

The table called `projects` held the work folders that sessions report (a key from the git remote, a display name). It is not a managed
business entity. The maintainer decided (19/10/2026) to free the word:

| Before | After | Notes |
|---|---|---|
| table `projects`, entity `Project` | table `repos`, entity `Repo` | migration `RenameProjectsToRepos`: a rename, rows and ids stay |
| `harness_sessions.project_id` | `harness_sessions.repo_id` | same migration |
| new | table `projects` | the managed project (§2) |

This is the one deliberate exception to additive-only (global #4), approved by the maintainer: the API of the release before this one
cannot read the renamed table, so a rollback past this migration needs a database restore. **Not renamed:** the JSON names the agent and
the web already use (`projectId` and `projectName` in a session row, `projectKey` in events and runs), the `machine_projects` table and
`project_key` columns (they key a folder on a machine, not a managed project). The wire contract stays as it is (global #5).

## 2. Phase 1: projects and applications

Everything is workspace-scoped; every call checks membership first (global #6).

**`projects`:** name, purpose, owner (a workspace member), sponsor (free text, may be outside the workspace), stakeholders (names with an
optional role, at most 50), budget (amount and ISO currency), start and end date, and the settings the levels below inherit: work item
types (a subset of `epic`, `story`, `task`, `bug`, `spike`), priority scale (`p0_p3`, `moscow`, `high_medium_low`, `one_to_five`),
estimation unit (`story_points`, `hours`, `days`, `t_shirt`), default sprint length (days) and the default Definition of Ready and of Done
(lists of short lines). `status` is `active` or `archived`; "delete" archives (a row is retired, not removed).

**`project_members`:** the project roles and what they allow.

| Project role | Reads | Edits applications | Edits project, members | Archives |
|---|---|---|---|---|
| `manager` | yes | yes | yes | no |
| `contributor` | yes | yes | no | no |
| `reviewer`, `viewer` | yes | no | no | no |

The project's owner and a workspace `owner` or `admin` act as managers and may also archive. Creating a project needs the workspace role
`member` or above; reading needs any role. A workspace `viewer` is read-only everywhere. A person removed from the workspace loses access at
once (the workspace check comes first); their rows stay.

**`applications`** (a project has any number, or none): name, description, kind (`web`, `mobile`, `api`, `other`), repository link, the
environments (`dev`, `test`, `prod`, each with an optional URL), technology stack (short names), versioning scheme (`semver` by default,
`calver`, `none`), technical owner (a workspace member), non-functional targets (`performance`, `accessibility`, `sla`, `security`,
`other`, each with a target text) and `status`.

**`application_components`:** the components and modules of an application, which later tag issues (so a table, not a list in the
application). **`application_releases`:** the release calendar: name or version, planned date, `planned`, `released` or `cancelled`, so
that sprints and issues can point at a release later.

Text a person types (names, purposes, notes, links) is shown as text only and never as markup. A repository link must be `https`, `ssh` or
`git` and may not carry a password.

## 3. API (Phase 1)

| Call | Needs |
|---|---|
| `GET /api/workspaces/{ws}/projects`, `POST` | read / member |
| `GET /api/projects/{id}`, `PUT` (replaces the definition), `DELETE` (archive), `POST /restore` | read / project edit / archive |
| `GET /api/projects/{id}/members`, `PUT /{userId}`, `DELETE /{userId}` | read / project edit |
| `GET /api/projects/{id}/applications`, `POST` | read / application edit |
| `GET /api/applications/{id}`, `PUT`, `DELETE` (archive), `POST /restore` | read / application edit |
| `GET`, `POST /api/applications/{id}/components` and `PUT`, `DELETE /api/components/{id}` | read / application edit |
| `GET`, `POST /api/applications/{id}/releases` and `PUT`, `DELETE /api/releases/{id}` (a release that went out cannot be cancelled) | read / application edit |

Lists are paged by key (newest first). Error codes are i18n keys. Changes write audit rows that name the row by id, never its text.

## 4. Phase 2: issues and the backlog

An issue belongs to a project and, optionally, to one of its applications, so a backlog exists with or without an application. The backlog is
the list of issues of a project (or of one application) that are in no sprint yet, in the order people give them; sprints come in Phase 3,
which adds the sprint column to `issues` (additive).

**`issues`:** `number` (per project, shown as `#12`), title, type (one of the project's enabled item types), priority (a value of the
project's priority scale), estimate (a number in the project's unit, or `xs`-`xl` for T-shirt sizes), assignee (a workspace member who
can work), parent epic (an `epic` of the same project), application, component and release (of that application), status (`backlog`,
`ready`, `in_progress`, `in_review`, `done`, `cancelled`), description (user-story form is a hint in the form, not enforced),
Given/When/Then criteria (at most 30), a technical design note, and for a `bug` only: environment, steps to reproduce, expected result,
actual result and severity (`critical`, `major`, `minor`, `trivial`); another type may not carry them. `position` orders the backlog.
Deleting archives. Reading needs any workspace role; creating and changing need the right to work on applications (`ProjectAccess`:
owner, manager, contributor, workspace admin).

**`issue_links`:** `blocks` between two issues of the same project; "blocked by" is the same row seen from the other end. No self-link, no
loop of any length, no duplicate. **`issue_test_cases`:** scenario title, steps, expected result, and the latest result (`not_run`, `passed`,
`failed`, `blocked`) with who set it and when. **`issue_code_links`:** a pull request or commit link (https, no password) added by hand;
the automatic fill from the agent's branch and commit names is a later step.

| Call | Needs |
|---|---|
| `GET /api/projects/{id}/issues` (filters `q`, `type`, `status`, `assignee`, `application`, `component`, `parent`, `release`, `scope=backlog`) and `POST` | read / work |
| `GET /api/issues/{id}`, `PUT`, `DELETE` (archive), `POST /restore`, `PUT /position` | read / work |
| `POST /api/issues/{id}/links`, `DELETE /api/issue-links/{id}` | work |
| `GET`, `POST /api/issues/{id}/test-cases`, `PUT`, `DELETE /api/test-cases/{id}`, `POST /api/test-cases/{id}/result` | read / work |
| `GET`, `POST /api/issues/{id}/code-links`, `DELETE /api/code-links/{id}` | read / work |

## 5. Phase 3: sprints

A sprint belongs to a project and, optionally, to one application (started from the project page or from the application page); it keeps that
scope for life. It has a number (per project, never reused), an optional name, a goal, start and end dates (the project's default sprint
length fills the end date in the form), a capacity in the project's estimation unit, a status, a team and the release it contributes to.

**Status:** `planned` -> `active` -> `closed`. Starting needs both dates and no other active sprint of the same scope (one per project-wide
scope, one per application). A closed sprint is read-only. A planned sprint can be deleted (archived; its issues go back to the backlog).
Closing asks where the unfinished issues (not `done` or `cancelled`) go: to a planned sprint of the same scope, or back to the backlog,
where an `in_progress` or `in_review` issue becomes `ready`.

**Issues:** `issues` gains `sprint_id` (an issue is in at most one sprint). The backlog is the issues in no sprint. Issues are added to a
planned or active sprint from the backlog (many at once) or created new and added; a sprint of an application takes only issues of that
application. Removing one returns it to the backlog. The sprint page groups its issues by status; a quick status change has its own call.
**Team:** workspace members who can work. **Release:** a release of an application of the project (of that application when the sprint
has one). Committed and done estimates are summed from the issues (numeric units only; T-shirt sizes are not summed).

| Call | Needs |
|---|---|
| `GET /api/projects/{id}/sprints` (filters `status`, `application`) and `POST` | read / work |
| `GET /api/sprints/{id}`, `PUT`, `DELETE` (planned only) | read / work |
| `POST /api/sprints/{id}/start`, `POST /api/sprints/{id}/close` | work |
| `POST /api/sprints/{id}/issues` (several ids), `DELETE /api/sprints/{id}/issues/{issueId}` | work |
| `PUT /api/issues/{id}/status` (a quick status change), issue list filter `sprint=<id>`, `scope=backlog` now also means "in no sprint" | work / read |

## 6. Phase 4a: remote runs on issues

A test run (or a build) that an agent ran on a machine (ADR-0005) can be attached to an issue, so "start the test, follow the result, fix what
failed" stays in one place: `issue_runs` (issue, run, who linked, when). The link shows what ran where and how it ended (kind, status, exit code,
the parsed passed, failed and skipped counts, host, dates); **never the command or the output**, which keep the rules of the run itself. A run
must be in the issue's workspace; one pair is linked once; at most 50 runs per issue. The web offers it from the issue page ("attach a run") and
from the run list ("link to an issue", and "create a bug" for a failed test run, which fills a title and links the run; no captured text goes into
the issue). The rights are those of the issue (work on applications); reading follows the run list (any member). Calls: `GET`, `POST
/api/issues/{id}/runs`, `DELETE /api/issues/{id}/runs/{runId}`, `GET /api/runs/{id}/issues`.

**Not in this step (machines and Claude, agreed with the aipod work, ADR-0014 Part B):** starting and stopping Claude and opening sessions from the
web are ADR-0014's; the limit view (5-hour, weekly), plugin and skill install, and per-session remote settings (working mode, branch policy, CI)
need agent-side facts or commands that do not exist yet and are separate decisions.

## 7. Later phases (a plan)

3. **Machines and Claude:** planned in §7a (below).
4. **AI pods** of the account: decided (see "Decided later" in §7a): an AI pod is the aipod daemon, on a server or a desktop.
5. **Administration** (users, system settings) and **profile** pages.

### 7a. Machines and Claude: a plan (19/10/2026)

**Names.** The web is **Podium**; the daemon on a machine is **aipod** (ADR-0012). Texts say "Podium" for the web and "aipod" for the daemon.

**Already there (not rebuilt).** The machine page (ADR-0005, ADR-0008): load, alerts, runs, grants, jobs, schedules, services, projects, asks; the fixed
agent actions `restart`, `update_check`, `update`, `status`; Claude start and stop on demand (ADR-0014 P1: `claude_start`, `claude_stop`, a Claude card,
dev and test machines only, at most 3 workers); runs of kind `test | build | deploy | other` and, since §6, their link to issues.

**Still to build, in this order (one branch and one commit per step, each through the gate).**

| Step | What | Depends on |
|---|---|---|
| A | The new-session form on the Claude card (ADR-0014 P2): project from the machine's reported list, permission mode, optional first prompt (wrapped, labelled as from the web, capped, never audited) | decision 3 |
| B | "Start a test" on the machine page: the project's test command is set at the machine, Podium only picks the project (design below); the existing run approval flow; the result on the same card, with the link to an issue from §6 | decision 9 (made) |
| C | Automatic mode `on_message` (ADR-0014 P3): backoff after a failed start, a start budget per hour, never a second worker for one project. Done on `dev` (19/10/2026) for held asks, in the other session's build of ADR-0014 P3 (which also has the mode `always`); web messages to an ended session are not covered | decision 2 (made: owner only) |
| D | A tool panel per machine: aipod status and update (the existing actions in one card). Done on `dev` (19/10/2026) as the "aipod" card: its facts for every member (version, how it runs, what it may run, last heard) above the owner's fixed actions; a pod is the aipod daemon, on a server or a desktop (decision 8) | decision 8 (made) |
| E | Claude management (sessions list, plugins, skills, limits) and per-session settings (working mode, branch policy, CI) | decisions 5 and 6 (made); read-only parts done, writes and limits below |

**Step B design (maintainer, 19/10/2026: the command is set at the machine, the web only picks the project, the approval flow stays).**
- *At the machine:* `aipod config projects test <folder> -- <program> [args...]` sets the test command of a listed or visited folder (an argv list, no shell; at most 32
  elements of 512 characters), `aipod config projects test <folder> --clear` removes it. It lives in `agent.json` with the folder list; a service agent has no verb for it.
- *Reported:* for a project that has a test command, the agent adds the argv and the folder path to that project's fact. This is a narrow exception to "the folder path never
  leaves the machine" (ADR-0008 §3a′): a run's command and working directory are shown to its approver as text anyway, and the person at the machine chose to publish this
  one command. Projects without a test command keep reporting only the HMAC. The web gets `hasTest` for every member and the command itself only for the machine's owner (as a run's command is).
- *Started:* `POST /api/agents/{id}/test-runs { projectKey }`, the machine's owner only, makes an ordinary argv run (kind `test`, the project key, the reported argv and folder, the
  workspace's default timeout) with the machine as its own requester. From there nothing changes: the workspace switch, the target's execution level, grants and jobs, the owner's approval
  (with a code or a signature where required), the output rules, retention, and the link to an issue (§6). A machine that is not accepting argv runs gets the usual refusal.
- *Not done:* a test command chosen or edited from the web; a shell or script test; a schedule (jobs exist for that).

**Decided (maintainer, 19/10/2026).**
2. **Automatic mode:** only the machine's owner may turn it on.
1. **Windows:** `claude --bg` has to be tried on the Windows server with the list in ADR-0014 ("Windows: what still has to be tried") before anything in A or C is
   called working there; until then the Claude card is documented as macOS-verified only.
3. **Permission mode of an unwatched session:** `manual` is the default and the highest offered; `bypassPermissions`, `auto` and `dontAsk` are never offered from Podium.
7. **A folder Claude does not trust yet:** Podium says "open Claude in that folder once at the machine and accept the trust prompt"; the page does not read
   Claude's own trust list (no access to `~/.claude.json` for this).

**Step E findings: what the agent can read (investigation 19/10/2026; Claude Code 2.1.294 on macOS; read-only commands, nothing changed on the machine; Windows not tried).**

| Wanted | Can it be read? | How, and the catch |
|---|---|---|
| Sessions of Claude | **Yes, now** | `<config dir>/sessions/<pid>.json` (the agent already reads pid and status) and `claude agents --json [--all]` (cwd, kind, name, pid, sessionId, startedAt, status). A list of name, project key, kind and status needs no new command. The session name is text a person or Claude wrote: untrusted, shown as text only. |
| Plugins | **Yes** | `claude plugin list --json`: id, version, scope, enabled, installPath, installedAt, projectEnabled. The install path is under the home folder: keep it out of the upload (as for the `claude` path). `claude plugin details <name>` (component inventory, token cost) has **no JSON**: its text is not a stable format, so it is not parsed. |
| Skills | **Partly** | No list command found. A skill is a folder with a `SKILL.md` in the user skills folder, a project `.claude/skills`, or inside an installed plugin. Listing names means listing folders and reading front matter, which is Claude Code's format, not a promise. A skill's description is untrusted text. |
| Limits (5-hour, weekly) | **Not safely** | Claude Code knows them (five-hour and seven-day use per cent, per model, a spend limit, the subscription type) and shows them in `/usage` and in the status line's JSON (`rate_limits`). They are **not on disk** (searched `state`, `cache`, `sessions`, `settings.json`, plugin data), there is **no CLI command** for them, and the only other source is Anthropic's usage endpoint, which needs the person's Claude login token: aipod holds no Claude secret and must not start to. The one supported way is a **status line command** (`statusLine` in Claude's `settings.json`; it receives the JSON on stdin). This machine has none, so aipod would have to install one or wrap the person's own, which edits Claude's settings and needs the person's consent. **Unverified:** whether a background (`--bg`) session runs a status line, and whether the field exists for an API-key login (the binary has a `rate_limits_available` flag, so probably not). |
| Working mode, branch policy, CI | **Yes, but only for the adyusuf plugin** | These are that plugin's files in the project folder: `.claude/mode`, `.claude/mode-model`, `.claude/adyusuf.json` (merge, push and run-before-push per branch). Small plain files, readable from a listed project folder. They are not a Claude Code feature, so Podium would depend on that plugin. Claude Code's own permission mode is already in the hooks payload (`permission_mode`). |
| Changing any of these from the web | **Possible, not recommended yet** | `claude plugin install`, `enable`, `disable` and `update` exist (an install from a marketplace that declares a command needs `--accept-command <sha256>`), and the files above are writable, but each is a command from the web that changes what runs on, or what may be pushed from, the machine: decision 6. |

**What this makes buildable now (read-only, no new decision):** E1 a sessions list on the machine page (name, project, kind, status); E2 a plugins list (id, version, enabled) with skills by name; E3 the working mode, branch policy and CI of a listed project, read from the three files and shown as text. Each is an additive field of the machine profile, capped and stripped of control characters like every machine fact, and shown to the machine's owner and admins only. **Needs a decision first:** limits (decision 5) and anything that writes (decision 6).

**E1 to E3 as built (19/10/2026, maintainer: only the owner and admins see them).** The daemon reads the parts every two minutes (the plugin list at most every ten, the only process it starts is `claude plugin list --json`) and puts them in its profile as `inventory`; a change in content, not the time of reading, makes a new profile. The API re-checks and stores one document per machine (migration `ClaudeInventory`, `data-model.md`) and sends it in the machine detail to the owner and admins; Podium shows a "Claude on this machine" card: sessions (name, project, kind, status, start), plugins (id, version, enabled), skill names, and per project the working mode, model and gates. Nothing in it can be changed from the web, no skill text or plugin path is read or sent, and a list the agent could not read is shown as unknown, not as empty. **Owner's switch (maintainer, 19/10/2026):** off by default; the owner turns it on with a recent sign-in (`PUT /api/agents/{id}/inventory`, audited, like listening services). While it is off the daemon reads nothing (the API tells it through the settings pass), the API stores nothing, and turning it off drops what was stored; moving the machine to another workspace and deleting the account clear it too. Not verified on a real machine with a running daemon, and not on Windows.

**Still open.**
Nothing else is open in this plan: decisions 4, 5, 6 and 8 were made on 19/10/2026 (below).

**Decided later (maintainer, 19/10/2026).**
4. **Cost:** a session started from Podium, by hand or by the automatic mode, runs on the Claude plan of the person the machine's agent belongs to and counts against their limits. Accepted; the Claude card says so in words (with that person's name).
8. **AI pod:** an AI pod is the aipod daemon, nothing more. It can run on a server (service mode, a boot service) or on a desktop (interactive, in a person's session); both are first-class. Podium calls a machine a "server" pod when its agent runs as a service and a "desktop" pod otherwise, on the aipod card. "The AI pods of the account" are the machines of the account across its workspaces; there is no separate server-side object, and no page across workspaces is built.
6. **Remote writes: yes, as a closed set (design below).**
5. **Limits: yes, through a status line collector that the person sets up at the machine (design below).**

**Remote writes (decision 6, as built 19/10/2026).** New action kinds, with no free text beyond a checked id: `plugin_enable`, `plugin_disable`, `plugin_update`, `plugin_install` (target: `name@marketplace`) and `project_setting` (project key from the machine's list, a setting name from a closed list, a value that fits that setting). Everything of a Claude start from the web applies: the workspace's remote-work switch, the owner's Claude switch, an admin-set dev or test machine with no stricter claim, no service agent, Claude on the machine, owner only, and a recent sign-in or a two-step code for every one. The agent runs only Claude's own commands with a fixed shape (`claude plugin enable|disable|update|install <id>`, never `--accept-command`: a marketplace that declares a command is installed at the machine by a person) and refuses an id that `claude plugin list` does not show (for an install: that the machine's own marketplaces do not offer). A setting is written to the adyusuf files of that project folder only (`.claude/mode`, `.claude/mode-model`, `.claude/adyusuf.json`), by the same checks the reader uses, atomically; it changes the working tree, so the person commits it. The result text and an audit row (kind, target, value; never more) say what happened. A skill has no install of its own in Claude Code: skills arrive with plugins, so installing, enabling or disabling a plugin is how Podium changes them. Not verified on a real machine; not on Windows.

**Limits (decision 5, as built 19/10/2026).** A plugin cannot ship a `statusLine` (only `agent` and `subagentStatusLine` take effect), and `rate_limits` appears only in the status line's input (claude.ai Pro and Max logins, or behind a gateway with a spend limit; only after the first answer of a session). So the person sets up a collector at the machine: `aipod config claude-statusline on` makes `statusLine` in Claude's user settings call `aipod statusline`, which writes the two windows (`five_hour`, `seven_day`: use per cent and reset time) to a user-only file in the aipod home and prints the person's previous status line, if they had one (it is kept and restored by `off`). Nothing is changed without that command. The daemon reads the newest file and reports it with the inventory, under the same owner switch; Podium shows two bars with the time they were last seen and says "no data" for an API-key login or a machine where no session has answered yet. It runs only while a session with a status line is active: **unverified** whether a background (`--bg`) session runs one, and not yet tried in a real interactive session (the tests feed it the documented payload). The inventory carries the limits under the owner's switch; Podium draws one meter per window with its reset time and when it was last seen.

## 8. Not decided here

Whether the `repos` rows should link to an application by its repository link; account export and deletion for the new tables (the rows
keep the deleted user's id, like other records, and carry no personal fields of their own); attachments storage (mockups, screenshots, logs, screen recordings: needs a decision on where files live, so Phase 2 has none).
