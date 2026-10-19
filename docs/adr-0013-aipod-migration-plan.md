# ADR-0013 (proposed): moving the names left over from the rename to aipod

Status: **proposed 19/10/2026. Built so far (19/10/2026): step 1 (the home resolver, section 3.1), step 2 (the tier 1 display names, section 4) and
step 3 (the tool names, section 2: `McpToolNames`, `ToolRegistry`, `aipod mcp --names`), step 4 (the `aipod` plugin and the bridge, section 1, "As built") step 5 (`aipod migrate-home`, section 3, "As built") and step 6 (`aipod service migrate`, section 4, "As built"); the rest is not built.** Follows ADR-0012 (the name people see is aipod; the old names keep working). This ADR
designs, one item at a time, the four names ADR-0012 left alone: the plugin id, the MCP tool names, the home folder, and the service names.
Each section says what exists, what depends on it, the proposed way across, how to go back, what is tested, and what needs the maintainer.

Rules that shape every item: additive only (global rule 4: nothing is deleted or renamed without a period in which both work); a hook never
blocks or fails a session; an installed agent updates itself from a signed manifest, so what the new build does must also work next to an old
build for as long as old builds are around; Windows services and Claude Code's plugin semantics cannot be run on the development Mac, so each
item names the spike or test machine it needs.

## 0. What must never change (internal identifiers)

- `AgentConfig.CredentialService = "claude-monitor-agent"`. It is the macOS Keychain service name and the entropy of the Windows DPAPI
  files that hold the tokens. Changing it makes every stored token unreadable and forces every machine to log in again. It stays for good;
  it is not shown to anyone.
- The wire markers between the agent, the transcript tailer and the web: `<<<claude-monitor-message` / `claude-monitor-message>>>`, the
  notice marker `claude-monitor web message`, and the header "Messages sent to this session from Claude Monitor (the web):". Section 2 says
  how (and whether) the visible ones change.

## 1. The plugin id and folder (`monitor-agent`)

**What exists.** Plugin `monitor-agent` in `plugin/monitor-agent/` (36 files mention the name); URL catalog `monitor-agent-releases` and
`monitor-agent-plugin.zip` built by `scripts/build_plugin_site.py`; the local copy `monitor-agent-local` that `install` writes; the id
`monitor-agent@monitor-agent-releases` hard-coded in `run.sh` as `CM_PLUGIN_ID` (the daemon stops when Claude Code no longer enables that id,
`PluginWatch`); skills `/monitor-agent:connect|status|disconnect`; user options `server` and `push`.

**Why it is not a rename.** Claude Code identifies a plugin by name and marketplace, so `aipod@aipod-releases` is a *different* plugin. On a
machine that has both, every hook runs twice and every event is reported twice (`ensure.sh` already guards this for the old local plugin).
The new plugin also gets a new data directory, so the binary is downloaded again, the `server` option is asked again, people's permission rules
(`mcp__plugin_monitor-agent_…`) and their muscle memory (`/monitor-agent:connect`) stop matching. Login is *not* lost: the identity file and the
tokens live in the agent home and the OS credential store, which the plugin does not own.

**Proposal: side by side, then retire.**

1. *Agent 0.6.x, no behaviour change for old plugins:* the agent already takes the plugin id from `CM_PLUGIN_ID`, so it works with any id; add
   nothing there.
2. *A new plugin `aipod`* in `plugin/aipod/` (git mv), catalog `aipod-releases`, zip `aipod-plugin.zip`, skills `/aipod:connect|status|disconnect`,
   local marketplace `aipod-local`; `build_plugin_site.py` publishes **both** catalogs from the same agent builds (the zips of the agent keep
   their names, see ADR-0012).
3. *A final version of the old plugin (the bridge)*: its `run.sh` exits quietly (as it does with no binary) when `aipod@aipod-releases` is enabled
   in the user's `settings.json`, so the two never report together; its `ensure.sh` says once "aipod replaces this plugin: run /aipod:connect".
   The new plugin's `run.sh` does the mirror image for a still-enabled *pre-bridge* old plugin (it cannot know the old one is a bridge): it
   records nothing and prints the instruction, so a machine is never double-reported while people migrate.
4. *`/aipod:connect`* installs nothing new itself; it removes the old plugin (`claude plugin uninstall monitor-agent@monitor-agent-releases`,
   as `connect` already does for `monitor-agent-local`), reads the server from `agent.json` (so the option is not asked twice), and prints the
   permission rule to allow for the new tool names (section 2).
5. *Retire:* the old catalog and zip are removed after a published period (proposed: 90 days after the bridge, and only when the web shows no
   machine still on the old id; for that the agent reports its plugin id in the heartbeat profile, an additive nullable column
   `agents.plugin_id`; optional, but it turns "when is it safe" from a guess into a number).

**Back.** Both ids stay valid in the agent for the whole period; to go back, uninstall the new plugin and install the old one. No data is lost
(home and tokens are shared).

**Tests.** Python script tests for the site builder (both catalogs, pins), `PluginWatch` with either id, wrapper tests for the stand-down in
both directions (the existing `test_plugin_run.py` / `test_plugin_ensure.py` patterns). **Needs a spike on a real Claude Code first** (about an
hour): that a skill can run `claude plugin uninstall`, that two plugins with the same MCP server key do not collide, and how `userConfig` is
carried. **Needs the maintainer:** the retire period (section 5), and whether the agent may report its plugin id.

**As built (19/10/2026, step 4).** The spike ran on Claude Code 2.1.294 in a private config directory: two plugins that carry the same MCP server key install
side by side and do not collide (the server is namespaced by plugin, `plugin:<name>:claude-monitor`, so the tools are `mcp__plugin_aipod_claude-monitor__*`);
`claude plugin uninstall <id> --json` works from a shell, which is all a skill needs; `claude plugin configure <id> --values-stdin` saves a `userConfig` value;
a plugin whose required option is unset starts no MCP server. Built: `plugin/aipod` (same hooks and MCP server, its own words, id `aipod@aipod-releases` in
`run.sh`), the old plugin as the bridge, both rendered by `aipod plugin-files` so the drift test covers both, `scripts/build_plugin_site.py` writing
`aipod-plugin.zip` + `aipod-marketplace.json` beside the old pair, and `write_plugin_pins.py` writing the same pins to both folders. Two details the text above left open:
the plugin's version is the agent's version (the generator takes `AgentConfig.Version`), so "pre-bridge" is "up to 0.5.0": `aipod`'s `run.sh` stands down for
hooks and the MCP server while `monitor-agent@monitor-agent-releases` is enabled at 0.5.0 or older (a version it cannot read means "go on", because the bridge is the likelier
one), and the bridge stands down when `aipod@aipod-releases` is enabled; `status`, `connect` and `logout` are never held back. The bridge says once, at a session start, that aipod
has a new plugin. `/aipod:connect` uninstalls the old plugin and names the new permission prefix. Added after: the repository catalog (`.claude-plugin/marketplace.json`) lists both plugins, so `aipod` can also be installed from git (its id is then `aipod@monitor-agent-releases`: the wrapper picks whichever of the two ids is enabled, and the bridge stands down for either); the `server` option of `aipod` is no longer required (a connected machine's `agent.json` is read, and an option nobody set that reaches the MCP server unexpanded is ignored); the git route of the web page shows the `aipod` commands. Built after: the agent reports its plugin id with the profile (`agents.plugin_id`, validated as `name@marketplace`, migration `PluginId`), the machines list marks a machine still on the old plugin, and `scripts/build_plugin_site.py --without-old` retires it (builds only aipod and removes the old zip and catalog): run it 90 days after the bridge shipped and only when the list shows no machine on the old plugin. The site route of that page now shows the `aipod`
commands and a note for machines on the old plugin; deploy the downloads with `aipod-marketplace.json` before that web build (`docs/release.md`), or the command points at a file that is not there). The git marketplace
(`.claude-plugin/marketplace.json`) still lists only the old plugin: one repository has one catalog, and its name is the old id.

## 2. The MCP tool names (`monitor_*`) and the server key (`claude-monitor`)

**What exists.** 19 tools (`monitor_status`, `monitor_machines`, `monitor_ask`, `monitor_answer`, `monitor_inbox_read`, `monitor_run`, ...)
registered by attribute (`McpServerTool(Name = "monitor_…")`) in seven classes; the server key `claude-monitor` in `.mcp.json` and
`PluginInstaller.McpServerName`. The names are repeated in about 200 text sites: tool descriptions that point at each other, the notices and
envelopes written into sessions (`monitor_inbox_read`, `monitor_answer`), the machines block the SessionStart hook hands the model, `status`
text, the skills. The fully qualified name Claude sees is `mcp__plugin_<plugin>_<server>__<tool>`, so section 1 already changes the prefix.

**Proposal: the plugin decides the names, the binary serves both.** No tool is registered twice (that would add about 19 descriptions to every
session's context):

1. `aipod mcp --names aipod|legacy`; **no flag means `legacy`**, so every old plugin, running with a newer binary, keeps `monitor_*`. The new
   plugin's `.mcp.json` passes `--names aipod`, server key `aipod`, tools `aipod_*`.
2. One central mapping (`Tool.Name("ask_result")` → `monitor_ask_result` or `aipod_ask_result`) replaces the literals; texts that mention a tool
   are built with it, so each mode only ever names its own tools. The machines block is stored with placeholders and filled in by the
   `--context` hook, which knows the mode (the plugin's `run.sh` passes it).
3. The tool *behaviour* is unchanged; a test runs the whole tool suite in both modes and checks no `monitor_` appears in `aipod` mode texts.
4. **Markers stay unchanged** (section 0) in the first release. If the maintainer wants the visible ones ("claude-monitor web message" in the
   📩 line, the header the web matches) to say aipod: release N teaches the tailer and the web parser to accept both; release N+1 (after the
   web is deployed everywhere) makes the agent write the new ones. Sending first would break delivery confirmation on machines running N-1.

**Built (step 3).** `McpToolNames` holds the mode (`aipod mcp --names aipod|legacy`, else `AIPOD_TOOL_NAMES`/`CM_TOOL_NAMES`, else legacy; scoped to the
run, so tests cannot leak it) and the one place names are made: `Name("ask_result")`, `Text(...)` (rewrites only the closed list of 19 names, whole words, in
STATIC text: descriptions, rules, notices), and `Slot`/`Fill` (a placeholder kept in the database for a reader whose mode the writer does not know).
`ToolRegistry` registers the legacy set exactly as before and creates the aipod set from the same methods with their descriptions and parameter schemas
rewritten. Text that carries data (an ask's question, a machine name, a script's output) is never rewritten: it names its tools through `Name`. The
machines block is stored with slots and filled by the SessionStart hook in its own mode; a notice or an ask written into a session names the tools of THAT
session, because the hook stores the mode in the session's inbox row (`session_inboxes.tools`, additive). **Step 4 must:** pass `--names aipod` in the new
plugin's `.mcp.json` and export `AIPOD_TOOL_NAMES=aipod` in its `run.sh`; the old plugin passes nothing and keeps `monitor_*`.

**Back.** Omit the flag. **Tests:** both modes, the cross-reference texts, the tailer and the web parser with both markers. **Needs the
maintainer:** whether the visible markers should change at all (default: no), and the names themselves (`aipod_status` etc., prefix swap only).

## 3. The home folder

**What exists.** macOS `~/Library/Application Support/ClaudeMonitor`; Windows and Linux `~/.claude-monitor` (moved once before from
`%LOCALAPPDATA%\ClaudeMonitor` because the Claude desktop app, a packaged MSIX app, sees a redirected AppData). It holds `agent.json`
(identity, no tokens), `agent.db` (SQLite, WAL, open while the daemon runs), `agent.log`, `daemon.lock`/`daemon.pid`, update state, `bin/`
(the installed binary), the connect log, and on Windows the DPAPI-protected token files. `HomeMigration` is the precedent: it copied only
`agent.json` and deliberately did not move the database.

**Proposal: resolve first, move only on request.**

1. *Resolver (small, safe, first):* home = `AIPOD_AGENT_HOME` / `CM_AGENT_HOME`, else the new folder if it exists, else the old folder if it
   exists (keep using it, no move), else the new folder (fresh installs). New names: `~/Library/Application Support/aipod` on macOS,
   `~/.aipod` elsewhere. Every consumer (agent, hooks, tray, plugin wrapper) uses the one resolver, so they cannot disagree.
2. *`aipod migrate-home` (explicit, never automatic):* takes the daemon lock (so no daemon starts), refuses if a daemon is running or a file
   is held (Windows), renames the old folder to the new one (one atomic rename on one volume, so the database and its WAL move together), leaves
   a symlink (macOS, Linux) or junction (Windows) at the old path for old binaries and old plugin wrappers, restarts the daemon, and prints how
   to undo it (`mv` back, remove the link).
3. *Later, once the resolver has shipped for a release:* the daemon may offer the same move at start when it is provably safe. Not before.
4. The credential store is untouched (section 0). Service-mode homes (`/var/lib/cm-agent`, `%ProgramData%\ClaudeMonitor\service`, ...) are set
   by the service definition and belong to section 4.

**Back.** Move the folder back and delete the link. **Tests:** the resolver table (env, new only, old only, both, none) on all three OSes
as pure functions; the move on temp folders with a held lock, a running-daemon refusal, a held file, the link; the existing home-migration
tests stay. **Needs the maintainer:** agree that no automatic move happens in the first release (the cost of "never" is two different paths
on a fleet, which docs and the tray handle through the resolver).

**As built (19/10/2026, step 5).** `aipod migrate-home [--dry-run]` (`HomeMove`): it runs before anything else writes in the home, checks everything first, and only
then stops the daemon (the `restart` way, a kill if it hangs), takes the daemon's own lock so none starts in between, renames the folder in one step, makes a symbolic link
at the old path and starts the daemon again if it had been running; if the link cannot be made the folder is put back, and a daemon it stopped is started again after any
failure. It refuses (exit 2, nothing touched) for a home named by `AIPOD_AGENT_HOME`/`CM_AGENT_HOME`, a boot service's home, a home that is neither the old nor the aipod
folder, an aipod folder that has content (an empty one is replaced), and **Windows**, which needs a junction and test machines and stays on the resolver for now. "Already
using the aipod folder" and "already a link" are exit 0. Tested on temp folders (move, link, no-start when it was not running, dry run, stop failure, lock taken in the
meantime, each refusal) and run once for real against a throwaway `HOME`; never run on a real home, a launchd or systemd unit (a unit that restarts the daemon by itself can
race the stop: the lock turns that into a refusal, not a corrupt move) or Windows. Not built: the automatic offer (3.3), a junction for Windows.

## 4. The service, unit, task and label names

**What exists.** Boot service (ADR-0005): service and unit `cm-agent`, accounts `cm-agent` (Linux), `_cmagent` (macOS), `NT SERVICE\cm-agent`
(Windows, a virtual account derived from the service name), launchd label `com.claudemonitor.agent`, folders `/usr/local/lib/cm-agent`,
`/var/lib/cm-agent`, `/Library/Application Support/ClaudeMonitor/{bin,home}`, `%ProgramFiles%\ClaudeMonitor`, `%ProgramData%\ClaudeMonitor\service`,
the admin-owned exec policy `/etc/cm-agent/exec.json` (and its Windows and macOS equivalents), the sudoers file `cm-agent-run`. Standalone
(ADR-0008 §3g): launchd label `com.claudemonitor.agent.user`, bundle `ClaudeMonitorAgent.app` (bundle id `com.claudemonitor.agent.app`), systemd
user unit `claude-monitor-agent.service`, scheduled task `ClaudeMonitorAgent`, `cm-agentw.exe`.

**Two different things hide under "the names".**

- *What people see:* the Windows service's display name and description, the macOS Login Items entry (taken from the bundle's name), the
  unit and task descriptions. These can change **without** changing an identifier: `sc config … DisplayName=`, `Info.plist`
  `CFBundleName`/`CFBundleDisplayName`, `Description=`. **Proposal tier 1, do it:** show "aipod" in all of them.
- *What the system keys on:* the service name, accounts, labels, folders, the policy path. Renaming them makes a *new* service: a new
  Windows account SID (every ACL on the home and the policy must be re-applied), new owners on files, the exec policy and its approver
  keys under another path, downtime, and an administrator at the console of every server. **Proposal tier 2, only if the maintainer wants it:**
  an explicit `aipod service migrate [--dry-run]` per OS that stops the old service, copies the service home to the new folder with the
  right owner, creates the new service and account, starts it, waits for a healthy heartbeat, and only then disables (not deletes) the old
  one for seven days. Until then the agent reads the exec policy from the new path first and the old path second.

**Back (tier 2).** Re-enable the old service (it was only disabled) and stop the new one. **Tests:** the argument lists and plans as pure
functions (as `WindowsServiceSetup` and `ServiceInstallerTests` do now), a Linux run in a container if one is available; **Windows and macOS
cannot be exercised on this Mac, so tier 2 needs a Windows Server test machine and a macOS test machine before it ships.** **Needs the
maintainer:** tier 1 yes/no, tier 2 yes/no (my recommendation: tier 1 now, tier 2 not unless a name becomes a real problem), and the
reverse-DNS prefix for launchd labels and bundle ids. ADR-0012's tray used `net.aipod.tray` as a placeholder; no domain of ours is known, so it is now `aipod.tray` (launchd needs a unique label, not a
reverse-DNS name). The existing identifiers (`com.claudemonitor.agent*`) stay as they are, and a real prefix is only needed if tier 2 renames them.

**As built (19/10/2026, step 6, on the maintainer's yes; it first read "decided against" and was corrected the same day).** Not run on any real service, nor on macOS or Windows: the tests (written, not run) drive the Linux flow on temp folders with a fake `systemctl`.

- **Names.** `ServiceNames` (service/unit, unit path, launchd label, plist path, sudoers file) rides on `ServiceLayout.Names`; the old set is `ServiceNames.Legacy` (unchanged constants), the new one `ServiceNames.Aipod`: `aipod`, `aipod.service`, label `aipod.agent` (no reverse-DNS, as for the tray),
  accounts `aipod`, `_aipod`, `NT SERVICE\aipod`, folders and policy under `aipod` names (the table in `SETUP.md`), sudoers `aipod-run`. The binary file stays `cm-agent`. `install --service` picks the aipod layout for a fresh install and keeps the old one for a service that exists;
  `uninstall --service` removes whichever exists (both, half way through a migration).
- **Policy path.** `AgentConfig.ExecConfigPath` is the aipod place when a policy is there, else the old one (and the approvers file follows it).
- **`aipod service migrate [--dry-run | --undo | --finish [--now]]`** (`ServiceMigrator`). Refuses (changes nothing) unless the old service is installed and connected, the aipod names and folders are free and it runs as an administrator (a dry run needs none). Real run: stop the old service; copy the home
  (not the bundle folder, locks, login code or the run scratch folder) and `exec.json`/`approvers.json`; install the new service with the same installer as a fresh install (account, binary copy, policy kept, unit, start); wait up to 90 s for the new database to show a contact with the server
  (read-only; Linux and macOS); disable (not remove) the old service; write a marker with the time beside the new policy. Any failure before the disable removes what the run made and starts the old service again. `--undo` stops and disables the new service, brings its identity and database
  (and policy) back to the old places and enables the old one. `--finish` needs seven days since the marker (or `--now`), checks every path it will delete against the new layout first, then removes the old unit/plist, account, folders, policy, sudoers drop-in.
- **Windows is different.** The tokens are DPAPI-protected for `NT SERVICE\cm-agent` and cannot be read by `NT SERVICE\aipod`, so the home is copied after the new service exists (so the files take its ACL) without the `cred-*.bin` files, the identity loses its agent and workspace ids and keeps its
  machine key and server, and the service logs in again: an administrator reads the code in the new home and approves it. No health wait there; the old service is disabled right after the new one starts.
- **Not built.** The standalone (user-level) registrations keep their names (label `com.claudemonitor.agent.user`, unit `claude-monitor-agent.service`, task `ClaudeMonitorAgent`, the app bundle); a Windows service host name inside the program stays `cm-agent` (for an own-process service Windows ignores it).
  The run account (`cm-run`, `_cmrun`) is shared by both services and is never removed.

## Order and size

| Step | Items | Size | Risk | Needs |
|---|---|---|---|---|
| 1 | Home resolver (3.1) | small | low | nothing |
| 2 | Tier 1 display names (4) | small | low | the label prefix |
| 3 | Tool-name mapping and `--names` (2) | large (about 200 text sites) | medium | nothing |
| 4 | New plugin, both catalogs, bridge (1) | large | medium | the Claude Code spike |
| 5 | `migrate-home` (3.2) | medium | medium | test machines |
| 6 | Tier 2 service migrate (4) | large | high | built 19/10/2026 on the maintainer's yes; **not run on a real service or on macOS and Windows** |

Still outside this ADR: the release file names and the update manifest (`cm-agent-<os>-<arch>.zip`, `cm-agent-update/1`; a dual-publish plus a
manifest version for old agents), the .NET namespaces and project names (internal, no compatibility cost, one large mechanical commit), and the
`cm-agent` binary file name itself (follows the release names).
