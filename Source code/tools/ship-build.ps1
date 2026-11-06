<#
    One command to get the current build onto the server's download and into the technician
    console, so copy and paste, files and folders, and drag and drop can actually be tested.

        powershell -ExecutionPolicy Bypass -File .\tools\ship-build.ps1

    WHY THIS EXISTS RATHER THAN "just run deploy-client-payload.ps1".

    deploy-client-payload.ps1 refreshes the installer SCRIPTS and the health check toolkit in the
    download, but it does not refresh RemoteSupport.Host.exe, .Agent.exe or .Client.exe. Those
    are copied into the payload by build.ps1. So running the deploy script on its own ships
    today's installer wrapped around whatever exes happened to be staged last time — on this tree
    that was 11 September, which is before the clipboard existed. The install would succeed, the
    Clipboard tab would sit there asking for ever, and the obvious conclusion would be that the
    clipboard code is broken.

    The demo\technician copy goes stale the same way, and that has wasted time twice already
    (CLAUDE.md). It is refreshed here too, so the console you run is the console you just built.

    What it does, in order:
      1. stops the platform on 8090 (asks first, unless -Force)
      2. publishes Host, Agent, Client and Technician
      3. copies the three client exes and branding into the server's client payload
      4. hands over to deploy-client-payload.ps1 for the installer scripts and the platform build
      5. refreshes demo\technician
      6. tells you the two things left to do by hand

    -Force skips both confirmations. -SkipTechnician leaves demo\technician alone.

    -OnlyTechnician does the OPPOSITE, and it is the one to use after a console-only change.

        powershell -ExecutionPolicy Bypass -File .\tools\ship-build.ps1 -OnlyTechnician

    Clint tests through demo\technician always, not by running the console from source, so a
    change to the console is not testable until that copy is refreshed. The full ship does that
    at step 5 — but it also stops the server, republishes Host, Agent and Client, rebuilds the
    installer payload and then tells you to reinstall the client on the customer's machine.
    None of that is true of a change that touched only RemoteSupport.Technician: the host on
    the far end is a different program and is not affected by it.

    So -OnlyTechnician publishes the console, refreshes demo\technician and the portal's
    technician.zip payload, and touches nothing else. The server keeps running throughout; the
    customer's machine needs nothing. It is about ninety seconds instead of five minutes.

    Use the full ship whenever Host, Agent, Client or Shared changed — including when you are
    not sure, because a stale Host.exe presents as the feature being broken rather than as an
    old binary, and that has cost half a day twice.
#>
param(
    [switch]$Force,
    [switch]$SkipTechnician,
    [switch]$OnlyTechnician
)
$ErrorActionPreference = 'Stop'

if ($OnlyTechnician -and $SkipTechnician) {
    throw '-OnlyTechnician and -SkipTechnician contradict each other. Pass one or neither.'
}

$root = Split-Path -Parent $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$rid = 'win-x64'
$stage = Join-Path $env:TEMP ("rs-ship-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$payloadDir = Join-Path $root 'src\RemoteSupport.Platform\bin\Release\net10.0\payloads\client'
$brand = Join-Path $root 'branding'
# Stamped like build.ps1's (2026-10-16): the moment of this ship, in UTC. Pack-ClientExe reads it back off the host.
. (Join-Path $PSScriptRoot 'BuildVersion.ps1')
$versionArgs = VersionArgs (New-BuildVersion)

function Publish($proj, $out, [string[]]$extra = @()) {
    Write-Host "   $proj" -ForegroundColor DarkGray
    & $dotnet publish (Join-Path $root $proj) -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out --nologo -v quiet @versionArgs @extra
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

Write-Host ''
# No ternary anywhere in this file: it is run with `powershell`, which is Windows PowerShell 5.1,
# and `a ? b : c` is a parse error there rather than a runtime one — the whole script fails to
# load, which looks like the script being missing.
if ($OnlyTechnician) { Write-Host '== 1. Checking the console is closed ==' -ForegroundColor Cyan }
else                 { Write-Host '== 1. Stopping the server ==' -ForegroundColor Cyan }
# The technician console holds its own exe open, so step 5 cannot overwrite demo\technician while
# it is running — and because the console CLOSES TO THE TRAY (handover section 18), the window
# being gone does not mean the process is. Checked here rather than at step 5: everything between
# is two minutes of publishing, and failing at the end wastes all of it.
if (-not $SkipTechnician) {
    $techProc = Get-Process 'RemoteSupport.Technician' -ErrorAction SilentlyContinue
    if ($techProc) {
        Write-Warning 'The technician console is running, so demo\technician cannot be refreshed at the end.'
        Write-Host ("   PID {0}, started {1}" -f $techProc[0].Id, $techProc[0].StartTime) -ForegroundColor Gray
        Write-Host ''
        Write-Host '   Closing its window is not enough - it hides to the tray. Right-click the tray' -ForegroundColor Gray
        Write-Host '   icon and choose Exit, then run this again.' -ForegroundColor White
        Write-Host ''
        Write-Host '   Or pass -SkipTechnician to ship the client payload and leave demo\technician' -ForegroundColor DarkGray
        Write-Host '   alone - but then the console you test with is the one you built last time.' -ForegroundColor DarkGray
        exit 1
    }
}

# Publishing fails while the server holds RemoteSupport.Shared.dll, roughly fifteen seconds in,
# with an error about a file copy rather than about the server. Deal with it up front.
#
# EXCEPT for -OnlyTechnician. The lock is on the copy of Shared.dll in the PLATFORM's own output
# folder, and publishing the console never writes there — it writes to a staging folder under
# TEMP. So a console-only refresh runs happily alongside a live server, and stopping the server
# would mean every console change costs a restart and a re-login for no reason.
if ($OnlyTechnician) {
    Write-Host '   Leaving the server alone - a console-only refresh does not touch its files.' -ForegroundColor Green
    $listening = @()
}
else {
$listening = @(Get-NetTCPConnection -LocalPort 8090 -State Listen -ErrorAction SilentlyContinue)
if ($listening.Count -eq 0) {
    Write-Host '   Nothing on 8090. Good.' -ForegroundColor Green
}
else {
    $proc = $null
    try { $proc = Get-Process -Id $listening[0].OwningProcess -ErrorAction Stop } catch { }
    if (-not $proc) { throw 'Port 8090 is in use but the owning process could not be read. Check it by hand.' }
    Write-Host ("   Port 8090 is held by {0} (PID {1}), started {2}" -f $proc.ProcessName, $proc.Id, $proc.StartTime) -ForegroundColor Yellow
    # Only ever stop the platform itself; guessing about anything else could be expensive.
    if ($proc.ProcessName -ne 'RemoteSupport.Platform') {
        Write-Warning 'That is not RemoteSupport.Platform, so this script will not stop it. Work out what it is first.'
        exit 1
    }
    if (-not $Force) {
        $answer = Read-Host '   Stop it and carry on? [y/N]'
        if ($answer -notmatch '^(y|yes)$') { Write-Host '   Cancelled - nothing was changed.' -ForegroundColor Gray; exit 1 }
    }
    # A server started from an ELEVATED terminal cannot be stopped from a normal one, even by the
    # same user: Windows compares integrity levels, not accounts. It fails as a bare "Access is
    # denied" from Stop-Process, which reads like a permissions bug in this script rather than
    # what it is. The giveaway, if this is ever diagnosed again, is that Get-CimInstance
    # Win32_Process returns an EMPTY ExecutablePath and CommandLine for it.
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop }
    catch {
        $elevatedServer = -not (Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.Id)" -ErrorAction SilentlyContinue).CommandLine
        Write-Host ''
        if ($elevatedServer) {
            Write-Warning 'The server is running from an ELEVATED terminal, so this window cannot stop it.'
            Write-Host '   Two ways round it, either is fine:' -ForegroundColor Gray
            Write-Host '     1. Go to the terminal the server is running in and press Ctrl+C, then run this again.' -ForegroundColor White
            Write-Host '     2. Run this script from an elevated PowerShell instead.' -ForegroundColor White
            Write-Host ''
            Write-Host '   The first is tidier. The server does not need administrator rights to run -' -ForegroundColor DarkGray
            Write-Host '   nothing it does requires them, and less privilege is the right default for' -ForegroundColor DarkGray
            Write-Host '   something that will eventually face the internet.' -ForegroundColor DarkGray
        }
        else {
            Write-Warning "The server could not be stopped: $($_.Exception.Message)"
        }
        Write-Host ''
        Write-Host '   Nothing was changed.' -ForegroundColor Gray
        exit 1
    }
    $waited = 0
    while ((@(Get-NetTCPConnection -LocalPort 8090 -State Listen -ErrorAction SilentlyContinue)).Count -gt 0 -and $waited -lt 10) {
        Start-Sleep -Milliseconds 500; $waited += 0.5
    }
    if ((@(Get-NetTCPConnection -LocalPort 8090 -State Listen -ErrorAction SilentlyContinue)).Count -gt 0) {
        throw 'Port 8090 is still held after ten seconds. Nothing else was changed.'
    }
    Write-Host "   Stopped (PID $($proc.Id))." -ForegroundColor Green
}
}

Write-Host ''
Write-Host '== 2. Publishing ==' -ForegroundColor Cyan
Write-Host '   This takes a couple of minutes; they are self-contained single-file builds.' -ForegroundColor DarkGray
$hostOut  = Join-Path $stage 'host'
$agentOut = Join-Path $stage 'agent'
$clientOut = Join-Path $stage 'client'
$techOut  = Join-Path $stage 'technician'
if (-not $OnlyTechnician) {
    Publish 'src\RemoteSupport.Host\RemoteSupport.Host.csproj'   $hostOut
    Publish 'src\RemoteSupport.Agent\RemoteSupport.Agent.csproj' $agentOut
    Publish 'src\RemoteSupport.Client\RemoteSupport.Client.csproj' $clientOut
}
if (-not $SkipTechnician) {
    # The console downloads as one .exe (2026-10-01) and sets itself up (ConsoleDownload). This is
    # the server a copy asks when the download did not record where it came from: this office's
    # platform, as Pack-ClientExe.ps1 does for the customers' .exe.
    $consoleServer = ''
    $m = [regex]::Match([System.IO.File]::ReadAllText((Join-Path $root 'src\RemoteSupport.Platform\appsettings.json')), '"PublicUrl"\s*:\s*"([^"]*)"')
    if ($m.Success) { $consoleServer = $m.Groups[1].Value }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $consoleDefaults = Join-Path $stage 'console-defaults.json'
    [System.IO.File]::WriteAllText($consoleDefaults, (@{ server = $consoleServer } | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding($false)))
    Publish 'src\RemoteSupport.Technician\RemoteSupport.Technician.csproj' $techOut @("-p:ConsoleDefaults=$consoleDefaults")
}
Write-Host '   Published.' -ForegroundColor Green

Write-Host ''
if ($OnlyTechnician) {
    Write-Host '== 3. The console download on the portal ==' -ForegroundColor Cyan
}
else {
Write-Host '== 3. Putting the new exes into the client payload ==' -ForegroundColor Cyan
if (-not (Test-Path $payloadDir)) { throw "No payload folder at $payloadDir. Has the platform been built and run from this tree before?" }
Copy-Item (Join-Path $hostOut   'RemoteSupport.Host.exe')   $payloadDir -Force
Copy-Item (Join-Path $agentOut  'RemoteSupport.Agent.exe')  $payloadDir -Force
Copy-Item (Join-Path $clientOut 'RemoteSupport.Client.exe') $payloadDir -Force
$bd = Join-Path $payloadDir 'branding'
New-Item -ItemType Directory -Path $bd -Force | Out-Null
Copy-Item (Join-Path $brand '*') $bd -Recurse -Force
foreach ($f in @('RemoteSupport.Host.exe', 'RemoteSupport.Agent.exe', 'RemoteSupport.Client.exe')) {
    $fi = Get-Item (Join-Path $payloadDir $f)
    Write-Host ("   {0,-28} {1}" -f $fi.Name, $fi.LastWriteTime) -ForegroundColor Green
}
}

# ---- and the TECHNICIAN payload, which this script never touched until 2026-09-18 -------------
# The portal offers a console download at /downloads/<slug>/technician.zip, served straight out of
# payloads\technician. Nothing refreshed it. On 2026-09-18 it was still the build of 11 September,
# a week old, while eight ships had gone past it.
#
# That is not a tidy-up. A technician running the console on any machine but this one gets it from
# that link, so every "ship and try again" left their console exactly as it was — and the symptoms
# they reported were the symptoms of a build nobody was changing any more. Half a day went into
# diagnosing faults that had already been fixed, on a binary that did not have the fixes.
#
# demo\technician (step 5) only ever helps somebody sitting at THIS laptop.
if (-not $SkipTechnician) {
    $techPayload = Join-Path $root 'src\RemoteSupport.Platform\bin\Release\net10.0\payloads\technician'
    New-Item -ItemType Directory -Path $techPayload -Force | Out-Null
    Copy-Item (Join-Path $techOut 'RemoteSupport.Technician.exe') $techPayload -Force
    $tb = Join-Path $techPayload 'branding'
    New-Item -ItemType Directory -Path $tb -Force | Out-Null
    Copy-Item (Join-Path $brand '*') $tb -Recurse -Force
    # Tells the platform this console can set itself up, so /tech/<slug> offers the .exe, not the zip.
    [System.IO.File]::WriteAllText((Join-Path $techPayload 'console-exe.ready'), "Published $(Get-Date -Format 'yyyy-MM-dd HH:mm') for $consoleServer")
    $tfi = Get-Item (Join-Path $techPayload 'RemoteSupport.Technician.exe')
    Write-Host ("   {0,-28} {1}  (the portal's console download, as <company>-console.exe)" -f $tfi.Name, $tfi.LastWriteTime) -ForegroundColor Green
}

if (-not $OnlyTechnician) {
    Write-Host ''
    Write-Host '== 4. Installer scripts and platform build ==' -ForegroundColor Cyan
    # Reuse the proven script rather than repeating what it does. The server is already stopped, so
    # its own step 1 finds nothing on 8090 and passes straight through.
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'deploy-client-payload.ps1') -Force
    if ($LASTEXITCODE -ne 0) { throw 'deploy-client-payload.ps1 failed. The exes above are staged and harmless.' }

    # The ONE downloadable .exe (2026-09-30): the customer app with the host, the agent and the
    # scripts just staged packed inside it, signed inside-first when there is a certificate. It
    # replaces RemoteSupport.Client.exe in the payload, and /get/<slug> offers it instead of the zip.
    Write-Host ''
    Write-Host '== 4b. The one downloadable .exe ==' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Pack-ClientExe.ps1') -PayloadDir $payloadDir
    if ($LASTEXITCODE -ne 0) { throw 'Pack-ClientExe.ps1 failed. The zip download still works; the .exe is not offered until this succeeds.' }
}

if (-not $SkipTechnician) {
    Write-Host ''
    if ($OnlyTechnician) { Write-Host '== 4. Refreshing demo\technician ==' -ForegroundColor Cyan }
    else                 { Write-Host '== 5. Refreshing demo\technician ==' -ForegroundColor Cyan }
    # This copy goes stale and testing yesterday's console has wasted time twice (CLAUDE.md).
    $demo = Join-Path $root 'demo\technician'
    New-Item -ItemType Directory -Path $demo -Force | Out-Null
    Copy-Item (Join-Path $techOut 'RemoteSupport.Technician.exe') $demo -Force
    $db = Join-Path $demo 'branding'
    New-Item -ItemType Directory -Path $db -Force | Out-Null
    Copy-Item (Join-Path $brand '*') $db -Recurse -Force
    Write-Host ("   RemoteSupport.Technician.exe {0}" -f (Get-Item (Join-Path $demo 'RemoteSupport.Technician.exe')).LastWriteTime) -ForegroundColor Green
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

if ($OnlyTechnician) {
    Write-Host ''
    Write-Host '== Done. The console is refreshed and nothing else was touched ==' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  Start it:' -ForegroundColor Gray
    Write-Host '       .\demo\technician\RemoteSupport.Technician.exe' -ForegroundColor White
    Write-Host ''
    Write-Host '  The server was left running and the customer machine needs nothing: a console-only' -ForegroundColor DarkGray
    Write-Host '  change cannot reach either of them. If Host, Agent, Client or Shared changed too,' -ForegroundColor DarkGray
    Write-Host '  this was the wrong switch - run the script again with no arguments.' -ForegroundColor DarkGray
    Write-Host ''
    exit 0
}

Write-Host ''
Write-Host '== Done. Two things left, both by hand ==' -ForegroundColor Cyan
Write-Host ''
Write-Host '  1. Start the server again, in a terminal you keep open:' -ForegroundColor Gray
Write-Host '       dotnet run -c Release --project src/RemoteSupport.Platform' -ForegroundColor White
Write-Host ''
Write-Host '  2. On DESKTOP-OPHQIDH, reinstall the client from:' -ForegroundColor Gray
Write-Host '       http://192.168.1.115:8090/get/anywhere-it' -ForegroundColor White
Write-Host '     The old Host.exe has to be replaced; that is the whole point of this.' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  AND IF YOU RUN THE CONSOLE ANYWHERE BUT THIS LAPTOP:' -ForegroundColor Yellow
Write-Host '     download it again from the portal, Downloads page, on that machine.' -ForegroundColor Gray
Write-Host '     demo\technician above is THIS laptop only. Until 2026-09-18 the portal copy' -ForegroundColor DarkGray
Write-Host '     was never refreshed by this script and sat a week out of date while eight' -ForegroundColor DarkGray
Write-Host '     ships went past it, so a console on another machine never changed at all.' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Then prove it, from this laptop, in this order:' -ForegroundColor Gray
Write-Host '       dotnet run -c Release --project tools/capturecheck/capturecheck.csproj -- DESKTOP-OPHQIDH' -ForegroundColor White
Write-Host '         RUN THIS FIRST for stage 2.1. It is the only place four questions can be' -ForegroundColor DarkGray
Write-Host '         answered: does Desktop Duplication work with the host running as SYSTEM, is' -ForegroundColor DarkGray
Write-Host '         the picture right rather than merely fast, does a first frame arrive on an' -ForegroundColor DarkGray
Write-Host '         IDLE desktop (duplication only hands over what moved, so this is where a' -ForegroundColor DarkGray
Write-Host '         black screen would appear), and if duplication is refused does the GDI' -ForegroundColor DarkGray
Write-Host '         fallback fire AND SAY SO. It writes a PNG - open it, that is the real test.' -ForegroundColor DarkGray
Write-Host '       dotnet run -c Release --project tools/capturecheck/capturecheck.csproj -- DESKTOP-OPHQIDH --uac' -ForegroundColor White
Write-Host '         The same, but it then waits 45 s for you to raise a UAC prompt on that' -ForegroundColor DarkGray
Write-Host '         machine and dismiss it. Every prompt costs an ACCESS_LOST and a rebuild;' -ForegroundColor DarkGray
Write-Host '         this proves the capture follows to the Winlogon desktop and comes back.' -ForegroundColor DarkGray
Write-Host '         Needs a hand on that machine, which is why it is a separate run.' -ForegroundColor DarkGray
Write-Host '       dotnet run -c Release --project tools/fetchfile/fetchfile.csproj -- DESKTOP-OPHQIDH --list' -ForegroundColor White
Write-Host '         RUN THIS FIRST. No path, so it asks for %DESKTOP%, which until 2026-09-15 came' -ForegroundColor DarkGray
Write-Host '         back as "Could not find a part of the path C:\Program Files\...\%DESKTOP%".' -ForegroundColor DarkGray
Write-Host '         Their desktop listing closes BUGS #6, and it is one second.' -ForegroundColor DarkGray
Write-Host '       dotnet run -c Release --project tools/reconnectcheck/reconnectcheck.csproj -- DESKTOP-OPHQIDH' -ForegroundColor White
Write-Host '         kills the viewer socket mid-session and proves the session survived and' -ForegroundColor DarkGray
Write-Host '         tiles resumed. Add --grace to also wait out the 90-second hold (~100 s).' -ForegroundColor DarkGray
Write-Host '       dotnet run -c Release --project tools/clipcheck/clipcheck.csproj -- DESKTOP-OPHQIDH' -ForegroundColor White
Write-Host '         copy and paste of TEXT, both ways, on the clipboard the user really has' -ForegroundColor DarkGray
Write-Host '       dotnet run -c Release --project tools/treecheck/treecheck.csproj -- DESKTOP-OPHQIDH' -ForegroundColor White
Write-Host '         FILES AND FOLDERS both ways, hashed by a separate process on their machine' -ForegroundColor DarkGray
Write-Host '       dotnet run -c Release --project tools/audiocheck/audiocheck.csproj -- DESKTOP-OPHQIDH --screen' -ForegroundColor White
Write-Host '         AUDIO: their machine plays a 1 kHz tone, this checks what arrives for it' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  And four things only a person can check, because a drag is a hand on a mouse:' -ForegroundColor Gray
Write-Host '       - Open the Files tab. It must list THEIR DESKTOP without you typing anything.' -ForegroundColor White
Write-Host '       - Drag a file out of that list onto your own desktop. It must open.' -ForegroundColor White
Write-Host '       - On their machine, open the tray app: its chat log must carry a line naming' -ForegroundColor White
Write-Host '         the file you took. No banner and no balloon - that is deliberate.' -ForegroundColor DarkGray
Write-Host '       - The ticket for that session must carry the same line.' -ForegroundColor White
Write-Host ''
Write-Host '  The tray line needs RemoteSupport.Client.exe replaced, not just Host.exe. A' -ForegroundColor DarkGray
Write-Host '  host-only update leaves the disclosure silent while everything else looks right,' -ForegroundColor DarkGray
Write-Host '  which is the most misleading outcome available - see handover sections 18 and 22.' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Nothing above covers the drag out itself on this laptop; that is dragoutcheck,' -ForegroundColor DarkGray
Write-Host '  which needs no machine at all:' -ForegroundColor DarkGray
Write-Host '       dotnet run -c Release --project tools/dragoutcheck/dragoutcheck.csproj' -ForegroundColor White
Write-Host ''
Write-Host '  If audiocheck says no audio arrived, the first thing to check is whether the' -ForegroundColor DarkGray
Write-Host '  session it joined was an OLD one, still hosted by the previous Host.exe. End' -ForegroundColor DarkGray
Write-Host '  any open session on that machine first, so a new host is spawned.' -ForegroundColor DarkGray
Write-Host ''
Write-Host '  Neither needs anything on the customer machine beyond the client you just' -ForegroundColor DarkGray
Write-Host '  installed. Both clean up after themselves.' -ForegroundColor DarkGray
Write-Host ''
