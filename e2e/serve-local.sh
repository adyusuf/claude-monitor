#!/usr/bin/env bash
# The local stand-in for the test environment (global #33, "no test environment" fallback): starts the e2e
# services, builds the web app and runs the API on E2E_PORT serving it, with production settings except TLS.
# Playwright starts this through webServer and stops it at the end.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
port="${E2E_PORT:-5190}"
docker compose -f "$root/e2e/services.yml" up -d --wait >/dev/null
npm --prefix "$root/web" ci --silent >/dev/null 2>&1 || true
npm --prefix "$root/web" run build >/dev/null
export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_URLS="http://127.0.0.1:${port}"
export MONITOR_DB="Host=127.0.0.1;Port=55433;Database=monitor_e2e;Username=monitor;Password=monitor"
export MONITOR_PUBLIC_ORIGIN="http://localhost:${port}"
export MONITOR_SMTP_HOST=127.0.0.1 MONITOR_SMTP_PORT=1125 MONITOR_SMTP_FROM=monitor-e2e@localhost MONITOR_SMTP_TLS=false
export MONITOR_ARCHIVE_DIR="$(mktemp -d)"
export MONITOR_WEB_ROOT="$root/web/dist"
export MONITOR_COMMIT="$(git -C "$root" rev-parse HEAD)"
export MONITOR_AUTH_RATE_PER_MINUTE=1000
export MONITOR_MFA_KEY="$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=')"
dotnet build "$root/src/ClaudeMonitor.Api" -c Release --nologo -v q >/dev/null
dotnet run --project "$root/src/ClaudeMonitor.Api" -c Release --no-build --no-launch-profile -- --migrate
exec dotnet run --project "$root/src/ClaudeMonitor.Api" -c Release --no-build --no-launch-profile
