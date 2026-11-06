<#
    Puts the new installer download onto the live server.

    The server has to be stopped while this runs, because a build fails while it holds
    RemoteSupport.Shared.dll. This script stops it for you:

        powershell -ExecutionPolicy Bypass -File .\tools\deploy-client-payload.ps1

    It shows you exactly what it is about to stop and asks first. A platform started
    detached from a shell has no console window to press Ctrl+C in, which is the usual
    situation here, so telling you to stop it yourself is not much help.

    Add -Force to skip the confirmation, or -KeepServerRunning to make it refuse and
    leave the server alone instead.

    What it does, in order:
      1. checks nothing is listening on 8090
      2. stages the six installer files into the server's payloads\client
      3. rebuilds RemoteSupport.Platform
      4. tells you the command to start the server again

    Optionally, -ServerUrl sets Platform:PublicUrl, which is what gets baked into every
    downloaded client as its serverUrl. It is currently the LAN address and correct for
    testing here; this is how to change it if the laptop ever moves. For LAN testing:

        powershell -ExecutionPolicy Bypass -File .\tools\deploy-client-payload.ps1 -ServerUrl http://192.168.1.115:8090

    Leave it off and appsettings.json is not touched.
#>
param(
    [string]$ServerUrl,
    [switch]$Force,
    [switch]$KeepServerRunning
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$platformProj = Join-Path $root 'src\RemoteSupport.Platform\RemoteSupport.Platform.csproj'
$binDir = Join-Path $root 'src\RemoteSupport.Platform\bin\Release\net10.0'
$payloadDir = Join-Path $binDir 'payloads\client'
$deployClient = Join-Path $root 'deploy-client'

Write-Host ''
Write-Host '== 1. Making sure the server is stopped ==' -ForegroundColor Cyan

# A build while the server holds RemoteSupport.Shared.dll fails after ten retries, roughly fifteen
# seconds in, with an error about a file copy rather than about the server. So deal with it up
# front, and say plainly what is being stopped: a platform started detached from a shell has no
# console window, so "press Ctrl+C in your terminal" is advice that cannot be followed.
$listening = @(Get-NetTCPConnection -LocalPort 8090 -State Listen -ErrorAction SilentlyContinue)

if ($listening.Count -eq 0) {
    Write-Host '   Nothing on 8090. Good.' -ForegroundColor Green
}
else {
    $proc = $null
    try { $proc = Get-Process -Id $listening[0].OwningProcess -ErrorAction Stop } catch { }
    if (-not $proc) { throw "Port 8090 is in use but the owning process could not be read. Check it by hand before continuing." }

    $hasWindow = ($proc.MainWindowHandle -ne 0)
    Write-Host ''
    Write-Host '   Port 8090 is in use by:' -ForegroundColor Yellow
    Write-Host ("     {0}  (PID {1}), started {2}" -f $proc.ProcessName, $proc.Id, $proc.StartTime) -ForegroundColor Yellow
    Write-Host ("     console window: {0}" -f $(if ($hasWindow) { 'yes' } else { 'no - it was started detached' })) -ForegroundColor Yellow
    Write-Host ''

    if ($KeepServerRunning) {
        Write-Warning 'Leaving it alone because -KeepServerRunning was given. Nothing was changed.'
        exit 1
    }

    # Only ever stop the platform itself. If something else has taken 8090, stopping it is not this
    # script's business and the guess could be expensive.
    if ($proc.ProcessName -ne 'RemoteSupport.Platform') {
        Write-Warning "That is not RemoteSupport.Platform, so this script will not stop it."
        Write-Warning "Work out what it is first, then run this again."
        exit 1
    }

    if (-not $Force) {
        $answer = Read-Host '   Stop it and carry on? [y/N]'
        if ($answer -notmatch '^(y|yes)$') {
            Write-Host '   Cancelled - nothing was changed.' -ForegroundColor Gray
            exit 1
        }
    }

    Stop-Process -Id $proc.Id -Force
    # The port does not always free the instant the process dies, and building against a
    # half-released lock fails in the same confusing way. Wait for it to actually go.
    $waited = 0
    while ((@(Get-NetTCPConnection -LocalPort 8090 -State Listen -ErrorAction SilentlyContinue)).Count -gt 0 -and $waited -lt 10) {
        Start-Sleep -Milliseconds 500
        $waited += 0.5
    }
    if ((@(Get-NetTCPConnection -LocalPort 8090 -State Listen -ErrorAction SilentlyContinue)).Count -gt 0) {
        throw "Port 8090 is still held after ten seconds. Nothing else was changed."
    }
    Write-Host "   Stopped (PID $($proc.Id))." -ForegroundColor Green
}

Write-Host ''
Write-Host '== 2. Staging the installer into the client payload ==' -ForegroundColor Cyan

if (-not (Test-Path $payloadDir)) {
    throw "No payload folder at $payloadDir. Has the platform been built and run from this tree before?"
}

# The same six files build.ps1 now stages into dist\client. Named explicitly rather than copied
# with *, because deploy-client also holds its own copies of the three exes and a branding folder,
# and those would overwrite the published ones with whatever was last tested by hand.
$files = @(
    'Install-Client.ps1', 'Uninstall-Client.ps1',
    'Install-Client.bat', 'Install-Client-Unattended.bat', 'Uninstall-Client.bat',
    'INSTALL - read me.txt'
)
foreach ($f in $files) {
    $src = Join-Path $deployClient $f
    if (-not (Test-Path $src)) { throw "deploy-client\$f is missing - the download would ship without an installer." }
    Copy-Item $src $payloadDir -Force
    Write-Host "   $f" -ForegroundColor Green
}

# The health check toolkit, the same six scripts build.ps1 stages - keep the two lists the same.
# This one fell a script behind on 2026-09-28: the installer began refusing to run without
# Uninstall-HealthCheck.ps1 beside it, build.ps1 staged it, and this did not, so a full ship would
# have put an installer on every customer machine that stopped at "These files are missing".
# The toolkit's Debug-* and Test-* diagnostics are deliberately not shipped to customer machines.
$hcSrc = Join-Path $root 'healthcheck'
$hcDest = Join-Path $payloadDir 'healthcheck'
New-Item -ItemType Directory -Path $hcDest -Force | Out-Null
foreach ($f in @('Get-ClientHealthCheck.ps1', 'Invoke-ClientMaintenance.ps1',
                 'New-ClientReport.ps1', 'Invoke-MonthlyRun.ps1', 'Install-HealthCheck.ps1',
                 'Uninstall-HealthCheck.ps1')) {
    $src = Join-Path (Join-Path $hcSrc 'src') $f
    if (-not (Test-Path $src)) { throw "healthcheck\src\$f is missing - the health check would ship incomplete." }
    Copy-Item $src $hcDest -Force
    Write-Host "   healthcheck\$f" -ForegroundColor Green
}
# No toolkit logo, for the reason build.ps1 gives: it is Anywhere IT's, and this is every tenant's
# download. A copy an earlier run of this script left behind is removed.
Remove-Item (Join-Path $hcDest 'templates') -Recurse -Force -ErrorAction SilentlyContinue

if ($ServerUrl) {
    Write-Host ''
    Write-Host '== 2b. Setting Platform:PublicUrl ==' -ForegroundColor Cyan
    $settingsPath = Join-Path $root 'src\RemoteSupport.Platform\appsettings.json'
    $backup = "$settingsPath.bak-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $settingsPath $backup -Force
    # Replace the one value as TEXT rather than round-tripping through ConvertFrom-Json and
    # ConvertTo-Json. The round trip is valid JSON but reformats the whole file to PowerShell's
    # own indentation, so a one-line change arrives in git as a 34-line rewrite and the real edit
    # is invisible in review. Done once already; not again.
    [string]$text = [System.IO.File]::ReadAllText($settingsPath)
    $pattern = '("PublicUrl"\s*:\s*")([^"]*)(")'
    $m = [regex]::Match($text, $pattern)
    if (-not $m.Success) { throw "Could not find PublicUrl in appsettings.json. Nothing was changed." }
    $was = $m.Groups[2].Value
    # A MatchEvaluator, so the URL is inserted literally and nothing in it is treated as a
    # replacement token - a '$' in a value would otherwise be swallowed.
    $rx = New-Object System.Text.RegularExpressions.Regex($pattern)
    $text = $rx.Replace($text, { param($mm) $mm.Groups[1].Value + $ServerUrl + $mm.Groups[3].Value }, 1)
    # Re-read it as JSON before writing, so a bad -ServerUrl cannot leave the server unable to start.
    $null = $text | ConvertFrom-Json
    # WriteAllText with a BOM-less encoding, not Set-Content -Encoding UTF8, which writes a byte
    # order mark that a JSON parser then reads as content.
    [System.IO.File]::WriteAllText($settingsPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "   was: $was" -ForegroundColor DarkGray
    Write-Host "   now: $ServerUrl" -ForegroundColor Green
    Write-Host "   previous file kept at $(Split-Path $backup -Leaf)" -ForegroundColor DarkGray
}

Write-Host ''
Write-Host '== 3. Rebuilding the platform ==' -ForegroundColor Cyan
& $dotnet build $platformProj -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "The build failed. Nothing was started; the payload files above are staged and harmless." }
Write-Host '   Built.' -ForegroundColor Green

Write-Host ''
Write-Host '== 4. Ready ==' -ForegroundColor Cyan
Write-Host ''
Write-Host '   The server is stopped. Start it again with:' -ForegroundColor Gray
Write-Host '     dotnet run -c Release --project src/RemoteSupport.Platform' -ForegroundColor White
Write-Host ''
Write-Host '   Run that in a terminal you keep open. It stays in the foreground, so next' -ForegroundColor DarkGray
Write-Host '   time Ctrl+C really does stop it - unlike the detached one this just ended.' -ForegroundColor DarkGray
Write-Host ''
Write-Host '   Then open the download page and check it offers two routes:' -ForegroundColor Gray
Write-Host '     http://192.168.1.115:8090/get/anywhere-it' -ForegroundColor White
Write-Host ''
if (-not $ServerUrl) {
    # READ the setting rather than asserting what it is. This block used to say flatly that a
    # downloaded client "still points at wss://theremotesupportprogram.com and cannot connect",
    # which stopped being true when bug 4 was closed on 2026-09-13 — so every ship since has
    # printed a scary warning about a fault that no longer exists, right at the moment somebody
    # is deciding whether to reinstall a working machine. A diagnostic that cries wolf is worse
    # than no diagnostic.
    $current = '(could not read appsettings.json)'
    try {
        $settingsPath = Join-Path $root 'src\RemoteSupport.Platform\appsettings.json'
        $m = [regex]::Match([System.IO.File]::ReadAllText($settingsPath), '("PublicUrl"\s*:\s*")([^"]*)(")')
        if ($m.Success) { $current = $m.Groups[2].Value }
    } catch { }

    Write-Host "   Clients downloaded from that page will be built to connect to:" -ForegroundColor Gray
    if ($current -match '^https?://(192\.168\.|10\.|127\.|localhost)') {
        Write-Host "     $current" -ForegroundColor Green
        Write-Host '   That is a LAN address, which is what you want for testing here.' -ForegroundColor DarkGray
    }
    else {
        Write-Host "     $current" -ForegroundColor Yellow
        Write-Host '   That is not a LAN address. Unless it is genuinely serving, a client installed' -ForegroundColor Yellow
        Write-Host '   from that download can never connect (docs/BUGS.md bug 4). Re-run with' -ForegroundColor Yellow
        Write-Host '   -ServerUrl http://192.168.1.115:8090 to point it back at this laptop.' -ForegroundColor Yellow
    }
    Write-Host ''
}
