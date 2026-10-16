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
if [ -n "${CLAUDE_PLUGIN_OPTION_SERVER:-}" ]; then export CM_SERVER="$CLAUDE_PLUGIN_OPTION_SERVER"; fi
exec "$bin" "$@"
