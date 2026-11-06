# =====================================================================================
# The restart storm (2026-10-12): restart the platform on the LOAD-TEST server while a step is running, as
# every deploy does on live, so the load boxes can time every machine, console and session coming back.
#
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Restart-LoadServer.ps1
#
# Run it when the LOAD BOX windows of a -Storm step say "READY FOR THE RESTART". Refuses the live server.
# =====================================================================================
param(
    [string]$Server,
    [string]$User = "ubuntu"
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Servers.ps1")
if (-not $Server) { $Server = (Get-LoadServers).Server }
$liveIps = @("154.65.110.136")
try { $liveIps += [System.Net.Dns]::GetHostAddresses("theremotesupportprogram.com") | ForEach-Object { $_.IPAddressToString } } catch { }
if ($Server -in $liveIps) { throw "$Server is the LIVE server. Never restart it for a load test." }
Write-Host "== Restarting the platform on $Server ==  (type your key's passphrase now)" -ForegroundColor Cyan
ssh "$User@$Server" "date -u +%T; sudo systemctl restart remotesupport-platform && echo restarted at; date -u +%T"
if ($LASTEXITCODE -ne 0) { throw "the restart failed - see above" }
Write-Host "`nRestarted. Leave the LOAD BOX windows alone until they say 'Results:'." -ForegroundColor Green
