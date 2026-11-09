# Data model: remote work

Moved out of [data-model.md](data-model.md) §3b to keep it under 300 lines; the conventions there apply.

## Remote work ([ADR-0005](adr-0005-remote-work.md))

| Table | Columns | Keys and indexes |
|---|---|---|
| `machine_metrics` | `agent_id`, `sampled_at`, `workspace_id`, `received_at`, `cpu_pct`, `mem_used_bytes`, `mem_total_bytes`, `disks` `jsonb` (at most 16 `{mount, usedBytes, totalBytes}`) | PK (`agent_id`, `sampled_at`); BRIN (`sampled_at`) |
| `machine_alerts` | `id`, `workspace_id`, `agent_id`, `kind` (`cpu`, `memory`, `disk`, `offline`), `subject` (the mount, or `''`), `state` (`open`, `resolved`), `threshold_pct`, `last_value`, `peak_value`, `opened_at`, `updated_at`, `resolved_at` | UNIQUE (`agent_id`, `kind`, `subject`) WHERE `state = 'open'`; (`workspace_id`, `opened_at` DESC, `id` DESC) |
| `remote_runs` | `id`, `workspace_id`, `requester_agent_id`, `requester_user_id`, `requester_session_id`, `target_agent_id`, `target_user_id`, `client_key`, `mode` (`argv`, `shell`), `argv` `text[]`, `shell_command`, `cwd`, `resolved_exe`, `timeout_seconds` (1-3600), `reason`, `status` (`pending_approval`, `approved`, `delivered`, `running`, `succeeded`, `failed`, `timed_out`, `denied`, `expired`, `cancelled`), `created_at`, `expires_at`, `decided_at`, `decided_by`, `delivered_at`, `started_at`, `ended_at`, `exit_code`, `error`, `output_bytes`, `output_truncated`, `grant_id`, `job_id` | UNIQUE (`requester_agent_id`, `client_key`); (`target_agent_id`, `status`) WHERE live; (`target_user_id`, `created_at`) WHERE `status = 'pending_approval'`; (`target_agent_id`, `created_at` DESC, `id` DESC); (`workspace_id`, `created_at` DESC) |
| `remote_run_output` | `run_id`, `seq`, `stream` (`stdout`, `stderr`), `body`, `bytes` (1-65536), `gap_before`, `received_at` | PK (`run_id`, `seq`) |
| `machine_grants` | `id`, `workspace_id`, `target_agent_id`, `owner_user_id`, `grantee_user_id`, `grantee_agent_id`, `requested_by_agent_id`, `template` `text[]`, `cwd`, `template_hash`, `reason`, `status` (`requested`, `active`, `denied`, `revoked`, `expired`), `created_at`, `expires_at` (at most 90 days), `decided_at`, `decided_by`, `revoked_at`, `revoked_by`, `use_count`, `last_used_at` | (`target_agent_id`, `grantee_user_id`) WHERE `status = 'active'`; UNIQUE (`target_agent_id`, `grantee_user_id`, `template_hash`) WHERE `status IN ('requested', 'active')` |
| `machine_jobs` | `id`, `workspace_id`, `target_agent_id`, `owner_user_id`, `name`, `name_search`, `argv` `text[]`, `cwd`, `timeout_seconds`, `status` (`proposed`, `active`, `denied`, `retired`), `proposed_by_user_id`, `proposed_by_agent_id`, `created_at`, `decided_at`, `decided_by`, `retired_at` | UNIQUE (`target_agent_id`, `name_search`) WHERE `status IN ('proposed', 'active')` |

`workspace_settings` gains `alert_cpu_pct`, `alert_memory_pct`, `alert_disk_pct` (default 90, 1-100),
`alert_sustain_seconds` (default 300, 60-86400) and `remote_runs_enabled` (default false). New columns carry
database defaults, so an older API keeps inserting rows.

**Script runs (ADR-0008 phase 2, migration `ScriptRuns`).** `remote_runs.mode` gains `script` (the mode CHECK and the shape CHECK are dropped and
re-added: a script run has no `argv`, `shell_command` or `cwd`; an emptied run keeps its shape), and the table gains `script_interpreter`
(`sh`, `bash`, `powershell`, `python`; CHECK), `script_body` (text; emptied on account deletion, exported), `script_args` `text[]`,
`target_environment` and `target_function` (the effective role frozen at creation, part of the approval hash), `approved_with_totp`
(boolean, default false), `output_cap` (bytes, 1-64 MiB, the workspace limit frozen at creation; NULL for argv and shell runs) and the
output account: `output_read_bytes`, `output_dropped_bytes` (as the target reports them), `output_refused_bytes`, `output_missing_from_seq`
(what the server refused over the cap, always one range from a chunk on), `output_chunks` and `output_complete` (NULL for argv and shell
runs; true only when nothing was dropped, refused or stopped at the read limit). `workspace_settings.script_output_max_bytes` (default 8 MiB;
CHECK 1-64 MiB; set by an admin in the remote settings).

**Files and stdin on a script run (ADR-0010 §2, migration `ScriptRunFiles`).** `remote_runs` gains `script_files_json` (jsonb: the run's text files as `[{path, text}]`, sorted by path; null for none) and `script_stdin` (text; null for none), with a CHECK that only a `script` run carries either. The
hash of a run with files or stdin uses the tag `script2`; one without keeps `script1` and its old bytes. `agents.capabilities` may now contain `script_files`, which an agent reports when it writes a script's files and stdin; the API refuses a run with either to an agent that does not (`files_unsupported`).
Both columns are cleared by account deletion and included in the export.

**Asks across machines (ADR-0008 phase 3, migration `MachineAsks`).** `agents` gains `accepts_asks` (boolean, default false: the machine owner's opt-in),
`accepts_asks_set_at` and `accepts_asks_set_by` (FK users). New table `machine_asks`: `id`, `workspace_id`, `requester_agent_id`, `requester_user_id`,
`requester_session_id` (FK sessions), `target_agent_id`, `target_user_id`, `target_session` (the Claude session that received it), `client_key`,
`project_key`, `question` (1-8000; NULL once emptied), `status` (`queued`, `delivered`, `answered`, `no_idle_session`, `not_accepting`, `no_answer`,
`expired`, `cancelled`; CHECK), `answer` (≤ 100 000; NULL once emptied), `answer_truncated`, `error`, `created_at`, `expires_at`, `received_at` (migration `AskReceivedAt`: when the target daemon said it holds the ask; the status stays `queued`), `delivered_at`,
`answered_at`, `ended_at`; CHECKs `requester_user_id = target_user_id` and `requester_agent_id <> target_agent_id`; UNIQUE (`requester_agent_id`,
`client_key`); indexes on (`target_agent_id`, `status`) WHERE open, (`requester_agent_id`, `created_at`) and (`workspace_id`, `created_at` DESC). The
question and the answer are personal data like a command: exported (`asks`), emptied on account deletion and the rows deleted after `retention_days`
past their end; the audit rows name an ask by id only. Every transition is a compare-and-set; the housekeeping loop expires a queued ask after 30 minutes (2 before 19/10/2026)
and a delivered one after 15 without an answer, and member removal, an agent move or revoke, the opt-in turning off and the workspace switch turning off
cancel the open ones.

**Agent actions (ADR-0005 amendment, migration `AgentActions`).** New table `agent_actions`: `id`, `workspace_id`, `agent_id` (FK agents), `requested_by` (FK users, the machine's owner),
`kind` (`restart`, `update_check`, `update`, `status`; CHECK), `status` (`queued`, `delivered`, `succeeded`, `failed`, `expired`; CHECK, default `queued`), `result` (the agent's text, ≤ 2000;
it can name the machine's folders, so it is exported to its requester and emptied on account deletion), `created_at`, `expires_at` (created + 10 minutes), `delivered_at`, `finished_at`;
UNIQUE (`agent_id`, `kind`) WHERE status IN ('queued', 'delivered') (one open action of a kind per agent) and an index on (`agent_id`, `created_at` DESC, `id` DESC). Every transition is a
compare-and-set (`queued → delivered → succeeded | failed`; an open action past `expires_at` becomes `expired`); finished rows are deleted after `retention_days` by the housekeeping loop.

- **Transitions are compare-and-set** (`UPDATE … WHERE status = <expected>`); a run, its audit row and a grant's
  use count change in one transaction; the API publishes to the stream after commit.
- **Output** is masked by the target, at most 1 MB per run (checked atomically with the chunk insert), never logged,
  and never carries a NUL byte. Audit rows name a run by id and command hash, not its argv.
- **Retention:** metrics 7 days; runs with their output, and resolved alerts, after `retention_days`; deletes run in
  batches. Grants and jobs are configuration: retired, never deleted. Removing a member or moving an agent revokes
  its grants, retires its jobs, resolves its alerts and cancels its open runs.
