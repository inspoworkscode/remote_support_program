using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// The privacy screen (roadmap 1.7): the customer's monitors go black, with one line saying who is
/// working, while the technician goes on seeing the real desktop.
///
/// <para><b>How.</b> One black window per monitor, topmost, click-through, and marked
/// <c>WDA_EXCLUDEFROMCAPTURE</c> (Windows 10 2004 and later): it is on the customer's screen and
/// NOT in any capture of it, so Desktop Duplication and the GDI fallback both see through it.
/// Nothing is installed and nothing about the machine's display is changed; if the host goes away,
/// its windows go with it, so a machine can never be left black.</para>
///
/// <para><b>The failure it must not have</b> is the roadmap's: a technician believing the screen is
/// private when it is not. So it refuses rather than half-works — if Windows will not exclude a
/// window from capture, the technician would see black too, and <see cref="Show"/> says so and
/// shows nothing. <c>tools/privacyprobe</c> proves the exclusion against both capture paths, with
/// the control case (the same window NOT excluded) captured black, so "not black" cannot simply
/// mean the window never appeared.</para>
///
/// <para>The windows live on the normal desktop. A UAC prompt or Ctrl+Alt+Del is a different
/// desktop and is shown to the customer as usual — they must be able to see what they are being
/// asked to approve.</para>
///
/// <para>Raw Win32 on a thread of its own, per-monitor DPI aware, so every monitor is measured and
/// covered in real pixels: on a scaled display a window sized in scaled units covers part of it.</para>
/// </summary>
public sealed class PrivacyScreen : IDisposable
{
    private const uint WDA_NONE = 0x0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    private Thread? _thread;
    private uint _threadId;
    private readonly List<IntPtr> _windows = new();
    private readonly object _lock = new();
    private string _message = "";
    private WndProc? _proc;   // kept alive: the class holds a pointer to it

    public bool On { get { lock (_lock) return _windows.Count > 0; } }

    /// <summary>The cover windows, for tools/privacyprobe.</summary>
    public IReadOnlyList<IntPtr> Windows { get { lock (_lock) return _windows.ToList(); } }

    /// <summary>The monitors covered, in real pixels, as the cover thread measured them.</summary>
    public IReadOnlyList<Rectangle> Covered { get; private set; } = Array.Empty<Rectangle>();

    /// <summary>
    /// Blacks out every monitor. Returns "" when it is on, or why it is not — in which case nothing
    /// is left on the screen. <paramref name="excludeFromCapture"/> is false ONLY for the probe's
    /// control case; the product never passes it.
    /// </summary>
    public string Show(string message, bool excludeFromCapture = true)
    {
        if (On) return "";
        _message = message;
        string error = "";
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            // Per-monitor aware on THIS thread only: the rest of the host keeps the coordinates it
            // has always used.
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { error = Build(excludeFromCapture); }
            catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; }
            if (error.Length > 0) { DestroyAll(); ready.Set(); return; }
            ready.Set();
            // Keep them on top: another topmost window created after ours (a notification, an
            // "always on top" app) would otherwise sit above the black and show through it.
            SetTimer(IntPtr.Zero, IntPtr.Zero, 500, IntPtr.Zero);
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_TIMER) KeepOnTop();
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            DestroyAll();
        }) { IsBackground = true, Name = "rs-privacy" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!ready.Wait(5000)) return "the privacy screen did not come up in time";
        if (error.Length > 0) HostTrace.Write("privacy: REFUSED — " + error);
        else HostTrace.Write($"privacy: on, {Covered.Count} monitor(s): {string.Join(", ", Covered.Select(r => $"{r.Width}x{r.Height}@{r.X},{r.Y}"))}");
        return error;
    }

    /// <summary>The screen comes back. Safe to call when it is not on.</summary>
    public void Hide()
    {
        var t = _thread;
        if (t == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(3000);
        _thread = null;
        HostTrace.Write("privacy: off");
    }

    public void Dispose() => Hide();

    private string Build(bool excludeFromCapture)
    {
        var monitors = new List<Rectangle>();
        MonitorEnum cb = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) => { monitors.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)); return true; };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        if (monitors.Count == 0) return "no monitors were found on their machine";
        Covered = monitors;

        _proc = Paint;
        var hInst = GetModuleHandle(null);
        var cls = "RsPrivacy" + Environment.ProcessId;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst, lpszClassName = cls, hbrBackground = GetStockObject(BLACK_BRUSH),
            hCursor = LoadCursor(IntPtr.Zero, 32512)
        };
        RegisterClassEx(ref wc);   // already registered by an earlier Show in this process: harmless

        foreach (var m in monitors)
        {
            var h = CreateWindowEx(WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                cls, "Privacy screen", WS_POPUP, m.X, m.Y, m.Width, m.Height, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (h == IntPtr.Zero) return $"a privacy window could not be created (error {Marshal.GetLastWin32Error()})";
            lock (_lock) _windows.Add(h);
            // Fully opaque: LAYERED is only there so TRANSPARENT makes it click-through, and the
            // technician's clicks reach what is underneath.
            SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
            if (!SetWindowDisplayAffinity(h, excludeFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE))
                return "this version of Windows cannot hide a window from screen capture (it needs Windows 10 version 2004 or later), " +
                       $"so the privacy screen would black out your view too (error {Marshal.GetLastWin32Error()})";
            ShowWindow(h, SW_SHOWNOACTIVATE);
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        return "";
    }

    private void KeepOnTop()
    {
        foreach (var h in Windows) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void DestroyAll()
    {
        List<IntPtr> ws;
        lock (_lock) { ws = _windows.ToList(); _windows.Clear(); }
        foreach (var h in ws) DestroyWindow(h);
    }

    private IntPtr Paint(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_PAINT)
        {
            var dc = BeginPaint(h, out var ps);
            GetClientRect(h, out var rc);
            FillRect(dc, ref rc, GetStockObject(BLACK_BRUSH));
            // Sized from the monitor rather than written as a point size: a 4K screen and a
            // laptop panel should both read it from across a desk.
            int px = Math.Max(18, (rc.Bottom - rc.Top) / 36);
            var font = CreateFont(-px, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 5 /*CLEARTYPE*/, 0, "Segoe UI");
            var old = SelectObject(dc, font);
            SetBkMode(dc, 1 /*TRANSPARENT*/);
            SetTextColor(dc, 0x00B4B4B4);
            var text = rc;
            DrawText(dc, _message, -1, ref text, DT_CENTER | DT_VCENTER | DT_WORDBREAK | DT_CALCRECT);
            int textH = text.Bottom - text.Top;
            var box = new RECT { Left = rc.Left + px * 2, Right = rc.Right - px * 2, Top = (rc.Bottom - rc.Top - textH) / 2, Bottom = (rc.Bottom - rc.Top + textH) / 2 + px };
            DrawText(dc, _message, -1, ref box, DT_CENTER | DT_WORDBREAK);
            SelectObject(dc, old);
            DeleteObject(font);
            EndPaint(h, ref ps);
            return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, w, l);
    }

    // ---- Win32 ----------------------------------------------------------------------------------
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint WM_PAINT = 0x000F, WM_TIMER = 0x0113, WM_QUIT = 0x0012, LWA_ALPHA = 0x2;
    private const int SW_SHOWNOACTIVATE = 4, BLACK_BRUSH = 4;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_WORDBREAK = 0x10, DT_CALCRECT = 0x400;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    private delegate bool MonitorEnum(IntPtr h, IntPtr hdc, ref RECT r, IntPtr d);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT { public IntPtr hdc; public int fErase; public RECT rc; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgb; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnum cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(IntPtr dc, string s, int n, ref RECT r, uint fmt);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint colour);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
