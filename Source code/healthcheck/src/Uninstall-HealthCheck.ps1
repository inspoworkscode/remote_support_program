<#
.SYNOPSIS
    Removes the monthly health check schedule that The Remote Support Program set up.

.DESCRIPTION
    Called by the support app's agent service when a machine is switched off in the console or
    the portal, and by the monthly run itself when the server says it is no longer wanted.

    Removes the SCHEDULE only. The results already collected, and config.json, stay where they
    are: they are the machine's history, and if it is switched back on the install keeps its
    day of the month rather than choosing a new one.

    It touches nothing it did not install. A health check is ours only when its config.json names
    a platform server - the standalone toolkit writes the same folder and never that section -
    and only tasks that run something out of the install folder are removed. Anything else is
    reported and left alone, with exit code 3.

.NOTES
    Requires: PowerShell 5.1, administrator rights (the agent runs as SYSTEM)
    Exit codes: 0 removed or nothing to remove, 3 not ours and left alone, 1 could not remove.
#>

[CmdletBinding()]
param(
    [string]$InstallPath = "$env:ProgramData\HealthCheck",
    [string]$TaskName = "Monthly Maintenance and Health Check"
)

$ErrorActionPreference = 'Stop'

$configPath = Join-Path $InstallPath 'config.json'
$config = $null
if (Test-Path $configPath) {
    try { $config = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $config = $null }
}

# Nothing of ours here: nothing to do, and certainly nothing to delete.
$ours = $config -and $config.Platform -and $config.Platform.BaseUrl
if (-not $ours) {
    $task = $null
    try { $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { }
    if ($task) {
        Write-Host "  A health check is scheduled here, but not by this support app. Left alone."
        exit 3
    }
    Write-Host "  No health check from this support app on this machine."
    exit 0
}

# Our tasks are the ones that run something out of our install folder - the same rule the
# installer uses to find its own leftovers (Install-HealthCheck.ps1, "Tasks an earlier version left
# behind"), and for the same reason: a name catches the wrong task and misses a renamed one.
$failed = $false
$removed = 0
if (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue) {
    $tasks = @(Get-ScheduledTask -ErrorAction SilentlyContinue |
        Where-Object { $_.TaskPath -notlike "\Microsoft\*" } |
        Where-Object { $_.Actions | Where-Object { "$($_.Execute) $($_.Arguments)" -like "*$InstallPath*" } })
    foreach ($t in $tasks) {
        try {
            Unregister-ScheduledTask -TaskName $t.TaskName -TaskPath $t.TaskPath -Confirm:$false -ErrorAction Stop
            Write-Host "  Removed $($t.TaskPath)$($t.TaskName)"
            $removed++
        }
        catch {
            Write-Host "  Could not remove $($t.TaskPath)$($t.TaskName): $($_.Exception.Message)"
            $failed = $true
        }
    }
}
else {
    # Windows without the ScheduledTasks module. The name is all there is to go on.
    & schtasks.exe /Delete /TN "`"$TaskName`"" /F 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { $removed++ }
}

if ($failed) { exit 1 }
if ($removed -eq 0) { Write-Host "  The monthly schedule was already gone." }
else { Write-Host "  The monthly health check will not run again on this machine. Its past results are kept in $InstallPath." }
exit 0
