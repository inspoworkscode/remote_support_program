# Run-ElevatedChecks.ps1 - the two checks that need an elevated prompt (2026-10-06).
#
#   powershell -ExecutionPolicy Bypass -File tools\Run-ElevatedChecks.ps1
#
# Asks for elevation once (UAC), then runs, from the builds already made:
#   tools\clipfirst  --station   BUGS #10 against Windows itself: each case in a fresh window
#                                station, which only an administrator may create.
#   tools\printcheck --elevated  remote printing's customer side against the real print spooler: a
#                                technician's printer added, printed to, picked up, removed, and a
#                                left-over one swept. It adds and removes printers called
#                                "Printcheck Tech's printer (printcheck)" - nothing else is touched,
#                                and nothing is printed on paper.
#
# Results go to %TEMP%\rs-elevated-results.txt, where Claude reads them.

param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$results = Join-Path $env:TEMP 'rs-elevated-results.txt'

if (-not $Elevated) {
    $me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Asking Windows for administrator rights (a UAC prompt)...'
        $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated')
        if (Test-Path $results) { Get-Content $results | Select-Object -Last 12 }
        Write-Host ''
        Write-Host "Done. The full results are in $results"
        exit $p.ExitCode
    }
}

$exes = @(
    @{ Name = 'clipfirst --station'; Exe = Join-Path $repo 'tools\clipfirst\bin\Debug\net10.0-windows\clipfirst.exe'; Args = '--station' },
    @{ Name = 'printcheck --elevated'; Exe = Join-Path $repo 'tools\printcheck\bin\Debug\net10.0-windows\printcheck.exe'; Args = '--elevated' }
)
"elevated checks, $(Get-Date -Format 'yyyy-MM-dd HH:mm')" | Set-Content -Encoding utf8 $results
$failed = 0
foreach ($e in $exes) {
    "" | Add-Content -Encoding utf8 $results
    "===== $($e.Name) =====" | Add-Content -Encoding utf8 $results
    if (-not (Test-Path $e.Exe)) { "NOT BUILT: $($e.Exe)" | Add-Content -Encoding utf8 $results; $failed++; continue }
    $out = & $e.Exe $e.Args.Split(' ') 2>&1 | Out-String
    $out | Add-Content -Encoding utf8 $results
    if ($LASTEXITCODE -ne 0) { $failed++ }
}
"" | Add-Content -Encoding utf8 $results
"$failed of $($exes.Count) failed" | Add-Content -Encoding utf8 $results
exit $failed
