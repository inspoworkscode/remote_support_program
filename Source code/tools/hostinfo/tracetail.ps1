$ErrorActionPreference='SilentlyContinue'
$p='C:\ProgramData\RemoteSupport\host-trace.log'
Get-Content $p | Where-Object { $_ -match 'thread died|cursor|link:|codec:|host started' } | Select-Object -Last 20
