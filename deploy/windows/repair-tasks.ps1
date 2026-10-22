<#
.SYNOPSIS
  Points the existing backup and health-check scheduled tasks of one environment at the scripts they are started from now and at the current
  server root, keeping each task's name. Run as Administrator, from the folder that holds the current backup.ps1 and check-health.ps1.

.DESCRIPTION
  A scheduled task stores the full path of its script and the -Root it passes. When the scripts or the server folder move (for example
  C:\ClaudeMonitor to C:\iis\Podium) the task keeps running the old path and fails without a word. setup-server.ps1 leaves a task that exists
  alone, so this script is the way to correct it. For each of the two tasks it looks for the current name (Podium-backup-<env>,
  Podium-health-<env>) and the name from before the rename (ClaudeMonitor-backup-<env>, ClaudeMonitor-health-<env>), changes only its action
  (trigger, account and name stay) and prints the arguments before and after. Re-running changes nothing more. -WhatIf shows the change
  without making it.

.EXAMPLE
  .\repair-tasks.ps1 -Environment test -LoopbackPort 8081 -WhatIf
  .\repair-tasks.ps1 -Environment test -LoopbackPort 8081
#>
[CmdletBinding(SupportsShouldProcess)]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [Parameter(Mandatory)] [int] $LoopbackPort,
  [string] $Root = "C:\iis\Podium",
  [string] $PgBin = "C:\Program Files\PostgreSQL\18\bin"
)
$ErrorActionPreference = "Stop"

$tasks = @(
  @{ Names = @("Podium-backup-$Environment", "ClaudeMonitor-backup-$Environment"); Script = "backup.ps1"
     Arguments = "-Environment $Environment -Root `"$Root`" -PgBin `"$PgBin`"" },
  @{ Names = @("Podium-health-$Environment", "ClaudeMonitor-health-$Environment"); Script = "check-health.ps1"
     Arguments = "-Environment $Environment -LoopbackPort $LoopbackPort -Root `"$Root`"" }
)

foreach ($t in $tasks) {
  $script = Join-Path $PSScriptRoot $t.Script
  if (-not (Test-Path -LiteralPath $script)) { throw "$script not found: run this from the folder that holds the current $($t.Script)" }
  $found = $t.Names | Where-Object { Get-ScheduledTask -TaskName $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
  if (-not $found) { Write-Warning "no task named $($t.Names -join ' or ') on this machine: setup-server.ps1 creates it"; continue }
  $existing = Get-ScheduledTask -TaskName $found
  $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" $($t.Arguments)"
  Write-Host "$found"
  Write-Host "   before: $($existing.Actions[0].Execute) $($existing.Actions[0].Arguments)"
  Write-Host "   after:  powershell.exe $arguments"
  if ($existing.Actions[0].Arguments -eq $arguments) { Write-Host "   already correct"; continue }
  if ($PSCmdlet.ShouldProcess($found, "point the action at $script")) {
    Set-ScheduledTask -TaskName $found -Action (New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments) | Out-Null
    Write-Host "   updated"
  }
}
