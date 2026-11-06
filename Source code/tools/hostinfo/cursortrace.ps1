# Give this host's cursor loop a moment, then read what it said about itself.
$ErrorActionPreference='SilentlyContinue'
Start-Sleep -Seconds 6
$p='C:\ProgramData\RemoteSupport\host-trace.log'
"--- cursor lines from this host ---"
Get-Content $p | Where-Object { $_ -match 'cursor:|host started' } | Select-Object -Last 10
