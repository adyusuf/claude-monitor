#!/usr/bin/env bash
# Gate library: the test -> prod steps (deployed SHA on test, then the full e2e suite).
# Sourced by gate-core.sh - never run on its own. A TWIN (scripts/twins.txt). Code unchanged from
# the former gate-core.sh lines 460-524.
# shellcheck shell=bash

gate_prod() {

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
      else run "web e2e (playwright)" bash -c "cd \"$E2E_WEB_DIR\" && npx playwright test"; fi
    fi
    if [ "$HAS_E2E_MOBILE" = 1 ]; then
      ran=1
      if [ -n "${E2E_MOBILE_CMD:-}" ]; then run "mobile e2e ($E2E_MOBILE_CMD)" bash -c "$E2E_MOBILE_CMD"
      else skip "mobile e2e: set E2E_MOBILE_CMD (maestro needs a device/emulator)"; fi
    fi
    [ "$ran" = 0 ] && skip "e2e: this project has no e2e suite — nothing proves this promotion"
}
