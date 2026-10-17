#!/usr/bin/env bash
# Which gates were waived on the way to a promotion? A gate that was not run is recorded on the merge commit as
#     Gate-Waived: ci, e2e (neden: <the maintainer's words>)
# (global rule: ask before every merge, record what is waived). This lists those lines for the commits that are in
# <target> but not in <source> — i.e. what a promotion source -> target would carry — so the maintainer is told
# plainly before prod.
#
# Usage:  scripts/waived-gates.sh <source> <target>        e.g.  scripts/waived-gates.sh origin/prod origin/test
#
# Every commit in <source>..<target> is read, not only merge commits: a waiver written on any commit still counts.
# Output: one row per waived gate — short commit, gate, reason, subject.
# Exit codes: 0 no record (nothing was waived) · 1 at least one waived gate is recorded · 2 bad usage or a ref
# that cannot be read (never "nothing waived": a range that was not read proves nothing).
set -uo pipefail

[ $# -eq 2 ] || { echo "usage: $0 <source> <target>" >&2; exit 2; }
source_ref="$1"; target_ref="$2"
for ref in "$source_ref" "$target_ref"; do
  git rev-parse --verify --quiet "$ref^{commit}" >/dev/null || { echo "cannot read '$ref'" >&2; exit 2; }
done

rows="$(git log --format='@@%h%x09%s%n%B' "$source_ref..$target_ref" | awk '
  /^@@/ { split(substr($0, 3), head, "\t"); sha = head[1]; subj = head[2]; next }
  /^Gate-Waived:/ {
    line = $0; sub(/^Gate-Waived:[ \t]*/, "", line)
    gates = line; reason = ""
    if (match(line, /[ \t]*\(/)) { gates = substr(line, 1, RSTART - 1); reason = substr(line, RSTART + RLENGTH) ; sub(/\)[ \t]*$/, "", reason); sub(/^(neden|reason):[ \t]*/, "", reason) }
    n = split(gates, list, /[ \t]*,[ \t]*/)
    for (i = 1; i <= n; i++) if (list[i] != "") printf "%s\t%s\t%s\t%s\n", sha, list[i], reason, subj
  }')"

if [ -z "$rows" ]; then
  echo "no record: no gate is recorded as waived in $source_ref..$target_ref"
  exit 0
fi
echo "GATES NOT RUN in $source_ref..$target_ref:"
printf '%s\n' "$rows" | awk -F'\t' '{ printf "  %s  %-6s  %s  [%s]\n", $1, $2, ($3 == "" ? "(no reason written)" : $3), $4 }'
exit 1
