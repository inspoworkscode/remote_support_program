<#
    Proves what a SECOND run of Install-HealthCheck.ps1 does to settings that were configured
    after the first one.

    Installs twice into a temporary folder under a temporary task name, then checks that the
    things a technician or the platform configured survived, and that the branding did refresh.
    Cleans up after itself - it does not touch C:\ProgramData\HealthCheck or the real task.

    Run from an ELEVATED PowerShell:
        powershell -ExecutionPolicy Bypass -File .\Test-HealthCheckReinstall.ps1
#>
param(
    # Left empty so the body can work it out. A param() default cannot: $PSScriptRoot is empty
    # when this file is pasted into a console rather than run, and the whole script then ran on
    # nulls and still printed "ALL CHECKS PASSED" because no check had failed. See $checksRun.
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    if ($PSScriptRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
    else {
        Write-Host ""
        Write-Host "This file was pasted into the console rather than run as a script." -ForegroundColor Red
        Write-Host "Run it instead, from an elevated PowerShell:" -ForegroundColor Red
        Write-Host ""
        Write-Host '    powershell -ExecutionPolicy Bypass -File "C:\Users\Clint\Desktop\Remote Support\tools\Test-HealthCheckReinstall.ps1"'
        Write-Host ""
        Write-Host "Or pass the repository folder: -RepoRoot 'C:\Users\Clint\Desktop\Remote Support'" -ForegroundColor Gray
        Write-Host ""
        exit 1
    }
}

# Everything from here is written to a log as well as the screen. An elevated window that closes
# for any reason - a bad argument, a throw, somebody's reflexes - takes the only copy of the
# result with it otherwise, and the result is the entire point of running this.
$log = Join-Path $env:TEMP 'hc-reinstall-test.log'
try { Start-Transcript -Path $log -Force | Out-Null } catch { $log = $null }
if ($log) { Write-Host "Logging to $log" -ForegroundColor Gray }

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Run this from an elevated PowerShell - the installer registers a scheduled task." -ForegroundColor Red
    exit 1
}

$installer = Join-Path $RepoRoot 'healthcheck\src\Install-HealthCheck.ps1'
if (-not (Test-Path $installer)) { throw "Cannot find $installer" }

$work = Join-Path $env:TEMP ("hc-reinstall-test-" + [guid]::NewGuid().ToString('N').Substring(0,8))
$task = "RSP REINSTALL TEST - safe to delete"
$failures = @()
$checksRun = 0

function Check($label, $expected, $actual) {
    $script:checksRun++
    if ("$expected" -eq "$actual") { Write-Host ("  PASS  {0,-34} {1}" -f $label, $actual) -ForegroundColor Green }
    else {
        Write-Host ("  FAIL  {0,-34} expected '{1}', got '{2}'" -f $label, $expected, $actual) -ForegroundColor Red
        $script:failures += $label
    }
}

try {
    Write-Host ""
    Write-Host "Working in $work" -ForegroundColor Cyan

    # ---- first install: as a technician would set a machine up -------------
    Write-Host ""
    Write-Host "First install (Acme Accounting, day 7 at 06:45, branded 'Acme IT')..." -ForegroundColor Cyan
    & $installer -InstallPath $work -TaskName $task -NoTestRun `
        -Organisation 'Acme Accounting' -UserOrRole 'Sarah' `
        -DayOfMonth 7 -TimeOfDay '06:45' `
        -EmailTo 'reports@acme.example' -RecipientType 'Client' `
        -SmtpServer 'mail.acme.example' -SmtpUser 'send@acme.example' -SmtpPassword 'test-only-not-a-password' `
        -CompanyName 'Acme IT' -CompanyEmail 'help@acme.example' | Out-Null
    # A mail server since the toolkit's v2.9: an address with no way to send to it is refused now,
    # and this used to give one. The login is here so the second install has a stored password to
    # keep - which it could not, before 2026-09-28, without failing its own round-trip check.
    if ($LASTEXITCODE -ne 0) { throw "The first install was refused (exit $LASTEXITCODE)." }

    $first = Get-Content (Join-Path $work 'config.json') -Raw | ConvertFrom-Json

    # ---- second install: the client installer re-running --------------------
    # Machine name for the organisation and a different brand, exactly as Install-Client.ps1
    # passes them. Everything the technician set must survive; the branding must not.
    Write-Host ""
    Write-Host "Second install (as the client installer re-runs it, keeping settings)..." -ForegroundColor Cyan
    & $installer -InstallPath $work -TaskName $task -NoTestRun `
        -ExistingSettings Keep `
        -Organisation $env:COMPUTERNAME -UserOrRole $env:COMPUTERNAME `
        -CompanyName 'Rebranded IT' -CompanyEmail 'help@rebranded.example' `
        -PlatformManaged | Out-Null
    $secondExit = $LASTEXITCODE

    $second = Get-Content (Join-Path $work 'config.json') -Raw | ConvertFrom-Json

    Write-Host ""
    Write-Host "Kept, because a person or the platform configured them:" -ForegroundColor Cyan
    Check 'Organisation'        'Acme Accounting'      $second.Organisation
    Check 'UserOrRole'          'Sarah'                $second.UserOrRole
    Check 'ReportsGoTo'         'reports@acme.example' $second.ReportsGoTo
    Check 'RecipientType'       'Client'               $second.RecipientType
    Check 'Schedule.DayOfMonth' 7                      $second.Schedule.DayOfMonth
    Check 'Schedule.TimeOfDay'  '06:45'                $second.Schedule.TimeOfDay
    Check 'InstalledOn'         $first.InstalledOn     $second.InstalledOn
    Check 'Email.SmtpServer'    'mail.acme.example'    $second.Email.SmtpServer
    Check 'Email password, still encrypted the same' $first.Email.PasswordProtected $second.Email.PasswordProtected
    Check 'second install finished (exit code)' 0      $secondExit

    Write-Host ""
    Write-Host "Refreshed, because it is the tenant's current identity:" -ForegroundColor Cyan
    Check 'Branding.CompanyName' 'Rebranded IT'            $second.Branding.CompanyName
    Check 'Branding.CompanyEmail' 'help@rebranded.example' $second.Branding.CompanyEmail

    # ---- a task an older version left behind --------------------------------
    # This is what DESKTOP-OPHQIDH actually had: a second task, under a different name, pointing
    # at a script in the same folder, invisible to a registration that only replaces its own name.
    $decoy = 'RSP DECOY old health check - safe to delete'
    Write-Host ""
    Write-Host "Planting a stray task from an 'older version'..." -ForegroundColor Cyan
    $decoyAction  = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $work 'Get-ClientHealthCheck.ps1') + '" -ClientName "Test"')
    $decoyTrigger = New-ScheduledTaskTrigger -Daily -At '03:00'
    Register-ScheduledTask -TaskName $decoy -Action $decoyAction -Trigger $decoyTrigger `
        -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    Check 'stray task planted' 1 (@(Get-ScheduledTask -TaskName $decoy -ErrorAction SilentlyContinue)).Count

    # A task belonging to a DIFFERENT install. It runs the same script name, from the real
    # install folder, which is exactly what the machine's genuine task looks like. It must
    # survive: the first version of the cleanup matched on the script name wherever it lived and
    # deleted the real task off DESKTOP-OPHQIDH while testing against a temp folder.
    $outsider = 'RSP OUTSIDER do not remove - safe to delete'
    $outsiderAction = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument '-NoProfile -ExecutionPolicy Bypass -File "C:\ProgramData\HealthCheck\Invoke-MonthlyRun.ps1" -WorkPath "C:\ProgramData\HealthCheck"'
    Register-ScheduledTask -TaskName $outsider -Action $outsiderAction -Trigger $decoyTrigger `
        -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    Check 'other install task planted' 1 (@(Get-ScheduledTask -TaskName $outsider -ErrorAction SilentlyContinue)).Count

    # ---- third install: explicitly overwriting ------------------------------
    Write-Host ""
    Write-Host "Third install (-ExistingSettings Overwrite)..." -ForegroundColor Cyan
    & $installer -InstallPath $work -TaskName $task -NoTestRun `
        -ExistingSettings Overwrite `
        -Organisation 'Replaced Ltd' -UserOrRole 'Tech' `
        -DayOfMonth 3 -TimeOfDay '09:15' -PlatformManaged | Out-Null

    $third = Get-Content (Join-Path $work 'config.json') -Raw | ConvertFrom-Json

    Write-Host ""
    Write-Host "Replaced, because it was asked for:" -ForegroundColor Cyan
    Check 'Organisation'        'Replaced Ltd' $third.Organisation
    Check 'Schedule.DayOfMonth' 3              $third.Schedule.DayOfMonth
    Check 'Schedule.TimeOfDay'  '09:15'        $third.Schedule.TimeOfDay

    # ---- how many tasks did three installs leave? --------------------------
    $tasks = @(Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)
    Write-Host ""
    Write-Host "Scheduled tasks after three installs:" -ForegroundColor Cyan
    Check 'our task count'      1 $tasks.Count
    Check 'stray task removed'  0 (@(Get-ScheduledTask -TaskName $decoy -ErrorAction SilentlyContinue)).Count
    Check 'other install untouched' 1 (@(Get-ScheduledTask -TaskName $outsider -ErrorAction SilentlyContinue)).Count
}
finally {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    if ($decoy)    { Unregister-ScheduledTask -TaskName $decoy    -Confirm:$false -ErrorAction SilentlyContinue }
    if ($outsider) { Unregister-ScheduledTask -TaskName $outsider -Confirm:$false -ErrorAction SilentlyContinue }
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host ""
    Write-Host "Cleaned up the temporary task and folder." -ForegroundColor Gray
}

# An empty failure list is not success. If nothing ran - the installer threw, the folder was
# never written, the file was pasted instead of executed - then "no failures" means "no test",
# and saying so is the whole point of running this.
$expectedChecks = 20
Write-Host ""
if ($checksRun -lt $expectedChecks) {
    Write-Host "INCONCLUSIVE - only $checksRun of $expectedChecks checks ran." -ForegroundColor Red
    Write-Host "Something above stopped the test before it finished. Read the error, not this line." -ForegroundColor Red
    Write-Host ""
    exit 1
}
if ($failures.Count -eq 0) { Write-Host "ALL $checksRun CHECKS PASSED" -ForegroundColor Green }
else { Write-Host "FAILED ($($failures.Count) of $checksRun): $($failures -join ', ')" -ForegroundColor Red }
if ($log) { Write-Host ""; Write-Host "Full output: $log" -ForegroundColor Gray }
try { Stop-Transcript | Out-Null } catch { }
Write-Host ""
if ($failures.Count -gt 0) { exit 1 }
