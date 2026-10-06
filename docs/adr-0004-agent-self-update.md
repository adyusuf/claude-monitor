# ADR-0004: The agent updates itself

**Status:** Accepted (06/10/2026). Extends [ADR-0002](adr-0002-agent-platform.md); the agent's `/downloads` folder and
minimum-version gate (426) are from there.
**Decider:** the maintainer asked for it; the trust model below was chosen so that a compromised server cannot
push code to the machines.

## Context

A new agent version reached a machine only when someone rebuilt or downloaded it and ran `cm-agent install` again. The
API can refuse an agent that is too old (426), but gave it no way to get a newer one. An update path is also the most
dangerous thing an agent can have: it replaces a program that runs with the user's rights on every Claude Code session. Every
decision below is made so that the path is **off until someone turns it on**, **cannot be abused by the server**, and
**can be undone by itself**.

## Decision

### (a) Default off; the lower of the machine and the workspace wins

Three modes, `off | check | on`:

- **Machine:** `cm-agent config auto-update off|check|on` (saved in `agent.json`) or `CM_AUTO_UPDATE` (wins when set). Default `off`.
- **Workspace cap:** the workspace setting `agentUpdate` (default `off`), which the daemon reads with the settings it already fetches.
- **The lower applies** (`UpdateModes.Lower`): `check` looks and reports, `on` also installs. Anything unknown counts as `off` (fail-closed, global #6).
- **No network call at all while the effective mode is `off`:** the daemon's update chore returns before it asks the server anything.
- **An explicit `cm-agent update` is the person's own decision and ignores both settings.** Only the daemon's own rounds obey them.

Why: a machine's owner and a workspace's admin both have a stake in what runs on the machine; neither may switch it on over
the other's head. A default of off keeps every existing agent byte-for-byte what it was (global #4). Rejected: a workspace
setting that forces `on` (the machine's owner could not refuse code), and a single global switch (no per-machine control).

### (b) Trust: a manifest signed offline

- Each build is described by a manifest signed **offline** with **ECDSA P-256 / SHA-256** (DER signature, public key as base64
  SubjectPublicKeyInfo). The signed text is `UpdateManifest.Payload`: format line, `channel`, `version`, `os`, `arch`,
  `sha256` (of the zip), `minSupported`, one field per line. `scripts/sign_manifest.py` builds the same text; a known-answer test
  in both test suites keeps them equal.
- **The server only relays** the signed entry. It cannot sign and cannot change a field without breaking the signature.
- **The PUBLIC key is built into the agent** at build time: `deploy/update-keys/<channel>.pub`, passed by `scripts/build-agent.sh` as the
  `UpdateChannel` and `UpdatePublicKey` assembly metadata. A build without a channel has no key and refuses every update (`no-key`).
- **The PRIVATE keys** (one per channel, `test` and `prod`) live **only** in the maintainer's macOS Keychain (service
  `cm-agent-update-<channel>`). `scripts/sign_manifest.py keygen` creates one without the key touching a file or a command
  line (it goes to `security -i` and `openssl` over standard input), and refuses to overwrite an existing one. They are never in the repository, in CI or on a server.
- **An agent trusts only its own channel's key and requires the offer's channel to equal its own.** A prod agent can therefore never take a test
  build, even from a misconfigured server.

Why ECDSA P-256 and not Ed25519: the .NET base library has no Ed25519 (verified when this was decided), and a crypto package is a new
dependency that needs approval (global #10). P-256 is in the BCL on both OSes and in OpenSSL/LibreSSL for the signing script.

Rejected: trusting TLS and the server alone (a server compromise would then ship code to every machine); signing in CI (the
private key would sit in a place many people and jobs can read); one key for both channels (a test-signing mistake could then reach prod).

**Consequence:** if the `prod` private key is lost or leaks, installed prod agents cannot be updated safely and must be reinstalled by hand. Rotation
is a new key, a new build with the new public key, installed manually on each machine. A spare "next" key, trusted in advance so a
rotation could be signed with the old one, was considered and not built (small step, global #1).

### (c) Checks before anything is replaced

All of these must hold. Every refusal is written to `agent.log` and shown by `cm-agent status` (`last update check: <code> (<reason>)`); none is silent.

1. The signature verifies against this agent's own key for its own channel, OS and CPU (`bad-signature`, `channel`, `malformed`).
2. The offered version is **strictly higher** than the running one (`downgrade`): a replayed old manifest is refused.
3. The download is from **the server's own origin**, under `/downloads/`, and is **not redirected** (`bad-url`).
4. The zip's SHA-256 equals the signed one (`hash-mismatch`).
5. Size caps: download 250 MB, extracted binary 400 MB (`too-large`).
6. The zip holds **exactly one file**, with the binary's own name (`bad-archive`).
7. The candidate, run with `version`, prints the offered version (`bad-binary`).
8. macOS: `codesign --verify --strict` passes (`bad-binary`). An ad-hoc signature is accepted; the Developer ID signature and notarisation come from `scripts/build-agent.sh` on the Mac. A file the agent downloads itself carries no quarantine attribute, so Gatekeeper does not look at it; the code-signature check is made anyway.

**Known limits.** A withholding or freeze attack (the server never offers a newer build, or keeps offering the current one) cannot be detected by the
agent. The manifest is not time-limited: any validly signed build that is newer than the installed one stays acceptable for ever. Both are accepted for now.

### (d) Replacement and rollback

There is no OS service (the hooks start the daemon on demand), so the update does its own process control:

- **Stage:** the verified binary is written beside the installed one as `<bin>.new`, so the final rename never crosses a volume.
- **macOS:** the old binary is copied to `<bin>.prev`, then `<bin>.new` is renamed over the binary (atomic).
- **Windows:** a running exe cannot be overwritten but can be renamed: the running exe is renamed to `<bin>.prev`, then `<bin>.new` is moved in. If an old `.prev` is locked and cannot be deleted it is renamed `<bin>.old<id>` and deleted at the next daemon start.
- **Stopping the daemon:** an update writes a stop-request file (`daemon.stop`); the daemon sees it, exits, and "stopped" means its exclusive lock is free. The new daemon is started from the installed binary.
- **Health:** the update counts only when the new daemon reports its version and the API has answered it since the swap, within `UpdateHealthWait` (90 s). Otherwise the previous binary is **restored automatically** and the version is **blocked from automatic retry** (`update-state.json`, `BlockedVersion`).
- **One previous version (`.prev`) is always kept.**
- **Hooks** run the file at the fixed path, so replacing it in place is enough. **MCP processes** (one per Claude Code session) keep running the old version until their session ends; they are **not** killed.

Why not a service manager or a helper updater: the agent registers nothing with the OS (SETUP.md, "The agent"), and a second program
to update the first would be a second thing to sign, trust and keep alive.

### (e) API (additive, global #4)

- `GET /api/agent/latest?os=<macos|windows>&arch=<arm64|x64>`, agent token. It is **deliberately outside the 426 minimum-version gate**, so an
  agent below the minimum can still fetch its update. Answer: `{version, url, sha256, signature, minSupported, channel}`; `404 no_update` when nothing is published for that OS and CPU.
- The `url` is built from the server's own public origin and `downloads/manifest.json`, written by `scripts/sign_manifest.py` at release. **Channel separation therefore follows the deploy:** a server offers whatever its web root holds.
- The machine list rows gain `latestVersion` and `updateAvailable`.
- Workspace settings gain `agentUpdate` (EF migration `AgentUpdateSetting`, column `agent_update`, default `off`, CHECK constraint `off|check|on`; set by a workspace admin or owner, an unknown value is `invalid_mode`, the change is audited with before and after); `AgentSettings`, which the agent reads, gains `agentUpdate`. Old clients ignore the new fields.

### (f) `/downloads` is its own static mount

The prerequisite fix. `SHA256SUMS` has no extension, was an "unknown type" to the default static mount, fell through to the single-page app and came back
as `index.html`, so a download could not be verified. `/downloads` is now its own mount: every file is served as what it is (`SHA256SUMS` as `text/plain`, others as
`application/octet-stream` by default), no-cache, a missing file is a 404, and the SPA fallback does not cover `/downloads/`.

### (g) Not decided here

Updating Claude Code itself (the harness the agent reports on) is **not part of this ADR**. It affects sessions that are running, which an agent update does
not, and is tracked separately.

## Consequences

- With everything at its default nothing changes: no update call, no new behaviour. A person can run `cm-agent update --check` and `cm-agent update` at any time.
- Releasing becomes a signing step on the maintainer's Mac (SETUP.md, "Updating the agent"). Two long-lived private keys exist and are on the secret inventory.
- A prod agent cannot be moved to the test channel or back by any server. Changing a machine's channel is a manual reinstall of a build of the other channel.
- The version gate (426) and the update path now complement each other: an agent below the minimum is refused at the API but can still reach `/api/agent/latest`.
