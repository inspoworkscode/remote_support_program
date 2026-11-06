# Read-only. Run this on the CUSTOMER machine through a live session's script runner when
# the screen is visible but mouse and keyboard do nothing.
#
# Input and capture fail for different reasons (docs/HANDOVER.md 4). This reports which of
# them is in play: who the host process is running as, whether the unattended agent is there
# at all, and what the host's own trace says about the desktop it is attached to.

$ErrorActionPreference = 'SilentlyContinue'

Write-Output "=== machine ==="
Write-Output ("computer   : " + $env:COMPUTERNAME)
Write-Output ("local time : " + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))

Write-Output ""
Write-Output "=== the unattended agent (its absence means sessions fall back to the tray app) ==="
$svc = Get-Service -Name 'RemoteSupportAgent' -ErrorAction SilentlyContinue
if ($svc) { Write-Output ("service    : {0} / start {1}" -f $svc.Status, $svc.StartType) }
else      { Write-Output "service    : NOT INSTALLED" }

Write-Output ""
Write-Output "=== processes and the account each runs as ==="
Get-CimInstance Win32_Process -Filter "Name LIKE 'RemoteSupport%'" | ForEach-Object {
    $owner = try { (Invoke-CimMethod -InputObject $_ -MethodName GetOwner) } catch { $null }
    $who = if ($owner -and $owner.User) { "$($owner.Domain)\$($owner.User)" } else { "(unknown)" }
    $sess = (Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue).SessionId
    Write-Output ("{0,-32} pid {1,-7} session {2,-3} running as {3}" -f $_.Name, $_.ProcessId, $sess, $who)
}

Write-Output ""
Write-Output "=== host-trace.log ==="
$p = Join-Path $env:ProgramData 'RemoteSupport\host-trace.log'
if (Test-Path $p) {
    Write-Output ("last written: " + (Get-Item $p).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
    Write-Output "--- last 40 lines ---"
    Get-Content $p -Tail 40
} else {
    Write-Output "NOT FOUND: $p"
    Write-Output "No host trace means no host process has started on this machine since install."
}

Write-Output ""
Write-Output "=== install ==="
foreach ($d in @("$env:ProgramFiles\Anywhere IT\Remote Support", "${env:ProgramFiles(x86)}\Anywhere IT\Remote Support")) {
    if (Test-Path $d) {
        Write-Output "folder: $d"
        Get-ChildItem $d -Filter '*.exe' | ForEach-Object {
            Write-Output ("  {0,-34} {1}" -f $_.Name, $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
        }
    }
}
$idf = Join-Path $env:ProgramData 'RemoteSupport\device.json'
Write-Output ("device.json: " + $(if (Test-Path $idf) { (Get-Item $idf).LastWriteTime.ToString('yyyy-MM-dd HH:mm') } else { 'missing' }))
