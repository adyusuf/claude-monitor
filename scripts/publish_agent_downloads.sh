#!/usr/bin/env bash
# Publishes a signed agent build to the download host (an R2 bucket behind a public domain), where the API's AIPOD_DOWNLOADS_URL points
# (docs/release.md, "Download host"). One folder per channel holds what the update check reads; every version is also kept under its own folder:
#
#   <prefix>/<channel>/                 the current build: the six zips, SHA256SUMS and manifest.json (overwritten each release), and the plugin
#                                       files when the build wrote them (AGENT_DOWNLOADS_URL: aipod-marketplace.json, aipod-plugin.zip), because the site no
#                                       longer carries any zip itself, and the installers (aipod-install.sh, aipod-install.ps1: README, "Install aipod")
#   <prefix>/<version>/<channel>/       the same files, kept
#
#   AGENT_R2_BUCKET=<bucket> bash scripts/publish_agent_downloads.sh <test|prod> [downloads-dir] [--dry-run]
#
# The downloads dir is the output of `AGENT_UPDATE_CHANNEL=<channel> bash scripts/build-agent.sh` (default out/downloads). Nothing secret is passed
# here: wrangler uses the maintainer's own login. The manifest goes up LAST, so it never names a file that is not there yet.
#   AGENT_R2_PREFIX   the top folder (default: daemons)
set -euo pipefail

channel="${1:-}"
case "$channel" in test|prod) ;; *) echo "usage: AGENT_R2_BUCKET=<bucket> $0 <test|prod> [downloads-dir] [--dry-run]" >&2; exit 2 ;; esac
shift
dir=""
dry=0
for arg in "$@"; do
  if [ "$arg" = "--dry-run" ]; then dry=1; else dir="$arg"; fi
done
root="$(git rev-parse --show-toplevel)"
dir="${dir:-$root/out/downloads}"
bucket="${AGENT_R2_BUCKET:-}"
prefix="${AGENT_R2_PREFIX:-daemons}"
[ -n "$bucket" ] || { echo "AGENT_R2_BUCKET is not set" >&2; exit 2; }
[ -f "$dir/manifest.json" ] || { echo "no signed manifest.json in $dir (build with AGENT_UPDATE_CHANNEL=$channel)" >&2; exit 2; }
(cd "$dir" && shasum -a 256 -c SHA256SUMS > /dev/null) || { echo "SHA256SUMS does not match the files in $dir" >&2; exit 2; }

read -r manifest_channel version < <(python3 -c '
import json, sys
m = json.load(open(sys.argv[1]))
channels = {e["channel"] for e in m["entries"]}
versions = {e["version"] for e in m["entries"]}
if len(channels) != 1 or len(versions) != 1:
    sys.exit("the manifest must hold one channel and one version, found %s / %s" % (sorted(channels), sorted(versions)))
print(channels.pop(), versions.pop())
' "$dir/manifest.json")
[ "$manifest_channel" = "$channel" ] || { echo "the manifest is signed for '$manifest_channel', not '$channel'" >&2; exit 2; }

put() { # <key> <file> <content-type> <cache-control>
  if [ "$dry" = 1 ]; then echo "put $bucket/$1 <- $(basename "$2")"; return; fi
  wrangler r2 object put "$bucket/$1" --file "$2" --content-type "$3" --cache-control "$4" --remote > /dev/null
  echo "uploaded $1"
}
upload() { # <folder> <cache-control>
  local file
  # The agent builds only: "aipod-*.zip" would also take the plugin zip (aipod-plugin.zip), which goes up on its own below.
  for file in "$dir"/aipod-macos-*.zip "$dir"/aipod-windows-*.zip "$dir"/aipod-linux-*.zip; do
    if [ -f "$file" ]; then put "$1/$(basename "$file")" "$file" application/zip "$2"; fi
  done
  if [ -f "$dir/aipod-plugin.zip" ]; then put "$1/aipod-plugin.zip" "$dir/aipod-plugin.zip" application/zip "$2"; fi
  if [ -f "$dir/aipod-marketplace.json" ]; then put "$1/aipod-marketplace.json" "$dir/aipod-marketplace.json" application/json "$2"; fi
  for file in aipod-install.sh aipod-install.ps1; do # the installers (README), when the build copied them
    if [ -f "$dir/$file" ]; then put "$1/$file" "$dir/$file" "text/plain; charset=utf-8" "$2"; fi
  done
  put "$1/SHA256SUMS" "$dir/SHA256SUMS" text/plain "$2"
  put "$1/manifest.json" "$dir/manifest.json" application/json "$2"
}

upload "$prefix/$version/$channel" "public, max-age=31536000, immutable"
upload "$prefix/$channel" "no-cache"
echo "✓ $channel $version published to $bucket/$prefix/$channel (and kept under $prefix/$version/$channel)"
