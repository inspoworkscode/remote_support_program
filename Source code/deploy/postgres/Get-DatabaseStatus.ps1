# =====================================================================================
# Which database a server's platform is on, its disks, counts and backups - read-only (2026-10-09).
#
#   powershell -ExecutionPolicy Bypass -File deploy\postgres\Get-DatabaseStatus.ps1 -Server 1.2.3.4
#
# Changes nothing; safe on the live server at any time. Saved in deploy\reports as well.
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$User = "ubuntu"
)
$ErrorActionPreference = "Stop"
$reports = Join-Path (Split-Path $PSScriptRoot -Parent) "reports"
New-Item -ItemType Directory -Path $reports -Force | Out-Null
$log = Join-Path $reports ("status-{0}-{1}.log" -f $Server, (Get-Date -Format "yyyyMMdd-HHmmss"))
Write-Host "== $Server ==  (type your key's passphrase now)" -ForegroundColor Cyan
$ErrorActionPreference = "Continue"
Get-Content (Join-Path $PSScriptRoot "database-status.sh") | ssh "$User@$Server" "tr -d '\r' | sudo bash -s" | Tee-Object -FilePath $log | Out-Host
Write-Host "Saved: $log"
