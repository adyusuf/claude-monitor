#!/usr/bin/env bash
# Gate library: the result counters, the step helpers, stack detection and the final verdict.
# Sourced by gate-core.sh - never run on its own. One of the gate's TWINS (scripts/twins.txt):
# it is copied to every project beside gate-core.sh and must stay byte-identical to the canonical copy.
# Split out of gate-core.sh so that no file passes 300 lines (global rule #9); the code is unchanged.
# shellcheck shell=bash

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
# The web suite is e2e/ or tests/e2e/ at the root, OR e2e/ inside the web tier (web/e2e, frontend/e2e):
# a project that keeps its Playwright suite next to the app used to be reported as "no e2e suite".
E2E_WEB_DIR=""
if [ -d e2e ] || [ -d tests/e2e ]; then E2E_WEB_DIR="."
elif [ -n "$WEB_DIR" ] && [ "$WEB_DIR" != "." ] && [ -d "$WEB_DIR/e2e" ]; then E2E_WEB_DIR="$WEB_DIR"; fi
HAS_E2E_WEB=0;    [ -n "$E2E_WEB_DIR" ] && HAS_E2E_WEB=1
HAS_E2E_MOBILE=0; [ -d .maestro ] || [ -d "${MOBILE_DIR:-mobile}/.maestro" ] && HAS_E2E_MOBILE=1

echo "merge gate → $TARGET   ($(git rev-parse --short HEAD), $ROOT)"
echo "stacks: dotnet=$HAS_DOTNET${SLN:+ (${SLN#./})} node=$HAS_NODE web=${WEB_DIR:-none} mobile=${MOBILE_DIR:-none} e2e=web:$HAS_E2E_WEB/mobile:$HAS_E2E_MOBILE"

# ── The verdict ──────────────────────────────────────────────────────────────
gate_result() {
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
}
