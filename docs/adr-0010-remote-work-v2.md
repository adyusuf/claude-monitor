# ADR-0010: Remote work v2: files and stdin for scripts, job schedules, signed approvals, a separate run account

**Status:** Proposal with a first build (16/10/2026). The maintainer asked for all five items ("job scheduling, shipping files to a target, end-to-end signed approvals, a separate run account,
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

- **A schedule belongs to an approved job and its owner.** The target agent's owner creates it on the web (job, `interval` of 15 minutes to 7 days, or `daily` at a UTC time); it is `active` after the owner confirms
  with a recent sign-in or a code (like a grant). It freezes the job's hash: retiring the job, changing its target's exec level below argv, or the owner leaving the workspace ends it.
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
- **What is signed.** `"cm-approval-v1\n" + kind + "\n" + id + "\n" + targetAgentId + "\n" + hash + "\n" + notAfter` where `kind` is `run`, `grant` or `job`, `hash` is the run hash (argv, shell or script, including files and stdin), the grant's template hash or the job's command hash,
  and `notAfter` the approval's expiry. The signature (P-256, IEEE P1363 as WebCrypto makes it) travels with the approval: the API stores it and sends it with the run, or with the grant or job a run names.
- **What the target checks.** In `required` mode, before executing: the run's own hash recomputed from the message it got equals the signed hash; the signature verifies under a pinned key; `notAfter` has not passed (the target's clock, with the
  existing skew allowance); the run id has not run here before. A run under a grant or job needs the grant's or job's signature, which the target remembers by id until it expires. Anything missing refuses the run
  (`approval_required`, `approval_invalid`, `approval_key_unknown`). A compromised API can then refuse or delay a run but cannot make one a pinned owner did not sign.
- **Compatibility.** An approval made without a key (an older web, or a browser without WebCrypto key support) is still valid for a target in `off` mode. A target in `required` mode tells the owner, on the card, that a key is needed.
- **Not covered.** A stolen unlocked browser profile can sign; the same holds for any cookie. The signature proves "a pinned key approved this exact payload", not that the person looked at it.

## 5. A separate run account (Linux and macOS)

- **Opt-in at service install:** `cm-agent install --service --run-account [<name>]` (default `cm-run` on Linux, `_cmrun` on macOS). The installer creates the non-login, unprivileged account, adds the service account to
  its group, creates the run folder `<service home>/run-work` (setgid, group of the run account, 0770), and writes a sudoers drop-in that lets **only the service account** run commands as **only the run account**
  without a password (validated with `visudo -cf` before it is moved into place). `uninstall --service` removes all of it.
- **How a run starts.** The executor's start path becomes `sudo -n -u <account> -- env -i <fixed environment> <program> <args>` (so sudo's environment handling cannot add anything), with the scratch folder under `run-work`
  and the run's TMPDIR inside it. Output, stdin, the process group and the kill logic are unchanged (the group leader is the `sudo`/`env` chain's child; the descendant tracker and the cgroup rules of ADR-0005 apply to it).
  The run account cannot read the service home, the credential file or the exec policy.
- **Refused where it cannot be checked.** Windows: `--run-account` is refused with a message, because starting a process as another local account needs a stored password or a service-for-user design that was not built.
  A run account that is root or in an admin group is refused at install and before each run.
- **Not covered.** A run can still do what the run account can (read world-readable files, use the network). It is a smaller blast radius than the service account, not a sandbox.

## 6. Data model (additive)

`remote_runs`: `script_files` (jsonb, null), `script_stdin` (text, null), `approval_signature` (text), `approval_key_id` (text), `schedule_id` (uuid, null).
New tables: `job_schedules` (id, job_id, workspace_id, owner_user_id, target_agent_id, kind, interval_minutes, daily_at_minutes, status `proposed|active|paused|ended`, pause_reason, next_run_at, last_run_at, failures_in_a_row, job_hash, created_at, decided_at),
`approval_keys` (id, user_id, name, public_key, fingerprint, created_at, retired_at). `workspace_settings.remote_schedules_enabled` (default false). `agents`: `run_files` (bool), `approvals_required` (bool, reported). `machine_grants`/`machine_jobs`: `approval_signature`, `approval_key_id`.
Contracts only grow at the end of records; the backward-compatibility scan stays green.

## 7. Phases

| # | What | Done when |
|---|---|---|
| 1 | files and stdin: contracts and hash, API rules and freeze, agent scratch, stdin, interpreter flags, profile flag, web card, MCP tool and CLI options | tests for the path rules, the hash, the scratch, a real multi-file script and stdin on this machine, the card |
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
6. The API refuses to send files or a signature-requiring run to an agent that does not report the capability, instead of letting an old agent run it without them.
