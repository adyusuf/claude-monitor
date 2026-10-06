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
# Windows and Linux builds are not signed (ADR-0002, open item: the signing route; ADR-0005): an admin verifies the
# SHA-256 in SHA256SUMS before a service upgrade (docs/release.md). Single file, never compressed (see the csproj).
#
# Self-update (ADR-0004): AGENT_UPDATE_CHANNEL=test|prod builds the channel's public key (deploy/update-keys/<channel>.pub)
# into the agent and signs a manifest.json for the zips with the channel's PRIVATE key, which lives only in the
# maintainer's keychain (scripts/sign_manifest.py keygen). Without a channel the agent is built without a key, cannot
# update itself, and no manifest is written.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
out="${1:-$root/out/downloads}"
project="$root/src/ClaudeMonitor.Agent"
entitlements="$root/deploy/agent.entitlements"
mkdir -p "$out"
channel="${AGENT_UPDATE_CHANNEL:-}"
channel_args=()
if [ -n "$channel" ]; then
  keyfile="$root/deploy/update-keys/$channel.pub"
  [ -s "$keyfile" ] || { echo "no public key for channel '$channel': $keyfile (scripts/sign_manifest.py keygen --channel $channel)" >&2; exit 1; }
  channel_args=("-p:UpdateChannel=$channel" "-p:UpdatePublicKey=$(tr -d '[:space:]' < "$keyfile")")
fi
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for rid in osx-arm64 osx-x64 win-x64 win-arm64 linux-x64 linux-arm64; do
  echo "▶ $rid"
  dotnet publish "$project" -c Release -r "$rid" -o "$work/$rid" --nologo -v q ${channel_args[@]+"${channel_args[@]}"}
  case "$rid" in
    osx-arm64) name="cm-agent-macos-arm64" ;;
    osx-x64) name="cm-agent-macos-x64" ;;
    win-x64) name="cm-agent-windows-x64" ;;
    win-arm64) name="cm-agent-windows-arm64" ;;
    linux-x64) name="cm-agent-linux-x64" ;;
    linux-arm64) name="cm-agent-linux-arm64" ;;
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
rm -f "$out/manifest.json"
if [ -n "$channel" ]; then
  version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$project/ClaudeMonitor.Agent.csproj" | head -1)"
  python3 "$root/scripts/sign_manifest.py" sign --channel "$channel" --downloads "$out" --version "$version" \
    --min-supported "${AGENT_MIN_SUPPORTED:-$version}"
else
  echo "! no AGENT_UPDATE_CHANNEL: built without an update key and without manifest.json (these agents cannot self-update)"
fi
echo "✓ $(find "$out" -type f | wc -l | tr -d ' ') files in $out"
