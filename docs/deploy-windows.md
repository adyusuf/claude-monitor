# Running test and production on Windows Server

The maintainer's server runs Windows Server without Docker (ADR-0002). Both environments live on it, each with its own
IIS site, app pool, PostgreSQL database and folder under `C:\ClaudeMonitor\<env>`. Cloudflare proxies both host names
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
     go into `MONITOR_PROXY_NETWORKS` (the set-up script writes them): forwarded client addresses are believed only from
     there. Cloudflare changes the list rarely; when it does, update both and recycle the app pool.
   - Turn on BitLocker for the drive that holds PostgreSQL's data, the archives and the backups (encryption at rest).
3. **Per environment**, from an elevated **Windows PowerShell 5.1** (`powershell.exe`, not `pwsh`: PowerShell 7 loads
   WebAdministration through its compatibility layer, without the `IIS:` drive the scripts use) in `deploy\windows` of
   the release package or the repository:

   ```powershell
   .\setup-server.ps1 -Environment test -HostName <test host> -CertThumbprint <thumbprint> -LoopbackPort 8081 -SmtpUser <sender> -SmtpFrom <sender>
   .\setup-server.ps1 -Environment prod -HostName <prod host> -CertThumbprint <thumbprint> -LoopbackPort 8080 -SmtpUser <sender> -SmtpFrom <sender>
   ```

   Then fill in `C:\ClaudeMonitor\<env>\monitor.env`: the Gmail app password, the GitHub and Google OAuth client
   ids and secrets (one OAuth app per environment; callbacks `https://<host>/api/auth/callback/github` and
   `.../google`), and `MONITOR_BACKUP_OFFSITE` (a folder on another machine). Copy `MONITOR_BACKUP_KEY` into the
   password manager: without it no backup can be restored.

   SMTP can also be given as an `"Smtp"` node (`Host`, `Port`, `User`, `Password`, `From`, `StartTls`) in
   `releases\<commit>\api\appsettings.Production.json` on the server. A non-empty `MONITOR_SMTP_*` value in
   `monitor.env` wins over it, an empty one does not hide it. The file is not in the package, so a new release folder
   starts without it: copy it into each release, or keep the values in `monitor.env`, which every deploy carries over.

## Every release

On the Mac, from a clean checkout of the commit to ship:

```bash
AGENT_SIGN_IDENTITY="<Developer ID Application identity>" AGENT_NOTARY_PROFILE=claude-monitor-notary bash scripts/build-agent.sh
bash scripts/build-release.sh                      # out/claude-monitor-<commit>.zip
```

For agents that update themselves (ADR-0004, SETUP.md "Updating the agent"), add `AGENT_UPDATE_CHANNEL=test` or `prod` to the first command: it signs
`manifest.json` into `out/downloads`, which the package carries. The server offers whatever its `downloads/` holds, so a `test` package goes to test and a `prod` package to prod.

Copy the zip to the server and deploy, test first (Windows PowerShell 5.1 again, elevated):

```powershell
.\deploy.ps1 -Environment test -Package C:\Temp\claude-monitor-<commit>.zip -LoopbackPort 8081
```

The script applies the migrations, switches the site, and checks `/api/version` reports the commit; if not, it
switches back and fails. The `test -> prod` gate does not require the test deploy (#33); to check it, run the gate
with `GATE_CHECK_TEST_DEPLOY=1` (and `GATE_RUN_E2E=1` for the e2e suite) and `E2E_BASE_URL=https://<test host>`.
Only a green gate deploys the same zip to prod (`-Environment prod
-LoopbackPort 8080`).

## Backups and the restore drill

- `setup-server.ps1` registers `ClaudeMonitor-backup-<env>`, daily at 03:30: the database and the event archives,
  encrypted, 30 days locally and a copy in `MONITOR_BACKUP_OFFSITE`.
- A failed backup writes an **error** to the Application event log (source `ClaudeMonitor`, event 1001).
- **The alarm:** `ClaudeMonitor-health-<env>` runs `check-health.ps1` every hour and mails `MONITOR_ALERT_EMAIL` when
  the API does not answer, no backup succeeded in 26 hours (a backup that never ran, global #18), a backup or drill
  failed, the ASP.NET Core module logged an error, or no restore drill succeeded in 35 days. Results are events 1019
  (ok) and 1020 (problems); a mail that cannot be sent is 1021.
- **Once a month**: `.\restore-drill.ps1 -Environment prod`. It restores the newest backup into a scratch database,
  checks it and records the result in `backups\restore-drills.log`; a failure is event 1011.

## Rollback

`deploy.ps1` keeps every release under `releases\<commit>`. To go back, deploy the previous package again (or point the
site at the previous folder in IIS and recycle its pool). Migrations only add (global #4), so an older release runs on
the newer database.
