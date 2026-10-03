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
| `workspace_settings` | `workspace_id`, `mask_secrets` (default true), `retention_days` (default 90), `event_max_bytes`, `updated_at`, `updated_by` | PK `workspace_id` |

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

## Account deletion

`users.status = 'deleted'` starts it: personal fields are cleared (e-mail, name, password hash, provider
links), tokens and login sessions are revoked, the user's agents are revoked, and the captured content of the
user's sessions is deleted. Audit rows keep the user id only. Workspace data the user created that others still
use stays with the workspace. (The legal side is open in ADR-0002.)
