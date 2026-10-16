#!/bin/sh
# SessionStart hook of the Claude Monitor plugin (ADR-0007): installs the agent binary into the plugin's data directory when it is missing
# or older than the version this plugin pins. The download runs detached, so the session is not held up; every path ends in exit 0.
#
# Trust: the zip must match the SHA-256 pinned in agent-release.txt, which came with the plugin (from git over https). The server cannot
# substitute a build. Later updates keep ADR-0004's signed manifest, which the agent verifies itself.
data="${CLAUDE_PLUGIN_DATA:-}"
root="${CLAUDE_PLUGIN_ROOT:-}"
{ [ -n "$data" ] && [ -n "$root" ]; } || exit 0
pins="$root/agent-release.txt"
server="${CLAUDE_PLUGIN_OPTION_SERVER:-}"
server="${server%/}"
msg=""
mkdir -p "$data/bin" 2>/dev/null || exit 0

say() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >> "$data/install.log"; }
finish() { [ -n "$msg" ] && printf '{"systemMessage":"%s"}\n' "$msg"; exit 0; }

# The old local plugin (cm-agent install) would report every event a second time.
if grep -q '"monitor-agent@monitor-agent-local": *true' "${CLAUDE_CONFIG_DIR:-$HOME/.claude}/settings.json" 2>/dev/null; then
  msg="Claude Monitor: the old local plugin is still enabled and would report every event twice. Run /monitor-agent:connect, which removes it."
fi

case "$(uname -s)" in
  Darwin) os=macos ;;
  Linux) os=linux ;;
  MINGW* | MSYS* | CYGWIN*) os=windows ;;
  *) finish ;;
esac
exe=cm-agent
[ "$os" = windows ] && exe=cm-agent.exe
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
pin="$(grep "^$os-$arch " "$pins" 2>/dev/null | head -n 1 | tr -d '\r')"
sha="$(printf '%s' "$pin" | awk '{print $2}')"
file="$(printf '%s' "$pin" | awk '{print $3}')"
installed=""
[ -x "$bin" ] && installed="$("$bin" version 2>/dev/null | head -n 1 | tr -d '\r ')"

# Install when there is no binary, or the pinned version is newer than the installed one (never downgrade: the agent may have updated itself).
newer() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]; }
if [ -n "$installed" ] && { [ -z "$pinned" ] || ! newer "$pinned" "$installed"; }; then finish; fi

if [ -z "$sha" ] || [ -z "$file" ]; then
  say "no build is pinned for $os-$arch in this plugin (version ${pinned:-unknown}); nothing was installed"
  msg="${msg:+$msg }Claude Monitor: this plugin has no published build for $os-$arch yet. /monitor-agent:status has the details."
  finish
fi
case "$file" in cm-agent-*-*.zip) ;; *) say "refused: $file is not a build file name"; finish ;; esac
case "$server" in
  https://* | http://localhost* | http://127.0.0.1*) ;;
  *) say "no https server address is set for this plugin; nothing was installed"
     msg="${msg:+$msg }Claude Monitor: set the server address in the plugin's options (/plugin, Claude Monitor agent, Configure options)."
     finish ;;
esac

lock="$data/install.lock"
if [ -d "$lock" ] && [ -n "$(find "$lock" -maxdepth 0 -mmin +15 2>/dev/null)" ]; then rmdir "$lock" 2>/dev/null; fi
mkdir "$lock" 2>/dev/null || finish # another session is already installing

sha256() { if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi; }
(
  trap 'rmdir "$lock" 2>/dev/null; rm -rf "$data/download.$$"' EXIT
  tmp="$data/download.$$"
  mkdir -p "$tmp/x" || exit 0
  say "installing version $pinned for $os-$arch from the server"
  curl -fsSL --max-time 300 --max-filesize 262144000 -o "$tmp/$file" "$server/downloads/$file" || { say "download failed"; exit 0; }
  [ "$(sha256 "$tmp/$file")" = "$sha" ] || { say "refused: the download does not match the pinned SHA-256"; exit 0; }
  if command -v unzip > /dev/null 2>&1; then unzip -o -q "$tmp/$file" -d "$tmp/x" || { say "unzip failed"; exit 0; }
  else tar -xf "$tmp/$file" -C "$tmp/x" || { say "extract failed"; exit 0; }; fi
  [ -f "$tmp/x/$exe" ] || { say "the download holds no $exe"; exit 0; }
  chmod +x "$tmp/x/$exe"
  mv -f "$tmp/x/$exe" "$bin" || { say "could not replace the installed binary (is it running?)"; exit 0; }
  say "installed version $pinned"
  CM_BINARY="$bin" "$bin" restart > /dev/null 2>&1 || true # a daemon of the older version hands over to the new one
) < /dev/null >> "$data/install.log" 2>&1 &

msg="${msg:+$msg }Claude Monitor: installing the agent (version ${pinned:-?}) in the background. /monitor-agent:status shows how it went."
finish
