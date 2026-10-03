# Setup

A clean machine is set up by following this file.

## Prerequisites

| Tool | Why | Check |
|---|---|---|
| macOS or Linux, with `lsof` and `ps` | replacing a stale server at session start; Windows is not supported | `lsof -v`, `ps -p $$` |
| Python 3.9+ | the hooks, CLI and server (standard library only) | `python3 --version` |
| git | project roots and worktrees | `git --version` |
| Claude Code | the source of the hooks and transcripts | `claude --version` |
| Node 20+ | the page's tests, and the browser tests (`e2e/`) | `node --version` |
| `coverage` (Python package, in a venv outside the repository) | the coverage gate | see below |
| Rust (rustup) with `llvm-tools-preview`, `cargo-llvm-cov`, `cargo-audit` | only for the desktop window (`desktop/`), its coverage and its dependency scan | `rustc --version`, `cargo llvm-cov --version`, `cargo audit --version` |
| Playwright's Chromium and WebKit (about 150 MB, from `e2e/package.json`) | only the browser tests, at the `test -> prod` gate | `cd e2e && npx playwright install chromium webkit` |
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

## Desktop window

An optional native window around the board (docs/live-board.md §2g). Skip it if the browser's
"Install" menu or `board_open.py` is enough.

```bash
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs -o rustup-init.sh   # read it, then:
sh rustup-init.sh -y --profile minimal -c llvm-tools-preview
cargo install cargo-llvm-cov cargo-audit --locked   # the gate: coverage, dependency CVE
python3 desktop/make_icon.py                     # draws desktop/icons/icon.png (git-ignored)
cd desktop && cargo run                          # opens the window (starts the board server if needed)
```

macOS and Linux only. On Linux Tauri also needs the WebKitGTK development packages (see CI).
`BOARD_SCRIPTS_DIR` points at `scripts/board` if it is not linked at `~/.claude/scripts/board`.
An app bundle: `cargo install tauri-cli --locked --version '^2'`, then `cd desktop && cargo tauri build`
writes `desktop/target/release/bundle/macos/Claude Monitor.app`. It is unsigned, so macOS asks you to
confirm the first start (right-click, Open). A Finder-started app has a minimal `PATH`: it needs
`/usr/bin/python3` and finds the scripts at `~/.claude/scripts/board` (or `BOARD_SCRIPTS_DIR`, which a
Finder-started app does not inherit from your shell, so link the clone there).

**A signed, notarised build** (only needed to hand the `.app` to someone else; a locally built one opens
without it). Done once on 03/10/2026, outside the Mac App Store (`docs/adr-0001-mac-app-store.md` explains
why the store does not fit this design as it stands). It needs a paid Apple Developer Program membership and
two things in the maintainer's own keychain, so no secret ever enters the repository, the shell environment
or a tool's context:

1. a **Developer ID Application** certificate (developer.apple.com, Certificates; the CSR comes from Keychain
   Access with the `login` keychain selected). Check: `security find-identity -v -p codesigning`.
2. a **`notarytool` profile**, stored once by the maintainer with an App Store Connect API key (Users and Access,
   Integrations, App Store Connect API; the `.p8` downloads only once):
   `xcrun notarytool store-credentials claude-monitor-notary --key <path to .p8> --key-id <KEY_ID> --issuer <ISSUER_ID>`
   (the `<...>` are replaced by the real values, without the angle brackets).

```bash
cd desktop
APPLE_SIGNING_IDENTITY="Developer ID Application: <name> (<TEAMID>)" cargo tauri build --bundles app
# Tauri signs with the hardened runtime and a secure timestamp; it skips its own notarisation because no APPLE_* credentials are set
APP="target/release/bundle/macos/Claude Monitor.app"
ditto -c -k --keepParent "$APP" /tmp/ClaudeMonitor.zip
xcrun notarytool submit /tmp/ClaudeMonitor.zip --keychain-profile claude-monitor-notary --wait   # the first submission took over an hour
xcrun stapler staple "$APP" && spctl -a -vv -t exec "$APP"
```

`spctl` should print `accepted` and `source=Notarized Developer ID`. If `--wait` is cut short, the submission is
still on Apple's side: `xcrun notarytool info <id> --keychain-profile claude-monitor-notary`, never a second
submission. A rejected one: `xcrun notarytool log <id> --keychain-profile claude-monitor-notary`. The identity
and profile names are recorded in the maintainer's notes, not secrets; the key and the `.p8` never are.

## Where Claude Code runs it

The board is fed by Claude Code's hooks, so it works wherever the hooks fire. Checked on 02/10/2026:

| Surface | Status | Evidence |
|---|---|---|
| Desktop app | works | boards on this machine hold sessions whose transcript entrypoint is `claude-desktop` |
| Terminal (`claude`) | hooks fire | a headless `claude -p` run in a temporary folder wrote `turn_start` and `turn_stop` to its board. An interactive terminal session was not driven by hand. |
| VS Code extension | **not verified** | no VS Code session exists on this machine, so none could be observed. It should behave like the others (same hooks), but that is an assumption. |

How a queued message reaches an idle session differs per surface: `docs/live-board.md` §2d.

## Browser tests (e2e)

Playwright drives a real Chromium and a real WebKit (the engine of the desktop window's WKWebView and of Safari) against a real board server that `e2e/serve.py` starts on a seeded
temporary project (its own registry, transcripts and skills folder, removed on exit): the user's boards are
never touched. Dev-only; nothing here ships. Written and run at the `test -> prod` gate (global #33); the gate
runs it through `E2E_WEB_CMD` in `scripts/merge-gate.conf`.

```bash
cd e2e && npm ci && npx playwright install chromium webkit   # once
npm test                                              # 13 tests x 2 engines, about 30 s; E2E_PORT / E2E_PYTHON in .env.example
```

The board's service worker passes every request through, and Playwright cannot intercept a request a service
worker makes, so the two specs that fake a failed request run with the worker blocked (`test.use`). The
suite changes the seeded board (removes a task, queues a message), each on rows of its own, so it can be
repeated on one server (`--repeat-each`).

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
python3 scripts/mutation_check.py scripts/board/board_store.py --tests test_board   # before a release: docs/release.md
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

Only if you build a signed `.app` (see "Desktop window"): these live in your shell or keychain, never in
the repository, and CI does not use them.

| Secret | What it is | Where to get it | Stored | Owner | Rotation |
|---|---|---|---|---|---|
| the `claude-monitor-notary` keychain profile | the stored App Store Connect API credentials `notarytool` uses | created by `store-credentials` on the maintainer's machine | the macOS keychain | the maintainer | revoke the API key in App Store Connect and store a new profile |
| `APPLE_SIGNING_IDENTITY` | the name of a Developer ID Application certificate | Apple Developer account, Certificates | the macOS keychain (the name itself is not secret) | the maintainer | when the certificate expires or is revoked (yearly to five-yearly) |
| `APPLE_API_KEY`, `APPLE_API_ISSUER`, `APPLE_API_KEY_PATH` | App Store Connect API key used to notarise | App Store Connect, Users and Access, Keys | the `.p8` file outside the repository, `chmod 600` | the maintainer | revoke and reissue yearly, or on any leak |
| `APPLE_ID`, `APPLE_PASSWORD`, `APPLE_TEAM_ID` | the alternative to the API key (an app-specific password) | appleid.apple.com | your shell session only | the maintainer | revoke the app-specific password after the build |

## Backup

The code is in git and needs no backup. The runtime data does **not** regenerate itself:
`.claude/board/events.jsonl` is the primary, append-only record of a project's tasks, notes, decisions and
agent activity, and the board page is a fold over it (`control.json` holds what the page asked of the
sessions). The registry (`~/.cache/claude-board/projects.json`) can be rebuilt by starting a session in each
project. Losing `events.jsonl` loses that project's board history and nothing else, and that is accepted:
the board is a progress view, not a system of record, and there is no scheduled backup, offsite copy or
restore drill. Copy a project's `.claude/board/` directory first if you want to keep one.
