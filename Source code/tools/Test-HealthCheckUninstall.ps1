<#
    Does healthcheck\src\Uninstall-HealthCheck.ps1 remove ours, and ONLY ours?

    It is what the agent service runs when a machine is switched off, and what the monthly run runs
    on itself when the server says it is no longer wanted - so "only ours" is the property that
    matters. DESKTOP-OPHQIDH carries the standalone toolkit in the same folder, and the installer
    once deleted a real task by matching too widely (Install-HealthCheck.ps1, "Tasks an earlier
    version left behind").

    Works entirely in a temp folder with a scheduled task of its own, registered for the CURRENT
    user, never triggered (it is set for 2099) and removed at the end whatever happens. The
    machine's real health check, if it has one, is not touched: the script is only ever pointed at
    the temp folder, and it matches tasks by what they run out of that folder.

        powershell -ExecutionPolicy Bypass -File tools\Test-HealthCheckUninstall.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $root 'healthcheck\src\Uninstall-HealthCheck.ps1'
$work = Join-Path $env:TEMP ("rs-hcuninstall-" + (Get-Date -Format 'HHmmss'))
$taskName = "RS probe - health check uninstall test " + (Get-Random)
$pass = 0; $fail = 0
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "  PASS  $what $(if ($detail) { '- ' + $detail })" }
    else     { $script:fail++; Write-Host "  FAIL  $what $(if ($detail) { '- ' + $detail })" -ForegroundColor Red }
}
function TaskExists { [bool](Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) }
function Run-Uninstall {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -InstallPath $work -TaskName $taskName | Out-Null
    return $LASTEXITCODE
}

New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    # A task whose action runs something out of the temp folder, exactly as ours runs
    # Invoke-MonthlyRun.ps1 out of C:\ProgramData\HealthCheck.
    $action  = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -File `"$work\Invoke-MonthlyRun.ps1`""
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date '2099-01-01T08:00:00')
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger | Out-Null
    Check 'the probe task was registered' (TaskExists)

    Write-Host ''
    Write-Host '== somebody else''s health check: no Platform section in config.json =='
    '{ "Organisation": "Test", "Email": { "To": "someone@example.com" } }' | Set-Content (Join-Path $work 'config.json') -Encoding UTF8
    $rc = Run-Uninstall
    Check 'it says so with exit code 3' ($rc -eq 3) "exit $rc"
    Check 'and the task is still there' (TaskExists)

    Write-Host ''
    Write-Host '== ours: config.json names a platform server =='
    '{ "Organisation": "Test", "Platform": { "BaseUrl": "http://127.0.0.1:1" } }' | Set-Content (Join-Path $work 'config.json') -Encoding UTF8
    $rc = Run-Uninstall
    Check 'it succeeds' ($rc -eq 0) "exit $rc"
    Check 'and the task is gone' (-not (TaskExists))
    Check 'the results folder and config are kept' (Test-Path (Join-Path $work 'config.json'))

    Write-Host ''
    Write-Host '== run again, with nothing left to remove =='
    $rc = Run-Uninstall
    Check 'it is not an error' ($rc -eq 0) "exit $rc"

    Write-Host ''
    Write-Host '== no health check at all =='
    Remove-Item (Join-Path $work 'config.json') -Force
    $rc = Run-Uninstall
    Check 'nothing to do is exit 0' ($rc -eq 0) "exit $rc"
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ''
Write-Host "$pass of $($pass + $fail) checks passed."
exit $(if ($fail -eq 0) { 0 } else { 1 })
