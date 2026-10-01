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
# out. The drift test in md-hook.sh covers this file too.
#
# What runs where:
#
#   dev, test : EVERYTHING EXCEPT RUNNING E2E — formatter/linter, typecheck,
#               build, unit tests, coverage (the 80% threshold per codebase),
#               secret scan, dependency CVE, SAST, backward-compatibility scan,
#               the CLAUDE.md size and rule gates, and a CHECK for missing e2e
#               specs (a WARNING in both directions — it never blocks; the gaps
#               are written at the test -> prod gate, #33 step 2).
#   prod      : the code must already be deployed to the TEST environment, the
#               FULL e2e suite runs against it, and only a completely green run
#               allows the promotion.
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
#   E2E_WEB_CMD="npx playwright test"                   # default when e2e/ exists
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
set -uo pipefail

TARGET="${1:-}"
LIST_ONLY=0
[ "${2:-}" = "--list" ] && LIST_ONLY=1
case "$TARGET" in
  dev|test|prod) ;;
  *) echo "usage: $0 <dev|test|prod> [--list]"; exit 2 ;;
esac

ROOT="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$ROOT" || exit 2
# shellcheck source=/dev/null  # the conf is per-project and may not exist; its
# absence is handled by the [ -f ] test and by each step's own default.
[ -f scripts/merge-gate.conf ] && . scripts/merge-gate.conf
COVERAGE_MIN="${COVERAGE_MIN:-80}"

PASS=(); FAIL=(); SKIP=(); WARN=()
say()  { printf '\n\033[1m▶ %s\033[0m\n' "$1"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; PASS+=("$1"); }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; FAIL+=("$1"); }
NA=()
na()   { printf '  \033[90m–\033[0m n/a: %s\n' "$1"; NA+=("$1"); }   # nothing to check here — not a gap
ACCEPTED=()
# Rule #29 grants the coverage threshold NO exceptions and says a project cannot
# override it — so ACCEPTED_GAPS cannot waive it either. That was where the rule
# was quietly losing: several projects listed `coverage` as an accepted gap and
# their gates printed GREEN while nothing measured coverage at all. Measured on
# It was measured across the projects carrying this gate.
NEVER_ACCEPTABLE='coverage'
skip() {
  local what="$1"
  if [ -n "${ACCEPTED_GAPS:-}" ] && [ -n "${ACCEPTED_GAPS_REASON:-}" ] && printf '%s' "$what" | grep -qiE "${ACCEPTED_GAPS}"; then
    if printf '%s' "$what" | grep -qiE "$NEVER_ACCEPTABLE"; then
      printf '  \033[33m·\033[0m SKIPPED (this gap CANNOT be accepted — rule #29): %s\n' "$what"; SKIP+=("$what"); return 0
    fi
    printf '  \033[33m~\033[0m ACCEPTED GAP: %s\n' "$what"; ACCEPTED+=("$what"); return 0
  fi
  printf '  \033[33m·\033[0m SKIPPED: %s\n' "$what"; SKIP+=("$what")
}
warn() { printf '  \033[33m!\033[0m %s\n' "$1"; WARN+=("$1"); }
have() { command -v "$1" >/dev/null 2>&1; }
run()  { # run <label> <command...>
  local label="$1"; shift
  if [ "$LIST_ONLY" = 1 ]; then printf '  → %-42s %s\n' "$label" "$*"; PASS+=("$label"); return 0; fi
  local out; out="$(mktemp)"   # not $$: the node track runs in a subshell that shares the parent's $$
  if "$@" >"$out" 2>&1; then ok "$label"; else bad "$label"; tail -20 "$out" | sed 's/^/      /'; fi
  rm -f "$out"
}

# ── Stack detection ──────────────────────────────────────────────────────────
#
# ⚠️ This block decides whether a whole tier is checked AT ALL, so a miss here is
# silent and total. Both halves used to miss:
#   · `ls ./*.sln` did not know about `.slnx`, the newer solution format;
#   · `ls ./**/*.csproj` is ONE level deep in a plain shell (globstar is off), so
#     a project at src/Api/X.csproj was invisible.
# Measured: a repository with a .slnx, 673 backend tests, a build
# that was RED and two high-severity advisories reported GATE GREEN, because
# HAS_DOTNET came out 0 and not one .NET step ran.
# The build target. Detection finding a project is not enough: `dotnet build`
# with no argument builds the CURRENT directory, so in a repository whose
# solution lives under api/ or backend/ it failed with
#   MSBUILD : error MSB1003: Specify a project or solution file.
# — a confusing error in place of a build. Measured: four of five
# .NET repositories here keep their solution below the root.
# shellcheck disable=SC2012  # `ls` on a glob is intentional: it answers "is
# there a solution AT THE ROOT" cheaply, and the `find` below covers every case
# ls cannot — including the filenames SC2012 is about.
SLN="$(ls ./*.sln ./*.slnx 2>/dev/null | head -1)"
if [ -z "$SLN" ]; then
  SLN="$(find . -maxdepth 4 \( -name '*.sln' -o -name '*.slnx' \) \
          -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/node_modules/*' \
          -print 2>/dev/null | sort | head -1)"
fi
if [ -z "$SLN" ]; then
  # No solution anywhere: build the project itself rather than the directory.
  SLN="$(find . -maxdepth 4 -name '*.csproj' \
          -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/node_modules/*' \
          -print 2>/dev/null | sort | head -1)"
fi
HAS_DOTNET=0
if [ -n "$SLN" ] || [ -n "$(find . -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -not -path '*/node_modules/*' -print -quit 2>/dev/null)" ]; then
  HAS_DOTNET=1
fi
# Node detection is only used for the rule-#16 document step (below), and it was
# root-only while .NET detection now looks at any depth. A repository whose only
# JS lives in frontend/ therefore skipped that step entirely — measured
# One repo had no SETUP.md and its gate said "no stack at the
# repository root, nothing to check". WEB_DIR/MOBILE_DIR keep their own job of
# deciding WHICH tier gets linted, built and tested.
HAS_NODE=0
if [ -f package.json ] || [ -n "$(find . -maxdepth 3 -name package.json -not -path '*/node_modules/*' -print -quit 2>/dev/null)" ]; then
  HAS_NODE=1
fi
WEB_DIR=""; for d in web frontend .; do [ -f "$d/package.json" ] && { WEB_DIR="$d"; break; }; done
MOBILE_DIR=""; for d in mobile app; do [ -f "$d/package.json" ] && { MOBILE_DIR="$d"; break; }; done
case " ${SKIP_STACKS:-} " in *" mobile "*) MOBILE_DIR="" ;; esac
HAS_E2E_WEB=0;    [ -d e2e ] || [ -d tests/e2e ] && HAS_E2E_WEB=1
HAS_E2E_MOBILE=0; [ -d .maestro ] || [ -d "${MOBILE_DIR:-mobile}/.maestro" ] && HAS_E2E_MOBILE=1

echo "merge gate → $TARGET   ($(git rev-parse --short HEAD), $ROOT)"
echo "stacks: dotnet=$HAS_DOTNET${SLN:+ (${SLN#./})} node=$HAS_NODE web=${WEB_DIR:-none} mobile=${MOBILE_DIR:-none} e2e=web:$HAS_E2E_WEB/mobile:$HAS_E2E_MOBILE"

# ── Node steps as functions ──────────────────────────────────────────────────
# Serial mode calls each one where its section is; GATE_PARALLEL_NODE=1 runs them
# all as one track beside the .NET steps (node_start / node_join below).
node_lint() {
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] || continue
    if [ -f "$d/node_modules/.bin/eslint" ] || grep -q '"lint"' "$d/package.json" 2>/dev/null; then
      run "lint ($d)" npm --prefix "$d" run lint
    else skip "lint ($d): no lint script"; fi
  done
}
node_typecheck() {
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] || continue
    # A solution-style root tsconfig ("files": [] + "references", the Vite
    # template) holds no files itself: `tsc -p` on it checks NOTHING and always
    # passes. Only build mode follows the references into the real projects.
    if [ ! -f "$d/tsconfig.json" ]; then skip "tsc ($d): no tsconfig"
    elif grep -q '"references"' "$d/tsconfig.json"; then
      run "tsc -b ($d)" npx --prefix "$d" tsc -b "$d" --noEmit
    else run "tsc ($d)" npx --prefix "$d" tsc -p "$d" --noEmit; fi
  done
}
node_build() {
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] || continue
    if grep -q '"build"' "$d/package.json" 2>/dev/null; then run "build ($d)" npm --prefix "$d" run build
    else skip "build ($d): no build script"; fi
  done
}
node_test() {
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] || continue
    if grep -q '"test"' "$d/package.json" 2>/dev/null; then
      # `--run` belongs to VITEST. Handing it to a jest project fails with
      # "Unrecognized option run", and the gate then reports a green test suite
      # as FAILING — Measured: one project's 10 mobile tests pass on
      # their own and this step called them red, purely because of this argument.
      # CI=true is what both runners understand: vitest does a single run instead
      # of watching, and jest is single-run anyway.
      if grep -qE '"test"[[:space:]]*:[[:space:]]*"[^"]*vitest' "$d/package.json"; then
        run "test ($d)" env CI=true npm --prefix "$d" test -- --run
      else
        run "test ($d)" env CI=true npm --prefix "$d" test
      fi
    else skip "test ($d): no test script"; fi
  done
}
node_audit() {
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] || continue
    # A pnpm workspace has pnpm-lock.yaml and NO package-lock.json, so `npm audit` fails with
    # ENOLOCK on every run: the step "failed" for the wrong reason and the dependencies were never
    # scanned (30/09/2026: 2 critical + 27 high production advisories nobody had seen). Use the
    # tool that owns the lockfile; a missing pnpm is SKIPPED, never a pass.
    if [ -f "$d/pnpm-lock.yaml" ] || { [ "$d" != "." ] && [ -f "pnpm-lock.yaml" ]; }; then
      if have pnpm; then
        local out; out="$(mktemp)"
        if pnpm --dir "$d" audit --audit-level=high >"$out" 2>&1; then ok "pnpm audit ($d)"; else bad "pnpm audit ($d): high or critical"; tail -10 "$out" | sed 's/^/      /'; fi
        rm -f "$out"
      else skip "pnpm audit ($d): pnpm missing"; fi
    elif have npm; then
      local out; out="$(mktemp)"
      if npm --prefix "$d" audit --audit-level=high >"$out" 2>&1; then ok "npm audit ($d)"
      elif [ -f "$ROOT/scripts/audit-triage.tsv" ] && [ -f "$ROOT/scripts/audit-triage.py" ] && have python3 \
           && { npm --prefix "$d" audit --audit-level=high --json >"$out.j" 2>/dev/null; true; } \
           && python3 "$ROOT/scripts/audit-triage.py" "$ROOT/scripts/audit-triage.tsv" <"$out.j" >"$out.t" 2>&1; then
        ok "npm audit ($d): every high advisory is triaged in scripts/audit-triage.tsv"; sed 's/^/      /' "$out.t"
      else
        bad "npm audit ($d): high or critical"; tail -10 "$out" | sed 's/^/      /'; [ -s "$out.t" ] && sed 's/^/      /' "$out.t"
      fi
      rm -f "$out.t" "$out.j"
      rm -f "$out"
    else skip "npm audit ($d): npm missing"; fi
  done
}

# ── Node track beside the .NET steps (opt-in: GATE_PARALLEL_NODE=1) ──────────
# The Node steps never touch what the .NET steps touch, so on a machine with
# spare cores they cost nothing extra. The track runs in a subshell, prints to a
# log and writes its results as tagged lines; node_join replays both in the
# parent. Fail-closed: no DONE marker in the results means the track did not
# finish, and that is a failure. It is joined BEFORE the coverage step, which
# runs the frontend suite again and must not overlap it.
NODE_PARALLEL_ACTIVE=0; NODE_JOINED=0; NODE_PID=""; NODE_LOG=""; NODE_RES=""
if [ "${GATE_PARALLEL_NODE:-0}" = 1 ] && [ "$TARGET" != "prod" ] && [ "$LIST_ONLY" = 0 ] \
   && [ "$HAS_DOTNET" = 1 ] && { [ -n "$WEB_DIR" ] || [ -n "$MOBILE_DIR" ]; }; then
  NODE_PARALLEL_ACTIVE=1
fi
node_track() {
  local t0=$SECONDS
  say "node track — lint · typecheck · build · test · audit (ran beside the .NET steps)"
  node_lint; node_typecheck; node_build; node_test; node_audit
  printf '  node track: %ds\n' $((SECONDS - t0))
}
node_start() {
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] || return 0
  NODE_LOG="$(mktemp)"; NODE_RES="$(mktemp)"
  (
    node_track
    {
      for x in "${PASS[@]+"${PASS[@]}"}"; do printf 'P\t%s\n' "$x"; done
      for x in "${FAIL[@]+"${FAIL[@]}"}"; do printf 'F\t%s\n' "$x"; done
      for x in "${SKIP[@]+"${SKIP[@]}"}"; do printf 'S\t%s\n' "$x"; done
      for x in "${ACCEPTED[@]+"${ACCEPTED[@]}"}"; do printf 'A\t%s\n' "$x"; done
      for x in "${NA[@]+"${NA[@]}"}"; do printf 'N\t%s\n' "$x"; done
      for x in "${WARN[@]+"${WARN[@]}"}"; do printf 'W\t%s\n' "$x"; done
      echo DONE
    } >"$NODE_RES"
  ) >"$NODE_LOG" 2>&1 &
  NODE_PID=$!
  trap '[ -n "$NODE_PID" ] && kill "$NODE_PID" 2>/dev/null; rm -f "$NODE_LOG" "$NODE_RES"' EXIT
  echo "  node track started beside the .NET steps (GATE_PARALLEL_NODE=1)"
}
node_join() {
  [ "$NODE_PARALLEL_ACTIVE" = 1 ] && [ "$NODE_JOINED" = 0 ] || return 0
  NODE_JOINED=1
  wait "$NODE_PID" 2>/dev/null
  cat "$NODE_LOG"
  if grep -qx DONE "$NODE_RES" 2>/dev/null; then
    local kind label
    while IFS=$'\t' read -r kind label; do
      case "$kind" in
        P) PASS+=("$label") ;; F) FAIL+=("$label") ;; S) SKIP+=("$label") ;;
        A) ACCEPTED+=("$label") ;; N) NA+=("$label") ;; W) WARN+=("$label") ;;
      esac
    done <"$NODE_RES"
  else
    bad "node track did not report a result — a track that did not finish did not pass"
  fi
  rm -f "$NODE_LOG" "$NODE_RES"
}

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
    behaviour="$(printf '%s\n' "$changed" | grep -Ev '^(docs/|\.github/|scripts/|e2e/|.*\.md$)' | grep -E '\.(cs|ts|tsx|js|jsx|kt|swift)$' || true)"
    specs_touched="$(printf '%s\n' "$changed" | grep -E '^(e2e/|tests/e2e/|.*\.maestro/|.*\.spec\.ts)' || true)"
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
    warn "the missing spec(s) above are written at the test -> prod gate (#33 step 2); dev -> test is not blocked"
  fi
  fi
fi

# ── prod: deployed to test, then the full e2e suite ──────────────────────────
if [ "$TARGET" = "prod" ]; then

  say "is this code deployed to the TEST environment?"
  HEAD_SHA="$(git rev-parse HEAD)"
  if [ "$LIST_ONLY" = 1 ]; then
    if [ -n "${TEST_DEPLOY_SHA_CMD:-}" ]; then
      printf '  → %-42s %s\n' "deploy verification" "$TEST_DEPLOY_SHA_CMD"; PASS+=("deploy verification")
    elif [ -n "${TEST_VERSION_URL:-}" ]; then
      printf '  → %-42s %s\n' "deploy verification" "curl ${TEST_VERSION_URL} == ${HEAD_SHA:0:7}"; PASS+=("deploy verification")
    else
      # ⚠️ This used to count as PASS whatever was configured, so --list printed
      # "<no source configured — would block>" and then reported GATE GREEN while
      # the real run reported INCOMPLETE. A dry run that disagrees with the gate is
      # worse than no dry run: it is the one people read before asking for a
      # promotion. It now skips exactly as the real run does.
      skip "the deployed SHA cannot be read: set TEST_VERSION_URL (a /version endpoint per standards/17 §6) or TEST_DEPLOY_SHA_CMD in scripts/merge-gate.conf"
    fi
  else
    deployed=""
    if [ -n "${TEST_DEPLOY_SHA_CMD:-}" ]; then
      # A project-specific command that prints the SHA deployed to test, e.g. a
      # deploy record on the server or a GitHub deployment API query.
      deployed="$(bash -c "$TEST_DEPLOY_SHA_CMD" 2>/dev/null | grep -oE '[0-9a-f]{7,40}' | head -1)"
    elif [ -n "${TEST_VERSION_URL:-}" ]; then
      # Every URL in the list must report the same SHA: a project with several
      # sites on test is only "deployed" when all of them are.
      allsame=1
      for u in $TEST_VERSION_URL; do
        body="$(curl -fsS --max-time 10 "$u" 2>/dev/null)"
        case "$body" in *'<!doctype'*|*'<!DOCTYPE'*) bad "$u returned HTML, not a version — the SPA fallback is swallowing it (standards/14 §8)"; allsame=0; continue ;; esac
        one="$(printf '%s' "$body" | grep -oE '[0-9a-f]{7,40}' | head -1)"
        if [ -z "$one" ]; then bad "$u reports no commit SHA"; allsame=0; continue; fi
        [ -z "$deployed" ] && deployed="$one"
        [ "$one" != "$deployed" ] && { bad "$u reports $one while another site reports $deployed"; allsame=0; }
      done
      [ "$allsame" = 0 ] && deployed=""
    fi
    if [ -z "$deployed" ]; then
      skip "the deployed SHA cannot be read: set TEST_VERSION_URL (a /version endpoint per standards/17 §6) or TEST_DEPLOY_SHA_CMD in scripts/merge-gate.conf"
    # ⚠️ The patterns are QUOTED. Unquoted, `${HEAD_SHA#$deployed}` treats the
    # value as a GLOB, so a `*` or `[` arriving from a project's
    # TEST_DEPLOY_SHA_CMD would change what "is this SHA a prefix of that one"
    # means — on the step that decides whether prod may carry this code.
    elif [ "${HEAD_SHA#"$deployed"}" != "$HEAD_SHA" ] || [ "${deployed#"${HEAD_SHA:0:7}"}" != "$deployed" ]; then
      ok "the test environment is running this code ($deployed)"
    else
      bad "the test environment is running $deployed, not ${HEAD_SHA:0:7} — deploy to test first and wait for it"
    fi
  fi

  say "full e2e suite against the test environment"
  ran=0
  if [ "$HAS_E2E_WEB" = 1 ]; then
    ran=1
    if [ -n "${E2E_WEB_CMD:-}" ]; then run "web e2e ($E2E_WEB_CMD)" bash -c "$E2E_WEB_CMD"
    else run "web e2e (playwright)" npx playwright test; fi
  fi
  if [ "$HAS_E2E_MOBILE" = 1 ]; then
    ran=1
    if [ -n "${E2E_MOBILE_CMD:-}" ]; then run "mobile e2e ($E2E_MOBILE_CMD)" bash -c "$E2E_MOBILE_CMD"
    else skip "mobile e2e: set E2E_MOBILE_CMD (maestro needs a device/emulator)"; fi
  fi
  [ "$ran" = 0 ] && skip "e2e: this project has no e2e suite — nothing proves this promotion"
fi

# ── Result ───────────────────────────────────────────────────────────────────
printf '\n\033[1m── result ──\033[0m\n'
printf '  passed  : %d\n' "${#PASS[@]}"
printf '  warnings: %d\n' "${#WARN[@]}"
printf '  n/a     : %d\n' "${#NA[@]}"
printf '  accepted: %d\n' "${#ACCEPTED[@]}"
printf '  skipped : %d\n' "${#SKIP[@]}"
printf '  failed  : %d\n' "${#FAIL[@]}"
for x in "${WARN[@]+"${WARN[@]}"}"; do printf '  ! %s\n' "$x"; done
for x in "${ACCEPTED[@]+"${ACCEPTED[@]}"}"; do printf '  ~ ACCEPTED GAP %s\n' "$x"; done
[ "${#ACCEPTED[@]}" -gt 0 ] && printf '    reason: %s\n' "${ACCEPTED_GAPS_REASON:-}"
for x in "${SKIP[@]+"${SKIP[@]}"}"; do printf '  · SKIPPED %s\n' "$x"; done
for x in "${FAIL[@]+"${FAIL[@]}"}"; do printf '  ✗ %s\n' "$x"; done

if [ "${#FAIL[@]}" -gt 0 ]; then
  printf '\n\033[31mGATE CLOSED\033[0m — %d step(s) failed. No merge to %s.\n' "${#FAIL[@]}" "$TARGET"
  exit 1
fi
if [ "${#SKIP[@]}" -gt 0 ]; then
  printf '\n\033[33mGATE INCOMPLETE\033[0m — %d step(s) did not run. A step that did not run did not pass;\n' "${#SKIP[@]}"
  printf 'the result is not green. Install the tool, or record the reason and get the user to accept it.\n'
  exit 1
fi
if [ "${#ACCEPTED[@]}" -gt 0 ]; then
  printf '\n\033[32mGATE GREEN\033[0m (with %d accepted gap(s)) — %s promotion is allowed.\n' "${#ACCEPTED[@]}" "$TARGET"
  exit 0
fi
printf '\n\033[32mGATE GREEN\033[0m — every applicable step passed. %s promotion is allowed.\n' "$TARGET"
exit 0
