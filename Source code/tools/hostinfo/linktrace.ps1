$ErrorActionPreference='SilentlyContinue'
$p='C:\ProgramData\RemoteSupport\host-trace.log'
"--- every link/codec/capture line from the last two hosts ---"
Get-Content $p | Where-Object { $_ -match 'link:|codec:|capture:|host started' } | Select-Object -Last 16
$n = (Get-Content $p | Where-Object { $_ -match 'link:' }).Count
"total 'link:' lines ever written: $n"
