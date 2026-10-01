#!/usr/bin/env bash
# PRE-COMMIT gate — runs BEFORE the commit and STOPS it when red.
#
# Why it exists: md-hook.sh is a behavioural warning and always exits 0. In a
# real round the CLAUDE.md budget stayed over the ceiling across two commits
# because nothing blocked them, and each "fixed it" commit was written without
# measuring first. A ceiling nobody measures is not a ceiling; the gate belongs
# on the commit itself.
#
# Four steps, all in seconds, so the "merging into dev is fast" rule still holds:
#   1. CLAUDE.md size budget   (scripts/md-size-gate.sh)
#   2. gitleaks — secret scan over the staged content (pre-commit gitleaks is
#      the one gate that stays on in the dev direction)
#   3. documentation consistency (scripts/doc-check.py, ~0.1 s) — only when the
#      project carries that script
#   4. real project names (scripts/real-name-check.sh) — file names and added lines;
#      the commit MESSAGE is checked by the commit-msg hook this file also installs
#
# Install:            bash scripts/pre-commit.sh --install
# Deliberate bypass:  git commit --no-verify   (write the reason in the message)
set -uo pipefail

root="$(git rev-parse --show-toplevel 2>/dev/null)" || exit 0
[ -n "$root" ] || exit 0

if [ "${1:-}" = "--install" ]; then
  mkdir -p "$root/.git/hooks"
  ln -sf ../../scripts/pre-commit.sh "$root/.git/hooks/pre-commit"
  echo "installed: $root/.git/hooks/pre-commit -> scripts/pre-commit.sh"
  if [ -f "$root/scripts/commit-msg.sh" ]; then
    ln -sf ../../scripts/commit-msg.sh "$root/.git/hooks/commit-msg"
    echo "installed: $root/.git/hooks/commit-msg -> scripts/commit-msg.sh"
  fi
  exit 0
fi

failed=0

# The LIVE configuration repository (~/.claude) is exempt from steps 3 and 4. Its scripts/ is a
# symlink into the configuration repository, so it "has" doc-check.py and real-name-check.sh — but
# its memory notes refer to OTHER projects' files (a broken link to this repo, a correct one to that
# project), and real project names are what make those notes usable. Steps 1 and 2 still apply.
live_config=0
if [ -d "$HOME/.claude" ] && [ "$(cd -P "$HOME/.claude" && pwd)" = "$(cd -P "$root" && pwd)" ]; then live_config=1; fi

# --- 1. CLAUDE.md size budget ------------------------------------------------
if [ -f "$root/scripts/md-size-gate.sh" ]; then
  output="$(MD_ROOT="$root" bash "$root/scripts/md-size-gate.sh" 2>&1)" || true
  if printf '%s' "$output" | grep -qi 'CEILING EXCEEDED\|NO BUDGET'; then
    printf '%s\n' "$output"
    echo "✗ commit STOPPED — CLAUDE.md budget (standards/00-working-method.md §6a)."
    echo "  Move the rule to docs/decision-log.md, or raise the ceiling with a written reason."
    failed=1
  fi
fi

# --- 2. gitleaks over the staged content -------------------------------------
if command -v gitleaks >/dev/null 2>&1; then
  if ! gitleaks git --staged --no-banner --redact -l error >/dev/null 2>&1; then
    echo "✗ commit STOPPED — gitleaks found a secret in the staged content."
    echo "  If a secret leaked, ROTATE it first, then clean the history."
    failed=1
  fi
else
  echo "⚠️ gitleaks is not installed — the secret scan DID NOT RUN (a gate that does not run is not a gate that passed)."
fi

# --- 3. documentation consistency ---------------------------------------------
if [ "$live_config" = 0 ] && [ -f "$root/scripts/doc-check.py" ]; then
  if ! output="$(python3 "$root/scripts/doc-check.py" "$root" 2>&1)"; then
    printf '%s\n' "$output"
    echo "✗ commit STOPPED — documentation drift (a broken link, a missing index entry, a stale count)."
    failed=1
  fi
fi

# --- 4. real project names in the staged change --------------------------------
if [ "$live_config" = 0 ] && [ -f "$root/scripts/real-name-check.sh" ]; then
  bash "$root/scripts/real-name-check.sh" --staged || {
    echo "✗ commit STOPPED — a real project name (see above)."
    failed=1
  }
fi

exit "$failed"
