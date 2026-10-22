#!/bin/sh
# Runs the installed aipod agent for a hook or for the MCP server (ADR-0007).
#   run.sh hook <Event> | mcp | status | connect ... | anything the agent takes
# The binary lives in the plugin's data directory, which Claude Code keeps across plugin updates and deletes when the plugin is
# uninstalled. Until ensure.sh has installed it there is nothing to run, and a hook must never fail a session: this exits 0 and says nothing.
data="${CLAUDE_PLUGIN_DATA:-}"
[ -n "$data" ] || exit 0
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) bin="$data/bin/aipod.exe" ;;
  *) bin="$data/bin/aipod" ;;
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
export AP_BINARY="$bin"
# The id Claude Code knows this plugin by: the marketplace it came from (the URL catalog aipod-releases, or the repository catalog of the same name).
plugin_id="aipod@aipod-releases"
export AP_PLUGIN_ID="$plugin_id"
# An option nobody set may reach the MCP server unexpanded: only an address counts.
case "${AP_SERVER:-}" in http://* | https://*) ;; *) unset AP_SERVER ;; esac
# The server: the plugin's option (hooks have it), else what the MCP server was given, else what the bootstrap kept (a Bash tool has neither).
if [ -n "${CLAUDE_PLUGIN_OPTION_SERVER:-}" ]; then
  export AP_SERVER="$CLAUDE_PLUGIN_OPTION_SERVER"
elif [ -z "${AP_SERVER:-}" ] && [ -f "$data/server.txt" ]; then
  AP_SERVER="$(head -n 1 "$data/server.txt" | tr -d '\r')"
  export AP_SERVER
fi
# The Stop hook of this plugin version has a timeout that fits the longest stop wait (aipod install --stop-wait); an older version's wrapper does not say so, and the agent then waits for nothing.
export AP_PLUGIN_STOP_WAIT_MAX=590
# The plugin's push option: "inbox" starts a turn in an idle session when a message is sent from the web. Anything else leaves what the agent has saved.
case "${CLAUDE_PLUGIN_OPTION_PUSH:-}" in inbox) export AP_PUSH=inbox ;; esac
exec "$bin" "$@"
