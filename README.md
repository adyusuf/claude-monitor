# claude-monitor

A live task board for [Claude Code](https://claude.com/claude-code). It shows, while Claude works,
which task is where, which agent runs on it, what each session cost and how full its context is,
what finished — and lets you queue work, switch an agent off or answer a decision from the page.

- **One page for every project** on `http://127.0.0.1:8765`, one tab per project. Served on
  `127.0.0.1` only; nothing is published.
- **Hooks, not polling of Claude:** Claude Code's hooks record agent starts and stops, todo and task
  tool calls and prompts into an append-only `events.jsonl` per project; the page folds it into state.
- **Cost is measured** from the session transcripts (tokens × the prices in `scripts/board/board_config.py`),
  never guessed; an unpriced model shows "cannot be measured".
- **Standard library only.** Python 3.9+ for the server and hooks, plain HTML/JS for the page.
  Node is needed only to run the page's tests.

The full design (data model, controls, cost, channels) is in [`docs/live-board.md`](docs/live-board.md).
Setup and the list of environment variables are in [`SETUP.md`](SETUP.md).

## Quick start

```bash
git clone https://github.com/adyusuf/claude-monitor.git ~/ClaudeCode/claude-monitor
# Claude Code's hooks call a stable path; point it at the clone:
ln -s ~/ClaudeCode/claude-monitor/scripts/board ~/.claude/scripts/board
# in the repository you want on the board:
python3 ~/.claude/scripts/board/board.py enable
```

`enable` merges the hook block into the repository's `.claude/settings.json` and adds
`.claude/board/` to `.gitignore`; commit those two files. Start a Claude Code session in that
repository: the `SessionStart` hook starts the server if nothing answers and prints where the
board is. Write the plan with the CLI:

```bash
B=~/.claude/scripts/board/board.py
python3 $B add auto "Fix the login redirect" --branch fix/login --role developer   # prints T-1
python3 $B set T-1 --status waiting --note "tests later"
python3 $B list
```

## Layout

| Path | What |
|---|---|
| `scripts/board/` | The application: hooks (`board_hook.py`), auto-start (`board_ensure.py`), CLI (`board.py`), server (`board_server.py`), state (`board_store.py`), cost (`board_cost.py`), page (`board.html`, `board_ui*.js`) |
| `scripts/tests/` | Unit tests: `python3 -m unittest discover -s scripts/tests -p 'test_*.py'` and `node --test 'scripts/tests/**/*.test.js'` |
| `scripts/gate-core.sh`, `scripts/merge-gate.sh` | The local merge gate: tests, line coverage ≥ 80% per codebase, secret scan, SAST |
| `docs/live-board.md` | Design reference |

## Tests and the gate

```bash
bash scripts/merge-gate.sh dev      # everything the CI would run, locally
```

Line coverage is measured per codebase (Python and JavaScript separately) and must be at least 80%.

## License

[MIT](LICENSE)
