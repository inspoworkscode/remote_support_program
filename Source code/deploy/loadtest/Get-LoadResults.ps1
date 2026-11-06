# =====================================================================================
# Bring every run's results home from the load boxes, if a LOAD BOX window was closed (or left at its
# second passphrase) before it copied them itself. Safe to run any time; it copies, never deletes.
#
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Get-LoadResults.ps1
# =====================================================================================
param([string]$User = "ubuntu")
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Servers.ps1")
$s = Get-LoadServers
$results = Join-Path $PSScriptRoot "out\results"
New-Item -ItemType Directory -Path $results -Force | Out-Null
foreach ($b in @($s.Box1, $s.Box2, $s.Box3) | Where-Object { $_ }) {
    Write-Host "== Copying the results from $b ==  (type your key's passphrase now)" -ForegroundColor Cyan
    scp "${User}@${b}:loadtest/run-*" $results
    if ($LASTEXITCODE -ne 0) { Write-Host "   nothing copied from $b - see above" -ForegroundColor Yellow }
}
Write-Host "`nResults in $results" -ForegroundColor Green
Get-ChildItem $results -Filter "run-*.txt" | ForEach-Object { Write-Host "   $($_.Name)" }
