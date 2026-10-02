#!/usr/bin/env bash
# Known-vulnerability check for the desktop window's Rust crates (global rule #19, dependency CVE).
# The shared core (gate-core.sh) has no step for Rust, so scripts/merge-gate.conf runs this
# beside SAST; it moves into the core when the core learns Rust upstream.
#
# Threshold: a RUSTSEC vulnerability in desktop/Cargo.lock FAILS. Warnings (unmaintained, unsound,
# yanked) are printed and do not block - the Linux-only GTK crates Tauri pulls in are unmaintained.
# A vulnerability that cannot be fixed is ignored only in desktop/.cargo/audit.toml, with a reason.
#
# The advisory database is fetched over the network, so a transient failure is retried
# (AUDIT_FETCH_TRIES attempts, AUDIT_FETCH_PAUSE seconds apart) before the run is called NOT RUN.
#
# Exit codes: 0 clean or n/a · 1 a vulnerability · 3 NOT RUN (no cargo-audit, or the advisory
# database could not be read) - which blocks exactly like a failure: a gate that did not run did
# not pass (#19).
set -uo pipefail

root="$(git rev-parse --show-toplevel 2>/dev/null)" || { echo "not inside a git repository" >&2; exit 2; }
cd "$root" || exit 2
PATH="${CARGO_HOME:-$HOME/.cargo}/bin:$PATH"

echo "▶ Dependency CVE (cargo audit, desktop/Cargo.lock)"
if [ ! -f desktop/Cargo.lock ]; then
  echo "  n/a: no Rust here"
  exit 0
fi
if ! command -v cargo-audit >/dev/null 2>&1; then
  echo "  NOT RUN: cargo-audit is not installed (cargo install cargo-audit --locked)"
  exit 3
fi

tries="${AUDIT_FETCH_TRIES:-3}"
pause="${AUDIT_FETCH_PAUSE:-5}"
log="$(mktemp)"
trap 'rm -f "$log"' EXIT
attempt=1
while :; do
  (cd desktop && cargo audit --file Cargo.lock) >"$log" 2>&1
  rc=$?
  grep -q 'Scanning Cargo.lock for vulnerabilities' "$log" && break
  [ "$attempt" -ge "$tries" ] && break
  echo "  advisory database unreachable (attempt $attempt of $tries), retrying in ${pause}s"
  attempt=$((attempt + 1))
  sleep "$pause"
done
if ! grep -q 'Scanning Cargo.lock for vulnerabilities' "$log"; then
  tail -10 "$log" | sed 's/^/    /'
  echo "  NOT RUN: cargo audit gave no verdict (advisory database unreachable?)"
  exit 3
fi
warnings="$(grep -c '^Crate:' "$log")"
if [ "$rc" != 0 ]; then
  grep -E '^(Crate|Version|Title|ID|Solution):' "$log" | sed 's/^/    /' | head -40
  echo "  ✗ a known vulnerability - the merge is blocked (#19)"
  exit 1
fi
echo "  ✓ no known vulnerability ($warnings advisory warning(s) reported, not blocking)"
