#!/bin/sh
# Runs the installed Claude Monitor agent for a hook or for the MCP server (ADR-0007).
#   run.sh hook <Event> | mcp | status | connect ... | anything the agent takes
# The binary lives in the plugin's data directory, which Claude Code keeps across plugin updates and deletes when the plugin is
# uninstalled. Until ensure.sh has installed it there is nothing to run, and a hook must never fail a session: this exits 0 and says nothing.
data="${CLAUDE_PLUGIN_DATA:-}"
[ -n "$data" ] || exit 0
case "$(uname -s)" in
  MINGW* | MSYS* | CYGWIN*) bin="$data/bin/cm-agent.exe" ;;
  *) bin="$data/bin/cm-agent" ;;
esac
[ -x "$bin" ] || exit 0
export CM_BINARY="$bin"
export CM_PLUGIN_ID="monitor-agent@monitor-agent-releases"
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
