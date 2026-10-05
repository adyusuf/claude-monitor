#!/usr/bin/env bash
# One release package for the Windows server (ADR-0002 phase 4): out/claude-monitor-<commit>.zip with
#   api/   the API, self-contained for win-x64, with the web.config IIS needs
#   web/   the built web app, and web/downloads/ the agent zips (from scripts/build-agent.sh, when present)
#   release.json   {"commit": "<sha>"}, which deploy.ps1 checks against /api/version after the switch
# The package holds no secret: deploy.ps1 adds the environment's values on the server.
#
#   bash scripts/build-release.sh            # from a clean checkout of the commit to ship
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "$root"
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
  echo "the working tree has uncommitted changes: a release is built from a commit" >&2
  exit 2
fi
# A server-only settings file may hold the SMTP password (ApiConfig.SmtpSection). It is git-ignored, so the check
# above does not see it, and `dotnet publish` would copy it into a package that is meant to hold no secret.
local_settings="$(find src -name 'appsettings.Production*.json' -not -path '*/bin/*' -not -path '*/obj/*' 2>/dev/null)"
if [ -n "$local_settings" ]; then
  echo "refusing: $local_settings would ship in the package — server-only settings live on the server" >&2
  exit 2
fi
commit="$(git rev-parse HEAD)"
out="$root/out"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

echo "▶ web"
npm --prefix web ci --silent
npm --prefix web run build >/dev/null
cp -R web/dist "$stage/web"
if [ -d "$out/downloads" ]; then
  mkdir -p "$stage/web/downloads"
  cp "$out/downloads/"* "$stage/web/downloads/"
fi

echo "▶ api (win-x64)"
dotnet publish src/ClaudeMonitor.Api -c Release -r win-x64 --self-contained true -o "$stage/api" --nologo -v q \
  -p:SourceRevisionId="$commit"

printf '{"commit":"%s"}\n' "$commit" > "$stage/release.json"
mkdir -p "$out"
rm -f "$out/claude-monitor-$commit.zip"
(cd "$stage" && zip -q -r -X "$out/claude-monitor-$commit.zip" api web release.json)
echo "✓ $out/claude-monitor-$commit.zip"
