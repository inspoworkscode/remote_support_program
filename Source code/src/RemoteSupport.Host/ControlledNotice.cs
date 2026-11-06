using System.Runtime.InteropServices;

namespace RemoteSupport.Host;

/// <summary>
/// "Being controlled remotely" (2026-10-06, the free version — Clint: "Lets add Being Controlled
/// Remotely"). A free computer has no tray app, so nothing else on its screen says somebody is connected;
/// this does, for the whole of a session, naming who.
///
/// <para><b>How.</b> One small bar at the top centre of every monitor, a sibling of
/// <see cref="PrivacyScreen"/>: raw Win32 on a thread of its own, per-monitor DPI aware, topmost,
/// click-through (so it never takes the person's or the technician's clicks), never activated, and
/// marked <c>WDA_EXCLUDEFROMCAPTURE</c> so it is NOT in the technician's picture — it must not cover
/// what they are working on. Unlike the privacy screen it does not refuse when Windows cannot exclude
/// it: the person being told matters more than a strip of the technician's view.</para>
///
/// <para>It lives on the normal desktop. A UAC prompt or the Ctrl+Alt+Del screen is another desktop and
/// shows without it. If the host goes, its windows go with it.</para>
/// </summary>
public sealed class ControlledNotice : IDisposable
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    // Amber, white words: not an error, but not something to miss.
    private const uint Back = 0x000953B4, Fore = 0x00FFFFFF;

    private Thread? _thread;
    private uint _threadId;
    private readonly List<IntPtr> _windows = new();
    private readonly object _lock = new();
    private string _message = "";
    private IntPtr _font;
    /// <summary>Each bar's font size, as measured for its own monitor. Touched on the notice thread only.</summary>
    private readonly Dictionary<IntPtr, int> _px = new();
    private WndProc? _proc;

    public bool On { get { lock (_lock) return _windows.Count > 0; } }
    public string Message { get { lock (_lock) return _windows.Count > 0 ? _message : ""; } }

    /// <summary>Each bar's place on its monitor, in real pixels — for a harness to check it.</summary>
    public IReadOnlyList<Rectangle> Bars { get; private set; } = Array.Empty<Rectangle>();
    /// <summary>Whether every bar is hidden from screen capture.</summary>
    public bool ExcludedFromCapture { get; private set; }
    public IReadOnlyList<IntPtr> Windows { get { lock (_lock) return _windows.ToList(); } }

    /// <summary>The words for these technicians — "Being controlled remotely by Fran Free".</summary>
    public static string Wording(IReadOnlyCollection<string> technicians) =>
        technicians.Count == 0 ? "Being controlled remotely" : "Being controlled remotely by " + string.Join(", ", technicians);

    /// <summary>Shows the bar with these words, or changes them. Empty hides it. <paramref name="excludeFromCapture"/>
    /// is false ONLY for tools/noticeprobe's control case, as with the privacy screen; the product never passes it.</summary>
    public void Set(string message, bool excludeFromCapture = true)
    {
        // One change at a time: the participants arrive on the receive loop and are handed off to a task each.
        lock (_setLock)
        {
            if (message.Length == 0) { Hide(); return; }
            if (On && Message == message && _exclude == excludeFromCapture) return;
            Hide();
            _exclude = excludeFromCapture;
            Show(message);
        }
    }
    private readonly object _setLock = new();
    private bool _exclude = true;

    private void Show(string message)
    {
        _message = message;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { Build(); }
            catch (Exception ex) { HostTrace.Write($"notice: could not be shown — {ex.GetType().Name}: {ex.Message}"); DestroyAll(); ready.Set(); return; }
            ready.Set();
            SetTimer(IntPtr.Zero, IntPtr.Zero, 500, IntPtr.Zero);
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_TIMER) KeepOnTop();
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            DestroyAll();
        }) { IsBackground = true, Name = "rs-notice" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(5000);
        HostTrace.Write($"notice: \"{message}\" on {Bars.Count} monitor(s){(ExcludedFromCapture ? "" : " — NOT hidden from capture")}");
    }

    public void Hide()
    {
        var t = _thread;
        if (t == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(3000);
        _thread = null;
    }

    public void Dispose() => Hide();

    private void Build()
    {
        var monitors = new List<Rectangle>();
        MonitorEnum cb = (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) => { monitors.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)); return true; };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);

        _proc = Paint;
        var hInst = GetModuleHandle(null);
        var cls = "RsNotice" + Environment.ProcessId;
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInst, lpszClassName = cls, hCursor = LoadCursor(IntPtr.Zero, 32512)
        };
        RegisterClassEx(ref wc);

        var bars = new List<Rectangle>();
        bool allExcluded = true;
        foreach (var m in monitors)
        {
            // Sized from the monitor, like the privacy screen's words: legible on a 4K panel and a laptop.
            int px = Math.Max(15, m.Height / 62);
            if (_font != IntPtr.Zero) DeleteObject(_font);
            _font = CreateFont(-px, 0, 0, 0, 600, 0, 0, 0, 0, 0, 0, 5 /*CLEARTYPE*/, 0, "Segoe UI");
            var screen = GetDC(IntPtr.Zero);
            var old = SelectObject(screen, _font);
            var size = new RECT();
            DrawText(screen, _message, -1, ref size, DT_CALCRECT | DT_SINGLELINE);
            SelectObject(screen, old);
            ReleaseDC(IntPtr.Zero, screen);

            int w = Math.Min(m.Width - px * 2, size.Right - size.Left + px * 3), h = size.Bottom - size.Top + px;
            var bar = new Rectangle(m.X + (m.Width - w) / 2, m.Y + px / 2, w, h);
            var hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                cls, "Being controlled remotely", WS_POPUP, bar.X, bar.Y, bar.Width, bar.Height, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
            lock (_lock) _windows.Add(hwnd);
            _px[hwnd] = px;
            SetLayeredWindowAttributes(hwnd, 0, 242, LWA_ALPHA);
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, bar.Width + 1, bar.Height + 1, h / 2, h / 2), true);
            if (!_exclude || !SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) allExcluded = false;
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            bars.Add(bar);
        }
        Bars = bars;
        ExcludedFromCapture = allExcluded && bars.Count > 0;
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
        _px.Clear();
        if (_font != IntPtr.Zero) { DeleteObject(_font); _font = IntPtr.Zero; }
    }

    private IntPtr Paint(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_PAINT)
        {
            var dc = BeginPaint(h, out var ps);
            GetClientRect(h, out var rc);
            var brush = CreateSolidBrush(Back);
            FillRect(dc, ref rc, brush);
            DeleteObject(brush);
            // The size this bar was measured with, so the words fill it exactly as they were measured to.
            int px = _px.TryGetValue(h, out var p) ? p : 15;
            var font = CreateFont(-px, 0, 0, 0, 600, 0, 0, 0, 0, 0, 0, 5, 0, "Segoe UI");
            var old = SelectObject(dc, font);
            SetBkMode(dc, 1 /*TRANSPARENT*/);
            SetTextColor(dc, Fore);
            DrawText(dc, _message, -1, ref rc, DT_CENTER | DT_VCENTER | DT_SINGLELINE);
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
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const uint DT_CENTER = 0x1, DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400;
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
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
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
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint colour);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint colour);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
