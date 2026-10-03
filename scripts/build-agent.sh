#!/usr/bin/env bash
# Builds the agent for every platform and packs one zip each into an output folder (default: out/downloads), which
# the deploy copies into the web root's /downloads (ADR-0002 phase 4).
#
#   bash scripts/build-agent.sh [out-dir]
#
# macOS builds are signed with a Developer ID Application identity (hardened runtime) and notarised when the
# environment names them; nothing secret is passed here, only names that resolve in the maintainer's keychain:
#   AGENT_SIGN_IDENTITY="Developer ID Application: <name> (<team>)"   # skip signing when unset
#   AGENT_NOTARY_PROFILE=<notarytool keychain profile>               # skip notarisation when unset
# Windows builds are not signed yet (ADR-0002, open item: the signing route).
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
out="${1:-$root/out/downloads}"
project="$root/src/ClaudeMonitor.Agent"
entitlements="$root/deploy/agent.entitlements"
mkdir -p "$out"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for rid in osx-arm64 osx-x64 win-x64 win-arm64; do
  echo "▶ $rid"
  dotnet publish "$project" -c Release -r "$rid" -o "$work/$rid" --nologo -v q
  case "$rid" in
    osx-arm64) name="cm-agent-macos-arm64" ;;
    osx-x64) name="cm-agent-macos-x64" ;;
    win-x64) name="cm-agent-windows-x64" ;;
    win-arm64) name="cm-agent-windows-arm64" ;;
  esac
  binary="$work/$rid/cm-agent"
  [ -f "$binary.exe" ] && binary="$binary.exe"
  if [[ "$rid" == osx-* && -n "${AGENT_SIGN_IDENTITY:-}" ]]; then
    codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$AGENT_SIGN_IDENTITY" "$binary"
    codesign --verify --strict --verbose=2 "$binary"
  fi
  rm -f "$out/$name.zip"
  (cd "$work/$rid" && zip -q -X "$out/$name.zip" "$(basename "$binary")")
  if [[ "$rid" == osx-* && -n "${AGENT_SIGN_IDENTITY:-}" && -n "${AGENT_NOTARY_PROFILE:-}" ]]; then
    # A bare executable cannot be stapled; Gatekeeper finds the notarisation ticket online on first run.
    xcrun notarytool submit "$out/$name.zip" --keychain-profile "$AGENT_NOTARY_PROFILE" --wait
  fi
  shasum -a 256 "$out/$name.zip" | sed "s|$out/||" >> "$work/SHA256SUMS"
done

mv "$work/SHA256SUMS" "$out/SHA256SUMS"
echo "✓ $(find "$out" -type f | wc -l | tr -d ' ') files in $out"
