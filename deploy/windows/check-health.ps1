<#
.SYNOPSIS
  The hourly health check of one environment, and its alarm (global #18: a failed backup and a backup that never ran
  are alarmed separately). Registered by setup-server.ps1. Mails MONITOR_ALERT_EMAIL through the environment's own
  SMTP settings when anything is wrong, and always writes the result to the event log.

.DESCRIPTION
  Alarms when:
    - the API does not answer /api/version on its loopback binding;
    - no successful backup (event 1000) in the last 26 hours: the backup did not run;
    - a backup or restore drill failed (events 1001, 1011) since the last check;
    - the ASP.NET Core module logged an error (the API failed to start or crashed) since the last check;
    - no successful restore drill recorded in the last 35 days (the monthly drill was missed).
  A mail that cannot be sent is itself an event-log error (1021), so a broken alarm is visible too.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [Parameter(Mandatory)] [int] $LoopbackPort,
  [string] $Root = "C:\ClaudeMonitor"
)
$ErrorActionPreference = "Stop"
$source = "ClaudeMonitor"
if (-not [Diagnostics.EventLog]::SourceExists($source)) { New-EventLog -LogName Application -Source $source }
$home_ = Join-Path $Root $Environment
$values = @{}
foreach ($line in Get-Content (Join-Path $home_ "monitor.env")) { if ($line -match '^([A-Z_]+)=(.*)$') { $values[$Matches[1]] = $Matches[2] } }
$stateFile = Join-Path $home_ "last-health-check.txt"
$since = if (Test-Path $stateFile) { [DateTime]::Parse((Get-Content $stateFile -Raw).Trim()) } else { (Get-Date).AddHours(-1) }
$now = Get-Date
$problems = New-Object System.Collections.Generic.List[string]

function Get-Events([string] $Provider, [int[]] $Ids, [DateTime] $After, [int] $Level = 0) {
  $filter = @{ LogName = "Application"; ProviderName = $Provider; StartTime = $After }
  if ($Ids) { $filter.Id = $Ids }
  if ($Level) { $filter.Level = $Level }
  try { return @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop) } catch { return @() }
}

try {
  $version = Invoke-RestMethod -Uri "http://127.0.0.1:$LoopbackPort/api/version" -TimeoutSec 10
  if (-not $version.commit) { $problems.Add("/api/version answered without a commit") }
} catch { $problems.Add("the API does not answer on 127.0.0.1:$LoopbackPort ($($_.Exception.Message))") }

$backups = Get-Events $source @(1000) $now.AddHours(-26) | Where-Object { $_.Message -like "backup $Environment *" }
if ($backups.Count -eq 0) { $problems.Add("no successful backup of $Environment in the last 26 hours: the backup did not run") }

foreach ($e in Get-Events $source @(1001, 1011) $since | Where-Object { $_.Message -like "* $Environment *" }) {
  $problems.Add("$($e.TimeCreated.ToString('dd/MM/yyyy HH:mm')): $($e.Message)")
}

foreach ($e in Get-Events "IIS AspNetCore Module V2" @() $since 2) {
  $problems.Add("ASP.NET Core module error at $($e.TimeCreated.ToString('dd/MM/yyyy HH:mm')): $($e.Message.Split("`n")[0])")
}

$drills = Join-Path $home_ "backups\restore-drills.log"
$lastDrill = if (Test-Path $drills) { Get-Content $drills | Where-Object { $_ -match '^\d{2}/\d{2}/\d{4} \d{2}:\d{2} OK' } | Select-Object -Last 1 } else { $null }
$drillDate = if ($lastDrill) { [DateTime]::ParseExact($lastDrill.Substring(0, 16), "dd/MM/yyyy HH:mm", $null) } else { [DateTime]::MinValue }
if ($drillDate -lt $now.AddDays(-35)) { $problems.Add("no successful restore drill of $Environment in the last 35 days: run restore-drill.ps1") }

Set-Content -Path $stateFile -Value $now.ToString("o")
if ($problems.Count -eq 0) {
  Write-EventLog -LogName Application -Source $source -EventId 1019 -EntryType Information -Message "health $Environment ok"
  return
}

$text = "Claude Monitor $Environment needs attention:`n`n- " + ($problems -join "`n- ")
Write-EventLog -LogName Application -Source $source -EventId 1020 -EntryType Error -Message $text
try {
  if (-not $values["MONITOR_ALERT_EMAIL"]) { throw "MONITOR_ALERT_EMAIL is not set" }
  $smtp = New-Object Net.Mail.SmtpClient($values["MONITOR_SMTP_HOST"], [int]$values["MONITOR_SMTP_PORT"])
  $smtp.EnableSsl = $values["MONITOR_SMTP_TLS"] -ne "false"
  if ($values["MONITOR_SMTP_USER"]) { $smtp.Credentials = New-Object Net.NetworkCredential($values["MONITOR_SMTP_USER"], $values["MONITOR_SMTP_PASSWORD"]) }
  $smtp.Send($values["MONITOR_SMTP_FROM"], $values["MONITOR_ALERT_EMAIL"], "Claude Monitor ${Environment}: $($problems.Count) problem(s)", $text)
} catch {
  Write-EventLog -LogName Application -Source $source -EventId 1021 -EntryType Error -Message "health alarm mail for $Environment could not be sent: $($_.Exception.Message)"
}
