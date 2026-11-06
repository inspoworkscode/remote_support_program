<#
    Installs the remote support client on this machine, branded from branding\brand.json.

    Run from an ELEVATED PowerShell in the folder that contains this script:
        powershell -ExecutionPolicy Bypass -File .\Install-Client.ps1 -Unattended

    Options:
        -Unattended        also install the background agent so technicians can connect
                           without anyone at the keyboard
        -NoAutoStart       do not start the tray app for every user at logon
        -Free              the free version: the agent service only, no tray app; the downloaded
                           copy that runs this sets the unattended password afterwards

    The monthly health check is not installed here: it is switched on per customer in the portal
    and the agent service sets it up (2026-09-28).
#>
param(
    [switch]$Unattended,
    [switch]$NoAutoStart,
    # The free version (2026-10-06): the agent service only - no tray app, shortcuts or logon entry.
    # The downloaded copy that runs this switches unattended on and sets the password afterwards.
    [switch]$Free,

    # Accepted and ignored since 2026-09-28, so a command line or shortcut that still passes it
    # does not fail. The agent service now installs the health check, always keeping settings.
    [ValidateSet('Ask','Keep','Overwrite')]
    [string]$HealthCheckSettings = 'Ask',
    # Left empty so it can default to the tenant's own company name, read from branding\brand.json
    # below. A param() default cannot do that: it is evaluated before the script body runs, and
    # $PSScriptRoot is the only path available there anyway.
    [string]$InstallDir
)
$ErrorActionPreference = "Stop"
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }

function Need-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this from an elevated PowerShell (right-click PowerShell, Run as administrator)."
    }
}
Need-Admin

if (-not (Test-Path (Join-Path $here 'RemoteSupport.Client.exe'))) {
    throw "RemoteSupport.Client.exe is not next to this script. Copy the whole folder across, not just the script."
}

$brand = Get-Content (Join-Path $here 'branding\brand.json') -Raw | ConvertFrom-Json

# Everything the customer sees comes from the tenant's own branding, not from a hardcoded
# "Anywhere IT". This script used to run only from Clint's laptop, where that was the same thing;
# it is now inside the branded zip served by /get/<slug>, so a reseller's customer would otherwise
# find another IT company's name in Program Files and in their Services list.
$companyName = if ($brand.companyName) { [string]$brand.companyName } else { 'Remote Support' }
$productName =
    if ($brand.productName)      { [string]$brand.productName }
    elseif ($brand.companyName)  { "$companyName Remote Support" }
    else                         { 'Remote Support' }   # must match Uninstall-Client.ps1's fallback

# A company name is free text and lands in a file path. Strip anything Windows will not accept
# rather than failing halfway through a copy. Apostrophes are legal in a path and are kept - a
# real client is called O'Brien Plumbing.
$safeCompany = $companyName
foreach ($c in [System.IO.Path]::GetInvalidFileNameChars()) { $safeCompany = $safeCompany.Replace($c, ' ') }
$safeCompany = ($safeCompany -replace '\s+', ' ').Trim()
if (-not $safeCompany) { $safeCompany = 'Remote Support' }

if (-not $InstallDir) { $InstallDir = Join-Path (Join-Path $env:ProgramFiles $safeCompany) 'Remote Support' }

Write-Host "== Checking the support server is reachable ==" -ForegroundColor Cyan
$wsUrl = $brand.serverUrl
$httpUrl = $wsUrl -replace '^wss://', 'https://' -replace '^ws://', 'http://'
try {
    $health = Invoke-RestMethod -Uri "$httpUrl/health" -TimeoutSec 6
    Write-Host "   OK - $($health.product)" -ForegroundColor Green
} catch {
    Write-Warning "Could not reach $httpUrl/health."
    Write-Warning "The app will still install, but it cannot connect until the server is reachable."
}

Write-Host "== Installing to $InstallDir ==" -ForegroundColor Cyan
Get-Process -Name 'RemoteSupport.Client','RemoteSupport.Host','RemoteSupport.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
# The 'healthcheck' folder is copied too, since 2026-09-28. It used to be excluded, because this
# script installed the health check from where it stood; now the AGENT SERVICE installs it later,
# from Program Files, when the server says this machine should have it - so the scripts have to
# be here, inert, for it to find. See the note where the health check used to be set up, below.
# Uninstall-Client.ps1 IS copied, since 2026-10-06: a machine removed from the company's list while online
# uninstalls itself, and the agent runs this same script to do it (AgentService.UninstallSelf).
Copy-Item (Join-Path $here '*') $InstallDir -Recurse -Force -Exclude `
    'Install-Client.ps1','INSTALL - read me.txt', `
    'Install-Client.bat','Install-Client-Unattended.bat','Uninstall-Client.bat','healthcheck'
# A reinstall's download carries the company's look as it is now. A look the server sent the OLD install
# (branding\live, 2026-10-07) is older than that, so it goes; the agent is sent a newer one if there ever is one.
Remove-Item (Join-Path $InstallDir 'branding\live') -Recurse -Force -ErrorAction SilentlyContinue
# Copied on its own rather than with everything else: a reinstall over an older version must not leave a script behind that this version no longer
# ships, so the folder is replaced rather than merged.
$hcHere = Join-Path $here 'healthcheck'
$hcThere = Join-Path $InstallDir 'healthcheck'
if (Test-Path $hcHere) {
    if (Test-Path $hcThere) { Remove-Item $hcThere -Recurse -Force -ErrorAction SilentlyContinue }
    Copy-Item $hcHere $hcThere -Recurse -Force
}

# Strip the "downloaded from the internet" mark, or the customer sees a security warning at EVERY
# LOGON and the tray app does not start until somebody clicks Run.
#
# Windows attaches a Zone.Identifier stream to anything a browser downloaded and propagates it to
# files extracted from a marked zip - which is exactly how this installer arrives. The dialog says
# "The publisher could not be verified" and names our own exe, so it reads as our software being
# broken or unsafe. Found on DESKTOP-OPHQIDH on 2026-09-15: all three exes carried ZoneId=3.
#
# Code signing (roadmap stage 5) would remove the "Unknown Publisher" wording, but it is the MARK
# that raises the dialog at all - so this is the fix, and it is needed whether or not we ever sign.
Get-ChildItem $InstallDir -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
Write-Host "   Cleared the downloaded-from-internet mark." -ForegroundColor Green

$exe = Join-Path $InstallDir 'RemoteSupport.Client.exe'

# THE FREE VERSION (2026-10-06, docs/NEXT-SESSION-FREE.md): nothing of ours on this computer's screen -
# no tray app, no shortcuts, nothing at logon. Only the agent service below, which the downloaded copy
# that ran this then sets up with the password typed into it (FreeSetup). A tray app left by an earlier
# full install is stopped and taken out of logon, so it cannot offer "Request support" on a computer
# that has none.
if ($Free) {
    Get-Process -Name 'RemoteSupport.Client' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TheRemoteSupportProgram' -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AnywhereITRemoteSupport' -ErrorAction SilentlyContinue
    Write-Host "   The free version: no tray app, no shortcuts, nothing at logon." -ForegroundColor Green
}
else {
# Start menu + desktop shortcuts. The parentheses matter: '<path> + "\Programs", <path2>' would make
# PowerShell concatenate the string with the *array*, producing one mangled path.
$shortcutDirs = @(
    (Join-Path ([Environment]::GetFolderPath('CommonStartMenu')) 'Programs'),
    ([Environment]::GetFolderPath('CommonDesktopDirectory'))
)
$shortcutName = $productName
foreach ($c in [System.IO.Path]::GetInvalidFileNameChars()) { $shortcutName = $shortcutName.Replace($c, ' ') }
$shortcutName = (($shortcutName -replace '\s+', ' ').Trim()) + '.lnk'

# The shortcut's icon. A .lnk carries an IconLocation - a PATH the shell reads when it draws the
# icon - so the tray app's rendered icon cannot serve it, and with neither set Windows falls back
# to the exe's embedded icon, which is the generic .NET one. That is why the shortcut arrived
# blank on every machine.
#
# It cannot be baked into the exe either: every reseller downloads the same binaries with their
# own logo.jpg beside them, so an ApplicationIcon would put Anywhere IT's logo on a reseller's
# customers' desktops. The client renders it here instead, from this tenant's branding, using the
# same BrandIcon the tray icon uses - one renderer, so the shortcut and the tray cannot disagree.
$iconPath = Join-Path $InstallDir 'branding\app-shortcut.ico'
$iconArg = $null
try {
    $render = Start-Process -FilePath $exe -ArgumentList @('--write-icon', "`"$iconPath`"") -Wait -PassThru -WindowStyle Hidden
    if ($render.ExitCode -eq 0 -and (Test-Path $iconPath)) {
        $iconArg = "$iconPath,0"
        Write-Host "   Rendered the shortcut icon from your logo." -ForegroundColor Green
    }
    else {
        # Said out loud rather than swallowed: a missing logo and a failed render look identical
        # on the desktop afterwards, and the shortcut still works, so nobody would investigate.
        Write-Warning "Could not render the shortcut icon (exit $($render.ExitCode)). The shortcuts will use the default icon."
    }
}
catch { Write-Warning "Could not render the shortcut icon: $($_.Exception.Message)" }

$ws = New-Object -ComObject WScript.Shell
foreach ($dir in $shortcutDirs) {
    try {
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $lnk = $ws.CreateShortcut((Join-Path $dir $shortcutName))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $InstallDir
        $lnk.Description = "Request remote support from $companyName"
        if ($iconArg) { $lnk.IconLocation = $iconArg }
        $lnk.Save()
        Write-Host "   Shortcut created in $dir" -ForegroundColor Green

        # Installs before the shortcut was branded left 'Anywhere IT Support.lnk' behind. Remove it
        # here as well as in the uninstaller, or a machine that upgrades keeps two shortcuts to the
        # same app until someone uninstalls.
        $legacy = Join-Path $dir 'Anywhere IT Support.lnk'
        if ((Test-Path $legacy) -and ($shortcutName -ne 'Anywhere IT Support.lnk')) {
            Remove-Item $legacy -Force -ErrorAction SilentlyContinue
        }
    }
    catch { Write-Warning "Could not create a shortcut in ${dir}: $($_.Exception.Message)" }
}

if (-not $NoAutoStart) {
    # Fixed name on every tenant, like the service name above: the uninstaller removes exactly this
    # value, and branding it per tenant would leave a second Run entry on any machine that upgrades,
    # starting the tray app twice at logon. Nobody sees this string.
    #
    # It names the publisher, not the tenant: The Remote Support Program ships the same binaries to
    # every reseller. It read 'AnywhereITRemoteSupport' until the business was registered under its
    # own name, so that older value is removed first - a machine installed before the rename would
    # otherwise keep both and start the tray app twice at logon.
    Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'AnywhereITRemoteSupport' -ErrorAction SilentlyContinue
    New-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'TheRemoteSupportProgram' -Value "`"$exe`" --startup" -PropertyType String -Force | Out-Null
    Write-Host "   Tray app will start at logon." -ForegroundColor Green
}
}   # not the free version

# ---------------------------------------------------------------------------------------------
# The agent service, on EVERY install.
#
# It used to be installed only by Install-Client-Unattended.bat, which made "unattended" a
# decision taken once, at install time, by whoever was holding the mouse — and impossible to
# change afterwards without another visit and another administrator password.
#
# Since 2026-09-20 the person at the machine decides, with a checkbox in the tray app, so the
# service has to exist on every machine for that checkbox to have anything to switch on. It is
# installed and running here, and it does NOTHING until permission is granted: the permission
# lives on the server, and the hub refuses to start an unattended session to a machine that has
# not allowed it, whatever this service is willing to do. Installing it is not the same as
# turning it on, and that separation is the entire design — see HANDOVER §31.
#
# -Unattended now means "and tick the box for them", not "and install the service".
# ---------------------------------------------------------------------------------------------
if ($true) {
    Write-Host "== Installing the agent service ==" -ForegroundColor Cyan
    $agent = Join-Path $InstallDir 'RemoteSupport.Agent.exe'
    sc.exe stop RemoteSupportAgent 2>$null | Out-Null
    sc.exe delete RemoteSupportAgent 2>$null | Out-Null
    Start-Sleep -Milliseconds 1000
    # The service NAME stays RemoteSupportAgent on every tenant: it is the handle the uninstaller,
    # the agent itself and every support note use, and renaming it per tenant would strand the
    # service on any machine that upgrades. Only the display name and description, which are what
    # a customer actually reads in services.msc, are branded.
    sc.exe create RemoteSupportAgent binPath= "`"$agent`"" start= auto DisplayName= "$productName Agent" | Out-Null
    sc.exe description RemoteSupportAgent "Allows $companyName technicians to provide unattended remote support." | Out-Null
    sc.exe failure RemoteSupportAgent reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Null
    sc.exe start RemoteSupportAgent | Out-Null

    # VERIFY, rather than announce.
    #
    # This line used to print "Agent service installed and running." unconditionally, with every
    # sc.exe call piped to Out-Null — and sc.exe reports its failures on stdout, so a create or a
    # start that failed was swallowed and then contradicted in green. That is the front end of
    # Clint's 2026-09-21 report: the service registry key is written by `sc create` whether or not
    # the thing ever runs, the tray app enables its tick box on that key alone, the customer ticks
    # it, and the console moves the machine to its Unattended tab. Four things agree and the agent
    # is dead. Nothing in that chain ever asked whether the service actually started.
    $svc = Get-Service -Name RemoteSupportAgent -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Host "   THE AGENT SERVICE COULD NOT BE CREATED." -ForegroundColor Red
        Write-Host "   Unattended access will not work on this machine. Attended support still will." -ForegroundColor Yellow
    }
    elseif ($svc.Status -ne 'Running') {
        # Give it a moment: sc start returns as soon as the service control manager accepts it.
        for ($i = 0; $i -lt 10 -and $svc.Status -ne 'Running'; $i++) {
            Start-Sleep -Milliseconds 500
            $svc.Refresh()
        }
        if ($svc.Status -eq 'Running') {
            Write-Host "   Agent service installed and running." -ForegroundColor Green
        }
        else {
            Write-Host "   The agent service was created but is $($svc.Status)." -ForegroundColor Red
            Write-Host "   Unattended access will not work until it starts. Check the Application event" -ForegroundColor Yellow
            Write-Host "   log under RemoteSupportAgent, or run tools\agentstate.ps1." -ForegroundColor Yellow
        }
    }
    else {
        Write-Host "   Agent service installed and running." -ForegroundColor Green
    }
    # The free version has nothing else: without the agent this computer cannot be reached at all, so a
    # dead one is a failed install - said, and the downloaded copy does not go on to set the password.
    if ($Free -and ((Get-Service -Name RemoteSupportAgent -ErrorAction SilentlyContinue).Status -ne 'Running')) {
        throw "The agent service is not running, so this computer cannot be reached. Restart the computer and run the installer again."
    }

    # Whether unattended access is ALLOWED is a separate question, and the answer is no unless
    # this install was asked for with -Unattended.
    #
    # The permission itself lives on the server, which this script cannot reach and should not
    # try to — it runs as an administrator on a machine that may not even be online yet. So it
    # leaves a request in HKLM, and the tray app turns it into one SetUnattendedMsg the first
    # time it connects. HKLM because a standard user must not be able to forge it, and because
    # it survives the tray app being reinstalled.
    $markerKey = 'HKLM:\SOFTWARE\TheRemoteSupportProgram'
    if ($Free) {
        # Switched on by the downloaded copy that ran this, with the password typed into it, once the
        # agent has said hello - not by a marker for a tray app that the free version does not have.
        Remove-ItemProperty -Path $markerKey -Name 'UnattendedRequested' -ErrorAction SilentlyContinue
    }
    elseif ($Unattended) {
        if (-not (Test-Path $markerKey)) { New-Item -Path $markerKey -Force | Out-Null }
        New-ItemProperty -Path $markerKey -Name 'UnattendedRequested' -Value 1 -PropertyType DWord -Force | Out-Null
        Write-Host "   Unattended access will be switched on when this machine first connects." -ForegroundColor Green
    }
    else {
        # An attended reinstall of a machine that was once installed unattended must not silently
        # re-enable it, and must not silently disable it either: clearing the REQUEST leaves the
        # server's stored answer alone, which is the only copy that decides anything.
        Remove-ItemProperty -Path $markerKey -Name 'UnattendedRequested' -ErrorAction SilentlyContinue
        Write-Host "   Unattended access is off. The person at this machine can switch it on in the support app." -ForegroundColor Gray
    }

    # Ctrl+Alt+Del needs one machine policy, and without it nothing anywhere reports a problem.
    #
    # SendSAS is how the secure attention sequence is generated in software - it cannot be
    # synthesised with SendInput, because Windows reserves it. But SendSAS is gated on
    # SoftwareSASGeneration, which DEFAULTS TO 0 = "none": disabled for everybody, services
    # included. The call then returns successfully and does nothing at all. Measured on
    # DESKTOP-OPHQIDH on 2026-09-15 from both the host and the agent.
    #
    # 1 = services only, which is the narrowest value that works. It does NOT let ordinary
    # applications generate it; only a service running as LocalSystem, which is this agent.
    # Set only when it is absent or 0, so a machine where an administrator has chosen 2 or 3
    # for their own reasons is left alone.
    $sasKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    try {
        $current = (Get-ItemProperty -Path $sasKey -Name 'SoftwareSASGeneration' -ErrorAction SilentlyContinue).SoftwareSASGeneration
        if ($null -eq $current -or $current -eq 0) {
            if (-not (Test-Path $sasKey)) { New-Item -Path $sasKey -Force | Out-Null }
            New-ItemProperty -Path $sasKey -Name 'SoftwareSASGeneration' -Value 1 -PropertyType DWord -Force | Out-Null
            Write-Host "   Ctrl+Alt+Del enabled for the agent service (SoftwareSASGeneration = 1)." -ForegroundColor Green
        }
        else {
            Write-Host "   SoftwareSASGeneration is already $current - left as it is." -ForegroundColor Gray
        }
    }
    catch {
        Write-Warning "Could not set SoftwareSASGeneration: $($_.Exception.Message)"
        Write-Warning "Everything else works; Ctrl+Alt+Del will not."
    }
}

# ---------------------------------------------------------------------------------------------
# Monthly maintenance and health check: NOT set up here, since 2026-09-28.
#
# Clint: not every customer should get it - SLA clients and the ones chosen. This script cannot
# know which: it runs before the machine is linked to anybody. So the default install is without
# it, and the decision lives on the server - a tick on the customer in the portal, which every
# machine of theirs follows, with a per-machine override on the console's right-click.
#
# The agent service installed above is told that decision when it connects and whenever it
# changes, and runs healthcheck\Install-HealthCheck.ps1 or Uninstall-HealthCheck.ps1 from Program
# Files to match. A health check already on this machine is left for it to judge: kept if this
# machine's customer has it, removed if not - and never touched if this app did not install it.
# ---------------------------------------------------------------------------------------------
Write-Host "   The monthly health check is switched on per customer in the portal; this machine" -ForegroundColor Gray
Write-Host "   picks it up by itself if its customer has it." -ForegroundColor Gray

if ($Free) {
    Write-Host "`nInstalled: the agent service is running. The password is set next." -ForegroundColor Green
    exit 0
}

Write-Host "== Starting the tray app ==" -ForegroundColor Cyan
# This script runs elevated, so a plain Start-Process would hand the tray app the elevated
# token. That breaks remote control: RemoteSupport.Host.exe runs at MEDIUM integrity, and UIPI
# silently discards SendInput aimed at a HIGHER-integrity window - so as soon as the customer
# uses the tray app (exactly what they do to request support) the technician's mouse and
# keyboard stop working, with no error anywhere. Launch it as the interactive user at normal
# integrity instead. At logon the HKLM Run key is processed by Explorer, which is already
# medium, so only this post-install launch needs the care.
$started = $false
try {
    $explorer = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | Select-Object -First 1
    if ($explorer) {
        $o = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner
        $interactiveUser = "$($o.Domain)\$($o.User)"
        $taskName = 'TheRemoteSupportProgram-StartClient'
        $action    = New-ScheduledTaskAction -Execute $exe -Argument '--startup' -WorkingDirectory $InstallDir
        $principal = New-ScheduledTaskPrincipal -UserId $interactiveUser -LogonType Interactive -RunLevel Limited
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName
        Start-Sleep -Seconds 3
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        $started = $true
        Write-Host "   Started as $interactiveUser at normal integrity." -ForegroundColor Green
    }
} catch {
    Write-Warning "Could not start the tray app de-elevated: $($_.Exception.Message)"
}
if (-not $started) {
    # Explorer launches its children at the shell's (medium) integrity. It cannot forward
    # arguments, so the window appears instead of starting hidden in the tray - cosmetic, and
    # far better than an elevated tray app that quietly blocks remote control.
    Write-Warning "Falling back to launching via Explorer; the window may appear instead of starting in the tray."
    Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList "`"$exe`""
    Start-Sleep -Seconds 3
}
if (Get-Process -Name 'RemoteSupport.Client' -ErrorAction SilentlyContinue) {
    Write-Host "`nInstalled. The machine should appear in the technician console within a few seconds." -ForegroundColor Green
} else {
    Write-Warning "The app did not stay running. If Windows blocked it, see the Smart App Control note in the readme."
}
