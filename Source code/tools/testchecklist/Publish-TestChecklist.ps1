<#
    Puts the test checklist on the shared drive, with the checklist as it stands in the repo.

    First time (the folder is remembered after that):
        powershell -ExecutionPolicy Bypass -File .\tools\testchecklist\Publish-TestChecklist.ps1 -To "\\SERVER\Share\Testing"

    After that:
        powershell -ExecutionPolicy Bypass -File .\tools\testchecklist\Publish-TestChecklist.ps1

    -ChecksOnly   copy docs\TEST-3.6.md across as checks.md and nothing else (seconds, not a build).
                  Everyone's open copy picks the new checklist up by itself within 15 seconds.

    It never touches data\ or exports\ on the share — those are the testers' results.
#>
param(
    [string]$To,
    [switch]$ChecksOnly
)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$remember = Join-Path $here 'share-path.txt'

if (-not $To) {
    if (Test-Path $remember) { $To = (Get-Content $remember -Raw).Trim() }
    else { throw 'Say where the shared folder is the first time: -To "\\SERVER\Share\Testing"' }
}
if (-not (Test-Path $To)) {
    New-Item -ItemType Directory -Path $To | Out-Null
    Write-Host "Made $To"
}
Set-Content -Path $remember -Value $To

if (-not $ChecksOnly) {
    $out = Join-Path $env:TEMP 'testchecklist-publish'
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Write-Host 'Building TestChecklist.exe (one self-contained file, so nobody needs .NET installed)...'
    & dotnet publish (Join-Path $here 'testchecklist.csproj') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -o $out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The build failed - see above.' }

    $exe = Join-Path $To 'TestChecklist.exe'
    try {
        Copy-Item (Join-Path $out 'TestChecklist.exe') $exe -Force
    }
    catch {
        # Somebody has it open. A running exe cannot be overwritten, but it can be renamed out of
        # the way; they carry on with the old one and get the new one next time they open it.
        $old = Join-Path $To ('TestChecklist.old-{0:yyyyMMdd-HHmmss}.exe' -f (Get-Date))
        Rename-Item $exe $old
        Copy-Item (Join-Path $out 'TestChecklist.exe') $exe -Force
        Write-Host 'Someone had it open - they keep the old copy until they close it.'
    }
    Get-ChildItem $To -Filter 'TestChecklist.old-*.exe' | ForEach-Object {
        try { Remove-Item $_.FullName -ErrorAction Stop } catch { }
    }
}

Copy-Item (Join-Path $repo 'docs\TEST-3.6.md') (Join-Path $To 'checks.md') -Force
# The browser version, for PCs where Smart App Control refuses the unsigned exe. Chrome only.
Copy-Item (Join-Path $here 'web\TestChecklist.html') (Join-Path $To 'TestChecklist.html') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $To 'data'), (Join-Path $To 'exports') | Out-Null

Write-Host ''
Write-Host "Done. On the share: $To"
Write-Host "  TestChecklist.exe   - what everyone opens"
Write-Host "  TestChecklist.html  - the same, in Chrome, where Smart App Control blocks the exe"
Write-Host "  checks.md           - the checklist (docs\TEST-3.6.md)"
Write-Host "  data\               - everyone's results, faults, upgrades and pictures"
Write-Host "  exports\            - the end-of-day reports"
