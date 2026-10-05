#!/usr/bin/env bash
# Merge gate for claude-monitor: the project's orchestrator. It calls the shared core (scripts/gate-core.sh) and adds
# one step of its own at the test -> prod gate: the OWASP ZAP baseline scan (global #19), after the core is green.
#
# Usage: scripts/merge-gate.sh <dev|test|prod>
set -uo pipefail
ROOT="$(git rev-parse --show-toplevel)"
target="${1:-dev}"
bash "$ROOT/scripts/gate-core.sh" "$target"
status=$?
# The published agent binary must stay up (docs/adr-0002-agent-platform.md, "Published build"); 3 = not run, which blocks.
if [ "$status" = 0 ]; then
  python3 "$ROOT/scripts/agent_smoke.py"
  smoke=$?
  if [ "$smoke" != 0 ]; then
    echo "GATE CLOSED — the published-agent smoke test did not pass (exit $smoke)."
    status="$smoke"
  fi
fi
if [ "$target" = "prod" ] && [ "$status" = 0 ]; then
  bash "$ROOT/scripts/zap-baseline.sh"
  zap=$?
  if [ "$zap" != 0 ]; then
    echo "GATE CLOSED — the OWASP ZAP baseline did not pass (exit $zap). No merge to prod."
    status="$zap"
  fi
fi
exit "$status"
