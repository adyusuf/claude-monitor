#!/usr/bin/env bash
# What did a workflow say about a commit? CI owns the full gate here (CLAUDE.md), so before a promotion the
# answer comes from GitHub, not from a local re-run.
#
# Usage:  scripts/ci-status.sh <branch> [commit] [--workflow ci|e2e]
#   branch    the branch the workflow ran for (dev, test, prod, a feature branch)
#   commit    the commit to ask about (default: the tip of origin/<branch>, else of <branch>)
#   --workflow  ci (default, .github/workflows/ci.yml) or e2e (.github/workflows/e2e.yml)
#
# Exit codes — only a run that is COMPLETED and SUCCESSFUL is 0; every other answer is non-zero and says why:
#   0  completed, success
#   1  completed, not success (failure, cancelled, timed out, ...)
#   2  bad usage, or the commit could not be resolved
#   3  not finished: queued, waiting or in progress — wait for it, do not merge on it
#   4  no run for that commit: the workflow never ran, so it did not pass
#   5  gh is missing, or it could not answer (not signed in, offline, ...)
set -uo pipefail

branch=""; commit=""; workflow="ci"
while [ $# -gt 0 ]; do
  case "$1" in
    --workflow) shift; workflow="${1:-}" ;;
    -*) echo "usage: $0 <branch> [commit] [--workflow ci|e2e]" >&2; exit 2 ;;
    *) if [ -z "$branch" ]; then branch="$1"; elif [ -z "$commit" ]; then commit="$1"; else echo "usage: $0 <branch> [commit] [--workflow ci|e2e]" >&2; exit 2; fi ;;
  esac
  shift
done
case "$workflow" in ci|e2e) ;; *) echo "usage: $0 <branch> [commit] [--workflow ci|e2e]  (workflow: ci or e2e)" >&2; exit 2 ;; esac
[ -n "$branch" ] || { echo "usage: $0 <branch> [commit] [--workflow ci|e2e]" >&2; exit 2; }

if [ -z "$commit" ]; then
  commit="$(git rev-parse --verify --quiet "origin/$branch^{commit}" || git rev-parse --verify --quiet "$branch^{commit}")" \
    || { echo "cannot resolve the tip of $branch (give the commit)" >&2; exit 2; }
else
  commit="$(git rev-parse --verify --quiet "$commit^{commit}" || printf '%s' "$commit")"
fi

if ! command -v gh >/dev/null 2>&1; then
  echo "NOT CHECKED: gh is not installed, so $workflow for ${commit:0:7} on $branch is unknown (not a pass)"; exit 5
fi
runs="$(gh run list --workflow "$workflow.yml" --branch "$branch" --commit "$commit" --limit 20 \
        --json databaseId,status,conclusion,headSha,url,createdAt 2>&1)" \
  || { echo "NOT CHECKED: gh could not list the runs (not signed in? offline?): $(printf '%s' "$runs" | head -3)"; exit 5; }

# The newest run for exactly this commit decides (a re-run supersedes the first attempt).
verdict="$(printf '%s' "$runs" | COMMIT="$commit" python3 -c '
import json, os, sys
commit = os.environ["COMMIT"]
try:
    runs = json.load(sys.stdin)
except ValueError:
    print("unreadable"); sys.exit(0)
runs = [r for r in runs if r.get("headSha", "").startswith(commit) or commit.startswith(r.get("headSha", "?"))]
runs.sort(key=lambda r: r.get("createdAt", ""), reverse=True)
if not runs:
    print("none")
else:
    r = runs[0]
    print("\t".join([r.get("status") or "", r.get("conclusion") or "", r.get("url") or ""]))
')" || { echo "NOT CHECKED: python3 could not read gh's answer"; exit 5; }

IFS=$'\t' read -r status conclusion url <<<"$verdict"
what="$workflow on ${commit:0:7} ($branch)"
case "$verdict" in
  unreadable) echo "NOT CHECKED: gh's answer was not JSON"; exit 5 ;;
  none) echo "NO RUN: $what — the workflow never ran for this commit, so it did not pass"; exit 4 ;;
esac
if [ "$status" = "completed" ]; then
  if [ "$conclusion" = "success" ]; then echo "PASSED: $what — $url"; exit 0; fi
  echo "RED: $what finished as '$conclusion' — $url"; exit 1
fi
echo "NOT FINISHED: $what is '$status' — $url"; exit 3
