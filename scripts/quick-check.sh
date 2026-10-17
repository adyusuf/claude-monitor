#!/usr/bin/env bash
# Quick local pre-check — a fast look before a merge, NOT the gate. The full gate is CI's (it runs
# scripts/gate-core.sh on every push); this runs the cheap steps of it with the SAME commands, so
# there is no second rule set here and nothing that passes here can fail there for a different rule.
#
# Usage:  scripts/quick-check.sh [--tests] [--base <ref>] [--list]
#   --tests       also run the test projects the change can affect (a .NET test project whose project
#                 closure holds a changed file, and the web tests when web/ changed)
#   --base <ref>  what "changed" is measured against (default: origin/dev, else only uncommitted files)
#   --list        print what WOULD run and run nothing
#
# Steps (the command is the gate's own):
#   dotnet format <sln> --verify-no-changes     dotnet build <sln> -warnaserror
#   the shell linter, severity=style, on the changed *.sh files (scripts/codeql-scan.sh lints every script the same way)
#   gitleaks detect --no-banner --redact
#   the script tests (scripts/tests) when a .py or .sh file changed — the command coverage.sh runs
#
# Exit codes: 0 every step ran and passed · 1 a step failed · 3 a step did not run (a tool is missing):
# it is reported as SKIPPED and the result is INCOMPLETE — a step that did not run did not pass.
set -uo pipefail

want_tests=0; list_only=0; base_ref=""
while [ $# -gt 0 ]; do
  case "$1" in
    --tests) want_tests=1 ;;
    --list) list_only=1 ;;
    --base) shift; base_ref="${1:-}"; [ -n "$base_ref" ] || { echo "usage: $0 [--tests] [--base <ref>] [--list]" >&2; exit 2; } ;;
    *) echo "usage: $0 [--tests] [--base <ref>] [--list]" >&2; exit 2 ;;
  esac
  shift
done

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repository" >&2; exit 2; }
cd "$ROOT" || exit 2

PASS=0; FAIL=0; SKIP=0
ok()   { printf '  ✓ %s\n' "$1"; PASS=$((PASS + 1)); }
bad()  { printf '  ✗ %s\n' "$1"; FAIL=$((FAIL + 1)); }
skip() { printf '  · SKIPPED: %s\n' "$1"; SKIP=$((SKIP + 1)); }
have() { command -v "$1" >/dev/null 2>&1; }
step() { # step <label> <tool> <command...>: a listing, a skipped step, a pass or a failure
  local label="$1" tool="$2"; shift 2
  if [ "$list_only" = 1 ]; then printf '  → %-34s %s\n' "$label" "$*"; return 0; fi
  if ! have "$tool"; then skip "$label ($tool is not installed)"; return 0; fi
  local out; out="$(mktemp)"
  if "$@" >"$out" 2>&1; then ok "$label"; else bad "$label"; tail -20 "$out" | sed 's/^/      /'; fi
  rm -f "$out"
}

# What changed: against the base (merge-base, so a stale dev does not add other people's files) plus
# uncommitted and untracked files. Without the base ref only the uncommitted files count.
base_ref="${base_ref:-origin/dev}"
if base="$(git merge-base HEAD "$base_ref" 2>/dev/null)"; then
  changed="$( { git diff --name-only "$base"; git ls-files --others --exclude-standard; } | sort -u)"
else
  echo "note: $base_ref not found — only uncommitted files count as changed" >&2
  changed="$( { git diff --name-only HEAD; git ls-files --others --exclude-standard; } | sort -u)"
fi
changed_files() { printf '%s\n' "$changed" | grep -E "$1" || true; }

# shellcheck disable=SC2012  # the same lookup as the gate's stack detection: a solution at the root
SLN="$(ls ./*.slnx ./*.sln 2>/dev/null | head -1)"

printf 'quick check (%s, %s changed file(s)) — the full gate is CI, this is only a first look\n' \
  "$(git rev-parse --short HEAD)" "$(printf '%s\n' "$changed" | grep -c . || true)"

echo; echo "▶ formatter / build"
if [ -n "$SLN" ]; then
  step "dotnet format" dotnet dotnet format "$SLN" --verify-no-changes
  step "dotnet build" dotnet dotnet build "$SLN" -warnaserror
else
  echo "  n/a: no solution at the repository root"
fi

echo; echo "▶ shellcheck (changed shell scripts)"
sh_files="$(changed_files '\.sh$')"
sh_existing=()
while IFS= read -r f; do [ -n "$f" ] && [ -f "$f" ] && sh_existing+=("$f"); done <<<"$sh_files"
if [ "${#sh_existing[@]}" -gt 0 ]; then
  step "shellcheck (${#sh_existing[@]} file(s))" shellcheck shellcheck --severity=style "${sh_existing[@]}"
else
  echo "  n/a: no shell script changed"
fi

echo; echo "▶ secret scan"
step "gitleaks detect" gitleaks gitleaks detect --no-banner --redact

echo; echo "▶ script tests"
if [ -n "$(changed_files '\.(py|sh)$')" ]; then
  step "scripts/tests" python3 python3 -m unittest discover -s scripts/tests -p 'test_*.py'
  find scripts -name __pycache__ -prune -exec rm -rf {} + 2>/dev/null
else
  echo "  n/a: no .py or .sh file changed"
fi

# The test projects a change can affect: a test project is affected when a changed file lies inside it or
# inside any project it references (transitively, through ProjectReference).
closure() { # closure <csproj>: the directories of the project and everything it references
  local seen=" " queue=("$1") cur dir ref
  while [ "${#queue[@]}" -gt 0 ]; do
    cur="${queue[0]}"; queue=("${queue[@]:1}")
    dir="$(dirname "$cur")"
    case "$seen" in *" $dir "*) continue ;; esac
    seen="$seen$dir "
    while IFS= read -r ref; do
      [ -n "$ref" ] || continue
      ref="${ref//\\//}"
      queue+=("$(cd "$dir" && cd "$(dirname "$ref")" 2>/dev/null && printf '%s/%s' "$PWD" "$(basename "$ref")")")
    done < <(grep -o 'ProjectReference Include="[^"]*"' "$cur" | sed 's/.*Include="//; s/"$//')
  done
  printf '%s\n' "$seen" | tr ' ' '\n' | grep -v '^$' || true
}
if [ "$want_tests" = 1 ]; then
  echo; echo "▶ affected tests"
  ran=0
  for proj in tests/*/*.csproj; do
    [ -f "$proj" ] || continue
    hit=0
    while IFS= read -r dir; do
      rel="${dir#"$ROOT"/}"
      [ -n "$(changed_files "^$(printf '%s' "$rel" | sed 's/[.[\*^$]/\\&/g')/")" ] && { hit=1; break; }
    done < <(closure "$ROOT/$proj")
    if [ "$hit" = 1 ]; then ran=1; step "dotnet test $(basename "$(dirname "$proj")")" dotnet dotnet test "$proj" --nologo; fi
  done
  if [ -n "$(changed_files '^web/')" ]; then
    ran=1; step "web tests" npm env CI=true npm --prefix web test -- --run
  fi
  [ "$ran" = 1 ] || echo "  n/a: no test project is affected by this change"
fi

[ "$list_only" = 1 ] && exit 0
printf '\n── quick check: %d passed, %d failed, %d skipped ──\n' "$PASS" "$FAIL" "$SKIP"
if [ "$FAIL" -gt 0 ]; then echo "RED — fix the failing step(s) before the merge."; exit 1; fi
if [ "$SKIP" -gt 0 ]; then echo "INCOMPLETE — a step that did not run did not pass. Install the missing tool."; exit 3; fi
echo "GREEN — a first look only. CI is the gate."
exit 0
