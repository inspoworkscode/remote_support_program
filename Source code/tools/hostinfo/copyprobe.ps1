# Can the SYSTEM host READ a file list that the PERSON at the machine copied?
#
# Run through tools/runscript, so this script is a child of the host and its token IS the host's
# token — SYSTEM, on WinSta0. That is the whole point: it asks the question from exactly where
# HostEngine.SendClipboardToTechnician asks it.
#
#   runscript DESKTOP-OPHQIDH copyprobe.ps1 --timeout 180
#
# It starts a one-shot task AS THE LOGGED-ON USER that copies two files and then holds them on
# the clipboard, and while they are held this SYSTEM process reports, in order:
#
#   IsClipboardFormatAvailable(CF_HDROP)   - is a file list even advertised to us
#   GetClipboardData(CF_HDROP)             - can we get the handle, and win32 if not
#   DragQueryFile count                    - can we read paths out of it
#   the clipboard owner                    - which process, and as whom
#   the same questions for CF_UNICODETEXT  - the format that is known to work, as a control
#
# Read-only apart from the user's own copy, which it cleans up after.
$ErrorActionPreference = 'SilentlyContinue'

Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class K {
  [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint f);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr h);
  [DllImport("user32.dll")] public static extern bool CloseClipboard();
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetClipboardData(uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int CountClipboardFormats();
  [DllImport("user32.dll")] public static extern uint EnumClipboardFormats(uint f);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClipboardFormatNameW(uint f, StringBuilder s, int max);
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)] public static extern uint DragQueryFileW(IntPtr h, uint i, StringBuilder f, uint cch);
  [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
  [DllImport("kernel32.dll")] public static extern UIntPtr GlobalSize(IntPtr h);
}
"@

function Inspect($label) {
    Write-Output ""
    Write-Output "=== $label ==="
    Write-Output ("  sequence            : {0}" -f [K]::GetClipboardSequenceNumber())
    $own = [K]::GetClipboardOwner(); $opid = 0; [void][K]::GetWindowThreadProcessId($own, [ref]$opid)
    $op = Get-CimInstance Win32_Process -Filter "ProcessId = $opid"
    $asWho = '?'
    if ($op) { $o = Invoke-CimMethod -InputObject $op -MethodName GetOwner; $asWho = "$($o.Domain)\$($o.User)" }
    Write-Output ("  clipboard owner     : hwnd 0x{0:x}  pid {1} ({2})  as {3}" -f $own.ToInt64(), $opid, $(if ($op) { $op.Name } else { 'gone' }), $asWho)
    Write-Output ("  CF_HDROP advertised : {0}" -f [K]::IsClipboardFormatAvailable(15))
    Write-Output ("  CF_UNICODETEXT      : {0}" -f [K]::IsClipboardFormatAvailable(13))

    if ([K]::OpenClipboard([IntPtr]::Zero)) {
        try {
            Write-Output ("  formats on it       : {0}" -f [K]::CountClipboardFormats())
            $f = 0
            $names = @()
            while ($true) {
                $f = [K]::EnumClipboardFormats($f)
                if ($f -eq 0) { break }
                $sb = New-Object System.Text.StringBuilder 256
                $n = [K]::GetClipboardFormatNameW($f, $sb, 256)
                $names += $(if ($n -gt 0) { "$f=$($sb.ToString())" } else { "$f" })
            }
            Write-Output ("  they are           : {0}" -f ($names -join ', '))

            $h = [K]::GetClipboardData(15)
            $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            if ($h -ne [IntPtr]::Zero) {
                $sz = [K]::GlobalSize($h)
                $p = [K]::GlobalLock($h)
                $n = [K]::DragQueryFileW($p, 0xFFFFFFFF, $null, 0)
                Write-Output ("  GetClipboardData(15): OK, {0} bytes, {1} path(s)" -f $sz, $n)
                for ($i = 0; $i -lt $n; $i++) {
                    $sb = New-Object System.Text.StringBuilder 1024
                    [void][K]::DragQueryFileW($p, $i, $sb, 1024)
                    Write-Output ("     path             : " + $sb.ToString())
                }
                [void][K]::GlobalUnlock($h)
            } else {
                Write-Output ("  GetClipboardData(15): NULL  <-- the host cannot read the file list.  win32 {0}" -f $err)
            }
        } finally { [void][K]::CloseClipboard() }
    } else {
        Write-Output ("  OpenClipboard FAILED: win32 {0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }

    # The managed route the host does NOT use, for comparison.
    Add-Type -AssemblyName System.Windows.Forms
    $ole = @(Get-Clipboard -Format FileDropList)
    Write-Output ("  Get-Clipboard (OLE) : {0} item(s)" -f $ole.Count)
}

Write-Output ("this probe runs as : {0}  session {1}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name, (Get-Process -Id $PID).SessionId)

$exp = Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | Select-Object -First 1
if (-not $exp) { Write-Output 'nobody is logged on'; exit 1 }
$o = Invoke-CimMethod -InputObject $exp -MethodName GetOwner
$user = "$($o.Domain)\$($o.User)"
Write-Output ("the person at the machine: {0}" -f $user)

$tag = [guid]::NewGuid().ToString('N').Substring(0,8)
$taskName = "RS-CopyProbe-$tag"
$script = "C:\Users\Public\$taskName.ps1"
$dir = "C:\Users\Public\copyprobe-$tag"
New-Item -ItemType Directory $dir -Force | Out-Null
'one' | Set-Content (Join-Path $dir 'one.txt')
'two' | Set-Content (Join-Path $dir 'two.txt')

# The user copies and then HOLDS, so this SYSTEM process can look while the copy is live. Two
# ways of copying, because Set-Clipboard and the shell's own Copy verb are not the same thing.
$userSrc = @"
`$ErrorActionPreference = 'SilentlyContinue'
Set-Clipboard -Path @('$dir\one.txt', '$dir\two.txt')
Start-Sleep -Seconds 18
`$sh = New-Object -ComObject Shell.Application
`$sh.NameSpace('$dir').ParseName('one.txt').InvokeVerb('Copy')
Start-Sleep -Seconds 18
"@
[IO.File]::WriteAllText($script, $userSrc, (New-Object Text.UTF8Encoding $true))
icacls $script /grant "*S-1-5-32-545:(R)" | Out-Null

try {
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
    $pri = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 3)
    Register-ScheduledTask -TaskName $taskName -Action $act -Principal $pri -Settings $set -Force -ErrorAction Stop | Out-Null
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    Write-Output "started the user's copies"
} catch { Write-Output ("could not start the user task: " + $_.Exception.Message); exit 1 }

Start-Sleep -Seconds 6
Inspect "AFTER THE USER RAN Set-Clipboard -Path  (files, as the user)"
Start-Sleep -Seconds 18
Inspect "AFTER THE USER RAN THE SHELL Copy VERB  (files, exactly Ctrl+C)"

# Control: the same question for a copy this SYSTEM process makes itself.
Set-Clipboard -Path @("$dir\one.txt")
Start-Sleep -Seconds 1
Inspect "AFTER SYSTEM COPIED THE SAME FILE ITSELF  (the control)"

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item $script -Force -ErrorAction SilentlyContinue
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
