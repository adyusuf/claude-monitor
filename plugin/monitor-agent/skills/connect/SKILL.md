---
name: connect
description: Connect this machine to Claude Monitor. Shows a code to approve on the web, then the agent reports this machine's sessions. Use when the user asks to connect, log in or set up Claude Monitor.
---

Connect this machine to Claude Monitor. Run these commands with the Bash tool, one at a time, and report what they print.

1. Remove the old local registration of this agent, if there is one, so events are not reported twice (it prints nothing when the agent is not installed yet; in that case run `/monitor-agent:status` and explain what it says):

```bash
CLAUDE_PLUGIN_DATA="${CLAUDE_PLUGIN_DATA}" sh "${CLAUDE_PLUGIN_ROOT}/scripts/run.sh" uninstall
```

2. Start the login. It prints a web address and a short code and returns at once; the login finishes in the background when the code is approved:

```bash
CLAUDE_PLUGIN_DATA="${CLAUDE_PLUGIN_DATA}" sh "${CLAUDE_PLUGIN_ROOT}/scripts/run.sh" connect
```

If it says the server is missing, the plugin has not been through a session start yet (or its server option is empty): ask the user for the https address of their Claude Monitor server and run the same command with `connect --server '<address>'`, the address in single quotes. Show the user the address and the code exactly as printed and ask them to approve it on that page while signed in. Never ask for, type or repeat a password or a token: the approval happens in the browser. When the user says they approved it, run `/monitor-agent:status` to confirm.
