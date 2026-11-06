# What the host did around desktop switches: Ctrl+Alt+Del, UAC, the lock screen. Read-only.
#
# Written 2026-09-29: Ctrl+Alt+Del "does not seem to work" on DESKTOP-2N6FVGT, yet the input
# desktop is measured switching to Winlogon when it is sent (sysactioncheck --observe). So the
# question moved to the technician's side of it: did the host follow the switch, and did capture
# keep producing a picture on the secure desktop? This prints the lines that answer that, from
# the last several host runs, rather than the last 25 lines of whatever happened most recently.
#
# Pure ASCII: PowerShell 5.1 reads a BOM-less file as ANSI (docs/HANDOVER.md section 10).

$ErrorActionPreference = 'SilentlyContinue'
$log = Join-Path $env:ProgramData 'RemoteSupport\host-trace.log'
if (-not (Test-Path $log)) { Write-Output "NO host trace at $log"; exit 0 }
$lines = Get-Content $log
Write-Output ("{0}: {1} lines, last written {2}" -f $log, $lines.Count, (Get-Item $log).LastWriteTime)
$old = $log + '.1'
if (Test-Path $old) { Write-Output ("previous file {0}: {1} lines" -f $old, (Get-Content $old).Count) }
Write-Output ""
$lines | Select-Object -Last 900 |
    Where-Object { $_ -match 'host started|desktop|Winlogon|secure|capture|duplication|DXGI|GDI|codec:|input thread|followed|sys action|stalled|failed|FAILED|error' -and $_ -notmatch 'cursor:' } |
    Select-Object -Last 200 |
    ForEach-Object { Write-Output ("  " + $_) }
