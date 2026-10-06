# Data model (PostgreSQL 18)

The central database behind the API ([ADR-0002](adr-0002-agent-platform.md)). This is the design;
the EF Core migrations of phase 1 implement it and this file follows them.

## Conventions

- **Keys:** `uuid` filled by `uuidv7()` (time-ordered). The event table uses `bigint` identity.
- **Time:** `timestamptz`, always UTC. Shown as `dd/mm/yyyy` on the web (global #12).
- **Additive only** (global #4): no column or table is dropped, renamed or retyped. A row is retired by a
  status or a `*_at` column, not deleted, except where retention or an account deletion requires it.
- **Closed sets** (a status, a role) are `text` with a `CHECK`; the code holds the same values as an enum
  with a `default` branch (global #11). A set that grows with the product (`harness_kinds`) is a table.
- **Tokens and codes** are stored as SHA-256 hashes, never in clear.
- **Search** is case- and accent-insensitive (global #13): one normaliser in the API fills the `*_search`
  columns, and searches compare normalised text.
- **Paging** is by key (`last_event_at`, `id`), newest first; no list is unbounded.
- **Foreign keys** on every reference, none cascading.
- **Authorisation:** every team-owned row carries `workspace_id`, and every query filters by the caller's
  membership. Nothing is readable without it (global #6).

## 1. Accounts

| Table | Columns | Keys and indexes |
|---|---|---|
| `users` | `id`, `email`, `email_normalized`, `display_name`, `display_name_search`, `password_hash` (null when only a provider is used), `email_verified_at`, `status` (`active`, `disabled`, `deleted`), `created_at`, `updated_at` | UNIQUE `email_normalized` |
| `user_logins` | `id`, `user_id`, `provider` (`github`, `google`), `provider_subject`, `provider_email`, `linked_at` | UNIQUE (`provider`, `provider_subject`) |
| `user_tokens` | `id`, `user_id`, `purpose` (`verify_email`, `reset_password`), `token_hash`, `created_at`, `expires_at`, `used_at` | UNIQUE `token_hash` |
| `login_sessions` | `id`, `user_id`, `token_hash`, `created_at`, `expires_at`, `last_seen_at`, `revoked_at`, `user_agent` | UNIQUE `token_hash`; (`user_id`) |

## 2. Workspaces

| Table | Columns | Keys and indexes |
|---|---|---|
| `workspaces` | `id`, `name`, `name_search`, `created_by`, `created_at`, `status` (`active`, `archived`) | |
| `workspace_members` | `workspace_id`, `user_id`, `role` (`owner`, `admin`, `member`, `viewer`), `joined_at`, `removed_at` | PK (`workspace_id`, `user_id`); (`user_id`) |
| `workspace_invitations` | `id`, `workspace_id`, `email_normalized`, `role`, `token_hash`, `invited_by`, `created_at`, `expires_at`, `accepted_at`, `revoked_at` | UNIQUE `token_hash`; (`workspace_id`) |
| `workspace_settings` | `workspace_id`, `mask_secrets` (default true), `retention_days` (default 90), `event_max_bytes`, `agent_update` (`off` default, `check`, `on`; CHECK `ck_workspace_settings_agent_update`; the most an agent may do about updating itself, ADR-0004), `updated_at`, `updated_by` | PK `workspace_id` |

A workspace always has at least one owner; the API refuses the change that would leave it without one.

## 3. Machines and agents

| Table | Columns | Keys and indexes |
|---|---|---|
| `machines` | `id`, `workspace_id`, `machine_key_hash`, `hostname`, `os` (`macos`, `windows`), `os_version`, `arch`, `first_seen_at`, `last_seen_at` | UNIQUE (`workspace_id`, `machine_key_hash`) |
| `agents` | `id`, `machine_id`, `user_id`, `workspace_id`, `version`, `status` (`active`, `revoked`), `enrolled_at`, `last_heartbeat_at`, `revoked_at`, `revoked_by` | UNIQUE (`machine_id`, `user_id`) WHERE `status = 'active'`; (`workspace_id`) |
| `agent_tokens` | `id`, `agent_id`, `kind` (`access`, `refresh`), `token_hash`, `created_at`, `expires_at`, `replaced_by`, `revoked_at`, `last_used_at` | UNIQUE `token_hash`; (`agent_id`) |
| `device_authorizations` | `id`, `device_code_hash`, `user_code`, `machine_key_hash`, `requested_hostname`, `requested_os`, `requested_arch`, `agent_version`, `status` (`pending`, `approved`, `denied`, `expired`, `consumed`), `workspace_id`, `approved_by`, `created_at`, `expires_at`, `last_polled_at` | UNIQUE `device_code_hash`; UNIQUE `user_code` WHERE `status = 'pending'` |

`machine_key_hash` is a hash of a per-installation random id kept by the agent, not a hardware serial.
A machine may run several agents, one per OS user. A refresh token is single-use: using it issues a new
pair and marks the old one `replaced_by`; a reused, already-replaced refresh token revokes the agent's tokens.

## 4. Sessions and what they report

| Table | Columns | Keys and indexes |
|---|---|---|
| `harness_kinds` | `code` (`claude_code`, `codex_cli`, `gemini_cli`, `cursor`, ...), `display_name`, `added_at` | PK `code` |
| `projects` | `id`, `workspace_id`, `key` (normalised git remote, or a hash of the folder name when there is none), `display_name`, `display_name_search`, `created_at` | UNIQUE (`workspace_id`, `key`) |
| `harness_sessions` | `id`, `workspace_id`, `agent_id`, `project_id`, `harness_kind`, `external_id`, `title`, `title_search`, `model`, `git_branch`, `status` (`active`, `idle`, `waiting`, `ended`), `started_at`, `last_event_at`, `ended_at` | UNIQUE (`agent_id`, `harness_kind`, `external_id`); (`workspace_id`, `last_event_at` DESC, `id` DESC); (`project_id`, `last_event_at` DESC) |
| `session_tasks` | `id`, `session_id`, `external_id`, `subject`, `subject_search`, `status` (`pending`, `in_progress`, `completed`, `deleted`), `created_at`, `updated_at` | UNIQUE (`session_id`, `external_id`) |
| `subagent_runs` | `id`, `session_id`, `external_id`, `agent_type`, `description`, `status` (`running`, `finished`, `failed`), `started_at`, `ended_at` | UNIQUE (`session_id`, `external_id`) |
| `session_usage` | `session_id`, `model`, `input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_write_tokens`, `cost_usd` `numeric(12,6)` (null when the model has no known price), `updated_at` | PK (`session_id`, `model`) |
| `agent_batches` | `agent_id`, `batch_seq`, `event_count`, `bytes`, `received_at` | PK (`agent_id`, `batch_seq`) |
| `session_events` | `id`, `session_id`, `workspace_id`, `kind`, `occurred_at`, `received_at`, `payload` `jsonb`, `truncated` | partitioned by month on `received_at`; PK (`id`, `received_at`); (`session_id`, `occurred_at`) |

- **`session_events` is the record**; tasks, subagent runs, usage and the session's status are projections
  updated in the same transaction as the batch that carries the events.
- **Idempotency** sits in `agent_batches`, not in the event table: a unique key on a partitioned table must
  include the partition key, so it cannot catch a retried batch that lands in another month.
- **Retention:** every few hours a job writes each workspace-day older than `retention_days` (UTC, by `received_at`)
  to one zipped JSON array, records it in `event_archives`, and deletes those rows in the same transaction.

| Table | Columns | Keys and indexes |
|---|---|---|
| `event_archives` | `id`, `workspace_id`, `day`, `path`, `event_count`, `bytes`, `sha256`, `created_at` | (`workspace_id`, `day`); several parts per day are allowed |
- Project folder paths are **not** stored: they carry the OS user name.

## 5. Commands from the web

| Table | Columns | Keys and indexes |
|---|---|---|
| `session_commands` | `id`, `workspace_id`, `session_id`, `agent_id`, `kind` (`prompt`, `stop`), `body`, `created_by`, `created_at`, `expires_at`, `status` (`queued`, `delivered`, `applied`, `failed`, `expired`, `cancelled`), `delivered_at`, `applied_at`, `result` | (`agent_id`, `status`) WHERE `status IN ('queued', 'delivered')`; (`session_id`, `created_at` DESC) |

| `permission_requests` | `id`, `workspace_id`, `session_id`, `agent_id`, `tool_name`, `tool_input` `jsonb`, `status` (`open`, `answered`, `expired`), `created_at`, `expires_at`, `decision` (`allow`, `deny`), `reason`, `answered_by`, `answered_at` | (`session_id`, `status`) |

The API accepts a command or a permission answer only from the session's owner (`agents.user_id` of the
session's agent).

**Claude's answer to a command is a view, not a status.** `applied` means the hook handed the text to the session; it
does not say Claude has answered. `GET /sessions/{id}/commands` adds three fields to each row, computed from
`session_events` and never stored: `replyEventId` (the event), `replyText` (its first 300 characters) and `replyMore`
(the message goes on). A command's reply is the first `transcript` event of the same session that is an assistant
message with a non-blank text block and was written after the command's `applied_at`. "Written" is the transcript
line's own `timestamp`, else the event's `occurred_at` (the agent stamps an event with the moment it READ the line,
which can be later). Only an `applied` `prompt` has one; two commands applied together share the first reply written
after them; an event shortened to a marker (`truncated`) carries no message and is skipped. The web shows
Applied -> "waiting for Claude's reply" -> "Claude replied" from these fields; `session_commands.status` keeps its
six values. `applied_at` is the moment the hook handed the command to the session: the agent sends it as `at` with its
`applied` report (the report itself can come seconds later) and the API believes it unless it is older than the command
(give or take `ClockSkewMax`, 2 minutes), never later than the report; an older agent sends none and `applied_at` is the
report's time. The search reads at most `ReplyScanMax` events (500) from the oldest applied command in the page.

## 6. Audit

| Table | Columns | Keys and indexes |
|---|---|---|
| `audit_events` | `id` (`bigint`), `workspace_id` (null for account-level events), `actor_user_id`, `actor_agent_id`, `action`, `target_type`, `target_id`, `at`, `ip_hash`, `detail` `jsonb` | (`workspace_id`, `at` DESC); (`actor_user_id`, `at` DESC) |

Written for sign-in and its failures, provider linking, password changes, invitations and role changes,
device approvals, token revocation, settings changes and every session command. Never contains captured
content, passwords or tokens.

## Relationships

```
users 1-n workspace_members n-1 workspaces 1-1 workspace_settings
users 1-n user_logins, user_tokens, login_sessions
workspaces 1-n machines 1-n agents n-1 users          agents 1-n agent_tokens, agent_batches
workspaces 1-n projects 1-n harness_sessions n-1 agents
harness_sessions 1-n session_events, session_tasks, subagent_runs, session_usage, session_commands, permission_requests
workspaces 1-n event_archives
```

## Your data: export and account deletion

- **Export** (`GET /api/me/export`): one JSON file, streamed: the profile, sign-in providers, memberships, machines,
  commands sent, the user's own audit trail, and every session run on the user's machines with its tasks, usage and
  events.
- **Deletion** (`POST /api/me/delete`, with the password when there is one and the word `DELETE`): refused (409
  `sole_owner`) while the user is the only owner of a workspace that has other members. Otherwise, in one transaction:
  the personal fields are cleared (`users.email`, name, password hash; status `deleted`), provider links deleted,
  login sessions, mail tokens, agents and agent tokens revoked, open invitations to the address revoked, memberships
  ended, workspaces with no other member archived; the captured content of the user's sessions is deleted (events,
  tasks) or emptied (titles, subagent descriptions, command bodies, permission inputs). Ids, times, usage and audit
  rows stay, so other people's records still make sense. The address is free for a new sign-up.
- **Archives too:** the day files already written past retention are rewritten without the user's sessions' events
  (a file left empty is deleted with its row), and their `event_archives` rows get the new count, size and SHA-256.
  Copies already taken off the server by the backups age out with the backups' own retention.
