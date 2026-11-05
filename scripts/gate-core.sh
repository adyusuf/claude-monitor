#!/usr/bin/env bash
# Shared gate CORE — one definition of the step set for every project.
#
# Usage:  scripts/gate-core.sh <dev|test|prod> [--list | --coverage-only]
#
# Two layers, deliberately: this file owns the SHARED STEPS, while a project's own
# scripts/merge-gate.sh stays the orchestrator (it pulls, merges, pushes and adds
# whatever that project needs) and CALLS this file for the shared set. That way the
# step definition lives in one place and a project can ADD steps without forking it.
# `--list` prints the steps that WOULD run, with the command each one resolves to,
# and runs nothing — use it when rolling the gate into a project. It ends "LISTING
# ONLY", never "GATE GREEN": a listing is not a verdict (a step it can already see
# would be skipped or fail still ends INCOMPLETE / CLOSED, as the real run would).
# `--coverage-only` runs the coverage step and nothing else: it is what runs when the
# configuration turns the gate off (gates_before_push.<t> = no) and coverage on
# (standards/13 §8a). COVERAGE_CMD must then measure on its own, not read a result an
# earlier step of the full gate left behind.
# Whether the coverage step runs at all is the panel's "Run coverage before pushing"
# (coverage_before_push.<t>, user decision 30/10/2026), read from the live panel when the podium plugin is enabled
# here (not in CI), else from .claude/sdlc-settings.json (gate-lib-panel.sh, user decision 06/11/2026):
# `false` there reports the step WAIVED (never passed); anything else runs it (fail closed).
# The other steps work the same way (user decision 02/11/2026): each is one row of GATE_STEPS in gate-lib.sh, and
# gate_steps.<id>.<t> = false in that file waives it - it does not run, it is reported WAIVED and the merge commit's
# Gate-Waived line names it. A missing key runs it.
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
#               specs (a WARNING — it never blocks). e2e is OPTIONAL (#33): a test
#               promotion runs it on dev only when the panel's e2e_before_deploy.test
#               is yes (E2E_DEV_WEB_CMD / E2E_DEV_MOBILE_CMD), else it warns.
#               gates_before_push.<t> = no (not in CI) runs coverage only: exit 10.
#   prod      : the test branch and the TEST environment are NOT checked (04/10/2026,
#               CLAUDE.md #33) and e2e is NOT run — the gate warns for both.
#               GATE_CHECK_TEST_DEPLOY=1 verifies the deployed SHA (TEST_VERSION_URL /
#               TEST_DEPLOY_SHA_CMD below); GATE_RUN_E2E=1 or the panel's
#               e2e_before_deploy.prod = yes runs the suite. A red
#               result of either then blocks.
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
#   COVERAGE_CMD="bash scripts/coverage.sh"             # must MEASURE (run the tests), not read an old
#                                                       # report, and exit non-zero below the threshold
#   COVERAGE_MIN=80                                     # NOT in a project's conf (standards/13 §8a): 80 is
#                                                       # the default here; the env may only RAISE it (#29)
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
# The gate is split over five files so that none passes 300 lines (global rule #9): this one
# orchestrates, gate-lib.sh holds the helpers, stack detection and the verdict, gate-lib-panel.sh where the panel's
# values come from, gate-lib-node.sh the Node steps, gate-lib-prod.sh the e2e and test -> prod steps. They are TWINS:
# copy all five (scripts/twins.txt).
set -uo pipefail

TARGET="${1:-}"
LIST_ONLY=0
COVERAGE_ONLY=0
case "${2:-}" in
  "") ;;
  --list) LIST_ONLY=1 ;;
  --coverage-only) COVERAGE_ONLY=1 ;;
  *) echo "usage: $0 <dev|test|prod> [--list | --coverage-only]"; exit 2 ;;
esac
case "$TARGET" in
  dev|test|prod) ;;
  *) echo "usage: $0 <dev|test|prod> [--list | --coverage-only]"; exit 2 ;;
esac

GATE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$ROOT" || exit 2
# shellcheck source=/dev/null  # the conf is per-project and may not exist; its
# absence is handled by the [ -f ] test and by each step's own default.
[ -f scripts/merge-gate.conf ] && . scripts/merge-gate.conf
COVERAGE_MIN="${COVERAGE_MIN:-80}"

# Fail closed: a copy of this file without its libraries must not run a partial gate.
for lib in gate-lib.sh gate-lib-panel.sh gate-lib-node.sh gate-lib-prod.sh; do
  if [ ! -f "$GATE_DIR/$lib" ]; then
    echo "gate-core.sh: $lib is missing next to it - the gate is five twin files (scripts/twins.txt); copy them all" >&2
    exit 2
  fi
  # shellcheck source=/dev/null  # the path is resolved at run time, beside this file
  . "$GATE_DIR/$lib"
done
# ...nor with a STALE one: a library from an older version may lack a function this file calls, and
# bash would say "command not found" and carry on. With gate_coverage missing that dropped coverage
# without a trace and ended GATE GREEN (found 29/10/2026). Every function the core calls must exist.
for fn in say ok bad skip warn na have run coverage_setting gate_coverage gate_result gate_prod node_start node_join \
  gate_steps_off step_on gate_step gate_e2e_specs node_lint node_typecheck node_build node_test node_audit \
  panel_resolve panel_value gate_e2e_suite; do
  declare -F "$fn" >/dev/null || {
    echo "gate-core.sh: $fn is not defined - a library beside it is older than this file; copy all five twins (scripts/twins.txt)" >&2
    exit 2
  }
done

# Every panel value comes from the live panel when the podium plugin is enabled here, else from the project's copy.
panel_resolve
# The gate is off by configuration (gates_before_push.<t> = no, or the caller's --coverage-only); coverage may not be
# (coverage_before_push.<t>): run that one step and stop.
# It is a setting about pushing: CI ignores it and runs the gate (qa 06/11/2026). A self-waived run exits 10, never 0,
# so a caller cannot read it as a pass (it may push: the configuration allows it; nothing passed).
if [ "$COVERAGE_ONLY" != 1 ] && [ "$(panel_value "gates_before_push.$TARGET")" = "no" ]; then
  if gate_in_ci; then
    say "gates_before_push.$TARGET = no is a local setting — CI runs the gate"
  else
  say "gate off: gates_before_push.$TARGET = no in $SDLC_SETTINGS_LABEL"
  printf '  \033[33m-\033[0m WAIVED: the gate (only coverage runs; exit 10; the merge commit must carry Gate-Waived)\n'
  COVERAGE_ONLY=1
  # shellcheck disable=SC2034  # read by gate_result in gate-lib.sh
  GATE_OFF_BY_PANEL=1
  fi
fi
if [ "$COVERAGE_ONLY" = 1 ]; then gate_coverage; gate_result; fi

# ── Everything except e2e: the SAME step set for dev, test and prod (29/10/2026) ──
# prod used to run only its own extras, on the assumption that the code had passed the gate on test; with the
# gate off by configuration that proves nothing, and the projects had already worked around it by calling the
# prod gate with the test target.
# A step the user unticked (gate_steps.<id>.<t> = no, gate-lib.sh) prints WAIVED and does not run (02/11/2026).
if true; then
  gate_steps_off      # read once here, before the Node track forks
  node_start

  if gate_step lint; then
  if [ "$HAS_DOTNET" = 1 ]; then
    if have dotnet; then run "dotnet format" dotnet format ${SLN:+"$SLN"} --verify-no-changes
    else skip "dotnet format (dotnet missing)"; fi
  fi
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_lint
  fi

  if gate_step typecheck; then [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_typecheck; fi

  if gate_step build; then
  if [ "$HAS_DOTNET" = 1 ]; then
    if have dotnet; then run "dotnet build" dotnet build ${SLN:+"$SLN"} -warnaserror
    else skip "dotnet build (dotnet missing)"; fi
  fi
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_build
  fi

  if gate_step unit; then
  if [ "$HAS_DOTNET" = 1 ]; then
    if have dotnet; then run "dotnet test" dotnet test ${SLN:+"$SLN"} --nologo
    else skip "dotnet test (dotnet missing)"; fi
  fi
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || node_test
  fi

  node_join
  gate_coverage

  if gate_step secrets; then
  if [ -n "${SECRET_CMD:-}" ]; then run "secret scan" bash -c "$SECRET_CMD"
  elif have gitleaks; then run "gitleaks detect" gitleaks detect --no-banner --redact
  else skip "gitleaks is not installed"; fi
  fi

  if ! gate_step cve; then :
  elif [ "$LIST_ONLY" = 1 ]; then
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

  if gate_step sast; then
  if [ -n "${SAST_CMD:-}" ]; then run "SAST" bash -c "$SAST_CMD"
  elif [ -x scripts/codeql-scan.sh ]; then run "SAST" bash scripts/codeql-scan.sh
  else skip "SAST: set SAST_CMD in scripts/merge-gate.conf"; fi
  fi

  if gate_step compat; then
  if [ -n "${BACKCOMPAT_CMD:-}" ]; then run "backward compatibility" bash -c "$BACKCOMPAT_CMD"
  elif [ -x scripts/backward-compat-scan.sh ]; then run "backward compatibility" bash scripts/backward-compat-scan.sh
  else skip "backward-compatibility scan: set BACKCOMPAT_CMD in scripts/merge-gate.conf"; fi
  fi

  if gate_step md; then
  if [ -x scripts/md-size-gate.sh ]; then run "md-size-gate.sh" bash scripts/md-size-gate.sh
  else skip "md-size-gate.sh is missing"; fi
  if [ -f scripts/md-rule-gate.py ]; then ok "md-rule-gate.py present (run by hand when splitting)"
  else skip "md-rule-gate.py is missing"; fi
  fi

  if ! gate_step documents; then :
  elif [ "$LIST_ONLY" = 1 ]; then
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

  if gate_step e2e_specs; then gate_e2e_specs; fi
  if [ "$TARGET" = "test" ]; then gate_e2e_suite; fi
fi

# ── prod: on top of the shared set, the test deploy check and the full e2e suite, both on request ──
if [ "$TARGET" = "prod" ]; then gate_prod; fi

# ── Result ───────────────────────────────────────────────────────────────────
gate_result
