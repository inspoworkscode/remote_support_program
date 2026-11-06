# Read-only clipboard diagnostic, run on the remote machine through a live session the same way
# hostinfo.ps1 and audioprobe.ps1 are (docs/HANDOVER.md 7).
#
# WHY THIS EXISTS. The clipboard does not belong to a machine or to a user. It belongs to a
# WINDOW STATION. A process on the wrong one reads and writes a different, empty clipboard and
# is told nothing at all -- OpenClipboard succeeds, the read returns nothing, and it looks
# exactly like "the user had not copied anything". Services normally live on their own window
# station (Service-0x0-3e7$); the interactive one is WinSta0. The host runs as SYSTEM, so which
# of those it is on is the whole question.
#
# The proof here needs nobody at the keyboard. If the clipboard OWNER is a process running as
# the logged-on user in their session, then we are demonstrably looking at the same clipboard
# they are. If we are on a service window station the owner comes back as nothing.
#
# It never prints clipboard CONTENT. A customer's clipboard is very often a password -- that is
# half the reason Clint wants this feature. Length, a hash and a shape description are enough to
# tell one clipboard from another.
#
# Test locally before sending it anywhere:
#   powershell -NoProfile -ExecutionPolicy Bypass -File clipboardprobe.ps1
$ErrorActionPreference = 'Continue'

function Section($t) { Write-Output ""; Write-Output ("=== " + $t + " ===") }

Section "identity"
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
Write-Output ("user          : " + $me.Name)
Write-Output ("is system     : " + $me.IsSystem)
$p = Get-Process -Id $PID
Write-Output ("process       : " + $p.ProcessName + " pid=" + $p.Id + " session=" + $p.SessionId)
try {
    $parent = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $PID) -ErrorAction SilentlyContinue).ParentProcessId
    $pp = Get-Process -Id $parent -ErrorAction SilentlyContinue
    if ($pp) { Write-Output ("parent        : " + $pp.ProcessName + " pid=" + $pp.Id + " session=" + $pp.SessionId) }
} catch { }

# Add-Type in Windows PowerShell 5.1 compiles as C# 5: no interpolation, no "out var".
Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RSClip
{
    public static class Probe
    {
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetThreadDesktop(int threadId);
        [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool GetUserObjectInformationW(IntPtr h, int index, byte[] info, int len, out int needed);

        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint fmt);
        [DllImport("user32.dll", SetLastError = true)] static extern uint EnumClipboardFormats(uint fmt);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int GetClipboardFormatNameW(uint fmt, StringBuilder name, int max);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
        [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);

        const int UOI_NAME = 2;
        const uint CF_TEXT = 1, CF_UNICODETEXT = 13, CF_HDROP = 15, CF_BITMAP = 2, CF_DIB = 8;

        static string ObjectName(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(none -- handle is null)";
            byte[] buf = new byte[512];
            int needed;
            if (!GetUserObjectInformationW(h, UOI_NAME, buf, buf.Length, out needed))
                return "(unreadable, win32 error " + Marshal.GetLastWin32Error() + ")";
            return Encoding.Unicode.GetString(buf, 0, Math.Max(0, needed - 2));
        }

        public static string Station() { return ObjectName(GetProcessWindowStation()); }
        public static string Desktop() { return ObjectName(GetThreadDesktop(GetCurrentThreadId())); }

        public static string FormatName(uint fmt)
        {
            switch (fmt)
            {
                case CF_TEXT: return "CF_TEXT";
                case CF_UNICODETEXT: return "CF_UNICODETEXT";
                case CF_BITMAP: return "CF_BITMAP";
                case CF_DIB: return "CF_DIB";
                case CF_HDROP: return "CF_HDROP (file list -- this is what drag and drop carries)";
                default:
                    StringBuilder sb = new StringBuilder(256);
                    int n = GetClipboardFormatNameW(fmt, sb, sb.Capacity);
                    return n > 0 ? sb.ToString() : ("format " + fmt);
            }
        }

        // Who currently owns the clipboard? This is the measurement that matters: an owner
        // running as the logged-on user, in their session, proves we share their clipboard.
        public static string Owner()
        {
            IntPtr hwnd = GetClipboardOwner();
            if (hwnd == IntPtr.Zero)
                return "no owner window.\n    On a service window station the clipboard is a separate, empty one and this\n    is exactly what that looks like. Compare the window station name above.";
            int pid;
            GetWindowThreadProcessId(hwnd, out pid);
            string who = "pid " + pid;
            try
            {
                System.Diagnostics.Process pr = System.Diagnostics.Process.GetProcessById(pid);
                who = pr.ProcessName + " (pid " + pid + ", session " + pr.SessionId + ")";
            }
            catch { who = "pid " + pid + " (gone)"; }
            return "hwnd 0x" + hwnd.ToString("X") + "  owned by " + who;
        }

        public static string SequenceNumber() { return GetClipboardSequenceNumber().ToString(); }

        public static string Formats()
        {
            if (!OpenClipboard(IntPtr.Zero))
                return "  OpenClipboard FAILED, win32 error " + Marshal.GetLastWin32Error() +
                       "\n  (error 5 is access denied; a busy clipboard gives error 0 and is worth retrying)";
            try
            {
                StringBuilder sb = new StringBuilder();
                uint fmt = 0;
                int count = 0;
                while ((fmt = EnumClipboardFormats(fmt)) != 0)
                {
                    count++;
                    sb.AppendLine("    " + FormatName(fmt));
                }
                if (count == 0)
                    sb.AppendLine("    (nothing on the clipboard at all)");
                return sb.ToString().TrimEnd();
            }
            finally { CloseClipboard(); }
        }

        // Never the content. A customer's clipboard is very often a password. Length, a hash and
        // a shape are enough to tell one clipboard from another, which is all we need.
        public static string TextSummary()
        {
            if (!OpenClipboard(IntPtr.Zero))
                return "OpenClipboard FAILED, win32 error " + Marshal.GetLastWin32Error();
            try
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero)
                    return "no CF_UNICODETEXT (win32 " + Marshal.GetLastWin32Error() + ") -- no text on this clipboard";
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return "GlobalLock failed";
                try
                {
                    string s = Marshal.PtrToStringUni(p);
                    if (s == null) return "text pointer was null";
                    byte[] hash = SHA256.Create().ComputeHash(Encoding.Unicode.GetBytes(s));
                    string hex = BitConverter.ToString(hash).Replace("-", "").Substring(0, 16);
                    int lines = s.Split('\n').Length;
                    bool ascii = true;
                    foreach (char c in s) if (c < 32 && c != '\r' && c != '\n' && c != '\t') { ascii = false; break; }
                    return string.Format("{0} characters, {1} line(s), sha256 {2}..., {3}",
                        s.Length, lines, hex, ascii ? "printable" : "contains control characters");
                }
                finally { GlobalUnlock(h); }
            }
            finally { CloseClipboard(); }
        }
    }
}
'@

# ---------------------------------------------------------------------------------------------
# The window station is the answer. WinSta0 is the interactive one and has the clipboard the
# user sees; Service-0x0-3e7$ is the one services get, and its clipboard is a different, empty
# object that nothing else on the machine can see.
# ---------------------------------------------------------------------------------------------
Section "window station and desktop"
$station = [RSClip.Probe]::Station()
$desktop = [RSClip.Probe]::Desktop()
Write-Output ("process window station : " + $station)
Write-Output ("this thread's desktop  : " + $desktop)
Write-Output ""
if ($station -like 'WinSta0*') {
    Write-Output "  -> WinSta0 is the INTERACTIVE window station. This is the clipboard the user sees."
} else {
    Write-Output "  -> NOT WinSta0. This process has its own private clipboard and cannot see the"
    Write-Output "     user's at all. Reads will come back empty and writes will go nowhere, silently."
}

Section "who owns the clipboard right now"
# The decisive check, and it needs nobody at the keyboard: an owner process running in the
# logged-on user's session proves we are on the same clipboard they are.
Write-Output ("  " + [RSClip.Probe]::Owner())
Write-Output ("  sequence number: " + [RSClip.Probe]::SequenceNumber())

Section "formats currently on the clipboard"
Write-Output ([RSClip.Probe]::Formats())

Section "the text on it, described but not printed"
# Deliberately never the content: this is exactly the feature where the customer's clipboard is
# a password, and a diagnostic that leaks it into a log would be worse than no diagnostic.
Write-Output ("  " + [RSClip.Probe]::TextSummary())

Write-Output ""
Write-Output "=== end of clipboard probe ==="
