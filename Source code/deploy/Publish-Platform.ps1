# =====================================================================================
# Build the platform and put it live on the server, in one command, from this PC.
#
#   .\deploy\Publish-Platform.ps1 -Server 1.2.3.4
#
# First run does the whole go-live (Caddy, TLS, service, firewall). Every run after that is
# just an update: the database, logos and owner key on the server are left alone.
#
#  -Server   the server's IP or hostname
#  -User     SSH user with sudo (default ubuntu - xneelo Cloud's Ubuntu images; root has no login there)
#  -Domain   default theremotesupportprogram.com
#  -Email    address the certificate is registered to
#  -SkipBuild  upload what is already in dist\platform-linux
#  -SkipUpload install the package already uploaded to the server (after an install that failed)
#  -LandingOnly  only the holding page (landing\index.html) - nothing else is built, shipped or restarted
#  -RealSite   show the platform's own home, features and pricing pages to the public. Without it
#              the front of the site is the holding page (landing\index.html), and only those
#              pages are hidden: sign-in, the portal, downloads, help links and the apps all work.
#              -RealSite also OPENS SIGNUPS and puts the chat bubble on the site (2026-10-27); without it both
#              are closed again. Every deploy after go-live needs -RealSite, or the holding page comes back.
#  -SiteChat   whose website chat answers on our site with -RealSite (default anywhereit). It says The Remote
#              Support Program in our colour; that company's people answer it. "" for no bubble.
#  -Unsigned   let unsigned apps go live. Since the certificate arrived (2026-10-15) a deploy REFUSES
#              to ship anything unsigned: plug the YubiKey in. This is the deliberate way past that.
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$User = "ubuntu",
    [string]$Domain = "theremotesupportprogram.com",
    [string]$Email = "clint@anywhereit.co.za",
    [switch]$SkipBuild,
    [switch]$SkipUpload,
    [switch]$RealSite,
    [string]$SiteChat = "anywhereit",
    [switch]$LandingOnly,
    [switch]$Unsigned
)
if ($SkipUpload) { $SkipBuild = $true }
if ($SiteChat -notmatch '^[a-z0-9-]{0,40}$') { throw "-SiteChat must be a company's short code (a-z, 0-9, -), such as anywhereit." }

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$payload = Join-Path $root "dist\platform-linux"
$target = "$User@$Server"

foreach ($exe in @("ssh", "scp")) {
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) {
        throw "$exe is not on PATH. Install the Windows OpenSSH Client (Settings > Optional features)."
    }
}

# -LandingOnly (2026-10-06): the holding page alone — for PayFast's terms, refunds and privacy —
# without shipping the platform. The page is rendered exactly as a full deploy renders it and
# copied over the one file go-live.sh put at /var/www/landing; Caddy serves it at once, nothing
# restarts. NEVER use Publish-Landing.ps1 -Server against the live server instead: it rewrites
# Caddy's whole configuration for a landing-only site, which would cut the platform off.
if ($LandingOnly) {
    Write-Host "== Holding page only ==" -ForegroundColor Cyan
    powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Publish-Landing.ps1") -ForUpload -Domain $Domain
    if ($LASTEXITCODE -ne 0) { throw "the holding page did not render" }
    Write-Host "== Uploading the page ==  (type your key's passphrase now)" -ForegroundColor Cyan
    scp (Join-Path $root "landing\upload\index.html") "${target}:/tmp/rsp-landing.html"
    if ($LASTEXITCODE -ne 0) { throw "upload failed (could not reach $target, or the passphrase was refused)" }
    Write-Host "== Putting it in place ==  (type your key's passphrase again now)" -ForegroundColor Cyan
    ssh $target "test -d /var/www/landing || { echo 'No /var/www/landing - this server has not had go-live.sh.'; exit 1; }; sudo install -m 644 /tmp/rsp-landing.html /var/www/landing/index.html && rm -f /tmp/rsp-landing.html"
    if ($LASTEXITCODE -ne 0) { throw "could not put the page in place - see the output above" }
    Write-Host "`nDone. https://$Domain" -ForegroundColor Green
    exit 0
}

if (-not $SkipBuild) {
    Write-Host "== Building ==" -ForegroundColor Cyan
    # -PublicUrl: the downloadable .exe falls back on the LIVE address, not the office LAN's.
    # -RequireSigning: the build stops BEFORE cleaning dist if the YubiKey is not there.
    $buildArgs = @('-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root "build.ps1"), '-Platform', '-PublicUrl', "https://$Domain")
    if (-not $Unsigned) { $buildArgs += '-RequireSigning' }
    & powershell @buildArgs
    if ($LASTEXITCODE -ne 0) {
        if ($Unsigned) { throw "build failed" }
        throw "build failed. If it says signing is not possible, plug the YubiKey in and run this again (or -Unsigned to ship unsigned on purpose)."
    }
}
if (-not $SkipUpload) {
    if (-not (Test-Path (Join-Path $payload "RemoteSupport.Platform"))) {
        throw "dist\platform-linux is missing or incomplete. Run without -SkipBuild."
    }

    # The last look before anything leaves this PC (2026-10-15): every program and script the server
    # will hand to customers and technicians must carry OUR valid signature. It also catches
    # -SkipBuild uploading an older, unsigned dist. Our files are in no Windows catalog, so
    # Get-AuthenticodeSignature reads the embedded signature here.
    if ($Unsigned) {
        Write-Warning "-Unsigned: the apps are not checked for a signature and may go live unsigned."
    } else {
        Write-Host "== Checking every app the server hands out is signed ==" -ForegroundColor Cyan
        $apps = @(Get-ChildItem (Join-Path $payload "payloads") -Recurse -File -Include *.exe, *.ps1)
        if ($apps.Count -eq 0) { throw "no apps found under dist\platform-linux\payloads." }
        $bad = @()
        foreach ($f in $apps) {
            $s = Get-AuthenticodeSignature $f.FullName
            if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notlike '*The Remote Support Program*') {
                $bad += "   $($f.FullName.Substring($payload.Length + 1)): $($s.Status) $(if ($s.SignerCertificate) { $s.SignerCertificate.Subject })"
            }
        }
        if ($bad) {
            Write-Host ($bad -join "`n") -ForegroundColor Red
            throw "$($bad.Count) of $($apps.Count) apps are not signed by us - nothing was uploaded. Plug the YubiKey in and run without -SkipBuild."
        }
        Write-Host "   all $($apps.Count) signed by The Remote Support Program" -ForegroundColor Green
    }

    # The holding page rides in the package as holding\index.html; go-live.sh puts it in front of
    # the platform when it is there and takes it away when it is not (2026-10-03: going live on
    # 2026-10-01 had silently replaced it with the unfinished public pages).
    $holding = Join-Path $payload "holding"
    if (Test-Path $holding) { Remove-Item $holding -Recurse -Force }
    if ($RealSite) {
        Write-Host "== The platform's own public pages will be shown (-RealSite) ==" -ForegroundColor Yellow
        Write-Host "   Signups will be OPEN. Chat bubble: $(if ($SiteChat) { "answered by $SiteChat" } else { 'none' })" -ForegroundColor Yellow
    } else {
        Write-Host "== Holding page ==" -ForegroundColor Cyan
        powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Publish-Landing.ps1") -ForUpload -Domain $Domain
        if ($LASTEXITCODE -ne 0) { throw "the holding page did not render" }
        New-Item -ItemType Directory -Path $holding -Force | Out-Null
        Copy-Item (Join-Path $root "landing\upload\index.html") $holding -Force
    }

    # One compressed file over the wire beats several thousand small ones over scp.
    $tar = Join-Path $env:TEMP "rsp-platform.tar.gz"
    Write-Host "== Packing ==" -ForegroundColor Cyan
    if (Test-Path $tar) { Remove-Item $tar -Force }
    tar -czf $tar -C $payload .
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    $mb = [math]::Round((Get-Item $tar).Length / 1MB, 1)
    Write-Host "   $mb MB"
}

# Two SSH connections, not three (2026-10-01): each asks for the key's passphrase, and the server
# gives up on a login left waiting about two minutes — which is how the first live deploy failed,
# its third prompt unanswered while the upload finished. The staging folder is made by the install.
if ($SkipUpload) {
    Write-Host "== Not uploading: installing the package already on the server ==" -ForegroundColor Cyan
} else {
    Write-Host "== Uploading to $target ==  (type your key's passphrase now)" -ForegroundColor Cyan
    scp $tar "${target}:/tmp/rsp-platform.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "upload failed (could not reach $target, or the passphrase was refused)" }
}

Write-Host "== Installing ==  (type your key's passphrase again now)" -ForegroundColor Cyan
# sed strips CRLF in case the scripts picked up Windows line endings in git; bash will not run them otherwise.
$remote = @"
set -e
test -f /tmp/rsp-platform.tar.gz || { echo 'No package on the server - run without -SkipUpload.'; exit 1; }
rm -rf /tmp/rsp-stage && mkdir -p /tmp/rsp-stage
tar -xzf /tmp/rsp-platform.tar.gz -C /tmp/rsp-stage
sed -i 's/\r$//' /tmp/rsp-stage/*.sh
chmod +x /tmp/rsp-stage/*.sh
cd /tmp/rsp-stage
sudo ./go-live.sh $Domain $Email $SiteChat
rm -f /tmp/rsp-platform.tar.gz
"@
ssh $target $remote.Replace("`r`n", "`n")
if ($LASTEXITCODE -ne 0) { throw "the server-side install failed - see the output above" }

Write-Host "`nDone. https://$Domain" -ForegroundColor Green
