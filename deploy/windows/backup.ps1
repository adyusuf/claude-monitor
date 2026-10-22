<#
.SYNOPSIS
  The daily backup of one environment (global #18): the database AND the event archives, encrypted, kept locally for
  30 days and copied off the server. A failure is written to the Windows event log as an error (the alarm), and so is
  a backup that did not run: restore-drill.ps1 and the monitoring check look for today's file.

.DESCRIPTION
  pg_dump (custom format) and a zip of the archive folder are packed together and encrypted (BackupCrypto.psm1) with
  AIPOD_BACKUP_KEY from the environment file (a secret that lives only there and in the maintainer's password
  manager: without it no backup can be restored). The result goes to <env>\backups and, when AIPOD_BACKUP_OFFSITE
  names a folder (a mapped share or synced cloud folder on another machine), there too.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [string] $Root = "C:\iis\Podium",
  [string] $PgBin = "C:\Program Files\PostgreSQL\18\bin",
  [int] $KeepDays = 30
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "BackupCrypto.psm1")
$source = "Podium"
if (-not [Diagnostics.EventLog]::SourceExists($source)) { New-EventLog -LogName Application -Source $source }
$home_ = Join-Path $Root $Environment
$values = @{}
foreach ($line in Get-Content (Join-Path $home_ "aipod.env")) { if ($line -match '^([A-Z_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] } }

$work = $null
try {
  $stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHHmmssZ")
  $work = New-BackupWork -Environment $Environment -Stamp $stamp
  $db = @{}; foreach ($part in $values["AIPOD_DB"].Split(";")) { $k, $v = $part.Split("=", 2); $db[$k] = $v }
  $env:PGPASSWORD = $db["Password"]
  & "$PgBin\pg_dump.exe" -h $db["Host"] -p $db["Port"] -U $db["Username"] -d $db["Database"] -Fc -f (Join-Path $work.Folder "database.dump")
  if ($LASTEXITCODE -ne 0) { throw "pg_dump failed ($LASTEXITCODE)" }
  Remove-Item Env:PGPASSWORD
  Compress-Archive -Path (Join-Path $home_ "archive\*") -DestinationPath (Join-Path $work.Folder "archive.zip") -ErrorAction SilentlyContinue
  Compress-Archive -Path "$($work.Folder)\*" -DestinationPath $work.Zip

  $out = Join-Path $home_ "backups\$Environment-$stamp.cmbak"
  Protect-BackupFile -Source $work.Zip -Destination $out -Secret $values["AIPOD_BACKUP_KEY"]
  Remove-BackupWork $work

  if ($values["AIPOD_BACKUP_OFFSITE"]) { Copy-Item $out -Destination $values["AIPOD_BACKUP_OFFSITE"] }
  Get-ChildItem (Join-Path $home_ "backups") -Filter "*.cmbak" | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$KeepDays) } | Remove-Item
  Write-EventLog -LogName Application -Source $source -EventId 1000 -EntryType Information -Message "backup $Environment written: $out"
} catch {
  Write-EventLog -LogName Application -Source $source -EventId 1001 -EntryType Error -Message "backup $Environment FAILED: $($_.Exception.Message)"
  throw
} finally {
  if (Test-Path Env:PGPASSWORD) { Remove-Item Env:PGPASSWORD }
  if ($work) {
    try { Remove-BackupWork $work } catch {
      Write-EventLog -LogName Application -Source $source -EventId 1001 -EntryType Error -Message "backup ${Environment}: scratch files not removed, and they hold a plain database dump: delete $($work.Folder) ($($_.Exception.Message))"
    }
  }
}
