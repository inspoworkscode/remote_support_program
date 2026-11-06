# =====================================================================================
# The nightly copy of the live server's backup to the office share (deploy/backup, 2026-10-08).
# Run by the scheduled task Install-BackupPull.ps1 makes; safe to run by hand at any time.
#
# It asks the server for its list of nightly backups (name, size, SHA-256), fetches the newest
# one this share does not already hold, checks it byte for byte against the server's own SHA-256
# and only then keeps it. Copies older than KeepDays are removed. Every run writes a line to
# pull-log.txt beside the copies; a good run also rewrites LAST-OK.txt. The copies are still
# AES-256 encrypted: the passphrase is NOT here and must never be put here.
#
#   powershell -ExecutionPolicy Bypass -File Pull-Backup.ps1 [-Config <path to config.json>]
# =====================================================================================
param([string]$Config = (Join-Path $env:USERPROFILE ".rsp-backup\config.json"))
$ErrorActionPreference = "Stop"
$c = Get-Content $Config -Raw | ConvertFrom-Json
$ssh = if ($c.Ssh) { $c.Ssh } else { "$env:WINDIR\System32\OpenSSH\ssh.exe" }
$dest = $c.Dest
New-Item -ItemType Directory -Path $dest -Force | Out-Null
$log = Join-Path $dest "pull-log.txt"
function Log($text) {
    $line = "{0:yyyy-MM-dd HH:mm:ss}  {1}  {2}" -f (Get-Date), $env:COMPUTERNAME, $text
    Add-Content -Path $log -Value $line
    Write-Host $line
}

# Every argument quoted for the one command line Windows hands ssh.exe.
function Q($s) { '"' + ($s -replace '"', '\"') + '"' }
$baseArgs = @("-i", $c.Key, "-o", "IdentitiesOnly=yes", "-o", "UserKnownHostsFile=$($c.KnownHosts)",
    "-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes", "-o", "ConnectTimeout=30", "$($c.User)@$($c.Server)")

# Runs ssh with one request. With $toFile, its output goes there as raw bytes: PowerShell's own
# redirection would turn a binary file into text and ruin it.
function Ask($request, $toFile) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $ssh
    $psi.Arguments = ((@($baseArgs) + $request) | ForEach-Object { Q $_ }) -join " "
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEndAsync()
    if ($toFile) {
        $fs = [System.IO.File]::Create($toFile)
        try { $p.StandardOutput.BaseStream.CopyTo($fs) } finally { $fs.Close() }
        $text = ""
    } else { $text = $p.StandardOutput.ReadToEnd() }
    $p.WaitForExit()
    return [pscustomobject]@{ Code = $p.ExitCode; Text = $text; Err = $err.Result.Trim() }
}

try {
    $list = Ask "list" $null
    if ($list.Code -ne 0) { throw "the server refused the list (exit $($list.Code)): $($list.Err)" }
    $backups = @($list.Text -split "`n" | Where-Object { $_.Trim() } | ForEach-Object {
        $f = $_.Trim() -split " "
        [pscustomobject]@{ Name = $f[0]; Size = [long]$f[1]; Sha = $f[2].ToUpperInvariant() }
    } | Sort-Object Name)
    if ($backups.Count -eq 0) { throw "the server has no nightly backups at all" }
    $newest = $backups[-1]

    # The server's nightly backup itself may have stopped: say so, loudly, every night until it is fixed.
    # The server makes one at 02:30 its own time, so after 03:00 today's should be there; before 03:00 (a PC
    # catching up after being off), yesterday's is the newest there can be.
    $made = [datetime]::ParseExact($newest.Name.Substring(4, 8), "yyyyMMdd", $null)
    $due = if ((Get-Date).Hour -ge 3) { (Get-Date).Date } else { (Get-Date).Date.AddDays(-1) }
    if ($made -lt $due) { Log "WARNING: the server's newest backup is from $($made.ToString('yyyy-MM-dd')) - its own nightly backup may have stopped" }

    $target = Join-Path $dest $newest.Name
    if ((Test-Path $target) -and (Get-FileHash $target -Algorithm SHA256).Hash -eq $newest.Sha) {
        Log "already have $($newest.Name), it matches"
    } else {
        $partial = "$target.partial"
        $got = Ask @("get", $newest.Name) $partial
        if ($got.Code -ne 0) { Remove-Item $partial -ErrorAction SilentlyContinue; throw "the server would not send $($newest.Name) (exit $($got.Code)): $($got.Err)" }
        $size = (Get-Item $partial).Length
        $sha = (Get-FileHash $partial -Algorithm SHA256).Hash
        if ($size -ne $newest.Size -or $sha -ne $newest.Sha) {
            Remove-Item $partial -ErrorAction SilentlyContinue
            throw "$($newest.Name) arrived damaged ($size bytes, expected $($newest.Size)) - not kept"
        }
        Move-Item $partial $target -Force
        Log ("copied {0}, {1:N1} MB, SHA-256 matches the server's" -f $newest.Name, ($size / 1MB))
    }

    # Older than KeepDays, by the date in the name (a copy's file time is when it was copied).
    $cutoff = (Get-Date).Date.AddDays(-[int]$c.KeepDays)
    Get-ChildItem $dest -Filter "rsp-*.tar.gz.gpg" | Where-Object {
        $_.Name -match '^rsp-(\d{8})\.tar\.gz\.gpg$' -and [datetime]::ParseExact($Matches[1], "yyyyMMdd", $null) -lt $cutoff
    } | ForEach-Object { Remove-Item $_.FullName; Log "removed $($_.Name), older than $($c.KeepDays) days" }
    Get-ChildItem $dest -Filter "*.partial" | Remove-Item -ErrorAction SilentlyContinue

    Set-Content (Join-Path $dest "LAST-OK.txt") ("Last good copy: {0:yyyy-MM-dd HH:mm} on {1}, newest backup {2}" -f (Get-Date), $env:COMPUTERNAME, $newest.Name)
    exit 0
} catch {
    Log "FAILED: $($_.Exception.Message)"
    exit 1
}
