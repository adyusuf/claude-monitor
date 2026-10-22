# Podium

Follow your AI coding sessions from the web. A small agent on each machine (macOS and Windows) collects
what [Claude Code](https://claude.com/claude-code) and other harnesses report and sends it to a central
API; you and your team follow the sessions live in the browser and can send them a prompt or stop them.

**Status: being rebuilt.** The design is decided ([ADR-0002](docs/adr-0002-agent-platform.md),
[data model](docs/data-model.md)); the code arrives in phases. The earlier local live board (a Python
server and a desktop window on one machine) is removed; its last release is the tag `archive/board-final`.

## Layout

| Path | What |
|---|---|
| `docs/adr-0002-agent-platform.md` | The architecture: agent, central API, web, phases |
| `docs/data-model.md` | The PostgreSQL data model |
| `docs/release.md`, `docs/security.md` | Promotion and rollback; security notes |
| `scripts/gate-core.sh`, `scripts/merge-gate.sh` | The merge gate: tests, line coverage ≥ 80% per codebase, secret scan, SAST |

## Install aipod

One script per OS puts the agent and its Claude Code plugin on a machine and connects it. It reads the newest signed build from the
download host (`<server>/downloads`, which is the R2 bucket), so `<server>` below is the address you open in the browser.

```bash
curl -fsSL <server>/downloads/aipod-install.sh | sh -s -- --server <server>          # macOS and Linux
```

```powershell
irm <server>/downloads/aipod-install.ps1 -OutFile aipod-install.ps1                   # Windows
powershell -ExecutionPolicy Bypass -File .\aipod-install.ps1 -Server <server>
```

What it does, in order:

1. **Looks at the download host** for the newest build of this OS and CPU (`manifest.json`).
2. **Finds an older agent** (`cm-agent`, the old `monitor-agent` Claude Code plugin and marketplace, an old autostart, a running old daemon).
   If there is one it asks **"Remove the old agent and install aipod `<version>`?"** (default no). "No" changes nothing.
3. **Keeps the old settings and connection.** `agent.json` (server, machine identity, saved settings) and the two tokens (macOS Keychain,
   Windows Credential Manager, or the `cred-*` files on Linux) are copied to the new home before anything is removed, so the machine stays the
   same machine on the web and does not log in again. Tokens are never printed.
4. **Installs the build** into the agent home (`~/Library/Application Support/aipod/bin`, `~/.aipod/bin`, `%USERPROFILE%\.aipod\bin`), after
   checking its SHA-256 against the manifest (and, on macOS, its code signature). The Windows build is not code-signed yet.
5. **Sets up the Claude Code plugin**: adds the marketplace `aipod-releases` and installs `aipod@aipod-releases`, or updates it when it is
   already there, and removes the old `monitor-agent` plugin.
6. **Connects the machine**: when the old login was kept it is already connected; otherwise it shows a code to approve on the web. A new
   machine gets the inbox push (web messages start a turn in an idle session); a saved choice is never changed.

Then restart Claude Code (or open a new session); `/aipod:status` shows how it is doing. If aipod is already installed, the script only offers
the update (default yes) and refreshes the plugin.

| Option (`.sh` / `.ps1`) | Meaning |
|---|---|
| `--server` / `-Server` | The server address (https). Optional on a machine that was connected before: it is read from `agent.json`. `AIPOD_SERVER` also works. |
| `--from` / `-From` | Another download base than `<server>/downloads` (https, or `file:///` for a local copy). |
| `--yes` / `-Yes` | Answer yes to the questions. Without a terminal and without this, the removal question is answered no. |
| `--dry-run` / `-DryRun` | Say what would happen; change nothing (the build is not downloaded). |
| `--no-plugin`, `--no-login` / `-NoPlugin`, `-NoLogin` | Skip the plugin / the connection step. |
| `--remove-old`, `--purge` / `-RemoveOld`, `-Purge` | See below. |

The scripts live in [`scripts/install/`](scripts/install) and are published with every release beside the zips. They need no administrator
rights. An agent installed as a boot service (`install --service`, for servers) is a different install: the script names it and the command to
remove it (`sudo cm-agent uninstall --service`, or the same in an administrator PowerShell); install that kind with `aipod install --service`
([SETUP.md](SETUP.md)). Windows has not been run on a real host yet (the script parses and refuses to run elsewhere in the tests).

### Remove an old agent

The removal on its own (nothing is installed):

```bash
curl -fsSL <server>/downloads/aipod-install.sh | sh -s -- --remove-old            # macOS and Linux
```

```powershell
powershell -ExecutionPolicy Bypass -File .\aipod-install.ps1 -RemoveOld           # Windows, after downloading the script as above
```

It stops the old daemon, removes the old autostart, uninstalls the `monitor-agent` plugin and its marketplaces from Claude Code, and deletes
the old `cm-agent` binaries. It **keeps the old data folder** (`ClaudeMonitor` / `.claude-monitor`) and the old keychain entries, so nothing
is lost if you change your mind. `--purge` (`-Purge`) also deletes those, but only after copying the settings and tokens to the new home.
Sessions that were open during the removal may report a hook error once; a new session does not.

## The gate

```bash
bash scripts/merge-gate.sh dev      # everything CI runs, locally
```

Setup: [`SETUP.md`](SETUP.md).

## License

[MIT](LICENSE)
