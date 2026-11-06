# Which host binary is actually running, and does GetCursorInfo work in the host's own context?
# This script is a child of Host.exe, so its token and desktop are the host's.
$ErrorActionPreference='SilentlyContinue'
$p = Get-Process RemoteSupport.Host | Sort-Object StartTime -Descending | Select-Object -First 1
"running host : pid $($p.Id) started $($p.StartTime.ToString('HH:mm:ss'))"
$exe = 'C:\Program Files\Anywhere IT\Remote Support\RemoteSupport.Host.exe'
"on disk      : $((Get-FileHash $exe -Algorithm SHA256).Hash)"
"expected 2.4 : 3BB06D023A2C0FFFA7763D820861DFB894B754DFB8657EA19546BBE2D08511F5"

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Cur {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
  [DllImport("user32.dll", SetLastError=true)] public static extern bool GetCursorInfo(ref CURSORINFO pci);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool GetCursorPos(out POINT p);
  public static string Probe() {
    var ci = new CURSORINFO(); ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
    bool okInfo = GetCursorInfo(ref ci);
    int errInfo = Marshal.GetLastWin32Error();
    POINT p; bool okPos = GetCursorPos(out p);
    int errPos = Marshal.GetLastWin32Error();
    return "GetCursorInfo=" + okInfo + " (err " + errInfo + ") cbSize=" + ci.cbSize +
           " flags=" + ci.flags + " hCursor=" + ci.hCursor.ToInt64() + " at " + ci.pt.X + "," + ci.pt.Y +
           "  |  GetCursorPos=" + okPos + " (err " + errPos + ") at " + p.X + "," + p.Y;
  }
}
'@
"cursor       : $([Cur]::Probe())"
"desktop      : $([System.Environment]::GetEnvironmentVariable('SESSIONNAME'))"
