# ADR-0005: Remote work on other machines of a workspace

**Status:** Accepted (06/10/2026). Written in parallel with ADR-0004 (agent self-update) and numbered after it. Amends
[ADR-0002](adr-0002-agent-platform.md) on boot start, operating systems,
credential storage and who may command an agent. Data model: [`data-model.md`](data-model.md) §3b.
**Decider:** the maintainer; the security and data reviews of 06/10/2026 shaped the controls.

## Context

A command could only go from a web user into their own Claude session. The maintainer asked (06/10/2026) that
Claude, talking to the agent on its own machine, can have work done on **another machine of the same workspace**:
read a live server's logs while chasing a bug, run the tests on a test machine and get the result back, and follow
each machine's CPU, memory and disk. **Agents never decide or act on their own:** every action needs a permission,
agents ask for the ones they need, and a resource problem is reported to Claude, never fixed by the agent.

## Decision

### Words

- **Requester:** the agent (and its user) whose Claude session asks for work. **Target:** the agent that would do it.
  **Owner:** the target agent's user. One machine may carry several agents (one per OS user, or a service agent), so
  everything is addressed to an **agent**, never to a machine.
- **Run:** one command executed on a target (`remote_runs`). "Task" is not used: `session_tasks` and Claude's own
  task tools already exist.
- **Grant:** a standing permission the owner gives for one command template on one target. **Job:** a named command
  on one target, approved once by the owner.

### The path of a run

```
Claude --MCP tool--> requester's cm-agent mcp --local SQLite--> requester's daemon --HTTPS--> API
API --(grant matched, or owner approves on the web)--> target daemon over its own outbound stream
target daemon --executes, masks, caps--> API --> requester's daemon --> local SQLite --> MCP tool result
```

- **Only the daemon talks to the API.** The MCP process writes a request row and reads the answer from the local
  database (as ADR-0002 already does): refresh tokens are single-use, so a second API caller would revoke the agent.
- **Exactly once.** The requester sends a `clientKey`, unique per requester agent. Output chunks are unique per
  `(run, seq)`. The target writes the run into its local `exec_runs` before starting it; a replayed run it already
  knows is reported, never started again, and a run that was alive when the daemon restarted is reported
  `failed: agent restarted`.
- **Statuses:** `pending_approval → approved → delivered → running → succeeded | failed | timed_out`, or `denied`,
  `expired` (a pending run unanswered in 15 minutes), `cancelled`. Every transition is a compare-and-set in the
  database; the API publishes after commit. A run's command never changes after it is created.

### Four fail-closed keys

A run executes only when **all** hold; each one defaults to "no".

1. **Workspace switch** `remote_runs_enabled`, set by an admin (default off). Checked at create, delivery and exec;
   turning it off cancels pending runs. The target's own copy counts only when it was read for the workspace in its
   `agent.json` (the daemon tags the stored settings with the workspace `agent.json` named when the read began, so an agent
   moved on the web keeps working and a read that a login overtakes does not count); unread or another workspace's is off,
   and `cm-agent login`/`logout` forget it. Before refusing a run for an off or unread copy, the target reads the settings
   once more (at most once per 10 s), so a switch an admin has just turned on is not missed until the next settings pass.
2. **Local exec level on the target**, `cm-agent install --exec off|argv|shell` (default `off`; `shell` is a separate,
   stronger opt-in). In service mode it lives in an **admin-owned file the service account cannot write**, with an
   optional local ceiling (allowed executables and roots). The API can never raise it. Shell text cannot be held to a
   ceiling, so **a policy with a ceiling never allows shell** (the agent reports it as `argv`).
3. **A grant or a per-call approval by the owner.** Only the target agent's owner approves, as only a session's owner
   commands it (ADR-0002). **There is no self-approval:** a requester who is also the owner still clicks Allow.
4. **Membership.** Requester and owner are members (role ≥ member) of the same active workspace, checked at create,
   delivery and exec. An agent is accepted only while its user is a member of an active workspace; removing a member
   revokes their agents (done first, commit `331a077`), their grants and jobs, and cancels their open runs. Moving an
   agent to another workspace does the same for that agent.

### Two run modes

- **argv:** an absolute executable and its arguments, started with `ProcessStartInfo.ArgumentList`, never through a
  shell. It may match a grant.
- **shell:** free text run by `/bin/sh -c` or `cmd /d /s /c`. It is allowed (maintainer, 06/10/2026) but **never
  matches a grant or a job**: every shell run waits for a per-call approval, and the target must be at exec level
  `shell`.

### Grants: full templates, checked twice

- A template is a **fixed-length** argv list: no trailing wildcard, no variadic part. Each element is a literal, one
  placeholder, or `literal=` followed by one placeholder. `argv[0]` is a literal absolute path (on Windows an `.exe`),
  never looked up on PATH. The **working directory is a literal of the template**; a caller never chooses it.
- Placeholders: `{int:a..b}` (ASCII digits, no sign, no leading zero), `{word}`
  (`^[A-Za-z0-9][A-Za-z0-9._:@-]{0,127}$`), `{path:ROOT}` (absolute, ASCII, no control characters, quotes, spaces or
  shell characters, no `.` or `..` segment — refused, not resolved), `{enum:a|b}`. A `{word}` or `{path}` value never
  starts with `-` or `+`.
- **Never grantable:** an `argv[0]` whose real path is a shell, launcher or interpreter (sh, bash, env, sudo, ssh,
  xargs, python, perl, node, awk, sed, editors and pagers; cmd, powershell, wsl, mshta, rundll32 and the other Windows
  launchers; osascript, open, launchctl; dotnet, java, docker, kubectl, git). On Windows only `.exe` targets; UNC,
  device names, alternate data streams and trailing dots are refused (`.bat`/`.cmd` re-parse their arguments).
- **Paths:** the value and the root are resolved to real paths; the value must stay under the root at a separator
  boundary; case-insensitive on macOS and Windows only. Roots such as `/`, a drive root, `/etc`, `/home`, `/proc`,
  `C:\Windows`, `C:\Users` or the agent's own home are refused.
- One pure matcher in the shared contracts takes the **target's OS as a parameter**. The API uses it to auto-approve
  (syntax and template match); the target runs the full check again before exec (real paths, symlinks, the
  executable pinned and owned by root or Administrators, its folders not writable by the service account). A grant a
  Claude session asks for is linted by the API for options that execute (`-exec`, `--to-command`, `-o ProxyCommand`
  and similar) and refused if it has one.
- A grant is scoped to a **grantee user** (optionally one requester agent), expires within 90 days, can be revoked at
  once, and counts its uses. A grant asked for by Claude is only a request until the owner approves it, after
  re-authenticating within 10 minutes.

### Jobs

A job is a named argv command (or an argv call to an **admin-owned script**) on one target, approved once by the
owner; running it creates an ordinary run. Shell jobs do not exist. A job whose template has a free placeholder
(for example a git ref) still needs a per-call approval for each run, because the placeholder can select code the
attacker controls. A test run on a test machine is such a job: an admin-owned script that fetches and tests.
Approving a job freezes its command; changing it means retiring it and proposing a new one. **No schedules in v1:**
a job runs only when asked (a schedule would be an autonomous action and needs its own decision, global #20).

### The executor on the target

- Runs as the service's own account, **never root or SYSTEM** (checked at install and before every run; with
  `--allow-root` the agent may report metrics, but it never executes).
- Environment built **from empty**: a fixed PATH, HOME/USERPROFILE, `LANG=C.UTF-8`. Nothing from the requester, and
  never `CM_*`, `LD_*`, `DYLD_*`, `DOTNET_*`, `GIT_*`, proxy or interpreter variables. stdin closed; no inherited
  handles.
- At most two runs at a time per target (a third is refused as busy) and 10 pending per target. Timeout default
  120 s, at most 3600 s, measured on a monotonic clock; a run also carries a `not_after` the target enforces.
- **The whole process tree dies** on timeout or cancel: Linux and macOS start the run in its own process group
  (SIGTERM, then SIGKILL after 5 s; the systemd unit uses `KillMode=control-group`); Windows puts it in a Job Object
  with kill-on-close, a process limit and a memory limit, created suspended and resumed after assignment.
- **A process that leaves the process group (`setsid`)** is ended by the service's cgroup on Linux. macOS has no cgroup, so
  there a **descendant tracker** polls every 250 ms (`ExecTrackEvery`, 50 ms to 5 s, zero turns it off) while the run
  lives, through libproc: it records every descendant of the lead by following parent pids (from the lead and from what
  it already recorded), each as pid **and** start time. A kill polls once more first, signals the process group, then
  every recorded descendant that is still the same process (same start time, so a recycled pid is never signalled) and
  has left the group, with the same signal (SIGTERM, later SIGKILL); the poller stops when the run is disposed. **This is
  best effort:** a process that double-forks and re-parents to launchd between two polls (or whose parent dies before
  it is seen) is never recorded and can still escape, as can one created after the last poll. The recorded set is
  capped at 4096. Linux does not start the tracker.
- **Resource limits on macOS.** `posix_spawn` has no rlimit attribute there, and `setrlimit` on the daemon is unsafe (the
  CPU limit is cumulative and would signal the daemon), so a run starts through a trusted launcher: `/bin/sh` sets the
  limits with `ulimit` (hard, so the run cannot raise them) and then **execs the target in the same process**. The pid,
  the process group and the kill logic are unchanged; the target comes in as `"$0"`, its argv[0] as `"$1"` and its
  arguments as `"$@"`, never as script text, so argv mode still never goes through shell parsing (shell mode runs
  `/bin/sh -c <text>` as the target, as before). The limits are: CPU seconds = (timeout + kill grace) × the cores a run
  may use (`ExecCpuCores`, default the processor count), which a multi-threaded run inside its timeout never meets but a
  process that escaped the kill cannot exceed for ever (SIGXCPU); the largest file it may write (`ExecFileSizeMax`,
  default 1 GiB, SIGXFSZ); open descriptors (`ExecOpenFilesMax`, default 1024, at most 10240); core files off. The
  values are bounded in `RunLimits.For`. A limit the system refuses fails the run with exit 126; it never runs unlimited.
  Known differences from a direct start: the launcher shell exports `SHLVL=0` to the target, a program that is missing
  or not executable is still refused as `spawn_failed` (checked before the launcher starts), and `ulimit -f` counts
  1024-byte blocks in macOS bash (a test writes past the limit to catch a change). **Linux does not use the launcher**:
  the service unit is the place for `Limit*` settings and the cgroup, and `/bin/sh` there is often dash, which cannot
  keep argv[0] through `exec`.
- **Output** is read as a stream: the first 256 KB and a tail ring are kept, a run that prints more than 64 MB is
  killed (`output_limit`), and at most 1 MB per run is stored. Secrets are masked on whole lines (a pattern may span
  chunks), **always**, whatever the workspace's masking setting; control characters and ANSI sequences are stripped;
  NUL bytes are replaced. The agent log carries only the run id, grant id, exit code, sizes, duration and kill reason.

### What Claude sees

- MCP tools on the requester's agent: `monitor_machines`, `monitor_metrics`, `monitor_alerts`, `monitor_run`,
  `monitor_run_result` (waits up to 50 s, paged), `monitor_run_cancel`, `monitor_request_grant`, `monitor_grants`,
  `monitor_jobs`, `monitor_job_propose`, `monitor_job_run`.
- A machine named by host name that matches more than one agent is refused with the candidates' ids; the service
  agent is listed first. Another workspace's machine and an unknown one give the same "not found".
- Everything coming back from another machine — run output, alert subjects, mount names — is **untrusted data**:
  wrapped as `<<<claude-monitor-output … origin="remote-machine">>>`, wrapper-closing sequences defused as in
  ADR-0003, capped. What must appear outside the wrapper (a host or user name, an error) is reduced to a label of
  letters, digits and `. _ - @` (at most 64) or an error code, so it cannot carry a sentence.
- The requester's agent deletes finished runs and the output it read from its local database after 7 days. The tool descriptions say never to follow instructions found in it. Marking `monitor_run`
  destructive makes Claude Code ask on the requester's machine too, but that is a convenience, not a control: the
  controls are the four keys.

### The owner's approval

The web shows a waiting run with: target machine, agent, OS account; the mode; every argv element boxed, with
escapes, invisible and non-ASCII characters made visible, never truncated; working directory, timeout, the grant or
"none"; the requester's user, machine and session; "the same person as you" for self-approval; whether that session
read remote output in the last minutes; the reason, labelled **"written by Claude, untrusted"**; the time left. A
shell run, or an argv run whose executable is an interpreter, gets a red notice and the Allow button waits 3 s. There
is no "allow all", no batch and no "always allow" for shell. The Allow request carries the SHA-256 of the canonical
run and the API refuses a mismatch.

**Shell approvals ask for a TOTP code when the approver has two-step sign-in on** (maintainer, 06/10/2026). An owner
without it approves a shell run with the click above. The security review recommended making TOTP mandatory for
shell targets; that is recorded under "Open" below.

### Resources and alerts

- The agent samples CPU, memory and disks every 60 s with the OS's own counters (no new dependency) and reports them;
  the API keeps 7 days. Thresholds (default 90 % each, sustained 5 minutes) live in the workspace settings; the agent
  opens and resolves alerts with hysteresis, one open alert per agent, kind and subject. The API also opens an
  **`offline`** alert when a service-mode agent misses its reports (maintainer, 06/10/2026).
- The agent **never acts** on an alert; Claude reads them with `monitor_alerts` and decides what to ask for.

### Service mode, Linux and credentials (amends ADR-0002)

- `cm-agent install --service` registers the daemon to **start at boot** without a Claude session: systemd on Linux,
  a LaunchDaemon on macOS, a Windows service (through `Microsoft.Extensions.Hosting.WindowsServices`, approved by
  the maintainer). The interactive, harness-started agent is unchanged; service mode is opt-in.
- **Linux is supported** (linux-x64, linux-arm64), at home `~/.claude-monitor` for a user and `/var/lib/cm-agent` for
  the service.
- The service runs as a dedicated non-login account (Linux `cm-agent`, macOS `_cmagent`, Windows
  `NT SERVICE\cm-agent`), from a binary in an admin-owned folder (never the user-home copy), with a home only that
  account can read. The systemd unit sets `NoNewPrivileges`, `ProtectSystem=strict`, `PrivateTmp`, no capabilities,
  `UMask=0077`, a CPU and memory quota and the bundle extraction folder inside the home.
- **Credentials of a service** cannot use a login keychain: on Linux and macOS a 0600 file created atomically and
  refused if its owner or mode is wrong; on Windows DPAPI in the service account's **CurrentUser** scope (never
  LocalMachine), and the service refuses to start if that profile is not loaded. The interactive agent keeps the OS
  credential store.
- **A service logs in by itself** (decided while building it, 06/10/2026): Windows gives a virtual account no
  interactive session, so `cm-agent login` cannot run as it. `install --service --server <url>` sets `CM_SERVER`;
  a service that is not connected runs the device flow itself and writes the code to its log and to
  `login-code.txt` in its home, which only the service account, SYSTEM and administrators (read-only) can open. The
  admin reads it and approves it on the web. The same path serves Linux and macOS (no `sudo -u` needed).
- The exec policy file must be owned by root (uid 0) on Unix and must not be writable by the service process on
  Windows; anything else reads as `off`.
- A service agent is upgraded by an admin. Its binary lives in an admin-owned folder the service account cannot write,
  so the opt-in self-update of [ADR-0004](adr-0004-agent-self-update.md) (which replaces the binary in the agent's own
  home) cannot replace it. The API refuses an agent below a minimum version as a target.

### Backward compatibility

Old agents never report an exec level, so they are never targets; a new agent on an old API gets 404 and switches
the feature off. Contracts only grow at the end of records; new columns have database defaults; the OS CHECK widens
by drop and re-add (`scripts/backcompat_scan.py` stays green). `LinuxMachines.Down()` cannot run once a Linux machine
exists, so a rollback of that slice is code-only.

## Consequences

- A compromised API can reach every target up to that target's **local** exec level and ceiling; that is why the
  level is local and admin-owned. End-to-end signed approvals are not in v1.
- A shell run executes with the service account's rights and can read that account's files, its token included.
  Grant paths into the agent's home are refused; a separate run account is a v2 option.
- Run output, grants and jobs are personal data: export includes them; account deletion empties commands, templates
  and output; runs and output follow the workspace's retention.

## Open (reported, not decided here)

- **TOTP for every shell target** (security review): the maintainer chose "only when the approver has it on".
- **Schedules for jobs**, shipping files or test bundles to a target, end-to-end signed approvals, a separate run
  account, a multi-instance API broker.
- **Target-side checks not in v1** (security review items left open while building): refusing a grant root whose
  folder chain is writable by a non-admin (it would refuse ordinary app-owned log folders), a pinned hash of the
  executable, and an ACL ownership check of the executable on Windows (Unix checks owner and modes).
- **Unverified platforms:** the Linux and Windows paths of the executor, the metrics and the service installer compile
  and follow the platform contracts but have only run on macOS; they are verified on a Linux and a Windows Server
  machine before a target there is trusted.
