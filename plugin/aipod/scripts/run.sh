#!/bin/sh
# Runs the installed aipod agent for a hook or for the MCP server (ADR-0007).
#   run.sh hook <Event> | mcp | status | connect ... | anything the agent takes
# The binary lives in the plugin's data directory, which Claude Code keeps across plugin updates and deletes when the plugin is
# uninstalled. Until ensure.sh has installed it there is nothing to run, and a hook must never fail a session: this exits 0 and says nothing.
data="${CLAUDE_PLUGIN_DATA:-}"
[ -n "$data" ] || exit 0
# ADR-0013 section 1: while the old plugin (monitor-agent) is enabled in a version up to 0.5.0 (the last one before the bridge), it reports every event itself; reporting
# here too would count each one twice. The bridge version of the old plugin stands down for this one, so a version it cannot read means "go on".
cfg="${CLAUDE_CONFIG_DIR:-$HOME/.claude}"
old="monitor-agent@monitor-agent-releases"
if [ "${1:-}" = "hook" ] || [ "${1:-}" = "mcp" ]; then
  if grep -q "\"$old\": *true" "$cfg/settings.json" 2>/dev/null; then
    have="$(awk -v id="\"$old\"" 'index($0, id) { f = 1; next } f && /"version"/ { print; exit }' "$cfg/plugins/installed_plugins.json" 2>/dev/null | sed -n 's/.*"version": *"\([^"]*\)".*/\1/p')"
    if [ -n "$have" ] && [ "$(printf '%s\n%s\n' "$have" 0.5.0 | sort -V | head -n 1)" = "$have" ]; then exit 0; fi
  fi
fi
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) bin="$data/bin/cm-agent.exe" ;;
  *) bin="$data/bin/cm-agent" ;;
esac
[ -x "$bin" ] || exit 0
# The machines block (ADR-0008) needs a binary that knows `--context`: an older one, still installed while ensure.sh downloads the new
# one, would read it as a second recording hook. The plugin's pin is the oldest binary that knows it.
if [ "${1:-}" = "hook" ] && [ "${2:-}" = "SessionStart" ] && [ "${3:-}" = "--context" ]; then
  pinned="$(head -n 1 "${CLAUDE_PLUGIN_ROOT:-.}/agent-release.txt" 2>/dev/null | tr -d '\r')"
  have="$("$bin" version 2>/dev/null | tr -d '\r')"
  [ -n "$pinned" ] && [ -n "$have" ] || exit 0
  [ "$(printf '%s\n%s\n' "$pinned" "$have" | sort -V | head -n 1)" = "$pinned" ] || exit 0
fi
export CM_BINARY="$bin"
export CM_PLUGIN_ID="aipod@aipod-releases"
# The server: the plugin's option (hooks have it), else what the MCP server was given, else what the bootstrap kept (a Bash tool has neither).
if [ -n "${CLAUDE_PLUGIN_OPTION_SERVER:-}" ]; then
  export CM_SERVER="$CLAUDE_PLUGIN_OPTION_SERVER"
elif [ -z "${CM_SERVER:-}" ] && [ -f "$data/server.txt" ]; then
  CM_SERVER="$(head -n 1 "$data/server.txt" | tr -d '\r')"
  export CM_SERVER
fi
# The Stop hook of this plugin version has a timeout that fits the longest stop wait (cm-agent install --stop-wait); an older version's wrapper does not say so, and the agent then waits for nothing.
export CM_PLUGIN_STOP_WAIT_MAX=590
# The plugin's push option: "inbox" starts a turn in an idle session when a message is sent from the web. Anything else leaves what the agent has saved.
case "${CLAUDE_PLUGIN_OPTION_PUSH:-}" in inbox) export CM_PUSH=inbox ;; esac
exec "$bin" "$@"
