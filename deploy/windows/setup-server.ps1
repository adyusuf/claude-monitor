<#
.SYNOPSIS
  One-time set-up of a Windows Server for Podium (ADR-0002 phase 4). Run as Administrator, once per
  environment ("test" and "prod" share one server; each gets its own site, app pool, database and folder).

.DESCRIPTION
  Before running: install the .NET 10 ASP.NET Core Hosting Bundle and PostgreSQL 18 (SETUP.md, "Server"), and import
  the Cloudflare Origin certificate into LocalMachine\My. The script then:
    1. installs the IIS role if missing and checks the ASP.NET Core module is there;
    2. creates C:\iis\Podium\<env>\ (releases, archive, backups) and its environment file aipod.env, readable by
       Administrators and the site's app pool only;
    3. creates the PostgreSQL role and database for the environment with a random password (written only to aipod.env);
    4. creates the app pool (no managed code, always running: the API has background jobs) and the HTTPS site bound to
       the host name with SNI, plus a loopback HTTP binding the deploy uses to check /api/version;
    5. registers the daily backup and the hourly health check (which mails AIPOD_ALERT_EMAIL) as scheduled tasks.
  Nothing secret is printed. Re-running is safe: what exists is left as it is.

.EXAMPLE
  .\setup-server.ps1 -Environment test -HostName <test host> -CertThumbprint <thumbprint> -LoopbackPort 8081
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [Parameter(Mandatory)] [string] $HostName,
  [Parameter(Mandatory)] [string] $CertThumbprint,
  [Parameter(Mandatory)] [int] $LoopbackPort,
  [string] $Root = "C:\iis\Podium",
  [string] $PgBin = "C:\Program Files\PostgreSQL\18\bin",
  [string] $SmtpUser = "",
  [string] $SmtpFrom = ""
)
$ErrorActionPreference = "Stop"
$site = "Podium-$Environment"
$pool = "Podium-$Environment"
$home_ = Join-Path $Root $Environment
$envFile = Join-Path $home_ "aipod.env"

function New-Secret([int] $bytes = 24) {
  # RandomNumberGenerator::Fill is .NET Core only; this script runs on Windows PowerShell 5.1 (.NET Framework).
  $b = New-Object byte[] $bytes; $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($b) } finally { $rng.Dispose() }
  return [Convert]::ToBase64String($b).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

Write-Host "1. IIS and the ASP.NET Core module"
if (-not (Get-WindowsFeature Web-Server).Installed) { Install-WindowsFeature Web-Server -IncludeManagementTools | Out-Null }
if (-not (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")) {
  throw "The ASP.NET Core module is missing: install the .NET 10 Hosting Bundle first (SETUP.md, Server)."
}
Import-Module WebAdministration

Write-Host "2. Folders and the environment file"
foreach ($d in "releases", "archive", "backups") { New-Item -ItemType Directory -Force -Path (Join-Path $home_ $d) | Out-Null }
if (-not (Test-Path "IIS:\AppPools\$pool")) {
  $p = New-WebAppPool -Name $pool
  $p.managedRuntimeVersion = ""; $p.startMode = "AlwaysRunning"; $p.processModel.idleTimeout = [TimeSpan]::Zero
  $p | Set-Item
}
$poolIdentity = "IIS AppPool\$pool"
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($who in "BUILTIN\Administrators", "NT AUTHORITY\SYSTEM", $poolIdentity) {
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")))
}
Set-Acl -Path $home_ -AclObject $acl

$dbPassword = $null
if (-not (Test-Path $envFile)) {
  $dbPassword = New-Secret
  $origin = "https://$HostName"
  # Forwarded headers count only from Cloudflare: its published ranges, read once here (refresh with the runbook).
  $cloudflare = (((Invoke-RestMethod "https://www.cloudflare.com/ips-v4") + "`n" + (Invoke-RestMethod "https://www.cloudflare.com/ips-v6")) -split "`n" |
    ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ","
  @(
    "# Podium $Environment - read by deploy.ps1 into the site's web.config. Never commit this file.",
    "ASPNETCORE_ENVIRONMENT=Production",
    "AIPOD_DB=Host=127.0.0.1;Port=5432;Database=aipod_$Environment;Username=aipod_$Environment;Password=$dbPassword",
    "AIPOD_PUBLIC_ORIGIN=$origin",
    "AIPOD_SMTP_HOST=smtp.gmail.com",
    "AIPOD_SMTP_PORT=587",
    "AIPOD_SMTP_TLS=true",
    "AIPOD_SMTP_USER=$SmtpUser",
    "AIPOD_SMTP_PASSWORD=",
    "AIPOD_SMTP_FROM=$SmtpFrom",
    "AIPOD_GITHUB_CLIENT_ID=",
    "AIPOD_GITHUB_CLIENT_SECRET=",
    "AIPOD_GOOGLE_CLIENT_ID=",
    "AIPOD_GOOGLE_CLIENT_SECRET=",
    "AIPOD_ARCHIVE_DIR=$(Join-Path $home_ 'archive')",
    "AIPOD_TRUST_PROXY=true",
    "AIPOD_PROXY_NETWORKS=$cloudflare",
    "AIPOD_BACKUP_KEY=$(New-Secret 32)",
    "AIPOD_MFA_KEY=$(New-Secret 32)",
    "AIPOD_BACKUP_OFFSITE=",
    "AIPOD_ALERT_EMAIL="
  ) | Set-Content -Path $envFile -Encoding UTF8
  Write-Host "   wrote $envFile - fill in the SMTP password and the OAuth client secrets"
}

Write-Host "3. PostgreSQL role and database"
if ($dbPassword) {
  $su = Read-Host "PostgreSQL superuser (postgres) password" -AsSecureString
  $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($su))
  try {
    & "$PgBin\psql.exe" -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1 -c "CREATE ROLE aipod_$Environment LOGIN PASSWORD '$dbPassword';" | Out-Null
    & "$PgBin\psql.exe" -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE aipod_$Environment OWNER aipod_$Environment;" | Out-Null
  } finally { Remove-Item Env:PGPASSWORD }
} else { Write-Host "   aipod.env already existed: the database is assumed to exist" }

Write-Host "4. Site and bindings"
if (-not (Get-Website -Name $site)) {
  $placeholder = Join-Path $home_ "releases\empty"; New-Item -ItemType Directory -Force -Path $placeholder | Out-Null
  New-Website -Name $site -PhysicalPath $placeholder -ApplicationPool $pool -HostHeader $HostName -Port 443 -Ssl -SslFlags 1 | Out-Null
  (Get-WebBinding -Name $site -Protocol https).AddSslCertificate($CertThumbprint, "My")
  New-WebBinding -Name $site -Protocol http -IPAddress 127.0.0.1 -Port $LoopbackPort | Out-Null
}

Write-Host "5. Daily backup task (03:30)"
$task = "Podium-backup-$Environment"
if (-not (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)) {
  $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\backup.ps1`" -Environment $Environment -Root `"$Root`" -PgBin `"$PgBin`""
  Register-ScheduledTask -TaskName $task -Action $action -Trigger (New-ScheduledTaskTrigger -Daily -At 03:30) -User "SYSTEM" -RunLevel Highest | Out-Null
}
Write-Host "6. Hourly health check and alarm"
$check = "Podium-health-$Environment"
if (-not (Get-ScheduledTask -TaskName $check -ErrorAction SilentlyContinue)) {
  $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\check-health.ps1`" -Environment $Environment -LoopbackPort $LoopbackPort -Root `"$Root`""
  $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours 1)
  Register-ScheduledTask -TaskName $check -Action $action -Trigger $trigger -User "SYSTEM" -RunLevel Highest | Out-Null
}
Write-Host "Done. Next: fill in $envFile (with AIPOD_ALERT_EMAIL), then deploy a release with deploy.ps1."
