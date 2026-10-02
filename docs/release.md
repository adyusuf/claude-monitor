# Release and rollback

claude-monitor is a local tool: there is no server to deploy. "Live" means the clone that a machine's
Claude Code hooks run (`~/ClaudeCode/claude-monitor`, or `$CLAUDE_MONITOR_HOME`), checked out on `prod`.

## Promotion

`dev` -> `test` -> `prod`, each a fast-forward made by the maintainer after `scripts/merge-gate.sh <target>`
is green; CI runs the same gate on every push (`.github/workflows/ci.yml`). Nothing is pushed to `prod` by a tool.
After a promotion, update the clone (`git -C <clone> pull --ff-only`) and tag the promoted commit, so that a
rollback target has a name:

```bash
git tag -a prod-DD-MM-YYYY -m "<one line: what shipped>" <sha> && git push origin prod-DD-MM-YYYY
```

A second promotion on the same day appends `-2`. A hotfix says so in the tag message.

## Before a release: does the suite notice a fault?

Coverage says a line ran, not that anything checks it. `scripts/mutation_check.py` changes one thing at a
time in a module (a comparison flipped, `and` for `or`, a boolean inverted, a number moved by one, a `not`
dropped) in a throw-away copy and runs the module's tests; a change nothing notices is a **survivor**.
It takes minutes, so it is run by hand on the modules a release touched, not in the gate:

```bash
python3 scripts/mutation_check.py scripts/board/board_store.py --tests test_board test_board_fuzz
python3 scripts/mutation_check.py scripts/board/board_tasks.py --tests test_board_tasks --max 40 --min-score 0.8
```

Read each survivor: write the missing test, or note why the change is harmless (an *equivalent mutant*: for
example `split("-", 1)` -> `split("-", 2)` on an id that has one dash). A file mode such as `0o600` showing up
as a survivor means no test reads the mode. The working tree is never touched.

First use, on `board_store.py`: 33 of 60 changes caught (55%). The 27 survivors became 18 tests
(`test_board_store_behaviour.py`) and the rate went to 56 of 60 (93%); what is left is a subprocess timeout
(`5` -> `6`), the JSON `indent`, and one guard no event sequence can reach.

## How a new version takes effect

The hooks start no new code by themselves; the next Claude session start does. `board_ensure.py` compares the
build fingerprint the running server reports (`/api/info`, a hash of the `.py`, `.html` and `.js` files) with the
files on disk and replaces a server that runs older code. That works in both directions, so it also applies a
rollback. To apply a change at once, stop the server and start any session; the next start brings up the
right one:

```bash
kill "$(lsof -nP -iTCP:8765 -sTCP:LISTEN -t)"   # the port is BOARD_PORT
```

## Rollback

1. **Fast, local, reversible** (the clone is what runs, so moving it is the rollback):

   ```bash
   git -C <clone> fetch --tags
   git -C <clone> switch --detach <previous prod tag or sha>
   ```

   Start a session (or stop the server as above) and check the line the session prints: it says the server was
   restarted because it was running older code. `curl -s http://127.0.0.1:8765/api/info` must report the build of
   the commit you switched to (`python3 -c "import sys; sys.path.insert(0, '<clone>/scripts/board'); import board_config; print(board_config.code_build())"`).
2. **Permanent**: a revert commit goes through `dev`, `test` and `prod` like any other change. `prod` is never
   force-pushed, and a hotfix still carries "e2e skipped (hotfix)" in the report (this repository has no e2e suite).
3. **Back to normal**: `git -C <clone> switch prod && git -C <clone> pull --ff-only`.

## What a rollback does to the data

There is no migration, so there is nothing to reverse and no `DROP`. `events.jsonl` is append-only and the
fold ignores an event type it does not know (checked: a log with an unknown type folds to the same tasks), so an
older version reads a log written by a newer one and simply does not show what it does not understand.
`control.json` carries a version number and is rewritten whole. Events written while on a newer version stay
in the log and appear again after the roll forward. No backup is taken before a promotion, because the
promotion changes no data (see the Backup section in `SETUP.md`).

## Flags

There is no feature flag. A change that must be switchable ships as an environment variable with a default in
`scripts/board/board_config.py` (the same way `BOARD_ALLOW_REMOTE` does).
