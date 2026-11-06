<#
.SYNOPSIS
    Turns collected health check JSON into a branded, client-facing HTML report.

.DESCRIPTION
    Reads every JSON file produced by Get-ClientHealthCheck.ps1, groups them by
    client, and writes one self-contained HTML report per client.

    The report is written for the END CLIENT - a business owner, not a technician.
    No jargon, no raw data, no severity codes.

    Self-contained: the logo is embedded, there are no external files, no internet
    connection needed. It opens in any browser and prints straight to PDF.

.NOTES
    Requires: PowerShell 5.1
    Version:  1.0

.EXAMPLE
    .\New-ClientReport.ps1

.EXAMPLE
    .\New-ClientReport.ps1 -InputPath "D:\Collected" -Organisation "Riverside Guest House"
#>

[CmdletBinding()]
param(
    # Folder containing the collected JSON files.
    [string]$InputPath = ".\samples",

    # Only build a report for this organisation. Omit to build one for every
    # organisation found in the data.
    [Alias('ClientName')]
    [string]$Organisation,

    # Where the finished reports are written.
    [string]$OutputPath = ".\reports",

    # Your business details, as they appear on the report.
    #
    # Deliberately blank. These were Anywhere IT's own details, which was right when the toolkit
    # only ever ran on Clint's machines and wrong the moment it shipped inside every tenant's
    # client payload: a reseller whose branding failed to reach one of these parameters got
    # another IT company's name, email and phone number on their customer's monthly report.
    # A blank line is a cosmetic gap; somebody else's name is a commercial one.
    #
    # Nothing that has branding relies on these: Install-Client.ps1 passes the tenant's from
    # brand.json, Invoke-MonthlyRun.ps1 passes them from config.json, and a standalone run brands
    # itself by passing these four. Blank renders as no element at all, not as an empty one.
    [string]$CompanyName  = "",
    # Blank drops the line from the footer entirely. The platform has no tenant field for a
    # tagline, and an IT company's own name with an empty strapline under it reads better than
    # somebody else's strapline.
    [string]$CompanyTag   = "",
    [string]$CompanyEmail = "",
    [string]$CompanyPhone = "",

    # Report colours. These were literals in the stylesheet, which happened to be Anywhere IT's
    # own - so the report looked correctly branded here and would have looked wrong on every
    # other IT company's first report. The defaults below are those same literals, so a
    # standalone run is unchanged; the platform passes the tenant's colours instead.
    #
    # AccentColor carries headings and rules, PrimaryColor carries emphasis. Neither is used
    # behind white text, so no contrast adjustment is needed here - the masthead stays black
    # (see the note on it in the stylesheet) precisely so any logo sits safely on it.
    [string]$AccentColor  = "#00b0f0",
    [string]$PrimaryColor = "#7a2e8e",

    # Longest edge the embedded logo is scaled down to. Every report carries its logo inline as
    # base64, and a report is emailed every month for every machine, so a 2 MB upload straight
    # from the portal would otherwise add roughly 2.7 MB to each one. 0 disables scaling.
    [int]$LogoMaxHeight = 120,

    # Something else will produce the PDF, so a missing one is not a fault worth a warning.
    # The platform sets this: the endpoint cannot print as SYSTEM and the server converts instead.
    # A [bool] rather than a [switch] so it survives a powershell.exe -File boundary.
    [bool]$PdfOptional = $false,

    # Logo file. Embedded into the report so it travels with it.
    [string]$LogoPath = ".\templates\logo.png",

    # Which month to report on, as yyyy-MM. Defaults to the newest month found.
    # Without this, a folder holding several months of data would blend them
    # into one report - February's disk space next to August's, and every
    # month's maintenance totals added together.
    [string]$Month,

    # Include every file regardless of month. Only for looking back over time.
    [bool]$AllMonths = $false,

    # Reporting period label. Defaults to the month of the newest data file.
    [string]$ReportMonth,

    # Produce a PDF as well as the web page, using the copy of Microsoft Edge
    # already on the machine. On by default - the PDF is what gets sent.
    [bool]$Pdf = $true,

    # Open the finished report when done. A [bool] rather than a [switch]
    # because switches cannot reliably be passed in from a batch file.
    [bool]$Show = $false
)


# Version of this toolkit. Printed at run time so a stale copy cannot hide.
$ToolkitVersion = '2.11'

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

function ConvertTo-HtmlText {
    param([string]$Text)
    if (-not $Text) { return '' }
    return ($Text -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;' -replace '"','&quot;')
}

function ConvertTo-ReportPdf {
    <#
        Converts a finished HTML report to PDF using Microsoft Edge, which is on
        every Windows 10 and 11 machine. Nothing to install, nothing to license.
        Returns the PDF path, or $null if it could not be produced.
    #>
    param([string]$HtmlPath)

    $edge = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $edge) { return $null }

    # Edge will not start without a writable profile folder of its own,
    # particularly when running as SYSTEM from a scheduled task.
    # SYSTEM has a different TEMP to a logged-in user, and Edge needs somewhere
    # it can definitely write.
    $profileBase = if ($env:TEMP -and (Test-Path $env:TEMP)) { $env:TEMP } else { 'C:\Windows\Temp' }
    $profileDir  = Join-Path $profileBase 'edge-pdf-profile'
    if (-not (Test-Path $profileDir)) {
        try { New-Item -Path $profileDir -ItemType Directory -Force | Out-Null }
        catch { $profileDir = Join-Path 'C:\Windows\Temp' 'edge-pdf-profile'
                New-Item -Path $profileDir -ItemType Directory -Force -ErrorAction SilentlyContinue | Out-Null }
    }

    # The path MUST be absolute. A relative path such as .\reports\x.html
    # produces a file:/// URL Edge cannot resolve, and Edge then fails silently
    # with no error and no PDF - which is exactly what it looked like.
    if (-not (Test-Path $HtmlPath)) {
        Write-Verbose "Report file not found: $HtmlPath"
        return $null
    }
    $HtmlPath = (Resolve-Path -LiteralPath $HtmlPath).ProviderPath

    $pdfPath = [System.IO.Path]::ChangeExtension($HtmlPath, 'pdf')

    # Edge drops privileges into a sandbox and cannot write to a locked-down
    # folder such as C:\ProgramData\HealthCheck, which the installer restricts to
    # SYSTEM and Administrators. It exits with code 0 having written nothing.
    #
    # So Edge always works inside a temp folder it can definitely reach, and the
    # finished PDF is moved into place afterwards. Same for the source page -
    # Edge must be able to read it as well.
    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ('pdf-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -Path $stage -ItemType Directory -Force | Out-Null

    $stageHtml = Join-Path $stage 'report.html'
    $stagePdf  = Join-Path $stage 'report.pdf'
    Copy-Item -LiteralPath $HtmlPath -Destination $stageHtml -Force

    $fileUrl = 'file:///' + ($stageHtml -replace '\\', '/')

    # No embedded quotes. Start-Process adds its own quoting around each element,
    # and pre-quoting produces arguments Edge silently ignores - it then exits
    # cleanly having done nothing, which is indistinguishable from success.
    $edgeArgs = @(
        '--headless'
        '--disable-gpu'
        '--disable-extensions'
        '--no-first-run'
        '--no-default-browser-check'
        # Both spellings. Chromium renamed this flag and silently ignores the old
        # one, so on current Edge every PDF carried the browser's own header and
        # footer: a US-format date, and the TEMP FILE PATH - including the
        # Windows username - printed at the foot of every page of a client's
        # report. The old name is kept for older Edge; unknown flags are ignored.
        '--no-pdf-header-footer'
        '--print-to-pdf-no-header'
        "--user-data-dir=$profileDir"
        "--print-to-pdf=$stagePdf"
        $fileUrl
    )

    # Edge writes nothing to standard output on failure, so its error stream is
    # captured to a file. Without this a PDF failure is completely silent.
    $errFile = Join-Path $env:TEMP "edge-pdf-$PID.err"

    $exitCode = $null
    try {
        $p = Start-Process -FilePath $edge -ArgumentList $edgeArgs -PassThru `
                           -WindowStyle Hidden -RedirectStandardError $errFile
        $null = $p.WaitForExit(120000)
        if (-not $p.HasExited) {
            try { $p.Kill() } catch { }
            Write-Host "      Edge did not finish within two minutes." -ForegroundColor DarkGray
        }
        else { $exitCode = $p.ExitCode }
    }
    catch {
        Write-Host "      Edge would not start: $($_.Exception.Message)" -ForegroundColor DarkGray
        return $null
    }

    # Edge can report it has finished slightly before the file lands
    for ($i = 0; $i -lt 10; $i++) {
        if (Test-Path $stagePdf) { break }
        Start-Sleep -Milliseconds 500
    }

    if (Test-Path $stagePdf) {
        try {
            Move-Item -LiteralPath $stagePdf -Destination $pdfPath -Force -ErrorAction Stop
            Remove-Item $errFile -Force -ErrorAction SilentlyContinue
            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
            return $pdfPath
        }
        catch {
            Write-Host "      PDF was made but could not be moved into place: $($_.Exception.Message)" -ForegroundColor DarkGray
            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
            return $null
        }
    }

    # Say why, rather than just "could not be created" - but only where a missing PDF is actually a
    # fault. On a platform-managed machine it is expected every single month, because the run is a
    # scheduled task as SYSTEM and Edge will not start there, so printing where the PDF was expected
    # and what Edge said is just noise in a log somebody has to read.
    #
    # Only when there is one. Start-Process -PassThru leaves ExitCode empty even on a run that
    # succeeded, so "Edge exit code:" with nothing after it was printed on every failure and read
    # like a clue. It is not one - it cost an afternoon.
    if (-not $PdfOptional) {
        if ($null -ne $exitCode -and "$exitCode" -ne '') {
            Write-Host "      Edge exit code: $exitCode" -ForegroundColor DarkGray
        }
        Write-Host "      Expected PDF at: $pdfPath" -ForegroundColor DarkGray
    }
    # The error file is tidied up either way: skipping the explanation must not leak a file per run.
    if (Test-Path $errFile) {
        $err = (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
        if ($err -and -not $PdfOptional) {
            $firstLine = ($err -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
            Write-Host "      Edge said: $($firstLine.Trim())" -ForegroundColor DarkGray
        }
        Remove-Item $errFile -Force -ErrorAction SilentlyContinue
    }
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    return $null
}

function ConvertTo-Slug {
    param([string]$Text)
    $s = $Text -replace '[^a-zA-Z0-9]+','-'
    return ($s -replace '(^-|-$)','').ToLower()
}

# Client-facing wording. The technical severity never appears in the report.
$SeverityLabel = @{
    'Critical' = 'Needs attention now'
    'Warning'  = 'Should be planned'
    'Info'     = 'Worth knowing'
    'OK'       = 'All good'
}
$SeverityRank = @{ 'Critical' = 0; 'Warning' = 1; 'Info' = 2; 'OK' = 3 }
$SeverityClass = @{ 'Critical' = 'sev-critical'; 'Warning' = 'sev-warning'; 'Info' = 'sev-info'; 'OK' = 'sev-ok' }

# ---------------------------------------------------------------------------
# Load the collected data
# ---------------------------------------------------------------------------

if (-not (Test-Path $InputPath)) {
    Write-Host "Cannot find the folder: $InputPath" -ForegroundColor Red
    exit 1
}

$InputPath = (Resolve-Path -LiteralPath $InputPath).ProviderPath
$files = Get-ChildItem -Path $InputPath -Filter '*.json' -File
if (-not $files) {
    Write-Host "No health check files found in $InputPath" -ForegroundColor Yellow
    exit 1
}

$machines    = @()
$maintenance = @()
$problems    = @()

foreach ($f in $files) {
    try {
        $data = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json

        # A file with no organisation is never thrown away. It is reported loudly
        # and filed under the machine name so the data is not lost.
        # Maintenance records are a different kind of file and must be sorted
        # out first, before any health-check expectations are applied.
        if ($data.RecordType -eq 'Maintenance') {
            $maintenance += $data
            continue
        }

        if (-not $data.Organisation -and -not $data.ClientName) {
            $fallbackOrg = if ($data.Machine.ComputerName) { $data.Machine.ComputerName } else { 'Unknown' }
            $data | Add-Member -NotePropertyName Organisation -NotePropertyValue $fallbackOrg -Force
            $problems += "$($f.Name) has no organisation recorded - filed under '$fallbackOrg'"
        }

        if ($data.SchemaVersion -and [version]("$($data.SchemaVersion).0") -lt [version]'1.2.0' -and $data.Machine) {
            $problems += "$($f.Name) was made by an older version of the health check (v$($data.SchemaVersion))"
        }

        $machines += $data
    }
    catch {
        $problems += "$($f.Name) could not be read: $($_.Exception.Message)"
    }
}

Write-Host ""
Write-Host "  Report generator v$ToolkitVersion" -ForegroundColor DarkGray
Write-Host "  Read $($files.Count) file(s) from $InputPath" -ForegroundColor Cyan
if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "  ATTENTION:" -ForegroundColor Yellow
    foreach ($p in $problems) { Write-Host "    - $p" -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "    Older files usually mean an out-of-date copy of the script is" -ForegroundColor Yellow
    Write-Host "    still on the USB stick. Replace it and collect again." -ForegroundColor Yellow
}
Write-Host ""
if (-not $machines) {
    Write-Host "  Nothing usable was found." -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# Keep to one month - chosen PER CLIENT
#
# A report covers one month so a machine's months never blend together:
# February's disk space beside August's. That rule used to pick the newest
# month across EVERYTHING in the folder, which on a stick used at several
# clients dropped whole clients: visit one on the 30th and another on the 1st,
# and the first vanished with a single grey line. Each client now gets its own
# newest month, inside the per-client loop. A month typed explicitly with
# -Month still applies to everyone.
# ---------------------------------------------------------------------------

function Get-RecordMonth {
    param($Record)
    foreach ($field in 'CollectedAtUtc','RanAtUtc','CollectedLocal','RanAtLocal') {
        if ($Record.$field) {
            try { return ([datetime]$Record.$field).ToString('yyyy-MM') } catch { }
        }
    }
    return $null
}

# Which records belong to the same client. Capitals, spaces and punctuation are
# ignored: "Anywhere IT" and "Anywhere I.T" are one client, as are "Riverside
# Guest House" and "Riverside Guesthouse". The organisation is typed by hand on
# every machine, and exact matching split one client into several reports - it
# had already happened on the toolkit's own test machine. The small risk is
# merging two genuinely different clients whose names differ only by a full
# stop, which is judged far less likely than a technician typing inconsistently.
function Get-OrgKey {
    param([string]$Name)
    if (-not $Name) { return '' }
    return ($Name.ToLowerInvariant() -replace '[^a-z0-9]', '')
}

if (-not $AllMonths -and $Month) {
    $beforeCount = $machines.Count + $maintenance.Count
    $machines    = @($machines    | Where-Object { (Get-RecordMonth $_) -eq $Month })
    $maintenance = @($maintenance | Where-Object { (Get-RecordMonth $_) -eq $Month })
    $dropped     = $beforeCount - ($machines.Count + $maintenance.Count)

    if ($dropped -gt 0) {
        Write-Host "  Reporting on $Month only - $dropped file(s) from other months ignored." -ForegroundColor DarkGray
    }
    if (-not $machines) {
        Write-Host "  No health check data found for $Month." -ForegroundColor Red
        Write-Host "  Use -Month yyyy-MM to pick a different month, or -AllMonths." -ForegroundColor DarkGray
        exit 1
    }
}

if ($Organisation) {
    # Same forgiving match as the grouping below, or a name typed at the menu as
    # "Anywhere IT" would miss the files saved as "Anywhere I.T".
    $orgKeyWanted = Get-OrgKey $Organisation
    $machines = @($machines | Where-Object { (Get-OrgKey $_.Organisation) -eq $orgKeyWanted -or (Get-OrgKey $_.ClientName) -eq $orgKeyWanted })
    if (-not $machines) {
        Write-Host "No data found for '$Organisation'" -ForegroundColor Yellow
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Logo, embedded so the report is a single portable file
# ---------------------------------------------------------------------------

# Resolve the logo relative to this script, not to wherever the user is standing.
# Without this, running from a USB stick produces unbranded reports.
if (-not (Test-Path $LogoPath)) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    foreach ($candidate in @(
        (Join-Path $here 'logo.png'),
        (Join-Path $here 'templates\logo.png'),
        (Join-Path (Split-Path -Parent $here) 'templates\logo.png')
    )) {
        if (Test-Path $candidate) { $LogoPath = $candidate; break }
    }
}

$logoTag = ''
if (Test-Path $LogoPath) {
    try {
        $logoFull = (Resolve-Path -LiteralPath $LogoPath).ProviderPath
        $bytes  = [System.IO.File]::ReadAllBytes($logoFull)
        $ext    = ([System.IO.Path]::GetExtension($LogoPath)).TrimStart('.').ToLower()
        if ($ext -eq 'jpg') { $ext = 'jpeg' }

        # Scale an oversized logo down before embedding. A tenant uploads whatever it has to the
        # portal (up to 2 MB) and that same file ends up base64'd into every monthly report for
        # every machine. Scaling is best-effort: if System.Drawing is unavailable or the file is
        # not an image it can read, the original bytes are embedded exactly as before rather than
        # the report losing its logo.
        if ($LogoMaxHeight -gt 0) {
            try {
                Add-Type -AssemblyName System.Drawing -ErrorAction Stop
                $src = [System.Drawing.Image]::FromFile($logoFull)
                try {
                    if ($src.Height -gt $LogoMaxHeight) {
                        $newH = $LogoMaxHeight
                        $newW = [int][Math]::Round($src.Width * ($newH / [double]$src.Height))
                        if ($newW -lt 1) { $newW = 1 }
                        $bmp = New-Object System.Drawing.Bitmap($newW, $newH)
                        try {
                            $g = [System.Drawing.Graphics]::FromImage($bmp)
                            try {
                                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                                $g.Clear([System.Drawing.Color]::Transparent)
                                $g.DrawImage($src, 0, 0, $newW, $newH)
                            } finally { $g.Dispose() }
                            $ms = New-Object System.IO.MemoryStream
                            try {
                                # Always PNG out, so a logo with transparency keeps it.
                                $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                                $scaled = $ms.ToArray()
                            } finally { $ms.Dispose() }
                            # Only take the scaled copy if it is actually smaller. A small JPEG
                            # re-encoded as PNG can be larger, and swelling the report to "save"
                            # space would be worse than leaving it alone.
                            if ($scaled.Length -lt $bytes.Length) { $bytes = $scaled; $ext = 'png' }
                        } finally { $bmp.Dispose() }
                    }
                } finally { $src.Dispose() }
            }
            catch { Write-Verbose "Logo could not be scaled, embedding it as it is: $($_.Exception.Message)" }
        }

        $b64 = [System.Convert]::ToBase64String($bytes)
        $logoTag = "<img class=`"logo`" src=`"data:image/$ext;base64,$b64`" alt=`"$(ConvertTo-HtmlText $CompanyName)`">"
    }
    catch {
        Write-Verbose "Logo could not be embedded: $($_.Exception.Message)"
    }
}
# With no logo, the company's name stands in for it. With neither - an unbranded run - the
# masthead carries nothing, rather than an empty element holding the space open.
if (-not $logoTag -and $CompanyName -and $CompanyName.Trim()) {
    $logoTag = "<div class=`"logo-text`">$(ConvertTo-HtmlText $CompanyName.Trim())</div>"
}

# ---------------------------------------------------------------------------
# Stylesheet
# ---------------------------------------------------------------------------

$css = @'
:root{
  --ink:#101820; --ink-soft:#4a5866; --hair:#dfe6ec;
  --paper:#ffffff; --wash:#f5f8fa;
  --cyan:#00b0f0; --purple:#7a2e8e;
  --critical:#b3261e; --warning:#a86400; --info:#4a5b6a; --ok:#15794b;
}
*{box-sizing:border-box}
body{
  margin:0; background:var(--wash); color:var(--ink);
  font-family:"Segoe UI",-apple-system,Roboto,Helvetica,Arial,sans-serif;
  font-size:15px; line-height:1.55;
}
.sheet{max-width:860px; margin:0 auto; background:var(--paper);
  box-shadow:0 1px 3px rgba(16,24,32,.10)}

/* Masthead - black so the logo sits on its own ground */
.masthead{background:#000; padding:26px 40px; display:flex; align-items:center;
  justify-content:space-between; gap:24px; flex-wrap:wrap}
.logo{height:52px; width:auto; display:block}
.logo-text{color:#fff; font-size:24px; font-weight:600; letter-spacing:.04em}
.masthead-right{text-align:right; color:#fff}
.masthead-right .doc{font-size:11px; letter-spacing:.18em; text-transform:uppercase;
  color:var(--cyan); font-weight:600}
.masthead-right .who{font-size:20px; font-weight:600; margin-top:2px}
.masthead-right .when{font-size:13px; color:#9fb0bd}

/* Opening statement - serif, because this is a letter, not a dashboard */
.verdict{padding:38px 40px 30px; border-bottom:1px solid var(--hair)}
.verdict p{font-family:Georgia,"Times New Roman",serif; font-size:19px; line-height:1.62;
  margin:0; color:var(--ink)}
.verdict .count{color:var(--purple); font-weight:700}
.verdict .sub{font-family:"Segoe UI",sans-serif; font-size:13.5px; color:var(--ink-soft);
  margin-top:14px}

h2{font-size:12px; letter-spacing:.16em; text-transform:uppercase; color:var(--ink-soft);
  margin:0 0 16px; font-weight:600}
section{padding:32px 40px; border-bottom:1px solid var(--hair)}
section:last-of-type{border-bottom:none}

/* Machine roster - the signature element. Which PC is the problem? */
.roster{border-top:1px solid var(--hair)}
.machine{display:flex; align-items:baseline; gap:14px; padding:13px 0;
  border-bottom:1px solid var(--hair)}
.machine:last-child{border-bottom:none}
.dot{width:9px; height:9px; border-radius:50%; flex:0 0 auto; margin-top:6px}
.dot.sev-critical{background:var(--critical)}
.dot.sev-warning{background:var(--warning)}
.dot.sev-info{background:var(--info)}
.dot.sev-ok{background:var(--ok)}
.machine-name{font-weight:600; min-width:190px}
.machine-desc{color:var(--ink-soft); font-size:13.5px; flex:1}
.machine-state{font-size:12px; font-weight:600; white-space:nowrap}
.machine-state.sev-critical{color:var(--critical)}
.machine-state.sev-warning{color:var(--warning)}
.machine-state.sev-info{color:var(--info)}
.machine-state.sev-ok{color:var(--ok)}

/* Findings */
.item{padding:20px 0 20px 18px; border-left:3px solid var(--hair);
  border-bottom:1px solid var(--hair)}
.item:last-child{border-bottom:none}
.item.sev-critical{border-left-color:var(--critical)}
.item.sev-warning{border-left-color:var(--warning)}
.item.sev-info{border-left-color:var(--info)}
.chip{display:inline-block; font-size:10.5px; letter-spacing:.1em; text-transform:uppercase;
  font-weight:700; padding:3px 8px; border-radius:2px; margin-bottom:9px}
.chip.sev-critical{background:#fdecea; color:var(--critical)}
.chip.sev-warning{background:#fdf3e2; color:var(--warning)}
.chip.sev-info{background:#eef2f5; color:var(--info)}
.item h3{margin:0 0 7px; font-size:16.5px; font-weight:600; line-height:1.4}
.item p{margin:0 0 11px; color:var(--ink-soft); font-size:14px}
.affects{font-size:12.5px; color:var(--ink-soft); margin-bottom:11px}
.affects strong{color:var(--ink); font-weight:600}
.rec{background:var(--wash); border-left:2px solid var(--cyan); padding:11px 14px;
  font-size:14px}
.rec b{display:block; font-size:10.5px; letter-spacing:.1em; text-transform:uppercase;
  color:var(--ink-soft); margin-bottom:4px; font-weight:700}

/* All clear */
.clear-list{margin:0; padding:0; list-style:none; columns:2; column-gap:32px}
.clear-list li{padding:6px 0 6px 22px; position:relative; font-size:14px;
  color:var(--ink-soft); break-inside:avoid}
.clear-list li::before{content:""; position:absolute; left:0; top:12px; width:11px;
  height:2px; background:var(--ok)}

/* Per-machine detail - the evidence that the retainer bought something */
.dev{font-size:15.5px; font-weight:600; margin:30px 0 0; padding-bottom:9px;
  border-bottom:2px solid var(--ink)}
.dev:first-child{margin-top:0}
.dev span{font-weight:400; color:var(--ink-soft); font-size:13px}
table.spec{width:100%; border-collapse:collapse; font-size:13.5px; margin-bottom:4px}
table.spec th{text-align:left; font-weight:600; color:var(--ink-soft); width:27%;
  padding:9px 14px 9px 0; vertical-align:top; font-size:12.5px;
  border-bottom:1px solid var(--hair)}
table.spec td{padding:9px 0; vertical-align:top; border-bottom:1px solid var(--hair)}
table.spec td.val{padding-right:16px}
table.spec tr:last-child th, table.spec tr:last-child td{border-bottom:none}
td.state{width:20%; text-align:right; font-size:12px; font-weight:600; white-space:nowrap}
.state.good{color:var(--ok)}
.state.watch{color:var(--warning)}
.state.note{color:var(--ink-soft)}
@media (max-width:640px){
  table.spec th{width:40%}
  td.state{display:none}
}

footer{background:#101820; color:#8fa3b2; padding:26px 40px; font-size:12.5px}
footer strong{color:#fff; display:block; font-size:14px; margin-bottom:3px}
footer .tag{color:var(--cyan)}
footer .note{margin-top:12px; line-height:1.6}

/* One section per machine. The heading carries the machine's name, what it is
   and its overall state, so each section reads on its own. */
.mach-head{display:flex; align-items:baseline; gap:12px; flex-wrap:wrap;
  padding-bottom:10px; margin-bottom:20px; border-bottom:2px solid var(--ink)}
.mach-name{font-size:19px; font-weight:600}
.mach-sub{font-size:13px; color:var(--ink-soft); flex:1}
.machine-section h2{margin-top:26px}
.machine-section .mach-head + h2{margin-top:0}
.maint-lead{font-family:Georgia,"Times New Roman",serif; font-size:17px; margin:0 0 18px; line-height:1.6}
.maint-lead strong{color:var(--purple)}

@media print{
  /* Page one is the summary; every machine then starts its own page. Only
     when there is more than one machine - see the newpage class. */
  .machine-section.newpage{break-before:page; page-break-before:always}
  .mach-name{font-size:13pt}
  .mach-sub{font-size:8.5pt}
  .maint-lead{font-size:11pt}

  /* The page margin does the spacing, so the sheet drops its own side padding
     and the content gets the full width. This alone removes several pages. */
  @page{ size:A4; margin:12mm 11mm; }

  body{background:#fff; font-size:9.5pt; line-height:1.45}
  .sheet{box-shadow:none; max-width:none}
  .masthead,.verdict,section,footer{padding-left:0; padding-right:0}

  .masthead{padding-top:10px; padding-bottom:10px}
  .logo{height:36px}
  .masthead-right .doc{font-size:7.5pt}
  .masthead-right .who{font-size:14pt}
  .masthead-right .when{font-size:9pt}

  .verdict{padding-top:14px; padding-bottom:12px}
  .verdict p{font-size:12.5pt; line-height:1.4}
  .verdict .sub{font-size:8.5pt; margin-top:8px}

  section{padding-top:13px; padding-bottom:13px}
  h2{font-size:8pt; margin-bottom:9px}

  .machine{padding:6px 0}
  .machine-name{min-width:145px; font-size:9.5pt}
  .machine-desc{font-size:8.5pt}
  .machine-state{font-size:8pt}

  .item{padding:10px 0 10px 11px}
  .item h3{font-size:10.5pt; margin-bottom:4px}
  .item p{font-size:9pt; margin-bottom:6px}
  .affects{font-size:8.5pt; margin-bottom:6px}
  .rec{padding:7px 10px; font-size:8.5pt}
  .chip{font-size:7pt; margin-bottom:6px; padding:2px 6px}

  .dev{font-size:10.5pt; margin-top:14px; padding-bottom:6px}
  .dev span{font-size:8.5pt}
  table.spec{font-size:8.5pt}
  table.spec th{padding:4px 10px 4px 0; font-size:8pt}
  table.spec td{padding:4px 0}

  footer{padding-top:13px; padding-bottom:13px; font-size:8.5pt}

  /* Keep individual entries whole, but never a whole section - forcing a
     section to stay together is what left half-empty pages. */
  .item,.machine,table.spec tr{break-inside:avoid; page-break-inside:avoid}
  h2,h3,.dev{break-after:avoid; page-break-after:avoid}
  section{break-inside:auto}

  /* Colours must survive the print pipeline or the whole thing goes grey */
  .masthead,.chip,.rec,.dot,footer,.machine-state,.state{
    -webkit-print-color-adjust:exact; print-color-adjust:exact
  }
}
@media (max-width:640px){
  .masthead,.verdict,section,footer{padding-left:22px; padding-right:22px}
  .masthead-right{text-align:left}
  .machine{flex-wrap:wrap}
  .machine-name{min-width:0; width:100%}
  .clear-list{columns:1}
}
'@

# The stylesheet is a NON-interpolating here-string on purpose, so the two brand colours are
# substituted afterwards rather than by making it interpolate. Interpolating 170 lines of CSS to
# reach two values would put every '$' in it at the mercy of the parser for no gain.
#
# Only the :root declarations are touched - matching '#00b0f0' everywhere would also rewrite any
# other rule that happens to use the same shade.
foreach ($pair in @(
    @{ Name = 'cyan';   Value = $AccentColor  },
    @{ Name = 'purple'; Value = $PrimaryColor }
)) {
    $colour = ([string]$pair.Value).Trim()
    # Anything that is not a plain hex colour is ignored rather than written into the stylesheet,
    # where a stray brace or quote would break every rule after it.
    if ($colour -notmatch '^#[0-9a-fA-F]{6}$') { continue }
    $css = $css -replace ("--{0}:#[0-9a-fA-F]{{6}}" -f $pair.Name), ("--{0}:{1}" -f $pair.Name, $colour)
}

# ---------------------------------------------------------------------------
# Per-machine detail: the actual numbers, not a claim that we looked
# ---------------------------------------------------------------------------

function New-SpecRow {
    param(
        [string]$Label,
        [string]$Value,
        [ValidateSet('good','watch','note')][string]$State = 'note',
        [string]$StateText = ''
    )
    if (-not $Value) { return '' }
    $st = if ($StateText) { $StateText } else { '' }
    return @"
        <tr><th>$(ConvertTo-HtmlText $Label)</th><td class="val">$(ConvertTo-HtmlText $Value)</td><td class="state $State">$(ConvertTo-HtmlText $st)</td></tr>

"@
}

# The one line under a machine's name: make, model, laptop or desktop, and age.
# Each part is escaped here, so the result is safe to drop into HTML as it is.
function Get-MachineSubtitle {
    param($Device)
    $m = $Device.Machine
    $bits = @()
    if ($m.Manufacturer) { $bits += (ConvertTo-HtmlText $m.Manufacturer) }
    if ($m.Model)        { $bits += (ConvertTo-HtmlText $m.Model) }
    if ($m.FormFactor)   { $bits += (ConvertTo-HtmlText $m.FormFactor) }
    if ($m.AgeYears)     { $bits += "$($m.AgeYears) years old" }
    return ($bits -join ' &middot; ')
}

# The measured values for one machine, as a table. No heading: the machine's
# own section supplies that, so it is not printed twice.
function New-MachineDetail {
    param(
        $Device,

        # True when this month's maintenance optimised the drives. The health
        # check's own record of when that last happened is then out of date.
        [bool]$DrivesDoneByMaintenance = $false
    )

    $m = $Device.Machine
    $os = $Device.OperatingSystem
    $sec = $Device.Security

    $rows = ''

    # ---- Windows ----
    $osName = $os.Caption
    if ($os.DisplayVersion) { $osName = "$osName, version $($os.DisplayVersion)" }
    if ($os.SupportStatus -eq 'End of support') {
        $rows += New-SpecRow -Label 'Windows version' -Value $osName -State 'watch' -StateText 'No longer updated'
    }
    else {
        $rows += New-SpecRow -Label 'Windows version' -Value $osName -State 'good' -StateText 'Supported'
    }

    if ($Device.Windows11 -and $Device.Windows11.Verdict -and $Device.Windows11.Verdict -ne 'Not assessed') {
        $v = $Device.Windows11.Verdict
        if ($v -eq 'Likely eligible') {
            $rows += New-SpecRow -Label 'Windows 11 upgrade' -Value 'This machine can be upgraded free of charge' -State 'good' -StateText 'Available'
        }
        elseif ($v -eq 'Already on Windows 11') {
            $rows += New-SpecRow -Label 'Windows 11 upgrade' -Value 'Already running Windows 11' -State 'good' -StateText 'Done'
        }
        else {
            $rows += New-SpecRow -Label 'Windows 11 upgrade' -Value ($v -replace '^Not eligible: ', 'Not possible as it stands. Missing: ') -State 'watch' -StateText 'Not possible'
        }
    }

    # ---- Virus protection ----
    if ($sec.ProtectionState) {
        $protState = if ($sec.ProtectionState -like 'Protected*') { 'good' } else { 'watch' }
        $protText  = if ($sec.ProtectionState -like 'Protected*') { 'Active' } else { 'Not protected' }
        $protValue = $sec.ProtectionState -replace '^Protected \(', '' -replace '\)$', ''
        $rows += New-SpecRow -Label 'Virus protection' -Value $protValue -State $protState -StateText $protText
    }
    if ($sec.SignatureAgeDays -ne $null) {
        $sig = if ($sec.SignatureAgeDays -le 0) { 'Updated today' }
               elseif ($sec.SignatureAgeDays -eq 1) { 'Updated yesterday' }
               else { "Last updated $($sec.SignatureAgeDays) days ago" }
        $sigState = if ($sec.SignatureAgeDays -le 3) { 'good' } else { 'watch' }
        $rows += New-SpecRow -Label 'Virus definitions' -Value $sig -State $sigState -StateText ''
    }

    # ---- Backup ----
    $bc = $Device.BackupCheck
    $b  = $Device.Backup

    if ($bc -and $bc.NewestBackupAgeDays -ne $null) {
        $age = $bc.NewestBackupAgeDays
        $ageText = if ($age -lt 1) { 'today' } elseif ($age -lt 2) { 'yesterday' } else { "$([math]::Round($age)) days ago" }
        $bState = if ($age -le 7) { 'good' } elseif ($age -le 31) { 'watch' } else { 'watch' }
        $val = "Last backup $ageText"
        if ($bc.TotalBackupSizeGB) { $val = "$val. $($bc.TotalBackupSizeGB) GB of backup data held." }
        $rows += New-SpecRow -Label 'Backup' -Value $val -State $bState -StateText $ageText

        if ($bc.NewestBackupPath) {
            $rows += New-SpecRow -Label 'Backup location' -Value $bc.NewestBackupPath -State 'note'
        }
        if ($bc.VeeamLastResult) {
            $vState = if ($bc.VeeamLastResult -eq 'Succeeded') { 'good' } else { 'watch' }
            $rows += New-SpecRow -Label 'Last backup job' -Value "Reported as $($bc.VeeamLastResult.ToLower())" -State $vState -StateText $bc.VeeamLastResult
        }
    }
    elseif ($bc -and $bc.KnownLocationMissing) {
        $rows += New-SpecRow -Label 'Backup' -Value "The backup drive is not connected. It was last seen at $($bc.KnownLocationMissing)." -State 'watch' -StateText 'Drive missing'
    }
    elseif ($b.EvidenceFound) {
        $bVal = 'Backup activity detected on this machine'
        if ($b.BackupSoftware -and $b.BackupSoftware.Count -gt 0) {
            $bVal = "Backed up using $(($b.BackupSoftware | Select-Object -ExpandProperty Product) -join ', ')"
        }
        $rows += New-SpecRow -Label 'Backup' -Value $bVal -State 'good' -StateText 'In place'
    }
    elseif ($b.OneDriveSyncFolders -and $b.OneDriveSyncFolders.Count -gt 0) {
        $rows += New-SpecRow -Label 'Backup' -Value 'Files sync to OneDrive, but there is no true backup keeping older versions' -State 'watch' -StateText 'Sync only'
    }
    else {
        $rows += New-SpecRow -Label 'Backup' -Value 'No backup was found on this machine' -State 'watch' -StateText 'None found'
    }

    # ---- Disk space, per drive ----
    foreach ($v in $Device.Storage.Volumes) {
        $label = "Space on drive $($v.Drive)"
        $val   = "$($v.FreeGB) GB free of $($v.TotalGB) GB"
        if ($v.FreePercent -le 10)      { $rows += New-SpecRow -Label $label -Value $val -State 'watch' -StateText "$($v.FreePercent)% free" }
        elseif ($v.FreePercent -le 20)  { $rows += New-SpecRow -Label $label -Value $val -State 'watch' -StateText "$($v.FreePercent)% free" }
        else                            { $rows += New-SpecRow -Label $label -Value $val -State 'good'  -StateText "$($v.FreePercent)% free" }
    }

    # ---- Drive health percentage, the CrystalDiskInfo-style number ----
    if ($Device.Storage.SmartDrives) {
        foreach ($sd in $Device.Storage.SmartDrives) {
            $kind = switch ("$($sd.MediaType)") {
                'SSD'  { 'Solid state drive' }
                'HDD'  { 'Mechanical hard drive' }
                default { 'Drive' }
            }
            $val = "$kind"
            if ($sd.SizeGB) { $val = "$val, $($sd.SizeGB) GB" }
            if ($sd.PowerOnHours) { $val = "$val, $([math]::Round($sd.PowerOnHours/24)) days of use" }
            if ($sd.TemperatureC)  { $val = "$val, $($sd.TemperatureC)$([char]0x00B0)C" }

            # Counted through a filtering pipeline, not @($x).Count: JSON from
            # before v2.9 has no Breached field, and @($null).Count is 1, which
            # would have marked every older drive as failing.
            $breachedCount = @($sd.Breached       | Where-Object { $_ }).Count
            $concernCount  = @($sd.Concerns       | Where-Object { $_ }).Count
            $attrCount     = @($sd.AttributesSeen | Where-Object { $_ }).Count

            # The drive's own verdict outranks any percentage. This used to be
            # ignored entirely: with no percentage the row said "Healthy" no matter
            # what, so a failing mechanical drive could be Critical in the findings
            # and "Healthy" in this table on the same page.
            if ($sd.PredictFailure -eq $true -or $breachedCount -gt 0) {
                $rows += New-SpecRow -Label 'Drive health' -Value "$val. The drive is reporting that it is failing." `
                                     -State 'watch' -StateText 'Failing'
            }
            elseif ($sd.HealthPercent -ne $null) {
                $hp = [int]$sd.HealthPercent
                $hState = if ($hp -le 80 -or $concernCount -gt 0) { 'watch' } else { 'good' }
                $rows += New-SpecRow -Label 'Drive health' -Value $val -State $hState -StateText "$hp% healthy"
            }
            elseif ($attrCount -gt 0) {
                # The drive's health table WAS read and nothing is past its
                # threshold. Mechanical drives report no life percentage at all -
                # CrystalDiskInfo shows them as "Good" without one - so this is a
                # real result, not a missing one.
                if ($concernCount -gt 0) {
                    $rows += New-SpecRow -Label 'Drive health' -Value $val -State 'watch' -StateText 'Early wear'
                }
                else {
                    $rows += New-SpecRow -Label 'Drive health' -Value $val -State 'good' -StateText 'No problems reported'
                }
            }
            else {
                # Nothing detailed could be read - most often an NVMe drive when
                # the check ran without administrator rights. Fall back to Windows'
                # own verdict, and say that is what it is rather than calling the
                # drive healthy on no evidence.
                $pd = @($Device.Storage.PhysicalDisks | Where-Object { $_.FriendlyName -eq $sd.Name })
                $winSays = if ($pd.Count -gt 0) { "$($pd[0].HealthStatus)" } else { '' }
                if ($winSays -eq 'Healthy') {
                    $rows += New-SpecRow -Label 'Drive health' -Value "$val. Detailed wear figures could not be read on this check." `
                                         -State 'good' -StateText 'No problems reported'
                }
                elseif ($winSays) {
                    $rows += New-SpecRow -Label 'Drive health' -Value $val -State 'watch' -StateText $winSays
                }
                else {
                    $rows += New-SpecRow -Label 'Drive health' -Value "$val. Its condition could not be read on this check." `
                                         -State 'note' -StateText 'Not checked'
                }
            }
        }
    }
    else {
    foreach ($pd in $Device.Storage.PhysicalDisks) {
        $kind = switch ("$($pd.MediaType)") {
            'SSD'  { 'Solid state drive' }
            'HDD'  { 'Mechanical hard drive' }
            default { 'Drive' }
        }
        $val = "$kind, $($pd.SizeGB) GB"
        if ($pd.TemperatureC) { $val = "$val, running at $($pd.TemperatureC)$([char]0x00B0)C" }
        if ($pd.PowerOnHours) { $val = "$val, $([math]::Round($pd.PowerOnHours/24)) days of use" }

        if ("$($pd.HealthStatus)" -eq 'Healthy') {
            $rows += New-SpecRow -Label 'Drive health' -Value $val -State 'good' -StateText 'Healthy'
        }
        else {
            $rows += New-SpecRow -Label 'Drive health' -Value $val -State 'watch' -StateText "$($pd.HealthStatus)"
        }
    }
    }

    # ---- Recycle bin ----
    $rb = $Device.Storage.RecycleBin
    if ($rb -and $rb.ItemCount -gt 0) {
        $rbSize = if ($rb.SizeGB -ge 1) { "$($rb.SizeGB) GB" } else { "$($rb.SizeMB) MB" }
        $val = "$rbSize in $($rb.ItemCount) deleted files"
        if ($rb.OldestDays -ge 365) {
            $yrs = [math]::Round($rb.OldestDays / 365, 1)
            $val = "$val, the oldest about $yrs years old"
        }
        elseif ($rb.OldestDays -ge 30) {
            $val = "$val, the oldest $($rb.OldestDays) days old"
        }
        $val = "$val. We leave these alone in case they are still wanted."
        $rbState = if ($rb.SizeGB -ge 5) { 'watch' } else { 'note' }
        $rows += New-SpecRow -Label 'Recycle bin' -Value $val -State $rbState -StateText "Could free $rbSize"
    }
    elseif ($rb) {
        $rows += New-SpecRow -Label 'Recycle bin' -Value 'Empty' -State 'good' -StateText 'Nothing held'
    }

    # ---- Temporary files ----
    # Always shown, even when it is below the level that raises a finding. The
    # measurement is the point - a client told nothing learns nothing, and the
    # figure is what makes a maintenance visit worth booking.
    $tf = $Device.Storage.TempFiles
    if ($tf -and $tf.FileCount -gt 0) {
        $tfSize = if ($tf.TotalGB -ge 1) { "$($tf.TotalGB) GB" } else { "$($tf.TotalMB) MB" }
        $rows += New-SpecRow -Label 'Temporary files' `
                             -Value "$tfSize of working files left behind by Windows and other programs, none of them touched in over $($tf.OlderThanDays) days. Safe to clear." `
                             -State 'note' -StateText "Could free $tfSize"
    }
    elseif ($tf) {
        $rows += New-SpecRow -Label 'Temporary files' -Value 'Nothing worth clearing' -State 'good' -StateText 'Clean'
    }

    # ---- Drive housekeeping ----
    $opt = $Device.Storage.Optimisation
    if ($DrivesDoneByMaintenance) {
        $rows += New-SpecRow -Label 'Drive housekeeping' `
                             -Value 'Tidied during this month''s maintenance, which keeps the drives running at full speed.' `
                             -State 'good' -StateText 'Done this month'
    }
    elseif ($opt) {
        if ($opt.NeverRun) {
            $rows += New-SpecRow -Label 'Drive housekeeping' `
                                 -Value 'Windows has no record of ever having tidied these drives. They gradually slow down without it.' `
                                 -State 'watch' -StateText 'Never run'
        }
        elseif ($opt.LastRunDays -ne $null) {
            $optState = if ($opt.LastRunDays -ge 60) { 'watch' } else { 'good' }
            $rows += New-SpecRow -Label 'Drive housekeeping' `
                                 -Value "Windows last tidied the drives $($opt.LastRunDays) days ago, which is what keeps them running at full speed." `
                                 -State $optState -StateText "$($opt.LastRunDays) days ago"
        }
        foreach ($ov in $opt.Volumes) {
            if ($ov.FragmentPercent -ne $null) {
                $fState = if ($ov.FragmentPercent -ge 10) { 'watch' } else { 'good' }
                $rows += New-SpecRow -Label "Drive $($ov.Drive) fragmentation" `
                                     -Value "Files on this drive are $($ov.FragmentPercent)% scattered across the disk." `
                                     -State $fState -StateText "$($ov.FragmentPercent)%"
            }
        }
    }

    # ---- Windows system files ----
    $sf = $Device.SystemFiles
    if ($sf -and $sf.Checked) {
        if ($sf.Result -eq 'Clean') {
            $rows += New-SpecRow -Label 'Windows system files' `
                                 -Value 'Every protected Windows file was compared against Microsoft originals and none had been damaged or altered.' `
                                 -State 'good' -StateText 'All intact'
        }
        elseif ($sf.Result -eq 'Damaged') {
            $rows += New-SpecRow -Label 'Windows system files' `
                                 -Value 'Some of Windows own files have been damaged or altered. Nothing was changed - this check only looks.' `
                                 -State 'watch' -StateText 'Damage found'
        }
        elseif ($sf.Result -eq 'TimedOut') {
            $rows += New-SpecRow -Label 'Windows system files' `
                                 -Value 'The check ran out of time before it could finish.' `
                                 -State 'watch' -StateText 'Did not finish'
        }
        elseif ($sf.Result -eq 'RebootRequired') {
            $rows += New-SpecRow -Label 'Windows system files' `
                                 -Value 'This could not be checked because the machine is waiting for a restart to finish an earlier update. It will be checked on the next run.' `
                                 -State 'note' -StateText 'After restart'
        }
        elseif ($sf.Result -eq 'Refused') {
            $rows += New-SpecRow -Label 'Windows system files' `
                                 -Value 'Windows would not run its own file check on this machine.' `
                                 -State 'watch' -StateText 'Would not run'
        }
        else {
            # Unrecognised or suspiciously fast. Never presented as a pass - we
            # genuinely do not know, and saying otherwise would be a false clean
            # bill of health, which is the one thing this report must not give.
            $rows += New-SpecRow -Label 'Windows system files' `
                                 -Value 'The check ran but did not report a result we could read, so this machine has not been confirmed either way. Worth checking by hand.' `
                                 -State 'watch' -StateText 'Not confirmed'
        }
    }
    # "Could not run" and "was deliberately not asked to run" are different, and
    # only the first is worth telling a client about.
    #
    # 'Not requested' means the maintenance script has already run sfc /scannow
    # this month - option 2 and the monthly run both switch the health check's
    # copy off to avoid ten minutes of duplicated work. Rendering a row for it
    # put two contradictory lines in the same report:
    #
    #   Windows system files   Checked every protected Windows file ... No damage found
    #   Windows system files   Not checked on this run - not requested.  Not checked
    #
    # Both true, and together they read as the report arguing with itself. The
    # other skip reasons - no administrator rights, a restart pending - are
    # genuine inabilities and must still be shown, or an unchecked machine looks
    # like a checked one.
    elseif ($sf -and $sf.Skipped -and $sf.Skipped -notmatch 'Not requested') {
        $rows += New-SpecRow -Label 'Windows system files' `
                             -Value "Not checked on this run - $($sf.Skipped.ToLower())." `
                             -State 'note' -StateText 'Not checked'
    }

    # ---- Encryption ----
    $sysVol = $sec.BitLockerVolumes | Where-Object { $_.Mount -eq 'C:' } | Select-Object -First 1
    if ($sysVol) {
        if ($sysVol.Protection -eq 'On') {
            $rows += New-SpecRow -Label 'Drive encryption' -Value 'The drive is encrypted, so the data cannot be read if the machine is stolen' -State 'good' -StateText 'On'
        }
        else {
            $enc = if ($m.IsPortable) { 'watch' } else { 'note' }
            $rows += New-SpecRow -Label 'Drive encryption' -Value 'Not encrypted. Anyone who removes the drive can read the files on it' -State $enc -StateText 'Off'
        }
    }

    # ---- Specification ----
    if ($m.CpuName) { $rows += New-SpecRow -Label 'Processor' -Value $m.CpuName -State 'note' }
    if ($m.RamGB) {
        $ram = [math]::Round($m.RamGB)
        $ramState = if ($ram -le 4) { 'watch' } elseif ($ram -le 8) { 'note' } else { 'good' }
        $rows += New-SpecRow -Label 'Memory' -Value "$ram GB" -State $ramState -StateText ''
    }
    if ($m.AgeYears) {
        $ageState = if ($m.AgeYears -ge 10) { 'watch' } elseif ($m.AgeYears -ge 5) { 'watch' } else { 'good' }
        $ageText  = if ($m.AgeYears -ge 5) { 'Ageing' } else { 'Current' }
        $rows += New-SpecRow -Label 'Machine age' -Value "Approximately $($m.AgeYears) years old" -State $ageState -StateText $ageText
    }

    # ---- Accounts ----
    if ($sec.LocalAdministrators -and $sec.LocalAdministrators.Count -gt 0) {
        $names = $sec.LocalAdministrators | ForEach-Object { ($_ -split '\\')[-1] }
        $adminState = if ($names.Count -gt 3) { 'watch' } else { 'good' }
        $rows += New-SpecRow -Label 'Accounts with full control' -Value ($names -join ', ') -State $adminState -StateText "$($names.Count) account$(if($names.Count -ne 1){'s'})"
    }

    # ---- Updates ----
    if ($Device.Updates.LastInstalledDate) {
        try {
            if (-not $Device.Updates.LastInstalledDate) { throw 'no date' }
            $lu = [datetime]$Device.Updates.LastInstalledDate
            $days = [math]::Round(((Get-Date) - $lu).TotalDays)
            $uState = if ($days -le 45) { 'good' } else { 'watch' }
            $rows += New-SpecRow -Label 'Last security update' -Value $lu.ToString('d MMMM yyyy') -State $uState -StateText "$days days ago"
        } catch { }
    }
    if ($Device.Updates.PendingCount -ne $null) {
        $p = [int]$Device.Updates.PendingCount
        $pState = if ($p -eq 0) { 'good' } elseif ($p -lt 10) { 'note' } else { 'watch' }
        $pVal = if ($p -eq 0) { 'Nothing outstanding' } else { "$p update$(if($p -ne 1){'s'}) waiting to install" }
        $rows += New-SpecRow -Label 'Updates waiting' -Value $pVal -State $pState -StateText ''
    }

    # ---- Reliability ----
    if ($Device.Reliability) {
        $ec = [int]$Device.Reliability.CriticalErrorCount
        $eState = if ($ec -ge 150) { 'watch' } elseif ($ec -ge 40) { 'note' } else { 'good' }
        $eText  = if ($ec -ge 150) { 'High' } elseif ($ec -ge 40) { 'Some' } else { 'Normal' }
        $rows += New-SpecRow -Label 'Errors in the last 30 days' -Value "$ec recorded by Windows" -State $eState -StateText $eText

        if ($Device.Reliability.UnexpectedShutdowns -ne $null) {
            $us = [int]$Device.Reliability.UnexpectedShutdowns
            $usState = if ($us -ge 3) { 'watch' } elseif ($us -gt 0) { 'note' } else { 'good' }
            $usVal = if ($us -eq 0) { 'None. The machine has always shut down cleanly' } else { "$us time$(if($us -ne 1){'s'}) the machine lost power or stopped responding" }
            $rows += New-SpecRow -Label 'Unexpected shutdowns' -Value $usVal -State $usState -StateText ''
        }
    }

    # ---- Licensing ----
    if ($Device.Licensing.WindowsActivated -ne $null) {
        if ($Device.Licensing.WindowsActivated) {
            $rows += New-SpecRow -Label 'Windows licence' -Value 'Genuine and correctly activated' -State 'good' -StateText 'Activated'
        }
        else {
            $rows += New-SpecRow -Label 'Windows licence' -Value 'Windows is not correctly activated on this machine' -State 'watch' -StateText 'Not activated'
        }
    }
    if ($Device.Licensing.OfficeProducts -and $Device.Licensing.OfficeProducts.Count -gt 0) {
        foreach ($o in $Device.Licensing.OfficeProducts) {
            $oState = if ("$($o.Name)" -match '2013|2016|2019') { 'watch' } else { 'good' }
            $oText  = if ("$($o.Name)" -match '2013|2016|2019') { 'Out of support' } else { 'Supported' }
            $rows += New-SpecRow -Label 'Microsoft Office' -Value "$($o.Name)" -State $oState -StateText $oText
        }
    }
    else {
        $rows += New-SpecRow -Label 'Microsoft Office' -Value 'No Microsoft Office installation was detected' -State 'note' -StateText ''
    }

    # ---- Firewall ----
    if ($sec.FirewallProfiles -and $sec.FirewallProfiles.Count -gt 0) {
        $off = @($sec.FirewallProfiles | Where-Object { -not $_.Enabled })
        if ($off.Count -eq 0) {
            $rows += New-SpecRow -Label 'Firewall' -Value 'Switched on for all network types' -State 'good' -StateText 'On'
        }
        else {
            $rows += New-SpecRow -Label 'Firewall' -Value "Switched off for: $(($off | Select-Object -ExpandProperty Profile) -join ', ')" -State 'watch' -StateText 'Partly off'
        }
    }

    return @"
      <table class="spec">
$rows      </table>

"@
}

# ---------------------------------------------------------------------------
# Build one report per client
# ---------------------------------------------------------------------------

if (-not (Test-Path $OutputPath)) { New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null }

# Everything downstream needs a real absolute path, not '.\reports'
$OutputPath = (Resolve-Path -LiteralPath $OutputPath).ProviderPath

# Older files have no Organisation, so fall back to the client name.
foreach ($m in $machines) {
    if (-not $m.Organisation) {
        $m | Add-Member -NotePropertyName Organisation -NotePropertyValue $m.ClientName -Force
    }
    if (-not $m.UserOrRole) {
        $fallback = if ($m.ClientName) { $m.ClientName } else { $m.Machine.ComputerName }
        $m | Add-Member -NotePropertyName UserOrRole -NotePropertyValue $fallback -Force
    }
}

# @() is essential - the trap this project keeps meeting. With one client,
# Group-Object returns a single group rather than a list of one, and .Count then
# reads that group's OWN count - the number of MACHINES. Three machines at one
# client printed "Building 3 report(s)" and wrote one.
$clients = @($machines | Group-Object { Get-OrgKey $_.Organisation })
$written = @()

# The name a client is known by. When the same client was typed more than one
# way, use the spelling that appears most; on a tie, the one with more capitals,
# so a client never sees their name in lower case at the top of their report.
function Get-DisplayName {
    param($Records)
    $spellings = @($Records | ForEach-Object { "$($_.Organisation)".Trim() } | Where-Object { $_ })
    if ($spellings.Count -eq 0) { return 'Unknown' }
    $pick = @($spellings | Group-Object -CaseSensitive |
              Sort-Object @{ Expression = { $_.Count }; Descending = $true },
                          @{ Expression = { ([regex]::Matches($_.Name, '[A-Z]')).Count }; Descending = $true },
                          Name)[0]
    return $pick.Name
}

Write-Host "  Building $($clients.Count) report(s):" -ForegroundColor Cyan
Write-Host ""

foreach ($clientGroup in $clients) {

    $clientKey     = $clientGroup.Name
    $clientRecords = @($clientGroup.Group)
    $client        = Get-DisplayName $clientRecords
    $clientMaint   = @($maintenance | Where-Object { (Get-OrgKey $_.Organisation) -eq $clientKey })

    # Tell the technician when one client was typed more than one way, so the
    # habit gets fixed - the report is combined either way.
    $variants = @($clientRecords | ForEach-Object { "$($_.Organisation)".Trim() } |
                  Where-Object { $_ -and $_ -cne $client } | Group-Object -CaseSensitive | ForEach-Object { $_.Name })
    if ($variants.Count -gt 0) {
        Write-Host "  NOTE: '$client' was also typed as $(($variants | ForEach-Object { "'$_'" }) -join ', ') - combined into one report." -ForegroundColor Yellow
    }

    # This client's own newest month. Chosen from the HEALTH CHECK records, since
    # those are what the report is built from; maintenance records then follow
    # that month.
    if (-not $AllMonths -and -not $Month) {
        $monthsHere = @($clientRecords | ForEach-Object { Get-RecordMonth $_ } | Where-Object { $_ })
        if ($monthsHere.Count -gt 0) {
            $clientMonth = @($monthsHere | Sort-Object -Descending)[0]
            $beforeHere  = $clientRecords.Count + $clientMaint.Count
            $clientRecords = @($clientRecords | Where-Object { (Get-RecordMonth $_) -eq $clientMonth })
            $clientMaint   = @($clientMaint   | Where-Object { (Get-RecordMonth $_) -eq $clientMonth })
            $droppedHere = $beforeHere - ($clientRecords.Count + $clientMaint.Count)
            if ($droppedHere -gt 0) {
                Write-Host "  $client - reporting on $clientMonth; $droppedHere older file(s) for this client ignored." -ForegroundColor DarkGray
            }
        }
    }

    # If a machine was checked twice in the same month, use only the latest run.
    $devices = @(
        $clientRecords |
        Group-Object { $_.Machine.ComputerName } |
        ForEach-Object {
            $_.Group | Sort-Object {
                if ($_.CollectedAtUtc) { try { [datetime]$_.CollectedAtUtc } catch { [datetime]'1900-01-01' } }
                else { [datetime]'1900-01-01' }
            } -Descending | Select-Object -First 1
        }
    )

    # Reporting period
    $period = $ReportMonth
    if (-not $period) {
        $dates = @()
        foreach ($d in $devices) {
            if ($d.CollectedAtUtc) {
                try { $dates += [datetime]$d.CollectedAtUtc } catch { }
            }
        }
        if ($dates.Count -gt 0) {
            $period = @($dates | Sort-Object -Descending)[0].ToString('MMMM yyyy')
        }
        else {
            $period = (Get-Date).ToString('MMMM yyyy')
        }
    }

    # =======================================================================
    # ONE SECTION PER MACHINE
    #
    # Each machine gets its own section, in the same order as the roster: its
    # problems, what was done to it, what was found healthy, and its measured
    # details - with nothing from any other machine mixed in.
    #
    # This replaces a layout that merged every machine together: one entry per
    # problem marked "Affects Reception, Office", and one list of maintenance
    # tasks for all machines combined. It kept the report short, but it hid
    # exactly what a retainer client is paying to see - what was done to EACH
    # machine. On paper, with more than one machine, page one is the summary
    # and every machine then starts its own page.
    # =======================================================================

    $ordered = @($devices | Sort-Object { $_.UserOrRole })

    # The latest maintenance record for one machine. Matched on the computer name
    # rather than the label, which can change between runs. Callers wrap the
    # result in @() - a function returning one item hands back the item itself,
    # not a list of one.
    function Get-DeviceMaintenance {
        param($Device)
        $cn = $Device.Machine.ComputerName
        return @($clientMaint | Where-Object { $_.ComputerName -eq $cn } |
                 Sort-Object { try { [datetime]$_.RanAtUtc } catch { [datetime]'1900-01-01' } } -Descending |
                 Select-Object -First 1)
    }

    # Everything the report says about one machine: its own findings, plus
    # anything its maintenance turned up.
    function Get-DeviceFindings {
        param($Device, $Maint)
        $list = New-Object System.Collections.ArrayList
        foreach ($f in @($Device.Findings)) { if ($f) { [void]$list.Add($f) } }
        foreach ($mr in @($Maint)) {
            foreach ($iss in @($mr.Issues)) { if ($iss) { [void]$list.Add($iss) } }
        }
        return $list
    }

    $perDevice = @()
    foreach ($d in $ordered) {
        $mr    = @(Get-DeviceMaintenance -Device $d)
        $finds = @(Get-DeviceFindings -Device $d -Maint $mr)

        # Cross-reference before alarming. When this month's maintenance dealt with
        # the drives - TRIM, a defragment, or a check that found no work needed -
        # the health check's "never been tidied" or "last tidied N days ago" is
        # out of date, and printing both put "Drive C: optimised - TRIM" and "this
        # machine's drives have never been given their routine tidy-up" on the
        # same page. Windows may not record our own optimisation where the health
        # check looks, so the maintenance record is the authority here.
        $drivesDone = @(@($mr | ForEach-Object { $_.Tasks }) |
                        Where-Object { $_.Name -like 'Drive *' -and $_.Result -in 'Done','Skipped' }).Count -gt 0
        if ($drivesDone) {
            $finds = @($finds | Where-Object {
                $_.Title -notmatch "^This machine's drives have never been|^Routine drive housekeeping last ran|^A drive is fragmented enough"
            })
        }

        $perDevice += [pscustomobject]@{ Device = $d; Maint = $mr; Findings = $finds; DrivesDone = $drivesDone }
    }

    # ---- Counts for the opening statement ----
    # Each machine's problems are counted separately, because each machine now
    # shows them separately. The same problem on three machines is three entries
    # on the page, so it is three in the count.
    $allFindings = @($perDevice | ForEach-Object { $_.Findings })
    $nCritical   = @($allFindings | Where-Object { $_.Severity -eq 'Critical' }).Count
    $nWarning    = @($allFindings | Where-Object { $_.Severity -eq 'Warning'  }).Count
    $nMachines   = $ordered.Count

    $machineWord = if ($nMachines -eq 1) { 'machine' } else { 'machines' }

    if ($nCritical -eq 0 -and $nWarning -eq 0) {
        $verdict = "We checked your $nMachines $machineWord this month and found <span class=`"count`">nothing that needs your attention</span>. Everything is running as it should."
    }
    elseif ($nCritical -eq 0) {
        $verdict = "We checked your $nMachines $machineWord this month. Nothing is urgent, but <span class=`"count`">$nWarning</span> $(if($nWarning -eq 1){'item is'}else{'items are'}) worth planning for."
    }
    else {
        $verdict = "We checked your $nMachines $machineWord this month and found <span class=`"count`">$nCritical</span> $(if($nCritical -eq 1){'thing that needs'}else{'things that need'}) your attention, plus $nWarning to plan for."
    }

    # ---- Machine roster: the contents page ----
    $rosterHtml = ''
    foreach ($pd in $perDevice) {
        $d = $pd.Device
        $devFindings = @($pd.Findings | Where-Object { $_.Severity -ne 'OK' })
        $worst = 'OK'
        foreach ($s in 'Critical','Warning','Info') {
            if (@($devFindings | Where-Object { $_.Severity -eq $s }).Count -gt 0) { $worst = $s; break }
        }

        $headline = 'Nothing needs attention this month'
        $top = @($devFindings | Sort-Object { $SeverityRank[$_.Severity] })
        if ($top.Count -gt 0) { $headline = $top[0].Title }

        $desc = @()
        if ($d.Machine.Manufacturer) { $desc += $d.Machine.Manufacturer }
        if ($d.Machine.Model)        { $desc += $d.Machine.Model }
        $descText = ($desc -join ' ')
        if ($d.Machine.FormFactor)   { $descText = "$descText ($($d.Machine.FormFactor))" }
        # A real character, not an entity - this string goes through HTML escaping
        $descText = "$descText $([char]0x00B7) $($d.Machine.ComputerName)"

        $cls = $SeverityClass[$worst]
        $rosterHtml += @"
    <div class="machine">
      <span class="dot $cls"></span>
      <span class="machine-name">$(ConvertTo-HtmlText $d.UserOrRole)<br><span style="font-weight:400;font-size:12px;color:var(--ink-soft)">$(ConvertTo-HtmlText $descText)</span></span>
      <span class="machine-desc">$(ConvertTo-HtmlText $headline)</span>
      <span class="machine-state $cls">$($SeverityLabel[$worst])</span>
    </div>

"@
    }

    # ---- One machine's findings of one severity ----
    # No "Affects" line: everything in a machine's section is about that machine.
    function Get-MachineFindingBlock {
        param([string]$Severity, $Items)
        $html = ''
        foreach ($f in @($Items | Where-Object { $_.Severity -eq $Severity })) {
            $cls = $SeverityClass[$Severity]
            $rec = ''
            if ($f.Recommendation) {
                $rec = "<div class=`"rec`"><b>What we suggest</b>$(ConvertTo-HtmlText $f.Recommendation)</div>"
            }
            $html += @"
    <div class="item $cls">
      <span class="chip $cls">$($SeverityLabel[$Severity])</span>
      <h3>$(ConvertTo-HtmlText $f.Title)</h3>
      <p>$(ConvertTo-HtmlText $f.Detail)</p>
      $rec
    </div>

"@
        }
        return $html
    }

    # ---- What was done to one machine ----
    function Get-MachineMaintBlock {
        param($Maint)
        $mr = @($Maint)
        if ($mr.Count -eq 0) { return '' }
        $mr = $mr[0]

        if ($mr.MaintenanceRan -eq $false) {
            # Only point "above" when the reason really is there - it is recorded
            # as an issue by the maintenance script, and a pointer to nothing is
            # worse than no pointer.
            $why = if (@($mr.Issues | Where-Object { $_ }).Count -gt 0) { ' The reason is given above.' } else { '' }
            return "    <p class=`"maint-lead`">Maintenance was <strong>held back</strong> on this machine this month as a precaution.$why</p>`n"
        }

        $mb = 0
        if ($mr.TotalFreedMB) { $mb = [double]$mr.TotalFreedMB }

        # A machine that needed nothing cleaned is the retainer working, not the
        # retainer failing. "We cleared 0 GB" read as "we did nothing".
        if ($mb -lt 1) {
            $lead = "Nothing was left to clear on this machine this month, which is what regular maintenance is for. Everything below was still checked and tuned."
        }
        elseif ($mb -ge 1024) {
            # One decimal place, the same as the task rows below - the same number
            # printed as "1,76" in the sentence and "1,8" in the table looked like
            # two different figures.
            $lead = "We cleared <strong>$("{0:N1} GB" -f ($mb / 1024))</strong> of unnecessary files on this machine."
        }
        else {
            $lead = "We cleared <strong>$("{0:N0} MB" -f $mb)</strong> of unnecessary files on this machine."
        }

        $rows = ''
        foreach ($t in @($mr.Tasks | Where-Object { $_.Result -in 'Done','Skipped' -and $_.Note -ne 'Not touched' })) {
            $freed = 0
            if ($t.FreedMB) { $freed = [double]$t.FreedMB }
            $txt = if ($freed -ge 1024) { "{0:N1} GB" -f ($freed / 1024) }
                   elseif ($freed -ge 1) { "{0:N0} MB" -f $freed }
                   elseif ($t.Note) { $t.Note }
                   else { 'Done' }
            # A skipped task is not a tick. It used to show green like the rest,
            # so "needs a restart first" looked like a job done.
            $st = if ($t.Result -eq 'Skipped') { 'note' } else { 'good' }
            $rows += New-SpecRow -Label $t.Name -Value $t.Description -State $st -StateText $txt
        }

        return @"
    <p class="maint-lead">$lead</p>
    <table class="spec">
$rows    </table>

"@
    }

    # ---- The sections themselves ----
    $sectionsHtml    = ''
    $detailRowsTotal = 0

    foreach ($pd in $perDevice) {
        $d = $pd.Device

        $crit = Get-MachineFindingBlock -Severity 'Critical' -Items $pd.Findings
        $warn = Get-MachineFindingBlock -Severity 'Warning'  -Items $pd.Findings
        $info = Get-MachineFindingBlock -Severity 'Info'     -Items $pd.Findings
        $okb  = Get-MachineFindingBlock -Severity 'OK'       -Items $pd.Findings
        $mnt  = Get-MachineMaintBlock   -Maint $pd.Maint
        $det  = New-MachineDetail       -Device $d -DrivesDoneByMaintenance $pd.DrivesDone
        $detailRowsTotal += ([regex]::Matches($det, '<tr>')).Count

        $worst = 'OK'
        foreach ($s in 'Critical','Warning','Info') {
            if (@($pd.Findings | Where-Object { $_.Severity -eq $s }).Count -gt 0) { $worst = $s; break }
        }
        $cls = $SeverityClass[$worst]

        # Problems first, then what was done, then what is healthy, then the
        # measured values. Headings only appear when there is something under them.
        $body = ''
        if ($crit) { $body += "    <h2>Needs attention now</h2>`n$crit" }
        if ($warn) { $body += "    <h2>Worth planning for</h2>`n$warn" }
        if ($info) { $body += "    <h2>For your information</h2>`n$info" }
        if ($mnt)  { $body += "    <h2>What we did this month</h2>`n$mnt" }
        if ($okb)  { $body += "    <h2>Checked and found healthy</h2>`n$okb" }
        $body += "    <h2>Measured on this machine</h2>`n$det"

        # With more than one machine, each starts a new page when printed. A
        # single-machine report does not, so it has no half-empty first page.
        $pageClass = if ($perDevice.Count -gt 1) { ' newpage' } else { '' }

        $sectionsHtml += @"
  <section class="machine-section$pageClass">
    <div class="mach-head">
      <span class="dot $cls"></span>
      <span class="mach-name">$(ConvertTo-HtmlText $d.UserOrRole)</span>
      <span class="mach-sub">$(ConvertTo-HtmlText $d.Machine.ComputerName) &middot; $(Get-MachineSubtitle -Device $d)</span>
      <span class="machine-state $cls">$($SeverityLabel[$worst])</span>
    </div>
$body  </section>

"@
    }

    # Every row in the detail tables is something that was measured, so that
    # plus the findings is an honest count of what was checked.
    $checksRun = $detailRowsTotal + $allFindings.Count

    # The footer used to say "Nothing was changed on any machine" on every
    # report - including ones listing the files maintenance had just cleared.
    # It is only said now when it is true.
    $anyMaintRan = @($perDevice | Where-Object { @($_.Maint | Where-Object { $_.MaintenanceRan -ne $false }).Count -gt 0 }).Count -gt 0
    if ($anyMaintRan) {
        $footerNote = "The health checks are read-only. The only changes made were the maintenance listed under each machine."
    }
    else {
        $footerNote = "Nothing was changed on any machine &mdash; every check is read-only."
    }

    $phoneLine = ''
    if ($CompanyPhone) { $phoneLine = " &nbsp;&middot;&nbsp; $(ConvertTo-HtmlText $CompanyPhone)" }

    # With neither an email nor a phone number there is no contact line, rather than an empty
    # line and a stray break above the note. Unbranded runs are the normal case for this now.
    $contactLine = ''
    if ($CompanyEmail -or $CompanyPhone) { $contactLine = "$(ConvertTo-HtmlText $CompanyEmail)$phoneLine<br>" }
    # No tagline means no element at all, rather than an empty pill sitting under the company
    # name. The platform has no tenant field for one, so this is the normal case there.
    $tagSpan = ''
    if ($CompanyTag -and $CompanyTag.Trim()) { $tagSpan = "<span class=`"tag`">$(ConvertTo-HtmlText $CompanyTag.Trim())</span>" }

    # Same rule for the name itself, which is blank on an unbranded run: no empty bold line
    # sitting above the note.
    $companyStrong = ''
    if ($CompanyName -and $CompanyName.Trim()) { $companyStrong = "<strong>$(ConvertTo-HtmlText $CompanyName.Trim())</strong>" }

    # ---- Assemble ----
    $html = @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$(ConvertTo-HtmlText $client) - Health Report - $period</title>
<style>
$css
</style>
</head>
<body>
<div class="sheet">

  <div class="masthead">
    $logoTag
    <div class="masthead-right">
      <div class="doc">Monthly Health Report</div>
      <div class="who">$(ConvertTo-HtmlText $client)</div>
      <div class="when">$period</div>
    </div>
  </div>

  <div class="verdict">
    <p>$verdict</p>
    <div class="sub">$checksRun checks completed across $nMachines $machineWord. Everything below is in plain English &mdash; if anything is unclear, phone us and we will explain it.</div>
  </div>

  <section>
    <h2>Your machines</h2>
    <div class="roster">
$rosterHtml    </div>
  </section>

$sectionsHtml
  <footer>
    $companyStrong
    $tagSpan
    <div class="note">
      $contactLine
      This report was produced automatically from checks run on your machines.
      $footerNote
    </div>
  </footer>

</div>
</body>
</html>
"@

    $fileName = '{0}-health-report-{1}.html' -f (ConvertTo-Slug $client), (ConvertTo-Slug $period)
    $full     = Join-Path $OutputPath $fileName

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($full, $html, $utf8NoBom)

    Write-Host "  $client - $nMachines $machineWord, $nCritical urgent, $nWarning to plan  ->  $fileName" -ForegroundColor Cyan

    if ($Pdf) {
        $pdfMade = ConvertTo-ReportPdf -HtmlPath $full
        if ($pdfMade) {
            Write-Host "      PDF: $(Split-Path -Leaf $pdfMade)" -ForegroundColor Green
            $written += $pdfMade
        }
        else {
            if ($PdfOptional) {
                # Expected where something else makes the PDF - on the platform the server does,
                # because the monthly run is a scheduled task as SYSTEM and Edge will not start
                # under that account. Not a fault, so not yellow.
                Write-Host "      No PDF here - the web page version is what gets sent on." -ForegroundColor DarkGray
            }
            else {
                Write-Host "      PDF could not be created - the web page version is ready instead." -ForegroundColor Yellow
            }
            $written += $full
        }
    }
    else {
        $written += $full
    }
}

Write-Host ""
Write-Host "$($written.Count) report(s) written to $OutputPath" -ForegroundColor Green

# Record what to open, so the batch file can do it without needing a switch
if ($written.Count -gt 0) {
    $toOpen = ($written | Where-Object { $_ -like '*.pdf' } | Select-Object -First 1)
    if (-not $toOpen) { $toOpen = $written[0] }
    try {
        # NOT Set-Content -Encoding UTF8. PowerShell 5.1 writes a byte order mark,
        # and the batch file then reads it as part of the path, producing
        # "Windows cannot find '<junk>E:\Reports\...'". ASCII has no BOM, and a
        # file path needs nothing beyond it.
        [System.IO.File]::WriteAllText(
            (Join-Path $OutputPath 'last-report.txt'), $toOpen,
            (New-Object System.Text.ASCIIEncoding))
    } catch { }
}

if ($Show -and $written.Count -gt 0) {
    # Prefer the PDF - it is what gets handed to or printed for the client
    $toOpen = ($written | Where-Object { $_ -like '*.pdf' } | Select-Object -First 1)
    if (-not $toOpen) { $toOpen = $written[0] }
    try { Start-Process (Resolve-Path -LiteralPath $toOpen).ProviderPath } catch { }
}
