# Podium

Follow your AI coding sessions from the web. A small agent on each machine collects what
[Claude Code](https://claude.com/claude-code) and other harnesses report and sends it to a central API; you and your
team follow the sessions live in the browser and can send them a prompt or stop them.

## Install

Run the block for your system and environment. It installs the agent and the Claude Code plugin and connects the machine; everything is
downloaded from the download host (R2), not from the web site. If an older agent (`cm-agent`) is installed it asks before removing it, and
keeps its settings and login.

| Environment | Web site (`--server`) | Download host (`--from`) |
|---|---|---|
| Test | `https://testpodium.bitreka.com` | `https://testaipods.bitreka.com/pods/test` |
| Live | `https://podium.bitreka.com` | `https://aipods.bitreka.com/pods/prod` |

**macOS, test**

```bash
curl -fsSL https://testaipods.bitreka.com/pods/test/aipod-install.sh | sh -s -- --server https://testpodium.bitreka.com --from https://testaipods.bitreka.com/pods/test
```

**macOS, live**

```bash
curl -fsSL https://aipods.bitreka.com/pods/prod/aipod-install.sh | sh -s -- --server https://podium.bitreka.com --from https://aipods.bitreka.com/pods/prod
```

**Linux, test** (the same script as macOS; `curl`, `unzip` and `sh` are needed)

```bash
curl -fsSL https://testaipods.bitreka.com/pods/test/aipod-install.sh | sh -s -- --server https://testpodium.bitreka.com --from https://testaipods.bitreka.com/pods/test
```

**Linux, live**

```bash
curl -fsSL https://aipods.bitreka.com/pods/prod/aipod-install.sh | sh -s -- --server https://podium.bitreka.com --from https://aipods.bitreka.com/pods/prod
```

**Windows, test** (PowerShell, no administrator needed)

```powershell
irm https://testaipods.bitreka.com/pods/test/aipod-install.ps1 -OutFile "$env:TEMP\aipod-install.ps1"; powershell -ExecutionPolicy Bypass -File "$env:TEMP\aipod-install.ps1" -Server https://testpodium.bitreka.com -From https://testaipods.bitreka.com/pods/test
```

**Windows, live**

```powershell
irm https://aipods.bitreka.com/pods/prod/aipod-install.ps1 -OutFile "$env:TEMP\aipod-install.ps1"; powershell -ExecutionPolicy Bypass -File "$env:TEMP\aipod-install.ps1" -Server https://podium.bitreka.com -From https://aipods.bitreka.com/pods/prod
```

**Template** (any environment: fill in your two addresses)

```bash
SERVER=https://your-site                                  # the web address you open in the browser
DOWNLOADS=https://your-download-host/your-folder          # the download host's folder for this environment (R2)
curl -fsSL "$DOWNLOADS/aipod-install.sh" | sh -s -- --server "$SERVER" --from "$DOWNLOADS"
```

```powershell
$Server = 'https://your-site'
$Downloads = 'https://your-download-host/your-folder'
irm "$Downloads/aipod-install.ps1" -OutFile "$env:TEMP\aipod-install.ps1"
powershell -ExecutionPolicy Bypass -File "$env:TEMP\aipod-install.ps1" -Server $Server -From $Downloads
```

Add `--dry-run` (Windows: `-DryRun`) to see what it would do without changing anything. Then restart Claude Code (or open a new session);
`/aipod:status` shows how it is doing.

What the script does, in order:

1. Reads the newest build for this OS and CPU from the download host (`manifest.json`).
2. Finds an older agent (`cm-agent`, the old `monitor-agent` plugin, an old autostart, a running old daemon). If there is one it asks
   **"Remove the old agent and install aipod `<version>`?"** (default no; "no" changes nothing).
3. Copies the old `agent.json` (server, machine identity, saved settings) and the two tokens (macOS Keychain, Windows Credential Manager, the `cred-*` files on Linux)
   to the new home before removing anything, so the machine stays the same machine on the web and does not log in again. Tokens are never printed.
4. Installs the build (`~/Library/Application Support/aipod/bin` on macOS, `~/.aipod/bin` on Linux, `%USERPROFILE%\.aipod\bin` on Windows) after checking its SHA-256 against the manifest
   (and, on macOS, its code signature; the Linux and Windows builds are not code-signed yet).
5. Adds the Claude Code marketplace `aipod-releases` and installs `aipod@aipod-releases`, or updates it, and removes the old `monitor-agent` plugin.
6. Connects the machine: already connected when the old login was kept, otherwise it shows a code to approve on the web.

If aipod is already installed the script only offers the update (default yes) and refreshes the plugin.

| Option (macOS, Linux / Windows) | Meaning |
|---|---|
| `--server` / `-Server` | The server address (https). Optional on a machine that was connected before. |
| `--from` / `-From` | The download host's folder for the environment (above). Without it the script asks the web site's `/downloads`, which redirects there. |
| `--yes` / `-Yes` | Answer yes to the questions. |
| `--dry-run` / `-DryRun` | Say what would happen; change nothing. |
| `--remove-old` / `-RemoveOld` | Only remove the old agent; install nothing. Keeps the old data folder; `--purge` / `-Purge` deletes it too, after copying the settings. |
| `--no-plugin`, `--no-login` / `-NoPlugin`, `-NoLogin` | Skip the plugin / the connection step. |

The scripts are [`scripts/install/`](scripts/install). A boot-service agent (for servers) is a different install: the script only names the
command to remove it ([SETUP.md](SETUP.md)). The Linux path is covered by the same script and its tests only on macOS so far (the token-file branch is exercised by the test's file store, not on a Linux host), and the Windows script has not been run on a real host yet.

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
