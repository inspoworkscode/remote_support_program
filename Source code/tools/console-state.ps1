<#
    What console is actually running, which build it is, and what it last wrote.

        powershell -ExecutionPolicy Bypass -File .\tools\console-state.ps1

    WHY THIS EXISTS. On 2026-09-18 the session window froze and copy-and-paste did nothing, and
    three rounds of diagnosis went past each other because two basic facts were never established:
    WHICH BINARY was running, and WHERE it was writing its trace. The trace on this laptop
    contained nothing but harness sessions, which was read as "the console must be on another
    machine" — a conclusion built on an assumption rather than a measurement, and Clint said so.

    So this asks the machine rather than reasoning about it. Run it ON THE MACHINE RUNNING THE
    CONSOLE, while the problem is happening if you can — Responding=False is the freeze, caught in
    the act.
#>
$ErrorActionPreference = 'Continue'

function Head($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }

Head 'Consoles running right now'
$procs = @(Get-Process RemoteSupport.Technician -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) {
    Write-Host '   none. Start the console and run this again while it is up.' -ForegroundColor Yellow
} else {
    foreach ($p in $procs) {
        $exe = $null; try { $exe = $p.Path } catch { }
        $built = if ($exe -and (Test-Path $exe)) { (Get-Item $exe).LastWriteTime } else { 'unknown' }
        Write-Host ("   pid {0}  started {1}" -f $p.Id, $p.StartTime)
        # Responding=False IS the freeze. It is the same thing the taskbar is telling you when it
        # beeps instead of restoring the window.
        $colour = if ($p.Responding) { 'Green' } else { 'Red' }
        Write-Host ("   responding: {0}" -f $p.Responding) -ForegroundColor $colour
        Write-Host ("   exe:        {0}" -f $exe)
        Write-Host ("   exe built:  {0}" -f $built) -ForegroundColor Yellow
        Write-Host ("   window:     '{0}'" -f $p.MainWindowTitle)
    }
}

Head 'Every trace this machine has'
# Two folder names exist in the wild: the current one, and AnywhereIT from before the product was
# renamed. A console writing to one while somebody reads the other explains an empty log.
$traces = @()
foreach ($dir in 'TheRemoteSupportProgram', 'AnywhereIT') {
    $f = Join-Path $env:LOCALAPPDATA "$dir\technician-trace.log"
    if (Test-Path $f) { $traces += Get-Item $f }
}
if ($traces.Count -eq 0) {
    Write-Host '   NONE. The console has never written a trace on this machine.' -ForegroundColor Red
    Write-Host '   That alone answers the question: the console doing the work is not this one.' -ForegroundColor Red
} else {
    foreach ($t in $traces) {
        Write-Host ("   {0}" -f $t.FullName)
        Write-Host ("      last written {0}   {1:N0} KB" -f $t.LastWriteTime, ($t.Length / 1KB))
    }
}

$newest = $traces | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newest) {
    Head 'Sessions that trace has ever opened'
    $sessions = Select-String -Path $newest.FullName -Pattern 'form: opening session .* for (\S+) ' -AllMatches |
                ForEach-Object { $_.Matches[0].Groups[1].Value } | Group-Object | Sort-Object Count -Descending
    if (-not $sessions) { Write-Host '   none at all' -ForegroundColor Yellow }
    foreach ($s in $sessions) {
        # PROBE-PC and PASTE-PC are the harnesses, not you. A trace with only those in it has
        # never seen a real support session.
        $note = if ($s.Name -in 'PROBE-PC', 'PASTE-PC') { '   <- a harness, not a real session' } else { '' }
        Write-Host ("   {0,-20} {1,4} time(s){2}" -f $s.Name, $s.Count, $note)
    }

    Head 'The last 40 lines'
    Get-Content $newest.FullName -Tail 40

    Head 'Anything the watchdog said'
    $w = Select-String -Path $newest.FullName -Pattern 'watchdog:' | Select-Object -Last 10
    if (-not $w) {
        Write-Host '   nothing. Either the console never froze, or it is a build older than' -ForegroundColor Yellow
        Write-Host '   2026-09-18 and has no watchdog in it at all.' -ForegroundColor Yellow
    } else { $w | ForEach-Object { Write-Host ("   " + $_.Line) } }
}

Head 'The builds on this machine, for comparison'
$root = Split-Path -Parent $PSScriptRoot
foreach ($p in @('demo\technician\RemoteSupport.Technician.exe',
                 'dist\technician\RemoteSupport.Technician.exe',
                 'src\RemoteSupport.Platform\bin\Release\net10.0\payloads\technician\RemoteSupport.Technician.exe')) {
    $full = Join-Path $root $p
    if (Test-Path $full) { Write-Host ("   {0,-72} {1}" -f $p, (Get-Item $full).LastWriteTime) }
    else { Write-Host ("   {0,-72} missing" -f $p) -ForegroundColor DarkGray }
}
Write-Host ''
Write-Host 'The exe the running console was built from is the number that matters. If it is older' -ForegroundColor Gray
Write-Host 'than the last ship, nothing that was fixed since is in it.' -ForegroundColor Gray
