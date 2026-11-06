# Sit still for 10 seconds with nothing subscribed, then read this session's own trace.
# runscript never sends a SubscribeMsg, so this host has no viewer on any monitor for its whole
# life -- which is exactly the condition the idle release is supposed to act on.
$ErrorActionPreference='SilentlyContinue'
$p='C:\ProgramData\RemoteSupport\host-trace.log'
$me = (Get-Process -Id $PID).Parent
"parent    : $($me.ProcessName) pid $($me.Id) started $($me.StartTime.ToString('HH:mm:ss.fff'))"
$before = (Get-Item $p).Length
"waiting 10 s with nothing subscribed..."
Start-Sleep -Seconds 10
"--- trace written by THIS host since it started ---"
Get-Content $p | Where-Object { $_ -match 'capture:|codec:|host started' } | Select-Object -Last 8
