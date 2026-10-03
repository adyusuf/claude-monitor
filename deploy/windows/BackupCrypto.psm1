<#
  Backup encryption shared by backup.ps1 and restore-drill.ps1. Streaming, so a dump of any size never sits in memory,
  and it runs on Windows PowerShell 5.1 as well as PowerShell 7.
  Format: IV (16) | AES-256-CBC ciphertext | HMAC-SHA256 (32) over IV and ciphertext (encrypt-then-MAC).
  The two keys are derived from MONITOR_BACKUP_KEY (base64url of 32 random bytes); a wrong key or a changed file fails
  the MAC check before anything is decrypted.
#>

function Get-BackupKeys([string] $Secret) {
  $raw = [Convert]::FromBase64String(($Secret.Replace("-", "+").Replace("_", "/")).PadRight(44, "="))
  $h = New-Object Security.Cryptography.HMACSHA256 (, $raw)
  $enc = $h.ComputeHash([Text.Encoding]::ASCII.GetBytes("claude-monitor backup encryption"))
  $mac = $h.ComputeHash([Text.Encoding]::ASCII.GetBytes("claude-monitor backup authentication"))
  return @{ Enc = $enc; Mac = $mac }
}

function Get-FileMac([string] $Path, [byte[]] $MacKey, [long] $Length) {
  $hmac = New-Object Security.Cryptography.HMACSHA256 (, $MacKey)
  $stream = [IO.File]::OpenRead($Path)
  try {
    $buffer = New-Object byte[] 1048576; $left = $Length
    while ($left -gt 0) {
      $n = $stream.Read($buffer, 0, [int][Math]::Min($buffer.Length, $left)); if ($n -le 0) { break }
      [void]$hmac.TransformBlock($buffer, 0, $n, $null, 0); $left -= $n
    }
    [void]$hmac.TransformFinalBlock((New-Object byte[] 0), 0, 0)
    return $hmac.Hash
  } finally { $stream.Dispose() }
}

function Protect-BackupFile([string] $Source, [string] $Destination, [string] $Secret) {
  $keys = Get-BackupKeys $Secret
  $aes = [Security.Cryptography.Aes]::Create(); $aes.Key = $keys.Enc; $aes.GenerateIV()
  $out = [IO.File]::Create($Destination)
  try {
    $out.Write($aes.IV, 0, 16)
    $crypto = New-Object Security.Cryptography.CryptoStream($out, $aes.CreateEncryptor(), [Security.Cryptography.CryptoStreamMode]::Write, $true)
    $in = [IO.File]::OpenRead($Source)
    try { $in.CopyTo($crypto) } finally { $in.Dispose() }
    $crypto.FlushFinalBlock(); $crypto.Dispose()
  } finally { $out.Dispose() }
  $mac = Get-FileMac $Destination $keys.Mac (Get-Item $Destination).Length
  $append = [IO.File]::Open($Destination, [IO.FileMode]::Append)
  try { $append.Write($mac, 0, 32) } finally { $append.Dispose() }
}

function Unprotect-BackupFile([string] $Source, [string] $Destination, [string] $Secret) {
  $keys = Get-BackupKeys $Secret
  $length = (Get-Item $Source).Length
  if ($length -lt 64) { throw "not a backup file" }
  $expected = Get-FileMac $Source $keys.Mac ($length - 32)
  $in = [IO.File]::OpenRead($Source)
  try {
    $tail = New-Object byte[] 32; [void]$in.Seek($length - 32, [IO.SeekOrigin]::Begin); [void]$in.Read($tail, 0, 32)
    $diff = 0; for ($i = 0; $i -lt 32; $i++) { $diff = $diff -bor ($tail[$i] -bxor $expected[$i]) }
    if ($diff -ne 0) { throw "the backup's authentication failed: wrong MONITOR_BACKUP_KEY, or the file was changed" }
    [void]$in.Seek(0, [IO.SeekOrigin]::Begin)
    $iv = New-Object byte[] 16; [void]$in.Read($iv, 0, 16)
    $aes = [Security.Cryptography.Aes]::Create(); $aes.Key = $keys.Enc; $aes.IV = $iv
    $out = [IO.File]::Create($Destination)
    try {
      $crypto = New-Object Security.Cryptography.CryptoStream($out, $aes.CreateDecryptor(), [Security.Cryptography.CryptoStreamMode]::Write, $true)
      $buffer = New-Object byte[] 1048576; $left = $length - 48
      while ($left -gt 0) {
        $n = $in.Read($buffer, 0, [int][Math]::Min($buffer.Length, $left)); if ($n -le 0) { break }
        $crypto.Write($buffer, 0, $n); $left -= $n
      }
      $crypto.FlushFinalBlock(); $crypto.Dispose()
    } finally { $out.Dispose() }
  } finally { $in.Dispose() }
}

Export-ModuleMember -Function Protect-BackupFile, Unprotect-BackupFile
