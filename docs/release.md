# Release and rollback

## Promotion

`dev` -> `test` -> `prod`, each a `--no-ff` merge. **The owner of the full gate is CI**: `.github/workflows/ci.yml` runs
`scripts/gate-core.sh dev` on every push to `dev`, `test` and `prod` (free minutes, public repository), so a promotion
waits for CI to be green on the commit being promoted, not for a second local run:

```bash
bash scripts/quick-check.sh --tests        # locally, before the merge: format, build, shellcheck, secret scan, script tests,
                                           # and only the test projects the change reaches. A first look, not the gate.
bash scripts/ci-status.sh dev              # the merge is allowed when CI is green for dev's tip (exit 0); 3 = still running,
bash scripts/ci-status.sh test             # 4 = it never ran, 1 = red, 5 = gh could not answer: none of those is a pass
bash scripts/ci-status.sh test <sha> --workflow e2e   # the optional e2e workflow, if one ran
```

**Before every merge the maintainer is asked** (global rule): "Will CI and e2e run for this merge?", two separate answers,
and nothing is merged before they come. A "yes" for CI means waiting for it to be green (on `dev` before `test`, on `test`
before `prod`). A "no" is recorded, never silent: the merge commit message carries a line naming only what was skipped,

```
Gate-Waived: ci, e2e (neden: <the maintainer's words>)
```

and nothing is waived without that question and that line. `bash scripts/waived-gates.sh origin/prod origin/test` lists
the lines in a range (exit 1 when there are any, "no record" otherwise). **Before preparing a prod merge, run it for
`origin/prod origin/test` (and again for `origin/test origin/dev` if the promotion spans both) and tell the maintainer
which commits ran without which gate; the prod merge waits for their answer.** The prod push stays the maintainer's.
Coverage >= 80 % per codebase (global #29) is part of CI's gate and is never waived by any of this.

The `test` -> `prod` gate (`scripts/merge-gate.sh prod`) also needs the full e2e suite green (global #33)
only when `GATE_RUN_E2E=1`; otherwise it warns that e2e was not run. Every prod promotion is tagged, so a rollback
target has a name:

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

Every zip holds the one single-file `aipod` (`cm-agent.exe` on Windows), published uncompressed: never switch
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
  `cm-agent-linux-arm64.zip` join the downloads (and the web's "Get the agent" page); `aipod install --service`
  registers a boot service on Linux, macOS and Windows Server. Old agents keep working but never report an exec level,
  so they are never a target of remote work. The API needs the usual deploy to test, then prod, **before** the agents:
  a new agent on an old API gets 404 and switches the feature off.

- **Windows agent home moved** (`fix/agent-home-outside-appdata`): the default is now `%USERPROFILE%\.claude-monitor`,
  not `%LOCALAPPDATA%\ClaudeMonitor` (the Claude desktop app's MSIX packaging redirects AppData, ADR-0002). On first
  start the new agent copies `agent.json`; nothing else moves, and events not yet uploaded are lost. **Installs made
  before this release keep using the old binary and folder until `aipod install` is run again** (SETUP.md, "The
  agent"). `aipod status` now prints `home:`. A new agent version also needs a deploy to test, then prod
  (`docs/deploy-windows.md`).

- **Commands from the web, honest status** (`fix/idle-session-commands`): the web now says what a waiting command waits
  for, how long is left, and why an expired one lapsed, and warns above the prompt box when a session is idle (a prompt
  is applied only when something is typed or a turn ends). The agent gains `aipod install --stop-wait <seconds>`
  (0-590, default 0; re-run `install` to rewrite the hooks) and `aipod status` prints `commands waiting` and
  `stop wait`. No API change. Web and agent both need the usual deploy to test, then prod.

- **Claude Code plugin** (`feature/inbox-push`, ADR-0007): `bash scripts/build-agent.sh` now also writes `plugin/monitor-agent/agent-release.txt`
  (the version and the SHA-256 of every zip; the digests of one version accumulate across environments, `scripts/write_plugin_pins.py <dir> <version> --replace` starts over).
  **Commit that file with the release**: the plugin's bootstrap installs exactly the zips it pins and refuses
  anything else, so a plugin published before its pins names no build and installs nothing. Order: build -> commit the pins -> promote; the zips must be in the
  environment's `/downloads` before the plugin version that pins them is published. To serve the plugin from the environment's own site instead of the repository, set `AGENT_SITE_URL=https://<site>` for `build-agent.sh` (it writes `aipod-marketplace.json` + `aipod-plugin.zip` and the old pair `marketplace.json` + `monitor-agent-plugin.zip` into `/downloads`, ADR-0007 section 9.2 and ADR-0013 section 1; the pins file is written for both plugin folders) and `VITE_PLUGIN_FROM_SERVER=1` for the web build. Otherwise set `VITE_PLUGIN_MARKETPLACE` for the web build of the environment
  (`<owner>/<repo>#test` or `#prod`). When a hook is added or changed in the agent, run `aipod plugin-files .` and commit the result (a test fails otherwise).
  `--push inbox` pushes through the session's inbox (no start-up flag); `--push on` and `--push channel` are the old channel, unchanged.

## Download host

The agent builds can be published on a download host instead of each server's own `/downloads`: an R2 bucket behind a public domain (the domain is a deployment value, never in the repository).

- **Publish:** `AGENT_UPDATE_CHANNEL=<channel> bash scripts/build-agent.sh`, then `AGENT_R2_BUCKET=<bucket> bash scripts/publish_agent_downloads.sh <channel>` (wrangler, the maintainer's own login). It checks `SHA256SUMS` and that the manifest is signed for that channel,
  uploads the files to `daemons/<version>/<channel>/` (kept, cached for good) and then to `daemons/<channel>/` (`no-cache`), and the manifest last.
- **Point a server at it:** `MONITOR_DOWNLOADS_URL=https://<downloads host>/daemons/<channel>` in that server's `monitor.env` (test: `.../daemons/test`, prod: `.../daemons/prod`). The API reads `manifest.json` from there once a minute, keeps the last good copy when the host is down, and
  offers `<url>/<file>`. `/downloads/<file>` on the server answers 302 to the host when the server does not hold the file itself, so the web's "Get the agent" page, old links and the plugin's bootstrap keep working. Unset, nothing changes.
- **Trust:** the address is not what makes a build trusted; the agent still verifies the signed SHA-256 against its built-in channel key. It will fetch from the server's own `/downloads/` or from an https address on a public host name (default port, no credentials or query, a plain file name, no redirect); anything else is refused (`bad_url`).
- Channels stay apart by folder: a test agent carries the test key and only the `test` manifest is signed for it. Never point the prod server at `daemons/test`.

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
