# ADR-0007 follow-ups: what is left, and how to do it

State on 16/10/2026: ADR-0007 (web messages enter a session through its inbox socket; the agent ships as a
Claude Code plugin) is on `dev` (4702c16), gate green. Nothing is promoted to `test` or `prod`; the maintainer
is trying it on their own machine first. Design and measurements: `docs/adr-0007-plugin-distribution-and-inbox-push.md`
(sections 8, 9, 9.1). Three helper sessions were started and stopped before they changed anything (no commits,
clean worktrees), so every item below is still open.

## For the maintainer (not code work)

| Item | What to do |
|---|---|
| Try it locally | `dotnet publish src/Podium.Agent -c Release -r osx-arm64 -o out/osx-arm64`, `aipod login --server <server>`, `aipod install --push inbox`, restart Claude Code, send a prompt from the web, read `aipod status` (line "Push into the session", and "Last inbox failure" if it fails). Expect a turn in an idle session in about 1 s. |
| Real release | Build, write the SHA-256 pins into `plugin/monitor-agent/agent-release.txt` (`scripts/build-agent.sh` calls `scripts/write_plugin_pins.py`), sign the manifest (ADR-0004). The pins file is a placeholder, so the marketplace plugin downloads no binary yet. Order: `docs/release.md`. |
| Marketplace address | Set `VITE_PLUGIN_MARKETPLACE` (`owner/repo#branch`) per environment so the download page shows the install card. |
| Push default | Stays off (opt-in rule). Turning it on by default is the maintainer's call. |
| Old local plugin | `monitor-agent-local` on the Mac was `connected:false`; `/monitor-agent:connect` replaces it. |
| Promotion | `dev` to `test` to `prod` when satisfied: `bash scripts/merge-gate.sh test`, then `prod`; the `prod` push is the maintainer's. |

## Code work: what was done and what is still open (updated 16/10/2026)

Done (ADR-0007 sections 9.1 and 9.2, ADR-0010 for the rest of the day's work): the command's transport label and the "pushed, not shown yet" state with the push-aware idle warning;
the plugin lifecycle gaps (a plugin missing from Claude Code's list, project and local scopes, `--keep-data`, `--stop-wait`); the marketplace served from the site itself
(`scripts/build_plugin_site.py`, `AGENT_SITE_URL`, `VITE_PLUGIN_FROM_SERVER`).

Still open, and needing a real machine: **Windows** (the named-pipe branch of `SocketInboxTransport`, `sh` for the MCP server on PATH, `Detach`, the rename-aside in `ensure.sh` have never run) and
a **real end-to-end with Claude Code** (the daemon's write into a real session's inbox, a real marketplace install from the site's own address with a public certificate). The checklist is `docs/adr-0008-host-checks.md`.

Rules for any further item: own branch, one commit per task, files under 300 lines, backward compatible, no host or real names, no push to `prod`.
