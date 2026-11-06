using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>Win32 interop for screen capture, input injection and cursor.</summary>
internal static class Native
{
    // ---- DPI awareness ----
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);

    // ---- GDI capture ----
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int xd, int yd, int w, int h, IntPtr src, int xs, int ys, int rop);
    public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    // ---- monitors ----
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    public const uint MONITORINFOF_PRIMARY = 1;

    // ---- cursor ----
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    // ---- input injection ----
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000, MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;

    public static void SetDpiAware()
    {
        try { SetProcessDpiAwareness(2); } catch { try { SetProcessDPIAware(); } catch { } }
    }

    // ---- desktops -------------------------------------------------------------------------
    // A thread can only capture from, and inject into, the desktop it is attached to. Windows
    // moves the user between desktops: "Default" normally, "Winlogon" for UAC prompts, the lock
    // screen and Ctrl+Alt+Del, "Screen-saver" for a secure screensaver. A thread pinned to
    // Default therefore sees a frozen picture and its input goes nowhere the moment a UAC prompt
    // appears. Only SYSTEM may open the Winlogon desktop — an elevated user process may not.
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetThreadDesktop(int threadId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformation(IntPtr hObj, int index, byte[]? info, int length, out int lengthNeeded);
    [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();

    public const uint DF_ALLOWOTHERACCOUNTHOOK = 0x0001;
    public const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_CREATEWINDOW = 0x0002, DESKTOP_CREATEMENU = 0x0004,
                      DESKTOP_HOOKCONTROL = 0x0008, DESKTOP_JOURNALRECORD = 0x0010, DESKTOP_JOURNALPLAYBACK = 0x0020,
                      DESKTOP_ENUMERATE = 0x0040, DESKTOP_WRITEOBJECTS = 0x0080, DESKTOP_SWITCHDESKTOP = 0x0100;
    public const uint DESKTOP_ALL = DESKTOP_READOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU |
                                    DESKTOP_HOOKCONTROL | DESKTOP_JOURNALRECORD | DESKTOP_JOURNALPLAYBACK |
                                    DESKTOP_ENUMERATE | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP;
    private const int UOI_NAME = 2;

    /// <summary>Name of a desktop handle ("Default", "Winlogon", "Screen-saver"), or "" if unknown.</summary>
    public static string DesktopName(IntPtr hDesktop)
    {
        if (hDesktop == IntPtr.Zero) return "";
        try
        {
            GetUserObjectInformation(hDesktop, UOI_NAME, null, 0, out int need);
            if (need <= 0) need = 256;
            var buf = new byte[need];
            if (!GetUserObjectInformation(hDesktop, UOI_NAME, buf, buf.Length, out _)) return "";
            return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
        }
        catch { return ""; }
    }
}
