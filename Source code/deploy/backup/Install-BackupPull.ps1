# =====================================================================================
# Set up the OFFICE PC that copies the live server's backups to the share every night
# (deploy/backup, 2026-10-08). Run it twice on that PC, signed in as the Windows account that
# can open the share:
#
#   1)  powershell -ExecutionPolicy Bypass -File Install-BackupPull.ps1
#         makes this PC's own key and puts its PUBLIC half on the share for step 2
#   2)  (on Clint's laptop: Enable-OffsiteBackup.ps1)
#   3)  powershell -ExecutionPolicy Bypass -File Install-BackupPull.ps1 -Finish
#         checks the server is the real one, copies a backup now, and schedules it nightly
#
# The private key never leaves this PC (%USERPROFILE%\.rsp-backup). On the server it can only
# list and fetch backups, and only from the office's address.
# =====================================================================================
param(
    [switch]$Finish,
    [string]$Server = "154.65.110.136",
    [string]$Dest = "\\192.168.1.33\anywhereit\Backups\RemoteSupport",
    [int]$KeepDays = 60,
    [string]$At = "04:00"
)
$ErrorActionPreference = "Stop"
$home2 = Join-Path $env:USERPROFILE ".rsp-backup"
$key = Join-Path $home2 "rsp-backup-key"
$known = Join-Path $home2 "known_hosts"
$cfg = Join-Path $home2 "config.json"
$ssh = "$env:WINDIR\System32\OpenSSH\ssh.exe"
$keygen = "$env:WINDIR\System32\OpenSSH\ssh-keygen.exe"
$keyscan = "$env:WINDIR\System32\OpenSSH\ssh-keyscan.exe"
foreach ($t in $ssh, $keygen, $keyscan) {
    if (-not (Test-Path $t)) { throw "Windows' OpenSSH Client is not installed ($t). Settings > Apps > Optional features > Add > OpenSSH Client, then run this again." }
}

if (-not $Finish) {
    Write-Host "== 1 of 2: this PC's key ==" -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $home2 -Force | Out-Null
    try { New-Item -ItemType Directory -Path $Dest -Force | Out-Null; Set-Content (Join-Path $Dest "write-test.txt") "ok"; Remove-Item (Join-Path $Dest "write-test.txt") }
    catch { throw "This PC cannot write to $Dest as $env:USERNAME. Sign in as the account that can open the share, then run this again." }
    Write-Host "   the share is reachable: $Dest"
    if (-not (Test-Path $key)) {
        # No passphrase: it has to work at 04:00 with nobody there. What protects it is what the server lets it do.
        # One argument string, handed over exactly as written: Windows PowerShell drops an empty "" on its way to a program.
        Start-Process -FilePath $keygen -ArgumentList "-q -t ed25519 -N `"`" -C rsp-backup-pull@$env:COMPUTERNAME -f `"$key`"" -Wait -NoNewWindow
        if (-not (Test-Path "$key.pub")) { throw "ssh-keygen did not make the key" }
    }
    [ordered]@{ Server = $Server; User = "rspcopy"; Key = $key; KnownHosts = $known; Dest = $Dest; KeepDays = $KeepDays } |
        ConvertTo-Json | Set-Content $cfg -Encoding ASCII
    Copy-Item "$key.pub" (Join-Path $Dest "rsp-backup-key.pub") -Force
    Copy-Item (Join-Path $PSScriptRoot "Pull-Backup.ps1") (Join-Path $home2 "Pull-Backup.ps1") -Force
    Write-Host "`nDone. The public half is on the share as rsp-backup-key.pub." -ForegroundColor Green
    Write-Host "Next, on Clint's laptop:  powershell -ExecutionPolicy Bypass -File deploy\backup\Enable-OffsiteBackup.ps1"
    exit 0
}

Write-Host "== 2 of 2: check the server, copy one now, schedule it ==" -ForegroundColor Cyan
if (-not (Test-Path $cfg)) { throw "Run this without -Finish first." }
$fpFile = Join-Path $Dest "server-fingerprint.txt"
if (-not (Test-Path $fpFile)) { throw "No $fpFile yet - run Enable-OffsiteBackup.ps1 on Clint's laptop first." }
$expected = (Get-Content $fpFile -Raw).Trim()

# The server's own key, fetched, then checked against the fingerprint Clint's laptop read from the
# server over its trusted connection: a stranger answering at that address is refused here.
$scanned = & $keyscan -t ed25519 $Server 2>$null
if (-not $scanned) { throw "Could not reach $Server on port 22 from this PC." }
($scanned | Where-Object { $_ -notmatch '^#' }) | Set-Content $known -Encoding ASCII
$got = ((& $keygen -lf $known) -split " ")[1]
if ($got -ne $expected) { Remove-Item $known; throw "The server at $Server answered with key $got, but the real server's is $expected. NOT trusted - nothing was set up." }
Write-Host "   the server is the real one ($got)"

Write-Host "   copying the newest backup now..."
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $home2 "Pull-Backup.ps1") -Config $cfg
if ($LASTEXITCODE -ne 0) { throw "The first copy failed - see the line above and $Dest\pull-log.txt." }

Write-Host "   scheduling it every night at $At (Windows asks for this account's password, to run it while nobody is signed in)"
$cred = Get-Credential -UserName "$env:USERDOMAIN\$env:USERNAME" -Message "This Windows account's password, so the nightly copy can run while nobody is signed in"
if (-not $cred) { throw "Cancelled - the copy works, but is not scheduled." }
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $home2 'Pull-Backup.ps1')`" -Config `"$cfg`""
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -ExecutionTimeLimit (New-TimeSpan -Hours 2) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 30)
Register-ScheduledTask -TaskName "Remote Support - copy the server backup" -Action $action -Trigger $trigger -Settings $settings `
    -User $cred.UserName -Password $cred.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null
Write-Host "`nDone. Every night at $At this PC copies the server's newest backup to $Dest." -ForegroundColor Green
Write-Host "It catches up if the PC was off at that time. Check $Dest\LAST-OK.txt to see the last good copy."
