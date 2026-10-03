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

## Rollback

- **A change on `dev` or `test`:** a revert commit through the gate, like any other change. Nothing is ever
  force-pushed.
- **The old board:** `git switch --detach archive/board-final` in the clone the hooks run.
- **The hosted API and database** (from phase 4): the deploy and rollback procedure, and what a rollback does to
  the data, are written here with the first deploy. Migrations are additive only (global #4), so an older API
  runs on a newer schema.

## Flags

There is no feature flag yet. A switch ships as a configuration value read by the tier's one configuration
module (global #2).
