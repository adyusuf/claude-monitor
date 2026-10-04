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
dotnet publish src/ClaudeMonitor.Agent -c Release -r osx-arm64 -o out/osx-arm64   # one self-contained binary (also osx-x64, win-x64, win-arm64)
out/osx-arm64/cm-agent login --server http://localhost:5080            # shows a code; approve it on the web (/device)
out/osx-arm64/cm-agent install                                         # copies itself to the agent home and registers the Claude Code plugin
out/osx-arm64/cm-agent status
```

`install` registers a local plugin marketplace (`monitor-agent-local`) whose hooks and MCP server run the
installed binary, so Claude Code starts the agent with its sessions; nothing is registered with the OS. Without
the `claude` CLI on `PATH` it prints the two `claude plugin` commands to run. The agent's tokens are in the macOS
Keychain / Windows Credential Manager (service `claude-monitor-agent`); its log is `agent.log` in the agent home.

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
| `MONITOR_SMTP_PASSWORD` | an app password of the sending Google account | Google Account, Security, App passwords (2-step verification on) | server environment file | the maintainer | revoke and create a new one yearly or on any leak |
| `MONITOR_GITHUB_CLIENT_SECRET` | the GitHub OAuth app's secret; callback `https://<host>/api/auth/callback/github` | GitHub, Settings, Developer settings, OAuth Apps (one app per environment) | server environment file | the maintainer | generate a new secret, deploy, delete the old one |
| `MONITOR_MFA_KEY` | seals every user's two-step sign-in secret in the database; lost or changed, every user's authenticator stops working | generated by `deploy/windows/setup-server.ps1` | server environment file **and** the maintainer's password manager | the maintainer | only with a re-seal of the stored secrets (no rotation tool yet): treat as long-lived; on a leak, users turn two-step sign-in off and on again |
| `MONITOR_BACKUP_KEY` | the key the backups are encrypted with; without it no backup restores | generated by `deploy/windows/setup-server.ps1` | server environment file **and** the maintainer's password manager | the maintainer | never silently: a new key only for new backups; keep the old one until its backups expire |
| the Cloudflare Origin certificate and key | TLS between Cloudflare and the server | Cloudflare, SSL/TLS, Origin Server | the server's `LocalMachine\My` store (key not exportable) | the maintainer | before it expires (15 years), or on any leak: revoke in Cloudflare, issue a new one |
| the PostgreSQL `postgres` password | the database superuser (set-up and restore drills only) | chosen at the PostgreSQL install | the maintainer's password manager only | the maintainer | yearly |
| `MONITOR_GOOGLE_CLIENT_SECRET` | the Google OAuth client's secret; redirect `https://<host>/api/auth/callback/google` | Google Cloud Console, APIs and Services, Credentials (one client per environment) | server environment file | the maintainer | add a new secret, deploy, disable the old one |

Already present on the maintainer's machine, for the macOS installer of phase 4 (they live in the macOS
keychain, never in the repository, the shell environment or CI):

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| the `claude-monitor-notary` keychain profile | the stored App Store Connect API credentials `notarytool` uses | created by `xcrun notarytool store-credentials` | the macOS keychain | the maintainer | revoke the API key in App Store Connect and store a new profile |
| the Developer ID Application certificate | signs the agent binary | Apple Developer account, Certificates | the macOS keychain (its name is not secret) | the maintainer | when it expires or is revoked |

## Backup

Nothing to back up yet: the code is in git and there is no runtime data. The hosted database arrives in
phase 4 with backups and a monthly restore drill (global #18), documented here then.
