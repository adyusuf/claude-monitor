#!/usr/bin/env bash
# CLAUDE.md size gate — stops guidance files from bloating silently.
#
# Every `CLAUDE.md` enters context automatically in EVERY session that runs in
# that directory, so the cost of adding a line is paid not once but again in
# every session that reads the file. The budget lives in `scripts/md-budget.tsv`
# (a ratchet: the ceiling only ever goes down).
#
# Usage:
#   scripts/md-size-gate.sh            # audit (the gate)
#   scripts/md-size-gate.sh --update   # pull the ceilings down to TODAY's size
#
# Exit code: 0 = passed, 1 = a budget was exceeded or a file has no budget.
set -uo pipefail

# ⚠️ ROOT and BUDGET can be supplied from outside. Reason: the CANONICAL copy of
# this script lives under ~/.claude/scripts/, and when it is called from there a
# ROOT derived from the script's own location is wrong (it resolves to ~/.claude,
# not the project). For the copy COPIED INTO a project the defaults are correct;
# the hook passes MD_ROOT.
ROOT="${MD_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
UPDATE=0
HOOK=0
for arg in "$@"; do
  [ "$arg" = "--update" ] && UPDATE=1
  [ "$arg" = "--hook" ] && HOOK=1
done

# Budget file: explicit path > scripts/ > root > docs/. Most projects have no scripts/.
if [ -n "${MD_BUDGET:-}" ]; then BUDGET="$MD_BUDGET"; else
  for candidate in "$ROOT/scripts/md-budget.tsv" "$ROOT/md-budget.tsv" "$ROOT/docs/md-budget.tsv"; do
    [ -f "$candidate" ] && { BUDGET="$candidate"; break; }
  done
fi

if [ -z "${BUDGET:-}" ] || [ ! -f "$BUDGET" ]; then
  # ⚠️ In HOOK mode "no budget" is NOT an error, it means "not installed": the
  # gate passes silently and says so in one line. When it runs as a merge gate
  # (the default) a MISSING BUDGET IS AN ERROR — otherwise deleting the budget
  # file would delete the gate along with it.
  if [ "$HOOK" -eq 1 ]; then
    echo "· No CLAUDE.md budget is installed in this project (md-budget.tsv is missing)."
    echo "  To install it: /adyusuf:apply-project-standards  or"
    echo "  cp ~/.claude/scripts/md-*.sh ~/.claude/scripts/md-*.py scripts/ && bash scripts/md-size-gate.sh --update"
    exit 0
  fi
  echo "✗ No budget file: ${BUDGET:-<not found>}"; exit 1
fi

# ⚠️ LINE ENDINGS ARE NORMALIZED (a real incident). `wc -c` used to measure the
# file in the working tree. In a Windows checkout with `core.autocrlf=true` every
# line is one byte longer, and the result was that the backend/web/mobile
# CLAUDE.md files reported "ceiling exceeded" WITH NO CHANGE MADE AT ALL (+1753,
# +930 and +659 bytes respectively). The gate was structurally red on Windows and
# the ceilings drifted per machine. CR bytes are now SUBTRACTED from the count —
# the measurement matches the git blob and is INDEPENDENT of the operating system.
kb() {
  local bytes cr
  bytes=$(wc -c < "$1")
  cr=$(tr -cd '\r' < "$1" | wc -c)
  echo $(( ( bytes - cr + 1023 ) / 1024 ))
}

FAILED=0
LISTED=""
# Project-specific discovery exclusions: `# exclude:<relative-path>` in the budget
# file. Why in the tsv: an exclusion is project data (in ~/.claude, plugins/ and
# skills/addy are third-party; another project excludes something else) — it is
# not baked into the script, the script is generic.
# ⚠️ An empty "# exclude:" is ignored so a typo cannot exclude everything.
EXCLUDE=()
while IFS= read -r line; do
  case "$line" in
    "# exclude:"*) d="${line#\# exclude:}" ;;
    *) continue ;;
  esac
  d="${d%%[[:space:]]*}"; [ -n "$d" ] && EXCLUDE+=("${d%/}")
done < "$BUDGET"

printf '%-28s %7s %7s %7s  %s\n' FILE NOW CEILING TARGET STATUS
printf '%.0s─' {1..78}; echo

while IFS=$'\t' read -r path ceiling target note; do
  case "$path" in ''|\#*) continue ;; esac
  LISTED="$LISTED|$path"
  full="$ROOT/$path"
  if [ ! -f "$full" ]; then
    printf '%-28s %7s %7s %7s  %s\n' "$path" "-" "$ceiling" "$target" "✗ FILE MISSING"
    FAILED=1; continue
  fi
  now=$(kb "$full")
  if [ "$now" -gt "$ceiling" ]; then
    printf '%-28s %7s %7s %7s  %s\n' "$path" "$now" "$ceiling" "$target" "✗ CEILING EXCEEDED (+$((now-ceiling)) KB)"
    FAILED=1
  elif [ "$now" -le "$target" ]; then
    printf '%-28s %7s %7s %7s  %s\n' "$path" "$now" "$ceiling" "$target" "✓ on target"
  else
    printf '%-28s %7s %7s %7s  %s\n' "$path" "$now" "$ceiling" "$target" "· under the ceiling"
  fi
done < "$BUDGET"

# A new CLAUDE.md with no budget — the gate catches that too, so one cannot be
# added quietly and then grow.
# ⚠️ Exclusions are matched RELATIVE TO THE ROOT, not against the absolute path
# (found by mutation testing): the old pattern was `-not -path "*/.claude/*"`, and
# when the script ran from a git WORKTREE (path: .../Repo/.claude/worktrees/x/)
# THE ROOT ITSELF matched the pattern — the discovery loop found NO files at all
# and a new, budget-less CLAUDE.md passed silently. There was no symptom because
# the budgeted files were still checked through the tsv; the blind spot went
# unnoticed for a full day.
# ⚠️ Do NOT put a COMMENT inside the <( ... ) below: at runtime bash loses the ")"
# boundary, the loop collapses and the gate says "green" again (bash -n does NOT
# catch this).
while IFS= read -r file; do
  # ⚠️ QUOTED: unquoted, $ROOT is a GLOB here. A repository checked out under a
  # path containing `[`, `*` or `?` — a worktree name, a branch slug — would then
  # fail to strip the prefix, so `rel` kept the absolute path, the exclusion
  # patterns below stopped matching, and a budget-less CLAUDE.md passed. Same
  # class as the worktree blind spot recorded above, different mechanism.
  rel="${file#"$ROOT"/}"
  case "$rel" in .claude/*|**/node_modules/*|docs/claude-md-archive/*) continue ;; esac
  skip=0; for d in "${EXCLUDE[@]+"${EXCLUDE[@]}"}"; do case "$rel" in "$d"/*|"$d") skip=1 ;; esac; done
  [ "$skip" -eq 1 ] && continue
  if ! echo "$LISTED" | grep -q "|$rel"; then
    printf '%-28s %7s %7s %7s  %s\n' "$rel" "$(kb "$file")" "-" "-" "✗ NO BUDGET"
    FAILED=1
  fi
done < <(find "$ROOT" \( -path "$ROOT/.claude" -o -path "*/node_modules" -o -path "$ROOT/docs/claude-md-archive" \) -prune -o -name CLAUDE.md -print 2>/dev/null)

echo

if [ "$UPDATE" -eq 1 ]; then
  # Only ever LOWERS the ceiling. Raising it is done by hand, with a reason —
  # an automatic raise cancels the ratchet and makes the gate decorative.
  tmp="$(mktemp)"
  while IFS=$'\t' read -r path ceiling target note; do
    case "$path" in ''|\#*) echo "$path" >> "$tmp"; continue ;; esac
    full="$ROOT/$path"
    if [ -f "$full" ]; then
      now=$(kb "$full")
      if [ "$now" -lt "$ceiling" ]; then
        echo "  ↓ $path ceiling $ceiling -> $now KB"
        ceiling="$now"
      fi
    fi
    printf '%s\t%s\t%s\t%s\n' "$path" "$ceiling" "$target" "$note" >> "$tmp"
  done < "$BUDGET"
  mv "$tmp" "$BUDGET"
  echo "Budget updated (lowered only)."
  exit 0
fi

if [ "$FAILED" -eq 1 ]; then
  cat <<'END'
✗ GATE CLOSED — the CLAUDE.md budget was exceeded.

What to do, in order:
  1. Is what you added a RULE, or is it rationale/history/a test record?
     Rationale and history do NOT belong in the active file — move them to
     docs/<tier>-decision-log.md and leave the rule sentence plus a link behind.
  2. If it really is a new rule that has to live in the active file:
     raise the ceiling in scripts/md-budget.tsv and write the REASON in the note column.
  3. If you simplified the file, lower the ceiling:  scripts/md-size-gate.sh --update
END
  exit 1
fi

echo "✓ Every CLAUDE.md is within its budget."
exit 0
