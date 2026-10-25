<#
.SYNOPSIS
  Sets a new password for one account and marks its e-mail verified, directly in the environment's database. For a server whose
  mail cannot be sent yet (the reset and verification links are mailed), so nobody can sign in. Run it yourself, in an elevated
  PowerShell, on the server: it asks for the password (hidden, twice); nothing about it is printed or logged.

  It does what the API's own reset-password link does (AuthEndpoints.Reset): stores the password as the same ASP.NET Core Identity
  hash, sets the e-mail verified when it is not, clears a lockout, signs the account out everywhere, and writes the audit row
  user.password_reset (detail: the script's name; the password and its hash are never in it). Two-step sign-in is left as it is.

  The password needs 10 to 256 characters (the API's rule). Pick a strong one: after this, the account can change the server-wide
  mail and sign-in settings (ADR-0017).

  -VerifyOnly keeps the password as it is: no password prompt, it only marks the e-mail verified (and writes the audit row
  user.email_verified). Use it when the person knows their password and only the verification mail could not be sent.

  -DryRun only shows the account that would change; it writes nothing.

.EXAMPLE
  .\reset-owner-password.ps1 -Environment prod -Email owner@example.com
  .\reset-owner-password.ps1 -Environment prod -Email owner@example.com -VerifyOnly
  .\reset-owner-password.ps1 -Environment prod -Email owner@example.com -DryRun
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [ValidateSet("test", "prod")] [string] $Environment,
  [Parameter(Mandatory)] [string] $Email,
  [switch] $DryRun,
  [switch] $VerifyOnly,
  [string] $Root = "C:\iis\Podium"
)
$ErrorActionPreference = "Stop"
$PasswordMin = 10
$PasswordMax = 256

# ASP.NET Core Identity's PasswordHasher, format V3: 0x01, PRF (2 = HMACSHA512, the current default), iterations, salt length (each a big-endian
# uint32), the salt, then the 32-byte PBKDF2 sub-key. The API reads the iterations from the hash, so any count verifies.
function New-PasswordHash([string] $Password) {
  $iterations = 100000
  $salt = New-Object byte[] 16
  $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($salt) } finally { $rng.Dispose() }
  $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($Password, $salt, $iterations, [Security.Cryptography.HashAlgorithmName]::SHA512)
  try { $subkey = $kdf.GetBytes(32) } finally { $kdf.Dispose() }
  $be = { param([uint32] $n) $b = [BitConverter]::GetBytes($n); [Array]::Reverse($b); $b }
  $blob = [byte[]]@(1) + (& $be 2) + (& $be $iterations) + (& $be 16) + $salt + $subkey
  return [Convert]::ToBase64String($blob)
}

function Read-NewPassword {
  while ($true) {
    $a = Read-Host "New password ($PasswordMin to $PasswordMax characters)" -AsSecureString
    $b = Read-Host "Type it again" -AsSecureString
    $pa = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($a))
    $pb = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($b))
    if ($pa -ne $pb) { Write-Host "The two do not match. Try again."; continue }
    if ($pa.Length -lt $PasswordMin -or $pa.Length -gt $PasswordMax -or [string]::IsNullOrWhiteSpace($pa)) {
      Write-Host "The password needs $PasswordMin to $PasswordMax characters and cannot be only spaces. Try again."; continue
    }
    return $pa
  }
}

# The simplest safe e-mail shape: no quote, backslash, semicolon or whitespace can reach the SQL.
if ($Email -notmatch '^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$') { throw "That is not an e-mail address." }

$envFile = Join-Path (Join-Path $Root $Environment) "aipod.env"
if (-not (Test-Path $envFile)) { throw "${Environment}: $envFile not found" }
$dbLine = Get-Content $envFile | Where-Object { $_ -match '^AIPOD_DB=' } | Select-Object -First 1
if (-not $dbLine) { throw "${Environment}: AIPOD_DB is not set in $envFile" }
$db = @{}
foreach ($part in $dbLine.Substring(9).Split(';')) { if ($part -match '=') { $k, $v = $part.Split('=', 2); $db[$k.Trim()] = $v } }
foreach ($need in 'Host', 'Port', 'Database', 'Username', 'Password') { if (-not $db[$need]) { throw "${Environment}: AIPOD_DB has no $need" } }

$psql = Get-ChildItem (Join-Path $env:ProgramFiles "PostgreSQL\*\bin\psql.exe") -ErrorAction SilentlyContinue |
  Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $psql) { throw "psql.exe not found under $env:ProgramFiles\PostgreSQL" }

function Invoke-Psql([string] $Sql, [switch] $Tuples) {
  $args_ = @('-h', $db['Host'], '-p', $db['Port'], '-U', $db['Username'], '-d', $db['Database'], '-X', '-q', '-v', 'ON_ERROR_STOP=1')
  if ($Tuples) { $args_ += @('-A', '-t', '-F', '|') }
  $env:PGPASSWORD = $db['Password']
  try {
    $out = $Sql | & $psql @args_ 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("psql failed: " + (($out | Out-String).Trim() -replace [regex]::Escape($db['Password']), '<hidden>')) }
    return $out
  } finally { $env:PGPASSWORD = $null }
}

$who = Invoke-Psql -Tuples -Sql "select id, display_name, status, (email_verified_at is not null), (totp_enabled_at is not null), (password_hash is not null) from users where email_normalized = lower('$Email');"
$rows = @($who | Where-Object { $_ -and $_.ToString().Trim() })
if ($rows.Count -ne 1) { throw "${Environment}: $($rows.Count) accounts match $Email (exactly one is needed)" }
$f = $rows[0].ToString().Split('|')
if ($f[2] -ne 'active') { throw "${Environment}: the account is $($f[2]), not active" }
Write-Host "Account: $($f[1])  <$Email>  (id $($f[0]))"
Write-Host "  e-mail verified: $($f[3])   two-step sign-in: $($f[4])   has a password: $($f[5])"

if ($DryRun) {
  Write-Host "Dry run: nothing was changed."
  return
}

if ($VerifyOnly) {
  if ($f[3] -eq 't') { Write-Host "The e-mail is verified already. Nothing to do."; return }
  Write-Host "This marks the e-mail verified ($Environment). The password, the lockout and the sessions are left as they are."
  if ((Read-Host "Type YES to go on") -cne "YES") { Write-Host "Cancelled. Nothing was changed."; return }
  $verifySql = @"
\set ON_ERROR_STOP on
begin;
update users set email_verified_at = coalesce(email_verified_at, now()), updated_at = now()
  where email_normalized = lower('$Email') and status = 'active' returning id as uid \gset
insert into audit_events (actor_user_id, action, target_type, target_id, at, detail)
  values (:'uid', 'user.email_verified', 'user', :'uid', now(), '{"via":"reset-owner-password.ps1"}');
commit;
"@
  Invoke-Psql -Sql $verifySql | Out-Null
  Write-Host "Done: the e-mail is verified. Sign in at the site with $Email and your existing password."
  return
}

Write-Host "This sets a new password, marks the e-mail verified, clears any lockout and signs the account out everywhere ($Environment)."
if ((Read-Host "Type YES to go on") -cne "YES") { Write-Host "Cancelled. Nothing was changed."; return }

$plain = Read-NewPassword
$hash = New-PasswordHash $plain
$plain = $null

# The hash is base64 (no quote can be in it) and goes in over stdin, not on the command line.
$sql = @"
\set ON_ERROR_STOP on
\set hash '$hash'
begin;
update users set password_hash = :'hash', email_verified_at = coalesce(email_verified_at, now()), failed_sign_ins = 0, locked_until = null, updated_at = now()
  where email_normalized = lower('$Email') and status = 'active' returning id as uid \gset
update login_sessions set revoked_at = now() where user_id = :'uid' and revoked_at is null;
insert into audit_events (actor_user_id, action, target_type, target_id, at, detail)
  values (:'uid', 'user.password_reset', 'user', :'uid', now(), '{"via":"reset-owner-password.ps1"}');
commit;
"@
Invoke-Psql -Sql $sql | Out-Null
$hash = $null
Write-Host "Done: the password is set and the e-mail is verified. Sign in at the site with $Email."
