using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteSupport.UI;

/// <summary>
/// The parts of a window Windows draws for us, which no colour we set in WinForms ever reaches.
///
/// Three of them showed up the moment the apps were previewed on a dark ground, and all three make
/// a dark app look half-finished rather than dark:
///
///   1. the title bar, still white with black text above a navy window
///   2. scrollbars inside list views and text boxes, drawn light grey
///   3. the strip of column-header to the right of the last column in a ListView
///
/// The first two are fixed by opting the window into Windows' own dark mode — DWM for the title
/// bar, and the "DarkMode_Explorer" visual style for the control's scrollbars. Both are no-ops on
/// Windows 10 builds that predate them, which is why every call here is wrapped and failure is
/// silent: an older machine gets the light title bar and nothing else breaks.
///
/// The third has no such switch. The header is a separate native control (SysHeader32) inside the
/// ListView, and the area past the last column belongs to it, not to any item we owner-draw. It is
/// fixed by subclassing that header and painting the gap ourselves — <see cref="HeaderFiller"/>.
/// </summary>
public static class NativeDark
{
    // DwmSetWindowAttribute
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? id);

    /// <summary>
    /// Paint this window's title bar dark. Call after the handle exists — OnHandleCreated or the
    /// constructor of a form that has already been shown — and again is harmless.
    /// </summary>
    public static void TitleBar(IWin32Window window, bool dark)
    {
        if (window?.Handle is not { } h || h == IntPtr.Zero) return;
        var on = dark ? 1 : 0;
        try
        {
            // 20 is the documented attribute from Windows 10 2004 onward. 19 was the undocumented
            // one used by 1809-1909; trying it second costs nothing and covers older machines.
            if (DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref on, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Give a control's scrollbars (and a ListView's header) Windows' dark visual style, so they
    /// stop being light grey rectangles in a dark window.
    /// </summary>
    public static void Scrollbars(Control? c, bool dark)
    {
        if (c is null || !c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }

    /// <summary>
    /// Takes the faint vertical rule between a ListView's columns away, and keeps everything else.
    ///
    /// Those lines are NOT <see cref="ListView.GridLines"/>, which is off. They come from the
    /// Explorer visual style that <see cref="Scrollbars"/> applies for the dark scrollbars —
    /// Explorer's own details view draws them, so ours did too. That cost an afternoon to find,
    /// because every obvious place to look says the grid is switched off.
    ///
    /// Owner-drawn rows paint over them, so they only ever showed where nothing else was drawn:
    /// the empty space under the last row, and the band a group header sits in. That is enough to
    /// see, and Clint did — they read as something you can take hold of, so the eye goes to them
    /// when you are reaching for a column edge or a customer's fold, and they are neither.
    ///
    /// Call it after the handle exists and after <see cref="Apply"/>, which is what puts the theme
    /// on in the first place.
    /// </summary>
    public static void NoColumnRules(ListView? lv)
    {
        if (lv is null || !lv.IsHandleCreated) return;
        // An empty theme name means "no visual style at all" for this window. The scrollbar goes
        // back to the classic one, which is the price; on a light console nobody can tell, and a
        // quiet list is worth more than a rounded scrollbar.
        try { SetWindowTheme(lv.Handle, "", ""); } catch { }
    }

    /// <summary>
    /// Everything above, for a form and every control under it, in one call. Safe to call on a
    /// light theme too: it puts each piece back to the ordinary Explorer style.
    /// </summary>
    public static void Apply(Form form, Theme t)
    {
        if (form is null) return;
        TitleBar(form, t.IsDark);
        Walk(form);

        void Walk(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                // Only controls whose SCROLLBARS we need. Deliberately not TextBox or ComboBox:
                // giving those the Explorer visual style makes them paint their own background
                // and ignore the BackColor we set, which turns a dark field back to white.
                if (c is ListView or RichTextBox or ListBox or TreeView or Panel or FlowLayoutPanel)
                    Scrollbars(c, t.IsDark);
                if (c is ListView lv) { HeaderFiller.Attach(lv, t); EmptyFiller.Attach(lv); }
                Walk(c);
            }
        }
    }
}

/// <summary>
/// Paints the empty strip of a ListView's column header — the part to the right of the last
/// column, which the control fills with the system button face no matter what the ListView's own
/// BackColor is. Subclasses the header control and paints that gap after Windows has drawn the
/// real columns.
/// </summary>
public sealed class HeaderFiller : NativeWindow
{
    private const int WM_PAINT = 0x000F;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETHEADER = LVM_FIRST + 31;

    private const int WM_NCDESTROY = 0x0082;
    private readonly ListView _list;
    private readonly Color _fill;

    /// <summary>
    /// Every filler alive, by header. Held here because nothing else holds them: a NativeWindow that
    /// only the window procedure knows about can be collected, and then the gap is grey again.
    /// </summary>
    private static readonly Dictionary<IntPtr, HeaderFiller> Live = new();

    private HeaderFiller(ListView list, IntPtr headerHandle, Color fill)
    {
        _list = list;
        _fill = fill;
        AssignHandle(headerHandle);
    }

    /// <summary>
    /// Attach to a ListView's header, once. Light as well as dark (2026-10-06): the light theme's gap
    /// is the system's white, beside a header band in the palette's lavender — the band stopped short
    /// of the list's edge, which lookprobe measured on the dashboard and the ticket queue.
    /// </summary>
    public static void Attach(ListView list, Theme t)
    {
        if (list is null || !list.IsHandleCreated) return;
        try
        {
            var header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header == IntPtr.Zero || Live.ContainsKey(header)) return;
            Live[header] = new HeaderFiller(list, header, t.SurfaceAlt);
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCDESTROY) Live.Remove(Handle);
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT) return;

        try
        {
            // Right edge of the last column, in header coordinates. Everything past it is the gap.
            var used = 0;
            foreach (ColumnHeader col in _list.Columns) used += col.Width;

            using var g = Graphics.FromHwnd(Handle);
            var bounds = g.VisibleClipBounds;
            var from = used - _list.GetScrollPos();
            if (from >= bounds.Width) return;

            using var brush = new SolidBrush(_fill);
            g.FillRectangle(brush, from, 0, bounds.Width - from, bounds.Height);
        }
        catch { }
    }
}

/// <summary>
/// Paints the space under a ListView's last row in the list's own colour, after Windows has drawn it.
///
/// That space is where the faint vertical column rules show (see <see cref="NativeDark.NoColumnRules"/>):
/// the Explorer visual style draws them, and owner-drawn rows cover them everywhere but there. The
/// dashboard takes the visual style off its lists instead, which also takes the dark scrollbar; a list
/// without groups can keep the scrollbar and simply have its empty space painted over.
/// </summary>
public sealed class EmptyFiller : NativeWindow
{
    private const int WM_PAINT = 0x000F, WM_NCDESTROY = 0x0082, LVM_GETHEADER = 0x1000 + 31;
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private static readonly Dictionary<IntPtr, EmptyFiller> Live = new();
    private readonly ListView _list;

    private EmptyFiller(ListView list) { _list = list; AssignHandle(list.Handle); }

    public static void Attach(ListView list)
    {
        if (list is null || !list.IsHandleCreated || Live.ContainsKey(list.Handle)) return;
        Live[list.Handle] = new EmptyFiller(list);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCDESTROY) Live.Remove(Handle);
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT || _list.ShowGroups && _list.Groups.Count > 0) return;
        try
        {
            int top = 0;
            if (_list.Items.Count > 0) top = _list.GetItemRect(_list.Items.Count - 1).Bottom;
            else
            {
                var header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero && IsWindowVisible(header) && GetWindowRect(header, out var hr)) top = hr.Bottom - hr.Top;
            }
            var c = _list.ClientSize;
            if (top >= c.Height) return;
            using var g = Graphics.FromHwnd(Handle);
            using var b = new SolidBrush(_list.BackColor);
            g.FillRectangle(b, 0, Math.Max(0, top), c.Width, c.Height - Math.Max(0, top));
        }
        catch { }
    }
}

internal static class ListViewScroll
{
    [DllImport("user32.dll", EntryPoint = "GetScrollPos")]
    private static extern int GetScrollPosNative(IntPtr hWnd, int nBar);
    private const int SB_HORZ = 0;

    /// <summary>How far the list is scrolled sideways, so the header gap starts in the right place.</summary>
    public static int GetScrollPos(this ListView lv)
    {
        try { return lv.IsHandleCreated ? GetScrollPosNative(lv.Handle, SB_HORZ) : 0; } catch { return 0; }
    }
}
