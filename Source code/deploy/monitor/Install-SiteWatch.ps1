# =====================================================================================
# Set up the OFFICE PC to watch theremotesupportprogram.com and email Clint when it goes down
# (deploy/monitor, 2026-10-27, HANDOVER §170). Run it ONCE on the office PC that copies the
# backups (deploy/backup), signed in as the same Windows account:
#
#   powershell -ExecutionPolicy Bypass -File Install-SiteWatch.ps1
#
# It asks for two things, in Windows' own password boxes (nothing is typed into this window):
#   1) a theremotesupportprogram.com MAILBOX to send from - its full address and password
#      (the one set on Owner -> Mail does nicely). Kept in %USERPROFILE%\.rsp-watch\mail.xml,
#      readable only by this account on this PC.
#   2) this Windows account's password, so the check runs while nobody is signed in.
# Then it sends a test email, checks the site once, and schedules the check every 2 minutes.
#
#   -To       who is told (default clint@anywhereit.co.za)
#   -Remove   stop watching: the scheduled task and the folder are removed
# =====================================================================================
param(
    [string]$To = "clint@anywhereit.co.za",
    [string]$Url = "https://theremotesupportprogram.com",
    [string]$SmtpHost = "mail.theremotesupportprogram.com",
    [int]$SmtpPort = 587,
    [string]$BackupOkFile = "\\192.168.1.33\anywhereit\Backups\RemoteSupport\LAST-OK.txt",
    [switch]$Remove
)
$ErrorActionPreference = "Stop"
$taskName = "Remote Support - watch the website"
$home2 = Join-Path $env:USERPROFILE ".rsp-watch"
$cfg = Join-Path $home2 "config.json"

if ($Remove) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    if (Test-Path $home2) { Remove-Item $home2 -Recurse -Force }
    Write-Host "Done. This PC no longer watches the website." -ForegroundColor Green
    exit 0
}

Write-Host "== 1 of 3: the mailbox it sends from ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $home2 -Force | Out-Null
$mail = Get-Credential -Message "A theremotesupportprogram.com mailbox to send the alerts from: its FULL address as the user name, and its password"
if (-not $mail) { throw "Cancelled - nothing was set up." }
if ($mail.UserName -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { throw "'$($mail.UserName)' is not an email address. Run it again and type the mailbox's full address as the user name." }
$mail | Export-Clixml (Join-Path $home2 "mail.xml")
Copy-Item (Join-Path $PSScriptRoot "Watch-Site.ps1") (Join-Path $home2 "Watch-Site.ps1") -Force
if (-not (Test-Path $BackupOkFile)) {
    Write-Host "   (no $BackupOkFile here - the backup copy is not watched; set deploy\backup up and run this again to add it)" -ForegroundColor Yellow
    $BackupOkFile = ""
}
[ordered]@{ Url = $Url; To = $To; SmtpHost = $SmtpHost; SmtpPort = $SmtpPort; BackupOkFile = $BackupOkFile; CertWarnDays = 14;
            InternetProbes = @("1.1.1.1:443", "8.8.8.8:443") } | ConvertTo-Json | Set-Content $cfg -Encoding ASCII

Write-Host "   sending a test email to $To ..."
# Through the check's own sending, so the test proves the very path an alert takes.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $home2 "Watch-Site.ps1") -Config $cfg -TestEmail
if ($LASTEXITCODE -ne 0) { throw "The test email did not go (the line above says why). Check the mailbox address and password - run this again - and that $SmtpHost is reachable on port $SmtpPort." }
Write-Host "   sent - look for 'The website check is set up' in $To's inbox" -ForegroundColor Green

Write-Host "== 2 of 3: one check now ==" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $home2 "Watch-Site.ps1") -Config $cfg
Write-Host ("   " + (Get-Content (Join-Path $home2 "watch-log.txt") -Tail 1))

Write-Host "== 3 of 3: every 2 minutes ==  (Windows asks for this account's password, to run it while nobody is signed in)" -ForegroundColor Cyan
$cred = Get-Credential -UserName "$env:USERDOMAIN\$env:USERNAME" -Message "This Windows account's password, so the website check can run while nobody is signed in"
if (-not $cred) { throw "Cancelled - the check works, but is not scheduled." }
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$(Join-Path $home2 'Watch-Site.ps1')`" -Config `"$cfg`""
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 2)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 5) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -User $cred.UserName -Password $cred.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null

Write-Host "`nDone. Every 2 minutes this PC checks $Url and emails $To if it stops answering." -ForegroundColor Green
Write-Host "Keep this PC switched on and set never to sleep (Settings > System > Power). Its log: $(Join-Path $home2 'watch-log.txt')"
