<#
.SYNOPSIS
  The monthly restore drill (global #18: a backup is only a backup if it has been restored). Decrypts the newest backup
  of an environment, restores it into a scratch database, checks it, records the drill, and removes the scratch copy.

.DESCRIPTION
  The drill fails, loudly (event log error), when there is no backup from the last 26 hours (the backup did not run),
  when the file cannot be decrypted, when pg_restore fails, or when the restored database has no users or no
  workspaces. The record goes to <env>\backups\restore-drills.log: date, file, row counts, result.
  Only the scratch database monitor_restore_drill is created and dropped; the live database is never touched.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [string] $Root = "C:\ClaudeMonitor",
  [string] $PgBin = "C:\Program Files\PostgreSQL\18\bin"
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "BackupCrypto.psm1")
$source = "ClaudeMonitor"
if (-not [Diagnostics.EventLog]::SourceExists($source)) { New-EventLog -LogName Application -Source $source }
$home_ = Join-Path $Root $Environment
$log = Join-Path $home_ "backups\restore-drills.log"
$values = @{}
foreach ($line in Get-Content (Join-Path $home_ "monitor.env")) { if ($line -match '^([A-Z_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] } }
$scratch = "monitor_restore_drill"

try {
  $latest = Get-ChildItem (Join-Path $home_ "backups") -Filter "*.cmbak" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $latest -or $latest.LastWriteTime -lt (Get-Date).AddHours(-26)) { throw "no backup from the last 26 hours: the backup did not run" }

  $work = Join-Path $env:TEMP "cm-drill-$([guid]::NewGuid().ToString('N'))"; New-Item -ItemType Directory -Path $work | Out-Null
  Unprotect-BackupFile -Source $latest.FullName -Destination "$work\backup.zip" -Secret $values["MONITOR_BACKUP_KEY"]
  Expand-Archive "$work\backup.zip" -DestinationPath $work

  $su = Read-Host "PostgreSQL superuser (postgres) password" -AsSecureString
  $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($su))
  & "$PgBin\psql.exe" -h 127.0.0.1 -U postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $scratch;" -c "CREATE DATABASE $scratch;" | Out-Null
  & "$PgBin\pg_restore.exe" -h 127.0.0.1 -U postgres -d $scratch --no-owner "$work\database.dump"
  if ($LASTEXITCODE -ne 0) { throw "pg_restore failed ($LASTEXITCODE)" }
  $counts = & "$PgBin\psql.exe" -h 127.0.0.1 -U postgres -d $scratch -At -c "SELECT (SELECT count(*) FROM users) || ' users, ' || (SELECT count(*) FROM workspaces) || ' workspaces, ' || (SELECT count(*) FROM harness_sessions) || ' sessions, ' || (SELECT count(*) FROM session_events) || ' events';"
  if ($counts -match '^0 users' -or $counts -match ' 0 workspaces') { throw "the restored database is empty: $counts" }
  & "$PgBin\psql.exe" -h 127.0.0.1 -U postgres -c "DROP DATABASE $scratch;" | Out-Null
  Remove-Item Env:PGPASSWORD
  Remove-Item -Recurse -Force $work
  $record = "$((Get-Date).ToString('dd/MM/yyyy HH:mm')) OK $($latest.Name): $counts"
  Add-Content -Path $log -Value $record
  Write-EventLog -LogName Application -Source $source -EventId 1010 -EntryType Information -Message "restore drill $Environment passed: $counts"
  Write-Host $record
} catch {
  Add-Content -Path $log -Value "$((Get-Date).ToString('dd/MM/yyyy HH:mm')) FAILED: $($_.Exception.Message)"
  Write-EventLog -LogName Application -Source $source -EventId 1011 -EntryType Error -Message "restore drill $Environment FAILED: $($_.Exception.Message)"
  throw
}
