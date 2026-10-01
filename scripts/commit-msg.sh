#!/usr/bin/env bash
# commit-msg hook: the real-name check over the MESSAGE (three of the names that reached this
# repository's history came in through commit messages). Installed by `pre-commit.sh --install`.
# Silent when the project did not copy scripts/real-name-check.sh.
root="$(git rev-parse --show-toplevel 2>/dev/null)" || exit 0
[ -f "$root/scripts/real-name-check.sh" ] || exit 0
exec bash "$root/scripts/real-name-check.sh" --message "$1"
