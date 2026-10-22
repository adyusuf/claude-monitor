# Podium

Follow your AI coding sessions from the web. A small agent on each machine collects what
[Claude Code](https://claude.com/claude-code) and other harnesses report and sends it to a central API; you and your
team follow the sessions live in the browser and can send them a prompt or stop them.

## Install

`<your server>` is the address you open Podium at in the browser. Run the block for your system; it installs the agent and the Claude Code
plugin and connects the machine. If an older agent (`cm-agent`) is installed it asks before removing it, and keeps its settings and login.

**macOS**

```bash
SERVER=https://your-server
curl -fsSL "$SERVER/downloads/aipod-install.sh" | sh -s -- --server "$SERVER"
```

**Windows** (PowerShell, no administrator needed)

```powershell
$Server = 'https://your-server'
irm "$Server/downloads/aipod-install.ps1" -OutFile "$env:TEMP\aipod-install.ps1"
powershell -ExecutionPolicy Bypass -File "$env:TEMP\aipod-install.ps1" -Server $Server
```

Then restart Claude Code (or open a new session); `/aipod:status` shows how it is doing.

What the script does, in order:

1. Reads the newest build for this OS and CPU from the download host (`<your server>/downloads`, which is the R2 bucket).
2. Finds an older agent (`cm-agent`, the old `monitor-agent` plugin, an old autostart, a running old daemon). If there is one it asks
   **"Remove the old agent and install aipod `<version>`?"** (default no; "no" changes nothing).
3. Copies the old `agent.json` (server, machine identity, saved settings) and the two tokens (macOS Keychain, Windows Credential Manager)
   to the new home before removing anything, so the machine stays the same machine on the web and does not log in again. Tokens are never printed.
4. Installs the build (`~/Library/Application Support/aipod/bin`, `%USERPROFILE%\.aipod\bin`) after checking its SHA-256 against the manifest
   (and, on macOS, its code signature; the Windows build is not code-signed yet).
5. Adds the Claude Code marketplace `aipod-releases` and installs `aipod@aipod-releases`, or updates it, and removes the old `monitor-agent` plugin.
6. Connects the machine: already connected when the old login was kept, otherwise it shows a code to approve on the web.

If aipod is already installed the script only offers the update (default yes) and refreshes the plugin.

| Option (macOS / Windows) | Meaning |
|---|---|
| `--server` / `-Server` | The server address (https). Optional on a machine that was connected before. |
| `--from` / `-From` | Another download base than `<your server>/downloads`. |
| `--yes` / `-Yes` | Answer yes to the questions. |
| `--dry-run` / `-DryRun` | Say what would happen; change nothing. |
| `--remove-old` / `-RemoveOld` | Only remove the old agent; install nothing. Keeps the old data folder; `--purge` / `-Purge` deletes it too, after copying the settings. |
| `--no-plugin`, `--no-login` / `-NoPlugin`, `-NoLogin` | Skip the plugin / the connection step. |

The scripts are [`scripts/install/`](scripts/install). A boot-service agent (for servers) is a different install: the script only names the
command to remove it ([SETUP.md](SETUP.md)). The Windows script has not been run on a real host yet.

## Layout

| Path | What |
|---|---|
| `docs/adr-0002-agent-platform.md` | The architecture: agent, central API, web, phases |
| `docs/data-model.md` | The PostgreSQL data model |
| `docs/release.md`, `docs/security.md` | Promotion and rollback; security notes |
| `scripts/gate-core.sh`, `scripts/merge-gate.sh` | The merge gate: tests, line coverage ≥ 80% per codebase, secret scan, SAST |

Design: [ADR-0002](docs/adr-0002-agent-platform.md), [data model](docs/data-model.md). Setup for development and servers: [`SETUP.md`](SETUP.md).

## The gate

```bash
bash scripts/merge-gate.sh dev      # everything CI runs, locally
```

## License

[MIT](LICENSE)
