# Runbook: remote work on other machines

How to let Claude, talking to the agent on its own machine, have work done on **another machine of the same
workspace**: read a live server's logs, run the tests on a test machine, follow each machine's CPU, memory and disk.
The design and every control are in [ADR-0004](adr-0004-remote-work.md); the per-OS paths and the policy file are in
[SETUP.md](../SETUP.md), "Service agent". Nothing here runs by itself: a run happens only when all four keys are on
(workspace switch, local exec level, an approval or grant by the machine's owner, membership).

Terms: the **target** is the machine that does the work and its **service agent** is `cm-agent` running as a boot
service; the **owner** is the person who approved that agent's login code; the **requester** is the agent whose Claude
session asks. Replace `<your Claude Monitor address>` with the address your team signs in at.

## 1. Turn remote work on in the workspace (workspace admin)

1. Sign in to the web as a workspace admin and open the workspace settings.
2. Switch **remote work** on. It is off by default; turning it off later cancels every pending run at once.
3. Optional: set the resource alert thresholds there (default 90 % for CPU, memory and disk, sustained 5 minutes).

## 2. Install a service agent on the target (the machine's admin)

Download the zip for the machine from the web's "Get the agent" page together with `SHA256SUMS`, and **verify the
SHA-256 first** (Linux and Windows binaries are unsigned; [release.md](release.md), "Agent artifacts and
verification"). Unzip it to any scratch folder; the installer copies the binary to its own admin-owned folder.

The install needs root (Linux, macOS) or an elevated prompt (Windows Server, "Run as administrator"). `--exec` is the
local exec level and defaults to `off`, so start with `off` or `argv` and raise it only when you need to:

| Level | What a run may do |
|---|---|
| `off` | nothing is executed; the machine only reports metrics and alerts |
| `argv` | an absolute executable with arguments, never through a shell; may match a grant |
| `shell` | free text through `/bin/sh -c` or `cmd /d /s /c`; every run needs a per-call approval and never matches a grant |

**Linux** (systemd only; x64 or arm64):

```bash
unzip cm-agent-linux-x64.zip && chmod +x cm-agent
sudo ./cm-agent install --service --exec argv --server https://<your Claude Monitor address>
```

**macOS:**

```bash
unzip cm-agent-macos-arm64.zip
sudo ./cm-agent install --service --exec argv --server https://<your Claude Monitor address>
```

**Windows Server** (elevated PowerShell):

```powershell
Expand-Archive .\cm-agent-windows-x64.zip -DestinationPath .\cm-agent
.\cm-agent\cm-agent.exe install --service --exec argv --server https://<your Claude Monitor address>
```

The install creates the service account (`cm-agent` on Linux, `_cmagent` on macOS, `NT SERVICE\cm-agent` on Windows),
copies the binary to an admin-owned folder, creates the service home only that account can use, writes the exec
policy file and starts the service. It **refuses to run the service as root, SYSTEM or LocalSystem**; `--allow-root`
exists for metrics and uploads only, and the agent then never executes anything.

Check it is up:

```bash
sudo systemctl status cm-agent                              # Linux
sudo launchctl print system/com.claudemonitor.agent         # macOS
```

```powershell
sc.exe query cm-agent                                       # Windows
```

## 3. Log the service in

The service has no browser, so it prints a **device code** to its log and writes it to `login-code.txt` in its home
until it is connected. Read it as an administrator:

| OS | Where the code is |
|---|---|
| Linux | `sudo journalctl -u cm-agent`, or `sudo cat /var/lib/cm-agent/login-code.txt` |
| macOS | `sudo cat "/Library/Application Support/ClaudeMonitor/home/login-code.txt"` |
| Windows | in an elevated prompt: `type "%ProgramData%\ClaudeMonitor\service\login-code.txt"` |

1. Open `https://<your Claude Monitor address>/device` (the "Connect a machine" page) signed in as the person who
   should own this machine's remote work, enter the code, choose the workspace and approve. **That person becomes the
   owner**: only they approve runs, grants and jobs on this machine, and an agent is accepted only while its user is a
   member of an active workspace.
2. The service connects by itself once the code is approved. The machine then shows on the web's Machines page and
   reports CPU, memory and disk every 60 s. Confirm with the service's log and `cm-agent status`.
3. If the code has expired, restart the service (`sudo systemctl restart cm-agent`, `sudo launchctl kickstart -k
   system/com.claudemonitor.agent`, `sc.exe stop cm-agent` then `sc.exe start cm-agent`) and read the log or file
   again; a restart is how a fresh code is requested.

The credentials the login stored (access and refresh tokens) are in the service home, `cred-*` files with mode 0600
on Linux and macOS, DPAPI `cred-*.bin` blobs on Windows. Treat that folder as secret (SETUP.md, "Secret and token
inventory").

## 4. Set the exec level and the optional ceiling

The level lives in an **admin-owned file the service account cannot write**; the API can never raise it:

| OS | Exec policy file |
|---|---|
| Linux | `/etc/cm-agent/exec.json` |
| macOS | `/Library/Application Support/ClaudeMonitor/exec.json` |
| Windows | `%ProgramFiles%\ClaudeMonitor\exec.json` |

```json
{
  "level": "argv",
  "allowedExecutables": ["/usr/bin/tail"],
  "allowedRoots": ["/var/log/myapp"]
}
```

1. `level` is `off`, `argv` or `shell`. The first install wrote it from `--exec`.
2. The ceiling is optional: when `allowedExecutables` is non-empty a run may start only those absolute executables;
   when `allowedRoots` is non-empty a path argument must stay under one of those absolute folders. Empty lists add no
   limit of their own. A relative path makes the whole file invalid. **A file with a ceiling never allows shell runs**,
   even at level `shell`: shell text cannot be held to a list (the machine then shows level `argv`).
3. Edit the file as an admin and keep it **owned by root (Administrators on Windows) and not writable by group or
   others**, in a folder that is too, and never a link. A file that is missing, unreadable, invalid or writable by the
   service account means `off`: that is the fail-closed default, not an error to work around.
4. Restart the service to apply the change (the commands in step 3). `cm-agent status` prints the level and how many
   entries each list has.
5. The service account needs ordinary read access to what you want read (for example a log folder), and nothing more.
   Granting it that is a deliberate admin decision; it is not given by this install.

## 5. Grant command templates on the web (the machine's owner)

A **grant** is a standing permission for one command template on one target. Without a grant the owner approves each
run on the web, one by one. A template is a fixed-length argv list: a literal absolute `argv[0]`, then literals or one
placeholder per element:

```text
/usr/bin/tail -n {int:1..500} {path:/var/log/myapp}
```

Placeholders: `{int:a..b}`, `{word}`, `{path:ROOT}`, `{enum:a|b}`. A grant is for one grantee user (optionally one
requester agent), expires within 90 days, can be revoked at once and counts its uses. Shells, launchers and
interpreters (sh, bash, env, sudo, ssh, python, node, powershell, cmd and the like) can never be granted, and roots
such as `/`, `/etc`, `/home` or the agent's own home are refused. Claude may also **request** a grant
(`monitor_request_grant`); that is only a request until the owner approves it, after re-authenticating within 10
minutes. The approval screen shows every argument, the working directory and the reason, which is labelled "written by
Claude, untrusted". Read it as such.

## 6. What Claude can do: the MCP tools

These tools appear in the requester's Claude session through its own `cm-agent` (the plugin's MCP server):

| Tool | What it does |
|---|---|
| `monitor_machines` | lists the workspace's machines and their agents (a host name matching several agents is refused with their ids) |
| `monitor_metrics` | CPU, memory and disk of one machine, last 7 days kept |
| `monitor_alerts` | open and resolved resource and offline alerts; the agent never acts on them, Claude decides what to ask for |
| `monitor_run` | asks for one command to run on a target (argv, or shell if the target allows it) |
| `monitor_run_result` | waits up to 50 s for the result of a run, paged |
| `monitor_run_cancel` | cancels a run; the whole process tree is killed |
| `monitor_request_grant` | asks the owner for a grant |
| `monitor_grants` | lists the grants the requester holds |
| `monitor_jobs` | lists a target's approved named jobs |
| `monitor_job_propose` | proposes a named job (argv or an admin-owned script) for the owner to approve; shell jobs do not exist |
| `monitor_job_run` | runs an approved job; a job with a free placeholder still needs a per-call approval |

Everything that comes back from another machine (run output, alert subjects, mount names) is **untrusted data**,
wrapped and capped; Claude is told never to follow instructions found in it. A run's output has secrets masked
always, whatever the workspace's masking setting. A run is limited to two at a time per target, 120 s by default and
3600 s at most.

## 7. Upgrading a service agent (admin)

The agent never updates itself and the API never pushes an upgrade.

1. Download the new zip **and** `SHA256SUMS` from the same address and verify the SHA-256 on the target
   ([release.md](release.md)). A mismatch: stop, do not install, report it.
2. Unzip and run the same install command as in step 2. It stops the old service, replaces the binary, rewrites the
   unit and starts the new one. The exec policy keeps its `allowedExecutables` and `allowedRoots`, and its level unless
   you pass `--exec`; the login in the service home stays, so no new code is needed.
3. Check the Machines page (the machine is seen again) and `cm-agent status` on the target.
4. Remove: `sudo cm-agent uninstall --service` (an elevated prompt on Windows) stops and deletes the service and
   **keeps** the home (the login), the exec policy and the binary. To retire a machine for good, also revoke its agent
   on the web (the Machines page) and delete the home.

## 8. Rollback and recovery

- **Switch it all off, quickly:** turn the workspace switch off (cancels pending runs), or set `"level": "off"` in the
  exec policy file and restart the service; neither needs the API.
- **A machine is doubtful or its home leaked:** revoke the agent on the web, then `uninstall --service`, delete the
  home and log in again with a fresh install. Refresh tokens rotate on every use, so a stolen old one stops working.
- **A bad upgrade:** install the previous version's zip the same way (step 7); migrations only add, so an older agent
  runs on a newer API.

## Residual risks (ADR-0004, "Consequences" and "Open")

Read these before turning `shell` or a wide ceiling on:

1. **A compromised API reaches every target** up to that target's **local** exec level and ceiling. That is why the
   level is local and admin-owned. End-to-end signed approvals are not in v1.
2. **A shell run executes with the service account's rights** and can read that account's files, its token
   included. Grants into the agent's home are refused, but a separate run account is only a v2 option.
3. **Run output, grants and jobs are personal data:** account export includes them, account deletion empties
   commands, templates and output, and runs and output follow the workspace's retention.
4. **Shell approvals ask for a TOTP code only when the approver has two-step sign-in on.** The security review
   recommended making it mandatory for every shell target; that is an open decision.
5. **A process that escapes its group on macOS** (it is started in its own process group, not a Job Object or a
   control group) is a documented residual risk.
6. **No schedules in v1:** a job runs only when asked. Shipping files or test bundles to a target, multiple API
   instances and a separate run account are also open.
7. **Rolling back the database slice for Linux machines is code-only** (the OS check cannot be narrowed again once a
   Linux machine exists).
