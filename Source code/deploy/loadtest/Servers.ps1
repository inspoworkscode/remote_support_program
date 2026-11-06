# Dot-sourced by the load-test scripts: the addresses Save-Addresses.ps1 kept.
function Get-LoadServers {
    $f = Join-Path $PSScriptRoot "out\servers.json"
    if (-not (Test-Path $f)) { throw "No saved addresses yet. Run Save-Addresses.ps1 first." }
    return Get-Content $f -Raw | ConvertFrom-Json
}
