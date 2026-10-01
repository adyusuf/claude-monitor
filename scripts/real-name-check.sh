#!/usr/bin/env bash
# Real-name check: a REAL project name must never enter a commit that could be published.
#
# Usage:  real-name-check.sh --staged            # the staged change: file names and ADDED lines
#         real-name-check.sh --message FILE      # a commit message (what commit-msg.sh passes)
#
# Why it exists: real names reached this repository three times, and each time through a place a
# content scan would not look — a comment in a script, a generated log, and THREE COMMIT MESSAGES.
# Nothing failed, because nothing was checking; the names are still in the history.
#
# The names come from a LOCAL, git-ignored map (docs/project-nicknames.tsv) that also feeds the
# measurement ledger:  <folder key> TAB <nickname> [TAB <alias,alias>] [TAB public]
#   * column 1 (the key) and every alias in column 3 are searched for, case-insensitively;
#   * a 4th column of `public` means the name is not secret (this repository's own name, the
#     workspace folder) and is not searched for;
#   * a key ending in * (a prefix) or starting with - is skipped; a term of 3 characters or fewer is skipped.
# The map is looked up in $REAL_NAMES_MAP, then in this worktree, then in the main worktree.
#
# Exit 0 = clean, or the check could not run (no map: a warning, the same way pre-commit.sh treats a
# missing gitleaks — set REAL_NAMES_STRICT=1 to make a missing map a failure). Exit 1 = a real name
# was found. A deliberate exception is the USER's to make (git commit --no-verify); there is no
# environment switch, because a switch the agent can set is not a guard.
set -uo pipefail

mode="${1:-}"
target="${2:-}"
case "$mode" in
  --staged) ;;
  --message) [ -n "$target" ] || { echo "usage: $0 --message FILE" >&2; exit 2; } ;;
  *) echo "usage: $0 --staged | --message FILE" >&2; exit 2 ;;
esac

find_map() {
  if [ -n "${REAL_NAMES_MAP:-}" ]; then echo "$REAL_NAMES_MAP"; return; fi
  local top common
  top="$(git rev-parse --show-toplevel 2>/dev/null)" || return
  common="$(cd "$(git rev-parse --git-common-dir 2>/dev/null)/.." 2>/dev/null && pwd)"
  for candidate in "$top/docs/project-nicknames.tsv" "$common/docs/project-nicknames.tsv"; do
    [ -f "$candidate" ] && { echo "$candidate"; return; }
  done
}

map="$(find_map)"
if [ -z "$map" ] || [ ! -f "$map" ]; then
  echo "⚠️ real-name check NOT RUN — no project-nicknames.tsv (a gate that did not run did not pass)." >&2
  [ "${REAL_NAMES_STRICT:-0}" = "1" ] && exit 1
  exit 0
fi

terms="$(mktemp)"
trap 'rm -f "$terms"' EXIT
awk -F'\t' '
  /^#/ || NF < 2 { next }
  $4 == "public" { next }
  { key = $1
    if (key !~ /\*$/ && key !~ /^-/ && length(key) > 3) print key
    n = split($3, alias, ",")
    for (i = 1; i <= n; i++) { gsub(/^ +| +$/, "", alias[i]); if (length(alias[i]) > 3) print alias[i] } }
' "$map" > "$terms"
[ -s "$terms" ] || { echo "⚠️ real-name check NOT RUN — $map lists no name to look for." >&2; exit 0; }

found=0
report() { # report <where> <matching lines> — runs in the MAIN shell: `found` must survive it
  [ -n "$2" ] || return 0
  found=1
  printf '✗ a real project name is in %s:\n' "$1" >&2
  printf '%s\n' "$2" | head -8 | sed 's/^/    /' >&2
}

if [ "$mode" = "--staged" ]; then
  report "a staged FILE NAME" "$(git diff --cached --name-only --diff-filter=ACMR | grep -i -F -f "$terms")"
  report "the staged CONTENT (added lines)" "$(git diff --cached -U0 --no-color --diff-filter=ACMR | awk '
    /^\+\+\+ b\// { file = substr($0, 7); next }
    /^\+\+\+ /    { file = ""; next }
    /^@@/         { match($0, /\+[0-9]+/); line = substr($0, RSTART + 1, RLENGTH - 1) + 0; next }
    /^\+/         { print file ":" line ": " substr($0, 2); line++ }
  ' | grep -i -F -f "$terms")"
else
  report "the COMMIT MESSAGE" "$(grep -v '^#' "$target" | grep -n -i -F -f "$terms")"
fi

if [ "$found" = 1 ]; then
  echo "  Use a nickname or a made-up name. Terms come from: $map" >&2
  echo "  A deliberate exception is the user's to make (git commit --no-verify)." >&2
  exit 1
fi
exit 0
