#!/usr/bin/env bash
# Merge gate for claude-monitor: the project's orchestrator. It calls the shared core
# (scripts/gate-core.sh) and adds nothing of its own yet.
#
# Usage: scripts/merge-gate.sh <dev|test|prod>
set -uo pipefail
ROOT="$(git rev-parse --show-toplevel)"
exec bash "$ROOT/scripts/gate-core.sh" "${1:-dev}"
