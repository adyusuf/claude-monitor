#!/usr/bin/env bash
# Shared gate CORE — one definition of the step set for every project.
#
# Usage:  scripts/gate-core.sh <dev|test|prod> [--list]
#
# Two layers, deliberately: this file owns the SHARED STEPS, while a project's own
# scripts/merge-gate.sh stays the orchestrator (it pulls, merges, pushes and adds
# whatever that project needs) and CALLS this file for the shared set. That way the
# step definition lives in one place and a project can ADD steps without forking it.
# `--list` prints the steps that WOULD run, with the command each one resolves to,
# and runs nothing — use it when rolling the gate into a project.
#
# ⚠️ The canonical copy lives in the configuration repository; every project takes
# a COPY into its own scripts/ and commits it. A project's gate cannot depend on a
# path outside the repository — CI runners do not have the configuration checked
# out. The drift tests cover this file and its libraries (scripts/twins.txt).
#
# What runs where:
#
#   dev, test : EVERYTHING EXCEPT RUNNING E2E — formatter/linter, typecheck,
#               build, unit tests, coverage (the 80% threshold per codebase),
#               secret scan, dependency CVE, SAST, backward-compatibility scan,
#               the CLAUDE.md size and rule gates, and a CHECK for missing e2e
#               specs (a WARNING — it never blocks). A test promotion also warns
#               that e2e was NOT run: e2e is OPTIONAL (#33, 03/10/2026).
#   prod      : the code must already be deployed to the TEST environment (the
#               deploy is verified); e2e is NOT run — the gate warns, and
#               GATE_RUN_E2E=1 runs the suite and then a red result blocks.
#
# ⚠️ A STEP THAT DID NOT RUN DID NOT PASS. A missing tool is reported as SKIPPED
# and the result is INCOMPLETE, never green. The exit code is the gate: 0 only
# when every applicable step passed.
#
# Per-project settings are optional and live in scripts/merge-gate.conf (sourced
# if present); everything else is auto-detected:
#
#   TEST_VERSION_URL="https://test.example.com/version https://admin.test.example.com/version"
#                                                       # one or more; EVERY site must report the
#                                                       # deployed SHA, and a project with several
#                                                       # sites on test counts as deployed only when
#                                                       # all of them do. HTML back = the SPA fallback
#                                                       # is swallowing it (standards/14 §8).
#   TEST_DEPLOY_SHA_CMD="ssh deploy@host cat /srv/app/REVISION"
#                                                       # alternative source when there is no
#                                                       # /version endpoint yet
#   TEST_BASE_URL=https://test.example.com              # documentation + the e2e base URL
#   E2E_WEB_CMD="npx playwright test"                   # default when e2e/ (root, tests/e2e or <web tier>/e2e) exists;
#                                                       # it runs from the directory that holds the suite
#   E2E_MOBILE_CMD="bash scripts/mobile-e2e.sh"         # default when .maestro/ exists
#   COVERAGE_CMD="node scripts/coverage-budget.cjs"     # must exit non-zero below the threshold
#   COVERAGE_MIN=80
#   SAST_CMD="bash scripts/codeql-scan.sh"              # default: scripts/codeql-scan.sh
#   BACKCOMPAT_CMD="bash scripts/api-compat.sh"         # default: scripts/backward-compat-scan.sh
#   SECRET_CMD="gitleaks detect --no-banner --redact"   # default: the same
#   GATE_CVE_TIMEOUT=300                                # seconds for `dotnet list package --vulnerable`;
#                                                       # no answer in time is a FAILURE, never a pass
#   LINT_CMD / TYPECHECK_CMD / BUILD_CMD / UNIT_CMD     # override the auto-detected ones
#   SKIP_STACKS="mobile"                                # codebases this project does not have
#   GATE_PARALLEL_NODE=1                                # run the web/mobile Node steps (lint, typecheck,
#                                                       # build, test, npm audit) BESIDE the .NET steps
#                                                       # instead of after them; default 0 (serial).
#                                                       # Only dev/test. The track's output is printed as
#                                                       # one block at the join, and a track that did not
#                                                       # report counts as FAILED, never as passed.
#                                                       # Put it in merge-gate.conf as
#                                                       #   GATE_PARALLEL_NODE="${GATE_PARALLEL_NODE:-1}"
#                                                       # so the environment wins: =0 forces serial.
#   ACCEPTED_GAPS="SAST|backward"                       # gaps the USER has accepted, with a reason
#   ACCEPTED_GAPS_REASON="no SAST tooling yet; tracked in docs/gates.md, review 01/11/2026"
#
# ⚠️ ACCEPTED_GAPS is the only way a missing step stops failing the gate, and it is
# not a silence: every accepted gap is printed as an ACCEPTED GAP with its reason
# and counted separately. A gap with no reason is not accepted — the gate still
# fails. This is the written, time-boxed risk acceptance the security standard asks
# for, not a switch that turns a step off.
#
# ⚠️ ONE STEP CANNOT BE ACCEPTED AT ALL: coverage. Rule #29 grants it no
# exceptions and says a project cannot override it, so listing it in
# ACCEPTED_GAPS does nothing but print that it cannot be accepted, and the gate
# stays INCOMPLETE. Install the measurement (scripts/coverage.sh) instead.
#
# The gate is split over four files so that none passes 300 lines (global rule #9): this one
# orchestrates, gate-lib.sh holds the helpers, stack detection and the verdict, gate-lib-node.sh the
# Node steps, gate-lib-prod.sh the test -> prod steps. They are TWINS: copy all four (scripts/twins.txt).
set -uo pipefail

TARGET="${1:-}"
LIST_ONLY=0
[ "${2:-}" = "--list" ] && LIST_ONLY=1
case "$TARGET" in
  dev|test|prod) ;;
  *) echo "usage: $0 <dev|test|prod> [--list]"; exit 2 ;;
esac

GATE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$ROOT" || exit 2
# shellcheck source=/dev/null  # the conf is per-project and may not exist; its
# absence is handled by the [ -f ] test and by each step's own default.
[ -f scripts/merge-gate.conf ] && . scripts/merge-gate.conf
COVERAGE_MIN="${COVERAGE_MIN:-80}"

# Fail closed: a copy of this file without its libraries must not run a partial gate.
for lib in gate-lib.sh gate-lib-node.sh gate-lib-prod.sh; do
  if [ ! -f "$GATE_DIR/$lib" ]; then
    echo "gate-core.sh: $lib is missing next to it - the gate is four twin files (scripts/twins.txt); copy them all" >&2
    exit 2
  fi
  # shellcheck source=/dev/null  # the path is resolved at run time, beside this file
  . "$GATE_DIR/$lib"
done

# ── Everything except e2e: dev and test ──────────────────────────────────────
if [ "$TARGET" != "prod" ]; then
  node_start

  say "formatter / linter"
  if [ "$HAS_DOTNET" = 1 ]; then
    if have dotnet; then run "dotnet format" dotnet format ${SLN:+"$SLN"} --verify-no-changes
    else skip "dotnet format (dotnet missing)"; fi
  fi
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_lint

  say "typecheck"
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_typecheck

  say "build"
  if [ "$HAS_DOTNET" = 1 ]; then
    if have dotnet; then run "dotnet build" dotnet build ${SLN:+"$SLN"} -warnaserror
    else skip "dotnet build (dotnet missing)"; fi
  fi
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_build

  say "unit tests"
  if [ "$HAS_DOTNET" = 1 ]; then
    if have dotnet; then run "dotnet test" dotnet test ${SLN:+"$SLN"} --nologo
    else skip "dotnet test (dotnet missing)"; fi
  fi
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_test

  node_join
  say "coverage (>= ${COVERAGE_MIN}% lines, per codebase)"
  if [ -n "${COVERAGE_CMD:-}" ]; then run "coverage" bash -c "$COVERAGE_CMD"
  elif [ -x scripts/coverage.sh ]; then run "coverage" bash scripts/coverage.sh
  else skip "coverage: set COVERAGE_CMD in scripts/merge-gate.conf — the threshold is NOT measured"; fi

  say "secret scan"
  if [ -n "${SECRET_CMD:-}" ]; then run "secret scan" bash -c "$SECRET_CMD"
  elif have gitleaks; then run "gitleaks detect" gitleaks detect --no-banner --redact
  else skip "gitleaks is not installed"; fi

  say "dependency CVE"
  if [ "$LIST_ONLY" = 1 ]; then
    [ "$HAS_DOTNET" = 1 ] && { printf '  → %-42s %s\n' "dotnet vulnerable packages" "dotnet list package --vulnerable${SLN:+ ($SLN)}"; PASS+=("dotnet cve"); }
    for d in "$WEB_DIR" "$MOBILE_DIR"; do [ -n "$d" ] && { if [ -f "$d/pnpm-lock.yaml" ] || { [ "$d" != "." ] && [ -f "pnpm-lock.yaml" ]; }; then printf '  → %-42s %s\n' "pnpm audit ($d)" "pnpm --dir $d audit --audit-level=high"; PASS+=("pnpm audit $d"); else printf '  → %-42s %s\n' "npm audit ($d)" "npm --prefix $d audit --audit-level=high"; PASS+=("npm audit $d"); fi; }; done
  else
  # ⚠️ FAIL-CLOSED. This step used to pipe the listing straight into a grep for
  # "critical|high" and call everything else a pass — so a listing that never
  # happened passed too. Seen live: the NuGet vulnerability feed hung on a network
  # that black-holes IPv6, the process was killed after 13 minutes and the gate
  # printed "✓ dotnet packages". A missing dotnet made the step vanish from the
  # report altogether. Now only a listing that ran, exited 0 and printed NuGet's
  # own verdict for the projects can pass.
  if [ "$HAS_DOTNET" = 1 ]; then
    if have dotnet; then
      cve_limit="${GATE_CVE_TIMEOUT:-300}"; cve_timer=""
      if have timeout; then cve_timer="timeout $cve_limit"; elif have gtimeout; then cve_timer="gtimeout $cve_limit"; fi
      cve_out="$($cve_timer dotnet list ${SLN:+"$SLN"} package --vulnerable 2>&1)"; cve_rc=$?
      if [ -n "$cve_timer" ] && [ "$cve_rc" = 124 ]; then
        bad "dotnet vulnerable packages: no answer in ${cve_limit}s (NuGet feed unreachable? on an IPv6 black hole run the listing with DOTNET_SYSTEM_NET_DISABLEIPV6=1)"
      elif [ "$cve_rc" != 0 ] || printf '%s\n' "$cve_out" | grep -qiE '^[[:space:]]*error'; then
        bad "dotnet vulnerable packages: the listing failed (exit $cve_rc)"; printf '%s\n' "$cve_out" | tail -10 | sed 's/^/      /'
      elif ! printf '%s\n' "$cve_out" | grep -qiE 'has no vulnerable packages|has the following vulnerable packages'; then
        bad "dotnet vulnerable packages: no verdict in the output — the listing did not run"; printf '%s\n' "$cve_out" | tail -10 | sed 's/^/      /'
      elif printf '%s\n' "$cve_out" | grep -qiwE 'critical|high'; then
        bad "dotnet vulnerable packages (critical/high)"; printf '%s\n' "$cve_out" | grep -iwE 'critical|high' | head -10 | sed 's/^/      /'
      else
        ok "dotnet packages"
      fi
    else
      skip "dotnet vulnerable packages: dotnet missing"
    fi
  fi
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_audit
  fi

  say "SAST"
  if [ -n "${SAST_CMD:-}" ]; then run "SAST" bash -c "$SAST_CMD"
  elif [ -x scripts/codeql-scan.sh ]; then run "SAST" bash scripts/codeql-scan.sh
  else skip "SAST: set SAST_CMD in scripts/merge-gate.conf"; fi

  say "backward compatibility"
  if [ -n "${BACKCOMPAT_CMD:-}" ]; then run "backward compatibility" bash -c "$BACKCOMPAT_CMD"
  elif [ -x scripts/backward-compat-scan.sh ]; then run "backward compatibility" bash scripts/backward-compat-scan.sh
  else skip "backward-compatibility scan: set BACKCOMPAT_CMD in scripts/merge-gate.conf"; fi

  say "CLAUDE.md gates"
  if [ -x scripts/md-size-gate.sh ]; then run "md-size-gate.sh" bash scripts/md-size-gate.sh
  else skip "md-size-gate.sh is missing"; fi
  if [ -f scripts/md-rule-gate.py ]; then ok "md-rule-gate.py present (run by hand when splitting)"
  else skip "md-rule-gate.py is missing"; fi

  say "project documents (rule #16 — fail closed)"
  if [ "$LIST_ONLY" = 1 ]; then
    printf '  → %-42s %s\n' "SETUP.md, .env.example, secret inventory" "file and heading checks"
    PASS+=("project documents")
  elif [ "$HAS_DOTNET" = 0 ] && [ "$HAS_NODE" = 0 ]; then
    na "project documents: no stack at the repository root, nothing to check"
  else
    if [ -f SETUP.md ]; then ok "SETUP.md"
    else bad "SETUP.md is missing (rule #16: a clean machine must be set up from the document)"; fi
    if [ -f .env.example ]; then ok ".env.example"
    else bad ".env.example is missing (rule #16)"; fi
    if [ -f SETUP.md ]; then
      if grep -qE '^#{1,4} .*[Ii]nventory' SETUP.md; then ok "secret/token inventory in SETUP.md"
      else bad "SETUP.md has no secret/token inventory heading (rule #16)"; fi
    fi
  fi

  say "e2e specs — CHECK ONLY, nothing is run here"
  missing=0
  if [ "$LIST_ONLY" = 1 ]; then
    printf '  → %-42s %s\n' "e2e spec check" "git diff --name-only <base>..HEAD (no e2e run)"
    PASS+=("e2e spec check")
  else
  if [ "$HAS_E2E_WEB" = 1 ] || [ "$HAS_E2E_MOBILE" = 1 ]; then
    base="$(git merge-base HEAD "origin/$TARGET" 2>/dev/null || git rev-parse HEAD~1 2>/dev/null)"
    changed="$(git diff --name-only "$base"..HEAD 2>/dev/null)"
    behaviour="$(printf '%s\n' "$changed" | grep -Ev '^(docs/|\.github/|scripts/|e2e/|[^/]+/e2e/|.*\.md$)' | grep -E '\.(cs|ts|tsx|js|jsx|kt|swift)$' || true)"
    specs_touched="$(printf '%s\n' "$changed" | grep -E '^(e2e/|tests/e2e/|[^/]+/e2e/|.*\.maestro/|.*\.spec\.ts)' || true)"
    if [ -n "$behaviour" ] && [ -z "$specs_touched" ]; then
      missing=1
      warn "behaviour changed in $(printf '%s\n' "$behaviour" | wc -l | tr -d ' ') file(s) but no e2e spec was touched"
      printf '%s\n' "$behaviour" | head -8 | sed 's/^/      /'
    else
      ok "e2e specs: nothing missing for this change"
    fi
  else
    skip "e2e spec check: this project has no e2e suite"
  fi
  # A missing spec is a WARNING in both directions and blocks nothing. It used to
  # fail the dev -> test promotion, which put the whole e2e backlog in front of an
  # integration merge and stopped work that had nothing to do with e2e. #33 moved
  # the writing to the pre-prod gate, where a spec can actually be verified
  # against a deployed test environment — writing it earlier means writing it
  # blind. The gap is NOT dropped: step 2 of the test -> prod gate writes every
  # missing spec before the suite runs, and that gate does block. The gate still
  # SAYS it on every run, so the backlog stays visible rather than silent.
  # User decision.
  if [ "$missing" = 1 ] && [ "$TARGET" = "test" ]; then
    warn "the missing spec(s) above are a reminder only: e2e is optional (#33); dev -> test is not blocked"
  fi
  fi
  if [ "$TARGET" = "test" ]; then
    warn "e2e was NOT run (optional since 03/10/2026) — nothing in this promotion was proven end to end"
  fi
fi

# ── prod: deployed to test, then the full e2e suite ──────────────────────────
if [ "$TARGET" = "prod" ]; then gate_prod; fi

# ── Result ───────────────────────────────────────────────────────────────────
gate_result
