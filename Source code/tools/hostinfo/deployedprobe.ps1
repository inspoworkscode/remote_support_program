# What is actually installed and running on THIS machine, and what has the agent been saying?
#
# Written after Ctrl+Alt+Del "still did nothing" following a ship: the fix moved that work into
# the AGENT, so the question is no longer about the host at all - it is whether the agent binary
# was replaced and whether the service was restarted to pick it up. A service whose exe is new but
# whose process is old behaves exactly like one that was never updated.
#
# Pure ASCII (docs/HANDOVER.md section 10).

$ErrorActionPreference = 'SilentlyContinue'

Write-Output "=== installed binaries ==="
$dir = 'C:\Program Files\Anywhere IT\Remote Support'
if (-not (Test-Path $dir)) { Write-Output "NOT INSTALLED at $dir" }
else {
    foreach ($n in 'RemoteSupport.Host.exe','RemoteSupport.Agent.exe','RemoteSupport.Client.exe') {
        $f = Join-Path $dir $n
        if (Test-Path $f) { Write-Output ("{0,-30} built {1}" -f $n, (Get-Item $f).LastWriteTime) }
        else { Write-Output ("{0,-30} MISSING" -f $n) }
    }
}

Write-Output ""
Write-Output "=== the agent service ==="
$svc = Get-CimInstance Win32_Service -Filter "Name = 'RemoteSupportAgent'"
if (-not $svc) { Write-Output "no RemoteSupportAgent service" }
else {
    Write-Output ("state   : " + $svc.State + "   account: " + $svc.StartName)
    Write-Output ("pid     : " + $svc.ProcessId)
    if ($svc.ProcessId -gt 0) {
        $p = Get-Process -Id $svc.ProcessId
        if ($p) {
            Write-Output ("started : " + $p.StartTime)
            Write-Output ("session : " + $p.SessionId)
            Write-Output ("NOTE    : if 'started' is EARLIER than the Agent.exe build time above,")
            Write-Output ("          the running service is still the OLD binary.")
        }
    }
}

Write-Output ""
Write-Output "=== what the agent has logged (Windows event log) ==="
$ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddHours(-3) } -MaxEvents 400 |
      Where-Object { $_.Message -match 'Ctrl\+Alt\+Del|SendSAS|system action|RemoteSupport|Agent starting' }
if ($ev) { $ev | Select-Object -First 15 | ForEach-Object { Write-Output ("{0}  [{1}]  {2}" -f $_.TimeCreated, $_.ProviderName, ($_.Message -split "`n")[0]) } }
else { Write-Output "(nothing in the last 3 hours mentioning the agent or Ctrl+Alt+Del)" }

Write-Output ""
Write-Output "=== has any system action reached the HOST since the ship? ==="
$log = Join-Path $env:ProgramData 'RemoteSupport\host-trace.log'
if (Test-Path $log) {
    $acts = Select-String -Path $log -Pattern 'sys action' -SimpleMatch
    if ($acts) { $acts | Select-Object -Last 8 | ForEach-Object { Write-Output $_.Line } }
    else { Write-Output "(none ever)" }
}

exit 0
