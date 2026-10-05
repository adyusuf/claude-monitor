# Release and rollback

## Promotion

`dev` -> `test` -> `prod`, each a `--no-ff` merge after `scripts/merge-gate.sh <target>` is green; CI runs the
same gate on every push (`.github/workflows/ci.yml`). The `test` -> `prod` gate also needs the full e2e suite
green (global #33); until the web app and its suite exist (ADR-0002, phase 3) that step reports "skipped" and
the prod gate is not green. Every prod promotion is tagged, so a rollback target has a name:

```bash
git tag -a prod-DD-MM-YYYY -m "<one line: what shipped>" <sha> && git push origin prod-DD-MM-YYYY
```

A second promotion on the same day appends `-2`. A hotfix says so in the tag message.

## Rebuild in progress: `prod` stays on the old board until the cut-over

`prod` still holds the removed local board, and the maintainer's machine runs it from a clone checked out on
`prod`. The phases of ADR-0002 therefore reach `dev` (and `test`), but **not `prod`, until the cut-over
(phase 5)**, when the agent replaces the old hooks. The old board's last release is the tag `archive/board-final`.

## Agent releases: notes

- **Windows agent home moved** (`fix/agent-home-outside-appdata`): the default is now `%USERPROFILE%\.claude-monitor`,
  not `%LOCALAPPDATA%\ClaudeMonitor` (the Claude desktop app's MSIX packaging redirects AppData, ADR-0002). On first
  start the new agent copies `agent.json`; nothing else moves, and events not yet uploaded are lost. **Installs made
  before this release keep using the old binary and folder until `cm-agent install` is run again** (SETUP.md, "The
  agent"). `cm-agent status` now prints `home:`. A new agent version also needs a deploy to test, then prod
  (`docs/deploy-windows.md`).

- **Commands from the web, honest status** (`fix/idle-session-commands`): the web now says what a waiting command waits
  for, how long is left, and why an expired one lapsed, and warns above the prompt box when a session is idle (a prompt
  is applied only when something is typed or a turn ends). The agent gains `cm-agent install --stop-wait <seconds>`
  (0-590, default 0; re-run `install` to rewrite the hooks) and `cm-agent status` prints `commands waiting` and
  `stop wait`. No API change. Web and agent both need the usual deploy to test, then prod.

## Rollback

- **A change on `dev` or `test`:** a revert commit through the gate, like any other change. Nothing is ever
  force-pushed.
- **The old board:** `git switch --detach archive/board-final` in the clone the hooks run.
- **The hosted API and database:** `deploy/windows/deploy.ps1` rolls back by itself when the new release does not
  report its commit; a later rollback deploys the previous package again (`docs/deploy-windows.md`). Migrations are
  additive only (global #4), so an older API runs on a newer schema and no data is reversed.

## Flags

There is no feature flag yet. A switch ships as a configuration value read by the tier's one configuration
module (global #2).
