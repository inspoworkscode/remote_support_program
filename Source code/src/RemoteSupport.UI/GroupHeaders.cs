using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteSupport.UI;

/// <summary>
/// Paints a ListView's group headings — the customer names in the console's device list — in our
/// colours instead of Windows'.
///
/// Technicians, 2026-09-30: in dark mode "the client names are hardly readable, they are black."
/// They were. A group heading is drawn by the list control itself, and nothing WinForms offers
/// reaches it: not ForeColor, not the owner-draw events (those are for rows), not the header
/// colours of LVGROUPMETRICS (documented, and ignored). The list sends a custom-draw notification
/// for each heading, though, marked LVCDI_GROUP, and that is the one door in. This listens there,
/// ahead of WinForms — whose own custom-draw handler takes the heading's group id for a ROW index —
/// and draws the heading whole: the name in the signature colour, the way the portal writes a
/// client's name (a link, --grape), a hairline after it, and the fold chevron.
///
/// Folding still belongs to the list: a click on the heading is the control's own, and so is the
/// collapsed state. Only the picture is ours.
/// </summary>
public sealed class GroupHeaders : NativeWindow
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVGROUP lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETGROUPRECT = LVM_FIRST + 98;
    private const int LVM_GETGROUPINFO = LVM_FIRST + 149;
    private const int LVM_GETGROUPCOUNT = LVM_FIRST + 152;
    private const int LVM_GETGROUPINFOBYINDEX = LVM_FIRST + 153;
    private const int LVGGR_HEADER = 1;
    private const uint LVGF_HEADER = 0x1, LVGF_STATE = 0x4, LVGF_GROUPID = 0x10;
    private const uint LVGS_COLLAPSED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVGROUP
    {
        public uint cbSize, mask;
        public IntPtr pszHeader; public int cchHeader;
        public IntPtr pszFooter; public int cchFooter;
        public int iGroupId;
        public uint stateMask, state, uAlign;
        public IntPtr pszSubtitle; public uint cchSubtitle;
        public IntPtr pszTask; public uint cchTask;
        public IntPtr pszDescriptionTop; public uint cchDescriptionTop;
        public IntPtr pszDescriptionBottom; public uint cchDescriptionBottom;
        public int iTitleImage, iExtendedImage, iFirstItem;
        public uint cItems;
        public IntPtr pszSubsetTitle; public uint cchSubsetTitle;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVHITTESTINFO lParam);

    private const int WM_REFLECT_NOTIFY = 0x2000 + 0x004E;
    private const int NM_CUSTOMDRAW = -12;
    private const uint CDDS_PREPAINT = 0x00000001;
    private const uint LVCDI_GROUP = 1;
    private const int CDRF_SKIPDEFAULT = 0x4;
    private const int LVM_HITTEST = LVM_FIRST + 18;
    private const uint LVHT_EX_GROUP_COLLAPSE = 0x40000000;

    // NMHDR nested, not flattened: on 64-bit it is padded to 24 bytes, and written out flat the
    // fields after `code` are read four bytes early. That was the first run — every stage 0.
    [StructLayout(LayoutKind.Sequential)]
    private struct NMHDR { public IntPtr hwndFrom; public UIntPtr idFrom; public int code; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NMLVCUSTOMDRAW
    {
        public NMHDR hdr;
        public uint dwDrawStage; public IntPtr hdc; public RECT rc;
        public UIntPtr dwItemSpec; public uint uItemState; public IntPtr lItemlParam;
        public uint clrText, clrTextBk; public int iSubItem; public uint dwItemType;
        public uint clrFace; public int iIconEffect, iIconPhase, iPartId, iStateId;
        public RECT rcText; public uint uAlign;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO { public Point pt; public uint flags; public int iItem, iSubItem, iGroup; }

    private readonly ListView _list;
    private readonly Theme _t;
    private readonly Font _bold;
    /// <summary>Where the list keeps its fold button, relative to a heading's top-right corner.
    /// Found once by asking the list, because only the list knows, and cached by heading height.</summary>
    private Rectangle _button;
    private int _buttonFor = -1;

    private GroupHeaders(ListView list, Theme t)
    {
        _list = list; _t = t;
        _bold = new Font(list.Font, FontStyle.Bold);
        AssignHandle(list.Handle);
    }

    /// <summary>
    /// Paint this list's group headings from now on. Call once its handle exists (the form's
    /// Shown); a recreated handle is picked up again by itself.
    /// </summary>
    public static void Attach(ListView list, Theme t)
    {
        if (list is null) return;
        if (list.IsHandleCreated) { try { _ = new GroupHeaders(list, t); } catch { } }
        list.HandleCreated += (_, _) => { try { _ = new GroupHeaders(list, t); } catch { } };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_REFLECT_NOTIFY && m.LParam != IntPtr.Zero)
        {
            try
            {
                var cd = Marshal.PtrToStructure<NMLVCUSTOMDRAW>(m.LParam);
                if (cd.hdr.code == NM_CUSTOMDRAW && cd.dwDrawStage == CDDS_PREPAINT && cd.dwItemType == LVCDI_GROUP
                    && Paint(cd.hdc, (int)cd.dwItemSpec.ToUInt64()))
                {
                    m.Result = (IntPtr)CDRF_SKIPDEFAULT;
                    return;
                }
            }
            catch { }   // anything unexpected: let Windows draw it, as it always has
        }
        base.WndProc(ref m);
    }

    /// <summary>Draws one heading. False if the group could not be found, so Windows draws it.</summary>
    private bool Paint(IntPtr hdc, int groupId)
    {
        // Asked of the list by id. Not by position in ListView.Groups: WinForms keeps a hidden
        // default group at the list's index 0, so positions are one out — the first run of this
        // wrote every customer's name over the heading below it.
        if (GroupById(_list, groupId) is not { } info) return false;
        var (header, collapsed) = info;

        var r = new RECT { Top = LVGGR_HEADER };
        if (SendMessage(_list.Handle, LVM_GETGROUPRECT, (IntPtr)groupId, ref r) == IntPtr.Zero) return false;
        var band = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        if (band.Width <= 0 || band.Height <= 0) return false;

        using var g2 = Graphics.FromHdc(hdc);
        using (var bg = new SolidBrush(_t.Surface)) g2.FillRectangle(bg, band);

        // "Zenith Motors   (2 machines, 1 online)": the name is what the eye looks for, so it
        // takes the colour; the count after it is muted, as the portal puts it in a grey pill.
        var cut = header.IndexOf("   (", StringComparison.Ordinal);
        var name = cut > 0 ? header[..cut] : header;
        var count = cut > 0 ? header[cut..] : "";

        var button = FoldButton(band);
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                                      | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
        int pad = _list.LogicalToDeviceUnits(8);
        var text = Rectangle.FromLTRB(band.Left + pad, band.Top, button.Left - pad, band.Bottom - _list.LogicalToDeviceUnits(4));
        TextRenderer.DrawText(g2, name, _bold, text, _t.SurfaceAltText, flags);
        int nameW = TextRenderer.MeasureText(g2, name, _bold, text.Size, flags).Width;
        if (count.Length > 0 && nameW < text.Width)
            TextRenderer.DrawText(g2, count, _list.Font,
                Rectangle.FromLTRB(text.Left + nameW, text.Top, text.Right, text.Bottom), _t.Muted, flags);

        // A hairline under the whole band: the list's own was a short white bar on a dark ground.
        int y = band.Bottom - _list.LogicalToDeviceUnits(4);
        using (var line = new Pen(_t.CardBorder)) g2.DrawLine(line, band.Left + pad, y, band.Right - pad, y);

        // The fold chevron, where the list's own button is, so a click lands where it is drawn:
        // pointing up while open ("fold this away"), down while folded.
        Theme.Smooth(g2);
        using var pen = new Pen(_t.Muted, Math.Max(1.5f, _list.LogicalToDeviceUnits(2) * 0.8f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        float cx = button.Left + button.Width / 2f, cy = button.Top + button.Height / 2f, h = _list.LogicalToDeviceUnits(5);
        var pts = collapsed
            ? new[] { new PointF(cx - h, cy - h / 2), new PointF(cx, cy + h / 2), new PointF(cx + h, cy - h / 2) }
            : new[] { new PointF(cx - h, cy + h / 2), new PointF(cx, cy - h / 2), new PointF(cx + h, cy + h / 2) };
        g2.DrawLines(pen, pts);
        return true;
    }

    /// <summary>
    /// The list's fold button for this heading, found by asking the list what is under each point
    /// near the heading's right-hand end. Asked once per heading height and remembered as an offset.
    /// </summary>
    private Rectangle FoldButton(Rectangle band)
    {
        if (_buttonFor != band.Height)
        {
            _buttonFor = band.Height;
            int reach = Math.Min(band.Width, _list.LogicalToDeviceUnits(48));
            int l = int.MaxValue, t = int.MaxValue, rr = int.MinValue, b = int.MinValue;
            for (int y = band.Top; y < band.Bottom; y++)
                for (int x = band.Right - reach; x < band.Right; x++)
                {
                    var hit = new LVHITTESTINFO { pt = new Point(x, y) };
                    SendMessage(_list.Handle, LVM_HITTEST, (IntPtr)(-1), ref hit);
                    if ((hit.flags & LVHT_EX_GROUP_COLLAPSE) == 0) continue;
                    l = Math.Min(l, x); t = Math.Min(t, y); rr = Math.Max(rr, x); b = Math.Max(b, y);
                }
            _button = l == int.MaxValue
                ? Rectangle.Empty
                : new Rectangle(l - band.Right, t - band.Top, rr - l + 1, b - t + 1);
        }
        if (_button.IsEmpty)
        {
            // Not collapsible, or the list would not say: a chevron at the right-hand end anyway.
            int s = _list.LogicalToDeviceUnits(12);
            return new Rectangle(band.Right - s - _list.LogicalToDeviceUnits(6), band.Top + (band.Height - s) / 2, s, s);
        }
        return new Rectangle(band.Right + _button.Left, band.Top + _button.Top, _button.Width, _button.Height);
    }

    /// <summary>What the list itself holds for a group: its heading text and whether it is folded.</summary>
    private static (string header, bool collapsed)? GroupById(ListView lv, int id)
    {
        if (!lv.IsHandleCreated) return null;
        const int cap = 512;
        var buf = Marshal.AllocHGlobal(cap * 2);
        try
        {
            var g = new LVGROUP
            {
                cbSize = (uint)Marshal.SizeOf<LVGROUP>(), mask = LVGF_HEADER | LVGF_STATE, stateMask = LVGS_COLLAPSED,
                pszHeader = buf, cchHeader = cap
            };
            // It answers with the group's id, and -1 for no such group — 0 is a real id.
            if (SendMessage(lv.Handle, LVM_GETGROUPINFO, (IntPtr)id, ref g) == (IntPtr)(-1)) return null;
            return (Marshal.PtrToStringUni(g.pszHeader) ?? "", (g.state & LVGS_COLLAPSED) != 0);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }


    /// <summary>
    /// Where the list has put a group's heading, in its client coordinates, by the group's position
    /// in <see cref="ListView.Groups"/>. Empty if it has no heading on screen. Public for `dashtabs`,
    /// which reads the pixels there. Found by heading text, because the list's own positions are
    /// not ListView.Groups' (see Paint).
    /// </summary>
    public static Rectangle HeaderBounds(ListView lv, int index)
    {
        try
        {
            if (!lv.IsHandleCreated || index < 0 || index >= lv.Groups.Count) return Rectangle.Empty;
            var want = lv.Groups[index].Header ?? "";
            int count = (int)SendMessage(lv.Handle, LVM_GETGROUPCOUNT, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < count; i++)
            {
                var g = new LVGROUP { cbSize = (uint)Marshal.SizeOf<LVGROUP>(), mask = LVGF_GROUPID };
                if (SendMessage(lv.Handle, LVM_GETGROUPINFOBYINDEX, (IntPtr)i, ref g) == IntPtr.Zero) continue;
                if (GroupById(lv, g.iGroupId)?.header != want) continue;
                var r = new RECT { Top = LVGGR_HEADER };
                if (SendMessage(lv.Handle, LVM_GETGROUPRECT, (IntPtr)g.iGroupId, ref r) == IntPtr.Zero) return Rectangle.Empty;
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            return Rectangle.Empty;
        }
        catch { return Rectangle.Empty; }
    }
}
