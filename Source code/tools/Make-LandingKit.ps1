# =====================================================================================
# Builds the hand-out folder for the technician putting the landing page live.
#
#   .\tools\Make-LandingKit.ps1
#
# Produces dist\Landing-Page-Kit\ — copy that onto a memory stick and hand it over.
# Everything in it is a copy, so the kit can be rebuilt at any time and nothing in it
# needs editing by hand.
#
#  -Phone / -Email   what the page will show; these are baked into the README so the
#                    technician can check the published page against them. The page
#                    itself is still filled in at publish time by Publish-Landing.ps1.
#  -SkipPdf          do not re-render the PDF (use the one already in docs\)
# =====================================================================================
param(
    [string]$Phone = "078 131 5618",
    [string]$Email = "queries@theremotesupportprogram.com",
    [string]$Domain = "theremotesupportprogram.com",
    [switch]$SkipPdf
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$kit  = Join-Path $root "dist\Landing-Page-Kit"
$pdf  = Join-Path $root "docs\Landing-Page-Runbook.pdf"
$html = Join-Path $root "docs\landing-runbook.html"

# ---- 1. the PDF -----------------------------------------------------------------
# Rendered with Edge, which every Windows machine has. It must run as a logged-in user:
# Edge refuses to start as SYSTEM and exits 1002 with no output. See pitch\README.md.
if (-not $SkipPdf) {
    $edge = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $edge) { throw "Microsoft Edge not found - pass -SkipPdf to use the existing PDF." }

    Write-Host "== Rendering the PDF ==" -ForegroundColor Cyan
    # Delete first, so a failed render cannot leave yesterday's PDF looking like today's.
    if (Test-Path $pdf) { Remove-Item $pdf -Force }

    $uri = ([System.Uri](Resolve-Path $html).Path).AbsoluteUri
    & $edge --headless=new --disable-gpu --no-pdf-header-footer --virtual-time-budget=15000 `
        "--print-to-pdf=$pdf" $uri

    # Edge can return before the file is on disk - measured: the same command that works when
    # an Edge process is already running fails the existence check on a cold start. So wait for
    # the file to appear AND stop growing rather than trusting the exit.
    $size = -1
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not (Test-Path $pdf)) { continue }
        $now = (Get-Item $pdf).Length
        if ($now -gt 0 -and $now -eq $size) { break }
        $size = $now
    }
    if (-not (Test-Path $pdf) -or (Get-Item $pdf).Length -eq 0) { throw "the PDF did not render" }
    Write-Host ("   {0:N0} KB" -f ((Get-Item $pdf).Length / 1KB))
}
if (-not (Test-Path $pdf)) { throw "docs\Landing-Page-Runbook.pdf is missing and -SkipPdf was given." }

# ---- 2. the folder --------------------------------------------------------------
Write-Host "== Assembling the kit ==" -ForegroundColor Cyan
if (Test-Path $kit) { Remove-Item $kit -Recurse -Force }
New-Item -ItemType Directory -Path $kit -Force | Out-Null

Copy-Item $pdf                                        (Join-Path $kit "Landing-Page-Runbook.pdf") -Force
Copy-Item (Join-Path $root "landing\index.html")       (Join-Path $kit "index.html")               -Force
Copy-Item (Join-Path $root "deploy\Publish-Landing.ps1") (Join-Path $kit "Publish-Landing.ps1")    -Force
Copy-Item (Join-Path $root "deploy\go-live-landing.sh")  (Join-Path $kit "go-live-landing.sh")     -Force

# The shell script runs on Linux. If git handed us a CRLF copy on this Windows checkout,
# bash refuses it with a confusing error, so normalise it on the way into the kit.
$sh = Join-Path $kit "go-live-landing.sh"
$txt = [System.IO.File]::ReadAllText($sh) -replace "`r`n", "`n"
[System.IO.File]::WriteAllText($sh, $txt, (New-Object System.Text.UTF8Encoding $false))

# ---- 3. the one-screen version --------------------------------------------------
$readme = @"
PUT THE LANDING PAGE LIVE - theremotesupportprogram.com
=======================================================
The full instructions are in Landing-Page-Runbook.pdf. This is the short version.

Copy this whole folder onto the PC you are working from. Do not work off the stick.

1. CREATE THE SERVER at the hosting provider Clint gave you.
     - Ubuntu 24.04 LTS, 2 vCPU / 4 GB
     - add YOUR SSH public key so you can log in as root without a password
     - leave ports 22, 80 and 443 open
   Note the public IP. Check it works:   ssh root@<THE-IP>

2. POINT THE DOMAIN at it, in Afrihost ClientZone -> the domain -> DNS editor.
   Add or change exactly two records:
     A    @      <THE-IP>
     A    www    <THE-IP>
   DO NOT touch the MX records (that is the company's email) or the nameservers.
   Wait until this answers with the IP:   nslookup $Domain

3. PUBLISH. Open PowerShell in this folder and run:
     powershell -ExecutionPolicy Bypass -File Publish-Landing.ps1 -Server <THE-IP>
   It should end with:   https://$Domain -> HTTP 200   and   Live.
   Not 200? It is almost always DNS. Give step 2 a few more minutes.

4. CHECK IT FROM A PHONE, not from the PC you worked on:
     - https://$Domain loads with a padlock, and so does www.$Domain
     - the page shows $Phone and $Email
     - tapping the number offers to call; WhatsApp opens a chat; the email button works
     - it reads properly on a phone screen

5. TELL CLINT: the server IP and provider, that both addresses load with a padlock,
   that you tested the phone / WhatsApp / email links from a real phone, and anything
   that did not pass.

DO NOT delete or rebuild this server when you are done, install anything else on it,
or change the DNS again. The full product moves onto this same machine later.

Stuck for more than a few minutes? Call Clint on $Phone instead of working around it.
"@
[System.IO.File]::WriteAllText((Join-Path $kit "README.txt"), ($readme -replace "`r?`n", "`r`n"), (New-Object System.Text.UTF8Encoding $false))

# ---- 4. prove the kit is complete ------------------------------------------------
$expected = @("Landing-Page-Runbook.pdf", "index.html", "Publish-Landing.ps1", "go-live-landing.sh", "README.txt")
$actual = Get-ChildItem $kit | Select-Object -ExpandProperty Name
$missing = $expected | Where-Object { $_ -notin $actual }
if ($missing) { throw "the kit is missing: $($missing -join ', ')" }

# The page must still have its placeholders: they are filled in at publish time, and a
# kit shipped with them already replaced would silently publish the wrong number.
$page = [System.IO.File]::ReadAllText((Join-Path $kit "index.html"), [System.Text.Encoding]::UTF8)
if ($page -notmatch '\{\{PHONE_DISPLAY\}\}') { throw "index.html in the kit has lost its placeholders." }

Write-Host "`nKit ready: $kit" -ForegroundColor Green
Get-ChildItem $kit | ForEach-Object { "   {0,-28} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB) }
Write-Host "`nCopy that folder onto the memory stick." -ForegroundColor Green
