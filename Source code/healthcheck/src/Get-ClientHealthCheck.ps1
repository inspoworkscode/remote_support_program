<#
.SYNOPSIS
    Monthly endpoint health check collector.

.DESCRIPTION
    Runs a fixed set of read-only checks on a Windows machine and writes a single
    JSON file describing the result. The JSON contains both raw data (for your
    records) and a Findings array (for the report generator / AI summariser).

    Nothing is changed on the machine. All checks are read-only.

.NOTES
    Requires: PowerShell 5.1 (built into Windows 10 / 11 / Server 2016+)
    Run as:   SYSTEM or a local administrator (some checks degrade gracefully without it)
    Version:  1.6

.EXAMPLE
    .\Get-ClientHealthCheck.ps1 -Organisation "Knysna Guesthouse" -UserOrRole "Reception" -OutputPath "C:\HealthCheck"

.EXAMPLE
    .\Get-ClientHealthCheck.ps1 -Organisation "Acme Accounting" -UserOrRole "Sarah" -OutputPath "\\server\share\healthcheck"
#>

[CmdletBinding()]
param(
    # Who uses this PC, or what it is for. Shown to the client instead of the
    # machine name, because "Reception PC" means something to them and
    # "DESKTOP-OPHQIDH" does not.
    [Alias('ClientName')]
    [string]$UserOrRole,

    # The organisation this machine belongs to. Reports are produced per organisation.
    [Parameter(Mandatory = $false)]
    [string]$Organisation,

    # Settings file written by the installer. Holds the organisation, the client
    # and the email details. Keeps secrets off the command line, where Task
    # Scheduler would display them.
    [string]$ConfigPath,

    # Where the JSON file is written.
    [string]$OutputPath = "C:\ProgramData\HealthCheck",

    # Optional: extra folders to check for recent backup files (e.g. "D:\Backups", "\\nas\backup")
    [string[]]$BackupPaths = @(),

    # Windows Update search is the slowest check (30s-2min) and needs internet. Skip it for speed.
    [switch]$SkipWindowsUpdate,

    # Do not email from here. Used when the monthly run sends one combined email.
    [switch]$NoEmail,

    # Return the full result object. For scripting only - it is far too much
    # detail to put in front of a technician.
    [switch]$PassThru,

    # How many days back to scan the event log.
    [int]$EventLogDays = 30,

    # Thresholds - tune these to taste.
    [int]$DiskFreePercentWarning  = 20,
    [int]$DiskFreePercentCritical = 10,
    [int]$MachineAgeYearsWarning  = 5,
    [int]$BackupStaleDaysWarning  = 7,
    [int]$SignatureAgeDaysWarning = 5,

    # Temporary files older than this count as recoverable. Deliberately the
    # same as OlderThanDays in Invoke-ClientMaintenance.ps1, so the figure this
    # report promises and the figure maintenance actually recovers cannot drift.
    [int]$TempFileAgeDays = 7,

    # Only mention temporary files once there is enough to be worth clearing.
    [int]$TempFileWarnMB = 500,

    # Only mention the recycle bin once it holds at least this much.
    [double]$RecycleBinMinGB = 1,

    # Fragmentation at which a mechanical drive is worth defragmenting. Matches
    # DefragThreshold in the maintenance script.
    [int]$FragmentationWarnPercent = 10,

    # Days after which drive optimisation is considered overdue.
    [int]$OptimiseStaleDays = 60,

    # Run sfc /verifyonly. It REPORTS damage and never repairs it, so the
    # read-only promise still holds - but it adds roughly ten minutes.
    # A [bool] rather than a [switch] because it has to survive the trip from a
    # batch file through powershell.exe.
    [bool]$VerifySystemFiles = $true,

    # Give up on the system file check after this many minutes.
    [int]$SystemCheckTimeoutMinutes = 30
)


# Version of this toolkit. Printed at run time so a stale copy cannot hide.
$ToolkitVersion = '2.11'

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

# ---------------------------------------------------------------------------
# Settings file
#
# Anything passed on the command line wins. Anything not passed is taken from
# config.json next to this script, which is what the installer writes.
# ---------------------------------------------------------------------------

$Config = $null
if (-not $ConfigPath) {
    $ConfigPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'config.json'
}
if (Test-Path $ConfigPath) {
    try   { $Config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { Write-Verbose "Settings file could not be read: $($_.Exception.Message)" }
}

if (-not $UserOrRole   -and $Config.UserOrRole)   { $UserOrRole   = $Config.UserOrRole }
if (-not $UserOrRole   -and $Config.ClientName)   { $UserOrRole   = $Config.ClientName }
if (-not $Organisation -and $Config.Organisation) { $Organisation = $Config.Organisation }
if (-not $Organisation) { $Organisation = $UserOrRole }

if (-not $Organisation) {
    Write-Host "No organisation was supplied, and no settings file was found." -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# Infrastructure
# ---------------------------------------------------------------------------

$Findings = New-Object System.Collections.ArrayList
$Errors   = New-Object System.Collections.ArrayList

function Add-Finding {
    <#
        Severity: Critical | Warning | Info | OK
        Recommendation is what turns into a quote. Leave it empty for OK findings.
    #>
    param(
        [Parameter(Mandatory)][string]$Category,
        [Parameter(Mandatory)][ValidateSet('Critical','Warning','Info','OK')][string]$Severity,
        [Parameter(Mandatory)][string]$Title,
        [string]$Detail = '',
        [string]$Recommendation = ''
    )
    [void]$Findings.Add([pscustomobject]@{
        Category       = $Category
        Severity       = $Severity
        Title          = $Title
        Detail         = $Detail
        Recommendation = $Recommendation
    })
}

function Invoke-Check {
    # Wraps every check so one failure never kills the run.
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )
    try {
        & $Body
    }
    catch {
        [void]$Errors.Add([pscustomobject]@{
            Check   = $Name
            Message = $_.Exception.Message
        })
        Write-Verbose "Check '$Name' failed: $($_.Exception.Message)"
    }
}

function ConvertTo-Slug {
    param([string]$Text)
    $s = $Text -replace '[^a-zA-Z0-9]+', '-'
    return ($s -replace '(^-|-$)', '').ToLower()
}

$IsAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

# ---------------------------------------------------------------------------
# 1. Machine identity and age
# ---------------------------------------------------------------------------

$Machine = [pscustomobject]@{
    ComputerName = $env:COMPUTERNAME
    Manufacturer = $null
    Model        = $null
    SerialNumber = $null
    BiosDate     = $null
    AgeYears     = $null
    AgeBasis     = $null
    AgeAsOf      = $null
    AgeSignals   = @()
    CpuName      = $null
    RamGB        = $null
    IsPortable   = $null
    FormFactor   = $null
    LastUser     = $null
}

Invoke-Check -Name 'MachineInfo' -Body {
    $cs   = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $bios = Get-CimInstance Win32_BIOS -ErrorAction Stop
    $cpu  = Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1

    $Machine.Manufacturer = $cs.Manufacturer
    $Machine.Model        = $cs.Model
    $Machine.SerialNumber = $bios.SerialNumber
    $Machine.CpuName      = if ($cpu) { $cpu.Name.Trim() } else { $null }
    $Machine.RamGB        = [math]::Round($cs.TotalPhysicalMemory / 1GB, 1)
    $Machine.LastUser     = $cs.UserName

    # Laptop or desktop? Changes how seriously some findings should be taken.
    try {
        $chassis = (Get-CimInstance Win32_SystemEnclosure -ErrorAction Stop | Select-Object -First 1).ChassisTypes
        if ($chassis) {
            $type = [int]$chassis[0]
            $Machine.IsPortable = ($type -in 8,9,10,11,12,14,18,21,30,31,32)
            $Machine.FormFactor = if ($Machine.IsPortable) { 'Laptop' } else { 'Desktop' }
        }
    } catch { }

    if ($bios.ReleaseDate) { $Machine.BiosDate = $bios.ReleaseDate }

    # Hardware age cannot be read directly, and every software signal can be
    # reset. Using the BIOS release date alone was wrong:
    #
    #   BIOS ReleaseDate  a firmware update rewrites it. The machine that
    #                     prompted this went from "1.1 years old" to "0.3 years
    #                     old" overnight because Windows Update delivered an
    #                     Acer firmware update.
    #   OS InstallDate    resets on a reinstall.
    #   C:\ creation      is the OEM factory image date and can predate the
    #                     machine by years - 2022 on a 2024 laptop.
    #   Driver dates      contain obvious rubbish. The oldest on that same
    #                     machine was 1968.
    #
    # Windows records every previous install under HKLM\SYSTEM\Setup\Source OS*,
    # and those survive feature updates, so they are the most durable evidence
    # available. Taking the EARLIEST plausible signal makes the answer a lower
    # bound on the machine's real age.
    #
    # A firmware update can now only make the estimate slightly conservative,
    # where before it reset the age entirely and an eight-year-old machine would
    # quietly stop generating the replacement finding.
    $ageSignals = New-Object System.Collections.ArrayList

    if ($bios.ReleaseDate) {
        [void]$ageSignals.Add([pscustomobject]@{ Source = 'BIOS release date'; Date = $bios.ReleaseDate })
    }

    try {
        $osInstall = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).InstallDate
        if ($osInstall) {
            [void]$ageSignals.Add([pscustomobject]@{ Source = 'Windows install date'; Date = $osInstall })
        }
    } catch { }

    try {
        $setupKeys = @(Get-ChildItem 'HKLM:\SYSTEM\Setup' -ErrorAction Stop |
                       Where-Object { $_.PSChildName -like 'Source OS*' })
        foreach ($k in $setupKeys) {
            $pr = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
            if ($pr.InstallDate) {
                [void]$ageSignals.Add([pscustomobject]@{
                    Source = 'Earlier Windows install'
                    Date   = [datetimeoffset]::FromUnixTimeSeconds([int64]$pr.InstallDate).LocalDateTime
                })
            }
        }
    } catch { }

    # Anything before 2000 or in the future is rubbish, not evidence. @() because
    # a single surviving signal would otherwise come back as a bare object and
    # indexing it would return a property rather than the item.
    $plausible = @($ageSignals |
                   Where-Object { $_.Date -gt (Get-Date '2000-01-01') -and $_.Date -le (Get-Date).AddDays(1) } |
                   Sort-Object Date)

    if ($plausible.Count -gt 0) {
        $oldest = $plausible[0]
        $age    = [math]::Round(((Get-Date) - $oldest.Date).TotalDays / 365.25, 1)

        $Machine.AgeYears    = $age
        $Machine.AgeBasis    = $oldest.Source
        $Machine.AgeAsOf     = $oldest.Date.ToString('yyyy-MM-dd')
        $Machine.AgeSignals  = @($plausible | ForEach-Object {
                                   [pscustomobject]@{ Source = $_.Source; Date = $_.Date.ToString('yyyy-MM-dd') } })

        # Wording stays "approximately" throughout. It is an estimate from the
        # oldest evidence on the machine, not a manufacture date, and the report
        # must not imply more precision than that.
        if ($age -ge 10) {
            Add-Finding -Category 'Hardware' -Severity 'Critical' `
                -Title "Machine is approximately $age years old" `
                -Detail "$($Machine.Manufacturer) $($Machine.Model), in service since at least $($oldest.Date.ToString('MMMM yyyy')). Replacement parts for hardware this age are often no longer available." `
                -Recommendation "At this age a failure is a question of when, not if, and it will happen without warning rather than at a time that suits you. Worth planning a replacement now rather than reacting to a breakdown."
        }
        elseif ($age -ge $MachineAgeYearsWarning) {
            Add-Finding -Category 'Hardware' -Severity 'Warning' `
                -Title "Machine is approximately $age years old" `
                -Detail "$($Machine.Manufacturer) $($Machine.Model), in service since at least $($oldest.Date.ToString('MMMM yyyy'))." `
                -Recommendation "Machines past five years old fail more often, and the downtime usually costs more than keeping it going saves. Worth starting to plan a replacement rather than waiting for it to fail."
        }
    }

    # Reported RAM is always slightly below installed RAM because integrated graphics
    # reserve a share of it. An 8 GB machine typically reports 7.8 GB, so round up to
    # the nearest sensible size before judging it.
    $installedGB = [math]::Round($Machine.RamGB)
    if ($installedGB -le 4) {
        Add-Finding -Category 'Hardware' -Severity 'Warning' `
            -Title "Only $installedGB GB of memory installed" `
            -Detail "Windows plus a browser and Office needs 8 GB to run comfortably. This machine has $($Machine.RamGB) GB usable." `
            -Recommendation "A memory upgrade is usually the cheapest meaningful speed improvement available for this machine."
    }
    elseif ($installedGB -le 8) {
        Add-Finding -Category 'Hardware' -Severity 'Info' `
            -Title "$installedGB GB of memory installed" `
            -Detail "Adequate for everyday office work, but tight if you keep many browser tabs and applications open at once." `
            -Recommendation "If this machine feels slow, adding memory is usually the cheapest way to improve it."
    }
}

# ---------------------------------------------------------------------------
# 2. Operating system and support status
# ---------------------------------------------------------------------------

$OperatingSystem = [pscustomobject]@{
    Caption          = $null
    Version          = $null
    BuildNumber      = $null
    DisplayVersion   = $null
    InstallDate      = $null
    LastBootUpTime   = $null
    UptimeDays       = $null
    Architecture     = $null
    SupportStatus    = 'Unknown'
}

Invoke-Check -Name 'OperatingSystem' -Body {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $OperatingSystem.Caption        = $os.Caption
    $OperatingSystem.Version        = $os.Version
    $OperatingSystem.BuildNumber    = $os.BuildNumber
    $OperatingSystem.InstallDate    = $os.InstallDate
    $OperatingSystem.LastBootUpTime = $os.LastBootUpTime
    $OperatingSystem.Architecture   = $os.OSArchitecture
    $OperatingSystem.UptimeDays     = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalDays, 1)

    $dv = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue).DisplayVersion
    $OperatingSystem.DisplayVersion = $dv

    # Support status. Edit this block as Microsoft's dates move.
    $build = [int]$os.BuildNumber
    if ($os.Caption -match 'Windows 10') {
        $OperatingSystem.SupportStatus = 'End of support'
        Add-Finding -Category 'Operating System' -Severity 'Critical' `
            -Title 'Windows 10 no longer receives security updates' `
            -Detail "This machine runs $($os.Caption) (build $build). Mainstream support for Windows 10 ended in October 2025." `
            -Recommendation "Upgrade to Windows 11 if the hardware supports it, or replace the machine. Unpatched systems are the most common entry point for ransomware."
    }
    elseif ($os.Caption -match 'Windows 11') {
        $OperatingSystem.SupportStatus = 'Supported'
        if ($build -lt 22621) {
            Add-Finding -Category 'Operating System' -Severity 'Warning' `
                -Title 'Windows 11 feature version is out of date' `
                -Detail "Running build $build. Older Windows 11 releases stop receiving updates on a rolling schedule." `
                -Recommendation "Schedule a feature update to the current Windows 11 release."
        }
    }
    elseif ($os.Caption -match 'Server 2012|Server 2008|Windows 7|Windows 8') {
        $OperatingSystem.SupportStatus = 'End of support'
        Add-Finding -Category 'Operating System' -Severity 'Critical' `
            -Title "$($os.Caption) is past end of support" `
            -Detail "No further security updates are issued for this operating system." `
            -Recommendation "This machine no longer receives security updates and should be replaced or upgraded as a priority."
    }
    else {
        $OperatingSystem.SupportStatus = 'Supported'
    }

    if ($OperatingSystem.UptimeDays -gt 30) {
        Add-Finding -Category 'Operating System' -Severity 'Warning' `
            -Title "Machine has not been restarted in $($OperatingSystem.UptimeDays) days" `
            -Detail "Long uptime usually means pending updates have never finished installing." `
            -Recommendation "A restart outside working hours will let outstanding updates complete."
    }
}

# ---------------------------------------------------------------------------
# 2b. Windows 11 upgrade eligibility
#     This is the check that decides whether an out-of-support machine becomes a
#     free upgrade or a hardware quote. It is the most commercially useful check
#     in the whole script.
# ---------------------------------------------------------------------------

$Windows11 = [pscustomobject]@{
    Applicable   = $false
    TpmPresent   = $null
    TpmVersion   = $null
    TpmEnabled   = $null
    SecureBoot   = $null
    Ram64BitOk   = $null
    DiskOk       = $null
    Verdict      = 'Not assessed'
}

Invoke-Check -Name 'Windows11Readiness' -Body {
    if ($OperatingSystem.Caption -match 'Windows 11') {
        $Windows11.Verdict = 'Already on Windows 11'
        return
    }
    if ($OperatingSystem.Caption -notmatch 'Windows 10') { return }
    $Windows11.Applicable = $true

    # TPM
    try {
        $tpm = Get-CimInstance -Namespace 'root\cimv2\security\microsofttpm' -ClassName Win32_Tpm -ErrorAction Stop
        if ($tpm) {
            $Windows11.TpmPresent = $true
            $Windows11.TpmVersion = ($tpm.SpecVersion -split ',')[0].Trim()
            $Windows11.TpmEnabled = [bool]$tpm.IsEnabled_InitialValue
        }
        else { $Windows11.TpmPresent = $false }
    }
    catch { $Windows11.TpmPresent = $false }

    # Secure Boot - throws on legacy BIOS machines, which is itself the answer
    try   { $Windows11.SecureBoot = [bool](Confirm-SecureBootUEFI -ErrorAction Stop) }
    catch { $Windows11.SecureBoot = $false }

    $Windows11.Ram64BitOk = ($Machine.RamGB -ge 3.5 -and $OperatingSystem.Architecture -match '64')

    # Queried directly rather than from $Disks, because the storage section runs later.
    $sysDisk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction SilentlyContinue
    $Windows11.DiskOk = ($sysDisk -and ($sysDisk.Size / 1GB) -ge 60)

    $tpmOk = ($Windows11.TpmPresent -and $Windows11.TpmVersion -like '2*' -and $Windows11.TpmEnabled)

    if ($tpmOk -and $Windows11.SecureBoot -and $Windows11.Ram64BitOk -and $Windows11.DiskOk) {
        $Windows11.Verdict = 'Likely eligible'
        Add-Finding -Category 'Operating System' -Severity 'Info' `
            -Title 'This machine appears eligible for a free Windows 11 upgrade' `
            -Detail "Secure Boot and TPM $($Windows11.TpmVersion) are both present and enabled, and the hardware meets the memory and storage requirements." `
            -Recommendation "No new hardware is needed. The upgrade can be done on the machine as it stands and brings it back into security support, so the only cost is the time to do it."
    }
    else {
        $missing = @()
        if (-not $tpmOk)                 { $missing += 'TPM 2.0' }
        if (-not $Windows11.SecureBoot)  { $missing += 'Secure Boot' }
        if (-not $Windows11.Ram64BitOk)  { $missing += 'memory or 64-bit requirement' }
        if (-not $Windows11.DiskOk)      { $missing += 'system drive size' }

        $Windows11.Verdict = "Not eligible: $($missing -join ', ')"
        Add-Finding -Category 'Operating System' -Severity 'Warning' `
            -Title 'This machine cannot be upgraded to Windows 11 as it stands' `
            -Detail "Requirements not met: $($missing -join ', '). Note that TPM and Secure Boot are sometimes switched off in the BIOS rather than genuinely absent, which is worth checking before condemning the machine." `
            -Recommendation "This may be fixable with a settings change in the machine's firmware, or it may need new hardware. Both are worth pricing so you can decide which makes sense."
    }
}

# ---------------------------------------------------------------------------
# 3. Storage: capacity and physical health
# ---------------------------------------------------------------------------

$Disks         = New-Object System.Collections.ArrayList
$PhysicalDisks = New-Object System.Collections.ArrayList

Invoke-Check -Name 'LogicalDisks' -Body {
    $volumes = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction Stop
    foreach ($v in $volumes) {
        if (-not $v.Size -or $v.Size -eq 0) { continue }

        $freePct = [math]::Round(($v.FreeSpace / $v.Size) * 100, 1)
        [void]$Disks.Add([pscustomobject]@{
            Drive       = $v.DeviceID
            Label       = $v.VolumeName
            TotalGB     = [math]::Round($v.Size / 1GB, 1)
            FreeGB      = [math]::Round($v.FreeSpace / 1GB, 1)
            FreePercent = $freePct
        })

        if ($freePct -le $DiskFreePercentCritical) {
            Add-Finding -Category 'Storage' -Severity 'Critical' `
                -Title "Drive $($v.DeviceID) is almost full ($freePct% free)" `
                -Detail "$([math]::Round($v.FreeSpace/1GB,1)) GB free of $([math]::Round($v.Size/1GB,1)) GB." `
                -Recommendation "A full system drive causes update failures, crashes and data loss. Recommend a larger SSD or a clean-up and archive exercise."
        }
        elseif ($freePct -le $DiskFreePercentWarning) {
            Add-Finding -Category 'Storage' -Severity 'Warning' `
                -Title "Drive $($v.DeviceID) is running low on space ($freePct% free)" `
                -Detail "$([math]::Round($v.FreeSpace/1GB,1)) GB free of $([math]::Round($v.Size/1GB,1)) GB." `
                -Recommendation "Worth planning a storage upgrade before it becomes urgent."
        }
    }
}

Invoke-Check -Name 'PhysicalDisks' -Body {
    if (Get-Command Get-PhysicalDisk -ErrorAction SilentlyContinue) {
        foreach ($pd in Get-PhysicalDisk -ErrorAction Stop) {
            # Skip removable media. The technician's own USB stick was appearing
            # in client reports as a mystery 7.5 GB drive.
            if ("$($pd.BusType)" -in 'USB', 'SD', 'MMC') { continue }
            if ($pd.Size -lt 20GB) { continue }
            $rec = $null
            try { $rec = $pd | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue } catch { }

            [void]$PhysicalDisks.Add([pscustomobject]@{
                FriendlyName = $pd.FriendlyName
                MediaType    = "$($pd.MediaType)"
                SizeGB       = [math]::Round($pd.Size / 1GB, 1)
                HealthStatus = "$($pd.HealthStatus)"
                # Many consumer SATA controllers do not expose these counters and
                # return 0 rather than an error. Zero is treated as "not reported".
                Wear         = if ($rec -and $rec.Wear)         { $rec.Wear }         else { $null }
                TemperatureC = if ($rec -and $rec.Temperature)  { $rec.Temperature }  else { $null }
                PowerOnHours = if ($rec -and $rec.PowerOnHours) { $rec.PowerOnHours } else { $null }
            })

            if ("$($pd.HealthStatus)" -ne 'Healthy') {
                Add-Finding -Category 'Storage' -Severity 'Critical' `
                    -Title "Drive hardware is reporting a problem: $($pd.FriendlyName)" `
                    -Detail "Health status is '$($pd.HealthStatus)'." `
                    -Recommendation "Replace this drive as a priority and confirm a current backup exists before it fails completely."
            }

            if ($rec -and $rec.Wear -ne $null -and $rec.Wear -ge 80) {
                Add-Finding -Category 'Storage' -Severity 'Warning' `
                    -Title "SSD has used $($rec.Wear)% of its rated write life" `
                    -Detail "$($pd.FriendlyName)." `
                    -Recommendation "Plan a replacement before the drive reaches end of life."
            }

            if ("$($pd.MediaType)" -eq 'HDD') {
                Add-Finding -Category 'Storage' -Severity 'Info' `
                    -Title "Machine still uses a mechanical hard drive" `
                    -Detail "$($pd.FriendlyName), $([math]::Round($pd.Size/1GB,1)) GB." `
                    -Recommendation "Swapping to an SSD is the single biggest speed improvement available for this machine."
            }
        }
    }

    # SMART predictive failure, works on older hardware where Get-PhysicalDisk is thin
    $smart = Get-CimInstance -Namespace 'root\wmi' -ClassName 'MSStorageDriver_FailurePredictStatus' -ErrorAction SilentlyContinue
    foreach ($s in $smart) {
        if ($s.PredictFailure) {
            Add-Finding -Category 'Storage' -Severity 'Critical' `
                -Title 'A drive is predicting imminent failure (SMART)' `
                -Detail "Instance: $($s.InstanceName)." `
                -Recommendation "Back up immediately and replace the drive. SMART warnings usually precede failure by days or weeks."
        }
    }
}


# ---------------------------------------------------------------------------
# 3b. SMART health percentage
#
# This is what CrystalDiskInfo does. Windows exposes the raw SMART attribute
# table through WMI; it just never interprets it. Each attribute is 12 bytes:
#   0  attribute ID
#   1-2 flags
#   3  normalised value  (starts at 100, falls towards the failure threshold)
#   4  worst value ever seen
#   5-10 raw value
#
# The normalised value of the right attribute IS the health percentage.
# ---------------------------------------------------------------------------

$SmartDrives = New-Object System.Collections.ArrayList

Invoke-Check -Name 'SmartHealth' -Body {

    # Attributes that report REMAINING LIFE, in the order to prefer them.
    #
    # This replaces an earlier rule that took the lowest normalised value across
    # a basket of nine attributes and called it the health percentage. That was
    # our own invention, not what CrystalDiskInfo does, and it had two faults:
    # a single unrelated attribute could drag the figure down, and attributes
    # like "Pending sectors" are damage counters, not life meters.
    #
    # Confirmed against a Crucial CT240BX500SSD1: attribute 202 is what
    # CrystalDiskInfo shows as "Lifetime Remaining", and it read 96 while
    # CrystalDiskInfo displayed 96%. The name here matches CrystalDiskInfo's so
    # a client who checks with a free tool sees the same words.
    # Preference order as a plain ARRAY, and the names as a plain hashtable.
    #
    # NOT [ordered]@{}. With an ordered dictionary PowerShell treats an integer
    # index as a POSITION, not a key: $d[202] asks for item number 202 and
    # returns nothing. The health figure still came out right and the attribute
    # name came out blank, which is the kind of half-wrong that survives review.
    $lifePriority = @(202, 231, 233, 169, 173)

    $lifeAttributes = @{
        202 = 'Lifetime remaining'      # Crucial / Micron
        231 = 'Life left'               # vendor-specific - is temperature on some drives
        233 = 'Media wearout indicator' # Intel
        169 = 'Remaining life'
        173 = 'Wear levelling count'    # last resort: tracks wear, is not a life meter
    }

    # Damage counters. A non-zero RAW value here is worth raising even when the
    # normalised value still reads 100, because these counts only ever go up.
    # They are NOT used to calculate a health percentage.
    $damageAttributes = @{
        5   = 'Reallocated sectors'
        196 = 'Reallocation events'
        197 = 'Pending sectors'
        198 = 'Uncorrectable sectors'
        199 = 'UDMA CRC errors'
    }

    $smart   = @(Get-CimInstance -Namespace 'root\wmi' -ClassName MSStorageDriver_ATAPISmartData -ErrorAction SilentlyContinue)
    $ids     = @(Get-CimInstance -Namespace 'root\wmi' -ClassName MSStorageDriver_FailurePredictStatus -ErrorAction SilentlyContinue)

    # Thresholds. Never read before v2.9, and the single most important missing
    # piece: a normalised value means nothing on its own. On the drive that
    # prompted this, "Reallocated sectors" has a threshold of 10 and "Lifetime
    # remaining" has 1, while everything else is 0. A value of 9 against a
    # threshold of 0 is a healthy drive; against a threshold of 10 it is a
    # failing one. Comparing the bare number to 50 and 80 told us neither.
    $thresholds = @(Get-CimInstance -Namespace 'root\wmi' -ClassName MSStorageDriver_FailurePredictThresholds -ErrorAction SilentlyContinue)
    $physical = @()
    if (Get-Command Get-PhysicalDisk -ErrorAction SilentlyContinue) {
        $physical = @(Get-PhysicalDisk -ErrorAction SilentlyContinue |
                      Where-Object { "$($_.BusType)" -notin 'USB','SD','MMC' -and $_.Size -ge 20GB } |
                      Sort-Object DeviceId)
    }

    # Loop over whichever list is longer, not over the ATA table alone.
    #
    # MSStorageDriver_ATAPISmartData is the AT-A/ATAPI interface and returns
    # NOTHING for NVMe drives. Looping over $smart.Count meant that on an
    # NVMe-only machine the loop body never ran at all - so the NVMe branch
    # below, which reads wear directly and is the more reliable source anyway,
    # could never be reached. The whole health percentage silently produced
    # nothing and the report fell back to Windows' binary "Healthy".
    #
    # Pairing by index is still best-effort. On a multi-drive machine the two
    # lists are not guaranteed to be in the same order, which is why the drive
    # name always comes from the physical disk rather than the SMART instance.
    $driveCount = [Math]::Max($smart.Count, $physical.Count)

    for ($i = 0; $i -lt $driveCount; $i++) {

        $bytes = $null
        if ($i -lt $smart.Count) { $bytes = $smart[$i].VendorSpecific }

        $disk = $null
        if ($i -lt $physical.Count) { $disk = $physical[$i] }

        # Nothing to say about this slot at all
        if (-not $bytes -and -not $disk) { continue }

        $health       = $null
        $powerOnHours = $null
        $tempC        = $null
        $worstName    = $null
        $rawFlags     = New-Object System.Collections.ArrayList
        $seen         = New-Object System.Collections.ArrayList
        $breached     = New-Object System.Collections.ArrayList
        $lifeFound    = @{}

        # Thresholds come back in a parallel structure with the same 12-byte
        # layout, except that byte +1 of each entry is the threshold for that
        # attribute ID rather than a flag.
        $tMap = @{}
        if ($i -lt $thresholds.Count) {
            $tBytes = $thresholds[$i].VendorSpecific
            if ($tBytes -and $tBytes.Count -ge 30) {
                for ($a = 0; $a -lt 30; $a++) {
                    $o = 2 + ($a * 12)
                    if (($o + 1) -ge $tBytes.Count) { break }
                    $tid = [int]$tBytes[$o]
                    if ($tid -ne 0) { $tMap[$tid] = [int]$tBytes[$o + 1] }
                }
            }
        }

        # 30 attribute slots, starting 2 bytes in. Skipped entirely on NVMe,
        # where there is no table to read.
        if ($bytes -and $bytes.Count -ge 30) {
            for ($a = 0; $a -lt 30; $a++) {
                $o = 2 + ($a * 12)
                if (($o + 11) -ge $bytes.Count) { break }

                $id = [int]$bytes[$o]
                if ($id -eq 0) { continue }

                $value = [int]$bytes[$o + 3]
                $raw   = 0
                for ($r = 5; $r -ge 0; $r--) { $raw = ($raw * 256) + [int]$bytes[$o + $r + 5] }

                if ($id -eq 9)   { $powerOnHours = $raw }
                if ($id -eq 194) { $tempC = $raw -band 0xFF }

                $threshold = $null
                if ($tMap.ContainsKey($id)) { $threshold = $tMap[$id] }

                $isLife   = $lifeAttributes.ContainsKey($id)
                $isDamage = $damageAttributes.ContainsKey($id)

                if ($isLife -or $isDamage) {

                    $attrName = if ($isLife) { $lifeAttributes[$id] } else { $damageAttributes[$id] }

                    # Every relevant attribute is recorded with its threshold.
                    # Technician-facing, JSON only. IDs above about 170 are
                    # vendor-specific and the same number means different things
                    # on different drives, so when a health figure disagrees
                    # with CrystalDiskInfo this list shows exactly why.
                    [void]$seen.Add([pscustomobject]@{
                        Id         = $id
                        Name       = $attrName
                        Normalised = $value
                        Threshold  = $threshold
                        Raw        = $raw
                    })

                    # The test that actually matters, and the one CrystalDiskInfo
                    # applies: a value is only bad relative to its threshold.
                    if ($threshold -ne $null -and $threshold -gt 0 -and $value -le $threshold) {
                        [void]$breached.Add("$attrName ($value, fails at $threshold)")
                    }

                    # Life attributes are collected for selection after the loop,
                    # by priority rather than by lowest value.
                    if ($isLife -and $value -gt 0 -and $value -le 100) {
                        $lifeFound[$id] = $value
                    }

                    # Damage counters: the RAW count is what matters, not the
                    # normalised value, and it never contributes to health.
                    if ($isDamage -and $raw -gt 0) {
                        [void]$rawFlags.Add("$attrName`: $raw")
                    }
                }
            }

            # Pick the health figure from the FIRST life attribute this drive
            # actually reports, in the preference order above - not the lowest
            # across a basket. 231 is skipped when it looks like a temperature
            # rather than a percentage, because on some drives that ID is
            # temperature and a normalised 38 would read as a dying drive.
            foreach ($lid in $lifePriority) {
                if ($lifeFound.ContainsKey($lid)) {
                    $health    = $lifeFound[$lid]
                    $worstName = $lifeAttributes[$lid]
                    break
                }
            }
        }

        # NVMe drives report wear directly, and it is a better number than the
        # SMART table gives: a single vendor-neutral "percentage used" figure
        # rather than a basket of attributes whose meaning varies by maker.
        #
        # -ge 0, not -gt 0. Wear is the percentage USED, so a brand new drive
        # reports 0 - which is 100% health and the best possible answer. The old
        # -gt 0 test threw that away and left the drive with no health figure at
        # all, which read as "not measured" rather than "as good as new".
        # Only trusted on NVMe. On the Crucial SATA drive this was tested
        # against, Wear came back 0 while the SMART table showed 4% used - so
        # on SATA a 0 means "not reported", not "no wear". Believing it would
        # have printed 100% health for a drive with real wear on it, which is
        # the false all-clear this report must never give.
        $wearRead = $false
        $isNvme   = $false
        if ($disk -and "$($disk.BusType)" -eq 'NVMe') { $isNvme = $true }

        if ($disk) {
            try {
                $rel = $disk | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
                if ($isNvme -and $rel -and $rel.Wear -ne $null -and [int]$rel.Wear -ge 0 -and [int]$rel.Wear -le 100) {
                    $nvmeHealth = 100 - [int]$rel.Wear
                    $wearRead   = $true
                    if ($health -eq $null -or $nvmeHealth -lt $health) {
                        $health    = $nvmeHealth
                        $worstName = 'Drive wear'
                    }
                }
                if (-not $powerOnHours -and $rel.PowerOnHours) { $powerOnHours = $rel.PowerOnHours }
                if (-not $tempC -and $rel.Temperature)         { $tempC        = $rel.Temperature }
            } catch { }
        }

        $predictFail = $false
        if ($i -lt $ids.Count -and $ids[$i].PredictFailure) { $predictFail = $true }

        $entry = [pscustomobject]@{
            Name          = if ($disk) { $disk.FriendlyName } else { "Drive $($i + 1)" }
            MediaType     = if ($disk) { "$($disk.MediaType)" } else { $null }
            SizeGB        = if ($disk) { [math]::Round($disk.Size / 1GB, 1) } else { $null }
            HealthPercent = $health
            HealthBasedOn = $worstName
            HealthSource  = if ($wearRead) { 'NVMe wear counter' } elseif ($health -ne $null) { 'SMART attribute table' } else { $null }
            PowerOnHours  = $powerOnHours
            TemperatureC  = $tempC
            Concerns      = @($rawFlags)
            PredictFailure= $predictFail

            # Attributes that have fallen to or below the manufacturer's own
            # failure threshold. This is the drive maker's verdict, not ours.
            Breached      = @($breached)

            # Every watched attribute and its value. Technician-facing, never
            # shown to a client. This is what to look at when a drive reports a
            # health figure that disagrees with CrystalDiskInfo - it names the
            # attribute the number came from.
            AttributesSeen = @($seen)
        }
        [void]$SmartDrives.Add($entry)

        # ---- Findings ----
        $label = $entry.Name

        if ($predictFail) {
            Add-Finding -Category 'Storage' -Severity 'Critical' `
                -Title "The drive is warning that it is about to fail" `
                -Detail "$label is reporting an imminent failure warning from its own built-in monitoring." `
                -Recommendation "Back up immediately and replace the drive. These warnings usually come days or weeks before failure."
        }
        elseif ($breached.Count -gt 0) {
            # The manufacturer's own threshold has been crossed. This is a
            # stronger signal than any percentage we calculate, because the
            # number it fails against was set by the people who built the drive.
            Add-Finding -Category 'Storage' -Severity 'Critical' `
                -Title 'A drive has passed the point its maker considers safe' `
                -Detail "$label has reached the manufacturer's own failure threshold on: $($breached -join '; '). Drives are not expected to keep working reliably past this point." `
                -Recommendation "Back up now and replace the drive. This is the drive reporting its own condition, not an estimate."
        }
        elseif ($health -ne $null -and $health -le 50) {
            Add-Finding -Category 'Storage' -Severity 'Critical' `
                -Title "Drive health has dropped to $health%" `
                -Detail "$label reports $health% health remaining, based on $worstName. A new drive reads 100%." `
                -Recommendation "This drive is past half its usable life and should be replaced before it fails. Confirm a current backup in the meantime."
        }
        elseif ($health -ne $null -and $health -le 80) {
            Add-Finding -Category 'Storage' -Severity 'Warning' `
                -Title "Drive health is at $health%" `
                -Detail "$label reports $health% health remaining, based on $worstName." `
                -Recommendation "Not urgent, but worth budgeting for a replacement rather than waiting for it to fail unexpectedly."
        }

        if ($rawFlags.Count -gt 0 -and -not $predictFail -and ($health -eq $null -or $health -gt 80)) {
            Add-Finding -Category 'Storage' -Severity 'Warning' `
                -Title "Early signs of wear on a drive" `
                -Detail "$label is reporting: $($rawFlags -join '; '). The drive still reports as healthy overall, but these counts only ever go up." `
                -Recommendation "Worth watching each month. A rising count is the earliest warning of a drive going bad."
        }

        if ($tempC -ne $null -and $tempC -ge 60) {
            Add-Finding -Category 'Storage' -Severity 'Warning' `
                -Title "A drive is running hot at $tempC degrees" `
                -Detail "$label is at $tempC$([char]0x00B0)C. Sustained heat shortens drive life considerably." `
                -Recommendation "This is usually dust in the fans or vents. A physical clean-out normally fixes it and prevents heat damage."
        }
    }
}


# ---------------------------------------------------------------------------
# 3c. Recycle bin
#
# Read-only. The maintenance deliberately never empties the recycle bin, so the
# report tells the client what is sitting in it and what clearing it would
# recover. That is both an honest note and an easy conversation to have.
# ---------------------------------------------------------------------------

$RecycleBin = [pscustomobject]@{
    SizeGB      = 0
    SizeMB      = 0
    ItemCount   = 0
    OldestItem  = $null
    OldestDays  = $null
    DrivesFound = @()
}

Invoke-Check -Name 'RecycleBin' -Body {

    $totalBytes = 0
    $count      = 0
    $oldest     = $null
    $drives     = New-Object System.Collections.ArrayList

    foreach ($v in (Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction SilentlyContinue)) {
        $binRoot = Join-Path "$($v.DeviceID)\" '$Recycle.Bin'
        if (-not (Test-Path $binRoot)) { continue }

        $driveBytes = 0

        # One folder per user account, named after their SID
        $userBins = Get-ChildItem -LiteralPath $binRoot -Directory -Force -ErrorAction SilentlyContinue
        foreach ($ub in $userBins) {
            $items = Get-ChildItem -LiteralPath $ub.FullName -Recurse -File -Force -ErrorAction SilentlyContinue
            foreach ($i in $items) {
                # $I files are tiny pointers; $R files hold the actual data
                if ($i.Name -like '$I*') { continue }
                $driveBytes += $i.Length
                $count++
                if (-not $oldest -or $i.LastWriteTime -lt $oldest) { $oldest = $i.LastWriteTime }
            }
        }

        if ($driveBytes -gt 0) {
            [void]$drives.Add([pscustomobject]@{
                Drive  = $v.DeviceID
                SizeGB = [math]::Round($driveBytes / 1GB, 2)
            })
        }
        $totalBytes += $driveBytes
    }

    $RecycleBin.SizeGB      = [math]::Round($totalBytes / 1GB, 2)
    $RecycleBin.SizeMB      = [math]::Round($totalBytes / 1MB, 1)
    $RecycleBin.ItemCount   = $count
    $RecycleBin.DrivesFound = @($drives)

    if ($oldest) {
        $RecycleBin.OldestItem = $oldest.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $RecycleBin.OldestDays = [math]::Round(((Get-Date) - $oldest).TotalDays)
    }

    # Only worth raising when there is enough there to be worth recovering
    if ($RecycleBin.SizeGB -ge $RecycleBinMinGB) {
        $ageNote = ''
        if ($RecycleBin.OldestDays -ge 365) {
            $years = [math]::Round($RecycleBin.OldestDays / 365, 1)
            $ageNote = " The oldest has been sitting there for about $years years."
        }
        elseif ($RecycleBin.OldestDays -ge 60) {
            $ageNote = " The oldest has been there for $($RecycleBin.OldestDays) days."
        }

        Add-Finding -Category 'Storage' -Severity 'Info' `
            -Title "$($RecycleBin.SizeGB) GB of deleted files are still taking up space" `
            -Detail "The recycle bin holds $($RecycleBin.ItemCount) deleted files using $($RecycleBin.SizeGB) GB.$ageNote We never empty it during maintenance, in case something was deleted by mistake and is still needed." `
            -Recommendation "Emptying it would recover $($RecycleBin.SizeGB) GB. Worth looking through it first in case anything in there is still wanted, then it can be cleared safely."
    }
}

# ---------------------------------------------------------------------------
# 3b. Temporary files
#     Measurement only. Nothing is deleted here. This reports what a maintenance
#     run would recover, using the same folders and the same age cutoff, so the
#     figure the client is shown and the figure actually recovered agree.
# ---------------------------------------------------------------------------

$TempFiles = [pscustomobject]@{
    TotalMB       = 0
    TotalGB       = 0
    FileCount     = 0
    OlderThanDays = $TempFileAgeDays
    Locations     = @()
}

Invoke-Check -Name 'TempFiles' -Body {

    $cutoff     = (Get-Date).AddDays(-$TempFileAgeDays)
    $totalBytes = 0
    $count      = 0
    $locs       = New-Object System.Collections.ArrayList

    $targets = New-Object System.Collections.ArrayList
    [void]$targets.Add([pscustomobject]@{
        Name = 'Windows temporary folder'
        Path = (Join-Path $env:SystemRoot 'Temp')
    })

    $usersRoot = Join-Path $env:SystemDrive 'Users'
    if (Test-Path $usersRoot) {
        foreach ($u in (Get-ChildItem -LiteralPath $usersRoot -Directory -Force -ErrorAction SilentlyContinue)) {
            $p = Join-Path $u.FullName 'AppData\Local\Temp'
            if (Test-Path $p) {
                [void]$targets.Add([pscustomobject]@{ Name = "Temporary files for $($u.Name)"; Path = $p })
            }
        }
    }

    foreach ($t in $targets) {
        $bytes = 0
        $n     = 0
        # Files still in use are skipped by -ErrorAction SilentlyContinue rather
        # than counted, which keeps the promise conservative.
        $items = Get-ChildItem -LiteralPath $t.Path -Recurse -File -Force -ErrorAction SilentlyContinue |
                 Where-Object { $_.LastWriteTime -lt $cutoff }
        foreach ($i in $items) { $bytes += $i.Length; $n++ }

        if ($n -gt 0) {
            [void]$locs.Add([pscustomobject]@{
                Name      = $t.Name
                SizeMB    = [math]::Round($bytes / 1MB, 1)
                FileCount = $n
            })
        }
        $totalBytes += $bytes
        $count      += $n
    }

    $TempFiles.TotalMB   = [math]::Round($totalBytes / 1MB, 1)
    $TempFiles.TotalGB   = [math]::Round($totalBytes / 1GB, 2)
    $TempFiles.FileCount = $count
    $TempFiles.Locations = @($locs)

    # Info, never a warning. Temporary files build up on every working machine.
    # Raising this as a fault would fire on the whole estate and devalue the
    # genuine findings sitting next to it.
    if ($TempFiles.TotalMB -ge $TempFileWarnMB) {
        $sizeText = "$($TempFiles.TotalMB) MB"
        if ($TempFiles.TotalGB -ge 1) { $sizeText = "$($TempFiles.TotalGB) GB" }

        Add-Finding -Category 'Storage' -Severity 'Info' `
            -Title "$sizeText of temporary files can be safely cleared" `
            -Detail "Windows and the programs on this machine have left behind $($TempFiles.FileCount) working files that have not been touched in over $TempFileAgeDays days. They serve no purpose now and are safe to remove." `
            -Recommendation "Clearing these recovers $sizeText without affecting any documents or settings. It is part of a routine maintenance visit."
    }
}

# ---------------------------------------------------------------------------
# 3c. Drive optimisation
#     Solid state drives need TRIM; mechanical drives need defragmenting, and
#     only once fragmentation is high enough to be worth the time. Read-only:
#     defrag.exe /A analyses and reports without moving anything.
# ---------------------------------------------------------------------------

$DriveOptimisation = [pscustomobject]@{
    LastRun       = $null
    LastRunDays   = $null
    LastRunSource = $null
    NeverRun      = $false
    Volumes       = @()
}

Invoke-Check -Name 'DriveOptimisation' -Body {

    # When did Windows last optimise anything? Two sources, because neither is
    # dependable alone. The event log is the more accurate - it records manual
    # runs as well as scheduled ones - but is often empty. The scheduled task
    # always exists, but reports 30 November 1999 when it has never run. That is
    # a sentinel, not a date, and must never reach a client report.
    $lastRun = $null
    $source  = $null

    try {
        $ev = Get-WinEvent -FilterHashtable @{
                  LogName      = 'Application'
                  ProviderName = 'Microsoft-Windows-Defrag'
              } -MaxEvents 25 -ErrorAction Stop
        $done = @($ev | Where-Object { $_.Id -eq 258 } | Sort-Object TimeCreated -Descending)
        if ($done.Count -gt 0) {
            $lastRun = $done[0].TimeCreated
            $source  = 'Windows event log'
        }
    }
    catch { }

    if (-not $lastRun) {
        try {
            $task = Get-ScheduledTask -TaskPath '\Microsoft\Windows\Defrag\' `
                                      -TaskName 'ScheduledDefrag' -ErrorAction Stop
            $info = $task | Get-ScheduledTaskInfo -ErrorAction Stop

            # 267011 is 0x41303, "the task has never run". Its LastRunTime is
            # the 1999 placeholder, so both are rejected together.
            if ($info.LastTaskResult -ne 267011 -and $info.LastRunTime -and $info.LastRunTime.Year -gt 2000) {
                $lastRun = $info.LastRunTime
                $source  = 'Windows maintenance schedule'
            }
            else {
                $DriveOptimisation.NeverRun = $true
            }
        }
        catch { }
    }

    if ($lastRun) {
        $DriveOptimisation.LastRun       = $lastRun.ToString('yyyy-MM-ddTHH:mm:ssZ')
        $DriveOptimisation.LastRunDays   = [math]::Round(((Get-Date) - $lastRun).TotalDays)
        $DriveOptimisation.LastRunSource = $source
    }

    # What each drive physically is, and whether it needs work.
    $vols       = New-Object System.Collections.ArrayList
    $fragmented = New-Object System.Collections.ArrayList

    foreach ($v in (Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction SilentlyContinue)) {
        $letter = $v.DeviceID.TrimEnd(':')
        $media  = 'Unknown'

        try {
            $part = Get-Partition -DriveLetter $letter -ErrorAction Stop
            $pd   = @(Get-PhysicalDisk -ErrorAction Stop |
                      Where-Object { $_.DeviceId -eq "$($part.DiskNumber)" })
            if ($pd.Count -gt 0) { $media = "$($pd[0].MediaType)" }
        }
        catch { }

        # defrag.exe needs administrator rights. Without them it returns nothing
        # at all, so the result is recorded as unknown rather than as zero.
        $frag = $null
        if ($IsAdmin -and $media -eq 'HDD') {
            try {
                $analysis = (& defrag.exe "$($v.DeviceID)" /A /V 2>&1 | Out-String) -replace "`0", ''
                if ($analysis -match 'Total fragmented space:\s*(\d+)\s*%') { $frag = [int]$Matches[1] }
            }
            catch { }
        }

        [void]$vols.Add([pscustomobject]@{
            Drive           = $v.DeviceID
            MediaType       = $media
            FragmentPercent = $frag
        })

        if ($frag -ne $null -and $frag -ge $FragmentationWarnPercent) {
            [void]$fragmented.Add("$($v.DeviceID) at $frag%")
        }
    }

    $DriveOptimisation.Volumes = @($vols)

    $anyMechanical = @($vols | Where-Object { $_.MediaType -eq 'HDD' }).Count -gt 0
    $anySolidState = @($vols | Where-Object { $_.MediaType -eq 'SSD' }).Count -gt 0

    if ($fragmented.Count -gt 0) {
        Add-Finding -Category 'Storage' -Severity 'Warning' `
            -Title 'A drive is fragmented enough to be slowing the machine down' `
            -Detail "Files on $($fragmented -join ', ') have become scattered across the disk, so the machine has to work harder to open them. This affects mechanical drives only." `
            -Recommendation "Defragmenting tidies the files back into order and usually gives a noticeable speed improvement. It is part of a routine maintenance visit."
    }

    if ($DriveOptimisation.NeverRun) {
        $what = 'optimised'
        if ($anySolidState -and -not $anyMechanical) { $what = 'given their routine solid state tidy-up' }

        Add-Finding -Category 'Storage' -Severity 'Info' `
            -Title "This machine's drives have never been $what" `
            -Detail "Windows keeps a record of routine drive housekeeping and there is no sign it has ever run here. Drives gradually slow down without it." `
            -Recommendation "A one-off optimisation, then a monthly routine, keeps the machine at the speed it was bought for."
    }
    elseif ($DriveOptimisation.LastRunDays -ne $null -and $DriveOptimisation.LastRunDays -ge $OptimiseStaleDays) {
        Add-Finding -Category 'Storage' -Severity 'Info' `
            -Title "Routine drive housekeeping last ran $($DriveOptimisation.LastRunDays) days ago" `
            -Detail "Windows tidies drives periodically to keep them running at full speed. On this machine that has not happened for some months." `
            -Recommendation "Worth bringing into a monthly maintenance routine so it stops being forgotten."
    }
    elseif ($DriveOptimisation.LastRunDays -ne $null) {
        Add-Finding -Category 'Storage' -Severity 'OK' `
            -Title 'Drive housekeeping is up to date' `
            -Detail "Windows last tidied the drives $($DriveOptimisation.LastRunDays) days ago." `
            -Recommendation ''
    }
}

# ---------------------------------------------------------------------------
# 4. Backup evidence
#    There is no universal way to prove a backup ran, so this gathers evidence
#    and reports honestly when none is found. That "no evidence" line is often
#    the most valuable sentence in the whole report.
# ---------------------------------------------------------------------------

$Backup = [pscustomobject]@{
    EvidenceFound       = $false
    LastWindowsBackup   = $null
    BackupSoftware      = @()
    OneDriveSyncFolders = @()
    PathChecks          = @()
}

Invoke-Check -Name 'Backup' -Body {

    # a) Windows Server Backup / Windows Backup event log
    try {
        $ev = Get-WinEvent -FilterHashtable @{
            LogName   = 'Microsoft-Windows-Backup'
            StartTime = (Get-Date).AddDays(-90)
        } -MaxEvents 20 -ErrorAction Stop | Sort-Object TimeCreated -Descending | Select-Object -First 1
        if ($ev) {
            $Backup.LastWindowsBackup = $ev.TimeCreated
            $Backup.EvidenceFound = $true
        }
    } catch { }

    # b) Known third-party backup agents
    $knownAgents = 'Veeam','Acronis','Macrium','Backblaze','Carbonite','Datto','ShadowProtect','Altaro','Nakivo','CrashPlan','Redstor','Synology'
    $services = Get-Service -ErrorAction SilentlyContinue
    foreach ($agent in $knownAgents) {
        $match = $services | Where-Object { $_.DisplayName -like "*$agent*" }
        if ($match) {
            $running = ($match | Where-Object { $_.Status -eq 'Running' }).Count
            $Backup.BackupSoftware += [pscustomobject]@{
                Product        = $agent
                ServicesFound  = $match.Count
                ServicesRunning= $running
            }
            if ($running -gt 0) { $Backup.EvidenceFound = $true }
            # Veeam is skipped here: the backup verification below has its own,
            # better Veeam check that also reads the last job's result. Reporting
            # it from both places gave two Criticals for one stopped service.
            elseif ($agent -ne 'Veeam') {
                Add-Finding -Category 'Backup' -Severity 'Critical' `
                    -Title "$agent backup software is installed but not running" `
                    -Detail "The service exists on this machine but is currently stopped." `
                    -Recommendation "Backup software that is not running provides no protection at all. This needs investigation."
            }
        }
    }

    # c) OneDrive / cloud sync as partial protection
    $od = Get-ChildItem "$env:SystemDrive\Users" -Directory -ErrorAction SilentlyContinue |
          ForEach-Object { Get-ChildItem $_.FullName -Directory -Filter 'OneDrive*' -ErrorAction SilentlyContinue }
    if ($od) {
        $Backup.OneDriveSyncFolders = @($od | Select-Object -ExpandProperty FullName)
    }

    # d) Caller-supplied backup destinations
    foreach ($p in $BackupPaths) {
        $entry = [pscustomobject]@{ Path = $p; Exists = $false; NewestFile = $null; AgeDays = $null }
        if (Test-Path $p) {
            $entry.Exists = $true
            $newest = Get-ChildItem $p -Recurse -File -ErrorAction SilentlyContinue |
                      Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($newest) {
                $entry.NewestFile = $newest.FullName
                # Whole days. "158.8 days" is a number nobody says, and it reached
                # the client email as a finding title.
                $entry.AgeDays    = [math]::Round(((Get-Date) - $newest.LastWriteTime).TotalDays, 0)
                if ($entry.AgeDays -le $BackupStaleDaysWarning) { $Backup.EvidenceFound = $true }
                else {
                    Add-Finding -Category 'Backup' -Severity 'Critical' `
                        -Title "Backup location has not changed in $($entry.AgeDays) days" `
                        -Detail "Newest file in $p is $($entry.AgeDays) days old." `
                        -Recommendation "The backup job appears to have stopped running. This should be treated as urgent."
                }
            }
        }
        $Backup.PathChecks += $entry
    }

    # The overall verdict - "backup in place" or "no backup" - is NOT reached
    # here. It used to be, and the file search that finds actual backup files
    # had not run yet. See "Backup verdict" below section 4b.
}


# ---------------------------------------------------------------------------
# 4b. Backup verification
#
# The usual failure with external-drive backup is not the software. It is that
# nobody plugged the drive in. So rather than trusting that a product is
# installed, this looks for actual backup files and how old they are - which
# works whatever product the client uses.
#
# It also remembers where backups were found last month. If that location has
# vanished, the drive is unplugged and the client needs telling.
# ---------------------------------------------------------------------------

$BackupCheck = [pscustomobject]@{
    NewestBackupFile   = $null
    NewestBackupAgeDays= $null
    NewestBackupPath   = $null
    TotalBackupSizeGB  = $null
    BackupFileCount    = 0
    LocationsFound     = @()
    KnownLocationMissing = $null
    VeeamInstalled     = $false
    VeeamLastResult    = $null
    VeeamLastRun       = $null
}

Invoke-Check -Name 'BackupVerification' -Body {

    # Files that only ever exist because something made a backup
    $backupPatterns = '*.vbk','*.vib','*.vbm','*.mrimg','*.tib','*.tibx','*.spf','*.bkf','*.wim','*.vhdx','*.dblock.zip'

    # Folders worth looking in. Scanning an entire drive would take far too long.
    $searchRoots = New-Object System.Collections.ArrayList

    foreach ($v in (Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=2 OR DriveType=3 OR DriveType=4' -ErrorAction SilentlyContinue)) {
        $root = "$($v.DeviceID)\"
        if ($v.DeviceID -eq $env:SystemDrive) {
            # Only the standard Windows image location on the system drive
            $wib = Join-Path $root 'WindowsImageBackup'
            if (Test-Path $wib) { [void]$searchRoots.Add($wib) }
            continue
        }
        [void]$searchRoots.Add($root)
    }

    # Network shares the client may back up to
    foreach ($p in $BackupPaths) { if (Test-Path $p) { [void]$searchRoots.Add($p) } }

    $newest    = $null
    $totalSize = 0
    $count     = 0
    $locations = New-Object System.Collections.ArrayList

    foreach ($root in $searchRoots) {
        foreach ($pattern in $backupPatterns) {
            $hits = Get-ChildItem -LiteralPath $root -Filter $pattern -File -Recurse -Depth 4 -Force -ErrorAction SilentlyContinue

            # Two file types match backups AND things that are not backups.
            #
            # .wim - every Windows installation USB stick carries
            #   sources\boot.wim and sources\install.wim. A technician's install
            #   stick plugged into the client's machine was reported as "The last
            #   backup was 159 days ago" - a Critical about a backup that never
            #   existed, in the email sent to the client. Anything in a folder
            #   called "sources" is installation media.
            #
            # .vhdx - Hyper-V virtual machines and WSL both use it, and running
            #   VM disks are written to constantly. A Hyper-V host with no backup
            #   at all would have read "A recent backup was found and checked" -
            #   a false all-clear on exactly the machine where it matters most.
            #   Windows' own backup keeps its .vhdx inside a WindowsImageBackup
            #   folder, so only those count.
            $hits = @($hits | Where-Object {
                $ext = $_.Extension.ToLower()
                if ($ext -eq '.wim'  -and $_.Directory.Name -eq 'sources') { return $false }
                if ($ext -eq '.vhdx' -and $_.FullName -notmatch '\\WindowsImageBackup\\') { return $false }
                return $true
            })

            foreach ($h in $hits) {
                $count++
                $totalSize += $h.Length
                if (-not $newest -or $h.LastWriteTime -gt $newest.LastWriteTime) { $newest = $h }
                if ($locations -notcontains $h.DirectoryName) { [void]$locations.Add($h.DirectoryName) }
            }
        }
    }

    $BackupCheck.BackupFileCount = $count
    $BackupCheck.LocationsFound  = @($locations)
    if ($totalSize -gt 0) { $BackupCheck.TotalBackupSizeGB = [math]::Round($totalSize / 1GB, 1) }

    if ($newest) {
        $age = [math]::Round(((Get-Date) - $newest.LastWriteTime).TotalDays, 0)
        $BackupCheck.NewestBackupFile    = $newest.Name
        $BackupCheck.NewestBackupPath    = $newest.DirectoryName
        $BackupCheck.NewestBackupAgeDays = $age

        # Only a RECENT backup is evidence of a working one. This used to be set
        # for any backup file at all, so a 159-day-old image counted as "backup
        # activity detected" in the same report that called it Critical.
        if ($age -le $BackupStaleDaysWarning) { $Backup.EvidenceFound = $true }

        if ($age -le $BackupStaleDaysWarning) {
            Add-Finding -Category 'Backup' -Severity 'OK' `
                -Title 'A recent backup was found and checked' `
                -Detail "The most recent backup is $age days old, stored in $($newest.DirectoryName). Total backup data: $($BackupCheck.TotalBackupSizeGB) GB."
        }
        elseif ($age -le 31) {
            Add-Finding -Category 'Backup' -Severity 'Warning' `
                -Title "The most recent backup is $age days old" `
                -Detail "A backup exists in $($newest.DirectoryName), but nothing new has been written for $age days. Anything created or changed since then is not protected." `
                -Recommendation "Usually the backup drive has been left unplugged. Worth checking it is connected and the schedule is still running."
        }
        else {
            Add-Finding -Category 'Backup' -Severity 'Critical' `
                -Title "The last backup was $age days ago" `
                -Detail "Backup files exist in $($newest.DirectoryName) but the newest is $age days old. Everything created or changed since then would be lost." `
                -Recommendation "This needs attention now. Either the backup drive has been disconnected for a month or the backup job has stopped running."
        }
    }

    # ---- Has a previously working backup location disappeared? ----
    $statePath = Join-Path $OutputPath 'backup-location.txt'
    if ($newest) {
        # No BOM - this path is read back next month and compared. A byte order
        # mark would make it never match, so the "backup drive is unplugged"
        # check would silently never fire.
        try {
            [System.IO.File]::WriteAllText($statePath, $newest.DirectoryName,
                (New-Object System.Text.UTF8Encoding($false)))
        } catch { }
    }
    elseif (Test-Path $statePath) {
        # Trim the BOM too, in case this file was written by an older version
        $known = (Get-Content $statePath -Raw -ErrorAction SilentlyContinue)
        if ($known) { $known = $known.Trim().TrimStart([char]0xFEFF) }
        if ($known -and -not (Test-Path $known)) {
            $BackupCheck.KnownLocationMissing = $known
            Add-Finding -Category 'Backup' -Severity 'Critical' `
                -Title 'The backup drive is not connected' `
                -Detail "Backups were previously being written to $known, but that location cannot be reached. The drive appears to be unplugged." `
                -Recommendation "Plug the backup drive back in. Nothing has been backed up since it was removed."
        }
    }

    # ---- Veeam Agent, if it is what they are using ----
    $veeamSvc = Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Veeam*' }
    if ($veeamSvc) {
        $BackupCheck.VeeamInstalled = $true

        $running = @($veeamSvc | Where-Object { $_.Status -eq 'Running' })
        if ($running.Count -eq 0) {
            Add-Finding -Category 'Backup' -Severity 'Critical' `
                -Title 'The backup software is installed but not running' `
                -Detail "Veeam is present on this machine but its service is stopped, so no backups can take place." `
                -Recommendation "Backup software that is not running offers no protection at all. Needs investigating."
        }

        foreach ($logName in @('Veeam Agent','Veeam Endpoint Backup')) {
            try {
                $ev = Get-WinEvent -FilterHashtable @{ LogName = $logName; StartTime = (Get-Date).AddDays(-45) } `
                                   -MaxEvents 60 -ErrorAction Stop |
                      Where-Object { $_.Message -match 'backup job|Backup job' } |
                      Sort-Object TimeCreated -Descending | Select-Object -First 1
                if ($ev) {
                    $BackupCheck.VeeamLastRun    = $ev.TimeCreated.ToString('yyyy-MM-ddTHH:mm:ssZ')
                    $BackupCheck.VeeamLastResult = switch ($ev.LevelDisplayName) {
                        'Error'       { 'Failed' }
                        'Warning'     { 'Finished with warnings' }
                        default       { 'Succeeded' }
                    }
                    if ($ev.LevelDisplayName -eq 'Error') {
                        Add-Finding -Category 'Backup' -Severity 'Critical' `
                            -Title 'The last backup failed' `
                            -Detail "The backup software reported a failure on $($ev.TimeCreated.ToString('d MMMM yyyy'))." `
                            -Recommendation "A failed backup is the same as no backup. This needs looking at before the next one is due."
                    }
                    break
                }
            } catch { }
        }
    }

}

# ---------------------------------------------------------------------------
# 4c. Backup verdict
#
# Reached ONCE, after every piece of backup evidence is in: the event log, the
# backup agents, the caller's backup paths, the file search, the missing-drive
# check and Veeam's job results.
#
# This used to be decided at the end of section 4, before the file search in
# 4b had run. So a machine whose only backup was a set of image files on an
# external drive was told "No evidence of a working backup" - Critical - and
# then, a few lines later, "A recent backup was found and checked". A false
# Critical on a healthy machine, doing exactly what we would advise. A machine
# with OLD backup files got "no actual backup" beside "the last backup was 159
# days ago", both Critical, contradicting each other.
#
# The rule now: the generic "no backup" line only speaks when nothing more
# specific has already been said. A stale backup, an unplugged drive, a stopped
# agent or a failed job is always more useful than the generic line, and saying
# both reads as the report arguing with itself.
# ---------------------------------------------------------------------------

Invoke-Check -Name 'BackupVerdict' -Body {

    # Anything already raised about backup that is a problem, not a pass.
    $specific = @($Findings | Where-Object { $_.Category -eq 'Backup' -and $_.Severity -in 'Critical','Warning' })

    $recentFileChecked = ($BackupCheck.NewestBackupAgeDays -ne $null -and
                          $BackupCheck.NewestBackupAgeDays -le $BackupStaleDaysWarning)

    if ($Backup.EvidenceFound) {
        # The file search already says this more precisely when it found the
        # files itself, so the generic line is only for evidence found elsewhere
        # - a running agent, or the Windows backup log.
        if (-not $recentFileChecked) {
            Add-Finding -Category 'Backup' -Severity 'OK' `
                -Title 'Backup activity detected' `
                -Detail "Evidence of backup software running or a recent backup was found."
        }
    }
    elseif ($specific.Count -gt 0) {
        # A backup exists or existed, and something more precise has already
        # been said about what is wrong with it. Nothing to add.
    }
    elseif ($Backup.OneDriveSyncFolders.Count -gt 0) {
        # Cloud sync is not backup, and saying so plainly is one of the most
        # useful sentences in the whole report.
        Add-Finding -Category 'Backup' -Severity 'Critical' `
            -Title 'This machine has cloud file sync, but no actual backup' `
            -Detail "OneDrive is syncing files, which protects against hardware failure but not against accidental deletion, ransomware or a corrupted file - those changes sync straight to the cloud copy as well." `
            -Recommendation "A proper backup keeps older copies of files that can be restored after a mistake, a ransomware attack or a corruption, where cloud sync simply copies the damage across instead. A managed backup service is the usual answer."
    }
    else {
        Add-Finding -Category 'Backup' -Severity 'Critical' `
            -Title 'No evidence of a working backup was found on this machine' `
            -Detail "No backup software, scheduled backup history or recent backup files were detected." `
            -Recommendation "If there is anything on this machine the business could not afford to lose, a proper backup should be put in place as a priority."
    }

    # A large empty second drive is both a finding and an easy piece of free value
    # to hand the client - but only when there is no working backup already.
    $spare = @($Disks | Where-Object { $_.Drive -ne $env:SystemDrive -and $_.TotalGB -ge 100 -and $_.FreePercent -ge 95 })
    if ($spare.Count -gt 0 -and -not $Backup.EvidenceFound) {
        Add-Finding -Category 'Backup' -Severity 'Info' `
            -Title "Drive $($spare[0].Drive) is almost entirely unused ($($spare[0].TotalGB) GB)" `
            -Detail "There is a second drive in this machine sitting empty." `
            -Recommendation "This drive could be used as a local backup destination without buying any hardware. It works best as a first layer alongside an offsite backup, not instead of one."
    }
}

# ---------------------------------------------------------------------------
# 5. Patching
# ---------------------------------------------------------------------------

$Updates = [pscustomobject]@{
    LastInstalledDate = $null
    LastInstalledName = $null
    PendingCount      = $null
    RebootPending     = $false
    RebootReasons     = @()
}

Invoke-Check -Name 'RebootPending' -Body {
    $reasons = @()
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') {
        $reasons += 'Component servicing'
    }
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') {
        $reasons += 'Windows Update'
    }
    $pfr = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -ErrorAction SilentlyContinue).PendingFileRenameOperations
    if ($pfr) { $reasons += 'Pending file rename' }

    if ($reasons.Count -gt 0) {
        $Updates.RebootPending = $true
        $Updates.RebootReasons = $reasons

        # A pending file rename on its own is routine - browser and application
        # updaters set it constantly. Only the servicing and update flags mean
        # security patches are genuinely waiting.
        $realUpdateReboot = @($reasons | Where-Object { $_ -in 'Component servicing', 'Windows Update' }).Count -gt 0

        if ($realUpdateReboot) {
            # The Detail used to be "Reason: Component servicing, Windows Update,
            # Pending file rename" - Windows' internal names for where it keeps
            # the restart flags, printed straight into the client's report on every
            # machine waiting for a restart. It says what it means for them now.
            # The technical reasons are still recorded in Updates.RebootReasons for
            # the technician, who is the one reader they mean anything to.
            Add-Finding -Category 'Updates' -Severity 'Warning' `
                -Title 'This machine is waiting for a restart to finish installing updates' `
                -Detail "Windows has installed updates that only take effect once the machine restarts. Until then, the protection they add is not yet in place." `
                -Recommendation "Restart the machine outside working hours to finish the job."
        }
        # No finding for a pending file rename on its own. Browser and
        # application updaters queue these constantly, they are present on every
        # working machine, and a restart does not clear them for long - the
        # machine that prompted this had 20 of them minutes after rebooting.
        #
        # Reporting it told every client, every month, that a restart was queued,
        # which reads as an outstanding task that can never be completed. A
        # finding that fires on every machine is the check being wrong, not the
        # estate. The reasons stay in RebootReasons for the technician.
    }
}

Invoke-Check -Name 'WindowsUpdate' -Body {
    $session  = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()

    $total = $searcher.GetTotalHistoryCount()
    if ($total -gt 0) {
        $history = $searcher.QueryHistory(0, [Math]::Min($total, 50)) |
                   Where-Object { $_.ResultCode -eq 2 } |
                   Sort-Object Date -Descending | Select-Object -First 1
        if ($history) {
            $Updates.LastInstalledDate = $history.Date
            $Updates.LastInstalledName = $history.Title
            $days = [math]::Round(((Get-Date) - $history.Date).TotalDays, 0)
            if ($days -gt 45) {
                Add-Finding -Category 'Updates' -Severity 'Critical' `
                    -Title "No Windows updates have installed in $days days" `
                    -Detail "Last successful update: $($history.Date.ToString('d MMMM yyyy'))." `
                    -Recommendation "Update delivery has stopped working on this machine. It should be investigated as a security priority."
            }
        }
    }

    if (-not $SkipWindowsUpdate) {
        try {
            $result = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")
            $Updates.PendingCount = $result.Updates.Count
            if ($result.Updates.Count -ge 10) {
                Add-Finding -Category 'Updates' -Severity 'Warning' `
                    -Title "$($result.Updates.Count) updates are outstanding" `
                    -Detail "These have been offered by Microsoft but not yet installed." `
                    -Recommendation "Schedule a patching window outside business hours."
            }
        }
        catch {
            Add-Finding -Category 'Updates' -Severity 'Info' `
                -Title 'Could not check for outstanding updates' `
                -Detail "This machine could not reach the Microsoft update service at the time of the check. Often a temporary network issue, but it can also mean the update service itself is broken." `
                -Recommendation "If this appears again next month, the update service on this machine needs attention."
            throw
        }
    }
}

# ---------------------------------------------------------------------------
# 6. Security posture
# ---------------------------------------------------------------------------

$Security = [pscustomobject]@{
    ProtectionState       = 'Unknown'
    ActiveAntivirus       = $null
    DefenderEnabled       = $null
    RealTimeProtection    = $null
    SignatureAgeDays      = $null
    LastQuickScanDays     = $null
    ThirdPartyAntivirus   = @()
    FirewallProfiles      = @()
    LocalAdministrators   = @()
    NonExpiringPasswords  = @()
    BitLockerVolumes      = @()
}

# IMPORTANT: registered antivirus products are enumerated FIRST, because Defender
# correctly switches itself off when a third-party product takes over. Judging
# Defender in isolation produces a false "machine is unprotected" alarm.

Invoke-Check -Name 'RegisteredAntivirus' -Body {
    $av = Get-CimInstance -Namespace 'root\SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction SilentlyContinue
    foreach ($a in $av) {
        # productState is a bit field; the middle byte indicates whether it is enabled
        $state   = '{0:x6}' -f $a.productState
        $enabled = ($state.Substring(2,2) -match '^1[0-9]$')
        $outOfDate = ($state.Substring(4,2) -ne '00')

        $Security.ThirdPartyAntivirus += [pscustomobject]@{
            Name      = $a.displayName
            Enabled   = $enabled
            OutOfDate = $outOfDate
        }

        if ($enabled -and $a.displayName -notmatch 'Windows Defender|Microsoft Defender') {
            $Security.ActiveAntivirus = $a.displayName
        }
    }
}

Invoke-Check -Name 'Defender' -Body {
    if (Get-Command Get-MpComputerStatus -ErrorAction SilentlyContinue) {
        $mp = Get-MpComputerStatus -ErrorAction Stop
        $Security.DefenderEnabled    = $mp.AMServiceEnabled
        $Security.RealTimeProtection = $mp.RealTimeProtectionEnabled

        # Defender returns 0xFFFFFFFF (4294967295) for "not applicable" when it is
        # passive. Anything absurd is treated as unknown rather than reported.
        if ($mp.AntivirusSignatureAge -lt 3650) { $Security.SignatureAgeDays  = $mp.AntivirusSignatureAge }
        if ($mp.QuickScanAge          -lt 3650) { $Security.LastQuickScanDays = $mp.QuickScanAge }
    }
}

Invoke-Check -Name 'AntivirusVerdict' -Body {
    $activeThirdParty = @($Security.ThirdPartyAntivirus | Where-Object {
        $_.Enabled -and $_.Name -notmatch 'Windows Defender|Microsoft Defender'
    })

    if ($Security.RealTimeProtection -eq $true) {
        $Security.ProtectionState = 'Protected (Microsoft Defender)'
        Add-Finding -Category 'Security' -Severity 'OK' `
            -Title 'Virus protection is active' `
            -Detail "Microsoft Defender real-time protection is running."

        if ($Security.SignatureAgeDays -ne $null -and $Security.SignatureAgeDays -ge $SignatureAgeDaysWarning) {
            Add-Finding -Category 'Security' -Severity 'Warning' `
                -Title "Virus definitions are $($Security.SignatureAgeDays) days old" `
                -Detail "Definitions should normally update daily." `
                -Recommendation "Investigate why definition updates are not reaching this machine."
        }
    }
    elseif ($activeThirdParty.Count -gt 0) {
        # Defender's own signature and scan ages are stale and meaningless once
        # another product has taken over. Do not carry them into the report.
        $Security.SignatureAgeDays  = $null
        $Security.LastQuickScanDays = $null

        $names = ($activeThirdParty | Select-Object -ExpandProperty Name) -join ', '
        $Security.ProtectionState = "Protected ($names)"
        Add-Finding -Category 'Security' -Severity 'OK' `
            -Title 'Virus protection is active' `
            -Detail "$names is installed and reporting as enabled. Microsoft Defender has stepped aside, which is normal and expected."

        foreach ($p in ($activeThirdParty | Where-Object { $_.OutOfDate })) {
            Add-Finding -Category 'Security' -Severity 'Warning' `
                -Title "$($p.Name) is reporting out-of-date virus definitions" `
                -Detail "Windows Security Centre flags this product's definitions as stale." `
                -Recommendation "Check the product's licence status and update schedule. Expired antivirus licences are a common and easily missed gap."
        }
    }
    else {
        $Security.ProtectionState = 'No active protection detected'
        Add-Finding -Category 'Security' -Severity 'Critical' `
            -Title 'No active virus protection was found on this machine' `
            -Detail "Neither Microsoft Defender nor a registered third-party antivirus product is reporting as enabled." `
            -Recommendation "This machine is currently unprotected against malware and needs immediate attention."
    }
}

Invoke-Check -Name 'Firewall' -Body {
    if (Get-Command Get-NetFirewallProfile -ErrorAction SilentlyContinue) {
        foreach ($p in Get-NetFirewallProfile -ErrorAction Stop) {
            $Security.FirewallProfiles += [pscustomobject]@{
                Profile = $p.Name
                Enabled = [bool]$p.Enabled
            }
            if (-not $p.Enabled) {
                Add-Finding -Category 'Security' -Severity 'Warning' `
                    -Title "The $($p.Name) firewall profile is disabled" `
                    -Detail "Windows Firewall is not filtering traffic on this profile." `
                    -Recommendation "Re-enable the firewall unless there is a documented reason not to."
            }
        }
    }
}

Invoke-Check -Name 'LocalAccounts' -Body {
    if (Get-Command Get-LocalGroupMember -ErrorAction SilentlyContinue) {
        $admins = Get-LocalGroupMember -Group 'Administrators' -ErrorAction Stop
        $Security.LocalAdministrators = @($admins | Select-Object -ExpandProperty Name)

        if ($admins.Count -gt 3) {
            Add-Finding -Category 'Security' -Severity 'Warning' `
                -Title "$($admins.Count) accounts have full administrator rights" `
                -Detail "Accounts: $(($admins | Select-Object -ExpandProperty Name) -join ', ')." `
                -Recommendation "Reducing administrator accounts is the cheapest way to limit the damage a single compromised login can do."
        }
    }

    if (Get-Command Get-LocalUser -ErrorAction SilentlyContinue) {
        $never = Get-LocalUser -ErrorAction Stop |
                 Where-Object { $_.Enabled -and $_.PasswordExpires -eq $null -and $_.Name -ne 'DefaultAccount' }
        $Security.NonExpiringPasswords = @($never | Select-Object -ExpandProperty Name)

        # NOTE: PasswordRequired reports False for accounts signing in with a
        # Microsoft account or a Windows Hello PIN, which is normal and secure.
        # It fired on every test machine, so it is reported as something to
        # verify rather than as a confirmed fault.
        $blank = Get-LocalUser -ErrorAction SilentlyContinue |
                 Where-Object { $_.Enabled -and $_.PasswordRequired -eq $false }
        if ($blank) {
            Add-Finding -Category 'Security' -Severity 'Warning' `
                -Title 'Sign-in security on one or more accounts should be confirmed' `
                -Detail "Windows reports that these accounts may not require a password: $(($blank | Select-Object -ExpandProperty Name) -join ', '). This is normal for accounts using a Microsoft account or a Windows Hello PIN, but it also looks the same as a genuinely blank password." `
                -Recommendation "Worth a quick check that each of these accounts uses a PIN, a Microsoft account or a real password rather than no password at all."
        }
    }
}

Invoke-Check -Name 'BitLocker' -Body {
    if (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) {
        foreach ($b in (Get-BitLockerVolume -ErrorAction Stop)) {
            $Security.BitLockerVolumes += [pscustomobject]@{
                Mount      = $b.MountPoint
                Protection = "$($b.ProtectionStatus)"
            }
        }
        $sysVol = $Security.BitLockerVolumes | Where-Object { $_.Mount -eq $env:SystemDrive }
        if ($sysVol -and $sysVol.Protection -ne 'On') {
            if ($Machine.IsPortable) {
                Add-Finding -Category 'Security' -Severity 'Warning' `
                    -Title 'This laptop is not encrypted' `
                    -Detail "If the laptop is lost or stolen, anyone can remove the drive and read every file on it, including saved passwords and client information." `
                    -Recommendation "Encryption is built into Windows and costs nothing to enable. Strongly recommended for any machine that leaves the premises."
            }
            else {
                Add-Finding -Category 'Security' -Severity 'Info' `
                    -Title 'Drive encryption is not enabled on the system drive' `
                    -Detail "If this machine is removed from the premises, the data on it can be read." `
                    -Recommendation "Lower priority for a desktop that stays on site, but worth considering."
            }
        }
    }
}

# ---------------------------------------------------------------------------
# 7. Reliability: crashes, unexpected shutdowns, error volume
# ---------------------------------------------------------------------------

$Reliability = [pscustomobject]@{
    PeriodDays          = $EventLogDays
    CriticalErrorCount  = 0
    UnexpectedShutdowns = 0
    BugChecks           = 0
    TopErrorSources     = @()
}

Invoke-Check -Name 'EventLog' -Body {
    $since = (Get-Date).AddDays(-$EventLogDays)

    $sysEvents = Get-WinEvent -FilterHashtable @{
        LogName   = 'System'
        Level     = 1,2
        StartTime = $since
    } -ErrorAction SilentlyContinue

    if ($sysEvents) {
        $Reliability.CriticalErrorCount  = $sysEvents.Count
        $Reliability.UnexpectedShutdowns = @($sysEvents | Where-Object { $_.Id -in 41, 6008 }).Count
        $Reliability.TopErrorSources = @(
            $sysEvents | Group-Object ProviderName |
            Sort-Object Count -Descending | Select-Object -First 5 |
            ForEach-Object { [pscustomobject]@{ Source = $_.Name; Count = $_.Count } }
        )
    }

    $bc = Get-WinEvent -FilterHashtable @{
        LogName   = 'Application'
        Id        = 1001
        StartTime = $since
    } -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -match 'BugCheck' }
    $Reliability.BugChecks = @($bc).Count

    # Repeated update-client errors mean patching is silently failing, which is
    # invisible to the client and easy to miss on a manual check.
    $wuErrors = ($Reliability.TopErrorSources | Where-Object { $_.Source -match 'WindowsUpdateClient' }).Count
    if ($wuErrors) {
        $count = ($Reliability.TopErrorSources | Where-Object { $_.Source -match 'WindowsUpdateClient' }).Count
        $actual = ($Reliability.TopErrorSources | Where-Object { $_.Source -match 'WindowsUpdateClient' } | Select-Object -First 1).Count
        if ($actual -ge 5) {
            Add-Finding -Category 'Updates' -Severity 'Warning' `
                -Title "Windows Update has failed $actual times in the last $EventLogDays days" `
                -Detail "The update service is repeatedly reporting errors, which means patches may not be installing even though the machine appears to be updating normally." `
                -Recommendation "This needs hands-on investigation. Silent patching failure is one of the most common ways a machine that looks well-maintained ends up compromised."
        }
    }

    # A machine logging hundreds of errors a month has something structurally
    # wrong, even when no single source crosses its own threshold.
    if ($Reliability.CriticalErrorCount -ge 150) {
        $top = ($Reliability.TopErrorSources | Select-Object -First 2 |
                ForEach-Object { $_.Source -replace '^Microsoft-Windows-', '' }) -join ' and '
        Add-Finding -Category 'Reliability' -Severity 'Warning' `
            -Title "This machine logged $($Reliability.CriticalErrorCount) errors in the last $EventLogDays days" `
            -Detail "Most of them come from $top. A healthy machine typically logs a handful. This volume usually means a background service is failing repeatedly and retrying." `
            -Recommendation "Problems at this volume usually surface eventually as slowness, failed updates or an application that stops working. Worth investigating before that happens."
    }

    if ($Reliability.UnexpectedShutdowns -ge 3) {
        Add-Finding -Category 'Reliability' -Severity 'Warning' `
            -Title "$($Reliability.UnexpectedShutdowns) unexpected shutdowns in the last $EventLogDays days" `
            -Detail "The machine lost power or stopped responding rather than shutting down cleanly." `
            -Recommendation "Repeated hard shutdowns damage data over time. Common causes are a failing power supply, overheating, or no UPS on the desk."
    }

    if ($Reliability.BugChecks -ge 2) {
        Add-Finding -Category 'Reliability' -Severity 'Warning' `
            -Title "$($Reliability.BugChecks) system crashes recorded in the last $EventLogDays days" `
            -Detail "Windows recorded blue-screen crash events." `
            -Recommendation "These are usually caused by a faulty driver or failing memory. Worth investigating before it gets worse."
    }
}

# ---------------------------------------------------------------------------
# 8. Licensing: Windows activation and Office version
# ---------------------------------------------------------------------------

$Licensing = [pscustomobject]@{
    WindowsActivated = $null
    OfficeProducts   = @()
}

Invoke-Check -Name 'Activation' -Body {
    $lic = Get-CimInstance SoftwareLicensingProduct -ErrorAction Stop |
           Where-Object { $_.PartialProductKey -and $_.ApplicationID -eq '55c92734-d682-4d71-983e-d6ec3f16059f' } |
           Select-Object -First 1
    if ($lic) {
        $Licensing.WindowsActivated = ($lic.LicenseStatus -eq 1)
        if ($lic.LicenseStatus -ne 1) {
            Add-Finding -Category 'Licensing' -Severity 'Warning' `
                -Title 'Windows is not correctly activated' `
                -Detail "License status code: $($lic.LicenseStatus)." `
                -Recommendation "Unactivated Windows can lose features and indicates a licensing gap worth resolving."
        }
    }
}

Invoke-Check -Name 'Office' -Body {
    $c2r = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Office\ClickToRun\Configuration' -ErrorAction SilentlyContinue
    if ($c2r) {
        $Licensing.OfficeProducts += [pscustomobject]@{
            Name    = $c2r.ProductReleaseIds
            Version = $c2r.VersionToReport
            Type    = 'Click-to-Run'
        }
        if ("$($c2r.ProductReleaseIds)" -match '2013|2016|2019') {
            Add-Finding -Category 'Licensing' -Severity 'Warning' `
                -Title 'An unsupported version of Microsoft Office is installed' `
                -Detail "Detected: $($c2r.ProductReleaseIds)." `
                -Recommendation "Older Office versions no longer receive security fixes and are a common way malware gets in. Moving to a current, supported version closes that gap."
        }
    }
}

# ---------------------------------------------------------------------------
# 9. Assemble and write
# ---------------------------------------------------------------------------

$severityRank = @{ 'Critical' = 0; 'Warning' = 1; 'Info' = 2; 'OK' = 3 }

# If nobody told us who uses this PC, fall back to the account that last logged
# in, and finally to the machine name.
# ---------------------------------------------------------------------------
# 9. Windows system file verification
#     sfc /verifyonly checks every protected Windows file against Microsoft's
#     originals and REPORTS what is damaged. It repairs nothing, so the
#     read-only promise still holds.
#
#     Deliberately last: it takes about ten minutes, so everything quick is
#     already collected before this starts. If it times out, the rest of the
#     report is unaffected.
#
#     Repair belongs in Invoke-ClientMaintenance.ps1, which runs sfc /scannow.
#     Finding damage here and fixing it there is the point.
# ---------------------------------------------------------------------------

$SystemFiles = [pscustomobject]@{
    Checked          = $false
    Skipped          = $null
    Result           = $null
    DurationMins     = $null
    SuspiciouslyFast = $false
    RawOutput        = $null
}

Invoke-Check -Name 'SystemFiles' -Body {

    if (-not $VerifySystemFiles) { $SystemFiles.Skipped = 'Not requested'; return }
    if (-not $IsAdmin)           { $SystemFiles.Skipped = 'Needs administrator rights'; return }

    # sfc refuses to run at all while a repair is waiting on a restart: it
    # prints "There is a system repair pending which requires reboot to
    # complete" and exits within seconds. Checking first saves ten minutes of
    # waiting for a result that cannot come.
    #
    # No second warning is raised here. The pending restart is already reported
    # by the Updates check, and two warnings about one root cause is exactly the
    # crying wolf this report must avoid. The detail row says it was deferred.
    # Only the servicing and update flags stop sfc. PendingFileRenameOperations
    # is set constantly by browser and application updaters and is permanently
    # true on a normal machine - testing RebootPending as a whole would skip the
    # scan on virtually every PC, forever, and it would never once run.
    #
    # This is the same distinction the Updates check makes when deciding between
    # a Warning and routine housekeeping. If it is wrong, sfc says so itself and
    # the RebootRequired branch below catches it.
    $blocking = @($Updates.RebootReasons | Where-Object { $_ -in 'Component servicing', 'Windows Update' })
    if ($blocking.Count -gt 0) {
        $SystemFiles.Skipped = 'A restart is pending, and Windows will not check its own files until that has been done'
        return
    }

    Write-Host ""
    Write-Host "  Checking Windows' own system files. About ten minutes." -ForegroundColor DarkGray
    Write-Host "  Nothing is being repaired - this only looks and reports." -ForegroundColor DarkGray

    # sfc writes its output as UTF-16 with embedded nulls, which is why they are
    # stripped below. It is run as a separate process with output redirected so
    # progress can be shown while it works.
    $outFile = Join-Path $env:TEMP "sfcverify-$PID.txt"
    $errFile = Join-Path $env:TEMP "sfcverify-$PID.err"

    $proc = Start-Process -FilePath "$env:SystemRoot\System32\sfc.exe" `
                          -ArgumentList '/verifyonly' -PassThru -NoNewWindow `
                          -RedirectStandardOutput $outFile -RedirectStandardError $errFile

    $start    = Get-Date
    $timedOut = $false

    while (-not $proc.HasExited) {
        Start-Sleep -Seconds 30
        $mins = [math]::Round(((Get-Date) - $start).TotalMinutes)
        if ($mins -ge $SystemCheckTimeoutMinutes) {
            try { $proc.Kill() } catch { }
            $timedOut = $true
            break
        }
        Write-Host "    still checking, $mins minute(s) so far" -ForegroundColor DarkGray
    }

    $SystemFiles.DurationMins = [math]::Round(((Get-Date) - $start).TotalMinutes)
    $SystemFiles.Checked      = $true

    $out = ''
    if (Test-Path $outFile) {
        $out = (Get-Content $outFile -Raw -ErrorAction SilentlyContinue) -replace "`0", ''
    }
    Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue

    if ($timedOut) {
        $SystemFiles.Result = 'TimedOut'
        Add-Finding -Category 'Operating System' -Severity 'Warning' `
            -Title 'The Windows system file check could not finish' `
            -Detail "The check ran for $SystemCheckTimeoutMinutes minutes without completing and was stopped. On a healthy machine it takes about ten." `
            -Recommendation "This usually means Windows servicing itself is damaged on this machine, and it often sits behind update failures too. It needs hands-on repair."
        return
    }

    # Order matters: "did not find any integrity violations" also contains the
    # word "integrity violations", so the clean case must be tested first.
    if ($out -match 'did not find any integrity violations') {
        $SystemFiles.Result = 'Clean'
        Add-Finding -Category 'Operating System' -Severity 'OK' `
            -Title 'Windows own files are all intact' `
            -Detail 'Every protected Windows file was compared against Microsoft originals and none had been damaged or altered.' `
            -Recommendation ''
    }
    elseif ($out -match 'found integrity violations') {
        $SystemFiles.Result = 'Damaged'
        Add-Finding -Category 'Operating System' -Severity 'Warning' `
            -Title 'Some of Windows own files are damaged' `
            -Detail 'Comparing this machine against Microsoft originals found Windows files that have been damaged or altered. This shows up as odd behaviour, features that stop working, or updates that fail. Nothing was changed - this check only looks.' `
            -Recommendation "Windows can usually repair this itself, but the repair has to be run deliberately and watched. Repeated damage month after month points to a failing drive or faulty memory, which is worth investigating separately."
    }
    elseif ($out -match 'repair pending' -or $out -match 'requires reboot') {
        # Belt and braces. The registry check above should have caught this, but
        # it does not detect every kind of pending servicing operation, and sfc
        # itself is the authority on whether it can run.
        $SystemFiles.Result = 'RebootRequired'
    }
    elseif ($out -match 'could not perform the requested operation') {
        $SystemFiles.Result = 'Refused'
        Add-Finding -Category 'Operating System' -Severity 'Warning' `
            -Title 'Windows could not check its own files' `
            -Detail 'The built-in check would not run on this machine.' `
            -Recommendation "This normally accompanies a broader fault with Windows servicing and is worth looking at properly."
    }
    elseif ($out) {
        # The scan ran and said something we do not recognise. This must NOT be
        # reported as a pass. On a non-English Windows it may well be a clean
        # result; it may equally be a failure never seen before. Claiming a check
        # succeeded when we cannot tell is worse than admitting we cannot tell.
        #
        # The raw text is kept so the cause can be diagnosed from the JSON
        # instead of guessed at. It is never shown to the client.
        $SystemFiles.Result    = 'Unrecognised'
        $clean = ($out -replace '\s+', ' ').Trim()
        if ($clean.Length -gt 600) { $clean = $clean.Substring(0, 600) }
        $SystemFiles.RawOutput = $clean
    }
    else {
        $SystemFiles.Result = 'NoOutput'
    }

    # A verification that finishes far too quickly did not really run, whatever
    # it printed. Recorded so the report never presents it as a clean result.
    if ($SystemFiles.Result -ne 'Clean' -and $SystemFiles.DurationMins -lt 2) {
        $SystemFiles.SuspiciouslyFast = $true
    }
}

if (-not $UserOrRole) {
    if ($Machine.LastUser) { $UserOrRole = ($Machine.LastUser -split '\\')[-1] }
    else                   { $UserOrRole = $env:COMPUTERNAME }
}

$report = [pscustomobject]@{
    SchemaVersion   = '1.3'
    Organisation    = $Organisation
    UserOrRole      = $UserOrRole
    CollectedAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
    CollectedLocal  = (Get-Date).ToString('yyyy-MM-dd HH:mm')
    RanAsAdmin      = $IsAdmin
    Machine         = $Machine
    OperatingSystem = $OperatingSystem
    Windows11       = $Windows11
    Storage         = [pscustomobject]@{ Volumes = $Disks; PhysicalDisks = $PhysicalDisks; SmartDrives = @($SmartDrives); RecycleBin = $RecycleBin; TempFiles = $TempFiles; Optimisation = $DriveOptimisation }
    Backup          = $Backup
    BackupCheck     = $BackupCheck
    Updates         = $Updates
    Security        = $Security
    Reliability     = $Reliability
    Licensing       = $Licensing
    SystemFiles     = $SystemFiles
    Summary         = [pscustomobject]@{
        CriticalCount = @($Findings | Where-Object Severity -eq 'Critical').Count
        WarningCount  = @($Findings | Where-Object Severity -eq 'Warning').Count
        InfoCount     = @($Findings | Where-Object Severity -eq 'Info').Count
    }
    Findings        = @($Findings | Sort-Object { $severityRank[$_.Severity] })
    CollectionErrors= @($Errors)
}

if (-not (Test-Path $OutputPath)) {
    New-Item -Path $OutputPath -ItemType Directory -Force | Out-Null
}

$fileName = '{0}_{1}_{2}.json' -f (ConvertTo-Slug $Organisation), $env:COMPUTERNAME, (Get-Date -Format 'yyyy-MM')
$fullPath = Join-Path $OutputPath $fileName

# PowerShell 5.1 serialises DateTime as "\/Date(1630368000000)\/", which nothing
# downstream can read. Rewrite those into ISO 8601 before the file is written.
$json = $report | ConvertTo-Json -Depth 6
$json = [regex]::Replace(
    $json,
    '\\/Date\((-?\d+)\)\\/',
    {
        param($m)
        $epochMs = [long]$m.Groups[1].Value
        ([datetimeoffset]::FromUnixTimeMilliseconds($epochMs)).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
)

# PowerShell 5.1's Out-File -Encoding UTF8 writes a byte order mark, which many
# JSON parsers reject outright. Write UTF-8 without a BOM instead.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($fullPath, $json, $utf8NoBom)

Write-Host ""
Write-Host "Health check v$($report.SchemaVersion)" -ForegroundColor DarkGray
Write-Host "Health check complete: $UserOrRole on $($env:COMPUTERNAME) - $Organisation" -ForegroundColor Cyan
Write-Host "  Critical : $($report.Summary.CriticalCount)" -ForegroundColor Red
Write-Host "  Warning  : $($report.Summary.WarningCount)" -ForegroundColor Yellow
Write-Host "  Info     : $($report.Summary.InfoCount)" -ForegroundColor Gray
if ($Errors.Count -gt 0) {
    Write-Host "  Checks that failed to run: $($Errors.Count) (see CollectionErrors in the JSON)" -ForegroundColor DarkYellow
}
Write-Host "  Written to: $fullPath" -ForegroundColor Green
Write-Host ""


# ---------------------------------------------------------------------------
# Send the results
#
# The file is always written to disk first. Email is a bonus - if it fails,
# the machine keeps its local copy and records why, so nothing is ever lost.
# ---------------------------------------------------------------------------

function Unprotect-StoredSecret {
    param([string]$Base64)
    if (-not $Base64) { return $null }
    try {
        Add-Type -AssemblyName System.Security -ErrorAction Stop
        $bytes = [Convert]::FromBase64String($Base64)
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
                    $bytes, $null,
                    [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
        return [System.Text.Encoding]::UTF8.GetString($plain)
    }
    catch {
        Write-Verbose "Stored secret could not be read: $($_.Exception.Message)"
        return $null
    }
}

if (-not $NoEmail -and $Config -and $Config.Email -and $Config.Email.To -and $Config.Email.SmtpServer) {

    $mailLog = Join-Path $OutputPath 'last-send.txt'

    try {
        $emailCfg = $Config.Email

        $criticalList = @($report.Findings | Where-Object { $_.Severity -eq 'Critical' })
        $warningList  = @($report.Findings | Where-Object { $_.Severity -eq 'Warning'  })

        $subject = '[{0}] {1} ({2}) - {3} urgent, {4} to plan' -f `
                   $Organisation, $UserOrRole, $env:COMPUTERNAME,
                   $criticalList.Count, $warningList.Count

        $lines = @()
        $lines += "Organisation : $Organisation"
        $lines += "User or role : $UserOrRole"
        $lines += "Machine      : $($env:COMPUTERNAME)"
        $lines += "Checked      : $($report.CollectedLocal)"
        $lines += ""
        if ($criticalList.Count -gt 0) {
            $lines += "NEEDS ATTENTION"
            foreach ($c in $criticalList) { $lines += "  - $($c.Title)" }
            $lines += ""
        }
        if ($warningList.Count -gt 0) {
            $lines += "TO PLAN"
            foreach ($w in $warningList) { $lines += "  - $($w.Title)" }
            $lines += ""
        }
        if ($criticalList.Count -eq 0 -and $warningList.Count -eq 0) {
            $lines += "Nothing needs attention on this machine."
            $lines += ""
        }
        if ($report.CollectionErrors.Count -gt 0) {
            $lines += "Checks that could not run: $($report.CollectionErrors.Count)"
            $lines += ""
        }
        $lines += "The full results are attached."
        $body = $lines -join "`r`n"

        $from = if ($emailCfg.From) { $emailCfg.From } else { $emailCfg.User }
        $port = if ($emailCfg.Port) { [int]$emailCfg.Port } else { 587 }

        $mailArgs = @{
            To          = @($emailCfg.To -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            From        = $from
            Subject     = $subject
            Body        = $body
            SmtpServer  = $emailCfg.SmtpServer
            Port        = $port
            Attachments = $fullPath
            ErrorAction = 'Stop'
        }
        if ($emailCfg.UseSsl -ne $false) { $mailArgs['UseSsl'] = $true }

        if ($emailCfg.User) {
            $pw = Unprotect-StoredSecret -Base64 $emailCfg.PasswordProtected
            if ($pw) {
                $secure = ConvertTo-SecureString $pw -AsPlainText -Force
                $mailArgs['Credential'] = New-Object System.Management.Automation.PSCredential($emailCfg.User, $secure)
            }
            else {
                throw "The stored email password could not be read on this machine. Reinstall to store it again."
            }
        }

        # TLS 1.2 - older Windows defaults to TLS 1.0, which most mail providers refuse
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

        Send-MailMessage @mailArgs

        "$(Get-Date -Format 'yyyy-MM-dd HH:mm')  Sent to $($emailCfg.To)" | Out-File $mailLog -Encoding UTF8 -Force
        Write-Host "  Results emailed to $($emailCfg.To)" -ForegroundColor Green
    }
    catch {
        $msg = "$(Get-Date -Format 'yyyy-MM-dd HH:mm')  FAILED: $($_.Exception.Message)"
        $msg | Out-File $mailLog -Encoding UTF8 -Force
        Write-Host "  Could not email the results: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "  The results file is still saved on this machine." -ForegroundColor Yellow
    }
}

# Only return the object when asked. Dumping it to screen buries the summary a
# technician actually needs to read.
if ($PassThru) { $report }
