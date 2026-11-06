<#
    Recreates the monthly health check scheduled task, exactly as Install-HealthCheck.ps1 makes it,
    without touching config.json, the installed scripts or anything else.

    Written to repair DESKTOP-OPHQIDH after a bug in the installer's stray-task cleanup deleted the
    real task: the first version matched any task whose action named one of our scripts anywhere,
    so running an install against a different folder removed the machine's genuine one. The
    installer is fixed - a stray is now only a task running something out of the folder being
    installed into - but a machine that already lost its task needs putting back.

    Run from an ELEVATED PowerShell on the machine that lost it:
        powershell -ExecutionPolicy Bypass -File .\Restore-HealthCheckTask.ps1

    Defaults match what that machine had. Override if yours differed.
#>
param(
    [string]$TaskName    = 'Monthly Maintenance and Health Check',
    [string]$InstallPath = 'C:\ProgramData\HealthCheck',
    [ValidateRange(1,28)]
    [int]$DayOfMonth     = 3,
    [string]$TimeOfDay   = '08:00'
)

$ErrorActionPreference = 'Stop'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Run this from an elevated PowerShell." -ForegroundColor Red
    exit 1
}

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Write-Host "'$TaskName' is already present - nothing to do." -ForegroundColor Green
    exit 0
}

$target = Join-Path $InstallPath 'Invoke-MonthlyRun.ps1'
if (-not (Test-Path $target)) {
    Write-Host "Invoke-MonthlyRun.ps1 is not in $InstallPath - reinstall the client instead." -ForegroundColor Red
    exit 1
}

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $target + '" -WorkPath "' + $InstallPath + '"')
$trigger = New-ScheduledTaskTrigger -Daily -At $TimeOfDay
$trigger.RandomDelay = 'PT30M'
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -DontStopIfGoingOnBatteries `
    -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 4) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal -Force | Out-Null

# Swap the daily trigger for a monthly one. The XML route is the only reliable way to express
# "day N of every month" in PowerShell 5.1, and the StartBoundary must be the NEXT real
# occurrence - a time already past leaves Windows reporting no next run at all.
$xml = Export-ScheduledTask -TaskName $TaskName
$now = Get-Date
$parts = $TimeOfDay -split ':'
$candidate = Get-Date -Year $now.Year -Month $now.Month -Day $DayOfMonth -Hour ([int]$parts[0]) -Minute ([int]$parts[1]) -Second 0
if ($candidate -le $now) { $candidate = $candidate.AddMonths(1) }
$startBoundary = $candidate.ToString('yyyy-MM-ddTHH:mm:ss')

$monthly = @"
      <CalendarTrigger>
        <StartBoundary>$startBoundary</StartBoundary>
        <Enabled>true</Enabled>
        <RandomDelay>PT30M</RandomDelay>
        <ScheduleByMonth>
          <DaysOfMonth><Day>$DayOfMonth</Day></DaysOfMonth>
          <Months><January /><February /><March /><April /><May /><June /><July /><August /><September /><October /><November /><December /></Months>
        </ScheduleByMonth>
      </CalendarTrigger>
"@
$newXml = [regex]::Replace($xml, '<Triggers>.*?</Triggers>', "<Triggers>`n$monthly    </Triggers>", 'Singleline')
Register-ScheduledTask -TaskName $TaskName -Xml $newXml -User 'SYSTEM' -Force | Out-Null

$t = Get-ScheduledTask -TaskName $TaskName
$i = Get-ScheduledTaskInfo -TaskName $TaskName
Write-Host ""
Write-Host "RESTORED: $($t.TaskPath)$($t.TaskName)" -ForegroundColor Green
Write-Host "  state    : $($t.State)"
Write-Host "  runs as  : $($t.Principal.UserId) ($($t.Principal.RunLevel))"
Write-Host "  next run : $($i.NextRunTime)"
foreach ($a in $t.Actions) { Write-Host "  action   : $($a.Execute) $($a.Arguments)" }
Write-Host ""
