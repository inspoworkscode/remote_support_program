# =====================================================================================
# Proves deploy\monitor\Watch-Site.ps1 (HANDOVER §170) case by case, on this PC, sending no email:
# every email goes to an -Outbox folder. "Up" is the REAL live site's /health (one GET each);
# "down" is a closed port on this PC; "the office offline" is internet probes that cannot answer.
#
#   powershell -ExecutionPolicy Bypass -File tools\Test-SiteWatch.ps1
# =====================================================================================
$ErrorActionPreference = "Stop"
$watch = Join-Path $PSScriptRoot "..\deploy\monitor\Watch-Site.ps1"
$work = Join-Path $env:TEMP ("rsp-sitewatch-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$box = Join-Path $work "outbox"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$cfg = Join-Path $work "config.json"
$pass = 0; $fail = 0
function Check([string]$what, [bool]$ok, [string]$detail = "") {
    if ($ok) { $script:pass++; Write-Host "  PASS  $what" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $what $detail" -ForegroundColor Red }
}
function Set-Config([string]$url, [string[]]$probes = @("1.1.1.1:443", "8.8.8.8:443"), [string]$okFile = "", [int]$certDays = 14, [string]$smtp = "127.0.0.1", [int]$port = 9) {
    [ordered]@{ Url = $url; To = "clint@example.test"; SmtpHost = $smtp; SmtpPort = $port; BackupOkFile = $okFile; CertWarnDays = $certDays; InternetProbes = $probes } |
        ConvertTo-Json | Set-Content $cfg -Encoding ASCII
}
function Run([switch]$NoOutbox) {
    if ($NoOutbox) { & powershell -NoProfile -ExecutionPolicy Bypass -File $watch -Config $cfg | Out-Null }
    else { & powershell -NoProfile -ExecutionPolicy Bypass -File $watch -Config $cfg -Outbox $box | Out-Null }
    return $LASTEXITCODE
}
function Mails { @(Get-ChildItem $box -Filter *.txt -ErrorAction SilentlyContinue | Sort-Object Name) }
function State { Get-Content (Join-Path $work "state.json") -Raw | ConvertFrom-Json }
function LastLog { Get-Content (Join-Path $work "watch-log.txt") -Tail 1 }

$live = "https://theremotesupportprogram.com"
$dead = "https://127.0.0.1:9"
# A stand-in mailbox password, for the one case that really tries to send (to a closed port).
New-Object Management.Automation.PSCredential("watch@example.test", (ConvertTo-SecureString "not-a-real-password" -AsPlainText -Force)) |
    Export-Clixml (Join-Path $work "mail.xml")

Write-Host "== the site up, then a miss, then down =="
Set-Config $live
Check "the live site up: exit 0, no email" ((Run) -eq 0 -and (Mails).Count -eq 0)
Check "...logged as up, with the time it took" ((LastLog) -match '  up \(\d+ ms\)$') (LastLog)
Set-Config $dead
Run | Out-Null
Check "ONE miss (a deploy's restart): no email yet" ((Mails).Count -eq 0 -and (State).Fails -eq 1)
Set-Config $live
Run | Out-Null
Check "...and the next check up again: still no email, the count back to 0" ((Mails).Count -eq 0 -and (State).Fails -eq 0 -and (LastLog) -match 'one missed check')
Set-Config $dead
Run | Out-Null; Run | Out-Null
$m = Mails
Check "two misses in a row: ONE email, 'is DOWN'" ($m.Count -eq 1 -and (Get-Content $m[0].FullName -Raw) -match 'Subject: 127\.0\.0\.1 is DOWN') "$($m.Count) emails"
Check "...saying what was seen and what to look at" ((Get-Content $m[0].FullName -Raw) -match 'What the check saw' -and (Get-Content $m[0].FullName -Raw) -match 'journalctl')
Run | Out-Null; Run | Out-Null
Check "still down two checks later: no second email" ((Mails).Count -eq 1 -and (State).Fails -eq 4)

Write-Host "== the office offline =="
Set-Config $dead @("127.0.0.1:9", "127.0.0.1:7")
$before = (State).Fails
Run | Out-Null
Check "the office's internet down: no email, and the run counts for nothing" ((Mails).Count -eq 1 -and (State).Fails -eq $before)
Check "...logged as 'cannot tell'" ((LastLog) -match 'office offline') (LastLog)

Write-Host "== back up =="
Set-Config $live
Run | Out-Null
$m = Mails
Check "answering again: ONE email, 'is back up', with how long it was down" ($m.Count -eq 2 -and (Get-Content $m[1].FullName -Raw) -match 'Subject: theremotesupportprogram\.com is back up \(down about \d+ minutes\)') "$($m.Count) emails"
Check "...and everything reset" (-not (State).DownSent -and (State).Fails -eq 0)
Run | Out-Null
Check "up again: nothing more" ((Mails).Count -eq 2)

Write-Host "== the certificate =="
Set-Config $live -certDays 400   # the real certificate has under 90 days: as if it had under 400
Run | Out-Null
$m = Mails
Check "under the warning: ONE email, saying the date and the days left" ($m.Count -eq 3 -and (Get-Content $m[2].FullName -Raw) -match 'its certificate runs out in \d+ days') "$($m.Count) emails"
Run | Out-Null
Check "...once a day, not every 2 minutes" ((Mails).Count -eq 3)
Set-Config $live -certDays 14
$st = State; $st.CertWarnedOn = ""; $st | ConvertTo-Json | Set-Content (Join-Path $work "state.json")
Run | Out-Null
Check "the real certificate, with the real 14 days: no email" ((Mails).Count -eq 3)

Write-Host "== the backup copy =="
$okFile = Join-Path $work "LAST-OK.txt"
Set-Content $okFile "ok"
Set-Config $live -okFile $okFile
Run | Out-Null
Check "a copy made today: no email" ((Mails).Count -eq 3)
(Get-Item $okFile).LastWriteTime = (Get-Date).AddHours(-50)
$st = State; $st.BackupWarnedOn = ""; $st | ConvertTo-Json | Set-Content (Join-Path $work "state.json")
Run | Out-Null
$m = Mails
Check "nothing copied for 50 hours: ONE email" ($m.Count -eq 4 -and (Get-Content $m[3].FullName -Raw) -match 'no backup copied for 50 hours') "$($m.Count) emails"
Run | Out-Null
Check "...once a day" ((Mails).Count -eq 4)
Set-Config $live -okFile (Join-Path $work "no-share\LAST-OK.txt")
$st = State; $st.BackupWarnedOn = ""; $st | ConvertTo-Json | Set-Content (Join-Path $work "state.json")
Run | Out-Null
Check "the share out of reach: logged, no email" ((Mails).Count -eq 4 -and (Get-Content (Join-Path $work "watch-log.txt") -Tail 3) -match 'cannot see')

Write-Host "== an email that cannot go =="
Set-Config $dead -smtp "127.0.0.1" -port 9
Run -NoOutbox | Out-Null; Run -NoOutbox | Out-Null
Check "down, and the mail server refuses: nothing marked as told" (-not (State).DownSent -and (State).Fails -eq 2)
Check "...the failure logged, to be tried again" ((LastLog) -match 'DOWN email failed') (LastLog)
Run | Out-Null
Check "...and the next check, able to send, sends it" ((State).DownSent -and (Get-Content (Mails)[-1].FullName -Raw) -match 'is DOWN')
Set-Config $live; Run | Out-Null

Write-Host "== the test email =="
Set-Config $live
& powershell -NoProfile -ExecutionPolicy Bypass -File $watch -Config $cfg -TestEmail -Outbox $box | Out-Null
Check "-TestEmail sends 'The website check is set up' and stops" ($LASTEXITCODE -eq 0 -and (Get-Content (Mails)[-1].FullName -Raw) -match 'The website check is set up')
Set-Config $live -smtp "127.0.0.1" -port 9
& powershell -NoProfile -ExecutionPolicy Bypass -File $watch -Config $cfg -TestEmail 2>$null | Out-Null
Check "...and fails with exit 1 when the mail server cannot be reached (the installer stops)" ($LASTEXITCODE -eq 1)

Write-Host ""
Write-Host "Test-SiteWatch: $pass passed, $fail failed  (work folder $work)" -ForegroundColor $(if ($fail) { "Red" } else { "Green" })
if (-not $fail) { Remove-Item $work -Recurse -Force }
exit $fail
