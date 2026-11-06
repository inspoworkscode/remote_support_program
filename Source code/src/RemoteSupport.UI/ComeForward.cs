using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteSupport.UI;

/// <summary>
/// Puts a window that must be ANSWERED in front of everything and gives it the keyboard, from a program
/// the person is not using (2026-10-16). Clint: the Allow prompt opened behind his other windows.
///
/// <c>TopMost = true</c> was not enough, and <c>tools/consentfront</c> measured why: when the person has
/// just clicked into another program, Windows takes the always-on-top mark OFF a window that a background
/// program shows — it was gone the first time the window could be read, and the window sat behind the one
/// they had clicked. Only the program in the foreground may put things in front of it. So this joins the
/// foreground window's input for a moment (which is what lets <c>SetForegroundWindow</c> through), takes
/// the foreground, and then marks the window always-on-top again. If Windows still refuses, the taskbar
/// button flashes until it is looked at.
///
/// Call it from the window's own thread, after it is shown. Calling it again is harmless.
/// </summary>
public static class ComeForward
{
    public static void Now(Form form)
    {
        if (form is null || form.IsDisposed || !form.IsHandleCreated) return;
        var h = form.Handle;
        try
        {
            var fg = GetForegroundWindow();
            if (fg != h)
            {
                int me = GetCurrentThreadId();
                int theirs = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
                bool joined = theirs != 0 && theirs != me && AttachThreadInput(me, theirs, true);
                try
                {
                    ShowWindow(h, SW_SHOW);
                    BringWindowToTop(h);
                    SetForegroundWindow(h);
                }
                finally { if (joined) AttachThreadInput(me, theirs, false); }
            }
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            if (GetForegroundWindow() != h)
            {
                var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = h, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG };
                FlashWindowEx(ref fi);
            }
        }
        catch { }
    }

    /// <summary>Back on top of an always-on-top window another program opened after this one (a reminder, a
    /// call), without taking the keyboard from what they are typing in. For a timer while the question waits.</summary>
    public static void StayOnTop(Form form)
    {
        if (form is null || form.IsDisposed || !form.IsHandleCreated || !form.Visible) return;
        try { SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x10;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;
    private const int SW_SHOW = 5;
    private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(int a, int b, bool attach);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO fi);
}
