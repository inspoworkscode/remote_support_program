# What did the --put-files helper do? Trace lines, leftover list/result files, and any
# RemoteSupport.Host or powershell process still alive, with its owner. Read-only.
$ErrorActionPreference='SilentlyContinue'
'=== clipboard lines in the host trace (last 30) ==='
Get-Content 'C:\ProgramData\RemoteSupport\host-trace.log' -Tail 300 | Select-String 'clipboard|host started|put-files|helper' | Select-Object -Last 30 | ForEach-Object { '  ' + $_.Line }
''
'=== processes: host copies and powershells, with owners ==='
Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'RemoteSupport|powershell' } | ForEach-Object {
    $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
    '  pid {0,-6} {1,-24} as {2}\{3}  started {4}  cmd: {5}' -f $_.ProcessId, $_.Name, $o.Domain, $o.User, $_.CreationDate.ToString('HH:mm:ss'), ($_.CommandLine -replace '^.*RemoteSupport\.Host\.exe"?\s*','')
}
''
'=== leftover helper files in the paste root ==='
Get-ChildItem 'C:\Users\Lenovo\AppData\Local\Temp\RemoteSupport\paste' -File | ForEach-Object { '  ' + $_.Name + '  ' + $_.Length + ' bytes'; if ($_.Name -like '*.result') { '     ' + (Get-Content $_.FullName -Raw) } }
''
'=== the clipboard as SYSTEM sees it now ==='
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Q {
  [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
  [DllImport("user32.dll")] public static extern IntPtr GetOpenClipboardWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@
$own=[Q]::GetClipboardOwner(); $op=0; [void][Q]::GetWindowThreadProcessId($own,[ref]$op)
$opn=[Q]::GetOpenClipboardWindow(); $opp=0; [void][Q]::GetWindowThreadProcessId($opn,[ref]$opp)
'  sequence {0}  CF_HDROP {1}  owner hwnd 0x{2:x} pid {3}  HELD OPEN by hwnd 0x{4:x} pid {5}' -f [Q]::GetClipboardSequenceNumber(), [Q]::IsClipboardFormatAvailable(15), $own.ToInt64(), $op, $opn.ToInt64(), $opp
