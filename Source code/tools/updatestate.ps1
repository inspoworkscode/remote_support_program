<#
.SYNOPSIS
    Why has this machine not updated itself? Read-only.

.DESCRIPTION
    Run this ON the customer's machine when the console shows it on an older version than the
    server hands out (auto-update, HANDOVER 148).

    A machine updates in this order, and this script reports each link from where the machine
    stands: the agent service is running and is a build WITH the updater; the server offers a
    newer release; the agent downloaded it into update\<version>; it checked every file; it waited
    until nobody had been connected for 10 minutes; it started the apply step (update\apply.log);
    the new agent came back (healthy-<version>) or it rolled back; then the tray app restarts
    into its new file when nobody is using it. The console shows the OLDER of the agent and the
    tray app, so a tray app that never restarted looks exactly like an update that never came.

    The agent writes its update steps to the server, not to the Event Log (which only takes its
    warnings), so the files in update\ are the machine's own record.

    It changes nothing: no service is started or stopped, nothing is downloaded but a few bytes
    of the server's answer, no file is written except the report on the Desktop.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\updatestate.ps1

    Or paste the whole of it into the console's Tools -> Scripts (PowerShell) and Run: it then runs as
    SYSTEM in the person's session. No param() block on purpose — the Scripts tool puts a line of its
    own ahead of every script, and param() must be the first statement or nothing runs.
#>

# Only for testing this script against a copy: the install folder, instead of the service's.
$InstallDir = if ($args.Count -gt 0) { [string]$args[0] } else { "" }

$ErrorActionPreference = 'Continue'
$lines = New-Object System.Collections.Generic.List[string]
$findings = New-Object System.Collections.Generic.List[string]
function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c; $lines.Add($t) }
function Head($t) { Say ""; Say "== $t ==" Cyan }
function Good($t) { Say "   $t" Green }
function Bad ($t) { Say "   $t" Red }
function Note($t) { Say "   $t" Gray }

# A stamped version is yyyy.mm.dd.hhmm (UTC build time); 1.0.0.0 is any build from before 2026-10-16.
function VersionOf($path) {
    try { $v = (Get-Item -LiteralPath $path -ErrorAction Stop).VersionInfo.FileVersion; if ($v) { return $v.Trim() } } catch { }
    return ""
}
function AsVersion($s) { try { return [version]$s } catch { return $null } }
function Newer($a, $b) { $x = AsVersion $a; $y = AsVersion $b; return ($x -ne $null -and $y -ne $null -and $x -gt $y) }

Say ""
Say "Automatic updates on $env:COMPUTERNAME" White
$isSystem = [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem
Say ("Run {0} (UTC {1}) as {2}\{3}, session {4}" -f (Get-Date).ToString('yyyy-MM-dd HH:mm'), (Get-Date).ToUniversalTime().ToString('HH:mm'), $env:USERDOMAIN, $env:USERNAME, (Get-Process -Id $PID).SessionId) DarkGray
Say ("Logged on here: " + ((Get-CimInstance Win32_ComputerSystem).UserName)) DarkGray

# ------------------------------------------------------------------------------ the agent service
Head "The agent service"
$svc = Get-CimInstance Win32_Service -Filter "Name='RemoteSupportAgent'" -ErrorAction SilentlyContinue
if (-not $svc) {
    Bad "No RemoteSupportAgent service. Nothing on this machine can update it."
    $findings.Add("The agent service is not installed: reinstall the support client.")
} else {
    $exePath = ($svc.PathName -replace '^"([^"]+)".*$', '$1') -replace '^(\S+\.exe).*$', '$1'
    Note "State: $($svc.State), start: $($svc.StartMode)"
    Note "Program: $exePath"
    if ($svc.State -ne 'Running') { Bad "It is not running."; $findings.Add("The agent service is not running, so nothing is offered or installed.") }
    if (-not $InstallDir) { $InstallDir = Split-Path -Parent $exePath }
}
if (-not $InstallDir -or -not (Test-Path -LiteralPath $InstallDir)) {
    Bad "No install folder to read."
    $InstallDir = $null
}

# ------------------------------------------------------------------------------ what is installed
$agentV = ""; $clientV = ""; $hostV = ""
if ($InstallDir) {
    Head "What is installed in $InstallDir"
    $agentV  = VersionOf (Join-Path $InstallDir 'RemoteSupport.Agent.exe')
    $hostV   = VersionOf (Join-Path $InstallDir 'RemoteSupport.Host.exe')
    $clientV = VersionOf (Join-Path $InstallDir 'RemoteSupport.Client.exe')
    Note ("Agent     {0}" -f $(if ($agentV) { $agentV } else { '(missing)' }))
    Note ("Host      {0}" -f $(if ($hostV) { $hostV } else { '(missing)' }))
    Note ("Tray app  {0}" -f $(if ($clientV) { $clientV } else { '(missing)' }))

    foreach ($f in 'RemoteSupport.Agent.exe', 'RemoteSupport.Client.exe') {
        $p = Join-Path $InstallDir $f
        if (Test-Path -LiteralPath $p) {
            $sig = Get-AuthenticodeSignature -LiteralPath $p
            $who = if ($sig.SignerCertificate) { ($sig.SignerCertificate.Subject -split ',')[0] } else { 'nobody' }
            Note ("{0}: signature {1}, signed by {2}" -f $f, $sig.Status, $who)
        }
    }
    $aside = Get-ChildItem -LiteralPath $InstallDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\.(old|bad)$' }
    if ($aside) { Note ("Set aside by an update: " + (($aside | ForEach-Object { "$($_.Name) ($($_.LastWriteTime.ToString('MM-dd HH:mm')))" }) -join ', ')) }

    if ($agentV -eq '1.0.0.0' -or -not $agentV) {
        Bad "This agent is from before version numbers, so it has no updater."
        $findings.Add("The agent cannot update itself (1.0 old): connect, Tools -> Scripts -> Update this machine, once.")
    }
}

# ------------------------------------------------------------------------------ what is running
Head "What is running"
$procs = Get-CimInstance Win32_Process -Filter "Name='RemoteSupport.Agent.exe' OR Name='RemoteSupport.Client.exe' OR Name='RemoteSupport.Host.exe'" -ErrorAction SilentlyContinue
if (-not $procs) { Note "None of the three programs is running." }
foreach ($p in $procs) {
    $v = if ($p.ExecutablePath) { VersionOf $p.ExecutablePath } else { '' }
    $started = try { $p.CreationDate.ToString('MM-dd HH:mm') } catch { '?' }
    Note ("{0,-26} pid {1,-6} started {2}  {3}  {4}" -f $p.Name, $p.ProcessId, $started, $v, $p.ExecutablePath)
}
$runningAgent = $procs | Where-Object { $_.Name -eq 'RemoteSupport.Agent.exe' -and $_.CommandLine -notmatch '--apply-update' } | Select-Object -First 1
$applying = $procs | Where-Object { $_.Name -eq 'RemoteSupport.Agent.exe' -and $_.CommandLine -match '--apply-update' }
if ($applying) { Note "An apply step is running right now: $($applying.CommandLine)" }
$hosts = @($procs | Where-Object { $_.Name -eq 'RemoteSupport.Host.exe' })
if ($hosts.Count -gt 0) {
    Note "$($hosts.Count) session host(s) running. The agent waits until none has run for 10 minutes before it installs."
}
# A tray app still running from its renamed (.old) file, or older than the file in its place, has not restarted into the update.
$trays = @($procs | Where-Object { $_.Name -eq 'RemoteSupport.Client.exe' })
if ($trays.Count -eq 0 -and $clientV) {
    Bad "The tray app is not running."
    $findings.Add("The tray app is not running, so the server still shows the version it last said. It starts at the next logon, from the new file. Start it from its shortcut to see it update now.")
}
foreach ($t in $trays) {
    $tv = if ($t.ExecutablePath) { VersionOf $t.ExecutablePath } else { '' }
    if ($clientV -and $tv -and (Newer $clientV $tv)) {
        Bad "The tray app running (pid $($t.ProcessId)) is $tv, but $clientV is in its place."
        $findings.Add("The tray app has the update on disk but has not restarted into it. It restarts only when no session is open, nobody is ringing and none of its windows is showing (listed below). The console shows the OLDER of agent and tray app, so this alone holds the version back.")
    }
}

# What the tray app itself asks before it restarts (MainForm.AnyWindowShowing): is any window of it visible and not minimised?
# Asked here per process rather than per thread, so this list is never shorter than what it sees.
if ($trays.Count -gt 0) {
    Head "The tray app's windows on screen"
    Add-Type -ErrorAction SilentlyContinue -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class RsWin {
    delegate bool Cb(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(Cb cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
    [StructLayout(LayoutKind.Sequential)] struct R { public int L, T, Ri, B; }
    public static List<string> Showing(uint pid) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h) && !IsIconic(h)) {
                var t = new StringBuilder(256); GetWindowText(h, t, 256);
                var c = new StringBuilder(256); GetClassName(h, c, 256);
                R r; GetWindowRect(h, out r);
                list.Add(string.Format("\"{0}\"  class {1}  at {2},{3} size {4}x{5}", t, c, r.L, r.T, r.Ri - r.L, r.B - r.T));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@
    foreach ($t in $trays) {
        $shown = [RsWin]::Showing([uint32]$t.ProcessId)
        if ($shown.Count -eq 0) { Good "pid $($t.ProcessId): none showing (as seen from this session)." }
        else {
            foreach ($w in $shown) { Bad "pid $($t.ProcessId): $w" }
            $findings.Add("The tray app has a window on screen, so it will not restart into its update: " + ($shown -join '; ') + ". Close it (or, if it is invisible or off screen, that is the fault).")
        }
    }
}

# ------------------------------------------------------------------------------ what the server offers
$offer = ""
if ($InstallDir) {
    Head "What the server hands out"
    $brand = $null
    foreach ($b in (Join-Path $InstallDir 'branding\brand.json'), (Join-Path $InstallDir 'brand.json')) {
        if (-not $brand -and (Test-Path -LiteralPath $b)) { try { $brand = Get-Content -LiteralPath $b -Raw | ConvertFrom-Json } catch { } }
    }
    if (-not $brand -or -not $brand.serverUrl) { Bad "No brand.json with a server address in the install folder." }
    else {
        $http = ($brand.serverUrl.TrimEnd('/') -replace '^wss://', 'https://' -replace '^ws://', 'http://')
        Note "Server: $http"
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            $req = [Net.HttpWebRequest]::Create("$http/update/client.exe")
            $req.Method = 'GET'; $req.AddRange(0, 0); $req.Timeout = 15000
            $resp = $req.GetResponse()
            Note ("The release file is there: {0:N0} bytes." -f [long](($resp.Headers['Content-Range'] -split '/')[-1]))
            $resp.Close()
        } catch { Bad "Could not fetch the release from this machine: $($_.Exception.Message)"; $findings.Add("This machine cannot download $http/update/client.exe: $($_.Exception.Message)") }
        # The console's offer carries the release's version, and the client's is the same build. Who it is offered to
        # (the ring) is decided by the company; the slug is not in brand.json, so this asks about Anywhere IT's ring.
        try {
            $c = Invoke-RestMethod -Uri "$http/update/console.json?slug=anywhereit" -TimeoutSec 15 -UseBasicParsing
            if ($c.version) { $offer = $c.version; Note "Release on the server: $offer" }
        } catch { Note "Could not ask the server for its release version: $($_.Exception.Message)" }
    }
    if ($offer -and $agentV) {
        if (Newer $offer $agentV) { Note "The agent ($agentV) is older than the release ($offer): it should be offered it." }
        else { Good "The agent ($agentV) is already on the release." }
    }
}

# ------------------------------------------------------------------------------ the agent's own record
if ($InstallDir) {
    $u = Join-Path $InstallDir 'update'
    Head "The update folder ($u)"
    if (-not (Test-Path -LiteralPath $u)) {
        Note "There is no update folder: this agent has never been offered an update, or never started one."
        if ($agentV -and $agentV -ne '1.0.0.0' -and $offer -and (Newer $offer $agentV)) {
            $findings.Add("Nothing on this machine shows an offer ever arrived. Look at the server's history for it on /owner/updates.")
        }
    } else {
        Get-ChildItem -LiteralPath $u -Force -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | ForEach-Object {
            $size = if ($_.PSIsContainer) { "{0:N0} MB" -f ((Get-ChildItem -LiteralPath $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB) } else { "{0:N0} bytes" -f $_.Length }
            Note ("{0,-36} {1}  {2}" -f $_.Name, $_.LastWriteTime.ToString('MM-dd HH:mm'), $size)
        }
        $state = Join-Path $u 'state.json'
        if (Test-Path -LiteralPath $state) {
            $given = @()
            try { $given = @((Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).GaveUp) } catch { }
            if ($given.Count -gt 0) {
                Bad ("Gave up for good on: " + ($given -join ', '))
                $findings.Add("This machine refused or rolled back version(s) " + ($given -join ', ') + " and will never take them again. Why is in result.json / apply.log below, and on /owner/updates.")
            }
        }
        foreach ($f in 'result.json', 'applied.json') {
            $p = Join-Path $u $f
            if (Test-Path -LiteralPath $p) { Note "--- $f"; (Get-Content -LiteralPath $p -Raw) -split "`n" | Select-Object -First 30 | ForEach-Object { Note "    $_" } }
        }
        $log = Join-Path $u 'apply.log'
        if (Test-Path -LiteralPath $log) { Note "--- apply.log (last 40 lines)"; Get-Content -LiteralPath $log -Tail 40 | ForEach-Object { Note "    $_" } }
        else { Note "No apply.log: no update has reached the install step here." }
    }
}

# ------------------------------------------------------------------------------ the event log
Head "The agent's warnings and errors (Application log, 14 days)"
try {
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'RemoteSupportAgent'; StartTime = (Get-Date).AddDays(-14) } -MaxEvents 30 -ErrorAction Stop
    foreach ($e in $ev) { Note ("{0}  {1}  {2}" -f $e.TimeCreated.ToString('MM-dd HH:mm'), $e.LevelDisplayName, (($e.Message -split "`n")[0..2] -join ' ').Trim()) }
} catch { Note "None." }

# ------------------------------------------------------------------------------ verdict
Head "What this says"
if ($findings.Count -eq 0) { Good "Nothing here explains it. The server's history for this machine (/owner/updates) is the next place to look." }
else { $i = 1; foreach ($f in $findings) { Bad ("{0}. {1}" -f $i, $f); $i++ } }

# Run from the Scripts tool (SYSTEM) the output is already in the console; there is no Desktop of anybody's to save to.
if ($isSystem) { return }
$out = Join-Path ([Environment]::GetFolderPath('Desktop')) ("updatestate-{0}-{1}.txt" -f $env:COMPUTERNAME, (Get-Date -Format 'yyyyMMdd-HHmm'))
try { $lines | Set-Content -LiteralPath $out -Encoding UTF8; Write-Host ""; Write-Host "Saved: $out" -ForegroundColor Green } catch { }
