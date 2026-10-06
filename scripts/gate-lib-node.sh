#!/usr/bin/env bash
# Gate library: the Node steps (lint, typecheck, build, test, audit) and the opt-in parallel track.
# Sourced by gate-core.sh - never run on its own. A TWIN (scripts/twins.txt): code as in the canonical
# copy (the former gate-core.sh lines 184-315) plus node_install, which the canonical copy needs too.
# shellcheck shell=bash

# ── Node steps as functions ──────────────────────────────────────────────────
# Serial mode calls each one where its section is; GATE_PARALLEL_NODE=1 runs them
# all as one track beside the .NET steps (node_start / node_join below).
# A fresh checkout or worktree has no node_modules, and lint / tsc / build / test then fail with
# "eslint: command not found" - a false red, because the gate never installed what it runs (the install
# used to hide in the coverage step, which runs AFTER these). Install once, before the first Node step,
# with the same idiom coverage.sh uses: only when node_modules is missing. FAIL-CLOSED: a failed install
# is a failed step carrying the npm log, never a skip and never a pass.
NODE_DEPS_DONE=0
node_install() {
  [ "$NODE_DEPS_DONE" = 0 ] || return 0
  NODE_DEPS_DONE=1
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] && [ -f "$d/package.json" ] && [ ! -d "$d/node_modules" ] || continue
    if [ -f "$d/pnpm-lock.yaml" ] || { [ "$d" != "." ] && [ -f "pnpm-lock.yaml" ]; }; then
      if have pnpm || [ "$LIST_ONLY" = 1 ]; then run "install dependencies ($d)" pnpm --dir "$d" install --frozen-lockfile
      else bad "install dependencies ($d): pnpm is missing"; fi
    elif have npm || [ "$LIST_ONLY" = 1 ]; then run "install dependencies ($d)" npm --prefix "$d" ci
    else bad "install dependencies ($d): npm is missing"; fi
  done
}
node_lint() {
  node_install
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] || continue
    if [ -f "$d/node_modules/.bin/eslint" ] || grep -q '"lint"' "$d/package.json" 2>/dev/null; then
      run "lint ($d)" npm --prefix "$d" run lint
    else skip "lint ($d): no lint script"; fi
  done
}
node_typecheck() {
  node_install
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
  node_install
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] || continue
    if grep -q '"build"' "$d/package.json" 2>/dev/null; then run "build ($d)" npm --prefix "$d" run build
    else skip "build ($d): no build script"; fi
  done
}
node_test() {
  node_install
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
  node_install
  for d in "$WEB_DIR" "$MOBILE_DIR"; do
    [ -n "$d" ] || continue
    # A pnpm workspace has pnpm-lock.yaml and NO package-lock.json, so `npm audit` fails with
    # ENOLOCK on every run: the step "failed" for the wrong reason and the dependencies were never
    # scanned (30/09/2026: 2 critical + 27 high production advisories nobody had seen). Use the
    # tool that owns the lockfile; a missing pnpm is SKIPPED, never a pass.
    if [ -f "$d/pnpm-lock.yaml" ] || { [ "$d" != "." ] && [ -f "pnpm-lock.yaml" ]; }; then
      if have pnpm; then
        local out; out="$(mktemp)"
        if pnpm --dir "$d" audit --audit-level=high >"$out" 2>&1; then ok "pnpm audit ($d)"
        elif [ -f "$ROOT/scripts/audit-triage.tsv" ] && [ -f "$ROOT/scripts/audit-triage.py" ] && have python3 \
             && { pnpm --dir "$d" audit --audit-level=high --json >"$out.j" 2>/dev/null; true; } \
             && python3 "$ROOT/scripts/audit-triage.py" "$ROOT/scripts/audit-triage.tsv" <"$out.j" >"$out.t" 2>&1; then
          ok "pnpm audit ($d): every high advisory is triaged in scripts/audit-triage.tsv"; sed 's/^/      /' "$out.t"
        else
          bad "pnpm audit ($d): high or critical"; tail -10 "$out" | sed 's/^/      /'; [ -s "$out.t" ] && sed 's/^/      /' "$out.t"
        fi
        rm -f "$out.t" "$out.j" "$out"
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
