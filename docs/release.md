# Release and rollback

## Promotion

`dev` -> `test` -> `prod`, each a `--no-ff` merge after `scripts/merge-gate.sh <target>` is green; CI runs the
same gate on every push (`.github/workflows/ci.yml`). The `test` -> `prod` gate also needs the full e2e suite
green (global #33); until the web app and its suite exist (ADR-0002, phase 3) that step reports "skipped" and
the prod gate is not green. Every prod promotion is tagged, so a rollback target has a name:

```bash
git tag -a prod-DD-MM-YYYY -m "<one line: what shipped>" <sha> && git push origin prod-DD-MM-YYYY
```

A second promotion on the same day appends `-2`. A hotfix says so in the tag message.

## Rebuild in progress: `prod` stays on the old board until the cut-over

`prod` still holds the removed local board, and the maintainer's machine runs it from a clone checked out on
`prod`. The phases of ADR-0002 therefore reach `dev` (and `test`), but **not `prod`, until the cut-over
(phase 5)**, when the agent replaces the old hooks. The old board's last release is the tag `archive/board-final`.

## Agent artifacts and verification

`bash scripts/build-agent.sh [out-dir]` (default `out/downloads`) writes one zip per platform plus `SHA256SUMS`, the
manifest the deploy copies to the web root's `/downloads`:

| File | Platform | Code-signed | In the update `manifest.json` (ADR-0004) |
|---|---|---|---|
| `cm-agent-macos-arm64.zip`, `cm-agent-macos-x64.zip` | macOS | Developer ID, hardened runtime, notarised (when `AGENT_SIGN_IDENTITY` / `AGENT_NOTARY_PROFILE` name them) | yes |
| `cm-agent-windows-x64.zip`, `cm-agent-windows-arm64.zip` | Windows | **no** | yes |
| `cm-agent-linux-x64.zip`, `cm-agent-linux-arm64.zip` | Linux (systemd) | **no** | yes |

With `AGENT_UPDATE_CHANNEL` set, `scripts/sign_manifest.py` signs an update entry for every one of these six zips, so
an interactive agent on any of the three systems can update itself. A service agent cannot (below).

Every zip holds the one single-file `cm-agent` (`cm-agent.exe` on Windows), published uncompressed: never switch
`EnableCompressionInSingleFile` on (it crashes the daemon on macOS, see the csproj). The published-agent smoke test
(`scripts/agent_smoke.py`) runs the binary of the host it runs on: the macOS build on a Mac, the Linux build on a
Linux host. The Windows and Linux builds are only smoke-tested on a host of their own OS.

**Linux and Windows binaries are unsigned, so the SHA-256 in `SHA256SUMS` is what proves what an admin installs**
(ADR-0005: a service agent is upgraded by an admin; its binary is admin-owned, so the self-update of ADR-0004 cannot replace it).
Before a service upgrade the admin downloads the zip **and** `SHA256SUMS` over HTTPS from the same address and checks
the digest on the machine, not from a copy made elsewhere; see [`remote-work-setup.md`](remote-work-setup.md),
"Upgrading". A mismatch means: do not install, report it.

```bash
sha256sum --check --ignore-missing SHA256SUMS      # Linux  (macOS: shasum -a 256 -c SHA256SUMS)
```

```powershell
(Get-FileHash .\cm-agent-windows-x64.zip -Algorithm SHA256).Hash   # compare with the line in SHA256SUMS
```

## Agent releases: notes

- **Linux agents update themselves** (`fix/linux-self-update`, ADR-0004 amendment): `/api/agent/latest` accepts
  `os=linux` and the Linux zips are signed into `manifest.json`. Deploy the API first: an older API answers a Linux
  agent's check with 400 (`invalid_os`), which the agent records as `unreachable` and installs nothing.

- **Linux agent and service mode** (`feature/remote-work`, ADR-0005): `cm-agent-linux-x64.zip` and
  `cm-agent-linux-arm64.zip` join the downloads (and the web's "Get the agent" page); `cm-agent install --service`
  registers a boot service on Linux, macOS and Windows Server. Old agents keep working but never report an exec level,
  so they are never a target of remote work. The API needs the usual deploy to test, then prod, **before** the agents:
  a new agent on an old API gets 404 and switches the feature off.

- **Windows agent home moved** (`fix/agent-home-outside-appdata`): the default is now `%USERPROFILE%\.claude-monitor`,
  not `%LOCALAPPDATA%\ClaudeMonitor` (the Claude desktop app's MSIX packaging redirects AppData, ADR-0002). On first
  start the new agent copies `agent.json`; nothing else moves, and events not yet uploaded are lost. **Installs made
  before this release keep using the old binary and folder until `cm-agent install` is run again** (SETUP.md, "The
  agent"). `cm-agent status` now prints `home:`. A new agent version also needs a deploy to test, then prod
  (`docs/deploy-windows.md`).

- **Commands from the web, honest status** (`fix/idle-session-commands`): the web now says what a waiting command waits
  for, how long is left, and why an expired one lapsed, and warns above the prompt box when a session is idle (a prompt
  is applied only when something is typed or a turn ends). The agent gains `cm-agent install --stop-wait <seconds>`
  (0-590, default 0; re-run `install` to rewrite the hooks) and `cm-agent status` prints `commands waiting` and
  `stop wait`. No API change. Web and agent both need the usual deploy to test, then prod.

## Rollback

- **A change on `dev` or `test`:** a revert commit through the gate, like any other change. Nothing is ever
  force-pushed.
- **The old board:** `git switch --detach archive/board-final` in the clone the hooks run.
- **The hosted API and database:** `deploy/windows/deploy.ps1` rolls back by itself when the new release does not
  report its commit; a later rollback deploys the previous package again (`docs/deploy-windows.md`). Migrations are
  additive only (global #4), so an older API runs on a newer schema and no data is reversed.

## Flags

There is no feature flag yet. A switch ships as a configuration value read by the tier's one configuration
module (global #2).
