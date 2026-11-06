# Where is the host, and is there a user to paste for?
#
# Run through tools/runscript so it is a child of the host and inherits the host's window station:
#   runscript DESKTOP-OPHQIDH stationprobe.ps1
#
# It answers, from the host's own point of view:
#   - the name of the window station and desktop this process (and so the host) is on
#   - what GetClipboardSequenceNumber returns here — zero means "no clipboard on this station"
#   - which sessions exist and who is logged on, and whether the console session is locked
#   - which session explorer.exe (the thing that has to do the pasting) is running in, and as whom
# Read-only.

$ErrorActionPreference = 'SilentlyContinue'
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Sta {
  [DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
  [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint tid);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformationW(IntPtr h, int idx, StringBuilder s, int len, out int need);
  [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint f);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
  [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
  public static string Name(IntPtr h) { var sb = new StringBuilder(256); int n; GetUserObjectInformationW(h, 2, sb, 256, out n); return sb.ToString(); }
}
"@
$ws = [Sta]::GetProcessWindowStation()
$dt = [Sta]::GetThreadDesktop([Sta]::GetCurrentThreadId())
$inp = [Sta]::OpenInputDesktop(0, $false, 0x0001)
Write-Output ("running as         : {0}  pid {1}  session {2}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name, $PID, (Get-Process -Id $PID).SessionId)
Write-Output ("window station     : '{0}'" -f [Sta]::Name($ws))
Write-Output ("thread desktop     : '{0}'" -f [Sta]::Name($dt))
Write-Output ("input desktop      : '{0}'" -f $(if ($inp -ne [IntPtr]::Zero) { [Sta]::Name($inp) } else { '(could not open)' }))
Write-Output ("clipboard sequence : {0}   (0 = this station has no clipboard we can see)" -f [Sta]::GetClipboardSequenceNumber())
Write-Output ("CF_HDROP available : {0}   CF_UNICODETEXT: {1}" -f [Sta]::IsClipboardFormatAvailable(15), [Sta]::IsClipboardFormatAvailable(13))
Write-Output ("active console sess: {0}" -f [Sta]::WTSGetActiveConsoleSessionId())
Write-Output ''
Write-Output '=== sessions (query session) ==='
query session 2>&1 | ForEach-Object { '  ' + $_ }
Write-Output ''
Write-Output '=== explorer.exe, the process that has to paste ==='
Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | ForEach-Object {
    $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
    Write-Output ("  pid {0}  as {1}\{2}  session {3}" -f $_.ProcessId, $o.Domain, $o.User, $_.SessionId)
}
Write-Output ''
Write-Output '=== LogonUI.exe present means the lock/sign-in screen is up ==='
$l = Get-Process LogonUI -ErrorAction SilentlyContinue
Write-Output ("  LogonUI: {0}" -f $(if ($l) { 'RUNNING (locked or nobody signed in)' } else { 'not running (a desktop is unlocked)' }))
Write-Output ''
Write-Output '=== the host process ==='
Get-CimInstance Win32_Process -Filter "Name = 'RemoteSupport.Host.exe'" | ForEach-Object {
    $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
    Write-Output ("  pid {0}  as {1}\{2}  session {3}  started {4}" -f $_.ProcessId, $o.Domain, $o.User, $_.SessionId, $_.CreationDate)
}
