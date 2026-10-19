# ADR-0011: Project management in the web app

**Status:** Phase 1 is on `dev` (19/10/2026); Phase 2 (§4) is being built. Later phases are a plan, not a decision.
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

## 5. Later phases (a plan)

3. **Sprints:** number, dates, goal, capacity, state, team, release; start from a project or an application; add existing or new issues;
   the issues of a sprint by status.
4. **Machines:** the machine pages exist (ADR-0005, ADR-0008). Open: a per-tool panel on a machine (start, stop, update for the AI pod,
   Claude management: sessions, plugins, skills, limits), per-session settings (working mode, branch policy, CI), starting a test and
   following its result.
5. **AI pods** of the account: needs its own decision first (what a pod is on the server side).
6. **Administration** (users, system settings) and **profile** pages.

## 6. Not decided here

Whether the `repos` rows should link to an application by its repository link; account export and deletion for the new tables (the rows
keep the deleted user's id, like other records, and carry no personal fields of their own); attachments storage (mockups, screenshots, logs, screen recordings: needs a decision on where files live, so Phase 2 has none).
