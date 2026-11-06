# =====================================================================================
# Watch the load-test server every 5 s while a run is going: processor (platform, Caddy, PostgreSQL, the
# machine, steal), memory and swap, network, connections and the ports left for Caddy to reach the platform,
# PostgreSQL's connections against the platform's pool of 40, lock waits, the longest query, commits and slow
# statements, and the database disk (rsp-loadstats.sh). Keep it open in its own window; Ctrl+C to stop.
# Kept in deploy\loadtest\out\results\.
#
#   powershell -ExecutionPolicy Bypass -File deploy\loadtest\Watch-LoadServer.ps1
# =====================================================================================
param(
    [string]$Server,
    [string]$Label = "",
    [string]$User = "ubuntu"
)
. (Join-Path $PSScriptRoot "Servers.ps1")
if (-not $Server) { $Server = (Get-LoadServers).Server }
$host.UI.RawUI.WindowTitle = "SERVER - watching rsp-loadtest"
$out = Join-Path $PSScriptRoot "out\results"
New-Item -ItemType Directory -Path $out -Force | Out-Null
$file = Join-Path $out "server-$(Get-Date -Format yyyyMMdd-HHmmss)$(if ($Label) { "-$Label" }).csv"
Write-Host "Watching $Server - saved to $file. Ctrl+C to stop.  (type your key's passphrase now)" -ForegroundColor Cyan
Write-Host "The first lines say what the server is (cores, memory, database, ports); then one line every 5 s."
# Each line to the screen and to the file as UTF-8 (Windows PowerShell's Tee-Object writes UTF-16, 2026-10-12).
ssh -t "$User@$Server" "sudo /usr/local/sbin/rsp-loadstats" | ForEach-Object { $_; [System.IO.File]::AppendAllText($file, "$_`n") }
