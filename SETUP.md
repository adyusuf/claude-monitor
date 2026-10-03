# Setup

A clean machine is set up by following this file. The repository is being rebuilt
([ADR-0002](docs/adr-0002-agent-platform.md)): today it holds the design and the gate. Each phase that adds a
codebase (.NET API and agent, web app, e2e) adds its tools, variables and secrets here in the same change.

## Prerequisites

| Tool | Why | Check |
|---|---|---|
| git | the repository and the gate | `git --version` |
| Python 3.9+ | the gate tools' tests | `python3 --version` |
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
bash scripts/merge-gate.sh dev
```

## CI

`.github/workflows/ci.yml` runs `bash scripts/gate-core.sh dev` on every push to `dev`, `test` and `prod`
and on every pull request: the same script and the same thresholds as a local `scripts/merge-gate.sh dev`,
never a second rule set. Third-party actions are pinned to a commit SHA and gitleaks is
checksum-verified. To change a version, change it there and in the Prerequisites table above.

## Environment variables

None yet. `.env.example` lists every variable as soon as one is read.

## Secret and token inventory

The repository holds **no secrets**, and nothing it runs today reads one. What is already known to come
(ADR-0002): the database connection, the SMTP account, the GitHub and Google OAuth client secrets, and the
signing credentials for the installers. Each enters this table in the change that first uses it.

Already present on the maintainer's machine, for the macOS installer of phase 4 (they live in the macOS
keychain, never in the repository, the shell environment or CI):

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| the `claude-monitor-notary` keychain profile | the stored App Store Connect API credentials `notarytool` uses | created by `xcrun notarytool store-credentials` | the macOS keychain | the maintainer | revoke the API key in App Store Connect and store a new profile |
| the Developer ID Application certificate | signs the agent binary | Apple Developer account, Certificates | the macOS keychain (its name is not secret) | the maintainer | when it expires or is revoked |

## Backup

Nothing to back up yet: the code is in git and there is no runtime data. The hosted database arrives in
phase 4 with backups and a monthly restore drill (global #18), documented here then.
