<#
    What health check is actually installed on THIS machine.

    Answers the question a reinstall raises: is there one scheduled task or several, what settings
    is it carrying, and has a reinstall reset any of them. Read-only - it changes nothing.

        powershell -ExecutionPolicy Bypass -File .\Get-HealthCheckInstallState.ps1
#>
# No param() block. This is run through the product's own script execution (tools\runscript) as
# often as it is run by hand, and that writes the script into a file with lines above it - so a
# param() is no longer the first statement and PowerShell rejects the whole block.
$InstallPath = "$env:ProgramData\HealthCheck"

# NOT SilentlyContinue. The first version of this file set that, then used a regex that a here-doc
# had quietly broken; the match threw, Where-Object emitted nothing, and it printed a confident
# "0 tasks" on a machine that had two. A diagnostic that hides its own errors is worse than none.
$ErrorActionPreference = 'Continue'

Write-Host ""
Write-Host "Health check install state on $env:COMPUTERNAME" -ForegroundColor Cyan
Write-Host ("-" * 60)

# --- scheduled tasks -------------------------------------------------------
# Matched on the action, not just the name: a task registered under a different name still shows
# up if it runs Invoke-MonthlyRun.ps1, which is the thing that would actually duplicate a run.
# -notlike, not -notmatch: the path is full of backslashes, and a regex full of backslashes is
# one transcription away from being invalid. A wildcard pattern treats them as ordinary text.
# Matched on what a task RUNS, not on what it is called. Names catch a Lenovo task with the word
# "Maintenance" in it and miss anything somebody renamed; an action that points at one of our
# scripts, or into our install folder, is ours whatever the task is called. The two exact names
# are there as well because a task whose scripts have already been deleted still needs finding.
$ourNames = @('Monthly Maintenance and Health Check', 'Monthly Health Check')
$tasks = @(Get-ScheduledTask | Where-Object { $_.TaskPath -notlike "\Microsoft\*" } | Where-Object {
    $ourNames -contains $_.TaskName -or
    ($_.Actions | Where-Object {
        $cmd = "$($_.Execute) $($_.Arguments)"
        $cmd -match 'Invoke-MonthlyRun|Get-ClientHealthCheck|Invoke-ClientMaintenance' -or $cmd -like "*$InstallPath*"
    })
})

Write-Host ""
Write-Host "Scheduled tasks that would run a health check: $($tasks.Count)" -ForegroundColor $(if ($tasks.Count -gt 1) { 'Red' } elseif ($tasks.Count -eq 1) { 'Green' } else { 'Yellow' })
foreach ($t in $tasks) {
    $info = Get-ScheduledTaskInfo -TaskName $t.TaskName -TaskPath $t.TaskPath
    Write-Host ""
    Write-Host "  $($t.TaskPath)$($t.TaskName)" -ForegroundColor White
    Write-Host "    state     : $($t.State)"
    Write-Host "    runs as   : $($t.Principal.UserId)"
    Write-Host "    last run  : $($info.LastRunTime)  (result $($info.LastTaskResult))"
    Write-Host "    next run  : $($info.NextRunTime)"
    foreach ($a in $t.Actions) { Write-Host "    action    : $($a.Execute) $($a.Arguments)" }
}
if ($tasks.Count -gt 1) {
    Write-Host ""
    Write-Host "  MORE THAN ONE - these would each run a health check and each send a report." -ForegroundColor Red
}

# --- the install folder ----------------------------------------------------
Write-Host ""
Write-Host "Install folder: $InstallPath"
if (-not (Test-Path $InstallPath)) {
    Write-Host "  not present - the health check is not installed here." -ForegroundColor Yellow
}
else {
    Get-ChildItem $InstallPath -File | Sort-Object Name |
        ForEach-Object { Write-Host ("  {0,-46} {1,9} bytes  {2}" -f $_.Name, $_.Length, $_.LastWriteTime.ToString("yyyy-MM-dd HH:mm")) }

    $cfgPath = Join-Path $InstallPath 'config.json'
    if (Test-Path $cfgPath) {
        Write-Host ""
        Write-Host "config.json" -ForegroundColor Cyan
        try {
            $c = Get-Content $cfgPath -Raw | ConvertFrom-Json
            Write-Host "  Organisation  : $($c.Organisation)"
            Write-Host "  UserOrRole    : $($c.UserOrRole)"
            Write-Host "  InstalledOn   : $($c.InstalledOn)"
            Write-Host "  ReportsGoTo   : $($c.ReportsGoTo)"
            Write-Host "  RecipientType : $($c.RecipientType)"
            if ($c.Schedule) { Write-Host "  Schedule      : day $($c.Schedule.DayOfMonth) at $($c.Schedule.TimeOfDay)" }
            else             { Write-Host "  Schedule      : not recorded (installed before the schedule was stored)" -ForegroundColor Yellow }
            if ($c.Platform) { Write-Host "  Platform      : $($c.Platform.BaseUrl)" }
            if ($c.Branding) { Write-Host "  Branded as    : '$($c.Branding.CompanyName)'  logo '$($c.Branding.LogoFile)'" }
            if ($c.Email)    { Write-Host "  Email         : $($c.Email.SmtpServer) as $($c.Email.User)" }
            else             { Write-Host "  Email         : no SMTP section" }
        }
        catch { Write-Host "  could not be read: $($_.Exception.Message)" -ForegroundColor Red }
    }
    else { Write-Host "  no config.json" -ForegroundColor Yellow }

    # A logo sitting here that is not the tenant's is the branding leak this folder used to carry.
    $logos = @(Get-ChildItem $InstallPath -Filter 'logo.*' -File)
    if ($logos) { Write-Host ""; Write-Host "Logo files present: $($logos.Name -join ', ')" }
}

Write-Host ""
