#!/usr/bin/env bash
# SAST for this repository's Python (global rule #19).
#
# Rule #19 says SAST runs in every project and runs LOCALLY with the same
# thresholds CI uses — never two different rules in two places. gate-core.sh
# calls this file through SAST_CMD in scripts/merge-gate.conf.
#
# Scope: Python and JavaScript via CodeQL, shell via ShellCheck. The JavaScript is
# the live board's page script (scripts/board/, standards/22-live-board.md).
# CodeQL has no shell analyser, and this repository is mostly shell — "SAST passed" would have read as
# "everything was scanned" while more than half the code was never looked at.
# ShellCheck closes that half. Both tools are PROBED; a missing one is reported
# as NOT RUN and blocks, because a gate that did not run did not pass (#19).
#
# Threshold: a result whose rule has security-severity >= 7.0 (CodeQL's high and
# critical band) FAILS the gate. Lower-severity results are printed and do not
# block. #19: "critical/high findings block the merge."
#
# Exit codes: 0 clean · 1 a high/critical finding · 2 usage/environment
#             3 NOT RUN — which blocks exactly like a failure, because a gate
#               that did not run did not pass (#19)
#
# ⚠️ The database is rebuilt on every run and cached under .codeql/ (gitignored).
# A stale database is worse than none: it reports yesterday's code as today's.
set -uo pipefail

root="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repository" >&2; exit 2; }
cd "$root" || exit 2

command -v codeql >/dev/null 2>&1 || {
  echo "▶ SAST (CodeQL)"
  echo "  NOT RUN: codeql is not installed (brew install --cask codeql)"
  echo "  A gate that did not run did not pass (#19)."
  exit 3; }

log="$(mktemp)"
trap 'rm -f "$log"' EXIT

# One CodeQL pass: $1 language, $2 label, $3 file pattern, $4 query suite, $5 database,
# $6 SARIF output. Returns 0 clean or n/a · 1 high/critical · 3 NOT RUN.
codeql_pass() {
  local lang="$1" label="$2" pattern="$3" suite="$4" db="$5" out="$6"
  echo "▶ SAST (CodeQL, $label)"
  if [ -z "$(find . -name "$pattern" -not -path './.codeql/*' -not -path '*/node_modules/*' -print -quit 2>/dev/null)" ]; then
    echo "  n/a: this repository has no $label"
    return 0
  fi
  mkdir -p "$(dirname "$db")"
  rm -rf "$db"
  if ! codeql database create "$db" --language="$lang" --source-root="$root" \
        --overwrite >"$log" 2>&1; then
    tail -15 "$log" | sed 's/^/    /'
    echo "  NOT RUN: the CodeQL database could not be built"
    return 3
  fi
  if ! codeql database analyze "$db" "$suite" \
          --format=sarif-latest --output="$out" --download >"$log" 2>&1; then
    tail -15 "$log" | sed 's/^/    /'
    echo "  NOT RUN: the CodeQL analysis did not complete (query pack unavailable offline?)"
    return 3
  fi
  python3 - "$out" <<'REPORT'
import json, sys
from collections import Counter

with open(sys.argv[1], encoding='utf-8') as handle:
    sarif = json.load(handle)

# security-severity lives on the RULE, not the result, so the rules are indexed
# first. A rule without one is not a security rule — it is a quality query, and
# quality is not what this gate blocks on.
severity, blocking, other = {}, [], []
for run in sarif.get('runs', []):
    for rule in (run.get('tool', {}).get('driver', {}).get('rules') or []):
        props = rule.get('properties') or {}
        raw = props.get('security-severity')
        if raw is not None:
            try:
                severity[rule['id']] = float(raw)
            except (TypeError, ValueError):
                pass
    for result in run.get('results', []):
        rule_id = result.get('ruleId', '?')
        score = severity.get(rule_id)
        location = ''
        for loc in result.get('locations', [])[:1]:
            phys = loc.get('physicalLocation', {})
            location = '%s:%s' % (
                phys.get('artifactLocation', {}).get('uri', '?'),
                (phys.get('region') or {}).get('startLine', '?'))
        line = '%s  %s  %s' % (rule_id, location,
                               (result.get('message') or {}).get('text', '')[:100])
        (blocking if (score or 0) >= 7.0 else other).append((score, line))

print('  queries with a security severity: %d' % len(severity))
if other:
    print('  %d finding(s) below the high band (reported, not blocking):' % len(other))
    for score, line in sorted(other, key=lambda x: -(x[0] or 0))[:10]:
        print('    [%s] %s' % ('-' if score is None else score, line))
if blocking:
    print('  ✗ %d HIGH/CRITICAL finding(s) — the merge is blocked (#19):' % len(blocking))
    for score, line in sorted(blocking, key=lambda x: -(x[0] or 0)):
        print('    [%.1f] %s' % (score, line))
    raise SystemExit(1)
print('  ✓ no high or critical finding')
REPORT
}

codeql_pass python Python '*.py' \
  "${CODEQL_SUITE:-codeql/python-queries:codeql-suites/python-security-extended.qls}" \
  "${CODEQL_DB:-$root/.codeql/db}" "${CODEQL_SARIF:-$root/.codeql/results.sarif}"
status=$?
if [ "$status" = 3 ]; then exit 3; fi

echo
codeql_pass javascript JavaScript '*.js' \
  "codeql/javascript-queries:codeql-suites/javascript-security-extended.qls" \
  "$root/.codeql/db-javascript" "$root/.codeql/results-javascript.sarif"
js_status=$?
if [ "$js_status" = 3 ]; then exit 3; fi
if [ "$js_status" != 0 ] && [ "$status" = 0 ]; then status="$js_status"; fi

echo
echo "▶ SAST (ShellCheck, shell)"
if ! command -v shellcheck >/dev/null 2>&1; then
  echo "  NOT RUN: shellcheck is not installed (brew install shellcheck)"
  echo "  A gate that did not run did not pass (#19)."
  exit 3
fi

# The threshold: error and warning BLOCK, info and style are reported. Blocking
# on style is how a gate gets switched off; letting a warning through is how a
# quoting bug reaches production.
shell_files="$(find "$root/scripts" -maxdepth 1 -name '*.sh' -print 2>/dev/null | sort)"
if [ -z "$shell_files" ]; then
  echo "  n/a: no shell scripts here"
else
  # shellcheck disable=SC2086
  if shellcheck --format=json1 --severity=style $shell_files > "$log" 2>/dev/null || [ -s "$log" ]; then
    python3 - "$log" <<'SHELLREPORT'
import json, sys
from collections import Counter

with open(sys.argv[1], encoding='utf-8') as handle:
    data = json.load(handle)
comments = data.get('comments', [])
blocking = [c for c in comments if c.get('level') in ('error', 'warning')]
reported = [c for c in comments if c.get('level') not in ('error', 'warning')]

levels = Counter(c.get('level') for c in comments)
print('  %d finding(s): %s' % (len(comments),
      ', '.join(f'{n} {lvl}' for lvl, n in sorted(levels.items())) or 'none'))
if reported:
    # Printed, not blocking - but printed, because SC2015 in particular
    # (`a && b || c` is not if-then-else) is the shape that turns a FAILED step
    # into a SKIPPED one the moment a helper stops returning 0.
    for c in sorted(reported, key=lambda c: (c['file'], c['line']))[:12]:
        print('    [%s SC%s] %s:%s %s' % (c['level'], c['code'],
              c['file'].split('/')[-1], c['line'], c['message'][:80]))
if blocking:
    print('  \u2717 %d error/warning finding(s) - the merge is blocked (#19):' % len(blocking))
    for c in blocking:
        print('    [%s SC%s] %s:%s %s' % (c['level'], c['code'],
              c['file'].split('/')[-1], c['line'], c['message'][:100]))
    raise SystemExit(1)
print('  \u2713 no error or warning')
SHELLREPORT
    shell_status=$?
  else
    echo "  NOT RUN: shellcheck produced no report"
    shell_status=3
  fi
  [ "$shell_status" != 0 ] && [ "$status" = 0 ] && status="$shell_status"
fi

exit "$status"
