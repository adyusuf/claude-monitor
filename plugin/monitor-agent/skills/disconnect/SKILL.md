---
name: disconnect
description: Disconnect this machine from Claude Monitor (sign the agent out). Use only when the user asks to disconnect or log out.
disable-model-invocation: true
---

Sign the Claude Monitor agent out on this machine. Run this with the Bash tool:

```bash
CLAUDE_PLUGIN_DATA="${CLAUDE_PLUGIN_DATA}" sh "${CLAUDE_PLUGIN_ROOT}/scripts/run.sh" logout
```

Tell the user the machine stops reporting, and that it can be removed on the web (Machines page) as well. To stop the agent for good they can disable or uninstall the plugin with `/plugin`: the agent stops by itself within a minute of that.
