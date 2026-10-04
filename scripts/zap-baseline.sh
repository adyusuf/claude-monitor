#!/usr/bin/env bash
# OWASP ZAP baseline scan (global #19: "OWASP ZAP baseline after the test deploy"), run by merge-gate.sh at the
# test -> prod gate. Against E2E_BASE_URL (the test environment) when it is set; otherwise against the local stack
# e2e/serve-local.sh starts (the same fallback the e2e suite uses).
#
# Exit codes: 0 no FAIL-level alert (warnings are printed) · 1 a FAIL-level alert · 3 NOT RUN (no Docker, or the
# target never answered), which blocks exactly like a failure.
# Rule levels: deploy/zap-rules.tsv (a rule is raised to FAIL or IGNOREd there, with the reason beside it).
set -uo pipefail

root="$(git rev-parse --show-toplevel)"
image="${ZAP_IMAGE:-ghcr.io/zaproxy/zaproxy:stable}"
report_dir="$root/out/zap"
mkdir -p "$report_dir"
echo "▶ OWASP ZAP baseline"
if ! docker info >/dev/null 2>&1; then
  echo "  NOT RUN: Docker is not running"
  exit 3
fi

server_pid=""
port="${E2E_PORT:-5190}"
# shellcheck disable=SC2329  # called by the trap below
cleanup() {
  if [ -n "$server_pid" ]; then
    kill "$server_pid" 2>/dev/null
    lsof -t -nP -iTCP:"$port" -sTCP:LISTEN 2>/dev/null | xargs kill 2>/dev/null
  fi
  true
}
trap cleanup EXIT
if [ -n "${E2E_BASE_URL:-}" ]; then
  target="${E2E_BASE_URL%/}"
else
  bash "$root/e2e/serve-local.sh" >"$report_dir/server.log" 2>&1 &
  server_pid=$!
  for _ in $(seq 1 100); do curl -fsS -m 2 "http://localhost:$port/api/version" >/dev/null 2>&1 && break; sleep 3; done
  if ! curl -fsS -m 2 "http://localhost:$port/api/version" >/dev/null 2>&1; then
    echo "  NOT RUN: the local stack did not answer (see $report_dir/server.log)"
    exit 3
  fi
  target="http://host.docker.internal:$port"
fi

echo "  target: $target"
cp "$root/deploy/zap-rules.tsv" "$report_dir/rules.tsv"
docker run --rm -v "$report_dir:/zap/wrk:rw" --add-host=host.docker.internal:host-gateway "$image" \
  zap-baseline.py -t "$target" -c rules.tsv -r report.html -J report.json -m 2 >"$report_dir/zap.log" 2>&1
code=$?
grep -E '^(FAIL|WARN)-?[A-Z]*:' "$report_dir/zap.log" | sed 's/^/    /' | head -40
grep -E '^FAIL-NEW|^WARN-NEW|^PASS' "$report_dir/zap.log" | tail -1 | sed 's/^/  /'
case "$code" in
  0) echo "  ✓ no alert" ;;
  2) echo "  ✓ warnings only (report: out/zap/report.html)" ;;
  1) echo "  ✗ FAIL-level alert(s) — the promotion is blocked (#19); report: out/zap/report.html"; exit 1 ;;
  *) echo "  NOT RUN: ZAP ended with $code (see out/zap/zap.log)"; exit 3 ;;
esac
exit 0
