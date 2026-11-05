#!/usr/bin/env bash
# Gate library: the test -> prod steps (the test deploy check and e2e, BOTH only on request, see below).
# Sourced by gate-core.sh - never run on its own. A TWIN (scripts/twins.txt). Code unchanged from
# the former gate-core.sh lines 460-524.
# shellcheck shell=bash

# Opt-in since 04/10/2026 (CLAUDE.md #33): the prod gate does not look at the test branch or the test
# environment unless GATE_CHECK_TEST_DEPLOY=1 asks for it.
gate_prod_check_test_deploy() {

    say "is this code deployed to the TEST environment? (requested with GATE_CHECK_TEST_DEPLOY=1)"
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
}

gate_prod() {
    if [ "${GATE_CHECK_TEST_DEPLOY:-0}" = 1 ]; then
      gate_prod_check_test_deploy
    else
      say "test branch / test environment — not checked"
      warn "the test branch and the test environment were NOT checked (not required since 04/10/2026; GATE_CHECK_TEST_DEPLOY=1 checks the deployed SHA) — this promotion does not prove the code ran on test"
    fi

    gate_e2e_suite
}

# The e2e suite, on request only (#33). prod: GATE_RUN_E2E=1, or the panel's e2e_before_deploy.prod = yes, runs the suite
# against the TEST environment (E2E_WEB_CMD / E2E_MOBILE_CMD, the code being promoted is deployed there). test: only the
# panel's e2e_before_deploy.test = yes, and it runs on DEV (standards/13 §8a): E2E_DEV_WEB_CMD / E2E_DEV_MOBILE_CMD in
# merge-gate.conf - the test environment still holds the previous release, so the test commands would prove nothing.
# Asked by the panel where the project has no suite or no such environment: the report says so, not a failure (§8a).
# Asked with GATE_RUN_E2E=1 and no suite: SKIPPED, as before. A red run blocks like any other step.
gate_e2e_suite() {
    local asked="" web_cmd mobile_cmd where
    if [ "$TARGET" = "prod" ]; then
      [ "${GATE_RUN_E2E:-0}" = 1 ] && asked="GATE_RUN_E2E=1"
      web_cmd="${E2E_WEB_CMD:-}"; mobile_cmd="${E2E_MOBILE_CMD:-}"; where="the test environment"
    else
      web_cmd="${E2E_DEV_WEB_CMD:-}"; mobile_cmd="${E2E_DEV_MOBILE_CMD:-}"; where="dev"
    fi
    [ -z "$asked" ] && [ "$(panel_value "e2e_before_deploy.$TARGET")" = "yes" ] && asked="e2e_before_deploy.$TARGET = yes in $SDLC_SETTINGS_LABEL"
    if [ -z "$asked" ]; then
      say "e2e (optional) — not run"
      warn "e2e was NOT run (optional; e2e_before_deploy.$TARGET in the panel runs it) — nothing in this promotion was proven end to end"
      return 0
    fi
    say "e2e suite on $where (requested: $asked)"
    if [ "$TARGET" != "prod" ] && [ -z "$web_cmd$mobile_cmd" ]; then
      warn "no such environment: set E2E_DEV_WEB_CMD / E2E_DEV_MOBILE_CMD in scripts/merge-gate.conf (the suite against dev) — e2e was NOT run"
      return 0
    fi
    ran=0
    if [ "$HAS_E2E_WEB" = 1 ] || [ -n "$web_cmd" ]; then
      if [ -n "$web_cmd" ]; then ran=1; run "web e2e ($web_cmd)" bash -c "$web_cmd"
      elif [ "$TARGET" = "prod" ]; then ran=1; run "web e2e (playwright)" bash -c "cd \"$E2E_WEB_DIR\" && npx playwright test"; fi
    fi
    if [ "$HAS_E2E_MOBILE" = 1 ] || [ -n "$mobile_cmd" ]; then
      ran=1
      if [ -n "$mobile_cmd" ]; then run "mobile e2e ($mobile_cmd)" bash -c "$mobile_cmd"
      else skip "mobile e2e: set E2E_MOBILE_CMD (maestro needs a device/emulator)"; fi
    fi
    if [ "$ran" = 0 ]; then
      if [ "${GATE_RUN_E2E:-0}" = 1 ] && [ "$TARGET" = "prod" ]; then skip "e2e: this project has no e2e suite — nothing proves this promotion"
      else warn "no e2e suite in this project — e2e was NOT run (not a failure, standards/13 §8a)"; fi
    fi
}
