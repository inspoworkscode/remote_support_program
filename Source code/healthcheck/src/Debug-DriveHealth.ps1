<#
.SYNOPSIS
    Dumps everything Windows knows about drive health, so our figures can be
    compared against CrystalDiskInfo attribute by attribute.

.DESCRIPTION
    Reports facts. Changes nothing. Run this on a machine where our health
    percentage disagrees with CrystalDiskInfo, open CrystalDiskInfo beside it,
    and compare.

    The important column is THRESHOLD. A normalised SMART value means nothing on
    its own - only relative to its threshold. Value 9 against a threshold of 0
    is a healthy drive. Value 9 against a threshold of 10 is a failing one.
    Get-ClientHealthCheck.ps1 has never read thresholds, which is the leading
    suspect for the disagreement.

    Run from an ELEVATED PowerShell. Several sources return nothing without it.
#>

[CmdletBinding()]
param(
    [string]$OutputPath = "$env:USERPROFILE\Desktop"
)

$ToolkitVersion = '2.11'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

$out = New-Object System.Collections.ArrayList
function Say {
    param([string]$Text, [string]$Colour = 'Gray')
    Write-Host $Text -ForegroundColor $Colour
    [void]$out.Add($Text)
}

Say ""
Say "  =====================================================" 'Cyan'
Say "    DRIVE HEALTH DIAGNOSTIC              Toolkit: v$ToolkitVersion" 'Cyan'
Say "  =====================================================" 'Cyan'
Say ""
Say "  Machine        : $env:COMPUTERNAME"
Say "  Date           : $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Say "  Running as admin: $isAdmin"
Say ""

if (-not $isAdmin) {
    Say "  WARNING: not elevated. The SMART table and the reliability" 'Yellow'
    Say "  counters may both return nothing. Re-run as administrator." 'Yellow'
    Say ""
}

# Attribute names. Deliberately a superset - IDs above about 170 are
# vendor-specific and the same number means different things on different
# drives, so these names are a hint for reading the dump, NOT authority.
$names = @{
    1='Raw read error rate'; 3='Spin up time'; 4='Start/stop count'
    5='Reallocated sectors'; 7='Seek error rate'; 9='Power on hours'
    10='Spin retry count'; 12='Power cycle count'; 169='Remaining life'
    170='Reserved block count'; 171='Program fail count'; 172='Erase fail count'
    173='Wear levelling count'; 174='Unexpected power loss'; 175='Program fail'
    177='Wear range delta'; 179='Used reserved blocks'; 180='Unused reserved blocks'
    181='Program fail count'; 182='Erase fail count'; 183='SATA downshift errors'
    184='End to end error'; 187='Uncorrectable errors'; 188='Command timeout'
    190='Airflow temperature'; 194='Temperature'; 195='Hardware ECC recovered'
    196='Reallocation events'; 197='Pending sectors'; 198='Uncorrectable sectors'
    199='UDMA CRC errors'; 201='Soft read error rate'; 202='Media wear indicator'
    231='Life left / temperature (vendor)'; 232='Available reserved space'
    233='Media wearout indicator'; 241='Total LBAs written'; 242='Total LBAs read'
    246='Total host sector writes'
}

# ---------------------------------------------------------------------------
Say "  --- WINDOWS' OWN VIEW ---" 'Yellow'
Say ""
$physical = @()
try {
    $physical = @(Get-PhysicalDisk -ErrorAction Stop | Sort-Object DeviceId)
    foreach ($d in $physical) {
        Say ("    [{0}] {1}" -f $d.DeviceId, $d.FriendlyName)
        Say ("         BusType={0}  MediaType={1}  Size={2} GB" -f $d.BusType, $d.MediaType, [math]::Round($d.Size/1GB,1))
        Say ("         HealthStatus={0}  OperationalStatus={1}" -f $d.HealthStatus, $d.OperationalStatus)
        $rel = $null
        try { $rel = $d | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue } catch { }
        if ($rel) {
            Say ("         Wear={0}  (health would be {1}%)" -f $rel.Wear, $(if ($rel.Wear -ne $null) { 100 - [int]$rel.Wear } else { 'n/a' }))
            Say ("         PowerOnHours={0}  Temperature={1}  ReadErrors={2}  WriteErrors={3}" -f `
                 $rel.PowerOnHours, $rel.Temperature, $rel.ReadErrorsTotal, $rel.WriteErrorsTotal)
        }
        else { Say "         (no reliability counter - normal on many SATA drives, needs admin on some)" }
        Say ""
    }
}
catch { Say "    Get-PhysicalDisk failed: $($_.Exception.Message)" 'Red' }

# ---------------------------------------------------------------------------
Say "  --- SMART ATTRIBUTE TABLE (ATA/SATA only, empty on NVMe) ---" 'Yellow'
Say ""

$smart = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_ATAPISmartData -ErrorAction SilentlyContinue)
$thr   = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictThresholds -ErrorAction SilentlyContinue)
$pred  = @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictStatus -ErrorAction SilentlyContinue)

Say "    SMART data instances      : $($smart.Count)"
Say "    Threshold instances       : $($thr.Count)"
Say "    Predict-failure instances : $($pred.Count)"
Say ""

if ($smart.Count -eq 0) {
    Say "    No ATA SMART table on this machine. That is expected and normal" 'DarkGray'
    Say "    for NVMe drives - the attribute table is an ATA-era interface." 'DarkGray'
    Say "    For NVMe, the Wear figure above is the correct source." 'DarkGray'
    Say ""
}

for ($i = 0; $i -lt $smart.Count; $i++) {

    $bytes = $smart[$i].VendorSpecific
    Say "    ===== SMART instance $i ====="
    Say "    InstanceName: $($smart[$i].InstanceName)"

    # Thresholds live in a parallel structure with the same 12-byte layout,
    # where byte +1 of each entry is the threshold for that attribute ID.
    $tBytes = $null
    if ($i -lt $thr.Count) { $tBytes = $thr[$i].VendorSpecific }
    $tMap = @{}
    if ($tBytes -and $tBytes.Count -ge 30) {
        for ($a = 0; $a -lt 30; $a++) {
            $o = 2 + ($a * 12)
            if (($o + 1) -ge $tBytes.Count) { break }
            $tid = [int]$tBytes[$o]
            if ($tid -ne 0) { $tMap[$tid] = [int]$tBytes[$o + 1] }
        }
    }

    if (-not $bytes -or $bytes.Count -lt 30) { Say "    (no usable attribute data)"; continue }

    Say ""
    Say ("    {0,4} {1,-34} {2,6} {3,6} {4,6}  {5,-14} {6}" -f 'ID','Attribute','Value','Worst','Thresh','Raw','Verdict')
    Say ("    {0}" -f ('-' * 96))

    for ($a = 0; $a -lt 30; $a++) {
        $o = 2 + ($a * 12)
        if (($o + 11) -ge $bytes.Count) { break }
        $id = [int]$bytes[$o]
        if ($id -eq 0) { continue }

        $value = [int]$bytes[$o + 3]
        $worst = [int]$bytes[$o + 4]
        $raw   = 0
        for ($r = 5; $r -ge 0; $r--) { $raw = ($raw * 256) + [int]$bytes[$o + $r + 5] }

        $t = if ($tMap.ContainsKey($id)) { $tMap[$id] } else { $null }

        # This is the test CrystalDiskInfo actually applies. A value is only
        # bad relative to its threshold, never as a bare percentage.
        $verdict = 'ok'
        if ($t -ne $null -and $t -gt 0 -and $value -le $t) { $verdict = 'AT/BELOW THRESHOLD' }
        elseif ($id -in 5,197,198,199 -and $raw -gt 0)      { $verdict = 'raw count non-zero' }

        $nm = if ($names.ContainsKey($id)) { $names[$id] } else { 'vendor-specific' }
        Say ("    {0,4} {1,-34} {2,6} {3,6} {4,6}  {5,-14} {6}" -f `
             $id, $nm, $value, $worst, $(if ($t -ne $null) { $t } else { '-' }), $raw, $verdict)
    }
    Say ""

    if ($i -lt $pred.Count) { Say "    PredictFailure: $($pred[$i].PredictFailure)" }
    Say ""
}

# ---------------------------------------------------------------------------
Say "  --- WHAT OUR CURRENT LOGIC WOULD SAY ---" 'Yellow'
Say ""
Say "    It takes the LOWEST normalised value across nine attributes" 'DarkGray'
Say "    (5, 173, 177, 196, 197, 198, 202, 231, 233) and calls that the" 'DarkGray'
Say "    health percentage. It never looks at the threshold column above." 'DarkGray'
Say ""

$watch = @{5='Reallocated sectors';173='Wear levelling count';177='Wear range delta'
           196='Reallocation events';197='Pending sectors';198='Uncorrectable sectors'
           202='Media wear indicator';231='Life left';233='Media wearout indicator'}

for ($i = 0; $i -lt $smart.Count; $i++) {
    $bytes = $smart[$i].VendorSpecific
    if (-not $bytes -or $bytes.Count -lt 30) { continue }
    $low = $null; $lowName = $null
    for ($a = 0; $a -lt 30; $a++) {
        $o = 2 + ($a * 12)
        if (($o + 11) -ge $bytes.Count) { break }
        $id = [int]$bytes[$o]
        if (-not $watch.ContainsKey($id)) { continue }
        $value = [int]$bytes[$o + 3]
        if ($value -gt 0 -and $value -le 100) {
            if ($low -eq $null -or $value -lt $low) { $low = $value; $lowName = $watch[$id] }
        }
    }
    if ($low -ne $null) { Say "    Instance $i -> $low%  (from '$lowName')" }
    else                { Say "    Instance $i -> no health figure" }
}

Say ""
Say "  =====================================================" 'Cyan'
Say "    WHAT TO DO WITH THIS" 'Cyan'
Say "  =====================================================" 'Cyan'
Say ""
Say "  Open CrystalDiskInfo beside this and compare:" 'Green'
Say ""
Say "    1. Does its health percentage match any single attribute's" 'Green'
Say "       Value column above? If so, that is the attribute it trusts" 'Green'
Say "       for this drive - and the one we should be using." 'Green'
Say ""
Say "    2. Does any row say AT/BELOW THRESHOLD? If none do, the drive" 'Green'
Say "       is healthy by the test that actually matters, whatever our" 'Green'
Say "       percentage claims." 'Green'
Say ""
Say "    3. For an NVMe drive, compare CrystalDiskInfo against the Wear" 'Green'
Say "       figure in the first section. Those should agree exactly." 'Green'
Say ""

# Named per machine. These get collected from several PCs onto one stick, and a
# fixed filename means the second machine silently overwrites the first.
$save = Join-Path $OutputPath ("drive-health_{0}_{1}.txt" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyy-MM-dd'))
try {
    [System.IO.File]::WriteAllText($save, ($out -join "`r`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "  Saved to: $save" -ForegroundColor Green
}
catch { Write-Host "  Could not save: $($_.Exception.Message)" -ForegroundColor Yellow }
Write-Host ""
