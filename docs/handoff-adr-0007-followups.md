# ADR-0007 follow-ups: what is left, and how to do it

State on 16/10/2026: ADR-0007 (web messages enter a session through its inbox socket; the agent ships as a
Claude Code plugin) is on `dev` (4702c16), gate green. Nothing is promoted to `test` or `prod`; the maintainer
is trying it on their own machine first. Design and measurements: `docs/adr-0007-plugin-distribution-and-inbox-push.md`
(sections 8, 9, 9.1). Three helper sessions were started and stopped before they changed anything (no commits,
clean worktrees), so every item below is still open.

## For the maintainer (not code work)

| Item | What to do |
|---|---|
| Try it locally | `dotnet publish src/ClaudeMonitor.Agent -c Release -r osx-arm64 -o out/osx-arm64`, `cm-agent login --server <server>`, `cm-agent install --push inbox`, restart Claude Code, send a prompt from the web, read `cm-agent status` (line "Push into the session", and "Last inbox failure" if it fails). Expect a turn in an idle session in about 1 s. |
| Real release | Build, write the SHA-256 pins into `plugin/monitor-agent/agent-release.txt` (`scripts/build-agent.sh` calls `scripts/write_plugin_pins.py`), sign the manifest (ADR-0004). The pins file is a placeholder, so the marketplace plugin downloads no binary yet. Order: `docs/release.md`. |
| Marketplace address | Set `VITE_PLUGIN_MARKETPLACE` (`owner/repo#branch`) per environment so the download page shows the install card. |
| Push default | Stays off (opt-in rule). Turning it on by default is the maintainer's call. |
| Old local plugin | `monitor-agent-local` on the Mac was `connected:false`; `/monitor-agent:connect` replaces it. |
| Promotion | `dev` to `test` to `prod` when satisfied: `bash scripts/merge-gate.sh test`, then `prod`; the `prod` push is the maintainer's. |

## Code work still open (independent, one branch each, off `dev`)

1. **Show which transport delivered a command.** The agent reports inbox / channel / hook when it marks a command
   pushed or taken; the API stores it additively (nullable column); the web shows a label (i18n `en` and `tr`).
   Old agents that report nothing keep working. Files: `Push/InboxPump.cs`, `Storage/LocalStorePush.cs`, the
   API command state + migration, `web/src` command rows.
2. **Plugin lifecycle gaps** (`Daemon/PluginWatch.cs`, `Config/SavedSettings.cs`):
   a plugin disabled only at project or local scope is not noticed (stop only when disabled everywhere it was
   enabled for the user, never for one project); `--keep-data` uninstall hides the removal (find a signal such as
   `installed_plugins.json`, or document the limit with a test); `--stop-wait` is ignored for plugin-started agents.
   Tests use temp dirs, never real Claude config.
3. **Serve the marketplace from our own server** (deferred in ADR section 9). First an ADR addendum measured with
   `claude plugin marketplace add` against a local source using a temporary `CLAUDE_CONFIG_DIR`; code only if
   feasible and the trust model (pins, signed manifest) still holds. Removes the need for a public repo.
4. **Windows** (needs a Windows machine): the named-pipe branch of `SocketInboxTransport`, `sh` for the MCP server
   on PATH, `Detach` (a no-op there) and the rename-aside in `ensure.sh` have never run.
5. **Real end-to-end with Claude Code:** the daemon's write into a real session's inbox and a real marketplace
   install were measured by the probe but not run end to end.

Rules for every item: own branch, one commit per task, `bash scripts/merge-gate.sh dev`, files under 300 lines,
backward compatible, no host or real names, no push to `prod`. Mode C (sonnet) is in `.claude/mode`.
