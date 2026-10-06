# Setup

A clean machine is set up by following this file. The design is [ADR-0002](docs/adr-0002-agent-platform.md); each
phase that adds a codebase adds its tools, variables and secrets here in the same change.

## Prerequisites

| Tool | Why | Check |
|---|---|---|
| git | the repository and the gate | `git --version` |
| .NET 10 SDK | the API and the agent (build, tests, format) | `dotnet --version` |
| `dotnet-ef` 10 | creating migrations (`dotnet tool install -g dotnet-ef`) | `dotnet ef --version` |
| Docker | local PostgreSQL and Mailpit; the API's tests start their own PostgreSQL 18 | `docker info` |
| Python 3.9+ | the gate tools and their tests | `python3 --version` |
| gitleaks, ShellCheck, CodeQL CLI | the secret scan, and SAST | `gitleaks version`, `shellcheck --version`, `codeql version` |

## Install

```bash
git clone https://github.com/adyusuf/claude-monitor.git ~/ClaudeCode/claude-monitor
```

## Commit hooks

This repository is public, so a commit is checked before it exists: the `CLAUDE.md` size budget, a
`gitleaks` scan of the staged content, and a **real-project-name check** over the staged file names,
added lines and the commit message. Install once, in the main checkout (not in a linked worktree,
where `.git` is a file and the installer cannot write):

```bash
bash scripts/pre-commit.sh --install
```

The hooks are symlinks to `scripts/` of that checkout, so they run whatever branch the main checkout
has. The name check needs a local, git-ignored map of the names to look for. Point it at yours:

```bash
ln -s <your>/project-nicknames.tsv docs/project-nicknames.tsv   # or: export REAL_NAMES_MAP=<path>
```

Without a map the check prints "NOT RUN" and passes; `REAL_NAMES_STRICT=1` makes that a failure. The
format of the map is described in `scripts/real-name-check.sh`. A deliberate exception is
`git commit --no-verify`, and it is the maintainer's call.

## Development

```bash
docker compose -f deploy/dev-services.yml up -d          # PostgreSQL 18 on 127.0.0.1:55432, Mailpit on :1025 (UI :8025)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/ClaudeMonitor.Api -- --migrate   # apply migrations
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/ClaudeMonitor.Api                # the API
dotnet test                                                # all .NET tests (Docker must be running)
bash scripts/merge-gate.sh dev                             # everything CI runs
```

In Development every variable has a default (`.env.example`); sign-up mails land in Mailpit
(http://localhost:8025). A new migration: `dotnet ef migrations add <Name> -o Data/Migrations --project
src/ClaudeMonitor.Api`. Migrations only add (global #4); `scripts/backcompat_scan.py` blocks a drop, rename or
retype, an edited merged migration, a removed route and a removed or reordered contract field.

## CI

`.github/workflows/ci.yml` runs `bash scripts/gate-core.sh dev` on every push to `dev`, `test` and `prod`
and on every pull request: the same script and the same thresholds as a local `scripts/merge-gate.sh dev`,
never a second rule set. Third-party actions are pinned to a commit SHA and gitleaks is
checksum-verified. To change a version, change it there and in the Prerequisites table above.

## The agent (cm-agent)

```bash
dotnet run --project src/ClaudeMonitor.Agent -- version                # from source
dotnet publish src/ClaudeMonitor.Agent -c Release -r osx-arm64 -o out/osx-arm64   # one self-contained binary (also osx-x64, win-x64, win-arm64, linux-x64, linux-arm64)
out/osx-arm64/cm-agent login --server http://localhost:5080            # shows a code; approve it on the web (/device)
out/osx-arm64/cm-agent install                                         # copies itself to the agent home and registers the Claude Code plugin
out/osx-arm64/cm-agent status
```

`install` registers a local plugin marketplace (`monitor-agent-local`) whose hooks and MCP server run the
installed binary, so Claude Code starts the agent with its sessions; nothing is registered with the OS. Without
the `claude` CLI on `PATH` it prints the two `claude plugin` commands to run. The agent's tokens are in the macOS
Keychain / Windows Credential Manager (service `claude-monitor-agent`); on Linux, which has no credential store, in
user-only (0600) `cred-*` files in the agent home. Its log is `agent.log` in the agent home.

A prompt sent from the web enters a session only when something is typed in it or a turn ends; an idle session takes
it at neither, so it stays "delivered" until it expires. `cm-agent install --stop-wait 120` makes a finished turn wait
up to 120 s (0-590, default 0) for a prompt from the web and start a new turn from it; the value is saved in
`agent.json` and the hooks are rewritten (hook timeout = wait + 15 s), so re-run `install` after changing it.
`cm-agent status` prints `commands waiting: N (oldest expires HH:mm)` and `stop wait: N s`. Reasons for the default of 0:
ADR-0002, "Commands from the web".

### Pushing web messages into an idle session (opt-in)

Off by default. `cm-agent install --push on` (then restart the Claude Code session) makes the `claude-monitor` MCP server a
Claude Code **channel**: a prompt sent from the web starts a turn in an idle session within about a second, wrapped as
`<<<claude-monitor-message …>>>` and labelled as coming from the web (untrusted: the session is told to treat it as data and
to ask you before any request with side effects). A channel is a Claude Code research preview and a custom one is not on its
allowlist, so Claude Code must be started with the flag `install` prints:

```bash
claude --dangerously-load-development-channels plugin:monitor-agent@monitor-agent-local   # accept the "local development" prompt
```

Without the flag (or where an organisation has channels off) nothing is lost: a pushed message that does not show up in the
session transcript within 120 s goes back to the hooks and arrives at the next prompt, as before. `--push off` (or
`CM_PUSH=off`) removes it. `CM_PUSH_SCOPE=machine` pushes every prompt of the machine instead of only this session's (single-session
machines only). **No new token or secret:** the daemon uses the credential `cm-agent login` stored in the OS credential store; the MCP process
never touches the network. Revoke it from the web's Machines page; rotation is `cm-agent logout` + `cm-agent login`. `monitor_status` and `cm-agent
status` show whether push is on, the stream state (connected / reconnecting), the last message id, what is queued locally and the last upload
or failure. `scripts/push-check.py` repeats the end-to-end check against the local e2e stack (a stand-in client plays Claude Code). Design and limits: [ADR-0003](docs/adr-0003-push-into-idle-session.md).

The agent home is `~/Library/Application Support/ClaudeMonitor` on macOS, `%USERPROFILE%\.claude-monitor` on
Windows and `~/.claude-monitor` on Linux (`CM_AGENT_HOME` overrides all three); `cm-agent status` prints it as `home:`. It is deliberately **not** under
`%LOCALAPPDATA%`: the Claude desktop app is a packaged (MSIX) app, and everything it starts sees `%LOCALAPPDATA%`
redirected to a private copy, so a login made in a normal terminal would be invisible to its sessions
(ADR-0002, "Home outside AppData").

**Upgrading a Windows machine** that used the old default (`%LOCALAPPDATA%\ClaudeMonitor`): the first start of the new
`cm-agent` copies `agent.json` to the new home (no new login; `agent.db` is not moved, so events not yet uploaded are
lost). The old plugin keeps running the old binary until you run `cm-agent install` again with the new one; do that
from a normal terminal, then check that `cm-agent status` and `monitor_status` in a new Claude session show the same
`home:`. The old folder is left in place and can be deleted afterwards.

## Service agent (boot service, remote work)

`cm-agent install --service` registers the daemon to start at boot with no Claude session, so other machines of a
workspace can be reached (ADR-0005). It is opt-in and needs root / an elevated prompt; the interactive agent above is
unchanged. The step-by-step runbook is [docs/remote-work-setup.md](docs/remote-work-setup.md). What the install lays
down, per OS:

| | Linux (systemd) | macOS (LaunchDaemon) | Windows Server (service) |
|---|---|---|---|
| Service account | `cm-agent` (system, no login shell) | `_cmagent` (hidden role account) | `NT SERVICE\cm-agent` (virtual) |
| Binary (admin-owned) | `/usr/local/lib/cm-agent/cm-agent` | `/Library/Application Support/ClaudeMonitor/bin/cm-agent` | `%ProgramFiles%\ClaudeMonitor\cm-agent.exe` |
| Service home (account only) | `/var/lib/cm-agent` | `/Library/Application Support/ClaudeMonitor/home` | `%ProgramData%\ClaudeMonitor\service` |
| Exec policy file (admin-owned) | `/etc/cm-agent/exec.json` | `/Library/Application Support/ClaudeMonitor/exec.json` | `%ProgramFiles%\ClaudeMonitor\exec.json` |
| Unit / definition | `/etc/systemd/system/cm-agent.service` | `/Library/LaunchDaemons/com.claudemonitor.agent.plist` | service `cm-agent` (`sc query cm-agent`) |
| Login code (while not connected) | `<home>/login-code.txt`, `journalctl -u cm-agent` | `<home>/login-code.txt` | `<home>\login-code.txt` |

The service units set `CM_SERVICE=1`, `CM_AGENT_HOME=<service home>` and the single-file extraction folder
(`DOTNET_BUNDLE_EXTRACT_BASE_DIR=<home>/.net`); nobody sets them by hand. `cm-agent uninstall --service` removes the
service and **keeps** the home (the login), the exec policy and the binary.

**The exec policy file** is what decides how much a remote run may do on that machine. The service account cannot
write it (on Unix it must be owned by root and not group- or world-writable, as must its folder, and neither may be a
link; on Windows the service must not be able to open it for writing) and **anything doubtful means `off`**: a missing,
unreadable, invalid or writable file grants nothing. `cm-agent install --service --exec off|argv|shell` writes it
(default `off`; `shell` is a separate, stronger opt-in); an admin may edit it afterwards and restart the service:

```json
{
  "level": "argv",
  "allowedExecutables": ["/usr/bin/journalctl", "/usr/bin/tail"],
  "allowedRoots": ["/var/log", "/srv/app/releases"]
}
```

`level` is `off`, `argv` or `shell`. `allowedExecutables` and `allowedRoots` are the optional local **ceiling**: a
list of absolute executables a run may start and of absolute folders a path argument must stay under (Windows paths
likewise, e.g. `C:\Program Files\Git\cmd\git.exe`). A file with a ceiling never allows shell runs (shell text cannot be
held to it). An empty or missing list adds no restriction of its own; the
web grants and the agent's own checks (never a shell or interpreter in a grant, no `/etc`, `/home`, the agent's home)
apply either way. A relative path makes the whole file invalid, hence `off`. The API can never raise this level.

## Updating the agent

Design, trust model and limits: [ADR-0004](docs/adr-0004-agent-self-update.md). **Off by default**: an agent never asks the server for an update
unless a person runs the command below or turns the setting on.

```bash
cm-agent update --check                  # look only: prints "update available: <version>" or why none is taken
cm-agent update                          # install the newest signed build of this agent's channel
cm-agent config auto-update off|check|on # what the daemon may do by itself (default off); no argument prints the setting
```

- `cm-agent update` and `--check` are your own decision and ignore both settings below. The agent must be logged in (the build comes from its own
  server) and installed (`cm-agent install`); the installed binary in the agent home is what is replaced. Exit code 0 = installed or up to date, 1 = refused or failed, 2 = usage.
- **Machine setting:** `cm-agent config auto-update off|check|on` (saved in `agent.json`) or `CM_AUTO_UPDATE` (wins when set). `check` looks every 6 h and reports; `on` also installs.
- **Workspace setting** `agentUpdate` (`off` default, `check`, `on`): the cap an owner or admin sets in the workspace settings on the web. **The lower of the two applies.**
  While the effective mode is `off` the daemon makes no update call at all.
- `cm-agent status` shows `auto-update: <effective> (this machine: <m>, workspace: <w>)`, `update available: <version>`, `update: <from> -> <to> is being checked` while a new
  daemon is on probation, and `last update check: <code> (<reason>) <dd/mm/yyyy HH:mm>`. After a refused update the code says why (for example `bad-signature`, `channel`,
  `downgrade`, `hash-mismatch`, `no-key`); after a rolled-back one `status` also prints `update <version> was rolled back and is not retried by itself`. The same lines are in `agent.log`.
- **Replacement:** the new binary is staged as `cm-agent.new`, the old one is kept as `cm-agent.prev` (one version back), the daemon is stopped through a `daemon.stop`
  file and started again from the new binary. If it does not report its version and get an answer from the API within 90 s, the previous binary is put back automatically.
- **MCP processes** (one per Claude Code session) keep running the old version until their session ends; they are not killed. Hooks use the new binary at once.
- **The standards repository's `install.py`** may run `cm-agent update` instead of rebuilding when an agent is already installed (it only needs the exit code); that is a
  choice of that script, nothing here depends on it.

### Releasing an agent build (maintainer, macOS)

1. **Once per channel,** generate the signing key. The private half goes to your Keychain (service `cm-agent-update-<channel>`) and is never written to a file; the public half is printed:
   `python3 scripts/sign_manifest.py keygen --channel test` and `... --channel prod`. Save the printed line as `deploy/update-keys/<channel>.pub` and commit it (it is public by design).
2. Build: `AGENT_UPDATE_CHANNEL=test|prod bash scripts/build-agent.sh` (optional `AGENT_MIN_SUPPORTED=x.y.z`, default = this version; `AGENT_SIGN_IDENTITY` and `AGENT_NOTARY_PROFILE` as before).
   The channel's public key is built into the agent; `out/downloads` gets the six zips (macOS, Windows, Linux), `SHA256SUMS` and a signed `manifest.json`. Without `AGENT_UPDATE_CHANNEL` the agent has no key, cannot update itself, and no manifest is written.
3. Deploy copies `downloads/` as before (`docs/deploy-windows.md`). **The server offers whatever its web root holds**, so deploy test builds to test and prod builds to prod.
4. Agents already installed at that channel take it when a person runs `cm-agent update` or when `auto-update` allows it.

## Updating Claude Code (optional, off by default)

Design and limits: [ADR-0006](docs/adr-0006-claude-code-update.md). The agent can run Claude Code's own `claude update`, and nothing else:
it never downloads or replaces a Claude binary and never ends or restarts a session (running sessions keep their version until their
owner restarts them). It needs BOTH switches, and a CLI install (npm or native; the desktop app updates itself and is never touched):

```bash
cm-agent config claude-update on         # this machine; the workspace admin must also tick "Allow agents to update Claude Code"
cm-agent claude-update cancel            # stop a countdown that is running (not tried again for a day)
cm-agent status                          # shows claude-update: on/off, a running countdown, the last result and versions
```

It runs only when every Claude session on the machine has been idle for 10 minutes, after a 5-minute countdown announced in `agent.log`,
`cm-agent status` and (macOS only) a desktop notification. `claude update` has no dry run, so an enabled machine gets this notice about
once a day even when Claude Code is current. Not verified on this repository's machines: the native-installer paths, Windows, and the
Windows notification (not built).

## Browser tests (e2e)

Playwright (`e2e/`, Chromium and WebKit) drives the real web app and API. It is optional (global #33): the
`test -> prod` gate runs it only with `GATE_RUN_E2E=1`, or run it by hand. With `E2E_BASE_URL` set it runs against the test environment (its mail must reach a Mailpit at
`E2E_MAILPIT_URL`); without it, `e2e/serve-local.sh` starts its own PostgreSQL and Mailpit (`e2e/services.yml`,
throw-away), builds the web app and runs the API on `E2E_PORT`.

```bash
cd e2e && npm ci && npx playwright install chromium webkit   # once
npx playwright test
```

## Server (test and production)

Windows Server with IIS, the .NET 10 Hosting Bundle and PostgreSQL 18, behind Cloudflare: the whole runbook (set-up,
release, backups, restore drill, rollback) is [docs/deploy-windows.md](docs/deploy-windows.md).

## Environment variables

`.env.example` lists every variable with its default and what it is for. The API reads them only in
`src/ClaudeMonitor.Api/Config/ApiConfig.cs`.

## Secret and token inventory

The repository holds **no secrets**. The API's live in the server's environment (a root-only environment file
read by the service, never committed); development uses the local defaults in `.env.example`, which are not
secrets.

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| `MONITOR_DB` password | the API's PostgreSQL role | created with the database (`deploy/`) | server environment file | the maintainer | yearly, or on any leak: `ALTER ROLE ... PASSWORD`, update the file, restart |
| `MONITOR_SMTP_PASSWORD` | an app password of the sending Google account | Google Account, Security, App passwords (2-step verification on) | server environment file, or the `Smtp` node of the server-only `appsettings.Production.json` (git-ignored; `build-release.sh` refuses to package it) | the maintainer | revoke and create a new one yearly or on any leak |
| `MONITOR_GITHUB_CLIENT_SECRET` | the GitHub OAuth app's secret; callback `https://<host>/api/auth/callback/github` | GitHub, Settings, Developer settings, OAuth Apps (one app per environment) | server environment file | the maintainer | generate a new secret, deploy, delete the old one |
| `MONITOR_MFA_KEY` | seals every user's two-step sign-in secret in the database; lost or changed, every user's authenticator stops working | generated by `deploy/windows/setup-server.ps1` | server environment file **and** the maintainer's password manager | the maintainer | only with a re-seal of the stored secrets (no rotation tool yet): treat as long-lived; on a leak, users turn two-step sign-in off and on again |
| `MONITOR_BACKUP_KEY` | the key the backups are encrypted with; without it no backup restores | generated by `deploy/windows/setup-server.ps1` | server environment file **and** the maintainer's password manager | the maintainer | never silently: a new key only for new backups; keep the old one until its backups expire |
| the Cloudflare Origin certificate and key | TLS between Cloudflare and the server | Cloudflare, SSL/TLS, Origin Server | the server's `LocalMachine\My` store (key not exportable) | the maintainer | before it expires (15 years), or on any leak: revoke in Cloudflare, issue a new one |
| the PostgreSQL `postgres` password | the database superuser (set-up and restore drills only) | chosen at the PostgreSQL install | the maintainer's password manager only | the maintainer | yearly |
| `MONITOR_GOOGLE_CLIENT_SECRET` | the Google OAuth client's secret; redirect `https://<host>/api/auth/callback/google` | Google Cloud Console, APIs and Services, Credentials (one client per environment) | server environment file | the maintainer | add a new secret, deploy, disable the old one |

A **service agent** (ADR-0005) holds one more secret, on each machine that runs one. It is never in the repository,
never in a log and never leaves the machine except in the `Authorization` header to the API:

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| a service agent's credentials (`cred-access-token`, `cred-refresh-token`) | the access and refresh tokens of that machine's service agent, issued when its login code is approved on the web | `cm-agent` service's own first login (the code is in its log and `login-code.txt`, approved on the web) | Linux and macOS: 0600 files `cred-*` in the service home (`/var/lib/cm-agent`, `/Library/Application Support/ClaudeMonitor/home`), refused if their owner or mode is wrong; Windows: DPAPI **CurrentUser** blobs `cred-*.bin` in `%ProgramData%\ClaudeMonitor\service` (never LocalMachine scope) | the machine's admin | on any doubt: revoke the agent on the web (Machines page), then log the service in again. Refresh tokens rotate on every use, so there is nothing else to rotate on a schedule |

A remote run that executes as the service account can read that account's files, these tokens included
(ADR-0005, "Consequences"); that is why the exec level is local, admin-owned and `off` by default.

Already present on the maintainer's machine, for the macOS installer of phase 4 (they live in the macOS

Already present on the maintainer's machine, for the macOS installer of phase 4, and the two update-signing keys
of ADR-0004, created by `keygen` at the first release of each channel (they live in the macOS
keychain, never in the repository, the shell environment or CI):

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| the `claude-monitor-notary` keychain profile | the stored App Store Connect API credentials `notarytool` uses | created by `xcrun notarytool store-credentials` | the macOS keychain | the maintainer | revoke the API key in App Store Connect and store a new profile |
| the Developer ID Application certificate | signs the agent binary | Apple Developer account, Certificates | the macOS keychain (its name is not secret) | the maintainer | when it expires or is revoked |
| `cm-agent-update-test` signing key | the ECDSA P-256 private key that signs `test` agent builds for self-update (ADR-0004) | `python3 scripts/sign_manifest.py keygen --channel test`, once | the maintainer's macOS keychain, service `cm-agent-update-test` (never a file, the repository, CI or a server); the public half is `deploy/update-keys/test.pub` | the maintainer | new key + a new build installed by hand on every `test` agent: installed agents trust only the key built into them. `keygen` never overwrites an existing key |
| `cm-agent-update-prod` signing key | the same, for `prod` agent builds | `python3 scripts/sign_manifest.py keygen --channel prod`, once | the maintainer's macOS keychain, service `cm-agent-update-prod`; public half `deploy/update-keys/prod.pub` | the maintainer | as above; **lost or leaked, installed prod agents cannot be updated and must be reinstalled by hand** |

## Backup

Nothing to back up yet: the code is in git and there is no runtime data. The hosted database arrives in
phase 4 with backups and a monthly restore drill (global #18), documented here then.
