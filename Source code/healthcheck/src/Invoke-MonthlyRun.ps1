<#
.SYNOPSIS
    The full monthly run: maintenance, health check, report, email.

.DESCRIPTION
    This is what the scheduled task calls once a month. It runs everything in
    order and sends one email when it is done.

        1. Maintenance   - clean, optimise, check Windows system files
        2. Health check  - read-only inspection of the machine
        3. Report        - branded HTML, converted to PDF
        4. Email         - PDF and data file sent to support

    The PDF covers this machine only. To produce a client's combined report
    across all their machines, collect the attached data files and run
    New-ClientReport.ps1 against them.

    Everything is written to disk before anything is sent, so a mail failure
    never loses a month's work.

.NOTES
    Requires: PowerShell 5.1, administrator rights (runs as SYSTEM from the task)
    Version:  1.0
#>

[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$WorkPath = "$env:ProgramData\HealthCheck",

    # Skip parts of the run. Useful when testing.
    [switch]$SkipMaintenance,
    [switch]$SkipReport,
    [switch]$SkipEmail,

    # Cleaning only - no optimisation, no system file check. A [bool] rather
    # than a [switch] so it survives being passed through powershell.exe -File,
    # which hands switches over as literal strings.
    [bool]$Quick = $false,

    # How many months of history to keep on the machine.
    [int]$KeepMonths = 12
)


# Version of this toolkit. Printed at run time so a stale copy cannot hide.
$ToolkitVersion = '2.11'

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ConfigPath) { $ConfigPath = Join-Path $here 'config.json' }

$Config = $null
if (Test-Path $ConfigPath) {
    try { $Config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}

$Organisation = if ($Config.Organisation) { $Config.Organisation } else { $env:COMPUTERNAME }
$UserOrRole   = if ($Config.UserOrRole)   { $Config.UserOrRole }   else { $env:COMPUTERNAME }
$stamp        = Get-Date -Format 'yyyy-MM'
$started      = Get-Date

if (-not (Test-Path $WorkPath)) { New-Item -Path $WorkPath -ItemType Directory -Force | Out-Null }

$logPath = Join-Path $WorkPath "run_$stamp.log"
function Write-Log {
    param([string]$Text, [string]$Colour = 'Gray')
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $Text
    Write-Host "  $Text" -ForegroundColor $Colour
    Add-Content -Path $logPath -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Log "Toolkit v$ToolkitVersion" 'DarkGray'

# ---------------------------------------------------------------------------
# The Remote Support Program
#
# When this machine is managed by the platform it holds no mail settings at all.
# It does the work, builds its own report, and uploads both; the server sends the
# mail using settings held once per tenant. See docs/PLAN-HEALTHCHECK.md.
#
# Everything here is best-effort. A machine that cannot reach its server must
# still do its maintenance and keep its results, exactly as a standalone install
# does - the upload is retried next month, not treated as a failed run.
# ---------------------------------------------------------------------------

$PlatformUrl = ''
if ($Config -and $Config.Platform -and $Config.Platform.BaseUrl) { $PlatformUrl = [string]$Config.Platform.BaseUrl }
# Initialised here so the closing summary cannot claim a send that never happened - including when
# the upload block is skipped entirely because this machine has no device identity.
$uploadOk = $false

function Get-DeviceIdentity {
    # The same device.json the tray app and the unattended agent use, so the upload
    # authenticates as the machine the technician already sees in the console. Read at run
    # time rather than copied in at install time: a reinstall issues a new id and key, and a
    # stale copy would start failing silently a month later.
    $p = Join-Path $env:ProgramData 'RemoteSupport\device.json'
    if (-not (Test-Path $p)) { return $null }
    try {
        $d = Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($d.DeviceId -and $d.DeviceKey) { return $d }
    } catch { }
    return $null
}

function Invoke-PlatformUpload {
    # PowerShell 5.1 has no multipart support in Invoke-RestMethod - -Form is PowerShell 7 -
    # so the request is built with HttpClient. Returns $true only on a 2xx.
    param([string]$BaseUrl, $Identity, [string]$JsonPath, [string]$PdfPath, [string]$MaintenancePath)

    Add-Type -AssemblyName System.Net.Http -ErrorAction Stop
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

    $client = New-Object System.Net.Http.HttpClient
    $client.Timeout = [TimeSpan]::FromMinutes(5)
    $form = New-Object System.Net.Http.MultipartFormDataContent
    try {
        $form.Add((New-Object System.Net.Http.StringContent($Identity.DeviceId)),  'deviceId')
        $form.Add((New-Object System.Net.Http.StringContent($Identity.DeviceKey)), 'deviceKey')

        $jsonBytes = [System.IO.File]::ReadAllBytes($JsonPath)
        $jsonPart  = New-Object System.Net.Http.ByteArrayContent(,$jsonBytes)
        $form.Add($jsonPart, 'data', [System.IO.Path]::GetFileName($JsonPath))

        if ($PdfPath -and (Test-Path $PdfPath)) {
            $pdfBytes = [System.IO.File]::ReadAllBytes($PdfPath)
            $pdfPart  = New-Object System.Net.Http.ByteArrayContent(,$pdfBytes)
            $form.Add($pdfPart, 'report', [System.IO.Path]::GetFileName($PdfPath))
        }

        # The maintenance record is a separate file from the health check, and without it the server
        # has no way to know maintenance ran at all - it recorded a real maintenance run on
        # DESKTOP-OPHQIDH as "inspection only, nothing changed", which understates the work.
        if ($MaintenancePath -and (Test-Path $MaintenancePath)) {
            $mBytes = [System.IO.File]::ReadAllBytes($MaintenancePath)
            $mPart  = New-Object System.Net.Http.ByteArrayContent(,$mBytes)
            $form.Add($mPart, 'maintenance', [System.IO.Path]::GetFileName($MaintenancePath))
        }

        $resp = $client.PostAsync(($BaseUrl.TrimEnd('/') + '/api/healthcheck/report'), $form).GetAwaiter().GetResult()
        $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($resp.IsSuccessStatusCode) {
            Write-Log "Uploaded to the support server. $body" 'Green'
            return $true
        }
        Write-Log "Upload refused: $([int]$resp.StatusCode) $body" 'Yellow'
        return $false
    }
    catch {
        Write-Log "Could not reach the support server: $($_.Exception.Message)" 'Yellow'
        return $false
    }
    finally {
        if ($form)   { $form.Dispose() }
        if ($client) { $client.Dispose() }
    }
}

# Ask the server which customer this machine belongs to. The installer could not know: a machine
# is linked to a ticketing client in the portal afterwards, so the install put the machine name in
# as a placeholder. Falling back to that placeholder is correct when the server cannot be reached
# or the machine is not linked yet - an unlabelled report is better than a late one.
$DeviceIdentity = $null
if ($PlatformUrl) {
    $DeviceIdentity = Get-DeviceIdentity
    if (-not $DeviceIdentity) {
        Write-Log 'No device identity on this machine - the report cannot be uploaded.' 'Yellow'
    }
    else {
        try {
            try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
            $ctxUrl = '{0}/api/healthcheck/context?deviceId={1}&deviceKey={2}' -f `
                      $PlatformUrl.TrimEnd('/'),
                      [uri]::EscapeDataString($DeviceIdentity.DeviceId),
                      [uri]::EscapeDataString($DeviceIdentity.DeviceKey)
            $ctx = Invoke-RestMethod -Uri $ctxUrl -TimeoutSec 20 -ErrorAction Stop

            # Is this machine still meant to have it? The server's answer for THIS machine - its
            # customer's tick and its own override (2026-09-28). The server has always sent a
            # company-wide `enabled` and this script never read it, so switching the health check
            # off in the portal stopped nothing.
            #
            # Only an explicit false from an answer that authenticated counts. A server that cannot
            # be reached lands in the catch below and the run carries on: an unreachable server must
            # never be the thing that removes a machine's maintenance.
            if ($ctx.PSObject.Properties['wanted'] -and $ctx.wanted -eq $false) {
                Write-Log 'The support server says this machine no longer has the monthly health check. Removing its schedule.' 'Yellow'
                $uninstaller = Join-Path $here 'Uninstall-HealthCheck.ps1'
                if (Test-Path $uninstaller) {
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $uninstaller -InstallPath $WorkPath 2>&1 |
                        ForEach-Object { Write-Log "$_" 'DarkGray' }
                }
                else {
                    Write-Log 'Uninstall-HealthCheck.ps1 is not here; the schedule is left for the support app to remove.' 'Yellow'
                }
                exit 0
            }

            # "Inspect only" on the portal's Health check page. Sent every month and, until now,
            # never obeyed: maintenance ran whatever the IT company had chosen.
            if ($ctx.PSObject.Properties['runMaintenance'] -and $ctx.runMaintenance -eq $false -and -not $SkipMaintenance) {
                $SkipMaintenance = $true
                Write-Log 'Inspection only, as chosen by the support company - no maintenance this month.' 'DarkGray'
            }

            if ($ctx.clientName) {
                $Organisation = [string]$ctx.clientName
                Write-Log "This machine is filed under $Organisation." 'DarkGray'
            }
            else {
                Write-Log "Not linked to a customer yet - reporting as $Organisation." 'DarkGray'
            }
        }
        catch {
            Write-Log "Could not ask the support server who this machine belongs to: $($_.Exception.Message)" 'Yellow'
            Write-Log "Carrying on as $Organisation." 'DarkGray'
        }
    }
}

Write-Log "Monthly run starting for $UserOrRole ($env:COMPUTERNAME), $Organisation" 'Cyan'
Write-Host ""

# ---------------------------------------------------------------------------
# 1. Maintenance
# ---------------------------------------------------------------------------

if (-not $SkipMaintenance) {
    $maintScript = Join-Path $here 'Invoke-ClientMaintenance.ps1'
    if (Test-Path $maintScript) {
        Write-Log 'Running maintenance...' 'Cyan'
        try {
            $maintArgs = @{
                Organisation = $Organisation
                UserOrRole   = $UserOrRole
                OutputPath   = $WorkPath
                ConfigPath   = $ConfigPath
            }
            if ($Quick) { $maintArgs['Quick'] = $true }
            # Same reason as the report step below: Out-Null threw away the only account of what
            # maintenance actually did. "Creating a restore point before anything is changed", and
            # the reason when it cannot, are written with Write-Host - so on an unattended run the
            # log said only "Running maintenance..." then "Maintenance finished", and whether a
            # restore point existed was unknowable. Clint asked exactly that question.
            & $maintScript @maintArgs *>&1 | ForEach-Object {
                $line = ($_ | Out-String).TrimEnd()
                if ($line) { foreach ($l in ($line -split "`r?`n")) { if ($l.Trim()) { Write-Log ("  maintenance: " + $l.Trim()) 'DarkGray' } } }
            }
            Write-Log 'Maintenance finished.' 'Green'
        }
        catch { Write-Log "Maintenance failed: $($_.Exception.Message)" 'Yellow' }
    }
    else { Write-Log 'Maintenance script not found - skipped.' 'Yellow' }
}

# ---------------------------------------------------------------------------
# 2. Health check
#    Runs after maintenance so the figures reflect the cleaned machine.
# ---------------------------------------------------------------------------

$checkScript = Join-Path $here 'Get-ClientHealthCheck.ps1'
if (-not (Test-Path $checkScript)) {
    Write-Log 'Health check script is missing. Nothing more can be done.' 'Red'
    exit 1
}

# Maintenance runs sfc /scannow, which verifies AND repairs. Letting the health
# check then run sfc /verifyonly repeats ten minutes of identical work for a
# result already reported under "What we did this month", and on a slow machine
# the two together can hold the monthly run open for over an hour.
#
# The verification is still wanted where maintenance did not do its own: when
# maintenance was skipped entirely, or in Quick mode, which sets
# SkipSystemCheck. On a Windows Server - where maintenance always skips, because
# there is no System Restore - this is the only system file check that happens.
#
# Known gap: if maintenance starts but aborts because no restore point could be
# made, no system file check runs at all that month. The report says plainly
# that maintenance was skipped and why, so nobody is misled into thinking it did.
$verifySystemFiles = ($SkipMaintenance -or $Quick)
Write-Log "System file check in the health check: $verifySystemFiles" 'DarkGray'

Write-Log 'Running health check...' 'Cyan'
try {
    & $checkScript -Organisation $Organisation -UserOrRole $UserOrRole `
                   -OutputPath $WorkPath -ConfigPath $ConfigPath -NoEmail `
                   -VerifySystemFiles $verifySystemFiles | Out-Null
    Write-Log 'Health check finished.' 'Green'
}
catch {
    Write-Log "Health check failed: $($_.Exception.Message)" 'Red'
    exit 1
}

$dataFile = Get-ChildItem -Path $WorkPath -Filter "*_$($env:COMPUTERNAME)_$stamp.json" -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike 'maintenance_*' } |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $dataFile) {
    Write-Log 'No results file was produced. Stopping.' 'Red'
    exit 1
}

$maintFile = Get-ChildItem -Path $WorkPath -Filter "maintenance_*_$($env:COMPUTERNAME)_$stamp.json" -File -ErrorAction SilentlyContinue |
             Sort-Object LastWriteTime -Descending | Select-Object -First 1

# ---------------------------------------------------------------------------
# 3. Report, then PDF
# ---------------------------------------------------------------------------

$htmlPath = $null
$pdfPath  = $null

if (-not $SkipReport) {
    $reportScript = Join-Path $here 'New-ClientReport.ps1'
    if (Test-Path $reportScript) {
        Write-Log 'Building the report...' 'Cyan'
        $reportStarted = Get-Date
        try {
            $reportDir = Join-Path $WorkPath 'Reports'
            # Switches first - a value ending in a dot ("Anywhere I.T") followed
            # by a switch confuses the parameter binder.
            # Pdf defaults to on, so it is not passed. It is a [bool] rather
            # than a [switch], because a switch cannot survive the trip through
            # cmd when the batch file calls this.
            # Branding comes from the config the installer wrote, so a reseller's client sees
            # their own IT company on the report. Splatted rather than passed positionally,
            # because several values are legitimately empty - an IT company with no tagline -
            # and an empty string is exactly what gets lost crossing a process boundary. This
            # call is in-process, so empty values survive; keep it that way.
            $reportArgs = @{
                InputPath  = $WorkPath
                OutputPath = $reportDir
                Month      = $stamp
                LogoPath   = (Join-Path $here 'logo.png')
            }
            # Name the organisation explicitly. Without it the report builder groups whatever JSON
            # it finds in the work folder and builds one report per organisation, and the code
            # below then picks whichever file was written last - so a machine holding data under
            # two names could email the wrong report. That is not hypothetical: a machine that had
            # the standalone toolkit on it before keeps its old results in this same folder, filed
            # under whatever organisation it used then. The month filter hides it today; naming the
            # organisation means it cannot bite at all.
            # $Organisation, NOT $Config.Organisation. The platform replaces the installer's
            # placeholder with the real customer name at the top of this run, and the collector
            # has already written that name into the JSON. Naming the placeholder here matched
            # nothing and silently produced no report at all.
            if ($Organisation) { $reportArgs['Organisation'] = [string]$Organisation }
            # The server converts, so a missing PDF here is expected rather than a warning.
            if ($PlatformUrl) { $reportArgs['PdfOptional'] = $true }
            $brand = $Config.Branding
            if ($brand) {
                # Only override what is actually present. A config written by an older installer
                # has no Branding section at all, and must keep working unchanged.
                # Passed through even when blank. A Branding section exists only because the
                # installer wrote one, so this is a branded install and its values are
                # authoritative - including an empty one. Skipping a blank name used to let the
                # report builder's own default through, which was another IT company's name.
                if ($null -ne $brand.CompanyName)  { $reportArgs['CompanyName']  = [string]$brand.CompanyName }
                if ($null -ne $brand.CompanyTag)   { $reportArgs['CompanyTag']   = [string]$brand.CompanyTag }
                if ($null -ne $brand.CompanyEmail) { $reportArgs['CompanyEmail'] = [string]$brand.CompanyEmail }
                if ($null -ne $brand.CompanyPhone) { $reportArgs['CompanyPhone'] = [string]$brand.CompanyPhone }
                if ($null -ne $brand.AccentColor  -and $brand.AccentColor)  { $reportArgs['AccentColor']  = [string]$brand.AccentColor }
                if ($null -ne $brand.PrimaryColor -and $brand.PrimaryColor) { $reportArgs['PrimaryColor'] = [string]$brand.PrimaryColor }
                # The tenant's logo, or none at all. There is deliberately no fallback: on a
                # branded install the only logo that may reach the customer's report is their own
                # IT company's, and an unbranded report is a far smaller problem than somebody
                # else's mark on it. Without a LogoPath the report builder prints the company
                # name as text instead.
                $reportArgs.Remove('LogoPath')
                if ($brand.LogoFile) {
                    $brandLogo = Join-Path $here ([string]$brand.LogoFile)
                    if (Test-Path $brandLogo) { $reportArgs['LogoPath'] = $brandLogo }
                    else { Write-Log "Branded logo $($brand.LogoFile) is missing - the report will show the company name as text." 'Yellow' }
                }
            }
            # Out-Null threw away the one thing worth having when the PDF step fails. The report
            # builder prints exactly why Edge did not produce a file - it would not start, it timed
            # out, it exited non-zero, or it exited 0 having written nothing - and none of that
            # reached the log, so "PDF could not be created" was all anyone ever saw. On an
            # unattended run as SYSTEM on a machine nobody is sitting at, that is the only evidence
            # there will ever be. Kept at DarkGray so a healthy run stays quiet to read.
            # *>&1 and not 2>&1: the report builder says everything with Write-Host, which in
            # PowerShell 5.1 goes to the information stream, and 2>&1 captures none of it. Measured -
            # with 2>&1 the log recorded nothing at all from the report step.
            & $reportScript @reportArgs *>&1 | ForEach-Object {
                $line = ($_ | Out-String).TrimEnd()
                if ($line) { foreach ($l in ($line -split "`r?`n")) { if ($l.Trim()) { Write-Log ("  report: " + $l.Trim()) 'DarkGray' } } }
            }

            # Only files THIS run wrote. Taking the newest report in the folder meant that when the
            # PDF step failed, the previous run's PDF was picked up instead and sent as if it were
            # this month's - a customer receiving a stale report, with the log cheerfully saying
            # "PDF created". Seen exactly that way: the report said it could not make a PDF and the
            # run reported one, because an older file was still sitting there.
            #
            # A second's grace because the file can land a moment before this comparison is made.
            $runFrom  = $reportStarted.AddSeconds(-1)
            $htmlPath = (Get-ChildItem -Path $reportDir -Filter '*.html' -File -ErrorAction SilentlyContinue |
                         Where-Object { $_.LastWriteTime -ge $runFrom } |
                         Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
            $pdfPath  = (Get-ChildItem -Path $reportDir -Filter '*.pdf' -File -ErrorAction SilentlyContinue |
                         Where-Object { $_.LastWriteTime -ge $runFrom } |
                         Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName

            if ($htmlPath) {
                Write-Log "Report built." 'Green'

                if ($pdfPath) { Write-Log 'PDF created.' 'Green' }
                # On a platform-managed machine this is expected, not a fault: the monthly run is a
                # scheduled task running as SYSTEM, and Edge refuses to start under that account.
                # The web page version is uploaded and the server turns it into the PDF. Saying
                # "PDF could not be created" in yellow every month described a broken machine that
                # was working perfectly.
                elseif ($PlatformUrl) { Write-Log 'No PDF here - the support server builds it from the web page version.' 'DarkGray' }
                else                  { Write-Log 'PDF could not be created - the web page version will be sent instead.' 'Yellow' }
            }
        }
        catch { Write-Log "Report failed: $($_.Exception.Message)" 'Yellow' }
    }
    else { Write-Log 'Report script not found - skipped.' 'Yellow' }
}

# ---------------------------------------------------------------------------
# 3b. Upload to the support server
#
# Retries are driven by a .pending marker written next to a run that failed to
# upload, NOT by scanning the folder for JSON. A machine that had the standalone
# toolkit on it before still has that toolkit's results here, filed under a
# different organisation, and sweeping the folder would post somebody else's data
# to this tenant. Only runs this script produced are ever marked, so only those
# are ever retried. DESKTOP-OPHQIDH is exactly such a machine.
# ---------------------------------------------------------------------------

if ($PlatformUrl -and $DeviceIdentity) {

    # Oldest first, so a machine that was offline for three months sends March before May and
    # the portal history reads in the order things actually happened.
    $pending = @(Get-ChildItem -Path $WorkPath -Filter '*.pending' -File -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime)
    foreach ($marker in $pending) {
        $info = $null
        try { $info = Get-Content $marker.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
        if (-not $info -or -not $info.Json -or -not (Test-Path $info.Json)) {
            # The data it refers to is gone, so the marker can never succeed. Drop it rather than
            # retrying a dead entry every month for a year.
            Remove-Item $marker.FullName -Force -ErrorAction SilentlyContinue
            continue
        }
        Write-Log "Retrying an earlier upload from $($marker.LastWriteTime.ToString('yyyy-MM-dd'))..." 'Cyan'
        $pdfForRetry = if ($info.Pdf -and (Test-Path $info.Pdf)) { [string]$info.Pdf } else { '' }
        # Markers written before the maintenance file was uploaded have no Maintenance property at
        # all, so this must tolerate its absence rather than assume every marker has one.
        $maintForRetry = if ($info.PSObject.Properties['Maintenance'] -and $info.Maintenance -and (Test-Path $info.Maintenance)) { [string]$info.Maintenance } else { '' }
        if (Invoke-PlatformUpload -BaseUrl $PlatformUrl -Identity $DeviceIdentity -JsonPath ([string]$info.Json) -PdfPath $pdfForRetry -MaintenancePath $maintForRetry) {
            Remove-Item $marker.FullName -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Log 'Sending this month''s results to the support server...' 'Cyan'
    $uploadPdf = if ($pdfPath) { $pdfPath } elseif ($htmlPath) { $htmlPath } else { '' }
    $uploadMaint = if ($maintFile) { $maintFile.FullName } else { '' }
    $uploadOk = Invoke-PlatformUpload -BaseUrl $PlatformUrl -Identity $DeviceIdentity -JsonPath $dataFile.FullName -PdfPath $uploadPdf -MaintenancePath $uploadMaint
    if (-not $uploadOk) {
        # Nothing is lost: the results are on this machine and the marker brings them back next
        # month. Same rule the standalone toolkit follows for a failed send.
        $marker = Join-Path $WorkPath ($dataFile.BaseName + '.pending')
        $payload = [ordered]@{ Json = $dataFile.FullName; Pdf = $uploadPdf; Maintenance = $uploadMaint; Failed = (Get-Date).ToString('s') }
        # WriteAllText, not Set-Content -Encoding UTF8, which writes a byte order mark that
        # ConvertFrom-Json then refuses on the way back in.
        [System.IO.File]::WriteAllText($marker, ($payload | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
        Write-Log 'Kept here and marked to retry next month.' 'Yellow'
    }
}

# ---------------------------------------------------------------------------
# 4. Email
#
# Only when this machine holds its own mail settings, which a platform-managed
# install never does. There the server sends, using a password no customer
# machine ever stores.
# ---------------------------------------------------------------------------

function Unprotect-StoredSecret {
    param([string]$Base64)
    if (-not $Base64) { return $null }
    try {
        Add-Type -AssemblyName System.Security -ErrorAction Stop
        $bytes = [Convert]::FromBase64String($Base64)
        return [System.Text.Encoding]::UTF8.GetString(
            [System.Security.Cryptography.ProtectedData]::Unprotect(
                $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine))
    }
    catch { return $null }
}

$skipSend = $false
if (-not $SkipEmail -and $Config.Email -and $Config.Email.To -and $Config.Email.SmtpServer) {
    Write-Log 'Sending...' 'Cyan'
    try {
        $data = Get-Content $dataFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $crit = @($data.Findings | Where-Object { $_.Severity -eq 'Critical' })
        $warn = @($data.Findings | Where-Object { $_.Severity -eq 'Warning'  })

        # Megabytes under a gigabyte. The report was fixed for this and the email
        # was not: a 130 MB clean-up read "We cleared 0.13 GB", a number the
        # reader has to decode and one that looks like nothing was done.
        $freedMB = 0
        if ($maintFile) {
            try {
                $m = Get-Content $maintFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($m.TotalFreedMB) { $freedMB = [double]$m.TotalFreedMB }
            } catch { }
        }
        $freedText = "{0:N0} MB" -f $freedMB
        if ($freedMB -ge 1024) { $freedText = "{0:N1} GB" -f ($freedMB / 1024) }

        # A client gets the report only. Raw data files would be meaningless to
        # them. Internal recipients get everything, because the combined client
        # report is built from those files.
        $goesToClient = ($Config.RecipientType -eq 'Client')

        $lines = @()

        if ($goesToClient) {
            # Written for the person who owns the machine, not for a technician
            $monthName = (Get-Date).ToString('MMMM yyyy')
            $subject = 'Your monthly computer health report - {0} - {1}' -f $UserOrRole, $monthName

            $lines += "Good morning,"
            $lines += ""
            $lines += "Attached is this month's health report for $UserOrRole."
            $lines += ""
            if ($freedMB -ge 1) {
                $lines += "We cleared $freedText of unnecessary files and ran the usual maintenance."
                $lines += ""
            }
            if ($crit.Count -gt 0) {
                # The verb has to agree as well as the noun. This used to read
                # "2 things needs attention" - the first real sentence a client
                # reads in the email.
                $lines += "$($crit.Count) $(if ($crit.Count -eq 1) { 'thing needs' } else { 'things need' }) attention:"
                foreach ($c in $crit) { $lines += "  - $($c.Title)" }
                $lines += ""
            }
            if ($warn.Count -gt 0) {
                $lines += "$($warn.Count) thing$(if($warn.Count -ne 1){'s'}) worth planning for:"
                foreach ($w in $warn) { $lines += "  - $($w.Title)" }
                $lines += ""
            }
            if ($crit.Count -eq 0 -and $warn.Count -eq 0) {
                $lines += "Everything looks healthy this month. Nothing needs your attention."
                $lines += ""
            }
            $lines += "The attached report explains each item in plain English."
            $lines += "If you would like us to deal with any of it, or if anything is"
            $lines += "unclear, just reply to this email or give us a call."
        }
        else {
            $subject = '[{0}] {1} ({2}) - {3} urgent, {4} to plan' -f `
                       $Organisation, $UserOrRole, $env:COMPUTERNAME, $crit.Count, $warn.Count

            $lines += "Organisation : $Organisation"
            $lines += "User or role : $UserOrRole"
            $lines += "Machine      : $env:COMPUTERNAME"
            $lines += "Run finished : $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
            $tookMins = [math]::Round(((Get-Date) - $started).TotalMinutes)
            $lines += "Took         : $tookMins $(if ($tookMins -eq 1) { 'minute' } else { 'minutes' })"
            $lines += ""
            if ($freedMB -ge 1) { $lines += "Space recovered: $freedText"; $lines += "" }
            if ($crit.Count -gt 0) {
                $lines += "NEEDS ATTENTION"
                foreach ($c in $crit) { $lines += "  - $($c.Title)" }
                $lines += ""
            }
            if ($warn.Count -gt 0) {
                $lines += "TO PLAN"
                foreach ($w in $warn) { $lines += "  - $($w.Title)" }
                $lines += ""
            }
            if ($crit.Count -eq 0 -and $warn.Count -eq 0) { $lines += "Nothing needs attention on this machine."; $lines += "" }
            $lines += "Attached: this machine's report, and its data files."
            $lines += "Keep the data files - the client's combined report is built from all of them together."
        }

        $attach = @()
        if ($pdfPath)      { $attach += $pdfPath }
        elseif ($htmlPath) { $attach += $htmlPath }

        if (-not $goesToClient) {
            $attach += $dataFile.FullName
            if ($maintFile) { $attach += $maintFile.FullName }
        }

        # Sending a client an email that promises an attached report, with no
        # report attached, is worse than sending nothing. Better they hear from
        # us late than receive something broken.
        if ($goesToClient -and -not $pdfPath -and -not $htmlPath) {
            Write-Log 'No report was produced, so nothing was sent to the client.' 'Yellow'
            Write-Log 'Fix the report problem above, then run this again.' 'Yellow'
            $skipSend = $true
        }

        $e = $Config.Email
        $mailArgs = @{
            To          = @($e.To -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            From        = if ($e.From) { $e.From } else { $e.User }
            Subject     = $subject
            Body        = ($lines -join "`r`n")
            SmtpServer  = $e.SmtpServer
            Port        = if ($e.Port) { [int]$e.Port } else { 587 }
            Attachments = $attach
            ErrorAction = 'Stop'
        }
        if ($e.UseSsl -ne $false) { $mailArgs['UseSsl'] = $true }
        if ($e.User) {
            $pw = Unprotect-StoredSecret -Base64 $e.PasswordProtected
            if ($pw) {
                $mailArgs['Credential'] = New-Object System.Management.Automation.PSCredential(
                    $e.User, (ConvertTo-SecureString $pw -AsPlainText -Force))
            }
            else {
                # Without this, the send goes ahead with no credentials and the
                # server replies "authentication required" - which looks like a
                # wrong password rather than a password that never arrived.
                throw "The stored email password could not be read on this machine. Reinstall to store it again."
            }
        }

        # A record of exactly what this email said, kept beside the run log.
        # Written BEFORE the send so it exists whether or not the send works.
        #
        # Two uses. When a client asks "what did you send me?" the answer is on
        # the machine. And it is the only way to check the wording of the client
        # and support emails without actually sending one: there was previously
        # no way to see the composed message short of receiving it.
        #
        # Never contains the password - only the fields a recipient would see.
        try {
            $record = @(
                "Composed : $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
                "Type     : $(if ($goesToClient) { 'Client' } else { 'Support' })"
                "To       : $(($mailArgs.To) -join ', ')"
                "From     : $($mailArgs.From)"
                "Subject  : $subject"
                "Attached : $(if ($attach.Count) { ($attach | ForEach-Object { Split-Path -Leaf $_ }) -join ', ' } else { 'nothing' })"
                "Sent     : $(if ($skipSend) { 'NO - held back, see log' } else { 'attempted - see log for the result' })"
                ""
                ($lines -join "`r`n")
            ) -join "`r`n"
            [System.IO.File]::WriteAllText((Join-Path $WorkPath 'last-email.txt'), $record,
                                           (New-Object System.Text.UTF8Encoding($false)))
        } catch { }

        if (-not $skipSend) {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
        Send-MailMessage @mailArgs

        Write-Log "Sent to $($e.To)" 'Green'
        }
    }
    catch {
        Write-Log "Could not send: $($_.Exception.Message)" 'Yellow'
        Write-Log 'Everything is still saved on this machine.' 'Yellow'
    }
}
elseif (-not $SkipEmail) {
    # A platform-managed machine holding no mail settings is the design, not a mistake, and the
    # report has already gone to the server above. Saying "no email configured" here is what sent
    # Clint looking for a fault on a correctly installed machine.
    if ($PlatformUrl -and $uploadOk) { Write-Log 'Sent to the support server, which emails the report.' 'DarkGray' }
    elseif ($PlatformUrl)            { Write-Log 'Not sent yet - the results are here and will go up next month.' 'Yellow' }
    else                             { Write-Log 'No email configured - results are on this machine only.' 'Yellow' }
}

# ---------------------------------------------------------------------------
# 5. Housekeeping
#
# Keep a year of history on the machine and no more. Old files are not merged
# into reports, but leaving them to build up forever on a client machine is
# untidy and slows the report step down.
# ---------------------------------------------------------------------------

try {
    $keepAfter = (Get-Date).AddMonths(-$KeepMonths)
    $old = Get-ChildItem -Path $WorkPath -Filter '*.json' -File -ErrorAction SilentlyContinue |
           Where-Object { $_.LastWriteTime -lt $keepAfter }
    if ($old) {
        $old | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Log "Tidied up $($old.Count) file(s) older than $KeepMonths months." 'DarkGray'
    }

    $oldReports = Get-ChildItem -Path (Join-Path $WorkPath 'Reports') -File -ErrorAction SilentlyContinue |
                  Where-Object { $_.LastWriteTime -lt $keepAfter }
    if ($oldReports) { $oldReports | Remove-Item -Force -ErrorAction SilentlyContinue }
}
catch { }

Write-Host ""
Write-Log ("Finished in {0} minutes." -f [math]::Round(((Get-Date) - $started).TotalMinutes)) 'Green'
Write-Host ""
