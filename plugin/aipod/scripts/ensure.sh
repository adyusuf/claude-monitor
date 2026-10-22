#!/bin/sh
# SessionStart hook of the aipod plugin (ADR-0007): installs the agent binary into the plugin's data directory when it is missing
# or older than the version this plugin pins. The download runs detached, so the session is not held up; every path ends in exit 0.
#
# Trust: the zip must match a SHA-256 pinned in agent-release.txt, which came with the plugin (from git over https). The server cannot
# substitute a build. Later updates keep ADR-0004's signed manifest, which the agent verifies itself.
# shellcheck disable=SC2016  # the single-quoted PowerShell command below uses $env:ZIP and $env:DEST, which the shell must not expand
data="${CLAUDE_PLUGIN_DATA:-}"
root="${CLAUDE_PLUGIN_ROOT:-}"
{ [ -n "$data" ] && [ -n "$root" ]; } || exit 0
pins="$root/agent-release.txt"
server="${CLAUDE_PLUGIN_OPTION_SERVER:-}"
server="${server%/}"
# The option is not required: a machine that is already connected has a server in its identity file, which is asked instead (AP_AGENT_HOME, AP_AGENT_HOME, then the two default homes).
if [ -z "$server" ]; then
  for home in "${AP_AGENT_HOME:-}" "${AP_AGENT_HOME:-}" "$HOME/Library/Application Support/aipod" "$HOME/.aipod" "$HOME/Library/Application Support/Podium" "$HOME/.aipod"; do
    [ -n "$home" ] && [ -f "$home/agent.json" ] || continue
    server="$(sed -n 's/.*"server": *"\([^"]*\)".*/\1/p' "$home/agent.json" 2>/dev/null | head -n 1)"
    server="${server%/}"
    [ -n "$server" ] && break
  done
fi
msg=""
mkdir -p "$data/bin" 2>/dev/null || exit 0

say() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >> "$data/install.log"; }
finish() { [ -n "$msg" ] && printf '{"systemMessage":"%s"}\n' "$msg"; exit 0; }
# The log is for `status`: keep the last lines only.
if [ -f "$data/install.log" ] && [ "$(wc -c < "$data/install.log")" -gt 200000 ]; then
  tail -n 200 "$data/install.log" > "$data/install.log.tmp" && mv -f "$data/install.log.tmp" "$data/install.log"
fi

# The server address: https, or http on this machine. Only characters a URL needs, so it can be put in a command line as it is.
valid_server() { case "$1" in *"
"*) return 1 ;; esac; printf '%s' "$1" | grep -Eq '^(https://[A-Za-z0-9._:/@%~+-]+|http://(localhost|127\.0\.0\.1)(:[0-9]+)?(/[A-Za-z0-9._/%~+-]*)?)$'; }
# Kept for `status` and `connect` run from a session's Bash tool, which has no plugin options in its environment.
if valid_server "$server"; then printf '%s\n' "$server" > "$data/server.txt"; fi

case "$(uname -s)" in
  Darwin) os=macos ;;
  Linux) os=linux ;;
  MINGW* | MSYS* | CYGWIN*) os=windows ;;
  *) finish ;;
esac
exe=aipod
[ "$os" = windows ] && exe=aipod.exe
machine="$(uname -m)"
[ "$os" = windows ] && machine="${PROCESSOR_ARCHITEW6432:-${PROCESSOR_ARCHITECTURE:-$machine}}"
if [ "$os" = macos ] && [ "$machine" = x86_64 ] && [ "$(sysctl -n sysctl.proc_translated 2>/dev/null)" = 1 ]; then machine=arm64; fi
case "$machine" in
  arm64 | ARM64 | aarch64) arch=arm64 ;;
  x86_64 | AMD64 | amd64 | x64) arch=x64 ;;
  *) finish ;;
esac

bin="$data/bin/$exe"
pinned="$(sed -n '1p' "$pins" 2>/dev/null | tr -d '\r ')"
pin_lines="$(grep "^$os-$arch " "$pins" 2>/dev/null | tr -d '\r')"
file="$(printf '%s\n' "$pin_lines" | head -n 1 | awk '{print $3}')"
shas="$(printf '%s\n' "$pin_lines" | awk -v f="$file" '$3 == f {print $2}')" # one per build of this version (each environment has its own)
failure_key="$pinned $(printf '%s' "$shas" | tr '\n' ' ')" # a different pin of the same version is a new chance
installed=""
[ -x "$bin" ] && installed="$("$bin" version 2>/dev/null | head -n 1 | tr -d '\r ')"

# Install when there is no binary, or the pinned version is newer than the installed one (never downgrade: the agent may have updated itself).
newer() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]; }
if [ -n "$installed" ] && { [ -z "$pinned" ] || ! newer "$pinned" "$installed"; }; then finish; fi

if [ -z "$shas" ] || [ -z "$file" ]; then
  say "no build is pinned for $os-$arch in this plugin (version ${pinned:-unknown}); nothing was installed"
  msg="${msg:+$msg }aipod: this plugin has no published build for $os-$arch yet. /aipod:status has the details."
  finish
fi
printf '%s' "$file" | grep -Eq '^aipod-(macos|windows|linux)-(arm64|x64)\.zip$' || { say "refused: $file is not a build file name"; finish; }
if ! valid_server "$server"; then
  say "no https server address is set for this plugin; nothing was installed"
  msg="${msg:+$msg }aipod: set the server address in the plugin's options (/plugin, aipod agent, Configure options)."
  finish
fi
if ! command -v curl > /dev/null 2>&1; then
  say "curl is needed to download the agent and was not found"
  msg="${msg:+$msg }aipod: curl is needed to install the agent and was not found."
  finish
fi
# A failed install of this version is not tried again at every session start: a download of up to 250 MB each time would be worse than the failure.
if [ -f "$data/install.failed" ] && [ "$(cat "$data/install.failed" 2> /dev/null)" = "$failure_key" ] && [ -n "$(find "$data/install.failed" -mmin -360 2> /dev/null)" ]; then
  msg="${msg:+$msg }aipod: the last install of version $pinned failed; it is tried again after a few hours. /aipod:status has the details."
  finish
fi

lock="$data/install.lock"
if [ -d "$lock" ] && [ -n "$(find "$lock" -maxdepth 0 -mmin +15 2> /dev/null)" ]; then rmdir "$lock" 2> /dev/null; fi
mkdir "$lock" 2> /dev/null || finish # another session is already installing

sha256() { if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi; }
(
  trap 'rmdir "$lock" 2>/dev/null; rm -rf "$data/download.$$"' EXIT
  tmp="$data/download.$$"
  fail() { say "$1"; printf '%s\n' "$failure_key" > "$data/install.failed"; exit 0; }
  mkdir -p "$tmp/x" || exit 0
  say "installing version $pinned for $os-$arch from the server"
  curl -fsSL --max-time 300 --max-filesize 262144000 -o "$tmp/$file" "$server/downloads/$file" || fail "download failed"
  printf '%s\n' "$shas" | grep -qx "$(sha256 "$tmp/$file")" || fail "refused: the download does not match the pinned SHA-256"
  if command -v unzip > /dev/null 2>&1; then unzip -o -q "$tmp/$file" -d "$tmp/x" || fail "unzip failed"
  elif [ "$os" = windows ] && command -v powershell.exe > /dev/null 2>&1; then
    ZIP="$(cygpath -w "$tmp/$file")" DEST="$(cygpath -w "$tmp/x")" powershell.exe -NoProfile -NonInteractive -Command 'Expand-Archive -LiteralPath $env:ZIP -DestinationPath $env:DEST -Force' || fail "Expand-Archive failed"
  elif command -v bsdtar > /dev/null 2>&1; then bsdtar -xf "$tmp/$file" -C "$tmp/x" || fail "bsdtar failed"
  elif command -v python3 > /dev/null 2>&1; then python3 -m zipfile -e "$tmp/$file" "$tmp/x" || fail "python zipfile failed"
  else fail "no tool to unzip the download (unzip, bsdtar or python3 is needed)"; fi
  [ -f "$tmp/x/$exe" ] || fail "the download holds no $exe"
  chmod +x "$tmp/x/$exe"
  # Windows cannot overwrite a running program but can rename it: the running daemon keeps its old file until it is restarted below.
  if [ "$os" = windows ] && [ -f "$bin" ]; then rm -f "$bin.old" 2> /dev/null; mv -f "$bin" "$bin.old" 2> /dev/null; fi
  if ! mv -f "$tmp/x/$exe" "$bin"; then
    [ -f "$bin" ] || { [ -f "$bin.old" ] && mv -f "$bin.old" "$bin"; } # never leave the machine with no binary at all
    fail "could not replace the installed binary"
  fi
  rm -f "$data/install.failed"
  say "installed version $pinned"
  # A daemon of the older version hands over to the new one. Through the wrapper, so that it has what every other start has: the plugin id,
  # the server and the push option (without them the new daemon would not notice the plugin being switched off, or would stop pushing).
  sh "$root/scripts/run.sh" restart > /dev/null 2>&1 || true
) < /dev/null >> "$data/install.log" 2>&1 &

msg="${msg:+$msg }aipod: installing the agent (version ${pinned:-?}) in the background. /aipod:status shows how it went."
finish
