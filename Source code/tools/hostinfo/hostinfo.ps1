# Read-only diagnostic, run on the remote machine through a live session (docs/HANDOVER.md 6d).
#
# It runs as a child of RemoteSupport.Host.exe, so its OWN token is the host's token — which is
# exactly what decides whether SendInput is allowed to drive the desktop. Test locally before
# sending it anywhere: powershell -NoProfile -ExecutionPolicy Bypass -File hostinfo.ps1
$ErrorActionPreference = 'SilentlyContinue'

Add-Type -Namespace RS -Name Tok -MemberDefinition @'
[DllImport("advapi32.dll", SetLastError=true)]
public static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
[DllImport("advapi32.dll", SetLastError=true)]
public static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr buf, int len, out int ret);
[DllImport("kernel32.dll", SetLastError=true)]
public static extern bool CloseHandle(IntPtr h);
[DllImport("advapi32.dll", SetLastError=true)]
public static extern bool ConvertSidToStringSid(IntPtr sid, out System.IntPtr str);
'@

function Get-IntegrityLevel($proc) {
    # Getting the handle at all fails for a process we are not allowed to open. For a process
    # owned by the SAME user that means it sits at a higher integrity level than we do — i.e. it
    # is elevated — and UIPI will silently discard our SendInput against its windows.
    $h = $null
    try { $h = $proc.Handle } catch { return 'DENIED (elevated - higher integrity than us)' }
    if (-not $h) { return 'DENIED (no handle)' }
    try {
        $tok = [IntPtr]::Zero
        if (-not [RS.Tok]::OpenProcessToken($h, 0x0008, [ref]$tok)) {
            $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            # Access denied on a process owned by the SAME user means that process sits at a
            # higher integrity level than we do — i.e. it is elevated, and UIPI will block our
            # SendInput from reaching its windows.
            if ($err -eq 5) { return 'DENIED (elevated - higher integrity than us)' }
            return "n/a (win32 error $err)"
        }
        $len = 0
        [void][RS.Tok]::GetTokenInformation($tok, 25, [IntPtr]::Zero, 0, [ref]$len)   # TokenIntegrityLevel
        $buf = [Runtime.InteropServices.Marshal]::AllocHGlobal($len)
        try {
            if (-not [RS.Tok]::GetTokenInformation($tok, 25, $buf, $len, [ref]$len)) { return 'n/a' }
            $sidPtr = [Runtime.InteropServices.Marshal]::ReadIntPtr($buf)
            $sid = New-Object Security.Principal.SecurityIdentifier($sidPtr)
            switch -Wildcard ($sid.Value) {
                'S-1-16-4096'  { 'Low' }
                'S-1-16-8192'  { 'Medium' }
                'S-1-16-8448'  { 'Medium Plus' }
                'S-1-16-12288' { 'High' }
                'S-1-16-16384' { 'System' }
                default        { $sid.Value }
            }
        } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($buf); [void][RS.Tok]::CloseHandle($tok) }
    } catch { 'n/a' }
}

"=== this script's own token (this IS the host's token) ==="
"whoami          : " + (whoami)
"session id      : " + (Get-Process -Id $PID).SessionId
$me = Get-Process -Id $PID
"integrity level : " + (Get-IntegrityLevel $me)
""

"=== device identity (agent and client must share this, or they are two devices) ==="
foreach ($p in "$env:ProgramData\RemoteSupport\device.json", "$env:LOCALAPPDATA\RemoteSupport\device.json") {
    if (Test-Path $p) { "$p`n    " + ((Get-Content $p -Raw) -replace '\s', '') }
    else { "$p`n    (missing)" }
}
""

"=== RemoteSupport processes ==="
Get-CimInstance Win32_Process -Filter "Name LIKE 'RemoteSupport%'" | ForEach-Object {
    $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
    $ps = Get-Process -Id $_.ProcessId
    "{0,-26} pid={1,-6} ppid={2,-6} session={3} user={4}\{5} integrity={6}" -f `
        $_.Name, $_.ProcessId, $_.ParentProcessId, $_.SessionId, $o.Domain, $o.User, (Get-IntegrityLevel $ps)
    "     " + $_.CommandLine
}
""

"=== parents ==="
Get-CimInstance Win32_Process -Filter "Name LIKE 'RemoteSupport%'" | ForEach-Object {
    $parent = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $_.ParentProcessId)
    "{0} (pid {1}) was started by {2}" -f $_.Name, $_.ProcessId, $(if ($parent) { $parent.Name + " pid " + $parent.ProcessId } else { "(gone)" })
}
""

"=== UAC ==="
"EnableLUA = " + (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name EnableLUA).EnableLUA
""
"=== the foreground window (this is what decides whether SendInput is allowed) ==="
Add-Type -Namespace RS -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
[DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
'@
$fg = [RS.Win]::GetForegroundWindow()
if ($fg -ne [IntPtr]::Zero) {
    $fgPid = 0
    [void][RS.Win]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    $sb = New-Object System.Text.StringBuilder 512
    [void][RS.Win]::GetWindowTextW($fg, $sb, $sb.Capacity)
    $fp = Get-Process -Id $fgPid -ErrorAction SilentlyContinue
    if ($fp) {
        "foreground : {0} (pid {1}) integrity={2}" -f $fp.ProcessName, $fgPid, (Get-IntegrityLevel $fp)
        "title      : " + $sb.ToString()
        "NOTE: if that integrity is higher than this script's own, UIPI silently discards our input."
    } else { "foreground pid $fgPid (process gone)" }
} else { "no foreground window" }
