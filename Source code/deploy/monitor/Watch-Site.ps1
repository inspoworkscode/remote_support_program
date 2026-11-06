# =====================================================================================
# Is theremotesupportprogram.com up? Run every 2 minutes on the OFFICE PC by the scheduled task
# Install-SiteWatch.ps1 makes (deploy/monitor, 2026-10-27, HANDOVER §170). Emails Clint:
#
#   - ONCE when the site stops answering (two checks in a row, so a deploy's restart of a few
#     seconds never sends one), and ONCE when it answers again, saying for how long it was down;
#   - once a day while the HTTPS certificate has under 14 days left (Caddy renews at 30, so this
#     means renewal is failing);
#   - once a day while the nightly backup copy to the share (deploy/backup) has not arrived for
#     36 hours.
#
# When the OFFICE's own internet is down nothing can be told either way: the run is logged and
# counts for nothing. The mail goes through the theremotesupportprogram.com mailbox on xneelo's
# shared hosting, which stays up when the server is down. Its password is kept in mail.xml,
# readable only by this Windows account on this PC (Export-Clixml).
#
#   -TestEmail         send the "it is set up" email and stop (Install-SiteWatch.ps1 uses it).
#   -Outbox <folder>   (testing only) write each email to a file there instead of sending it.
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Config,
    [switch]$TestEmail,
    [string]$Outbox
)
$ErrorActionPreference = "Stop"
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]'Tls13' }
catch { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 }

$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$dir = Split-Path $Config -Parent
$stateFile = Join-Path $dir "state.json"
$logFile = Join-Path $dir "watch-log.txt"
$now = Get-Date
$today = $now.ToString("yyyy-MM-dd")

function Log([string]$line) {
    Add-Content $logFile ("{0}  {1}" -f $now.ToString("yyyy-MM-dd HH:mm:ss"), $line)
    # Kept to its last 5000 lines: every 2 minutes is 720 lines a day.
    if ((Get-Item $logFile).Length -gt 600KB) { $keep = Get-Content $logFile -Tail 5000; Set-Content $logFile $keep }
}
function Get-Or($o, [string]$name, $default) { if ($o -and ($o.PSObject.Properties.Name -contains $name)) { $o.$name } else { $default } }

$s = $null
if (Test-Path $stateFile) { try { $s = Get-Content $stateFile -Raw | ConvertFrom-Json } catch { $s = $null } }
$state = [ordered]@{
    Fails        = [int](Get-Or $s "Fails" 0)
    FirstFailure = [string](Get-Or $s "FirstFailure" "")
    DownSent     = [bool](Get-Or $s "DownSent" $false)
    LastReason   = [string](Get-Or $s "LastReason" "")
    CertWarnedOn = [string](Get-Or $s "CertWarnedOn" "")
    BackupWarnedOn = [string](Get-Or $s "BackupWarnedOn" "")
}
function Save { [pscustomobject]$state | ConvertTo-Json | Set-Content $stateFile -Encoding UTF8 }

function Send([string]$subject, [string]$body) {
    $body += "`r`n`r`n-- `r`nSent by the site check on $env:COMPUTERNAME (deploy\monitor\Watch-Site.ps1). Its log: $logFile"
    if ($Outbox) {
        New-Item -ItemType Directory -Path $Outbox -Force | Out-Null
        $n = (Get-ChildItem $Outbox -Filter *.txt -ErrorAction SilentlyContinue | Measure-Object).Count + 1
        Set-Content (Join-Path $Outbox ("{0:000}.txt" -f $n)) "To: $($cfg.To)`r`nSubject: $subject`r`n`r`n$body" -Encoding UTF8
        return
    }
    $cred = Import-Clixml (Join-Path $dir "mail.xml")
    $msg = New-Object Net.Mail.MailMessage($cred.UserName, $cfg.To, $subject, $body)
    $smtp = New-Object Net.Mail.SmtpClient($cfg.SmtpHost, [int]$cfg.SmtpPort)
    $smtp.EnableSsl = $true          # on 587 this is STARTTLS
    $smtp.Timeout = 30000
    $smtp.Credentials = $cred.GetNetworkCredential()
    try { $smtp.Send($msg) } finally { $msg.Dispose(); $smtp.Dispose() }
}

function Can-Reach([string]$hostPort) {
    $h, $p = $hostPort -split ':'
    $c = New-Object Net.Sockets.TcpClient
    try { return $c.ConnectAsync($h, [int]$p).Wait(5000) -and $c.Connected } catch { return $false } finally { $c.Dispose() }
}

$url = ([string]$cfg.Url).TrimEnd('/')
$siteHost = ([Uri]$url).Host

if ($TestEmail) {
    try {
        Send "The website check is set up" ("From now on $env:COMPUTERNAME checks $url every 2 minutes and emails you here if it stops answering, and again when it is back.`r`n`r`n" +
            "Nothing else needs doing. While this PC is switched off, or the office internet is down, it cannot check.")
        Log "test email sent to $($cfg.To)"; exit 0
    } catch {
        $m = $_.Exception.Message; if ($_.Exception.InnerException) { $m += " - " + $_.Exception.InnerException.Message }
        Write-Host "   $m" -ForegroundColor Red; Log "test email FAILED: $m"; exit 1
    }
}

# ---- 1. Is the office online at all? Two well-known addresses; either will do.
$probes = @(Get-Or $cfg "InternetProbes" @("1.1.1.1:443", "8.8.8.8:443"))
if (-not ($probes | Where-Object { Can-Reach $_ } | Select-Object -First 1)) {
    Log "office offline (none of $($probes -join ', ') answers) - cannot tell whether the site is up"
    Save; exit 0
}

# ---- 2. Does the site answer? /health says {"ok":true} when the platform is running.
$ok = $false; $reason = ""
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    $r = Invoke-WebRequest -Uri "$url/health" -UseBasicParsing -TimeoutSec 20 -MaximumRedirection 0
    if ($r.StatusCode -eq 200 -and $r.Content -match '"ok"\s*:\s*true') { $ok = $true }
    else { $reason = "it answered $($r.StatusCode): $(([string]$r.Content).Substring(0, [Math]::Min(120, ([string]$r.Content).Length)))" }
} catch {
    $reason = $_.Exception.Message
    if ($_.Exception.Response) { $reason = "it answered $([int]$_.Exception.Response.StatusCode) - $reason" }
}
$ms = $clock.ElapsedMilliseconds

if ($ok) {
    if ($state.DownSent) {
        $since = [datetime]::Parse($state.FirstFailure)
        $mins = [int][Math]::Ceiling(($now - $since).TotalMinutes)
        try {
            Send "$siteHost is back up (down about $mins minutes)" ("$url answered again at $($now.ToString('HH:mm')) on $($now.ToString('dddd d MMMM')).`r`n`r`n" +
                "It stopped answering at about $($since.ToString('HH:mm')). The last problem seen was:`r`n  $($state.LastReason)")
            Log "UP again after about $mins minutes - told $($cfg.To)"
        } catch { Log "UP again, but the email saying so failed: $($_.Exception.Message)" }
    } elseif ($state.Fails -gt 0) {
        Log "up ($ms ms) - one missed check before this, no email (a restart, most likely)"
    } else {
        Log "up ($ms ms)"
    }
    $state.Fails = 0; $state.FirstFailure = ""; $state.DownSent = $false; $state.LastReason = ""
} else {
    $state.Fails++
    if (-not $state.FirstFailure) { $state.FirstFailure = $now.ToString("o") }
    $state.LastReason = $reason
    Log "NOT answering ($($state.Fails) in a row): $reason"
    if ($state.Fails -ge 2 -and -not $state.DownSent) {
        try {
            $since = [datetime]::Parse($state.FirstFailure)
            Send "$siteHost is DOWN" ("$url/health has not answered since about $($since.ToString('HH:mm')) on $($now.ToString('dddd d MMMM')) - $($state.Fails) checks in a row, two minutes apart.`r`n`r`n" +
                "What the check saw:`r`n  $reason`r`n`r`n" +
                "Customers cannot connect while it is down. First look:`r`n" +
                "  ssh ubuntu@154.65.110.136 `"sudo journalctl -u remotesupport-platform -n 100 --no-pager`"`r`n" +
                "  ssh ubuntu@154.65.110.136 `"sudo systemctl restart remotesupport-platform`"`r`n" +
                "If ssh does not answer either, the server itself is down: xneelo's cloud panel, Compute > Instances.`r`n`r`n" +
                "You will get ONE more email, when it answers again.")
            $state.DownSent = $true
            Log "told $($cfg.To) it is DOWN"
        } catch { Log "the DOWN email failed (tried again in 2 minutes): $($_.Exception.Message)" }
    }
}

# ---- 3. The certificate, once a day: under 14 days left means Caddy's renewal is failing.
$warnDays = [int](Get-Or $cfg "CertWarnDays" 14)
if ($ok -and $state.CertWarnedOn -ne $today) {
    try {
        $tcp = New-Object Net.Sockets.TcpClient($siteHost, 443)
        $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false, { $true })
        try { $ssl.AuthenticateAsClient($siteHost); $notAfter = ([Security.Cryptography.X509Certificates.X509Certificate2]$ssl.RemoteCertificate).NotAfter }
        finally { $ssl.Dispose(); $tcp.Dispose() }
        $left = [int][Math]::Floor(($notAfter - $now).TotalDays)
        if ($left -lt $warnDays) {
            Send "$siteHost - its certificate runs out in $left days" ("The HTTPS certificate of $siteHost runs out on $($notAfter.ToString('d MMMM yyyy')) - $left days from now.`r`n`r`n" +
                "Caddy renews it by itself 30 days before, so it has been failing for over two weeks. Look at:`r`n" +
                "  ssh ubuntu@154.65.110.136 `"sudo journalctl -u caddy -n 100 --no-pager`"`r`n" +
                "Almost always DNS: the name must point at the server, and port 80 must be open.")
            $state.CertWarnedOn = $today
            Log "certificate: $left days left - told $($cfg.To)"
        }
    } catch { Log "certificate: could not be read or the email failed (tried again in 2 minutes): $($_.Exception.Message)" }
}

# ---- 4. The nightly backup copy (deploy/backup), once a day: nothing for 36 hours means it stopped.
$okFile = [string](Get-Or $cfg "BackupOkFile" "")
if ($okFile -and $state.BackupWarnedOn -ne $today) {
    if (Test-Path $okFile) {
        $age = $now - (Get-Item $okFile).LastWriteTime
        if ($age.TotalHours -gt 36) {
            try {
                Send "$siteHost - no backup copied for $([int]$age.TotalHours) hours" ("The nightly copy of the server's backup to the office share has not succeeded since $((Get-Item $okFile).LastWriteTime.ToString('dddd d MMMM HH:mm')).`r`n`r`n" +
                    "Its own log says why: $(Join-Path (Split-Path $okFile -Parent) 'pull-log.txt')`r`n" +
                    "deploy\backup\README.md has the set-up.")
                $state.BackupWarnedOn = $today
                Log "backup copy $([int]$age.TotalHours) h old - told $($cfg.To)"
            } catch { Log "the backup email failed: $($_.Exception.Message)" }
        }
    } else { Log "cannot see $okFile (the share is not reachable from here?)" }
}

Save
exit 0
