# Prints the host's own diagnostic trace from this machine, newest last.
#
# Why it exists: the host logs what account it runs as, whether it could open the input desktop,
# every desktop switch, and the outcome of every system action (reboot / lock / Ctrl+Alt+Del).
# When one of those "does not work", this file is the evidence and guessing is not.
#
# Pure ASCII on purpose: Windows PowerShell 5.1 reads a BOM-less file as ANSI, and the host writes
# the script out without one (see docs/HANDOVER.md section 10).

$ErrorActionPreference = 'SilentlyContinue'

Write-Output "=== identity of the process running this script ==="
Write-Output ("user       : " + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
Write-Output ("session id : " + (Get-Process -Id $PID).SessionId)

$log = Join-Path $env:ProgramData 'RemoteSupport\host-trace.log'
Write-Output ""
Write-Output "=== $log ==="
if (-not (Test-Path $log)) {
    Write-Output "NOT PRESENT - no host has ever run on this machine, or the folder was removed."
}
else {
    $fi = Get-Item $log
    Write-Output ("size " + $fi.Length + " bytes, last written " + $fi.LastWriteTime)
    Write-Output ""
    Write-Output "--- every system action ever attempted ---"
    $acts = Select-String -Path $log -Pattern 'sys action' -SimpleMatch
    if ($acts) { $acts | ForEach-Object { Write-Output $_.Line } } else { Write-Output "(none - no reboot, lock or Ctrl+Alt+Del has ever reached this host)" }
    Write-Output ""
    Write-Output "--- identity lines (one per session) ---"
    $ids = Select-String -Path $log -Pattern 'running as|isSystem|input desktop|input thread'
    if ($ids) { $ids | Select-Object -Last 12 | ForEach-Object { Write-Output $_.Line } } else { Write-Output "(none)" }
    Write-Output ""
    Write-Output "--- last 25 lines ---"
    Get-Content $log -Tail 25 | ForEach-Object { Write-Output $_ }
}

Write-Output ""
Write-Output "=== which Host.exe is installed, and when it was built ==="
Get-ChildItem 'C:\Program Files' -Recurse -Filter 'RemoteSupport.Host.exe' -Depth 3 |
    ForEach-Object { Write-Output ("{0}  {1}" -f $_.LastWriteTime, $_.FullName) }

# Program Files can throw access-denied on folders we do not care about; never let that become
# the exit code, because the caller reports a non-zero exit as "the script failed".
exit 0
