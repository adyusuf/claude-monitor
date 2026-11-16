#!/usr/bin/env bash
# Assembles the two installers that the download host serves, each as ONE file, from their step files:
#
#   bash scripts/install/assemble.sh <out-dir>     # writes <out-dir>/aipod-install.sh and <out-dir>/aipod-install.ps1
#
# The installers are fetched as a single file (README, "Install aipod": `curl ... | sh` and a downloaded .ps1), so a script that
# sources a sibling file would break there. The source is therefore split by step (global rule #9) into sh/*.part and ps1/*.part, and
# this script joins them, in file-name order, with nothing added or removed: the output is the parts one after the other, byte for byte.
# A step file is not a script of its own (the PowerShell try block opens in ps1/60-download-host.part and closes in ps1/99-finish.part),
# which is why the parts do not end in .sh or .ps1. scripts/build-agent.sh calls this to put the installers beside the zips, and the
# installer tests run the assembled files, never the parts.
set -euo pipefail
export LC_ALL=C # the parts are joined in plain name order, whatever the locale

here="$(cd "$(dirname "$0")" && pwd)"
out="${1:-}"
[ -n "$out" ] || { echo "usage: assemble.sh <out-dir>" >&2; exit 2; }
mkdir -p "$out"

assemble() { # <parts folder> <served file name> <mode>
  local parts=("$here/$1"/*.part) tmp
  [ -f "${parts[0]}" ] || { echo "assemble: no step files in $here/$1" >&2; exit 1; }
  tmp="$(mktemp "$out/.assemble.XXXXXX")"
  cat "${parts[@]}" > "$tmp"
  chmod "$3" "$tmp"
  mv -f "$tmp" "$out/$2"
}

assemble sh aipod-install.sh 755
assemble ps1 aipod-install.ps1 644
