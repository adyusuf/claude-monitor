<#
.SYNOPSIS
Installs or updates the aipod agent and its Claude Code plugin on Windows, and removes an older agent (cm-agent) on request.

.DESCRIPTION
The same steps as aipod-install.sh:
  1. looks at the download host (the server's /downloads, which is the R2 bucket; -From names another base) for the newest build;
  2. finds an older agent (cm-agent, the old Claude Code plugin, an old autostart task) and asks before removing it; "no" changes nothing;
  3. carries the old settings and login over (agent.json and the two tokens in Credential Manager);
  4. installs the build into %USERPROFILE%\.aipod\bin, checked against the manifest's SHA-256;
  5. adds or updates the Claude Code plugin (aipod@aipod-releases); when the machine is not connected yet, logs in (a code to approve on the web).
-RemoveOld only removes the old agent (the removal script). -Purge also deletes the old data folders and the old Credential Manager entries,
after the carry-over. Tokens are copied between the user's own Credential Manager entries and are never printed. Nothing here needs
administrator rights; an old Windows service (service mode) does, and is only named, with the command to run.
The Windows build is not code-signed yet, so the check is the manifest's SHA-256 over https.

.EXAMPLE
.\aipod-install.ps1 -Server https://<your server>
.\aipod-install.ps1 -RemoveOld
#>
[CmdletBinding()]
param(
    [string]$Server = $env:AIPOD_SERVER,
    [string]$From = '',
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$NoPlugin,
    [switch]$NoLogin,
    [switch]$RemoveOld,
    [switch]$Purge
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'aipod-install.ps1 is for Windows; on macOS and Linux run aipod-install.sh' }

$arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$arch = switch -Regex ($arch) { 'ARM64' { 'arm64' } 'AMD64|x64' { 'x64' } default { throw "no build for the CPU $arch" } }

$NewHome = if ($env:AP_AGENT_HOME) { $env:AP_AGENT_HOME } else { Join-Path $env:USERPROFILE '.aipod' }
$OldHomes = @($(if ($env:CM_AGENT_HOME) { $env:CM_AGENT_HOME }), (Join-Path $env:USERPROFILE '.claude-monitor'), (Join-Path $env:LOCALAPPDATA 'ClaudeMonitor')) |
    Where-Object { $_ -and $_ -ne $NewHome }
$ClaudeDir = if ($env:CLAUDE_CONFIG_DIR) { $env:CLAUDE_CONFIG_DIR } else { Join-Path $env:USERPROFILE '.claude' }
$NewBin = Join-Path $NewHome 'bin\aipod.exe'
$OldService = 'claude-monitor-agent' # the Credential Manager prefix the old agent kept its two tokens under
$NewService = 'aipod'
$Accounts = 'access-token', 'refresh-token'

function Say([string]$text) { Write-Host $text }
function Warn([string]$text) { Write-Warning $text }
function Die([string]$text) { throw "aipod install: $text" }
function Did([string]$text) { if (-not $DryRun) { Say $text } }
function Act([string]$what, [scriptblock]$do) { if ($DryRun) { Say "  [dry run] $what"; return $true }; & $do }
function Run-Native([string]$exe, [string[]]$arguments) { & $exe @arguments *> $null; return ($LASTEXITCODE -eq 0) }
function Have-Claude { return [bool](Get-Command claude -ErrorAction SilentlyContinue) }
function Newer([string]$a, [string]$b) { try { return ([version]$a) -gt ([version]$b) } catch { return $false } }

function Confirm-Step([string]$question, [bool]$default) {
    if ($Yes) { return $true }
    if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) {
        Warn "$question (no terminal to ask on: answered '$(if ($default) { 'y' } else { 'n' })'; use -Yes to answer yes)"
        return $default
    }
    $hint = if ($default) { '[Y/n]' } else { '[y/N]' }
    $answer = Read-Host "$question $hint"
    if ($answer -match '^(y|yes)$') { return $true }
    if ($answer -match '^(n|no)$') { return $false }
    return $default
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ApCred {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL { public int Flags; public int Type; public string TargetName; public string Comment; public long LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public int Persist; public int AttributeCount; public IntPtr Attributes;
        public string TargetAlias; public string UserName; }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);
    public static string Read(string target) {
        IntPtr p;
        if (!CredRead(target, 1, 0, out p)) return null;
        try {
            CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
            return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
        } finally { CredFree(p); }
    }
    public static bool Write(string target, string secret) {
        byte[] blob = Encoding.Unicode.GetBytes(secret);
        IntPtr h = Marshal.AllocHGlobal(blob.Length);
        try {
            Marshal.Copy(blob, 0, h, blob.Length);
            CREDENTIAL c = new CREDENTIAL();
            c.Type = 1; c.TargetName = target; c.CredentialBlobSize = (uint)blob.Length; c.CredentialBlob = h; c.Persist = 2; c.UserName = Environment.UserName;
            return CredWrite(ref c, 0);
        } finally { Marshal.FreeHGlobal(h); }
    }
    public static void Delete(string target) { CredDelete(target, 1, 0); }
}
'@

# ---------------------------------------------------------------- finding an older agent

function Get-OldBinaries {
    $found = @()
    foreach ($h in $OldHomes + $NewHome) { $found += Join-Path $h 'bin\cm-agent.exe'; $found += Join-Path $h 'bin\cm-agentw.exe' }
    foreach ($pattern in 'monitor-agent-*', 'aipod-*') {
        $found += Get-ChildItem -Path (Join-Path $ClaudeDir "plugins\data\$pattern\bin") -Filter 'cm-agent*.exe' -ErrorAction SilentlyContinue | ForEach-Object FullName
    }
    return @($found | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -Unique)
}
function Get-OldPluginIds {
    if (-not (Have-Claude)) { return @() }
    try { return @((claude plugin list --json 2>$null | ConvertFrom-Json) | ForEach-Object id | Where-Object { $_ -like 'monitor-agent@*' }) } catch { return @() }
}
function Get-OldMarketplaces {
    if (-not (Have-Claude)) { return @() }
    try { return @((claude plugin marketplace list --json 2>$null | ConvertFrom-Json) | ForEach-Object name | Where-Object { $_ -like 'monitor-agent-*' }) } catch { return @() }
}
function Get-OldTask { return @(Get-ScheduledTask -TaskName 'ClaudeMonitorAgent' -ErrorAction SilentlyContinue) }
function Get-OldService { return @(Get-Service -Name 'cm-agent' -ErrorAction SilentlyContinue) }
function Get-OldDaemons {
    $binaries = Get-OldBinaries
    return @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.ExecutablePath -and ($binaries -contains $_.ExecutablePath) -and $_.CommandLine -match '\sdaemon\s*$' })
}
function Test-FoundOld { return ((Get-OldBinaries).Count + (Get-OldPluginIds).Count + (Get-OldMarketplaces).Count + (Get-OldTask).Count + (Get-OldDaemons).Count) -gt 0 }
function Show-Old {
    Get-OldBinaries | ForEach-Object { Say "  binary: $_" }
    Get-OldPluginIds | ForEach-Object { Say "  Claude Code plugin: $_" }
    Get-OldMarketplaces | ForEach-Object { Say "  marketplace: $_" }
    Get-OldTask | ForEach-Object { Say "  autostart task: $($_.TaskName)" }
    Get-OldDaemons | ForEach-Object { Say "  running daemon, pid $($_.ProcessId)" }
}

# ---------------------------------------------------------------- carrying the settings and the login over

function Get-IdentityHome { foreach ($h in @($NewHome) + $OldHomes) { if (Test-Path -LiteralPath (Join-Path $h 'agent.json')) { return $h } } }

function Copy-Carry {
    $src = Get-IdentityHome
    if (-not $src) { return }
    Say "Carrying the old settings and login over from $src"
    if ($DryRun) { Say "  [dry run] agent.json and the tokens would be copied into $NewHome"; return }
    New-Item -ItemType Directory -Force -Path $NewHome | Out-Null
    if ($src -ne $NewHome -and -not (Test-Path -LiteralPath (Join-Path $NewHome 'agent.json'))) {
        Copy-Item -LiteralPath (Join-Path $src 'agent.json') -Destination (Join-Path $NewHome 'agent.json')
        Say '  copied agent.json (server, machine identity, saved settings)'
    }
    foreach ($account in $Accounts) {
        if ($null -ne [ApCred]::Read("$NewService/$account")) { continue } # one already there is never overwritten
        $secret = [ApCred]::Read("$OldService/$account")
        if ($null -eq $secret) { continue }
        if ([ApCred]::Write("$NewService/$account", $secret)) { Say "  carried the $account over to Credential Manager" }
        $secret = $null
    }
}

# ---------------------------------------------------------------- removing an older agent

function Stop-OldDaemons {
    if ((Get-OldDaemons).Count -eq 0) { return }
    foreach ($h in $OldHomes + $NewHome) {
        if (Test-Path -LiteralPath (Join-Path $h 'daemon.pid')) { Set-Content -LiteralPath (Join-Path $h 'daemon.stop') -Value ([DateTime]::UtcNow.ToString('o')) -NoNewline }
    }
    for ($n = 0; $n -lt 12 -and (Get-OldDaemons).Count -gt 0; $n++) { Start-Sleep -Seconds 1 }
    Get-OldDaemons | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($h in $OldHomes + $NewHome) { Remove-Item -LiteralPath (Join-Path $h 'daemon.stop') -Force -ErrorAction SilentlyContinue }
}

function Remove-OldAgent {
    Say 'Removing the old agent'
    $first = Get-OldBinaries | Select-Object -First 1
    if ((Get-OldService).Count -gt 0) {
        Warn "  an old Windows service (cm-agent) is installed; that needs an administrator: & '$first' uninstall --service"
    }
    if ((Get-OldTask).Count -gt 0) {
        if ($first) { if (Act 'run: cm-agent uninstall --standalone' { Run-Native $first @('uninstall', '--standalone') }) { Did '  removed the old autostart' } }
        else { Warn '  an old autostart task is registered and no old binary is left to remove it with' }
    }
    if (Have-Claude) {
        foreach ($id in Get-OldPluginIds) {
            if (Act "claude plugin uninstall $id" { Run-Native 'claude' @('plugin', 'uninstall', $id) }) { Did "  uninstalled the Claude Code plugin $id" }
        }
        foreach ($m in Get-OldMarketplaces) {
            if (Act "claude plugin marketplace remove $m" { Run-Native 'claude' @('plugin', 'marketplace', 'remove', $m) }) { Did "  removed the marketplace $m" }
        }
    }
    if ($DryRun) { Say '  [dry run] the old daemon would be stopped and the old binaries deleted' }
    else {
        Stop-OldDaemons
        foreach ($f in Get-OldBinaries) { Remove-Item -LiteralPath $f -Force; Say "  deleted $f" }
    }
    if ($Purge) {
        if (-not $DryRun -and -not (Test-Path -LiteralPath (Join-Path $NewHome 'agent.json'))) {
            Warn '  -Purge skipped: the new home has no agent.json yet, so the old data would be the only copy'
        }
        else {
            foreach ($h in $OldHomes) { if (Test-Path -LiteralPath $h) { if (Act "delete $h" { Remove-Item -LiteralPath $h -Recurse -Force; $true }) { Did "  deleted $h" } } }
            foreach ($account in $Accounts) { Act "delete Credential Manager entry $OldService/$account" { [ApCred]::Delete("$OldService/$account"); $true } | Out-Null }
        }
    }
    else {
        foreach ($h in $OldHomes) { if (Test-Path -LiteralPath $h) { Say "  kept the old data folder $h (delete it when you are happy; -Purge does it)" } }
    }
}

# ---------------------------------------------------------------- the removal script

if ($RemoveOld) {
    if (-not (Test-FoundOld)) { Say 'No old agent found.'; return }
    Say 'An old agent was found:'
    Show-Old
    if (-not (Confirm-Step 'Remove the old agent?' $false)) { Say 'Nothing was changed.'; return }
    if ($Purge) { Copy-Carry }
    Remove-OldAgent
    return
}

# ---------------------------------------------------------------- the server and the download host

function Test-Url([string]$url) { return $url -match '^(https://[A-Za-z0-9._:/@%~+-]+|file:///[A-Za-z0-9._/%~+ :-]+|http://(localhost|127\.0\.0\.1)(:[0-9]+)?(/[A-Za-z0-9._/%~+-]*)?)$' }
function Get-Remote([string]$url, [string]$out) {
    if ($url -like 'file:///*') { Copy-Item -LiteralPath ([Uri]$url).LocalPath -Destination $out; return }
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $out -TimeoutSec 300
}

if (-not $Server) {
    $h = Get-IdentityHome
    if ($h) { try { $Server = (Get-Content -LiteralPath (Join-Path $h 'agent.json') -Raw | ConvertFrom-Json).server } catch { $Server = '' } }
}
if (-not $Server -and -not $From) {
    if ([Console]::IsInputRedirected) { Die 'the server address is needed: -Server https://<your server> (or AIPOD_SERVER)' }
    $Server = Read-Host 'Server address (https://...)'
}
$Server = "$Server".TrimEnd('/')
if (-not $From) { $From = "$Server/downloads" }
$From = $From.TrimEnd('/')
if ($Server -and -not (Test-Url $Server)) { Die 'the server address must be https://<host>' }
if (-not (Test-Url $From)) { Die 'the download address must be https://<host>/...' }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("aipod-install-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Say "Looking for the newest build at $From"
    Get-Remote "$From/manifest.json" (Join-Path $tmp 'manifest.json')
    $entries = @((Get-Content -LiteralPath (Join-Path $tmp 'manifest.json') -Raw | ConvertFrom-Json).entries |
            Where-Object { $_.os -eq 'windows' -and $_.arch -eq $arch })
    $best = $null
    foreach ($e in $entries) { if ($e.version -and ($null -eq $best -or (Newer $e.version $best.version))) { $best = $e } }
    if (-not $best -or -not $best.file -or -not $best.sha256) { Die "the download host has no build for windows-$arch" }
    if ($best.file -notmatch '^aipod-windows-(arm64|x64)\.zip$') { Die "refused: $($best.file) is not a build file name" }
    $version = $best.version
    Say "  newest build: aipod $version (windows-$arch)"

    # ------------------------------------------------------------ deciding
    $oldPresent = $false
    $installed = ''
    if (Test-Path -LiteralPath $NewBin) { try { $installed = ((& $NewBin version 2>$null) | Select-Object -First 1).Trim() } catch { $installed = '' } }
    if (Test-FoundOld) {
        $oldPresent = $true
        Say 'An older agent is installed:'
        Show-Old
        $ov = ''
        $firstOld = Get-OldBinaries | Select-Object -First 1
        if ($firstOld) { try { $ov = ((& $firstOld version 2>$null) | Select-Object -First 1).Trim() } catch { $ov = '' } }
        $label = if ($ov) { " (cm-agent $ov)" } else { '' }
        if (-not (Confirm-Step "Remove the old agent$label and install aipod ${version}? Your settings and login are kept." $false)) {
            Say 'Nothing was changed. The old agent stays; run this again when you are ready.'
            return
        }
    }
    elseif ($installed) {
        if (Newer $version $installed) {
            if (-not (Confirm-Step "aipod $installed is installed; update to ${version}?" $true)) { Say 'Nothing was changed.'; return }
        }
        else { Say "aipod $installed is installed and is the newest build." }
    }

    $fresh = -not (Get-IdentityHome)
    Copy-Carry
    if ($oldPresent) { Remove-OldAgent }

    # ------------------------------------------------------------ installing the build
    if (-not $installed -or (Newer $version $installed)) {
        Say "Installing aipod $version"
        if ($DryRun) { Say "  [dry run] $($best.file) would be downloaded, checked and put at $NewBin" }
        else {
            $zip = Join-Path $tmp $best.file
            Get-Remote "$From/$($best.file)" $zip
            if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $best.sha256.ToLowerInvariant()) { Die "refused: $($best.file) does not match the SHA-256 in the manifest" }
            Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $tmp 'x') -Force
            if (-not (Test-Path -LiteralPath (Join-Path $tmp 'x\aipod.exe'))) { Die 'the download holds no aipod.exe' }
            New-Item -ItemType Directory -Force -Path (Split-Path $NewBin) | Out-Null
            if (Test-Path -LiteralPath $NewBin) { Remove-Item -LiteralPath "$NewBin.old" -Force -ErrorAction SilentlyContinue; Move-Item -LiteralPath $NewBin -Destination "$NewBin.old" -Force } # a running program can be renamed, not overwritten
            Move-Item -LiteralPath (Join-Path $tmp 'x\aipod.exe') -Destination $NewBin -Force
            Say "  installed $NewBin"
            foreach ($extra in 'aipodw.exe') { if (Test-Path -LiteralPath (Join-Path $tmp "x\$extra")) { Move-Item -LiteralPath (Join-Path $tmp "x\$extra") -Destination (Join-Path (Split-Path $NewBin) $extra) -Force } }
            $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
            if (($userPath -split ';') -notcontains (Split-Path $NewBin)) {
                [Environment]::SetEnvironmentVariable('Path', "$userPath;$(Split-Path $NewBin)".TrimStart(';'), 'User')
                Say "  added $(Split-Path $NewBin) to your PATH (a new terminal sees it)"
            }
        }
    }

    # ------------------------------------------------------------ the login
    if ($DryRun) { Say '  [dry run] the machine would be connected (the old login carried over, or a login)' }
    elseif (-not $NoLogin) {
        $env:AP_LANG = 'en'
        $connected = (& $NewBin status 2>$null) -match '^connected:'
        if ($connected) {
            Say 'This machine is already connected (its login was kept).'
            & $NewBin restart *> $null
        }
        else {
            if (-not $Server) { Die 'no server address to log in to: -Server https://<your server>' }
            Say 'Connecting this machine: approve the code on the web page it shows.'
            & $NewBin login --server $Server
            if ($LASTEXITCODE -ne 0) { Warn "the login did not finish; run: aipod login --server $Server" }
        }
        # A new machine gets the inbox push (web messages start a turn in an idle session); a saved choice is never touched.
        if ($fresh) { & $NewBin config push inbox *> $null }
    }

    # ------------------------------------------------------------ the Claude Code plugin
    if (-not $NoPlugin) {
        if (-not (Have-Claude)) {
            Say "Claude Code (the 'claude' command) was not found. When it is installed, run:"
            Say "  claude plugin marketplace add $From/aipod-marketplace.json"
            Say "  claude plugin install aipod@aipod-releases$(if ($Server) { " --config server=$Server" })"
        }
        else {
            Say 'Setting up the Claude Code plugin'
            $markets = @(); try { $markets = @((claude plugin marketplace list --json 2>$null | ConvertFrom-Json) | ForEach-Object name) } catch { $markets = @() }
            if ($markets -contains 'aipod-releases') { Act 'claude plugin marketplace update aipod-releases' { Run-Native 'claude' @('plugin', 'marketplace', 'update', 'aipod-releases') } | Out-Null }
            elseif (-not (Act "claude plugin marketplace add $From/aipod-marketplace.json" { Run-Native 'claude' @('plugin', 'marketplace', 'add', "$From/aipod-marketplace.json") })) {
                Warn "  could not add the marketplace $From/aipod-marketplace.json"
            }
            $plugins = @(); try { $plugins = @((claude plugin list --json 2>$null | ConvertFrom-Json) | ForEach-Object id) } catch { $plugins = @() }
            if ($plugins -contains 'aipod@aipod-releases') {
                if (Act 'claude plugin update aipod@aipod-releases' { Run-Native 'claude' @('plugin', 'update', 'aipod@aipod-releases') }) { Did '  the aipod plugin is up to date' }
            }
            else {
                # The server option lets the plugin download its own copy of the agent; without it the plugin finds the server in agent.json.
                $ok = $false
                if ($Server) { $ok = Act "claude plugin install aipod@aipod-releases --config server=$Server" { Run-Native 'claude' @('plugin', 'install', 'aipod@aipod-releases', '--config', "server=$Server") } }
                if (-not $ok) { $ok = Act 'claude plugin install aipod@aipod-releases' { Run-Native 'claude' @('plugin', 'install', 'aipod@aipod-releases') } }
                if ($ok) { Did '  installed the aipod plugin' } else { Warn '  the plugin was not installed; run: claude plugin install aipod@aipod-releases' }
            }
        }
    }

    Say ''
    Say 'Done. Restart Claude Code (or open a new session) so it loads the plugin; /aipod:status shows how it is doing.'
    if ($oldPresent) { Say 'A session that was open during the removal may report a hook error once; a new session does not.' }
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
