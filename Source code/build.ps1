# Builds and stages every component into .\dist, then (optionally) compiles the installers.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
#         powershell -ExecutionPolicy Bypass -File build.ps1 -Installers
#         powershell -ExecutionPolicy Bypass -File build.ps1 -Installers -RequireSigning
#
# Code signing (stage 5) is applied automatically when a code-signing certificate for the
# publisher is installed; see docs/CODE-SIGNING.md. Until one exists the build behaves exactly
# as it always has and says plainly that it is producing unsigned files.
param(
    [switch]$Installers,
    [switch]$Linux,
    [switch]$Platform,

    # The subject of the code-signing certificate. It is the publisher - the registered business -
    # and is the same string as <Company> in Directory.Build.props. Never a tenant's name: every
    # reseller ships these identical binaries.
    [string]$CertSubject = "The Remote Support Program",

    # Always timestamp, or every signature stops verifying the day the certificate expires, on
    # machines that were installed years earlier.
    [string]$TimestampUrl = "http://timestamp.sectigo.com",

    # Fail the build rather than shipping unsigned. Use it for anything that leaves this PC.
    [switch]$RequireSigning,

    # Sign nothing even if a certificate is installed - for a quick local build.
    [switch]$SkipSigning,

    # The live server's address, for the one downloadable .exe to fall back on when a copy cannot
    # tell where it was downloaded from. deploy\Publish-Platform.ps1 passes https://<domain>.
    # Left out, Pack-ClientExe uses the PublicUrl in the platform's appsettings.json - the office
    # LAN address - which is right for testing here and WRONG for anything handed out from the
    # live server (found 2026-10-01, preparing go-live).
    [string]$PublicUrl,

    # Stamp every app with this version instead of the moment of the build (2026-10-16), e.g. to make a build N+1
    # on purpose when testing auto-update. Four numbers; the rule is in src\RemoteSupport.Shared\AppVersion.cs.
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
$rid = "win-x64"
$dist = Join-Path $root "dist"
$brand = Join-Path $root "branding"

# ---- code signing (stage 5) --------------------------------------------------------------
# Unsigned binaries are blocked outright by Smart App Control - which is on by default on a clean
# Windows 11 - and warned about by SmartScreen, and every rebuild starts again from zero
# reputation. docs/CODE-SIGNING.md has the whole story and what to buy.
#
# This is deliberately automatic and self-disabling: with no certificate installed the build does
# exactly what it always did and says plainly that the output is unsigned, so nothing here has to
# change on the day the certificate finally arrives.

# The signing functions live in tools\Signing.ps1 since 2026-09-30, shared with
# tools\Pack-ClientExe.ps1, which signs what goes INSIDE the one downloadable .exe before packing it.
. (Join-Path $root 'tools\Signing.ps1')
$sign = Initialize-Signing -CertSubject $CertSubject -TimestampUrl $TimestampUrl -RequireSigning:$RequireSigning -SkipSigning:$SkipSigning

# ---- the version (2026-10-16, auto-update stage A) -------------------------------------------
# Every project of this build carries the same one, the platform included, so the server knows the version of the
# apps it hands out by knowing its own. Before this every app said 1.0.0 and no two builds could be told apart.
. (Join-Path $root 'tools\BuildVersion.ps1')
$buildVersion = New-BuildVersion $Version
$versionArgs = VersionArgs $buildVersion
Write-Host "== Version $buildVersion ==" -ForegroundColor Cyan

Write-Host "== Cleaning dist ==" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

function Publish($proj, $out, [bool]$singleFile = $true, [string[]]$extra = @()) {
    Write-Host "== Publishing $proj ==" -ForegroundColor Cyan
    $args = @("publish", (Join-Path $root $proj), "-c", "Release", "-r", $rid, "--self-contained", "true",
        "-p:PublishSingleFile=$singleFile", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-o", $out) + $versionArgs + $extra
    & $dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

# ---- staging folders ----
$clientOut = Join-Path $dist "client"
$techOut   = Join-Path $dist "technician"
$serverOut = Join-Path $dist "server"
$hostOut   = Join-Path $dist "_host"
$agentOut  = Join-Path $dist "_agent"

Publish "src\RemoteSupport.Host\RemoteSupport.Host.csproj"           $hostOut
Publish "src\RemoteSupport.Agent\RemoteSupport.Agent.csproj"         $agentOut
Publish "src\RemoteSupport.Client\RemoteSupport.Client.csproj"       $clientOut
# The console downloads as one .exe (2026-10-01) and sets itself up (ConsoleDownload); this is the
# server a copy asks when the download did not record where it came from - the same rule as
# Pack-ClientExe.ps1: -PublicUrl, else the platform's appsettings.json (the office LAN).
$consoleServer = $PublicUrl
if (-not $consoleServer) {
    $m = [regex]::Match([System.IO.File]::ReadAllText((Join-Path $root 'src\RemoteSupport.Platform\appsettings.json')), '"PublicUrl"\s*:\s*"([^"]*)"')
    if ($m.Success) { $consoleServer = $m.Groups[1].Value }
}
$consoleDefaults = Join-Path $dist 'console-defaults.json'
[System.IO.File]::WriteAllText($consoleDefaults, (@{ server = $consoleServer } | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
Publish "src\RemoteSupport.Technician\RemoteSupport.Technician.csproj" $techOut $true @("-p:ConsoleDefaults=$consoleDefaults")
Remove-Item $consoleDefaults -Force
Publish "src\RemoteSupport.Server\RemoteSupport.Server.csproj"       $serverOut $false

function CopyBrand($dest) {
    $bd = Join-Path $dest "branding"
    New-Item -ItemType Directory -Path $bd -Force | Out-Null
    Copy-Item (Join-Path $brand "*") $bd -Recurse -Force
}

# Client payload = Client + Host + Agent (unattended) + branding + the installer scripts
Copy-Item (Join-Path $hostOut  "RemoteSupport.Host.exe")  $clientOut -Force
Copy-Item (Join-Path $agentOut "RemoteSupport.Agent.exe") $clientOut -Force
CopyBrand $clientOut

# The installer scripts go in the client payload so the branded zip served by /get/<slug> can
# install properly - Program Files, the unattended agent, startup registration and (from the
# health check work) a place for the monthly scheduled task to live. Until this was added the
# download contained only the three exes, so the only thing a customer could do was run the tray
# app portable, and there was nowhere to install anything.
#
# PayloadService zips this folder recursively and swaps branding/ per tenant, so it needs no
# change to pick these up. Named explicitly rather than copied with *, because deploy-client also
# holds its own copies of the three exes and a branding folder, and those would overwrite the
# freshly published ones with whatever was last tested by hand.
$deployClient = Join-Path $root "deploy-client"
foreach ($f in @('Install-Client.ps1', 'Uninstall-Client.ps1',
                 'Install-Client.bat', 'Install-Client-Unattended.bat', 'Uninstall-Client.bat',
                 'INSTALL - read me.txt')) {
    $srcFile = Join-Path $deployClient $f
    if (-not (Test-Path $srcFile)) { throw "deploy-client\$f is missing - the download would ship without an installer." }
    Copy-Item $srcFile $clientOut -Force
}

# The health check toolkit rides along in the client payload and lands beside the app in Program
# Files, where it does nothing. Since 2026-09-28 the client installer does NOT set it up: the
# agent service runs Install-HealthCheck.ps1 (or Uninstall-HealthCheck.ps1) from here when the
# server says this machine's customer, or the machine itself, has it switched on.
#
# Only the scripts the monthly run actually uses. The toolkit's diagnostics (Debug-*, Test-*)
# are technician tools for the standalone product and have no business on a customer machine.
$hcSrc = Join-Path $root "healthcheck"
$hcOut = Join-Path $clientOut "healthcheck"
New-Item -ItemType Directory -Path $hcOut -Force | Out-Null
foreach ($f in @('Get-ClientHealthCheck.ps1', 'Invoke-ClientMaintenance.ps1',
                 'New-ClientReport.ps1', 'Invoke-MonthlyRun.ps1', 'Install-HealthCheck.ps1',
                 'Uninstall-HealthCheck.ps1')) {
    $srcFile = Join-Path (Join-Path $hcSrc "src") $f
    if (-not (Test-Path $srcFile)) { throw "healthcheck\src\$f is missing - the health check would ship incomplete." }
    Copy-Item $srcFile $hcOut -Force
}
# The toolkit's own logo is deliberately NOT staged. It is Anywhere IT's, and this payload is
# what every tenant downloads: a reseller who had not uploaded a logo got that file copied into
# C:\ProgramData\HealthCheck and Anywhere IT's mark on their customer's monthly report. The
# tenant's own logo travels in branding\ and Install-Client.ps1 hands it to the health check
# installer; with no logo the report prints the company's name as text instead.

# Technician payload = Technician + branding
CopyBrand $techOut

# Server payload keeps wwwroot + appsettings (already copied by publish) + branding for reference
CopyBrand $serverOut
Copy-Item (Join-Path $root "deploy\install-server-service.ps1") $serverOut -Force -ErrorAction SilentlyContinue

Remove-Item $hostOut, $agentOut -Recurse -Force

# Sign here, and not a line later. These staged copies are what every download is built from: the
# -Platform block below copies this whole folder into payloads\, and PayloadService zips it
# verbatim for each tenant, swapping nothing but branding\. Sign after that copy and the platform
# would hand every tenant unsigned binaries.
#
# The same signature serves every tenant, because every tenant gets these identical files - see
# the publisher-versus-branding note in Directory.Build.props.
$stagedSignables = @(
    (Join-Path $clientOut "RemoteSupport.Client.exe"),
    (Join-Path $clientOut "RemoteSupport.Host.exe"),
    (Join-Path $clientOut "RemoteSupport.Agent.exe"),
    (Join-Path $techOut   "RemoteSupport.Technician.exe"),
    (Join-Path $clientOut "Install-Client.ps1"),
    (Join-Path $clientOut "Uninstall-Client.ps1")
)
$stagedSignables += @(
    Get-ChildItem (Join-Path $clientOut "healthcheck") -Filter *.ps1 -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName }
)
Invoke-Signing $sign "the staged apps" $stagedSignables

# The console just published can set itself up from a download: the platform offers it as
# <company>-console.exe instead of the zip once this is beside it.
[System.IO.File]::WriteAllText((Join-Path $techOut 'console-exe.ready'), "Published $(Get-Date -Format 'yyyy-MM-dd HH:mm') for $consoleServer")

# The ONE downloadable .exe (2026-09-30), made from what was just signed: the host, the agent and
# the scripts go INSIDE the customer app, and the app is signed after - see docs/CODE-SIGNING.md,
# "One .exe for every company". It replaces client\RemoteSupport.Client.exe, so -Platform below
# carries it into payloads\client with its client-exe.packed marker.
$packArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\Pack-ClientExe.ps1'), '-PayloadDir', $clientOut, '-Version', $buildVersion)
if ($PublicUrl) { $packArgs += @('-Server', $PublicUrl) }
if ($RequireSigning) { $packArgs += '-RequireSigning' }
if ($SkipSigning) { $packArgs += '-SkipSigning' }
& powershell @packArgs
if ($LASTEXITCODE -ne 0) { throw "Packing the one downloadable .exe failed." }

# Optional Linux server payload
if ($Linux) {
    $serverLinux = Join-Path $dist "server-linux"
    Write-Host "== Publishing server for linux-x64 ==" -ForegroundColor Cyan
    & $dotnet publish (Join-Path $root "src\RemoteSupport.Server\RemoteSupport.Server.csproj") -c Release -r linux-x64 --self-contained true `
        -p:PublishSingleFile=$false -p:DebugType=none -o $serverLinux @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "linux publish failed" }
    CopyBrand $serverLinux
    Copy-Item (Join-Path $root "deploy\remotesupport-server.service") $serverLinux -Force
    Copy-Item (Join-Path $root "deploy\install-server-linux.sh") $serverLinux -Force
    Write-Host "  server-linux\ -> your Linux server (see install-server-linux.sh)"
}

# Optional multi-tenant platform (theremotesupportprogram.com) for a Linux host, with the app payloads
# it hands out to tenants baked in under payloads\.
if ($Platform) {
    $plat = Join-Path $dist "platform-linux"
    Write-Host "== Publishing platform for linux-x64 ==" -ForegroundColor Cyan
    & $dotnet publish (Join-Path $root "src\RemoteSupport.Platform\RemoteSupport.Platform.csproj") -c Release -r linux-x64 --self-contained true `
        -p:PublishSingleFile=$false -p:DebugType=none -o $plat @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "platform publish failed" }
    New-Item -ItemType Directory -Path (Join-Path $plat "payloads") -Force | Out-Null
    Copy-Item $clientOut (Join-Path $plat "payloads\client") -Recurse -Force
    Copy-Item $techOut   (Join-Path $plat "payloads\technician") -Recurse -Force
    Copy-Item (Join-Path $root "deploy\remotesupport-platform.service") $plat -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "deploy\install-platform-linux.sh") $plat -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $root "deploy\go-live.sh") $plat -Force -ErrorAction SilentlyContinue
    # The nightly backup go-live.sh installs (2026-10-09); go-live refuses to run without it, so no SilentlyContinue.
    Copy-Item (Join-Path $root "deploy\rsp-backup.sh") $plat -Force
    Write-Host "  platform-linux\ -> theremotesupportprogram.com host (see docs/PLATFORM.md)"
}

Write-Host "`nStaged payloads in $dist, version $buildVersion" -ForegroundColor Green
Write-Host "  client\      -> end-user machines (attended app + unattended agent)"
Write-Host "  technician\  -> your technicians"
Write-Host "  server\      -> your Windows server"
if ($Linux) { Write-Host "  server-linux\-> your Linux server" }

if ($Installers) {
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) { $iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" }
    if (-not (Test-Path $iscc)) { Write-Warning "Inno Setup (ISCC.exe) not found; skipping installer compile."; return }
    Write-Host "`n== Compiling installers ==" -ForegroundColor Cyan
    & $iscc "/DAppVersion=$buildVersion" (Join-Path $root "installers\client.iss")
    & $iscc "/DAppVersion=$buildVersion" (Join-Path $root "installers\technician.iss")

    # The setup .exe is the file SmartScreen judges first - it is what the customer downloads and
    # double-clicks - so it needs its own signature on top of the signed payload inside it.
    # Inno generates the uninstaller on the customer's machine at install time, so unins000.exe
    # can never carry ours; that is a known gap, not an oversight.
    Invoke-Signing $sign "the installers" @(
        (Join-Path $dist "installers\TheRemoteSupportProgram-Client-Setup.exe"),
        (Join-Path $dist "installers\TheRemoteSupportProgram-Technician-Setup.exe")
    )

    Write-Host "Installers written to $dist\installers" -ForegroundColor Green
}
