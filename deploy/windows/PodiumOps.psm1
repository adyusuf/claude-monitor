<#
.SYNOPSIS
  Helpers of the server scripts that are worth testing apart (scripts/tests/test_podium_ops.py). Written for
  Windows PowerShell 5.1 as well as PowerShell 7: the scheduled tasks run powershell.exe.
#>

function Find-AppSettings {
  <#
  .SYNOPSIS
    The server-only appsettings.Production.json of the live release, or $null. The layout is deploy.ps1's:
    <env home>\releases\<commit>\api\appsettings.Production.json. The release /api/version reported comes first,
    else the newest release folder that has the file (the file is not in the package: an operator puts it into
    a release by hand, so the live one may not have it).
  #>
  param([Parameter(Mandatory)] [string] $EnvironmentHome, [string] $Commit)
  $releases = Join-Path $EnvironmentHome "releases"
  if ($Commit -match '^[0-9a-f]{40}$') {
    $path = Join-Path $releases "$Commit\api\appsettings.Production.json"
    if (Test-Path -LiteralPath $path) { return $path }
  }
  $newest = Get-ChildItem -LiteralPath $releases -Directory -ErrorAction SilentlyContinue |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "api\appsettings.Production.json") } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($newest) { return Join-Path $newest.FullName "api\appsettings.Production.json" }
  return $null
}

function Get-SmtpSettings {
  <#
  .SYNOPSIS
    The SMTP settings the way the API reads them (ApiConfig.SmtpValue): per field, a non-empty AIPOD_SMTP_* value
    of the environment file wins, else the same field of the "Smtp" node of appsettings.Production.json
    (Host, Port, User, Password, From, StartTls). check-health.ps1 used to read the environment file alone, so with
    the user and password only in the node every alarm mail was refused ("The SMTP server requires a secure
    connection or the client was not authenticated", event 1021), unseen.
    StartTls follows ApiConfig exactly: unset counts as on (the production default), any set value is on only when it
    is "true" in any case, so "false", "0", "no" or a typo turn it off, as they do for the API.
    Throws, naming the fields, when host, port or sender stay unresolved or the port is not a number.
  #>
  param([Parameter(Mandatory)] [hashtable] $Values, [string] $AppSettingsPath)
  $node = $null
  if ($AppSettingsPath -and (Test-Path -LiteralPath $AppSettingsPath)) {
    # Read and parse apart, so event 1021 sends the operator to the right place (access denied vs. broken JSON).
    # Neither message quotes the file; the read error names only the exception type.
    try { $raw = Get-Content -LiteralPath $AppSettingsPath -Raw -ErrorAction Stop }
    catch { throw "appsettings.Production.json could not be read ($AppSettingsPath): $($_.Exception.GetType().Name)" }
    try { $node = ($raw | ConvertFrom-Json).Smtp }
    catch { throw "appsettings.Production.json could not be parsed ($AppSettingsPath)" }
  }
  function Pick([hashtable] $Source, $Node, [string] $Key, [string] $Field) {
    if ("$($Source[$Key])" -ne "") { return "$($Source[$Key])" }
    if ($Node -and "$($Node.$Field)" -ne "") { return "$($Node.$Field)" }
    return $null
  }
  $tls = Pick $Values $node "AIPOD_SMTP_TLS" "StartTls"
  $settings = @{
    Host     = Pick $Values $node "AIPOD_SMTP_HOST" "Host"
    Port     = Pick $Values $node "AIPOD_SMTP_PORT" "Port"
    User     = Pick $Values $node "AIPOD_SMTP_USER" "User"
    Password = Pick $Values $node "AIPOD_SMTP_PASSWORD" "Password"
    From     = Pick $Values $node "AIPOD_SMTP_FROM" "From"
    StartTls = if ("$tls" -ne "") { "$tls" -eq "true" } else { $true }   # as ApiConfig: unset = on, else on only for "true"
  }
  $missing = @()
  if (-not $settings.Host) { $missing += "host (AIPOD_SMTP_HOST / Smtp:Host)" }
  if (-not $settings.Port) { $missing += "port (AIPOD_SMTP_PORT / Smtp:Port)" }
  if (-not $settings.From) { $missing += "sender (AIPOD_SMTP_FROM / Smtp:From)" }
  if ($missing.Count -gt 0) {
    $where = if ($AppSettingsPath -and (Test-Path -LiteralPath $AppSettingsPath)) { "the Smtp node of $AppSettingsPath" } else { "any release (no appsettings.Production.json found)" }
    throw "SMTP $($missing -join ', ') not set in aipod.env, and not in $where"
  }
  $number = 0
  if (-not [int]::TryParse($settings.Port, [ref] $number)) { throw "SMTP port is not a number (AIPOD_SMTP_PORT / Smtp:Port)" }
  $settings.Port = $number
  return $settings
}

Export-ModuleMember -Function Find-AppSettings, Get-SmtpSettings
