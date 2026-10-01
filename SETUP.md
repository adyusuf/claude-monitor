# Setup

A clean machine is set up by following this file.

## Prerequisites

| Tool | Why | Check |
|---|---|---|
| macOS or Linux, with `lsof` and `ps` | replacing a stale server at session start; Windows is not supported | `lsof -v`, `ps -p $$` |
| Python 3.9+ | the hooks, CLI and server (standard library only) | `python3 --version` |
| git | project roots and worktrees | `git --version` |
| Claude Code | the source of the hooks and transcripts | `claude --version` |
| Node 20+ | only to run the page's tests | `node --version` |
| `coverage` (Python package, in a venv outside the repository) | the coverage gate | see below |
| gitleaks, ShellCheck, CodeQL CLI | the secret scan, and SAST for the shell and Python | `gitleaks version`, `shellcheck --version`, `codeql version` |

## Install

```bash
git clone https://github.com/adyusuf/claude-monitor.git ~/ClaudeCode/claude-monitor
ln -s ~/ClaudeCode/claude-monitor/scripts/board ~/.claude/scripts/board   # the stable path the hooks call
```

If `~/.claude/scripts` already exists as a directory with other tools, link the individual entry
points instead (`board.py`, `board_ensure.py`, `board_hook.py`, `board_open.py`, `board_server.py`
resolving to this clone), or keep a launcher at those names.

Every repository and folder at once: `python3 ~/.claude/scripts/board/board.py enable --user` (writes
`~/.claude/settings.json`, backup in `~/.claude/backups/`). Or enable a single repository: `python3 ~/.claude/scripts/board/board.py enable` (idempotent), commit
`.claude/settings.json` and `.gitignore`. Details: [`docs/live-board.md`](docs/live-board.md) §2.

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
python3 -m venv ~/.cache/claude-monitor/venv && ~/.cache/claude-monitor/venv/bin/pip install coverage
bash scripts/merge-gate.sh dev
```

## CI

`.github/workflows/ci.yml` runs `bash scripts/gate-core.sh dev` on every push to `dev`, `test` and `prod`
and on every pull request: the same script and the same thresholds as a local `scripts/merge-gate.sh dev`,
never a second rule set. Third-party actions are pinned to a commit SHA and gitleaks is
checksum-verified. To change a version, change it there and in the Prerequisites table above.

## Environment variables

None is required; defaults are in `scripts/board/board_config.py`, the single configuration module.
The optional ones are listed, with their defaults, in `.env.example`.

## Secret and token inventory

This application holds **no secrets and no tokens**. The page is served on loopback only and its
controls accept same-origin JSON only. Runtime data (`.claude/board/events.jsonl`, `control.json`)
is created `0600` and is git-ignored; it contains task titles, notes and session metadata, so treat
it as private. Nothing to rotate. If a future change adds a credential, it goes here in the same pull request.

## Backup

Nothing here needs a backup: the runtime data is derived and disposable, the code is in git.
