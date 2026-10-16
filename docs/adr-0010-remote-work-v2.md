# ADR-0010: Remote work v2: files and stdin for scripts, job schedules, signed approvals, a separate run account

**Status:** Built in four phases (16/10/2026), tests written and run on macOS; nothing has run on a Linux or Windows host and the run account has never started a real process as another account. The maintainer asked for all five items ("job scheduling, shipping files to a target, end-to-end signed approvals, a separate run account,
multi-file scripts and stdin", the open items of [ADR-0005](adr-0005-remote-work.md) and [ADR-0008](adr-0008-machine-awareness-script-runs-and-cross-machine-help.md) §10). Every choice below was
made by the implementer where the maintainer had not said; each is listed in §9 so it can be reversed. Nothing here has run on a Windows or Linux host.
**Extends:** ADR-0005 (runs, grants, jobs, the executor), ADR-0008 §3c (scripts and the strict-target rule, decision §9.13).

## 1. What exists, and what each item changes

| Item | Today | Change |
|---|---|---|
| Multi-file scripts, stdin, shipping files | a script is one text; stdin is closed | a script run may carry a small set of **text files** and a **stdin text**; both are part of what the owner approves and of its hash (§2) |
| Job schedules | "No schedules in v1: a job runs only when asked" (a schedule is an autonomous action, global #20) | an owner may put an approved job on a **schedule**, behind its own switch, with caps and an automatic pause (§3) |
| End-to-end signed approvals | the API tells the target a run is approved; a compromised API could say so falsely, limited by the target's local exec level | an owner's **browser key signs** what it approves; a target that requires it runs nothing without a signature from a key **its local admin pinned** (§4) |
| A separate run account | a run has the rights of the service account, its token file included | on Linux and macOS a service agent starts runs as a **second, unprivileged account** (§5) |

## 2. Files and stdin on a script run

- **What a run carries.** `files`: up to 8 text files (UTF-8, no NUL), each at most 64 KiB and 256 KiB together; `stdin`: one text of at most 64 KiB. Script mode only: argv and shell runs carry neither
  (a program that argv runs is chosen by an absolute path, and shell text is already free text). Binary files and archives are **not** supported: the owner approves the exact content, so it must be readable (the same
  reason scripts are text). A test bundle is its text files.
- **File names.** A relative path of at most 4 segments of `[A-Za-z0-9._-]` (at most 64 characters each), no segment that is `.` or `..`, no leading dot on the first segment, no backslash, no drive letter, no duplicate
  after case folding, and not `script.*` or `tmp`. The agent writes each below the run's scratch folder (0700 folder, 0600 file, never executable, no link followed) and refuses a path that resolves outside it.
- **What the owner sees and signs.** The approval card lists every file with line numbers (as for the script), its size and the stdin; "Allow" needs all of them scrolled to the end, as a script does. The hash covers the
  script, every file (path and text, sorted by path) and the stdin: `ScriptHash` keeps its `script1` tag and bytes when there are no files and no stdin (so every existing hash and old agent is unchanged) and uses `script2` otherwise.
- **How it runs.** stdin is written to the process and closed; with no stdin it is closed as today. A Python script is started with `-I -B` when it has no files, and with `-E -s -B` when it has files, because `-I`
  keeps the script's folder off `sys.path`, which is what a multi-file script needs; the scratch folder holds only approved files, the environment is empty and `-E -s` ignore the rest.
- **Targets that cannot.** An agent reports `runFiles: true` in its profile; the API refuses a run with files or stdin for any other target (`files_unsupported`). The workspace's output cap, the strict-target code rule and
  the role snapshot apply unchanged. Files and stdin are captured content: never logged, deleted with the run's retention, included in the account export and deletion like the script.

## 3. Job schedules

- **A schedule belongs to an approved job and its owner.** The target agent's owner creates it on the web (job, `interval` of 15 minutes to 7 days, or `daily` at a UTC time); creating it is the owner's confirmation (a recent
  sign-in or a code, like a grant), so it is `active` at once: there is no proposed stage, and Claude cannot create one. The owner or a workspace admin can pause it at once; only the owner starts a paused one again. It ends
  when its job is retired, its owner or its target goes away, or the owner ends it.
- **What may be scheduled.** Only a job with no free placeholder (those need a per-call approval), on a target that is **not strict** (no unattended runs on production or a database), whose exec level allows argv.
  Never a script or a shell command (neither can be a job).
- **Switches and caps.** A new workspace setting `remote_schedules_enabled` (admin, **default off**, audited) gates creation and firing. At most 5 active schedules per target, at most 96 runs a day per schedule
  (the 15-minute floor), no overlap (a schedule whose last run is still open skips that tick), no catch-up after downtime (the next tick is computed from now). **Three failed, timed-out or expired runs in a row pause**
  the schedule (`paused`, a reason, shown on the web); the owner resumes it. The target's own busy and pending rules apply.
- **How it fires.** A background service (like the housekeeper) claims due schedules by compare-and-set on `next_run_at` (so two API instances never fire one tick twice), then creates an ordinary run through the same
  creator, approved by the job. The requester is the target's own agent and the target's owner (recorded as `schedule_id` on the run, shown on the web). Everything a run has (masking, output cap, retention) stays.
- **Rule #20.** This is an autonomous, recurring action, started only by the maintainer's explicit request (16/10/2026), default off, per workspace, audited, capped and self-pausing; a person can stop it from the web at once.

## 4. End-to-end signed approvals

- **The key.** An owner's browser makes an ECDSA P-256 key pair with WebCrypto (no dependency), **non-extractable**, kept in IndexedDB; the account page lists the public key's fingerprint (SHA-256 of the SPKI, hex) and can retire it.
  The API stores the public key per user and device name (`approval_keys`), so it can check a signature too, but **the API's copy is not what a target trusts**.
- **Pinning.** A target trusts a key only after its **local admin** pinned it: `cm-agent approvers add <fingerprint> <public key>` (the person compares the fingerprint with the web page), stored in `approvers.json` in the agent's home,
  admin-owned (root-owned and not writable by the service on Unix, not writable by the service on Windows, as the exec policy file is). `cm-agent config approvals required|off` (same file, same ownership) sets the mode; the default is `off`, so nothing changes for anyone who does not opt in.
- **What is signed.** `"cm-approval-v1\n" + "run" + "\n" + runId + "\n" + targetAgentId + "\n" + hash + "\n" + notAfter` (Unix seconds), one value per line, where `hash` is the run hash the card carries (argv, shell or
  script, including files and stdin and, for a script, the role it was frozen with) and `notAfter` the run's pending expiry shown on the card. The signature (ECDSA P-256 over SHA-256, the 64-byte IEEE P1363 form WebCrypto makes) travels
  with the approval: the API checks it against the owner's registered key, stores it with the run and sends it to the target with the run, together with the role the script was frozen with (the agent needs it to rebuild the hash).
- **What the target checks.** In `required` mode, before executing anything: the run's hash recomputed from the message it got equals the signed one; the signature verifies under a pinned key; the signed expiry has not passed by more than
  15 minutes (the start window); the key id is one it pinned. Anything missing refuses the run (`approval_required`, `approval_invalid`, `approval_key_unknown`, `approval_key_missing`). A compromised API can then refuse or delay a run but cannot
  make one a pinned owner did not sign. **Grants and jobs are not signed in this build.** Instead a target in `required` mode reports it with its profile (`approvalsRequired`), the API then never auto-approves a run for it by a grant or a job (each
  run waits for a signature of its own), no schedule can be made for it, and an existing schedule pauses with the reason `approvals`; and the agent refuses a run that carries a grant or no signature, which is the same rule from the other side.
- **Compatibility.** An approval made without a key (an older web, or a browser without WebCrypto key support) is still valid for a target in `off` mode. A target in `required` mode tells the owner, on the card, that a key is needed.
- **Not covered.** A stolen unlocked browser profile can sign; the same holds for any cookie. The signature proves "a pinned key approved this exact payload", not that the person looked at it.

## 5. A separate run account (Linux and macOS)

- **Opt-in at service install:** `cm-agent install --service --run-account [<name>]` (default `cm-run` on Linux, `_cmrun` on macOS). The installer creates the non-login, unprivileged account, adds the service account to
  its group, creates the run folder `<service home>/run-work` (setgid, group of the run account, 0770), and writes a sudoers drop-in that lets **only the service account** run commands as **only the run account**
  without a password (validated with `visudo -cf` before it is moved into place). `uninstall --service` removes all of it.
- **How a run starts.** The executor's start path becomes `sudo -n -u <account> -- env -i <fixed environment> sh -c 'umask 007; exec "$@"' sh <program> <args>` (so sudo's environment handling cannot add anything, and what the run makes
  stays group-writable so the service can always clean it up), with the scratch folder directly under `run-work`, the run's HOME and the working directory of a run that names none in it. Output, stdin and the limits launcher are unchanged. The
  run account cannot read the service home, the credential file or the exec policy.
- **Kill reach, the known limit.** The service cannot signal a process that belongs to the run account, only sudo (which relays TERM, INT and HUP to its command), so a run that ignores TERM is not ended by the service's SIGKILL the way a run
  of the service account is. The drop-in sets `!use_pty` so sudo does not put the command in a session of its own; the process-group, cgroup and descendant-tracker rules of ADR-0005 cover what they covered, not more.
- **Refused where it cannot be checked.** Windows: `--run-account` is refused with a message, because starting a process as another local account needs a stored password or a service-for-user design that was not built.
  A run account that is root or in an admin group is refused at install and before each run.
- **Not covered.** A run can still do what the run account can (read world-readable files, use the network). It is a smaller blast radius than the service account, not a sandbox.

## 6. Data model (additive)

`remote_runs`: `script_files_json` (jsonb, null), `script_stdin` (text, null), `schedule_id` (uuid, null).
New tables: `job_schedules` (id, job_id, workspace_id, owner_user_id, target_agent_id, kind, interval_minutes, daily_at_minutes, status `active|paused|ended`, pause_reason, next_run_at, last_run_at, last_run_id, last_run_settled, failures_in_a_row, created_at, updated_at),
`approval_keys` (id, user_id, name, public_key, fingerprint, created_at, retired_at). `workspace_settings.remote_schedules_enabled` (default false). `agents`: `approvals_required` (bool, reported with the profile); a script-capable agent also lists `script_files` in its capabilities. `remote_runs`: `approval_key_id`, `approval_signature`, `approval_not_after`.
Contracts only grow at the end of records; the backward-compatibility scan stays green.

## 7. Phases

| # | What | Done when |
|---|---|---|
| 1 | files and stdin: contracts and hash, API rules and freeze, agent scratch, stdin, interpreter flags, capability, web card, MCP tool and CLI options | tests for the path rules, the hash, the scratch, a real multi-file script and stdin on this machine, the card |
| 2 | schedules: tables, setting, scheduler, rules, web card, audit | tests for the caps, the pause, a due tick firing once under a race, the strict refusal |
| 3 | signed approvals: keys on the web, signing, API storage and check, agent verifier, `approvers`, `config approvals`, refusal codes | a signature made by WebCrypto (Node) verified by the agent's code, tampering of each field refused, `off` mode unchanged |
| 4 | run account: installer steps, sudoers, the start path, refusals | the command lines and the installer steps with fakes; **not run on a real host** |
| 5 | docs: ADR-0005's open list, security.md, SETUP.md, data-model.md, remote-work-setup.md | the gate's document checks green |

## 8. Risks

Files and stdin widen what an approved script can do only by what the owner read; the limits keep the card readable. A schedule is the first thing that runs without a person asking, hence the switch, the caps and the pause.
A signed-approval mode that is `required` with no pinned key refuses everything (fail-closed), which a careless admin can hit; the message says why. The run account depends on `sudo`, which is not on every host; the installer says so and stops.

## 9. Decisions taken by the implementer (reversible)

1. Text files only, 8 files, 64 KiB each, 256 KiB total; script mode only. 2. Python without `-I` when files are present. 3. Schedules: no cron syntax; `interval` and `daily` only; 15-minute floor; refused on strict targets; default off;
three failures pause. 4. Signed approvals: ECDSA P-256 (WebCrypto everywhere, no Ed25519), non-extractable browser key, pinned by the local admin, mode off by default. 5. Run account: Linux and macOS only, through `sudo`; Windows refused.
6. The API refuses to send files to an agent that does not report the capability, instead of letting an old agent run the script without them.
7. Signed approvals cover per-call runs only; grants, jobs and schedules do not run on a target that requires them (§4), instead of being signed too.
8. A schedule is confirmed by its creating owner and starts active; there is no proposed stage and Claude cannot propose one.
