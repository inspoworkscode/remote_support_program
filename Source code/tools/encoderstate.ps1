# encoderstate.ps1 - read-only. Which graphics card and which H.264 encoder this machine's host
# uses, and what the host's trace says about the picture. For "the console only shows a change
# after Refresh" (2026-10-06): a hardware encoder that holds the last frame of a change.
# Paste into a session's Scripts tab, or: runscript <machine> encoderstate.ps1
$ErrorActionPreference = 'SilentlyContinue'
$os = Get-CimInstance Win32_OperatingSystem
"OS: $($os.Caption) $($os.Version) build $($os.BuildNumber)"
"Machine: $env:COMPUTERNAME"
""
"Graphics:"
Get-CimInstance Win32_VideoController | ForEach-Object {
    "  $($_.Name) - driver $($_.DriverVersion), dated $(if ($_.DriverDate) { $_.DriverDate.ToString('yyyy-MM-dd') } else { 'unknown' }), $($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)"
}
""
"Host trace (capture and codec lines, newest last):"
$log = Join-Path $env:ProgramData 'RemoteSupport\host-trace.log'
if (Test-Path $log) {
    Get-Content $log -Tail 4000 | Where-Object { $_ -match 'capture:|codec:|encoder|link:' } | Select-Object -Last 25 | ForEach-Object { "  $_" }
} else { "  no trace at $log" }
