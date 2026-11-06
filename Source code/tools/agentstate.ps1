<#
.SYNOPSIS
    Why is this machine's unattended access not working? Read-only.

.DESCRIPTION
    Run this ON the customer's machine when the console says a machine is Unattended but the
    person there is still asked to click Allow.

    Unattended access needs TWO things. The tray app's tick box is the permission, and it is
    stored on the server. The agent service is the capability, and it lives here. The tick box
    goes by whether the service is INSTALLED (a registry key), never by whether it is running or
    can reach the server -- so a service that is installed and dead lets the customer grant a
    permission that does nothing, the console moves the machine to its Unattended tab, and the
    session quietly falls back to the tray app, which asks for Allow.

    This reports which of those is true here. It changes nothing: no service is started or
    stopped, no setting is written, no file is modified.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\agentstate.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'

function Head($t) { Write-Host ""; Write-Host "== $t ==" -ForegroundColor Cyan }
function Good($t) { Write-Host "   $t" -ForegroundColor Green }
function Bad ($t) { Write-Host "   $t" -ForegroundColor Red }
function Note($t) { Write-Host "   $t" -ForegroundColor Gray }

Write-Host ""
Write-Host "Unattended access on $env:COMPUTERNAME" -ForegroundColor White
Write-Host ("Run {0} as {1}\{2}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $env:USERDOMAIN, $env:USERNAME) -ForegroundColor DarkGray

$findings = New-Object System.Collections.Generic.List[string]
$exe = $null

# --------------------------------------------------------------------------------- the service
Head "The agent service"

$svcKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\RemoteSupportAgent'
$keyThere = Test-Path $svcKey
$svc = Get-Service -Name RemoteSupportAgent -ErrorAction SilentlyContinue

# The registry key is what the tray app looks at, so report it in its own right: it is the thing
# that decides whether the tick box can be used, and it survives a service that never starts.
if ($keyThere) { Note "Registry key present  (this alone is what enables the tick box in the tray app)" }
else           { Note "Registry key absent" }

if (-not $svc) {
    Bad "NOT INSTALLED."
    $findings.Add("The agent service is not installed on this machine, so unattended access cannot work here at all. Reinstall the client with the current installer -- every install since 2026-09-20 installs it.")
}
else {
    $wmi     = Get-CimInstance Win32_Service -Filter "Name='RemoteSupportAgent'" -ErrorAction SilentlyContinue
    $binPath = if ($wmi) { $wmi.PathName } else { '(unknown)' }
    $account = if ($wmi) { $wmi.StartName } else { '(unknown)' }

    Note "Display name : $($svc.DisplayName)"
    Note "Start type   : $($svc.StartType)"
    Note "Runs as      : $account"
    Note "Binary       : $binPath"

    if ($svc.Status -eq 'Running') { Good "STATUS: Running" }
    else {
        Bad "STATUS: $($svc.Status)"
        $findings.Add("The agent service is installed but $($svc.Status). Nothing on this machine or in the console says so, which is why the tick box let the permission be granted anyway.")
    }

    if ($svc.StartType -ne 'Automatic') {
        $findings.Add("Start type is $($svc.StartType), not Automatic -- it will not come back after a restart.")
    }

    # An installer that moved, or a folder a later install renamed, leaves a service pointing at
    # a binary that is not there. The service then fails to start and says so only in its own
    # event log, which nobody reads.
    $exe = $binPath
    if ($exe -match '^\s*"([^"]+\.exe)"') { $exe = $Matches[1] }
    elseif ($exe -match '^\s*(\S+\.exe)') { $exe = $Matches[1] }

    if ($exe -and -not (Test-Path $exe)) {
        Bad "The binary above DOES NOT EXIST."
        $findings.Add("The service points at $exe, which is not there. It cannot start. Reinstall the client.")
    }
    elseif ($exe -and (Test-Path $exe)) {
        $f = Get-Item $exe
        Note ("Built        : {0}  ({1} KB)" -f $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm'), [math]::Round($f.Length / 1KB))
    }
}

# --------------------------------------------------------------------------------- the process
Head "Is it actually talking to the server?"

$proc = Get-Process -Name 'RemoteSupport.Agent' -ErrorAction SilentlyContinue
if (-not $proc) {
    Bad "No RemoteSupport.Agent process is running."
}
else {
    foreach ($p in $proc) { Note "Process id $($p.Id), started $($p.StartTime.ToString('yyyy-MM-dd HH:mm:ss'))" }

    # The connection is the only proof that matters. A running process that cannot reach the
    # server is indistinguishable, from the console, from one that was never installed.
    $ids  = @($proc | Select-Object -ExpandProperty Id)
    $conn = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
            Where-Object { $ids -contains $_.OwningProcess }
    if ($conn) {
        foreach ($c in $conn) { Good "CONNECTED to $($c.RemoteAddress):$($c.RemotePort)" }
    }
    else {
        Bad "The process is running but holds NO established connection."
        $findings.Add("The agent process is running but is not connected to the server. Check serverUrl below, and whether this machine can reach it.")
    }
}

# -------------------------------------------------------------------------------- the branding
Head "Which server it is trying to reach"

# Next to the service binary first, which is the answer whenever the service exists. The fallback
# SEARCHES rather than naming a folder: the install directory is branded per tenant, and a
# diagnostic that only works for one reseller is one that gets rewritten on the first call-out.
$dirs = @()
if ($exe) { $dirs += (Split-Path -Parent $exe) }
foreach ($pf in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    if (-not $pf) { continue }
    Get-ChildItem $pf -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'RemoteSupport.Client.exe') } |
        ForEach-Object { $dirs += $_.FullName }
}
$brand = $null
foreach ($d in ($dirs | Where-Object { $_ } | Select-Object -Unique)) {
    $candidate = Join-Path $d 'branding\brand.json'
    if (Test-Path $candidate) { $brand = $candidate; break }
}

if (-not $brand) {
    Bad "No branding\brand.json found next to the agent."
    # Only a finding when there IS an agent to mis-configure. On a machine with no service at all
    # this is the same fact said twice, and a second complaint makes the first look less certain.
    if ($svc) { $findings.Add("The agent has no brand.json, so it falls back to wss://support.example.com and will never connect. The install is incomplete.") }
}
else {
    Note "From $brand"
    try {
        $b = Get-Content $brand -Raw | ConvertFrom-Json
        $keyShown = if ($b.tenantKey) { $b.tenantKey.Substring(0, [Math]::Min(8, $b.tenantKey.Length)) + '...' } else { '(EMPTY)' }
        Note "serverUrl                : $($b.serverUrl)"
        Note "tenantKey                : $keyShown"
        Note "requireConsent           : $($b.requireConsent)    (attended sessions ask)"
        Note "unattendedRequireConsent : $($b.unattendedRequireConsent)    (unattended sessions ask)"

        # This is the OTHER way a customer gets an Allow prompt on a machine that is genuinely
        # unattended: the agent hosts it exactly as it should, and is told to ask anyway.
        if ($b.unattendedRequireConsent -eq $true) {
            $findings.Add("unattendedRequireConsent is TRUE, so even a correctly working unattended session asks the customer to click Allow. Turn it off in the portal under Branding.")
        }
        if (-not $b.tenantKey) {
            $findings.Add("tenantKey is empty, so the server will turn this machine away.")
        }
    }
    catch { Bad "brand.json could not be read: $($_.Exception.Message)" }
}

# -------------------------------------------------------------------------------- the identity
Head "Device identity"

# The agent runs as SYSTEM and the tray app as the person. They must agree on the device id, or
# the server sees two machines: permission granted on one, agent connected to the other, and the
# console showing exactly the fault being chased.
$machineJson = Join-Path $env:ProgramData 'RemoteSupport\device.json'
$userJson    = Join-Path $env:LOCALAPPDATA 'RemoteSupport\device.json'
$mid = $null; $uid = $null
if (Test-Path $machineJson) { try { $mid = (Get-Content $machineJson -Raw | ConvertFrom-Json).deviceId } catch {} }
if (Test-Path $userJson)    { try { $uid = (Get-Content $userJson    -Raw | ConvertFrom-Json).deviceId } catch {} }

Note "All users  : $(if ($mid) { $mid } else { '(none)' })   $machineJson"
Note "This user  : $(if ($uid) { $uid } else { '(none)' })   $userJson"

# WHO IS ASKING matters for this one check and no other. Run through tools/runscript the script
# is a child of Host.exe and therefore SYSTEM, so $env:LOCALAPPDATA is SYSTEM's profile and the
# per-user line above is about the wrong account entirely. That is the HANDOVER 39 lesson -- ask the
# question from where the person stands -- and the honest thing is to say so rather than print a
# reassuring "(none)". Everything else here is machine-wide and reads the same either way.
$asSystem = ($env:USERNAME -eq "$env:COMPUTERNAME$") -or ($env:USERNAME -eq 'SYSTEM')
if ($asSystem) {
    Note "(running as SYSTEM, so the per-user line is SYSTEM's own profile, not the person's --"
    Note " run this script signed in as them if the two ids need comparing)"
}
elseif ($mid -and $uid -and $mid -ne $uid) {
    Bad "THESE DO NOT MATCH."
    $findings.Add("The all-users and per-user device ids differ, so the agent and the tray app register as two different machines. Delete $userJson and restart the tray app.")
}
elseif ($mid) { Good "One identity, shared." }

# --------------------------------------------------------------------------- the install marker
Head "What the installer asked for"
$marker = Get-ItemProperty -Path 'HKLM:\SOFTWARE\TheRemoteSupportProgram' -ErrorAction SilentlyContinue
if ($marker -and $marker.UnattendedRequested -eq 1) { Note "Installed with -Unattended (the box was to be ticked automatically)." }
else { Note "Installed normally. The person here ticks the box themselves -- which is the case being tested." }

# ------------------------------------------------------------------------------------- the log
Head "The agent's own log (last 20 entries)"
try {
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteSupportAgent' } -MaxEvents 20 -ErrorAction Stop
    foreach ($e in $ev) {
        $colour = switch ($e.LevelDisplayName) { 'Error' { 'Red' } 'Warning' { 'Yellow' } default { 'Gray' } }
        $first = ($e.Message -split "`r?`n")[0]
        Write-Host ("   {0}  {1,-11} {2}" -f $e.TimeCreated.ToString('MM-dd HH:mm:ss'), $e.LevelDisplayName, $first) -ForegroundColor $colour
    }
}
catch {
    Note "Nothing in the event log under RemoteSupportAgent."
    Note "That is not proof it is healthy: the agent records 'connected' at Information level, and"
    Note "an event log provider left unconfigured may be dropping anything below Warning."
}

# --------------------------------------------------------------------------------------- verdict
Write-Host ""
Write-Host "== What this means ==" -ForegroundColor Cyan
if ($findings.Count -eq 0) {
    Good "Nothing wrong found here. The agent is installed, running and connected."
    Note "If the customer is still asked to click Allow, the prompt is coming from the server's"
    Note "choice of host rather than from this machine. Run tools/agentcheck against the hub."
}
else {
    $i = 1
    foreach ($f in $findings) { Bad "$i. $f"; $i++ }
}
Write-Host ""
