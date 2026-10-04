<#
.SYNOPSIS
  Deploys a release package (scripts/build-release.sh) to one environment, with an automatic rollback.

.DESCRIPTION
  1. unpacks the package into C:\ClaudeMonitor\<env>\releases\<commit>;
  2. writes the environment file's values into that release's web.config (environmentVariables of the ASP.NET Core
     module), so secrets stay on the server and out of the package;
  3. applies the database migrations (they only add, global #4, so the release still running is not broken);
  4. points the site at the new folder and recycles its app pool;
  5. checks that /api/version on the loopback binding reports the release's commit. If it does not within a minute,
     the site is pointed back at the previous release and the script fails.
  Run as Administrator.

.EXAMPLE
  .\deploy.ps1 -Environment test -Package C:\Temp\claude-monitor-<commit>.zip -LoopbackPort 8081
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [Parameter(Mandatory)] [string] $Package,
  [Parameter(Mandatory)] [int] $LoopbackPort,
  [string] $Root = "C:\ClaudeMonitor"
)
$ErrorActionPreference = "Stop"
Import-Module WebAdministration
$site = "ClaudeMonitor-$Environment"
$home_ = Join-Path $Root $Environment
$envFile = Join-Path $home_ "monitor.env"

$staging = Join-Path $home_ "releases\incoming"
Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
Expand-Archive -Path $Package -DestinationPath $staging
$commit = (Get-Content (Join-Path $staging "release.json") | ConvertFrom-Json).commit
if ($commit -notmatch '^[0-9a-f]{40}$') { throw "release.json has no commit" }
$target = Join-Path $home_ "releases\$commit"
Remove-Item -Recurse -Force $target -ErrorAction SilentlyContinue
Move-Item $staging $target
$api = Join-Path $target "api"

Write-Host "1. environment into web.config"
$values = @{}
foreach ($line in Get-Content $envFile) {
  if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
  $k, $v = $line.Split("=", 2); if ($v -ne "") { $values[$k.Trim()] = $v }
}
$values["MONITOR_WEB_ROOT"] = Join-Path $target "web"
$values["MONITOR_COMMIT"] = $commit
$config = [xml](Get-Content (Join-Path $api "web.config"))
$module = $config.SelectSingleNode("//aspNetCore")
$vars = $config.CreateElement("environmentVariables")
foreach ($k in $values.Keys) {
  $e = $config.CreateElement("environmentVariable"); $e.SetAttribute("name", $k); $e.SetAttribute("value", $values[$k]); [void]$vars.AppendChild($e)
}
[void]$module.AppendChild($vars)
$config.Save((Join-Path $api "web.config"))

Write-Host "2. migrations"
$old = @{}; foreach ($k in $values.Keys) { $old[$k] = [Environment]::GetEnvironmentVariable($k, "Process"); [Environment]::SetEnvironmentVariable($k, $values[$k], "Process") }
try {
  & (Join-Path $api "ClaudeMonitor.Api.exe") --migrate
  if ($LASTEXITCODE -ne 0) { throw "migrations failed ($LASTEXITCODE)" }
} finally { foreach ($k in $old.Keys) { [Environment]::SetEnvironmentVariable($k, $old[$k], "Process") } }

Write-Host "3. switch the site"
$previous = (Get-ItemProperty "IIS:\Sites\$site").physicalPath
Set-ItemProperty "IIS:\Sites\$site" -Name physicalPath -Value $api
Restart-WebAppPool -Name $site

Write-Host "4. verify /api/version"
$ok = $false
for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
  Start-Sleep -Seconds 2
  try { $ok = (Invoke-RestMethod -Uri "http://127.0.0.1:$LoopbackPort/api/version" -TimeoutSec 5).commit -eq $commit } catch { $ok = $false }
}
if (-not $ok) {
  Set-ItemProperty "IIS:\Sites\$site" -Name physicalPath -Value $previous
  Restart-WebAppPool -Name $site
  throw "the new release did not answer with $commit - rolled back to $previous"
}
Write-Host "Deployed $commit to $Environment."
