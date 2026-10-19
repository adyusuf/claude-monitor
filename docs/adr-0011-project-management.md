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
4. **AI pods** of the account: needs its own decision first (what a pod is on the server side).
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
| C | Automatic mode `on_message` (ADR-0014 P3): backoff after a failed start, a start budget per hour, never a second worker for one project | decision 2 |
| D | A tool panel per machine: aipod status and update (the existing actions in one card) | decision 8 |
| E | Claude management (sessions list, plugins, skills, limits) and per-session settings (working mode, branch policy, CI) | decisions 5, 6; needs facts and commands in the agent that do not exist yet |

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
1. **Windows:** `claude --bg` has to be tried on the Windows server with the list in ADR-0014 ("Windows: what still has to be tried") before anything in A or C is
   called working there; until then the Claude card is documented as macOS-verified only.
3. **Permission mode of an unwatched session:** `manual` is the default and the highest offered; `bypassPermissions`, `auto` and `dontAsk` are never offered from Podium.
7. **A folder Claude does not trust yet:** Podium says "open Claude in that folder once at the machine and accept the trust prompt"; the page does not read
   Claude's own trust list (no access to `~/.claude.json` for this).

**Still open.**
2. Who may turn the automatic mode on: the machine's owner only (proposed) or also a workspace admin.
4. The cost of a started session is the machine owner's Claude plan: accepted, and does the page say so?
5. The limit view (5-hour, weekly): where the numbers come from. Nothing in aipod reads them today; a source would have to be found and measured first, or the view dropped.
6. Remote plugin and skill install, and changing a session's settings from Podium: a new attack surface (a command from the web that changes what runs on the machine). Only with
   a closed set of actions and its own section here; otherwise read-only lists.
8. What an "AI pod" is on the server side (item 4 above); step D only gathers the existing aipod actions until this is decided.

## 8. Not decided here

Whether the `repos` rows should link to an application by its repository link; account export and deletion for the new tables (the rows
keep the deleted user's id, like other records, and carry no personal fields of their own); attachments storage (mockups, screenshots, logs, screen recordings: needs a decision on where files live, so Phase 2 has none).
