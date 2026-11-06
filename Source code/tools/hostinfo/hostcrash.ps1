# Did RemoteSupport.Host.exe crash on this machine, and with what? Read-only.
#
# Written 2026-09-29: on DESKTOP-2N6FVGT the hub records "Client disconnected" - the HOST's socket
# dropping - two to four seconds after every switch to the Winlogon desktop, and the host's own
# trace just stops. A process that dies of an unhandled exception cannot write its own last line,
# but Windows writes one for it: .NET Runtime (event 1026), Application Error (1000) and Windows
# Error Reporting (1001). This prints those for the host and the agent from the last day.
#
# Pure ASCII: PowerShell 5.1 reads a BOM-less file as ANSI (docs/HANDOVER.md section 10).

$ErrorActionPreference = 'SilentlyContinue'
$since = (Get-Date).AddDays(-1)
Write-Output ("machine: {0}   now: {1}" -f $env:COMPUTERNAME, (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
$ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since; Id = 1000, 1001, 1002, 1026 } -MaxEvents 300
$mine = $ev | Where-Object { $_.Message -match 'RemoteSupport' } | Sort-Object TimeCreated
if (-not $mine) { Write-Output "no crash, hang or .NET runtime error for any RemoteSupport process in the last day" }
foreach ($e in ($mine | Select-Object -Last 12)) {
    Write-Output ""
    Write-Output ("=== {0}  {1} (event {2}) ===" -f $e.TimeCreated.ToString('HH:mm:ss'), $e.ProviderName, $e.Id)
    $lines = $e.Message -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }
    $lines | Select-Object -First 40 | ForEach-Object { Write-Output ("  " + $_) }
}

Write-Output ""
Write-Output "=== anything else in the System log at the same moments (driver resets, display) ==="
$sys = Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = (Get-Date).AddHours(-2); Level = 1, 2, 3 } -MaxEvents 60
foreach ($e in ($sys | Sort-Object TimeCreated | Select-Object -Last 20)) {
    $m = ($e.Message -split "`r?`n")[0]; if ($m.Length -gt 150) { $m = $m.Substring(0, 150) }
    Write-Output ("  {0}  {1} {2}: {3}" -f $e.TimeCreated.ToString('HH:mm:ss'), $e.ProviderName, $e.Id, $m)
}
