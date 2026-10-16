---
name: status
description: Show whether the Claude Monitor agent is installed, connected and running on this machine, and how its last install went. Use when the user asks about the state of Claude Monitor or why nothing is reported.
---

Show the state of the Claude Monitor agent on this machine. Run these with the Bash tool and summarise the result for the user in plain words.

```bash
CLAUDE_PLUGIN_DATA="${CLAUDE_PLUGIN_DATA}" sh "${CLAUDE_PLUGIN_ROOT}/scripts/run.sh" status
tail -n 15 "${CLAUDE_PLUGIN_DATA}/install.log" 2>/dev/null
```

If the first command printed nothing, the agent binary is not installed yet: it is installed in the background when a Claude Code session starts, and the log lines above say how that went (a missing server address, a build that is not published for this system, a download that failed or did not match its checksum). If the agent is installed but "not connected", suggest `/monitor-agent:connect`.
