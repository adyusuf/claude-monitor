# ADR-0015: Work tracker integrations (Jira first)

**Status:** Plan approved by the maintainer (20/10/2026). Built in two parts on one branch, merged together: Part 1 the read path (§4, §6, §8), Part 2 the write path (§7). Not run against a real Atlassian site yet (§3, last point).
**Extends:** [ADR-0011](adr-0011-project-management.md) (projects, issues, sprints), [data-model.md](data-model.md) §8.
**Asked for (20/10/2026):** connect a Jira Cloud site by its sub-domain; list its spaces and import chosen ones as projects, with their backlog; list the
sprints of an imported space and import chosen ones, with their issues; bring the people of an imported space along; keep all of it up to date
automatically (use Jira's webhooks where they exist). Other project tools will follow, so nothing in the design may be Jira-only except one adapter.

## 1. Decisions (maintainer, 20/10/2026)

| # | Decision |
|---|---|
| 1 | **Two-way.** A change in Podium to a mirrored project, issue or sprint is written to Jira first and kept locally only when Jira accepts it (Part 2). |
| 2 | **OAuth 2.0 (3LO) app**, not an API token. One Atlassian app per deployment (`AIPOD_JIRA_CLIENT_ID` / `_SECRET`). |
| 3 | **Every user writes with their own Atlassian account.** A person who has not linked one cannot write to Jira (409 `jira_account_required`); reading and syncing use the account of whoever connected the site (the *connector*). |
| 4 | **People: automatic invitation.** A Jira person whose e-mail is visible and not yet a member gets one workspace invitation, as `viewer`; a person who already is a member is linked without mail. A person with no visible e-mail is listed as "no e-mail" and an admin can type one (a member with that address is linked on the next sync). The space's lead who is a workspace member becomes the project's Manager on import, without a confirmation; that grant and every link made by e-mail are audited, so an admin sees and can undo them. Per-connection switch `auto_invite` (default **off** until an admin turns it on, because a test environment connected to a real site would send real mail). |
| 5 | **New Jira sprints are not imported by themselves.** The sprint list shows them as "not imported"; an issue that moves into an unimported sprint is shown as belonging to a sprint that is not in Podium. |

## 2. Shape: one adapter per tool

`IWorkTracker` (namespace `Podium.Api.Integrations`) is the only thing the import and sync code knows. It lists *containers* (a Jira space), *iterations*
(a sprint), *items* (an issue, the backlog of a container or the items of an iteration), *people*, and verifies and parses the tool's webhook. It returns plain records
(`ExternalContainer`, `ExternalIteration`, `ExternalItem`, `ExternalPerson`) with the tool's own ids as strings. `ImportService`, `SyncService` and the web
talk only to those records and to `integration_links`. A second tool is one new `IWorkTracker` plus its mapping table. `provider` is a closed text set (`jira`).

Rows are tied to a tool by `integration_links` (connection, kind, external id → Podium id), not by a column per tool. Two columns are on `issues` because lists show them:
`external_key` (`ABC-12`) and `external_url`.

## 3. Jira facts the design rests on (read from Atlassian's documentation, 20/10/2026)

- Sites: `GET https://api.atlassian.com/oauth/token/accessible-resources` (needs `read:me`-class scope) gives `cloudId` and the site URL; calls go to
  `https://api.atlassian.com/ex/jira/{cloudId}/rest/api/3/...` and `.../rest/agile/1.0/...`. The sub-domain the maintainer gives picks the site; it must be
  `<label>.atlassian.net` (letters, digits, hyphen) and matches `accessible-resources` exactly. No other host is ever called with a token (SSRF).
- Spaces: `GET /rest/api/3/project/search` (paged `startAt`). Boards of a space: `GET /rest/agile/1.0/board?projectKeyOrId=`. Sprints: `GET /rest/agile/1.0/board/{id}/sprint`.
  Backlog: `GET /rest/agile/1.0/board/{id}/backlog`. Sprint issues: `GET /rest/agile/1.0/sprint/{id}/issue`. Search by JQL: `POST /rest/api/3/search/jql` with `nextPageToken`
  (the old `/search` is gone; there is no total).
- Webhooks for an OAuth app are the *dynamic* ones: `POST /rest/api/3/webhook` (issue, comment, sprint and version events only; no board, project or user events), at most
  5 per app and user, **expire after 30 days** and are extended with `PUT /rest/api/3/webhook/refresh`. Delivery is at least once, retried up to five times, may repeat
  (`X-Atlassian-Webhook-Identifier`), and may be late. The request carries `Authorization: Bearer <JWT>` signed (HS256) with the app's client secret.
- Rate limits answer 429 with `Retry-After`; the client waits and retries a bounded number of times.
- Jira hides most e-mail addresses (profile visibility): a missing `emailAddress` is normal.
- **Not verified on a real site** (no Atlassian app exists yet): the exact JWT claims of a webhook delivery, the granular scope names, and the sprint-event payloads. The code is built
  against fake HTTP only (tests never touch a real account) and the webhook handler trusts nothing in a body: a valid call only says *"this issue or sprint changed"*, and the
  record is read again from Jira. The reconciliation pass (§6) covers a webhook that never arrives.

## 4. Mapping

| Jira | Podium |
|---|---|
| space (project) | `projects` row; the connector's Podium user owns it; `source_connection_id` set; settings: all five item types, `one_to_five` priority, `story_points` |
| Epic / Story / Task / Bug / Sub-task / other | `epic` / `story` / `task` / `bug` / `task` / `task`; a sub-task's parent (a story) is not an epic, so it has no parent in Podium; its first description line says "Sub-task of ABC-3" |
| priority name | Highest 1, High 2, Medium 3, Low 4, Lowest 5; unknown → none |
| status category + name | *To Do* → `backlog` (in no sprint) or `ready` (in a sprint); *In Progress* → `in_progress`, or `in_review` when the name contains "review"; *Done* → `done`, or `cancelled` when the resolution is "Won't Do", "Cannot Reproduce", "Duplicate" or "Declined" |
| story points field | `estimate` (the field is found by name in `GET /field`: "Story point estimate" or "Story Points") |
| description (ADF) | plain text (≤ 20 000 characters); shown as text only |
| rank | `position` (the order Jira returns is kept, spaced by 1024) |
| sprint (`future` / `active` / `closed`) | `planned` / `active` / `closed`; start and end dates; goal; name; number from the Jira sprint order |
| issue in several sprints | the newest one |
| deleted issue, deleted or archived space | archived in Podium (never deleted) |

Imported text is untrusted and capped like any text a person types (names 200, summary 250, description 20 000). The Jira sprint rule "several active sprints" does not fit
Podium's one-active-sprint-per-scope index; the index is **narrowed** (an additive change of a unique index) so that sprints with `source_connection_id` set are left out of it.

## 5. Data (see data-model.md §8)

`integration_connections` (one per workspace and site), `integration_accounts` (a user's own Atlassian login on a connection: sealed refresh token and access token, expiry),
`integration_oauth_states` (login in progress: hash of the state, PKCE verifier sealed, purpose, expiry), `integration_links`, `integration_people`,
`integration_sync_runs` (counts, status, error code; no text), `integration_webhook_receipts` (delivery id, 7 days). Added columns: `projects.source_connection_id`, `issues.external_key`,
`issues.external_url`, `issues.assignee_person_id`, `sprints.source_connection_id`. All additive; nothing is dropped or retyped.

Tokens are sealed with `AIPOD_INTEGRATION_KEY` (AES-GCM, the same `SecretBox` as the two-step secrets, a different key), never logged, never sent to the web, never part of an audit row.
A refresh token rotates on every use; the new one is stored in the same transaction as the use. A token that Atlassian refuses marks the account `reauthorize` and the page says so.

## 6. Keeping up to date

1. **Webhook** `POST /api/integrations/jira/webhook/{connectionId}`: no session; the JWT is verified with the client secret (constant time, `exp` checked), the body is capped at 256 KiB, the
   delivery id is recorded once, and the answer is 200 at once (a token without `exp` is accepted, F-075, so a connection may leave at most 3000 new receipts an hour; past that a
   delivery is answered 429, recorded and queued nowhere, and the pass reads what it named). The handler reads only *which* issue key or sprint id changed and queues "read it again" in memory (`SyncQueue`); the
   worker fetches the record with the connector's token and applies it. A lost queue (restart) costs nothing: pass 2 finds it.
2. **Reconciliation** every 10 minutes per active connection (and once at start): issues of imported spaces with `updated >= last pass − 5 minutes`, the sprints of imported sprints' boards, the space list
   (a vanished space, like a vanished issue, is archived on the second pass in a row that misses it, F-066; an empty or cut issue listing archives nothing), the people of imported spaces. It is the only update path on a machine that Jira cannot reach (development) and the net under pass 1.
3. **Webhook upkeep:** the pass registers the webhook when the first space is imported (two registrations: issue events filtered by `project in (...)`, and sprint events), re-registers when the
   set of spaces changes, and extends it every 10 days (30-day expiry). A site that refuses webhooks keeps working on pass 2 alone; the page shows "webhook: off".
4. **Echo:** a write Podium made (Part 2) records the Jira `updated` value it got back; the webhook or pass that reads the same value changes nothing.

## 7. Write path (Part 2)

A change in Podium to a mirrored row is sent to Jira with the acting user's own token first; the local row changes only when Jira answers success, and Jira's answer (its `updated`) is stored.
- *Two-way fields:* title, description (text → ADF), priority, estimate, status (the transition whose target status category and name fit; none → 409 `jira_transition_unavailable`), assignee (when linked to a Jira person), parent epic,
  sprint membership and rank, new issue, sprint name, goal, dates, start and close.
- *Podium-only fields* (kept locally, never sent): acceptance criteria, technical design, bug fields, test cases, code links, runs, application, component, release, project settings.
- *Locked on a mirrored row:* the type (Jira needs a "move"), and archiving an issue (Jira's delete cannot be undone: use status `cancelled`).
- *Locks and write-through end with the connection (F-106, F-262):* they apply while the project's connection is not disconnected (one that only needs a new sign-in still locks, it comes back). After a disconnect the project's name and purpose, and its issues' type, archiving and every field, are Podium's to edit, and a new issue is made here without a call to Jira. The project keeps its source link until `POST /api/projects/{id}/detach` (the project's owner or a workspace admin, audited as `project.detached`, only after the disconnect: 409 `mirrored_connected` before it, 409 `not_mirrored` when there is nothing to detach) clears `SourceConnectionId` and the project's link row, so no sync takes it back. Mirrored sprints keep their own source and stay refused as detached.
- *Conflict:* the write carries the `updated` Jira had at the last sync; if Jira has a newer one the answer is 409 `jira_changed` and nothing is written.
- *Rights:* the same as for any issue, plus a linked Atlassian account; Jira's own permissions have the last word and their refusal is passed on as 403 `jira_forbidden`.

## 8. API (Part 1)

| Call | Needs |
|---|---|
| `GET /api/workspaces/{ws}/integrations` | any member |
| `POST /api/workspaces/{ws}/integrations/jira/connect { subdomain }` → `{ authorizeUrl }` | workspace admin or owner, recent sign-in |
| `GET /api/integrations/jira/callback?code&state` | the user who started it (state) → redirects to the web |
| `POST /api/integrations/{id}/account/connect` → `{ authorizeUrl }` (link my own account) | member of the workspace |
| `DELETE /api/integrations/{id}` (disconnect: tokens and webhook go; imported rows stay, marked detached) | admin, recent sign-in or code window (F-081) |
| `POST /api/integrations/{id}/disconnect` `{ code }` (the same disconnect, with the two-step code when the sign-in is not recent; the web uses it) | admin, recent sign-in or two-step code (F-081) |
| `POST /api/projects/{id}/detach` (cut an imported project of a disconnected connection loose for good) | project owner or workspace admin |
| `PUT /api/integrations/{id}` `{ autoInvite }` | admin |
| `GET /api/integrations/{id}/spaces?skip&take` (live list, with "imported" flags; at most 500, `X-Truncated: true` when rows were left out, F-077) | any member |
| `POST /api/integrations/{id}/spaces/import { keys[] }` | admin or owner |
| `GET /api/integrations/{id}/spaces/{key}/sprints` | any member |
| `POST /api/integrations/{id}/spaces/{key}/sprints/import { sprintIds[] }` | project manager |
| `GET /api/integrations/{id}/people?skip&take` (at most 1000, `X-Truncated: true` when rows were left out, F-077), `PUT /api/integrations/people/{personId} { email?, userId? }` | admin |
| `POST /api/integrations/{id}/sync` (a pass now) | admin |
| `GET /api/integrations/{id}/runs` | any member |
| `PUT /api/integrations/people/{personId} { email?, userId? }` | admin |
| `POST /api/integrations/jira/webhook/{connectionId}` | the JWT only |

The same `skip` and `X-Truncated: true` serve the machine lists outside this ADR (F-024): `GET /api/workspaces/{id}/agents?skip` and `GET /api/workspaces/{id}/machines?skip` answer at most 500 rows as a bare array, ordered with the agent id as the last tiebreak so pages neither overlap nor miss a row; a negative `skip` is 400 `invalid_paging`, a list of up to 500 rows without `skip` answers as before.

Errors are i18n keys. Audit rows (`integration.*`) name ids and counts, never Jira text or tokens.

## 8a. Built differently from the plan

- `AIPOD_INTEGRATION_KEY` is optional outside development: without it (or without the Jira app values) the integration is off and existing deployments start unchanged.
- A webhook delivery is checked by its HS256 token against the app's client secret and queued in memory (`SyncInbox`); the receipt table stops a repeat. Webhook registration needs
  a public `https` origin and is skipped (live updates off) otherwise.
- Closing a mirrored sprint first moves its unfinished issues in Jira (to the chosen sprint or the backlog), then closes it. A failed step after another succeeded is repaired by the next webhook or pass.
- Making a mirrored sprint writes to Jira first, then saves here. When the local save fails after Jira made the sprint, the one Jira made is deleted again (once; when that delete fails too it is logged with the tool's sprint id and the first error is still the answer). When Jira refuses the make, nothing was stored here.
- A person who is assigned in Jira but is no Podium member shows as the assignee by name; the form cannot clear that assignment by sending "nobody".
- Account deletion removes the person's Atlassian sign-ins and their links to Jira people; a connection they alone read with goes to "sign in again".

## 9. Not decided here

A second tool (Linear, Azure DevOps, GitHub Projects) and its mapping; comments, attachments, time entries and links between issues (not imported); Jira's custom fields other than the estimate; a per-space choice of
which board's sprints to use when a space has several boards (all boards of the space are listed and a sprint is imported once).
