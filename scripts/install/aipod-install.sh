#!/bin/sh
# Installs or updates the aipod agent and its Claude Code plugin on macOS and Linux, and removes an older agent (cm-agent) on request.
#
#   aipod-install.sh [--server <https address>] [--from <download base>] [--yes] [--dry-run]
#                    [--no-plugin] [--no-login] [--remove-old] [--purge]
#
# In order:
#   1. looks at the download host (the server's /downloads, which is the R2 bucket; --from names another base) for the newest signed build;
#   2. finds an older agent (cm-agent, the old Claude Code plugin, an old autostart) and asks before removing it; "no" changes nothing;
#   3. carries the old settings and login over (agent.json and the two tokens), so the machine stays the same machine on the web;
#   4. installs the build into the agent home, checked against the manifest's SHA-256 (and, on macOS, its code signature);
#   5. adds or updates the Claude Code plugin (aipod@aipod-releases); when the machine is not connected yet, logs in (a code to approve on the web).
# --remove-old only removes the old agent (the removal script). --purge also deletes the old data folder and the old keychain entries,
# after the carry-over. Tokens are copied between the user's own keychain entries or files and are never printed. Nothing here needs root;
# an old boot service (service mode) needs an administrator and is only named, with the command to run.
set -u

server="${AIPOD_SERVER:-}"
from=""
yes=0
dry=0
want_plugin=1
want_login=1
only_remove=0
purge=0

usage() { sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'; }
say() { printf '%s\n' "$*"; }
warn() { printf '%s\n' "$*" >&2; }
die() { warn "aipod install: $*"; exit 1; }
did() { [ "$dry" = 1 ] || say "$*"; } # a message about something that happened: silent in a dry run, where act already said what would

while [ $# -gt 0 ]; do
  case "$1" in
    --server) [ $# -ge 2 ] || die "--server needs an address"; server="$2"; shift ;;
    --from) [ $# -ge 2 ] || die "--from needs an address"; from="$2"; shift ;;
    --yes | -y) yes=1 ;;
    --dry-run) dry=1 ;;
    --no-plugin) want_plugin=0 ;;
    --no-login) want_login=0 ;;
    --remove-old) only_remove=1 ;;
    --purge) purge=1 ;;
    -h | --help) usage; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
  shift
done

case "$(uname -s)" in
  Darwin) os=macos ;;
  Linux) os=linux ;;
  *) die "this script is for macOS and Linux; on Windows run aipod-install.ps1" ;;
esac
machine="$(uname -m)"
if [ "$os" = macos ] && [ "$machine" = x86_64 ] && [ "$(sysctl -n sysctl.proc_translated 2> /dev/null)" = 1 ]; then machine=arm64; fi
case "$machine" in
  arm64 | aarch64) arch=arm64 ;;
  x86_64 | amd64) arch=x64 ;;
  *) die "no build for the CPU $machine" ;;
esac
[ -n "${HOME:-}" ] || die "HOME is not set"

if [ "$os" = macos ]; then
  new_home="$HOME/Library/Application Support/aipod"
  old_home="$HOME/Library/Application Support/ClaudeMonitor"
else
  new_home="$HOME/.aipod"
  old_home="$HOME/.claude-monitor"
fi
[ -n "${AP_AGENT_HOME:-}" ] && new_home="$AP_AGENT_HOME"
[ -n "${CM_AGENT_HOME:-}" ] && old_home="$CM_AGENT_HOME"
claude_dir="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
new_bin="$new_home/bin/aipod"
old_service=claude-monitor-agent # the keychain service the old agent kept its two tokens under
new_service=aipod
accounts="access-token refresh-token"

act() { # <description> <command...>: runs the command, or only says so in a dry run
  _what="$1"
  shift
  if [ "$dry" = 1 ]; then say "  [dry run] $_what"; return 0; fi
  "$@" > /dev/null 2>&1
}

can_prompt() { ( : < /dev/tty ) 2> /dev/null; }
confirm() { # <question> <default y|n>: --yes answers yes; with no terminal the default stands
  [ "$yes" = 1 ] && return 0
  if ! can_prompt; then
    warn "$1 (no terminal to ask on: answered '$2'; use --yes to answer yes)"
    [ "$2" = y ]
    return
  fi
  if [ "$2" = y ]; then hint="[Y/n]"; else hint="[y/N]"; fi
  printf '%s %s ' "$1" "$hint" > /dev/tty
  read -r answer < /dev/tty || answer=""
  case "$answer" in y | Y | yes | YES) return 0 ;; n | N | no | NO) return 1 ;; *) [ "$2" = y ] ;; esac
}

json_field() { sed -n "s/.*\"$1\": *\"\([^\"]*\)\".*/\1/p" | head -n 1; }
newer() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]; }
sha256() { if command -v sha256sum > /dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'; else shasum -a 256 "$1" | awk '{print $1}'; fi; }
valid_url() { printf '%s' "$1" | grep -Eq '^(https://[A-Za-z0-9._:/@%~+-]+|file:///[A-Za-z0-9._/%~+ -]+|http://(localhost|127\.0\.0\.1)(:[0-9]+)?(/[A-Za-z0-9._/%~+-]*)?)$'; }
have_claude() { command -v claude > /dev/null 2>&1; }

# ---------------------------------------------------------------- finding an older agent

old_binaries() {
  for f in "$old_home/bin/cm-agent" "$new_home/bin/cm-agent" "$claude_dir"/plugins/data/monitor-agent-*/bin/cm-agent "$claude_dir"/plugins/data/aipod-*/bin/cm-agent; do
    [ -f "$f" ] && printf '%s\n' "$f"
  done
}
old_plugin_ids() { have_claude && claude plugin list --json 2> /dev/null | sed -n 's/.*"id": *"\(monitor-agent@[^"]*\)".*/\1/p'; }
old_marketplaces() {
  have_claude || return 0
  claude plugin marketplace list --json 2> /dev/null | sed -n 's/^ *"name": *"\(monitor-agent-[a-z]*\)".*/\1/p'
}
# Only daemons run from one of the old binaries found here: never another user's, never a copy somewhere else.
old_daemon_pids() {
  old_binaries | while IFS= read -r f; do
    # shellcheck disable=SC2009  # pgrep -f cannot match a path with spaces reliably
    ps -axo pid=,command= 2> /dev/null | grep -F -- "$f daemon" | grep -v -E '^ *[0-9]+ +(grep|awk) ' | awk '{print $1}'
  done
}
old_autostart() {
  for f in "$HOME/Library/LaunchAgents/com.claudemonitor.agent.user.plist" "$HOME/.config/systemd/user/claude-monitor-agent.service"; do
    [ -f "$f" ] && printf '%s\n' "$f"
  done
}
old_boot_service() {
  for f in /Library/LaunchDaemons/com.claudemonitor.agent.plist /etc/systemd/system/cm-agent.service; do
    [ -f "$f" ] && printf '%s\n' "$f"
  done
}
old_binary_version() {
  first="$(old_binaries | head -n 1)"
  [ -n "$first" ] && "$first" version 2> /dev/null | head -n 1 | tr -d '\r '
}
found_old() { [ -n "$(old_binaries)$(old_plugin_ids)$(old_marketplaces)$(old_autostart)$(old_daemon_pids)" ]; }
list_old() {
  old_binaries | sed 's/^/  binary: /'
  old_plugin_ids | sed 's/^/  Claude Code plugin: /'
  old_marketplaces | sed 's/^/  marketplace: /'
  old_autostart | sed 's/^/  autostart: /'
  old_daemon_pids | sed 's/^/  running daemon, pid /'
}

# ---------------------------------------------------------------- carrying the settings and the login over

identity_home() { # the folder whose agent.json is the one to keep: the new home when it has one, else the old home
  for h in "$new_home" "$old_home"; do
    [ -f "$h/agent.json" ] && { printf '%s\n' "$h"; return; }
  done
}

copy_token() { # <account>
  if [ "$os" = macos ]; then
    command -v security > /dev/null 2>&1 || return 0
    security find-generic-password -s "$new_service" -a "$1" > /dev/null 2>&1 && return 0 # one already there is never overwritten
    secret="$(security find-generic-password -s "$old_service" -a "$1" -w 2> /dev/null)" || return 0
    [ -n "$secret" ] || return 0
    case "$secret" in *'"'* | *\\*) warn "  the $1 token has a character this script will not pass on; log in again"; secret=""; return 0 ;; esac
    # Through the interactive mode on standard input, so the token is never on a command line.
    if printf 'add-generic-password -U -s %s -a %s -w "%s"\n' "$new_service" "$1" "$secret" | security -i > /dev/null 2>&1; then
      say "  carried the $1 over to the keychain"
    fi
    secret=""
  else
    [ -f "$old_home/cred-$1" ] && [ ! -e "$new_home/cred-$1" ] || return 0
    cp -p "$old_home/cred-$1" "$new_home/cred-$1" && chmod 600 "$new_home/cred-$1" && say "  carried the $1 over"
  fi
}

carry_over() {
  src="$(identity_home)"
  [ -n "$src" ] || return 0
  say "Carrying the old settings and login over from $src"
  if [ "$dry" = 1 ]; then say "  [dry run] agent.json and the tokens would be copied into $new_home"; return 0; fi
  mkdir -p "$new_home" && chmod 700 "$new_home"
  if [ "$src" != "$new_home" ] && [ ! -f "$new_home/agent.json" ]; then
    cp -p "$src/agent.json" "$new_home/agent.json" && chmod 600 "$new_home/agent.json" && say "  copied agent.json (server, machine identity, saved settings)"
  fi
  for a in $accounts; do copy_token "$a"; done
}

# ---------------------------------------------------------------- removing an older agent

stop_old_daemons() {
  [ -n "$(old_daemon_pids)" ] || return 0
  for h in "$old_home" "$new_home"; do
    [ -f "$h/daemon.pid" ] && printf '%s' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$h/daemon.stop" # the daemon's own way to stop: it reads this file every few seconds
  done
  n=0
  while [ "$n" -lt 12 ] && [ -n "$(old_daemon_pids)" ]; do
    sleep 1
    n=$((n + 1))
  done
  for pid in $(old_daemon_pids); do kill -TERM "$pid" 2> /dev/null; done
  sleep 1
  for pid in $(old_daemon_pids); do kill -KILL "$pid" 2> /dev/null; done
  rm -f "$old_home/daemon.stop" "$new_home/daemon.stop"
}

remove_old() {
  say "Removing the old agent"
  first="$(old_binaries | head -n 1)"
  if [ -n "$(old_boot_service)" ]; then
    warn "  an old boot service is installed ($(old_boot_service | head -n 1)); that needs an administrator:"
    warn "    sudo '${first:-cm-agent}' uninstall --service"
  fi
  if [ -n "$(old_autostart)" ]; then
    if [ -n "$first" ]; then
      act "run: cm-agent uninstall --standalone" "$first" uninstall --standalone && did "  removed the old autostart"
    else
      warn "  an old autostart is still registered ($(old_autostart | head -n 1)) and no old binary is left to remove it with"
    fi
  fi
  if have_claude; then
    for id in $(old_plugin_ids); do
      act "claude plugin uninstall $id" claude plugin uninstall "$id" && did "  uninstalled the Claude Code plugin $id"
    done
    for m in $(old_marketplaces); do
      act "claude plugin marketplace remove $m" claude plugin marketplace remove "$m" && did "  removed the marketplace $m"
    done
  fi
  if [ "$dry" = 1 ]; then
    say "  [dry run] the old daemon would be stopped and the old binaries deleted"
  else
    stop_old_daemons
    old_binaries | while IFS= read -r f; do
      rm -f "$f" "${f%cm-agent}cm-agentw" && say "  deleted $f"
    done
  fi
  if [ "$purge" = 1 ]; then
    if [ "$dry" = 0 ] && [ ! -f "$new_home/agent.json" ]; then
      warn "  --purge skipped: the new home has no agent.json yet, so the old data would be the only copy"
    else
      if [ "$old_home" != "$new_home" ] && [ -d "$old_home" ]; then act "delete $old_home" rm -rf "$old_home" && did "  deleted $old_home"; fi
      for a in $accounts; do
        if [ "$os" = macos ] && command -v security > /dev/null 2>&1; then
          act "delete keychain entry $old_service/$a" security delete-generic-password -s "$old_service" -a "$a"
        elif [ "$old_home" != "$new_home" ]; then
          act "delete $old_home/cred-$a" rm -f "$old_home/cred-$a"
        fi
      done
    fi
  elif [ -d "$old_home" ] && [ "$old_home" != "$new_home" ]; then
    say "  kept the old data folder $old_home (delete it when you are happy; --purge does it)"
  fi
}

# ---------------------------------------------------------------- the removal script

if [ "$only_remove" = 1 ]; then
  found_old || { say "No old agent found."; exit 0; }
  say "An old agent was found:"
  list_old
  confirm "Remove the old agent?" n || { say "Nothing was changed."; exit 0; }
  if [ "$purge" = 1 ]; then carry_over; fi
  remove_old
  exit 0
fi

# ---------------------------------------------------------------- the server and the download host

if [ -z "$server" ]; then
  h="$(identity_home)"
  [ -n "$h" ] && server="$(json_field server < "$h/agent.json")" # a machine that was connected before already knows its server
fi
if [ -z "$server" ] && [ -z "$from" ]; then
  can_prompt || die "the server address is needed: --server https://<your server> (or AIPOD_SERVER)"
  printf 'Server address (https://...): ' > /dev/tty
  read -r server < /dev/tty || server=""
fi
server="${server%/}"
[ -n "$from" ] || from="$server/downloads"
from="${from%/}"
[ -z "$server" ] || valid_url "$server" || die "the server address must be https://<host>"
valid_url "$from" || die "the download address must be https://<host>/..."
command -v curl > /dev/null 2>&1 || die "curl is needed"

tmp="$(mktemp -d "${TMPDIR:-/tmp}/aipod-install.XXXXXX")" || die "no temporary folder"
trap 'rm -rf "$tmp"' EXIT INT TERM

say "Looking for the newest build at $from"
curl -fsSL --max-time 60 --max-filesize 1048576 -o "$tmp/manifest.json" "$from/manifest.json" || die "could not read $from/manifest.json"
file="" version="" sha=""
tr -d '\n\r' < "$tmp/manifest.json" | tr '}' '\n' | grep "\"os\": *\"$os\"" | grep "\"arch\": *\"$arch\"" > "$tmp/records"
while IFS= read -r rec; do
  v="$(printf '%s' "$rec" | json_field version)"
  if [ -n "$v" ] && { [ -z "$version" ] || newer "$v" "$version"; }; then
    version="$v"
    file="$(printf '%s' "$rec" | json_field file)"
    sha="$(printf '%s' "$rec" | json_field sha256)"
  fi
done < "$tmp/records"
{ [ -n "$version" ] && [ -n "$file" ] && [ -n "$sha" ]; } || die "the download host has no build for $os-$arch"
printf '%s' "$file" | grep -Eq '^aipod-(macos|linux)-(arm64|x64)\.zip$' || die "refused: $file is not a build file name"
say "  newest build: aipod $version ($os-$arch)"

# ---------------------------------------------------------------- deciding

old_present=0
installed=""
[ -x "$new_bin" ] && installed="$("$new_bin" version 2> /dev/null | head -n 1 | tr -d '\r ')"
if found_old; then
  old_present=1
  say "An older agent is installed:"
  list_old
  ov="$(old_binary_version)"
  confirm "Remove the old agent${ov:+ (cm-agent $ov)} and install aipod $version? Your settings and login are kept." n \
    || { say "Nothing was changed. The old agent stays; run this again when you are ready."; exit 0; }
elif [ -n "$installed" ]; then
  if newer "$version" "$installed"; then
    confirm "aipod $installed is installed; update to $version?" y || { say "Nothing was changed."; exit 0; }
  else
    say "aipod $installed is installed and is the newest build."
  fi
fi

fresh=0
[ -f "$new_home/agent.json" ] || [ -f "$old_home/agent.json" ] || fresh=1
carry_over
if [ "$old_present" = 1 ]; then remove_old; fi

# ---------------------------------------------------------------- installing the build

if [ -z "$installed" ] || newer "$version" "$installed"; then
  say "Installing aipod $version"
  if [ "$dry" = 1 ]; then
    say "  [dry run] $file would be downloaded, checked and put at $new_bin"
  else
    curl -fsSL --max-time 300 --max-filesize 262144000 -o "$tmp/$file" "$from/$file" || die "download of $file failed"
    [ "$(sha256 "$tmp/$file")" = "$sha" ] || die "refused: $file does not match the SHA-256 in the manifest"
    mkdir -p "$tmp/x"
    if command -v unzip > /dev/null 2>&1; then
      unzip -o -q "$tmp/$file" -d "$tmp/x" || die "unzip failed"
    elif command -v bsdtar > /dev/null 2>&1; then
      bsdtar -xf "$tmp/$file" -C "$tmp/x" || die "bsdtar failed"
    else
      die "unzip (or bsdtar) is needed"
    fi
    [ -f "$tmp/x/aipod" ] || die "the download holds no aipod"
    chmod +x "$tmp/x/aipod"
    if [ "$os" = macos ] && [ "${AIPOD_INSTALL_UNSIGNED_OK:-}" != 1 ]; then
      codesign --verify --strict "$tmp/x/aipod" 2> /dev/null || die "refused: the build is not validly code-signed"
    fi
    mkdir -p "$new_home/bin" && chmod 700 "$new_home"
    mv -f "$tmp/x/aipod" "$new_bin" || die "could not put the build at $new_bin"
    say "  installed $new_bin"
    case ":$PATH:" in
      *":$HOME/.local/bin:"*) [ -d "$HOME/.local/bin" ] && ln -sf "$new_bin" "$HOME/.local/bin/aipod" && say "  linked aipod into $HOME/.local/bin" ;;
      *) say "  to type 'aipod' anywhere, add this folder to PATH: $new_home/bin" ;;
    esac
  fi
fi

# ---------------------------------------------------------------- the login

connected() { AP_LANG=en AP_AGENT_HOME="$new_home" "$new_bin" status 2> /dev/null | grep -q '^connected:'; }
if [ "$dry" = 1 ]; then
  say "  [dry run] the machine would be connected (the old login carried over, or a login)"
elif [ "$want_login" = 1 ]; then
  if connected; then
    say "This machine is already connected (its login was kept)."
    AP_AGENT_HOME="$new_home" "$new_bin" restart > /dev/null 2>&1 || true
  else
    [ -n "$server" ] || die "no server address to log in to: --server https://<your server>"
    say "Connecting this machine: approve the code on the web page it shows."
    AP_AGENT_HOME="$new_home" "$new_bin" login --server "$server" || warn "the login did not finish; run: aipod login --server $server"
  fi
  # A new machine gets the inbox push (web messages start a turn in an idle session); a saved choice is never touched.
  if [ "$fresh" = 1 ]; then AP_AGENT_HOME="$new_home" "$new_bin" config push inbox > /dev/null 2>&1 || true; fi
fi

# ---------------------------------------------------------------- the Claude Code plugin

if [ "$want_plugin" = 1 ]; then
  if ! have_claude; then
    say "Claude Code (the 'claude' command) was not found. When it is installed, run:"
    say "  claude plugin marketplace add $from/aipod-marketplace.json"
    say "  claude plugin install aipod@aipod-releases${server:+ --config server=$server}"
  else
    say "Setting up the Claude Code plugin"
    if claude plugin marketplace list --json 2> /dev/null | grep -q '"name": *"aipod-releases"'; then
      act "claude plugin marketplace update aipod-releases" claude plugin marketplace update aipod-releases
    else
      act "claude plugin marketplace add $from/aipod-marketplace.json" claude plugin marketplace add "$from/aipod-marketplace.json" \
        || warn "  could not add the marketplace $from/aipod-marketplace.json"
    fi
    if claude plugin list --json 2> /dev/null | grep -q '"id": *"aipod@aipod-releases"'; then
      act "claude plugin update aipod@aipod-releases" claude plugin update aipod@aipod-releases && did "  the aipod plugin is up to date"
    else
      # The server option lets the plugin download its own copy of the agent; without it the plugin finds the server in agent.json.
      if [ -n "$server" ]; then
        act "claude plugin install aipod@aipod-releases --config server=$server" claude plugin install aipod@aipod-releases --config "server=$server" \
          || act "claude plugin install aipod@aipod-releases" claude plugin install aipod@aipod-releases \
          || warn "  the plugin was not installed; run: claude plugin install aipod@aipod-releases"
      else
        act "claude plugin install aipod@aipod-releases" claude plugin install aipod@aipod-releases \
          || warn "  the plugin was not installed; run: claude plugin install aipod@aipod-releases"
      fi
      did "  installed the aipod plugin"
    fi
  fi
fi

say ""
say "Done. Restart Claude Code (or open a new session) so it loads the plugin; /aipod:status shows how it is doing."
if [ "$old_present" = 1 ]; then say "A session that was open during the removal may report a hook error once; a new session does not."; fi
exit 0
