<#
.SYNOPSIS
  The daily backup of one environment (global #18): the database AND the event archives, encrypted, kept locally for
  30 days and copied off the server. A failure is written to the Windows event log as an error (the alarm), and so is
  a backup that did not run: restore-drill.ps1 and the monitoring check look for today's file.

.DESCRIPTION
  pg_dump (custom format) and a zip of the archive folder are packed together and encrypted (BackupCrypto.psm1) with
  MONITOR_BACKUP_KEY from the environment file (a secret that lives only there and in the maintainer's password
  manager: without it no backup can be restored). The result goes to <env>\backups and, when MONITOR_BACKUP_OFFSITE
  names a folder (a mapped share or synced cloud folder on another machine), there too.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [string] $Root = "C:\ClaudeMonitor",
  [string] $PgBin = "C:\Program Files\PostgreSQL\18\bin",
  [int] $KeepDays = 30
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "BackupCrypto.psm1")
$source = "ClaudeMonitor"
if (-not [Diagnostics.EventLog]::SourceExists($source)) { New-EventLog -LogName Application -Source $source }
$home_ = Join-Path $Root $Environment
$values = @{}
foreach ($line in Get-Content (Join-Path $home_ "monitor.env")) { if ($line -match '^([A-Z_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] } }

try {
  $stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHHmmssZ")
  $work = Join-Path $env:TEMP "cm-backup-$stamp"; New-Item -ItemType Directory -Path $work | Out-Null
  $db = @{}; foreach ($part in $values["MONITOR_DB"].Split(";")) { $k, $v = $part.Split("=", 2); $db[$k] = $v }
  $env:PGPASSWORD = $db["Password"]
  & "$PgBin\pg_dump.exe" -h $db["Host"] -p $db["Port"] -U $db["Username"] -d $db["Database"] -Fc -f (Join-Path $work "database.dump")
  if ($LASTEXITCODE -ne 0) { throw "pg_dump failed ($LASTEXITCODE)" }
  Remove-Item Env:PGPASSWORD
  Compress-Archive -Path (Join-Path $home_ "archive\*") -DestinationPath (Join-Path $work "archive.zip") -ErrorAction SilentlyContinue
  $plain = Join-Path $env:TEMP "cm-backup-$stamp.zip"
  Compress-Archive -Path "$work\*" -DestinationPath $plain

  $out = Join-Path $home_ "backups\$Environment-$stamp.cmbak"
  Protect-BackupFile -Source $plain -Destination $out -Secret $values["MONITOR_BACKUP_KEY"]
  Remove-Item -Recurse -Force $work, $plain

  if ($values["MONITOR_BACKUP_OFFSITE"]) { Copy-Item $out -Destination $values["MONITOR_BACKUP_OFFSITE"] }
  Get-ChildItem (Join-Path $home_ "backups") -Filter "*.cmbak" | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$KeepDays) } | Remove-Item
  Write-EventLog -LogName Application -Source $source -EventId 1000 -EntryType Information -Message "backup $Environment written: $out"
} catch {
  Write-EventLog -LogName Application -Source $source -EventId 1001 -EntryType Error -Message "backup $Environment FAILED: $($_.Exception.Message)"
  throw
}
