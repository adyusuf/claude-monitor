<#
.SYNOPSIS
  Turns on Settings -> Integrations (ADR-0017) on a server that is already deployed: adds the two keys its aipod.env lacks.
  - AIPOD_ADMIN_WORKSPACE: the workspace whose owners and admins may open the page (its id: the /w/<id> part of its address);
  - AIPOD_INTEGRATION_KEY: 32 random bytes (base64url) that seal the secrets saved on the page. An existing key is kept as it is.
  The old file is kept as aipod.env.bak-<date>. Prints key NAMES and the workspace id only, never a secret. Run in an elevated
  PowerShell; deploy the same release again afterwards (deploy.ps1 writes aipod.env into web.config).
  Copy AIPOD_INTEGRATION_KEY into the password manager: without it the saved secrets cannot be read again.

.EXAMPLE
  .\enable-integration-settings.ps1 -Environment test -AdminWorkspace 3f2b8c1e-0d4a-4a6e-9a51-2b6f3f0c7a01
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [Parameter(Mandatory)] [guid] $AdminWorkspace,
  [string] $Root = "C:\iis\Podium"
)
$ErrorActionPreference = "Stop"
$envFile = Join-Path (Join-Path $Root $Environment) "aipod.env"
if (-not (Test-Path $envFile)) { throw "${Environment}: $envFile not found" }

function New-Secret([int] $bytes = 32) {
  $b = New-Object byte[] $bytes; $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($b) } finally { $rng.Dispose() }
  return [Convert]::ToBase64String($b).TrimEnd("=").Replace("+", "-").Replace("/", "_")
}

# Sets KEY=value in the lines: replaces an existing KEY= line (empty or not), else appends. Returns the new lines.
function Set-Line([string[]] $lines, [string] $key, [string] $value) {
  $found = $false
  $out = foreach ($line in $lines) {
    if ($line -match "^$([regex]::Escape($key))=") { $found = $true; "$key=$value" } else { $line }
  }
  if (-not $found) { $out = @($out) + "$key=$value" }
  return , @($out)
}

$lines = @(Get-Content $envFile)
$backup = "$envFile.bak-$(Get-Date -Format yyyyMMdd)"
if (-not (Test-Path $backup)) { Copy-Item $envFile $backup }

$existing = $lines | Where-Object { $_ -match '^AIPOD_INTEGRATION_KEY=(.+)$' } | Select-Object -First 1
$lines = Set-Line $lines "AIPOD_ADMIN_WORKSPACE" $AdminWorkspace.ToString()
if (-not $existing) { $lines = Set-Line $lines "AIPOD_INTEGRATION_KEY" (New-Secret 32) }
[IO.File]::WriteAllLines($envFile, [string[]]$lines, (New-Object Text.UTF8Encoding $false))

Write-Host "${Environment}: AIPOD_ADMIN_WORKSPACE=$AdminWorkspace"
Write-Host "${Environment}: AIPOD_INTEGRATION_KEY $(if ($existing) { 'kept (already set)' } else { 'generated and written to ' + $envFile })"
if (-not $existing) { Write-Host "  Copy it into the password manager now: without it the secrets saved on the page cannot be read again." }
Write-Host "Deploy the same release again so the values reach web.config (deploy.ps1)."
