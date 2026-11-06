$ErrorActionPreference='SilentlyContinue'
"host processes now:"
Get-Process RemoteSupport.Host | ForEach-Object { "  PID $($_.Id)  started $($_.StartTime.ToString('HH:mm:ss'))" }
"agent processes:"
Get-Process RemoteSupport.Agent | ForEach-Object { "  PID $($_.Id)  started $($_.StartTime.ToString('HH:mm:ss'))" }
"now: $(Get-Date -Format HH:mm:ss)"
$p='C:\Temp\codecbench\RemoteSupport.Host.exe'
"partial file present: $(Test-Path $p)"
