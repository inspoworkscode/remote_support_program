<#
    Launches Test-HealthCheckReinstall.ps1 elevated, and keeps the window open.

        powershell -ExecutionPolicy Bypass -File tools\Run-ReinstallTest.ps1

    This exists because quoting a path with spaces through Start-Process is easy to get wrong and
    fails invisibly: -ArgumentList joins its elements with spaces and quotes none of them, so
    "C:\...\Remote Support\..." arrived as two arguments, powershell.exe could not find the file,
    and the elevated window closed before -NoExit could keep it open. Here the quoting is written
    once, in a file, where it can be read and tested.
#>
param(
    # Print the command line instead of running it - so the quoting can be checked without
    # raising a UAC prompt.
    [switch]$ShowOnly
)

$ErrorActionPreference = 'Stop'

if (-not $PSScriptRoot) {
    Write-Host "Run this as a file, not by pasting it:" -ForegroundColor Red
    Write-Host '    powershell -ExecutionPolicy Bypass -File tools\Run-ReinstallTest.ps1'
    exit 1
}

$test = Join-Path $PSScriptRoot 'Test-HealthCheckReinstall.ps1'
if (-not (Test-Path $test)) { throw "Cannot find $test" }

# Each element is one argument. The path is wrapped in literal double quotes HERE, because
# Start-Process will not add them.
$argList = @('-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $test + '"'))

if ($ShowOnly) {
    Write-Host ""
    Write-Host "Would run, elevated:" -ForegroundColor Cyan
    Write-Host "  powershell $($argList -join ' ')"
    Write-Host ""
    exit 0
}

Write-Host ""
Write-Host "Asking for administrator rights - approve the prompt." -ForegroundColor Cyan
Write-Host "The elevated window stays open so you can read the result." -ForegroundColor Gray
Write-Host ""
Start-Process powershell -Verb RunAs -ArgumentList $argList
