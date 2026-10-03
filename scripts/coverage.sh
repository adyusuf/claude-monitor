#!/usr/bin/env bash
# Line coverage per codebase (global rule #29), the gate's coverage step (gate-core.sh runs this file).
#
# Exit codes:  0  every codebase that exists is measured and at or above the threshold
#              1  the tests are red, or a measured codebase is below the threshold
#              3  a codebase exists but is NOT measured — which blocks, exactly like a failure
#                 (#29: an unmeasured codebase does not count as passing)
#
# The codebases (ADR-0002): the .NET API and agent, and the web app. Each is measured here from the phase
# that adds it; until its measurement is written, its mere presence is NOT MEASURED, so new code can never
# slip past this step as "n/a".
#
# Out of scope, stated: the gate tools under scripts/ are the shared copies of the configuration
# repository, whose own tests measure them there. Their tests here (scripts/tests) still run, and red blocks.
set -uo pipefail

root="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repository" >&2; exit 2; }
cd "$root" || exit 2
status=0
worse() { if [ "$1" != 0 ] && { [ "$status" = 0 ] || [ "$1" = 3 ]; }; then status="$1"; fi; }

echo "▶ gate tool tests (scripts/tests)"
log="$(mktemp)"
if python3 -m unittest discover -s scripts/tests -p 'test_*.py' >"$log" 2>&1; then
  echo "  ✓ $(grep -E '^Ran ' "$log")"
else
  tail -20 "$log" | sed 's/^/  /'
  echo "  ✗ the tests are red"
  worse 1
fi
rm -f "$log"
find scripts -name __pycache__ -prune -exec rm -rf {} + 2>/dev/null

echo "▶ .NET (API, agent)"
if [ -z "$(git ls-files '*.csproj')" ]; then
  echo "  n/a: no .NET project yet"
else
  echo "  NOT MEASURED: .NET projects exist but their coverage measurement is not written yet"
  worse 3
fi

echo "▶ web"
if [ -z "$(git ls-files 'web/package.json')" ]; then
  echo "  n/a: no web app yet"
else
  echo "  NOT MEASURED: web/ exists but its coverage measurement is not written yet"
  worse 3
fi

exit "$status"
