# claude-monitor

A live task board for Claude Code: hooks write an event log, one local server shows every
project's board. Design: `docs/live-board.md`. The maintainer's global rules apply; these are
this repository's own.

## Rules

- **Standard library only.** No runtime dependency is added without asking first (global #10).
- **One exception: `desktop/`.** The desktop window is a thin Tauri shell (approved by the
  maintainer). Its Rust crates live only there; it holds no logic, no URL and no port of its own,
  and asks `scripts/board/` for them. Everything else stays standard-library.
- **One config module.** `scripts/board/board_config.py` is the only place that reads the
  environment or holds a URL, port, path or price; every other file imports from it (#2).
- **Loopback only.** The server binds `127.0.0.1`; the page's controls accept same-origin,
  validated JSON and are fail-closed (#6).
- **Hooks never block a session.** A hook that fails prints to stderr and exits 0.
- **A test never writes the real registry or a real board.** Patch `REGISTRY` / use temporary
  directories (a test once leaked a project into the real registry).
- **Public repository.** No real project, customer or personal names in code, tests, docs or
  commit messages; no secrets. Fixtures use neutral names.
- **Tests:** Python `python3 -m unittest discover -s scripts/tests -p 'test_*.py'`, page
  `node --test 'scripts/tests/**/*.test.js'`. Coverage ≥ 80% per codebase (#29).
- **Gate:** `bash scripts/merge-gate.sh <dev|test|prod>` before every promotion.
