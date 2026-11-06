# While the PERSON at the machine holds a file copy, what does each read strategy actually
# return to a SYSTEM process? Run through tools/runscript so this is a child of the host.
#
#   runscript DESKTOP-OPHQIDH oleprobe.ps1 --timeout 240
#
# Prints the PATHS, not just counts: a count of 1 for a two-file copy is how a stale answer
# disguises itself as a working one.
#
# All the threading happens inside compiled C#. A PowerShell scriptblock cannot run on a raw
# .NET thread — it has no runspace there — and trying killed this script outright the first time.
$ErrorActionPreference = 'SilentlyContinue'

Add-Type -ReferencedAssemblies 'System.Windows.Forms','System.Drawing' @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

public static class OleRead
{
  [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint f);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr h);
  [DllImport("user32.dll")] public static extern bool CloseClipboard();
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetClipboardData(uint f);
  [DllImport("user32.dll")] public static extern uint EnumClipboardFormats(uint f);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClipboardFormatNameW(uint f, StringBuilder s, int max);
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)] public static extern uint DragQueryFileW(IntPtr h, uint i, StringBuilder f, uint cch);
  [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);

  public static string Raw()
  {
    var sb = new StringBuilder();
    sb.AppendLine("  advertised: " + IsClipboardFormatAvailable(15));
    if (!OpenClipboard(IntPtr.Zero)) { sb.AppendLine("  OpenClipboard failed " + Marshal.GetLastWin32Error()); return sb.ToString(); }
    try
    {
      var names = new List<string>();
      uint f = 0;
      while ((f = EnumClipboardFormats(f)) != 0)
      {
        var nb = new StringBuilder(256);
        int n = GetClipboardFormatNameW(f, nb, 256);
        names.Add(n > 0 ? f + "=" + nb : f.ToString());
      }
      sb.AppendLine("  formats: " + string.Join(", ", names));
      var h = GetClipboardData(15);
      if (h == IntPtr.Zero) { sb.AppendLine("  CF_HDROP: NULL (win32 " + Marshal.GetLastWin32Error() + ")"); return sb.ToString(); }
      var p = GlobalLock(h);
      uint c = DragQueryFileW(p, 0xFFFFFFFF, null, 0);
      sb.AppendLine("  CF_HDROP: " + c + " path(s)");
      for (uint i = 0; i < c; i++) { var pb = new StringBuilder(1024); DragQueryFileW(p, i, pb, 1024); sb.AppendLine("     " + pb); }
      GlobalUnlock(h);
      return sb.ToString();
    }
    finally { CloseClipboard(); }
  }

  /// <summary>WinForms GetFileDropList on a real STA thread — exactly what the host's fallback does.</summary>
  public static string DropList(int timeoutMs)
  {
    string outp = null;
    var t = new Thread(() =>
    {
      try
      {
        var col = Clipboard.GetFileDropList();
        var sb = new StringBuilder();
        sb.AppendLine("  got " + col.Count + " path(s)");
        foreach (var s in col) sb.AppendLine("     " + s);
        outp = sb.ToString();
      }
      catch (Exception ex) { outp = "  threw " + ex.GetType().Name + ": " + ex.Message; }
    });
    t.IsBackground = true;
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    if (!t.Join(timeoutMs)) return "  DID NOT ANSWER in " + timeoutMs + " ms";
    return outp ?? "  (nothing)";
  }

  /// <summary>What formats the OLE data object says it can render.</summary>
  public static string Formats(int timeoutMs)
  {
    string outp = null;
    var t = new Thread(() =>
    {
      try
      {
        var d = Clipboard.GetDataObject();
        if (d == null) { outp = "  GetDataObject returned null"; return; }
        outp = "  formats: " + string.Join(", ", d.GetFormats());
      }
      catch (Exception ex) { outp = "  threw " + ex.GetType().Name + ": " + ex.Message; }
    });
    t.IsBackground = true;
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    if (!t.Join(timeoutMs)) return "  DID NOT ANSWER in " + timeoutMs + " ms";
    return outp ?? "  (nothing)";
  }
}
"@

$exp = Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | Select-Object -First 1
if (-not $exp) { 'nobody is logged on'; exit 1 }
$o = Invoke-CimMethod -InputObject $exp -MethodName GetOwner
$user = "$($o.Domain)\$($o.User)"
"this probe runs as : $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
"the person is      : $user"

$tag = [guid]::NewGuid().ToString('N').Substring(0,8)
$taskName = "RS-OleProbe-$tag"
$script = "C:\Users\Public\$taskName.ps1"
$dir = "C:\Users\Public\oleprobe-$tag"
New-Item -ItemType Directory $dir -Force | Out-Null
'aaa' | Set-Content (Join-Path $dir 'alpha.txt')
'bbb' | Set-Content (Join-Path $dir 'beta.txt')

$userSrc = @"
`$ErrorActionPreference = 'SilentlyContinue'
Set-Clipboard -Path @('$dir\alpha.txt', '$dir\beta.txt')
Start-Sleep -Seconds 45
"@
[IO.File]::WriteAllText($script, $userSrc, (New-Object Text.UTF8Encoding $true))
icacls $script /grant "*S-1-5-32-545:(R)" | Out-Null
$act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$pri = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 3)
Register-ScheduledTask -TaskName $taskName -Action $act -Principal $pri -Settings $set -Force -ErrorAction Stop | Out-Null
Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
"started the user's copy of alpha.txt + beta.txt (2 files)"
Start-Sleep -Seconds 6

""
"--- A. raw Win32 (what ClipboardAccess.GetFilesRaw does) ---"
[OleRead]::Raw()
""
"--- B. WinForms GetFileDropList on an STA thread (what the host's OLE fallback does) ---"
[OleRead]::DropList(8000)
""
"--- C. the OLE data object's own format list ---"
[OleRead]::Formats(8000)
""
"--- D. Get-Clipboard cmdlet, same SYSTEM process ---"
$g = @(Get-Clipboard -Format FileDropList)
"  got $($g.Count) path(s)"
foreach ($x in $g) { "     $($x.FullName)" }

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item $script -Force -ErrorAction SilentlyContinue
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
