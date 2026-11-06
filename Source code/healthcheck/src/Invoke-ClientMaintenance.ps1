<#
.SYNOPSIS
    Performs safe monthly maintenance on a Windows machine and records what it did.

.DESCRIPTION
    This is the ONLY script in this toolkit that changes anything on a machine.
    The health check is read-only; this is not. They are kept separate on purpose.

    What it removes:
      - Temporary files older than a set number of days
      - The Windows temp folder
      - Recycle bin contents older than a set number of days
      - Windows Update download cache
      - Delivery Optimisation cache
      - Windows Error Reporting queue
      - Superseded Windows component files

    What it NEVER touches:
      - Documents, Desktop, Downloads, Pictures or any user data
      - Browser history, passwords, cookies or saved logins
      - Anything modified recently, in case it is in use
      - Program settings or installed software

    It writes maintenance-<machine>-<month>.json alongside the health check
    results, so the client's report can show what was actually done.

.NOTES
    Requires: PowerShell 5.1, administrator rights
    Version:  1.0

.EXAMPLE
    .\Invoke-ClientMaintenance.ps1 -WhatIf
    Shows what would be removed without removing anything.

.EXAMPLE
    .\Invoke-ClientMaintenance.ps1 -Organisation "Riverside Guest House" -UserOrRole "Reception"
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Organisation,

    [Alias('ClientName')]
    [string]$UserOrRole,

    [string]$ConfigPath,

    # Where the record of this run is written.
    [string]$OutputPath = "$env:ProgramData\HealthCheck",

    # Only remove temporary files older than this. Protects files in use.
    [int]$OlderThanDays = 7,

    # If the recycle bin is emptied at all, only items older than this go.
    [int]$RecycleBinDays = 90,

    # Component cleanup is thorough but slow. Off unless asked for.
    [switch]$DeepClean,

    # Trigger a scan with whatever antivirus is installed.
    [switch]$RunVirusScan,

    # Empty the recycle bin. OFF by default - deleted files belong to the user,
    # and someone who deleted something by mistake may still need it back.
    [switch]$EmptyRecycleBin,

    # Skip drive optimisation (TRIM on solid state, defragment on mechanical).
    [switch]$SkipOptimise,

    # Skip the Windows system file check. It is thorough but takes ~10 minutes.
    [switch]$SkipSystemCheck,

    # Only defragment a mechanical drive once fragmentation passes this level.
    [int]$DefragThreshold = 10,

    # Give up on the system file check after this long. A broken Windows
    # servicing stack can make it run for hours.
    [int]$SystemCheckTimeoutMinutes = 45,

    # Cleaning only. Skips optimisation and the system file check.
    [switch]$Quick,

    # Run without creating a restore point first. Only for machines where
    # System Restore genuinely is not available, such as Windows Server.
    [switch]$NoRestorePoint,

    # Return the full result object. For scripting only.
    [switch]$PassThru
)


# Version of this toolkit. Printed at run time so a stale copy cannot hide.
$ToolkitVersion = '2.11'

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

# ---------------------------------------------------------------------------
# Settings, shared with the health check
# ---------------------------------------------------------------------------

$Config = $null
if (-not $ConfigPath) {
    $ConfigPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'config.json'
}
if (Test-Path $ConfigPath) {
    try   { $Config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
}
if (-not $Organisation -and $Config.Organisation) { $Organisation = $Config.Organisation }
if (-not $UserOrRole   -and $Config.UserOrRole)   { $UserOrRole   = $Config.UserOrRole }
if (-not $Organisation) { $Organisation = $env:COMPUTERNAME }
if (-not $UserOrRole)   { $UserOrRole   = $env:COMPUTERNAME }

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Quick) { $SkipOptimise = $true; $SkipSystemCheck = $true }

$Tasks   = New-Object System.Collections.ArrayList
$Issues  = New-Object System.Collections.ArrayList
$Errors  = New-Object System.Collections.ArrayList
$cutoff  = (Get-Date).AddDays(-$OlderThanDays)
$started = Get-Date

function Add-Task {
    param(
        [string]$Name,
        [string]$Description,
        [double]$FreedMB = 0,
        [int]$ItemCount = 0,
        [ValidateSet('Done','Skipped','Failed')][string]$Result = 'Done',
        [string]$Note = ''
    )
    [void]$Tasks.Add([pscustomobject]@{
        Name        = $Name
        Description = $Description
        FreedMB     = [math]::Round($FreedMB, 1)
        ItemCount   = $ItemCount
        Result      = $Result
        Note        = $Note
    })
    $freed = if ($FreedMB -ge 1) { " ({0:N0} MB)" -f $FreedMB } else { '' }
    $colour = switch ($Result) { 'Done' { 'Green' } 'Skipped' { 'DarkGray' } default { 'Yellow' } }
    Write-Host ("  {0,-28} {1}{2}" -f $Name, $Result, $freed) -ForegroundColor $colour
}

function Add-Issue {
    # Anything found during maintenance that belongs in the client's report.
    param(
        [Parameter(Mandatory)][ValidateSet('Critical','Warning','Info')][string]$Severity,
        [Parameter(Mandatory)][string]$Category,
        [Parameter(Mandatory)][string]$Title,
        [string]$Detail = '',
        [string]$Recommendation = ''
    )
    [void]$Issues.Add([pscustomobject]@{
        Severity       = $Severity
        Category       = $Category
        Title          = $Title
        Detail         = $Detail
        Recommendation = $Recommendation
    })
}

function Clear-OldFiles {
    <#
        Removes files older than the cutoff from a folder and reports how much
        space came back. Anything locked or in use is left alone silently -
        that is normal and not a failure.
    #>
    param([string]$Path, [datetime]$Before)

    $freed = 0.0
    $count = 0
    if (-not (Test-Path $Path)) { return @{ FreedMB = 0; Count = 0 } }

    $items = Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue |
             Where-Object { -not $_.PSIsContainer -and $_.LastWriteTime -lt $Before }

    foreach ($item in $items) {
        try {
            $size = $item.Length
            Remove-Item -LiteralPath $item.FullName -Force -ErrorAction Stop
            $freed += $size / 1MB
            $count++
        }
        catch { }   # in use, locked, or protected - leave it
    }

    # Tidy up folders left empty behind us
    Get-ChildItem -LiteralPath $Path -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
            }
        }

    return @{ FreedMB = $freed; Count = $count }
}

# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "  Monthly maintenance - $UserOrRole ($env:COMPUTERNAME)" -ForegroundColor Cyan
Write-Host "  Removing temporary files not touched in the last $OlderThanDays days." -ForegroundColor DarkGray
Write-Host ""

$freeBefore = 0
try {
    $c = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction Stop
    $freeBefore = [math]::Round($c.FreeSpace / 1GB, 2)
} catch { }


# ---------------------------------------------------------------------------
# Safety net: a system restore point before anything is touched
#
# Nothing here is destructive, but this is the one script that changes a client
# machine. If Windows cannot give us a way back, we do not proceed.
# ---------------------------------------------------------------------------

$abort = $false

function New-SafetyRestorePoint {
    <#
        Enables System Restore if needed, creates a restore point, and verifies
        it actually appeared. Returns a result object rather than throwing, so
        the caller can decide what to do.
    #>
    param([string]$Description)

    $result = [pscustomobject]@{
        Created = $false
        Reason  = ''
        Point   = $null
    }

    # System Restore does not exist on Windows Server editions
    try {
        $pt = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).ProductType
        if ($pt -ne 1) {
            $result.Reason = 'This is a server edition of Windows, which does not support System Restore.'
            return $result
        }
    } catch { }

    $sysDrive = "$env:SystemDrive\"
    $freqKey  = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
    $oldFreq  = $null

    try {
        # Turning it on is harmless if it is already on, and is the fix if it is off
        try { Enable-ComputerRestore -Drive $sysDrive -ErrorAction Stop }
        catch {
            $result.Reason = "System Restore could not be switched on: $($_.Exception.Message)"
            return $result
        }

        # Windows refuses a second restore point within 24 hours by default.
        # Lifted for the moment it takes to create one, then put straight back.
        try {
            $oldFreq = (Get-ItemProperty -Path $freqKey -Name 'SystemRestorePointCreationFrequency' -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Set-ItemProperty -Path $freqKey -Name 'SystemRestorePointCreationFrequency' -Value 0 -Type DWord -ErrorAction SilentlyContinue
        } catch { }

        $before = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue)
        $newestBefore = $null
        if ($before.Count -gt 0) {
            $newestBefore = ($before | Sort-Object SequenceNumber -Descending | Select-Object -First 1).SequenceNumber
        }

        Checkpoint-Computer -Description $Description -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop

        # Checkpoint-Computer can report success without creating anything,
        # so the result is verified rather than trusted.
        Start-Sleep -Seconds 3
        $after = @(Get-ComputerRestorePoint -ErrorAction SilentlyContinue)
        if ($after.Count -eq 0) {
            $result.Reason = 'Windows reported success but no restore point appeared.'
            return $result
        }

        $newest = $after | Sort-Object SequenceNumber -Descending | Select-Object -First 1
        if ($newestBefore -ne $null -and $newest.SequenceNumber -eq $newestBefore) {
            $result.Reason = 'Windows did not create a new restore point.'
            return $result
        }

        $result.Created = $true
        $result.Point   = "$($newest.Description) (number $($newest.SequenceNumber))"
        return $result
    }
    catch {
        $result.Reason = $_.Exception.Message
        return $result
    }
    finally {
        # Always put the 24 hour rule back the way it was
        try {
            if ($oldFreq -ne $null) {
                Set-ItemProperty -Path $freqKey -Name 'SystemRestorePointCreationFrequency' -Value $oldFreq -Type DWord -ErrorAction SilentlyContinue
            }
            else {
                Remove-ItemProperty -Path $freqKey -Name 'SystemRestorePointCreationFrequency' -ErrorAction SilentlyContinue
            }
        } catch { }
    }
}

if ($NoRestorePoint) {
    Add-Task -Name 'Safety restore point' -Description 'Skipped because it was turned off for this run' `
             -Result 'Skipped' -Note 'Not touched'
    Write-Host "  WARNING: running without a restore point." -ForegroundColor Yellow
}
elseif ($isAdmin -and $PSCmdlet.ShouldProcess('System restore point', 'Create')) {

    Write-Host "  Creating a restore point before anything is changed..." -ForegroundColor DarkGray
    $rp = New-SafetyRestorePoint -Description "Before monthly maintenance - $(Get-Date -Format 'yyyy-MM-dd')"

    if ($rp.Created) {
        Add-Task -Name 'Safety restore point' `
                 -Description 'Created a Windows restore point so everything could be undone if needed' `
                 -Note 'Created'
    }
    else {
        $abort = $true
        Add-Task -Name 'Safety restore point' -Description 'Could not create a restore point' `
                 -Result 'Failed' -Note $rp.Reason

        Add-Issue -Severity 'Warning' -Category 'Maintenance' `
                  -Title 'Maintenance was skipped this month as a precaution' `
                  -Detail "Before cleaning anything, a Windows restore point is created so the machine can always be put back. On this machine that was not possible: $($rp.Reason) Rather than change anything without a way back, the maintenance was skipped. The health check still ran in full." `
                  -Recommendation 'Worth looking at why System Restore is unavailable on this machine. Once it works, maintenance resumes on its own next month.'

        Write-Host ""
        Write-Host "  MAINTENANCE SKIPPED - no restore point could be created." -ForegroundColor Yellow
        Write-Host "  Reason: $($rp.Reason)" -ForegroundColor Yellow
        Write-Host "  Nothing on this machine has been changed." -ForegroundColor Yellow
        Write-Host ""
    }
}
elseif (-not $isAdmin) {
    $abort = $true
    Add-Task -Name 'Safety restore point' -Description 'Could not create a restore point' `
             -Result 'Failed' -Note 'Not running as administrator'
    Add-Issue -Severity 'Warning' -Category 'Maintenance' `
              -Title 'Maintenance was skipped this month as a precaution' `
              -Detail 'A restore point could not be created because the maintenance did not have administrator rights. Nothing was changed.' `
              -Recommendation 'The scheduled run normally has the rights it needs. If this repeats, the scheduled task on this machine needs checking.'
}

# ---- 1. Per-user temp folders -------------------------------------------------
if (-not $abort -and $PSCmdlet.ShouldProcess('User temporary files', 'Remove')) {
    $freed = 0.0; $count = 0
    $userDirs = Get-ChildItem "$env:SystemDrive\Users" -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notin 'Public','Default','All Users','Default User' }
    foreach ($u in $userDirs) {
        $tmp = Join-Path $u.FullName 'AppData\Local\Temp'
        $r = Clear-OldFiles -Path $tmp -Before $cutoff
        $freed += $r.FreedMB; $count += $r.Count
    }
    Add-Task -Name 'User temporary files' -Description 'Removed leftover temporary files from each user account' -FreedMB $freed -ItemCount $count
}

# ---- 2. Windows temp folder ---------------------------------------------------
if (-not $abort -and $PSCmdlet.ShouldProcess('Windows temporary files', 'Remove')) {
    $r = Clear-OldFiles -Path "$env:SystemRoot\Temp" -Before $cutoff
    Add-Task -Name 'Windows temporary files' -Description 'Cleared the system temporary folder' -FreedMB $r.FreedMB -ItemCount $r.Count
}

# ---- 3. Windows Update download cache ----------------------------------------
if (-not $abort -and $isAdmin -and $PSCmdlet.ShouldProcess('Windows Update cache', 'Remove')) {
    try {
        $wuPath = "$env:SystemRoot\SoftwareDistribution\Download"
        $before = 0
        if (Test-Path $wuPath) {
            $before = (Get-ChildItem $wuPath -Recurse -Force -File -ErrorAction SilentlyContinue |
                       Measure-Object Length -Sum).Sum / 1MB
        }
        Stop-Service wuauserv -Force -ErrorAction Stop
        Start-Sleep -Seconds 2
        $r = Clear-OldFiles -Path $wuPath -Before (Get-Date).AddDays(-2)
        Start-Service wuauserv -ErrorAction SilentlyContinue
        Add-Task -Name 'Windows Update cache' -Description 'Removed installer files left behind by Windows updates' -FreedMB $r.FreedMB -ItemCount $r.Count
    }
    catch {
        Start-Service wuauserv -ErrorAction SilentlyContinue
        [void]$Errors.Add("Windows Update cache: $($_.Exception.Message)")
        Add-Task -Name 'Windows Update cache' -Description 'Removed installer files left behind by Windows updates' -Result 'Failed' -Note $_.Exception.Message
    }
}

# ---- 4. Delivery Optimisation cache ------------------------------------------
if (-not $abort -and $PSCmdlet.ShouldProcess('Delivery Optimisation cache', 'Remove')) {
    if (Get-Command Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue) {
        try {
            # The cache size MUST be read before the cache is deleted, or it
            # reads zero. This was the bug: $size was initialised to 0, the
            # snapshot was fetched and never used, and the task always reported
            # nothing freed. On the machine that found it, 2.5 GB went
            # unreported - the client was shown 0.13 GB when 2.61 GB had gone.
            #
            # Named $doSizeMB rather than $size because $size is already used for
            # two other things in this file, and a name collision here has cost
            # this project days before.
            #
            # The cmdlet warns when there are no active transfers, which is the
            # normal idle state and not a problem, so the warning is suppressed.
            $doSizeMB = 0
            $doSnap = @(Get-DeliveryOptimizationPerfSnap -ErrorAction SilentlyContinue -WarningAction SilentlyContinue)[0]
            if ($doSnap -and $doSnap.CacheSizeBytes -gt 0) {
                $doSizeMB = [math]::Round($doSnap.CacheSizeBytes / 1MB, 1)
            }
            Delete-DeliveryOptimizationCache -Force -ErrorAction Stop
            Add-Task -Name 'Update sharing cache' -Description 'Cleared cached update files shared with other machines' -FreedMB $doSizeMB
        }
        catch {
            Add-Task -Name 'Update sharing cache' -Description 'Cleared cached update files' -Result 'Skipped' -Note 'Not available on this machine'
        }
    }
    else {
        Add-Task -Name 'Update sharing cache' -Description 'Cleared cached update files' -Result 'Skipped' -Note 'Not available on this version of Windows'
    }
}

# ---- 5. Error reporting queue -------------------------------------------------
if (-not $abort -and $PSCmdlet.ShouldProcess('Error reports', 'Remove')) {
    $freed = 0.0; $count = 0
    foreach ($p in @("$env:ProgramData\Microsoft\Windows\WER\ReportQueue",
                     "$env:ProgramData\Microsoft\Windows\WER\ReportArchive")) {
        $r = Clear-OldFiles -Path $p -Before $cutoff
        $freed += $r.FreedMB; $count += $r.Count
    }
    Add-Task -Name 'Stored error reports' -Description 'Removed crash reports Windows had queued up' -FreedMB $freed -ItemCount $count
}

# ---- 6. Recycle bin -----------------------------------------------------------
if (-not $abort -and $EmptyRecycleBin -and $PSCmdlet.ShouldProcess('Recycle bin', 'Empty')) {
    try {
        $binFreed = 0.0; $binCount = 0
        $bins = Get-ChildItem "$env:SystemDrive\`$Recycle.Bin" -Force -Directory -ErrorAction SilentlyContinue
        $binCutoff = (Get-Date).AddDays(-$RecycleBinDays)
        foreach ($b in $bins) {
            $r = Clear-OldFiles -Path $b.FullName -Before $binCutoff
            $binFreed += $r.FreedMB; $binCount += $r.Count
        }
        Add-Task -Name 'Recycle bin' -Description "Emptied deleted files older than $RecycleBinDays days" -FreedMB $binFreed -ItemCount $binCount
    }
    catch {
        Add-Task -Name 'Recycle bin' -Description 'Emptied old deleted files' -Result 'Failed' -Note $_.Exception.Message
    }
}
else {
    Add-Task -Name 'Recycle bin' -Description 'Deliberately left alone' -Result 'Skipped' `
             -Note 'Not touched'
}

# ---- 7. Thumbnail and icon caches --------------------------------------------
if (-not $abort -and $PSCmdlet.ShouldProcess('Thumbnail cache', 'Remove')) {
    $freed = 0.0; $count = 0
    $userDirs = Get-ChildItem "$env:SystemDrive\Users" -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -notin 'Public','Default','All Users','Default User' }
    foreach ($u in $userDirs) {
        $explorer = Join-Path $u.FullName 'AppData\Local\Microsoft\Windows\Explorer'
        if (Test-Path $explorer) {
            Get-ChildItem $explorer -Filter 'thumbcache_*.db' -Force -ErrorAction SilentlyContinue |
                ForEach-Object {
                    try {
                        $sz = $_.Length
                        Remove-Item $_.FullName -Force -ErrorAction Stop
                        $freed += $sz / 1MB; $count++
                    } catch { }
                }
        }
    }
    Add-Task -Name 'Thumbnail cache' -Description 'Cleared cached picture previews, which Windows rebuilds as needed' -FreedMB $freed -ItemCount $count
}

# ---- 8. Component store (optional, slow) --------------------------------------
if (-not $abort -and $DeepClean -and $isAdmin -and $PSCmdlet.ShouldProcess('Superseded Windows files', 'Remove')) {
    try {
        $null = & dism.exe /online /cleanup-image /StartComponentCleanup /Quiet 2>&1
        Add-Task -Name 'Old Windows files' -Description 'Removed replaced Windows system files kept from previous updates' -Note 'Space recovered varies'
    }
    catch {
        Add-Task -Name 'Old Windows files' -Description 'Removed replaced Windows system files' -Result 'Failed' -Note $_.Exception.Message
    }
}

# ---- 9. Virus scan ------------------------------------------------------------
if (-not $abort -and $RunVirusScan) {
    $scanned = $false

    # Whatever is actually protecting the machine should do the scanning.
    $eset = Get-ChildItem "$env:ProgramFiles\ESET" -Recurse -Filter 'ecls.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
    if ($eset) {
        try {
            Write-Host "  Running an ESET scan. This can take a while..." -ForegroundColor DarkGray
            $null = & $eset.FullName /base-dir="$($eset.DirectoryName)" /files /subdir /quiet "$env:SystemDrive\Users" 2>&1
            Add-Task -Name 'Virus scan' -Description 'Ran a scan using ESET, the antivirus installed on this machine'
            $scanned = $true
        }
        catch { [void]$Errors.Add("ESET scan: $($_.Exception.Message)") }
    }

    if (-not $scanned -and (Get-Command Start-MpScan -ErrorAction SilentlyContinue)) {
        try {
            $mp = Get-MpComputerStatus -ErrorAction Stop
            if ($mp.RealTimeProtectionEnabled) {
                Start-MpScan -ScanType QuickScan -ErrorAction Stop
                Add-Task -Name 'Virus scan' -Description 'Ran a quick scan using Microsoft Defender'
                $scanned = $true
            }
        }
        catch { [void]$Errors.Add("Defender scan: $($_.Exception.Message)") }
    }

    if (-not $scanned) {
        Add-Task -Name 'Virus scan' -Description 'Scan requested' -Result 'Skipped' `
                 -Note 'No scanner on this machine could be started from the command line'
    }
}


# ---- 10. Drive optimisation -------------------------------------------------
#
# Defragmenting an SSD wears it out for no benefit. The correct monthly job on
# solid state is TRIM, and on mechanical drives it is a defragment - but only
# when fragmentation is actually high enough to be worth the time.

if (-not $abort -and -not $SkipOptimise -and $isAdmin -and $PSCmdlet.ShouldProcess('Drives', 'Optimise')) {

    $volumes = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction SilentlyContinue

    foreach ($v in $volumes) {
        $letter = $v.DeviceID.TrimEnd(':')
        $media  = 'Unknown'

        try {
            $part = Get-Partition -DriveLetter $letter -ErrorAction Stop
            $pd   = Get-PhysicalDisk -ErrorAction Stop |
                    Where-Object { $_.DeviceId -eq "$($part.DiskNumber)" } | Select-Object -First 1
            if ($pd) { $media = "$($pd.MediaType)" }
        } catch { }

        try {
            if ($media -eq 'SSD') {
                Optimize-Volume -DriveLetter $letter -ReTrim -ErrorAction Stop
                Add-Task -Name "Drive $($v.DeviceID) optimised" `
                         -Description 'Solid state drive tidied up so it keeps writing at full speed' `
                         -Note 'TRIM'
            }
            elseif ($media -eq 'HDD') {
                # Ask Windows how fragmented it is before spending an hour on it
                $frag = $null
                try {
                    $analysis = (& defrag.exe "$($v.DeviceID)" /A /V 2>&1 | Out-String) -replace "`0", ''
                    if ($analysis -match 'Total fragmented space:\s*(\d+)\s*%') { $frag = [int]$Matches[1] }
                } catch { }

                if ($frag -ne $null -and $frag -lt $DefragThreshold) {
                    Add-Task -Name "Drive $($v.DeviceID) checked" `
                             -Description "Checked for fragmentation and found only $frag% - no work needed" `
                             -Result 'Skipped' -Note "$frag% fragmented"
                }
                else {
                    Optimize-Volume -DriveLetter $letter -Defrag -ErrorAction Stop
                    $after = $null
                    try {
                        $analysis2 = (& defrag.exe "$($v.DeviceID)" /A /V 2>&1 | Out-String) -replace "`0", ''
                        if ($analysis2 -match 'Total fragmented space:\s*(\d+)\s*%') { $after = [int]$Matches[1] }
                    } catch { }

                    $note = if ($frag -ne $null -and $after -ne $null) { "$frag% down to $after%" } else { 'Defragmented' }
                    Add-Task -Name "Drive $($v.DeviceID) defragmented" `
                             -Description 'Reorganised files so the drive spends less time hunting for them' `
                             -Note $note
                }
            }
            else {
                Add-Task -Name "Drive $($v.DeviceID) optimised" `
                         -Description 'Drive optimisation' -Result 'Skipped' `
                         -Note 'Could not tell whether this is solid state or mechanical'
            }
        }
        catch {
            [void]$Errors.Add("Optimise $($v.DeviceID): $($_.Exception.Message)")
            Add-Task -Name "Drive $($v.DeviceID) optimised" -Description 'Drive optimisation' `
                     -Result 'Failed' -Note $_.Exception.Message
        }
    }
}

# ---- 11. Windows system file check ------------------------------------------
#
# sfc verifies every protected Windows file against Microsoft's originals and
# repairs anything damaged. Takes roughly ten minutes. Its output comes back as
# UTF-16 with embedded nulls, which is why it gets stripped below.

# sfc will not run at all while a servicing operation is waiting on a restart -
# it returns in seconds with "There is a system repair pending" - so there is no
# point starting it. Checked before the scan rather than after, so the run is not
# held up and the client is told plainly why it was deferred.
#
# Only the servicing and update flags block sfc. PendingFileRenameOperations is
# set constantly by application updaters and is true on virtually every working
# machine, so testing that as well would skip the scan permanently.
$sfcBlocked = $false
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')      { $sfcBlocked = $true }
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')     { $sfcBlocked = $true }

if (-not $abort -and -not $SkipSystemCheck -and $isAdmin -and $sfcBlocked) {
    Write-Host "  Skipping the Windows file check - a restart is pending." -ForegroundColor DarkGray
    Add-Task -Name 'Windows system files' `
             -Description 'Could not be checked - the machine is waiting for a restart to finish an earlier update' `
             -Result 'Skipped' -Note 'Needs a restart first'
    Add-Issue -Severity 'Info' -Category 'Maintenance' `
              -Title 'The Windows file check needs a restart before it can run' `
              -Detail 'This machine is waiting for a restart to finish an update it installed earlier. Windows will not check its own files until that has been done.' `
              -Recommendation 'It will be checked automatically on the next monthly run once the machine has been restarted.'
}

if (-not $abort -and -not $SkipSystemCheck -and $isAdmin -and -not $sfcBlocked -and $PSCmdlet.ShouldProcess('Windows system files', 'Check and repair')) {
    try {
        Write-Host "  Checking Windows system files." -ForegroundColor DarkGray
        Write-Host "  Usually ten minutes, but can take far longer on a machine with" -ForegroundColor DarkGray
        Write-Host "  update problems. Progress is shown below - leave it running." -ForegroundColor DarkGray
        Write-Host ""

        # sfc is run as a separate process with its output captured to a file,
        # so that progress can be reported while it works. Reading its output
        # directly makes the console sit blank for the whole run, which looks
        # identical to a hang and gets it killed unnecessarily.
        $sfcFile = Join-Path $env:TEMP "sfc-$PID.txt"
        $sfcErr  = Join-Path $env:TEMP "sfc-$PID.err"
        $cbsLog  = "$env:SystemRoot\Logs\CBS\CBS.log"

        $proc = Start-Process -FilePath "$env:SystemRoot\System32\sfc.exe" `
                              -ArgumentList '/scannow' -PassThru -NoNewWindow `
                              -RedirectStandardOutput $sfcFile -RedirectStandardError $sfcErr

        $sfcStart  = Get-Date
        $lastSize  = -1
        $stallMins = 0
        $timedOut  = $false

        while (-not $proc.HasExited) {
            Start-Sleep -Seconds 30
            $mins = [math]::Round(((Get-Date) - $sfcStart).TotalMinutes)

            # CBS.log grows continuously while the check runs. It is the only
            # reliable sign of life.
            $size    = if (Test-Path $cbsLog) { (Get-Item $cbsLog -ErrorAction SilentlyContinue).Length } else { 0 }
            $growing = ($size -ne $lastSize)
            $lastSize = $size

            if ($growing) {
                $stallMins = 0
                Write-Host ("    {0} min - working ({1:N0} MB of log written)" -f $mins, ($size/1MB)) -ForegroundColor DarkGray
            }
            else {
                $stallMins += 0.5
                Write-Host ("    {0} min - no activity for {1} min" -f $mins, $stallMins) -ForegroundColor Yellow
            }

            if ($mins -ge $SystemCheckTimeoutMinutes) {
                $timedOut = $true
                try { $proc.Kill() } catch { }
                break
            }
        }

        Write-Host ""

        $sfcOut = ''
        if (Test-Path $sfcFile) {
            $sfcOut = (Get-Content $sfcFile -Raw -ErrorAction SilentlyContinue) -replace "`0", ''
        }
        Remove-Item $sfcFile, $sfcErr -Force -ErrorAction SilentlyContinue

        if ($timedOut) {
            Add-Task -Name 'Windows system files' -Description 'System file check' `
                     -Result 'Failed' -Note "Gave up after $SystemCheckTimeoutMinutes minutes"
            Add-Issue -Severity 'Warning' -Category 'Maintenance' `
                      -Title 'The Windows system file check could not finish' `
                      -Detail "The check ran for $SystemCheckTimeoutMinutes minutes without completing and was stopped. On a healthy machine it takes about ten." `
                      -Recommendation 'This usually means Windows Update itself is damaged on this machine. It needs hands-on repair, and is often the same underlying fault behind update failures.'
            $sfcOut = ''
        }

        if ($sfcOut -match 'did not find any integrity violations') {
            Add-Task -Name 'Windows system files' `
                     -Description 'Checked every protected Windows file against Microsoft originals' `
                     -Note 'No damage found'
        }
        elseif ($sfcOut -match 'successfully repaired them') {
            Add-Task -Name 'Windows system files' `
                     -Description 'Found damaged Windows files and repaired them' `
                     -Note 'Repaired'
            Add-Issue -Severity 'Info' -Category 'Maintenance' `
                      -Title 'Damaged Windows files were found and repaired' `
                      -Detail 'Some protected Windows files had become corrupted. They were restored from Microsoft originals during this month''s maintenance.' `
                      -Recommendation 'No action needed. If this happens again next month, it usually points to a failing drive or memory, which is worth investigating.'
        }
        elseif ($sfcOut -match 'unable to fix some of them') {
            Add-Task -Name 'Windows system files' `
                     -Description 'Found damaged Windows files that could not be repaired automatically' `
                     -Result 'Failed' -Note 'Repair incomplete'
            Add-Issue -Severity 'Warning' -Category 'Maintenance' `
                      -Title 'Damaged Windows files could not be repaired automatically' `
                      -Detail 'Windows found corrupted system files and was unable to repair all of them on its own.' `
                      -Recommendation 'Needs hands-on attention. Usually fixed with a deeper repair, but repeated corruption can mean a failing drive or faulty memory.'
        }
        elseif ($sfcOut -match 'could not perform the requested operation') {
            Add-Task -Name 'Windows system files' -Description 'System file check' `
                     -Result 'Failed' -Note 'Windows refused to run the check'
            Add-Issue -Severity 'Warning' -Category 'Maintenance' `
                      -Title 'Windows could not check its own system files' `
                      -Detail 'The built-in system file check would not run on this machine.' `
                      -Recommendation 'Worth investigating. This often accompanies a broader problem with Windows servicing.'
        }
        elseif ($sfcOut -match 'repair pending' -or $sfcOut -match 'requires reboot') {
            # sfc refuses to run while a repair is waiting on a restart. It
            # returns in seconds. Before this branch existed it fell through to
            # the catch-all below and the client was told their Windows files had
            # been checked when nothing had been done at all.
            Add-Task -Name 'Windows system files' `
                     -Description 'Could not be checked - the machine is waiting for a restart to finish an earlier update' `
                     -Result 'Skipped' -Note 'Needs a restart first'
            Add-Issue -Severity 'Info' -Category 'Maintenance' `
                      -Title 'The Windows file check needs a restart before it can run' `
                      -Detail 'This machine is waiting for a restart to finish an update it installed earlier. Windows will not check its own files until that has been done.' `
                      -Recommendation 'It will be checked automatically on the next monthly run once the machine has been restarted.'
        }
        elseif ($sfcOut) {
            # The scan said something we do not recognise. NOT reported as work
            # done: on a non-English Windows this may be a clean result, but it
            # may equally be a failure never seen before. Telling a client their
            # files were checked when we cannot tell is the one thing this report
            # must never do - it is the invoice justification.
            Add-Task -Name 'Windows system files' `
                     -Description 'The system file check ran but did not report a result we could read' `
                     -Result 'Failed' -Note 'Result unclear'
        }
    }
    catch {
        [void]$Errors.Add("System file check: $($_.Exception.Message)")
        Add-Task -Name 'Windows system files' -Description 'System file check' `
                 -Result 'Failed' -Note $_.Exception.Message
    }
}

# ---------------------------------------------------------------------------
# Record what happened
# ---------------------------------------------------------------------------

$freeAfter = $freeBefore
try {
    $c = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction Stop
    $freeAfter = [math]::Round($c.FreeSpace / 1GB, 2)
} catch { }

$totalMB = ($Tasks | Measure-Object FreedMB -Sum).Sum
$record = [pscustomobject]@{
    SchemaVersion  = '1.0'
    RecordType     = 'Maintenance'
    Organisation   = $Organisation
    UserOrRole     = $UserOrRole
    ComputerName   = $env:COMPUTERNAME
    RanAtUtc       = $started.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    RanAtLocal     = $started.ToString('yyyy-MM-dd HH:mm')
    DurationMinutes= [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    RanAsAdmin     = $isAdmin
    FreeSpaceBeforeGB = $freeBefore
    FreeSpaceAfterGB  = $freeAfter
    MaintenanceRan = (-not $abort)
    TotalFreedMB   = [math]::Round($totalMB, 1)
    TotalFreedGB   = [math]::Round($totalMB / 1024, 2)

    # The gap between what the tasks measured and what actually came free.
    # Technician-facing only; never shown to the client. A large positive number
    # means a cleanup step is not reporting what it removed, which is exactly how
    # the Delivery Optimisation cache hid 2.5 GB and made the report understate
    # the work by twentyfold. It is noisy - the restore point consumes space and
    # other processes write during the run - so treat it as a smell, not a
    # measurement. Consistently large means go looking for the silent task.
    UnattributedMB = [math]::Round(((($freeAfter - $freeBefore) * 1024) - $totalMB), 1)
    Tasks          = @($Tasks)
    Issues         = @($Issues)
    Errors         = @($Errors)
}

if (-not (Test-Path $OutputPath)) { New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null }

$slug = ($Organisation -replace '[^a-zA-Z0-9]+','-').Trim('-').ToLower()
$file = Join-Path $OutputPath ('maintenance_{0}_{1}_{2}.json' -f $slug, $env:COMPUTERNAME, (Get-Date -Format 'yyyy-MM'))

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($file, ($record | ConvertTo-Json -Depth 5), $utf8NoBom)

Write-Host ""
Write-Host ("  Space recovered: {0:N2} GB" -f $record.TotalFreedGB) -ForegroundColor Green
Write-Host ("  Free space on {0} went from {1} GB to {2} GB" -f $env:SystemDrive, $freeBefore, $freeAfter) -ForegroundColor Green
Write-Host "  Record written to: $file" -ForegroundColor DarkGray
Write-Host ""

if ($PassThru) { $record }
