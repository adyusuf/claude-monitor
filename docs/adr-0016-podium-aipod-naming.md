# ADR-0016: One set of names: Podium and aipod

**Status:** Decided by the maintainer (22/10/2026). Done on the branch `chore/rename-podium-aipod`.
**Replaces the transition of** [ADR-0012](adr-0012-aipod-name-and-tray.md) and [ADR-0013](adr-0013-aipod-migration-plan.md): no installed copy of the old names is carried over. The older ADRs keep the old names, because they record what was decided then.

## 1. Decision

The product has two names and nothing else: **Podium** is the web site, the API and the repository; **aipod** is the daemon on a machine (and its tray, its plugin, its downloads). Every other name goes, with **no migration code** (a clean break, the maintainer's choice): an installed agent, plugin or service under an old name is not found, converted or stopped by the new code.

| Old | New |
|---|---|
| `ClaudeMonitor.*` namespaces, projects, `ClaudeMonitor.slnx`, `C:\ClaudeMonitor`, `Claude Monitor` in texts, the repository `claude-monitor` | `Podium.*`, `Podium.slnx`, `C:\iis\Podium`, `Podium`, the repository `podium` |
| `MonitorDb` (the DbContext) | `PodiumDb` |
| server variables `MONITOR_*`, the server file `monitor.env` | `AIPOD_*`, `aipod.env` |
| agent variables `CM_*` (and their `AIPOD_*` aliases) | `AP_*` (`AP_AGENT_HOME`, `AP_SERVER`, `AP_PUSH`, ...) |
| `cm-agent`, `cm-agentw`, `cm-agent-<os>-<arch>.zip`, `/etc/cm-agent` | `aipod`, `aipodw`, `aipod-<os>-<arch>.zip`, `/etc/aipod` |
| `cm_session`, `cm_mfa`, `cm_external` (cookies); `cm.language`, `cm.theme`, `cm.activity` (browser storage); `cm-approval-v1` (approval signature tag); `cm-agent-update/1` (manifest format) | `ap_session`, `ap_mfa`, `ap_external`; `ap.language`, `ap.theme`, `ap.activity`; `ap-approval-v1`; `aipod-update/1` |
| MCP tools `monitor_*`, the MCP server key | `aipod_*`, `aipod` |
| the Claude Code plugin `monitor-agent` (the bridge) and its catalog | removed; `plugin/aipod` in the catalog `aipod-releases` |
| agent home `~/.claude-monitor`, `%LOCALAPPDATA%\ClaudeMonitor`, macOS `.../ClaudeMonitor` | `~/.aipod`, macOS `~/Library/Application Support/aipod` |
| service, account, unit and launchd names (`_cmagent`, `_cmrun`, `com.aipod.agent*`) | `aipod`, `_aipod`, `_aprun`, `aipod.agent*` |
| the activity class `monitor_input` (a message from the web) | `podium_input` |

## 2. Removed with it

`aipod service migrate`, `aipod migrate-home`, the folder-resolving `HomeResolver`, `HomeMigration`, `BinaryAlias`, `Brand`'s aliases and legacy name, the two tool-name modes (`--names`, `AP_TOOL_NAMES`), `ServiceNames.Legacy`, `StandaloneNames.Legacy`, `ForAipod`, the `monitor-agent` plugin with the stand-down logic in `plugin/aipod/scripts`, the web's "old plugin" badge, and the zips of the old plugin in the download host.

## 3. What stays on purpose

- **The backup encryption labels** in `deploy/windows/BackupCrypto.psm1` (`"claude-monitor backup encryption"` and `... authentication`). They are inputs of the key derivation: changing them would make every existing backup unreadable.
- **Names the repository does not own:** the GitHub repository name and the local folder (renamed by the maintainer outside the code), the R2 bucket, the database names and roles that a server already has (they are values in `aipod.env`, not in the code), and the history of the older ADRs and git.
- **Generic English** (`monitoring`, `Monitor.Enter`, `IOptionsMonitor`, `MachineMonitor` for the metrics and alerts class) is not a name of the product.

## 4. What an operator has to do (nothing is automatic)

1. **Servers (test and prod), once each:** stop the site; rename `C:\ClaudeMonitor` to `C:\iis\Podium`, the IIS site and app pool to `Podium-<env>`, the scheduled backup task to `Podium-backup-<env>`; rename `monitor.env` to `aipod.env` and every `MONITOR_` line to `AIPOD_`; deploy the new package. The database and its role keep their names (they are values in the file).
2. **The signing key in the macOS keychain:** the service name is now `aipod-update-<channel>`. Copy each key once (for `test` and `prod`): `security add-generic-password -s aipod-update-<channel> -a <account> -w "$(security find-generic-password -s cm-agent-update-<channel> -w)"`, check that a build signs, then delete the old entry.
3. **Download host (R2):** publish with `scripts/publish_agent_downloads.sh`; the files are `aipod-*.zip` and the manifest format is `aipod-update/1`, so agents of the old names cannot read the new manifest. Delete the `cm-agent-*` files and `monitor-agent-plugin.zip` after the new ones are there.
4. **Machines:** uninstall the old agent (service, autostart, plugin), install the new one, log in again. The old home folder is not read; copy nothing.
5. **People:** everyone signs in again once (the cookie names changed) and the language and theme choices reset (the storage keys changed).
6. **Repository:** rename the GitHub repository to `podium` and the local folder; the marketplace coordinates in the web's install commands then read `<owner>/podium`.

## 5. Why a clean break

Two earlier ADRs (0012, 0013) carried both names side by side so that no install was orphaned: two tool-name modes, two service layouts, a migrator, a home mover, a bridge plugin. That doubled the code and the tests to protect a handful of machines the maintainer controls. The maintainer chose to remove the old names outright and reinstall.
