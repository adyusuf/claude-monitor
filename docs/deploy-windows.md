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
3. **Per environment**, from an elevated PowerShell in `deploy\windows` of the release package or the repository:

   ```powershell
   .\setup-server.ps1 -Environment test -HostName <test host> -CertThumbprint <thumbprint> -LoopbackPort 8081 -SmtpUser <sender> -SmtpFrom <sender>
   .\setup-server.ps1 -Environment prod -HostName <prod host> -CertThumbprint <thumbprint> -LoopbackPort 8080 -SmtpUser <sender> -SmtpFrom <sender>
   ```

   Then fill in `C:\ClaudeMonitor\<env>\monitor.env`: the Gmail app password, the GitHub and Google OAuth client
   ids and secrets (one OAuth app per environment; callbacks `https://<host>/api/auth/callback/github` and
   `.../google`), and `MONITOR_BACKUP_OFFSITE` (a folder on another machine). Copy `MONITOR_BACKUP_KEY` into the
   password manager: without it no backup can be restored.

## Every release

On the Mac, from a clean checkout of the commit to ship:

```bash
AGENT_SIGN_IDENTITY="<Developer ID Application identity>" AGENT_NOTARY_PROFILE=claude-monitor-notary bash scripts/build-agent.sh
bash scripts/build-release.sh                      # out/claude-monitor-<commit>.zip
```

Copy the zip to the server and deploy, test first:

```powershell
.\deploy.ps1 -Environment test -Package C:\Temp\claude-monitor-<commit>.zip -LoopbackPort 8081
```

The script applies the migrations, switches the site, and checks `/api/version` reports the commit; if not, it
switches back and fails. Then the `test -> prod` gate runs the e2e suite against the test environment
(`E2E_BASE_URL=https://<test host>`), and only a green gate deploys the same zip to prod (`-Environment prod
-LoopbackPort 8080`).

## Backups and the restore drill

- `setup-server.ps1` registers `ClaudeMonitor-backup-<env>`, daily at 03:30: the database and the event archives,
  encrypted, 30 days locally and a copy in `MONITOR_BACKUP_OFFSITE`.
- A failed backup writes an **error** to the Application event log (source `ClaudeMonitor`, event 1001). Alert on it,
  and on a missing daily success event 1000: a backup that never ran is alarmed separately (global #18).
- **Once a month**: `.\restore-drill.ps1 -Environment prod`. It restores the newest backup into a scratch database,
  checks it and records the result in `backups\restore-drills.log`; a failure is event 1011.

## Rollback

`deploy.ps1` keeps every release under `releases\<commit>`. To go back, deploy the previous package again (or point the
site at the previous folder in IIS and recycle its pool). Migrations only add (global #4), so an older release runs on
the newer database.
