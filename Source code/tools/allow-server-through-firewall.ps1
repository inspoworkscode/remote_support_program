<#
    Lets customer machines on the LAN reach the platform on port 8090.

        powershell -ExecutionPolicy Bypass -File .\tools\allow-server-through-firewall.ps1

    Add -ReportOnly to look without changing anything.

    WHY THIS EXISTS.

    Windows Firewall rules are per network PROFILE — Domain, Private, Public — and a rule created
    for one profile does nothing on another. When Windows first prompted for
    RemoteSupport.Platform.exe, the allow rules landed on **Public** only. That is fine right up
    until the Wi-Fi is categorised **Private**, at which point every inbound connection from the
    LAN is dropped.

    The symptom is deeply misleading, and it cost time on 2026-09-13:

      * the server is running and listening on 0.0.0.0:8090
      * http://<lan-ip>:8090/health answers perfectly FROM THE SERVER ITSELF, because traffic to
        your own address never crosses the firewall — so "I checked, it's reachable" is wrong
      * only the customer machines see anything wrong, and all they can report is "it stopped
        working", with nothing in any log on either side

    A port rule is used rather than an application rule on purpose: an application rule is tied to
    the exe's PATH, and this one is rebuilt constantly and would be re-prompted for.
#>
param([switch]$ReportOnly)
$ErrorActionPreference = 'Stop'

$RuleName = 'Remote Support Platform 8090'
$Port = 8090

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    if ($ReportOnly) {
        Write-Warning 'Not elevated. The report below will be incomplete: reading port-based rules needs admin.'
    }
    else {
        Write-Host 'Re-launching elevated (firewall rules need admin)...' -ForegroundColor Yellow
        # Relaunch THIS file rather than forwarding arguments through cmd: the path has a space in
        # it and that is exactly where these break (handover section 5).
        Start-Process powershell -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', $PSCommandPath
        )
        exit 0
    }
}

Write-Host ''
Write-Host '== Where the server is listening ==' -ForegroundColor Cyan
$listening = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
if ($listening.Count -eq 0) {
    Write-Warning "Nothing is listening on $Port. Start the server before testing from a client machine."
}
else {
    foreach ($l in $listening) {
        $p = $null; try { $p = Get-Process -Id $l.OwningProcess -ErrorAction Stop } catch { }
        Write-Host ("   {0}:{1}  <- {2} (PID {3})" -f $l.LocalAddress, $l.LocalPort, $(if ($p) { $p.ProcessName } else { '?' }), $l.OwningProcess) -ForegroundColor Green
    }
}

Write-Host ''
Write-Host '== Which firewall profile is actually in force ==' -ForegroundColor Cyan
$active = @()
foreach ($n in Get-NetConnectionProfile) {
    Write-Host ("   {0,-22} {1,-16} {2}" -f $n.Name, $n.InterfaceAlias, $n.NetworkCategory) -ForegroundColor Gray
    $active += [string]$n.NetworkCategory
}
$active = $active | Sort-Object -Unique
# Windows calls the category "DomainAuthenticated" but the firewall profile is "Domain".
$activeProfiles = $active | ForEach-Object { if ($_ -eq 'DomainAuthenticated') { 'Domain' } else { $_ } }
Write-Host ("   Rules must therefore cover: {0}" -f ($activeProfiles -join ', ')) -ForegroundColor White

Write-Host ''
Write-Host "== Existing rules that could let $Port in ==" -ForegroundColor Cyan
$found = $false
try {
    Get-NetFirewallPortFilter | Where-Object { $_.LocalPort -eq $Port -or $_.LocalPort -contains $Port } | ForEach-Object {
        $r = $_ | Get-NetFirewallRule
        if ($r.Direction -eq 'Inbound') {
            $found = $true
            Write-Host ("   port rule : {0,-40} enabled={1} action={2} profile={3}" -f $r.DisplayName, $r.Enabled, $r.Action, $r.Profile) -ForegroundColor Gray
        }
    }
}
catch { Write-Warning "Could not read port rules: $($_.Exception.Message)" }
try {
    Get-NetFirewallApplicationFilter | Where-Object { $_.Program -like '*RemoteSupport.Platform*' } | ForEach-Object {
        $r = $_ | Get-NetFirewallRule
        Write-Host ("   app  rule : {0,-40} enabled={1} action={2} profile={3} dir={4}" -f $r.DisplayName, $r.Enabled, $r.Action, $r.Profile, $r.Direction) -ForegroundColor Gray
    }
}
catch { Write-Warning "Could not read application rules: $($_.Exception.Message)" }
if (-not $found) { Write-Host "   No inbound PORT rule for $Port." -ForegroundColor Yellow }

if ($ReportOnly) {
    Write-Host ''
    Write-Host 'Report only - nothing was changed.' -ForegroundColor Gray
    exit 0
}

Write-Host ''
Write-Host '== Adding the rule ==' -ForegroundColor Cyan
# Domain, Private and Public together. The laptop moves between networks and Windows re-categorises
# them on its own; a rule that covers only the profile in force today reintroduces exactly this
# fault the next time it changes. This opens ONE port that is already listening on 0.0.0.0.
$existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host '   Rule already exists - updating it to cover every profile.' -ForegroundColor Gray
    $existing | Set-NetFirewallRule -Enabled True -Profile Domain, Private, Public -Action Allow
}
else {
    New-NetFirewallRule -DisplayName $RuleName -Description 'The Remote Support Program platform: customer machines and technician consoles connect here.' `
        -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain, Private, Public | Out-Null
    Write-Host '   Created.' -ForegroundColor Green
}

Write-Host ''
Write-Host '== Verifying ==' -ForegroundColor Cyan
$r = Get-NetFirewallRule -DisplayName $RuleName
Write-Host ("   {0}: enabled={1} action={2} profile={3} direction={4}" -f $r.DisplayName, $r.Enabled, $r.Action, $r.Profile, $r.Direction) -ForegroundColor Green

Write-Host ''
Write-Host 'Done. The client machine should reconnect on its own within a few seconds -' -ForegroundColor Cyan
Write-Host 'DeviceLink retries every 3 seconds and never gives up.' -ForegroundColor Cyan
Write-Host ''
Write-Host 'If it does not, the next thing to check is the CLIENT machine, not this one:' -ForegroundColor Gray
Write-Host '  - is the tray app actually running there?' -ForegroundColor Gray
Write-Host '  - does its branding\brand.json still say ws://192.168.1.115:8090 ?' -ForegroundColor Gray
Write-Host ''
