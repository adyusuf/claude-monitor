# Running test and production on Windows Server

The maintainer's server runs Windows Server without Docker (ADR-0002). Both environments live on it, each with its own
IIS site, app pool, PostgreSQL database and folder under `C:\iis\Podium\<env>`. Cloudflare proxies both host names
(orange cloud); the server holds a Cloudflare Origin certificate. The host names are not in this repository.

## Once per server

1. **Windows features and runtimes**
   - IIS (the setup script installs the role if missing).
   - The **.NET 10 ASP.NET Core Hosting Bundle** (dotnet.microsoft.com, ".NET 10", "Hosting Bundle"); restart IIS after it.
   - **PostgreSQL 18** for Windows (postgresql.org, Windows installer), listening on 127.0.0.1:5432 only. Keep the
     `postgres` superuser password in the password manager; the scripts ask for it, never store it.
2. **Cloudflare**
   - DNS: an A record for each host name to the server, proxied (orange cloud). SSL/TLS mode **Full (strict)**.
   - SSL/TLS, Origin Server: create an Origin Certificate for both host names (15 years), download certificate and key,
     and import them into `LocalMachine\My` on the server (as a .pfx: `certutil -mergepfx` or the Certificates MMC).
     Note its thumbprint.
   - The server's firewall accepts 443 only from Cloudflare's address ranges (cloudflare.com/ips). The same ranges
     go into `AIPOD_PROXY_NETWORKS` (the set-up script writes them): forwarded client addresses are believed only from
     there. Cloudflare changes the list rarely; when it does, update both and recycle the app pool.
   - Turn on BitLocker for the drive that holds PostgreSQL's data, the archives and the backups (encryption at rest).
3. **Per environment**, from an elevated **Windows PowerShell 5.1** (`powershell.exe`, not `pwsh`: PowerShell 7 loads
   WebAdministration through its compatibility layer, without the `IIS:` drive the scripts use) in `deploy\windows` of
   the release package or the repository:

   ```powershell
   .\setup-server.ps1 -Environment test -HostName <test host> -CertThumbprint <thumbprint> -LoopbackPort 8081 -SmtpUser <sender> -SmtpFrom <sender>
   .\setup-server.ps1 -Environment prod -HostName <prod host> -CertThumbprint <thumbprint> -LoopbackPort 8080 -SmtpUser <sender> -SmtpFrom <sender>
   ```

   Then fill in `C:\iis\Podium\<env>\aipod.env`: the Gmail app password, the GitHub and Google OAuth client
   ids and secrets (one OAuth app per environment; callbacks `https://<host>/api/auth/callback/github` and
   `.../google`), and `AIPOD_BACKUP_OFFSITE` (a folder on another machine). Copy `AIPOD_BACKUP_KEY` into the
   password manager: without it no backup can be restored.

   SMTP can also be given as an `"Smtp"` node (`Host`, `Port`, `User`, `Password`, `From`, `StartTls`) in
   `releases\<commit>\api\appsettings.Production.json` on the server. A non-empty `AIPOD_SMTP_*` value in
   `aipod.env` wins over it, an empty one does not hide it. The file is not in the package, so a new release folder
   starts without it: copy it into each release, or keep the values in `aipod.env`, which every deploy carries over.

## Every release

On the Mac, from a clean checkout of the commit to ship:

```bash
AGENT_SIGN_IDENTITY="<Developer ID Application identity>" AGENT_NOTARY_PROFILE=podium-notary bash scripts/build-agent.sh
bash scripts/build-release.sh                      # out/podium-<commit>.zip
```

For agents that update themselves (ADR-0004, SETUP.md "Updating the agent"), add `AGENT_UPDATE_CHANNEL=test` or `prod` to the first command: it signs
`manifest.json` into `out/downloads`. **The package carries none of it:** the agent builds, `SHA256SUMS` and `manifest.json` are published to the download host (R2) with `scripts/publish_agent_downloads.sh test|prod` (`docs/release.md`, "Download host"), and each server's `aipod.env` names its channel there with `AIPOD_DOWNLOADS_URL`, so a `test` build reaches test and a `prod` build prod.

The packages reach the server as **GitHub releases**, one per channel: `test-DD-MM-YYYY` (pre-release) for test and
`prod-DD-MM-YYYY` for prod, each with the package, `PACKAGE.sha256`, `deploy-windows-scripts.zip`, the six agents,
`SHA256SUMS` and the channel's signed `manifest.json`. On the server, deploy from the release, never from a package
built there (the package is the same either way, it holds no agent builds): publish the release's agents to the download host from the macOS build, because only that one has the signed macOS agents and the manifest, and
agents cannot update themselves.

```powershell
gh release download test-<DD-MM-YYYY> -D C:\Temp\ap-test -p "podium-*.zip" -p PACKAGE.sha256
(Get-FileHash C:\Temp\ap-test\podium-<commit>.zip -Algorithm SHA256).Hash   # must equal PACKAGE.sha256
```

Deploy, test first (Windows PowerShell 5.1 again, elevated):

```powershell
.\deploy.ps1 -Environment test -Package C:\Temp\ap-test\podium-<commit>.zip -LoopbackPort 8081
```

The script applies the migrations, switches the site, and checks `/api/version` reports the commit; if not, it
switches back and fails. Afterwards `/downloads/manifest.json` (a 302 to the download host) must equal the release's `manifest.json`. The
`test -> prod` gate does not require the test deploy (#33); to check it, run the gate with `GATE_CHECK_TEST_DEPLOY=1`
(and `GATE_RUN_E2E=1` for the e2e suite) and `E2E_BASE_URL=https://<test host>`. Only a green gate deploys to prod,
with the **prod** release's package (`-Environment prod -LoopbackPort 8080`): a test package on prod would offer
agents signed for the wrong channel.

## Backups and the restore drill

- `setup-server.ps1` registers `Podium-backup-<env>`, daily at 03:30: the database and the event archives,
  encrypted, 30 days locally and a copy in `AIPOD_BACKUP_OFFSITE`.
- A task stores the full path of its script and its `-Root`. After the scripts or the server folder move (for example `C:\ClaudeMonitor` to `C:\iis\Podium`) run
  `repair-tasks.ps1 -Environment <env> -LoopbackPort <port>` (add `-WhatIf` first) as Administrator from the folder that holds the current scripts: it finds each task by its
  current name or the name from before the rename (`ClaudeMonitor-backup-<env>`, `ClaudeMonitor-health-<env>`), changes only its action and keeps the name.
- A failed backup writes an **error** to the Application event log (source `Podium`, event 1001).
- **The alarm:** `Podium-health-<env>` runs `check-health.ps1` every hour and mails `AIPOD_ALERT_EMAIL` when
  the API does not answer, no backup succeeded in 26 hours (a backup that never ran, global #18), a backup or drill
  failed, the ASP.NET Core module logged an error, or no restore drill succeeded in 35 days. Results are events 1019
  (ok) and 1020 (problems); a mail that cannot be sent is 1021. The mail uses the API's SMTP settings per field: a
  non-empty `AIPOD_SMTP_*` value of `aipod.env`, else the `Smtp` node of the `appsettings.Production.json` of the release `/api/version` reported, or, when the API
  does not answer, of the newest release folder that has one (`PodiumOps.psm1`, which must sit next to
  `check-health.ps1`). StartTls is read as the API reads it: on when unset, otherwise only for `true`. An unresolved
  host, port or sender is named in event 1021.
- **Once a month**: `.\restore-drill.ps1 -Environment prod`. It restores the newest backup into a scratch database,
  checks it and records the result in `backups\restore-drills.log`; a failure is event 1011.

## Rollback

`deploy.ps1` keeps every release under `releases\<commit>`. To go back, deploy the previous package again (or point the
site at the previous folder in IIS and recycle its pool). Migrations only add (global #4), so an older release runs on
the newer database.
