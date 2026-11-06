# =====================================================================================
# Put the holding page live, in one command, from this PC.
#
# !! NOT FOR THE LIVE PLATFORM SERVER (2026-10-06). -Server rewrites Caddy's whole configuration
# !! for a landing-only site, which would cut the platform off, and installs Caddy from the apt
# !! repository whose signing key has expired. Since the platform went live on 2026-10-01, update
# !! the page there with:  .\deploy\Publish-Platform.ps1 -Server <ip> -LandingOnly
# !! -ForUpload and -PreviewOnly below are safe: they only write a file on this PC.
#
#   .\deploy\Publish-Landing.ps1 -Server 1.2.3.4
#
# First run installs Caddy, gets the certificate and publishes the page. Every run after
# that just replaces the page, which takes a couple of seconds.
#
# The phone number is injected into the page rather than written into landing\index.html, so
# there is exactly one place to change it - the -Phone default just below - and no half-updated
# copy of it in the markup.
#
# There is no server to SSH into while the domain sits on xneelo shared hosting, so the page
# also has to come out as a plain file to upload by hand through konsoleH:
#
#   .\deploy\Publish-Landing.ps1 -ForUpload
#
# That writes landing\upload\index.html - already named index.html, contact details already
# filled in - and asks for nothing else. Upload that file, not landing\index.html, which is
# the template and still has {{EMAIL}} and {{PHONE_DISPLAY}} in it.
#
#  -ForUpload  render the finished page to landing\upload\index.html and stop. No -Server needed.
#  -PreviewOnly  render it to landing\preview.html, to look at in a browser. No -Server needed.
#  -Server  the server's IP or hostname. Only for the SSH publish.
#  -Phone   the number shown on the page. Defaults to the Anywhere IT number; pass it only to
#           override. Any readable form works - the tel: and WhatsApp links are
#           derived from it. South African numbers starting 0 become +27 automatically.
#  -User    SSH user with sudo (default root)
#  -Domain  the site to publish (default theremotesupportprogram.com)
#  -Email   the address shown ON THE PAGE for enquiries - the shared product mailbox.
#  -CertEmail  who the TLS certificate is registered to. Deliberately a different address:
#           Let's Encrypt writes here when a renewal has been failing for weeks, and that
#           has to land in front of a person, not in a shared enquiries inbox.
# =====================================================================================
param(
    [string]$Server,
    [string]$Phone = "078 131 5618",
    [string]$User = "root",
    [string]$Domain = "theremotesupportprogram.com",
    [string]$Email = "queries@theremotesupportprogram.com",
    [string]$CertEmail = "clint@anywhereit.co.za",
    [switch]$PreviewOnly,
    [switch]$ForUpload
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# -Server used to be mandatory, which meant the two render-only modes prompted for a server
# that is never contacted - and a made-up answer looked like it had published something.
$renderOnly = $PreviewOnly -or $ForUpload
if (-not $renderOnly -and -not $Server) {
    throw "-Server is required to publish over SSH. To get a file to upload by hand instead, run: .\deploy\Publish-Landing.ps1 -ForUpload"
}
$target = "$User@$Server"

# Two places the page can be: ..\landing\index.html when this runs from the repository, or
# index.html right here when it runs from the hand-out kit on a memory stick. The same script
# has to work both ways, because the kit is a copy of this file, not a different one.
$source = Join-Path $root "landing\index.html"
if (-not (Test-Path $source)) { $source = Join-Path $PSScriptRoot "index.html" }
if (-not (Test-Path $source)) { throw "index.html not found next to this script or in ..\landing." }

if (-not $renderOnly) {
    foreach ($exe in @("ssh", "scp")) {
        if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
            throw "$exe is not on PATH. Install the Windows OpenSSH Client (Settings > Optional features)."
        }
    }
}

# ---- work out the three forms of the number ----------------------------------------
# Display: exactly what was typed. E.164 for tel: links. Digits only for wa.me, which
# rejects a leading + and any spaces.
$digits = ($Phone -replace '[^\d+]', '')
if ($digits.StartsWith("+")) {
    $e164 = $digits
} elseif ($digits.StartsWith("0")) {
    $e164 = "+27" + $digits.Substring(1)      # a local SA number
} elseif ($digits.StartsWith("27")) {
    $e164 = "+" + $digits
} else {
    throw "Cannot tell what country '$Phone' is in. Pass it in full international form, e.g. -Phone '+27 82 123 4567'."
}
$wa = $e164.TrimStart('+')
if ($wa.Length -lt 10 -or $wa.Length -gt 15) { throw "'$Phone' does not look like a complete number (got $e164)." }

if ($renderOnly) {
    Write-Host "Rendering the page for https://$Domain (nothing is uploaded)" -ForegroundColor Cyan
} else {
    Write-Host "Publishing to https://$Domain" -ForegroundColor Cyan
}
Write-Host "   email    : $Email"
Write-Host "   shown as : $Phone"
Write-Host "   tel link : $e164"
Write-Host "   WhatsApp : https://wa.me/$wa"

# PowerShell 5.1 reads files as ANSI unless told otherwise, which turns every em dash in
# the page into mojibake. Measured, not theoretical: it mangled the mailto subject line.
$html = [System.IO.File]::ReadAllText($source, [System.Text.Encoding]::UTF8)
$html = $html.Replace("{{EMAIL}}", $Email).
              Replace("{{PHONE_DISPLAY}}", $Phone).
              Replace("{{PHONE_E164}}", $e164).
              Replace("{{PHONE_WA}}", $wa)
if ($html -match '\{\{') { throw "A placeholder was left unreplaced in the page - check landing\index.html." }

if ($ForUpload) {
    # The page as a file to upload by hand - konsoleH's file manager on the xneelo shared
    # hosting, where there is no SSH to publish over. It is written into its own folder and
    # named index.html, because the name is what gets uploaded: renaming preview.html on the
    # way in is one more step to get wrong at the moment it matters.
    $dir = Join-Path (Split-Path $source -Parent) "upload"
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $out = Join-Path $dir "index.html"
    [System.IO.File]::WriteAllText($out, $html, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "`nUpload this file:" -ForegroundColor Green
    Write-Host "   $out"
    Write-Host "It replaces index.html in the web root (public_html). Nothing here uploads it."
    return
}

if ($PreviewOnly) {
    # Renders the finished page next to the source so it can be looked at in a browser
    # before it is anywhere public. Nothing is uploaded.
    $preview = Join-Path (Split-Path $source -Parent) "preview.html"
    [System.IO.File]::WriteAllText($preview, $html, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "`nPreview written to $preview" -ForegroundColor Green
    return
}

$stage = Join-Path $env:TEMP "rsp-landing"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
# UTF-8 without a BOM: a BOM shows up as stray characters at the top of the rendered page.
[System.IO.File]::WriteAllText((Join-Path $stage "index.html"), $html, (New-Object System.Text.UTF8Encoding $false))
Copy-Item (Join-Path $PSScriptRoot "go-live-landing.sh") $stage -Force

Write-Host "== Uploading ==" -ForegroundColor Cyan
ssh $target "rm -rf /tmp/rsp-landing && mkdir -p /tmp/rsp-landing"
if ($LASTEXITCODE -ne 0) { throw "could not reach $target over SSH" }
scp (Join-Path $stage "index.html") (Join-Path $stage "go-live-landing.sh") "${target}:/tmp/rsp-landing/"
if ($LASTEXITCODE -ne 0) { throw "upload failed" }

Write-Host "== Publishing ==" -ForegroundColor Cyan
$remote = @"
set -e
sed -i 's/\r$//' /tmp/rsp-landing/go-live-landing.sh
chmod +x /tmp/rsp-landing/go-live-landing.sh
cd /tmp/rsp-landing
sudo ./go-live-landing.sh $Domain $CertEmail
"@
ssh $target $remote.Replace("`r`n", "`n")
if ($LASTEXITCODE -ne 0) { throw "the server-side publish failed - see the output above" }

Write-Host "`nDone. https://$Domain" -ForegroundColor Green
