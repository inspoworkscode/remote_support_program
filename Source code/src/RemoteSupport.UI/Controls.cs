using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RemoteSupport.UI;

/// <summary>Warm peach→indigo gradient canvas, the desktop equivalent of the web page background.</summary>
public class CanvasPanel : Panel
{
    private readonly Theme _t;
    public CanvasPanel(Theme t) { _t = t; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }

    /// <summary>The colour the gradient has at a point — what a native control sitting here must
    /// paint its corners with (<see cref="Ground"/>).</summary>
    public Color ColourAt(Point p)
    {
        double f = Width <= 0 || Height <= 0 ? 0 : Math.Clamp((p.X / (double)Width + p.Y / (double)Height) / 2, 0, 1);
        return Theme.Blend(_t.Peach, _t.Mist, f);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var r = ClientRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;
        using var b = new LinearGradientBrush(r, _t.Peach, _t.Mist, LinearGradientMode.ForwardDiagonal);
        e.Graphics.FillRectangle(b, r);
        base.OnPaint(e);
    }
}

/// <summary>Rounded white card with a soft border — the workhorse container.</summary>
public class Card : Panel
{
    private readonly Theme _t;
    public int Radius { get; set; }
    public Card(Theme t, int padding = 14)
    {
        _t = t; Radius = t.RadiusCard;
        Padding = new Padding(padding);
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }
    /// <summary>What the inside of the card is painted.</summary>
    public Color Fill => _t.CardBg;

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Smooth(e.Graphics);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = Theme.RoundedPath(r, Radius);
        using var fill = new SolidBrush(_t.CardBg);
        using var pen = new Pen(_t.CardBorder);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
        base.OnPaint(e);
    }
}

/// <summary>
/// How a button looks, by what it is for — the portal's three, plus two kept for their meaning.
///
/// <para><b>Accent</b> is filled with the tenant's colour: the ONE main action of a window or area
/// (Sign in, Connect, Send, Post), and the chosen tab of a set. <b>Ghost</b> and <b>Neutral</b> are
/// the same outlined look, the portal's <c>.btn</c> — every ordinary button. They were two
/// different looks until 2026-10-06, a grey pill and an outline, which with the filled one made
/// three looks on one toolbar; Neutral is kept as a name only so the call sites still say what
/// they meant. <b>Danger</b> is outlined in red (Leave / End, Stop, Remove); <b>Success</b> is the
/// green of Accept call and Run.</para>
/// </summary>
public enum BtnStyle { Accent, Ghost, Danger, Success, Neutral }

/// <summary>Rounded, bold, gradient-filled button. Replaces the flat WinForms look everywhere.</summary>
public class RoundButton : Button
{
    private readonly Theme _t;
    private bool _hover, _down;
    public BtnStyle Style { get; set; } = BtnStyle.Accent;
    public int Radius { get; set; }

    /// <summary>
    /// A colour of its own, overriding <see cref="Style"/> (2026-10-09): New ticket's job types, each in its category's
    /// colour. Chosen, it is filled with <see cref="Tint"/> and white words; otherwise outlined, words and border in
    /// <see cref="TintInk"/> — the lighter shade on a dark surface. Null: the button is drawn by its style, as always.
    /// </summary>
    public Color? Tint { get; set; }
    public Color TintInk { get; set; }
    public bool Chosen { get; set; }

    /// <summary>The look a style is drawn with — Ghost and Neutral are one look (see <see cref="BtnStyle"/>).</summary>
    public static string LookOf(BtnStyle s) => s is BtnStyle.Ghost or BtnStyle.Neutral ? "Outline" : s.ToString();

    public RoundButton(Theme t, BtnStyle style = BtnStyle.Accent)
    {
        _t = t; Style = style; Radius = t.RadiusButton;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent; Font = t.ButtonFont; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);

        // THE BLACK CORNERS (2026-10-06). Windows' Button marks itself Opaque, which tells WinForms
        // "I paint every pixel myself" — so it never paints a button's background, and the code that
        // would have is ButtonBase's own OnPaint, which this class replaces. The corners outside the
        // rounded shape were therefore painted by nothing. With OptimizedDoubleBuffer a pixel nothing
        // paints is not left as it was: it is whatever the off-screen buffer held. A button under
        // 225x96 shares one buffer with every other small control, so its corners showed a piece of
        // the last one drawn (the blue corner on Photos, the stubs of a border under Open in browser);
        // a bigger one gets a new buffer, which starts black (Sign In, Connect, Create ticket).
        // Not Opaque: WinForms paints what is behind the button first, as it does for a Label.
        SetStyle(ControlStyles.Opaque, false);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Smooth(e.Graphics);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = Theme.RoundedPath(r, Radius);

        // The outlined look is the portal's .btn: the surface lifted a touch, the signature colour for
        // the words, and a border of the same colour, faint. Hover leans the fill toward the signature
        // rather than lightening it, which on a white surface would change nothing at all.
        var outline = _t.IsDark ? Theme.Lighten(_t.Surface, .05) : _t.Surface;
        Color a, b, fg, border = Color.Empty;
        bool outlined = false;
        switch (Style)
        {
            case BtnStyle.Accent: a = _t.Accent; b = Theme.Lighten(_t.Accent, .18); fg = Theme.On(_t.Accent); break;
            case BtnStyle.Success: a = _t.Green; b = Color.FromArgb(0x10, 0xB9, 0x81); fg = Color.FromArgb(0x04, 0x35, 0x2A); break;
            case BtnStyle.Danger: a = outline; b = outline; fg = _t.BadText; border = Color.FromArgb(140, _t.BadText); outlined = true; break;
            default: a = outline; b = outline; fg = _t.Grape; border = Color.FromArgb(90, _t.Grape); outlined = true; break;
        }
        float borderW = 1.4f;
        if (Tint is { } tint)
        {
            if (Chosen) { a = tint; b = Theme.Lighten(tint, .08); fg = Color.White; border = Color.Empty; outlined = false; }
            else { a = outline; b = outline; fg = TintInk; border = Color.FromArgb(210, TintInk); outlined = true; borderW = 2f; }
        }
        if (!Enabled) { a = _t.DisabledBg; b = a; fg = _t.Muted; border = Color.Empty; }
        else if (outlined && (_down || _hover))
        {
            var lean = Theme.Blend(a, fg, _down ? .16 : .08);
            a = b = lean;
            border = Color.FromArgb(_down ? 200 : 150, fg);
        }
        else if (_down) { a = Theme.Darken(a, .12); b = Theme.Darken(b, .12); }
        else if (_hover) { a = Theme.Lighten(a, .07); b = Theme.Lighten(b, .07); }

        using (var fill = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), a, b, LinearGradientMode.ForwardDiagonal))
            e.Graphics.FillPath(fill, path);
        if (border != Color.Empty) { using var pen = new Pen(border, borderW); e.Graphics.DrawPath(pen, path); }

        TextRenderer.DrawText(e.Graphics, Text, Font, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Pill-shaped status badge.</summary>
public class Pill : Label
{
    private Color _bg, _fg;
    public Pill(Theme t, string text, Color bg, Color fg)
    {
        _bg = bg; _fg = fg; Text = text;
        AutoSize = false; Height = 24; TextAlign = ContentAlignment.MiddleCenter;
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        BackColor = Color.Transparent; ForeColor = fg;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }
    public void Set(string text, Color bg, Color fg) { Text = text; _bg = bg; _fg = fg; ForeColor = fg; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Smooth(e.Graphics);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = Theme.RoundedPath(r, r.Height / 2);
        using var fill = new SolidBrush(_bg);
        e.Graphics.FillPath(fill, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, r, _fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Branded header band: tenant primary colour with the logo or company name.</summary>
public class BrandHeader : Panel
{
    private readonly Theme _t;
    private readonly Image? _logo;
    private readonly string _title, _subtitle;
    public BrandHeader(Theme t, Image? logo, string title, string subtitle = "", int height = 96)
    {
        _t = t; _logo = logo; _title = title; _subtitle = subtitle;
        Dock = DockStyle.Top; Height = height;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Where the words start: after the logo, when there is one.</summary>
    public int TextLeft => 18 + (_logo == null ? 0 : LogoWidth() + 14);

    private int LogoWidth()
    {
        if (_logo == null) return 0;
        int h = Math.Min(Height - 26, 56);
        return Math.Min((int)(_logo.Width * (h / (double)_logo.Height)), 220);
    }

    private int _textLimit = int.MaxValue;
    /// <summary>
    /// How far right the words may run — the left edge of the first button the owner has put on the
    /// header. Words that would go further end in "…" instead of being drawn underneath a button
    /// (the console's header, 2026-10-05: a seventh button reached the subtitle on a laptop screen).
    /// </summary>
    public int TextLimit
    {
        get => _textLimit;
        set { if (_textLimit == value) return; _textLimit = value; Invalidate(); }
    }

    /// <summary>Where the words end as drawn: their own width, or the limit, whichever comes first.</summary>
    public int TextRight
    {
        get
        {
            int w = 0;
            if (_logo == null) { using var f = new Font("Segoe UI", 16f, FontStyle.Bold); w = TextRenderer.MeasureText(_title, f).Width; }
            if (!string.IsNullOrEmpty(_subtitle)) w = Math.Max(w, TextRenderer.MeasureText(_subtitle, _t.Body).Width + 1);
            return Math.Min(TextLeft + w, _textLimit);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Smooth(e.Graphics);
        var r = ClientRectangle;
        if (r.Width <= 0 || r.Height <= 0) return;
        using (var b = new LinearGradientBrush(r, _t.Primary, Theme.Lighten(_t.Primary, .16), LinearGradientMode.Horizontal))
            e.Graphics.FillRectangle(b, r);

        int x = 18;
        if (_logo != null)
        {
            int h = Math.Min(Height - 26, 56);
            int w = LogoWidth();
            e.Graphics.DrawImage(_logo, new Rectangle(x, (Height - h) / 2, w, h));
            x += w + 14;
        }
        var fg = Theme.On(_t.Primary);
        if (_logo == null || !string.IsNullOrEmpty(_subtitle))
        {
            var titleFont = _logo == null ? new Font("Segoe UI", 16f, FontStyle.Bold) : _t.H3;
            int ty = string.IsNullOrEmpty(_subtitle) ? (Height - titleFont.Height) / 2 : Height / 2 - titleFont.Height;
            // Within the limit, ending in "…" rather than running underneath a button.
            int room = Math.Max(0, Math.Min(r.Width, _textLimit) - x);
            const TextFormatFlags Fit = TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            if (_logo == null) TextRenderer.DrawText(e.Graphics, _title, titleFont, new Rectangle(x, ty, room, titleFont.Height + 2), fg, Fit);
            if (!string.IsNullOrEmpty(_subtitle))
                TextRenderer.DrawText(e.Graphics, _subtitle, _t.Body, new Rectangle(x + 1, ty + titleFont.Height + 2, Math.Max(0, room - 1), _t.Body.Height + 2), Color.FromArgb(210, fg), Fit);
            if (_logo == null) titleFont.Dispose();
        }
    }
}

/// <summary>Rounded input: a painted border panel wrapping a borderless TextBox.</summary>
public class InputBox : Panel
{
    private readonly Theme _t;
    public TextBox Box { get; }
    private bool _focus;
    public InputBox(Theme t, bool password = false, bool multiline = false, int height = 40)
    {
        _t = t;
        Height = multiline ? Math.Max(height, 70) : height;
        BackColor = Color.Transparent;
        TabStop = false;   // the wrapper must not swallow Tab — the inner TextBox takes focus
        Padding = new Padding(12, multiline ? 8 : 0, 12, 0);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10.5f), BackColor = t.Surface,
            ForeColor = t.Ink, Multiline = multiline,
            Dock = multiline ? DockStyle.Fill : DockStyle.None
        };
        if (password) Box.UseSystemPasswordChar = true;
        if (!multiline)
        {
            Box.Width = Width - 24;
            Box.Location = new Point(12, (Height - Box.Height) / 2);
            Box.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Resize += (_, _) => { Box.Width = Width - 24; Box.Location = new Point(12, (Height - Box.Height) / 2); };
            Box.FontChanged += (_, _) => Box.Location = new Point(12, (Height - Box.Height) / 2);
        }
        Box.GotFocus += (_, _) => { _focus = true; Invalidate(); };
        Box.LostFocus += (_, _) => { _focus = false; Invalidate(); };
        Controls.Add(Box);
    }
    public string Value { get => Box.Text; set => Box.Text = value; }

    private int _growMin, _growMax;

    /// <summary>
    /// A multiline box that grows a line at a time as it is typed into (Clint, 2026-10-13: "all the comments boxes in the
    /// ticketing system need to automatically expand when putting in a new line" — the console showed three lines and
    /// the rest went out of sight). From the height it was made with up to <paramref name="maxLines"/> lines of its font,
    /// then a scroll bar — so a long note never pushes Submit off the window. Counted by Windows itself (EM_GETLINECOUNT),
    /// so a line wrapped at the edge counts as the line it looks like.
    /// </summary>
    public InputBox GrowWithText(int maxLines = 15)
    {
        if (!Box.Multiline) return this;
        _growMin = Height;
        _growMax = Math.Max(_growMin, Padding.Top + Padding.Bottom + LineHeight() * maxLines + 8);
        Box.WordWrap = true;
        Box.TextChanged += (_, _) => Grow();
        Box.HandleCreated += (_, _) => Grow();
        Box.SizeChanged += (_, _) => { if (!_growing) Grow(); };
        return this;
    }

    private bool _growing;
    private int LineHeight() => TextRenderer.MeasureText("Ag", Box.Font).Height;

    private void Grow()
    {
        if (_growMax == 0 || !Box.IsHandleCreated || _growing) return;
        _growing = true;
        try
        {
            int lines = Math.Max(1, (int)SendMessage(Box.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero));
            int want = Padding.Top + Padding.Bottom + lines * LineHeight() + 8;
            int h = Math.Min(_growMax, Math.Max(_growMin, want));
            var bars = want > _growMax ? ScrollBars.Vertical : ScrollBars.None;
            if (Box.ScrollBars != bars) Box.ScrollBars = bars;
            if (Height != h) Height = h;
        }
        finally { _growing = false; }
    }

    private const int EM_GETLINECOUNT = 0x00BA;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Move focus to another field when the user presses Tab or Enter here.</summary>
    public void NextField(Control next)
    {
        Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Tab or Keys.Enter && !e.Shift)
            { e.SuppressKeyPress = true; e.Handled = true; try { next.Focus(); } catch { } }
        };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Smooth(e.Graphics);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0) return;
        using var path = Theme.RoundedPath(r, _t.RadiusInput);
        using var fill = new SolidBrush(_t.Surface);
        e.Graphics.FillPath(fill, path);
        using var pen = new Pen(_focus ? _t.Purple : _t.FieldBorder, _focus ? 1.8f : 1.2f);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>
/// A dropdown that wears the palette — ALL of it, in dark as in light (2026-09-24).
///
/// <para>A flat WinForms ComboBox takes BackColor and ForeColor, but paints its outer ring in
/// <c>SystemColors.Window</c> — white — whatever the palette, and a focused one paints its chosen
/// item in the system highlight colour. On a dark console that was a white outline round every
/// dropdown and, on whichever had focus, a white bar. This draws the item itself (so the chosen
/// value is never highlighted in the box), then paints its own border and arrow over what Windows
/// drew: the field border like an <see cref="InputBox"/>'s, purple while it has focus.</para>
///
/// <para>Everything is measured (§30): the row height from the font, the arrow from the DPI.</para>
/// </summary>
public class ThemedCombo : ComboBox
{
    private readonly Theme _t;
    private const int WM_PAINT = 0x000F;

    public ThemedCombo(Theme t)
    {
        _t = t;
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = t.Surface;
        ForeColor = t.Ink;
        Font = t.Body;
        ItemHeight = MeasuredItemHeight();
    }

    private int MeasuredItemHeight() => TextRenderer.MeasureText("Ag", Font).Height + 6;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ItemHeight = MeasuredItemHeight();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        // The box itself shows the chosen value plainly — never in the highlight colour, which is the
        // white bar a focused dropdown drew on a dark console. The open list shows the hovered row.
        bool inBox = (e.State & DrawItemState.ComboBoxEdit) != 0;
        bool hot = !inBox && (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(hot ? _t.SurfaceAlt : _t.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, Rectangle.Inflate(e.Bounds, -4, 0),
            hot ? _t.SurfaceAltText : (Enabled ? _t.Ink : _t.Muted),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WM_PAINT || Width <= 2 || Height <= 2) return;
        try
        {
            using var g = CreateGraphics();
            int arrowW = SystemInformation.GetHorizontalScrollBarArrowWidthForDpi(DeviceDpi) + 2;
            var button = new Rectangle(Width - arrowW - 2, 1, arrowW + 1, Height - 2);
            using (var fill = new SolidBrush(_t.Surface)) g.FillRectangle(fill, button);

            // A small chevron, drawn rather than taken from the system so it is the palette's colour.
            Theme.Smooth(g);
            float cx = button.Left + button.Width / 2f, cy = Height / 2f, s = Math.Max(3f, arrowW / 5f);
            using (var arrow = new Pen(Enabled ? _t.Muted : _t.FieldBorder, Math.Max(1.5f, DeviceDpi / 64f)))
                g.DrawLines(arrow, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            using (var inner = new Pen(_t.Surface)) { g.DrawRectangle(inner, 1, 1, Width - 3, Height - 3); g.DrawRectangle(inner, 0, 0, Width - 1, Height - 1); }

            // Rounded like every other field (2026-10-06): Windows draws a ComboBox square, so the
            // corners outside the rounded shape are painted the colour behind it, and the border is
            // drawn round over the top.
            Ground.Round(g, this, _t, Focused || DroppedDown ? _t.Purple : _t.FieldBorder, Focused || DroppedDown ? 1.8f : 1.2f);
        }
        catch { /* a paint that fails leaves Windows' own drawing, which is a worse look and no worse a control */ }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>Section heading with a coloured accent dot.</summary>
public class SectionTitle : Label
{
    private readonly Color _dot;
    private readonly int _dotSize;

    public SectionTitle(Theme t, string text, Color dot)
    {
        _dot = dot; Text = "     " + text; Font = t.H3; ForeColor = t.Ink;
        AutoSize = false; BackColor = Color.Transparent; TextAlign = ContentAlignment.MiddleLeft;
        UseMnemonic = false;   // otherwise "Team & client chat" loses its ampersand

        // Measured from the font, not the 26 px this used to be. 26 was written against a 100%
        // display; one line of H3 is about 28 px at 150%, so every card heading in the product was
        // clipping its own descenders — "Incoming calls" lost the tail of its g. Invisible at 100%
        // and obvious on Clint's screen, which is precisely the shape of fault §30 of the handover
        // is about. It went unreported for months because a heading that is 2 px short still reads.
        int line = TextRenderer.MeasureText("Ag", Font).Height;
        Height = line + 8;
        _dotSize = Math.Max(8, line / 3);

        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Theme.Smooth(e.Graphics);
        using var b = new SolidBrush(_dot);
        // Centred on the text's line rather than at a fixed offset, so it stays beside the words
        // at any scaling instead of drifting up as the heading grows.
        e.Graphics.FillEllipse(b, 0, (Height - _dotSize) / 2, _dotSize, _dotSize);
    }
}

public static class UiExt
{
    /// <summary>Flat, modern styling for a ListView (WinForms can't round these, so we soften them).</summary>
    public static ListView Themed(this ListView lv, Theme t)
    {
        lv.BorderStyle = BorderStyle.None;
        lv.BackColor = t.Surface;
        lv.ForeColor = t.Ink;
        lv.Font = t.Body;
        lv.FullRowSelect = true;
        lv.View = View.Details;
        lv.OwnerDraw = true;
        lv.DrawColumnHeader += (s, e) =>
        {
            using var bg = new SolidBrush(t.SurfaceAlt);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", new Font("Segoe UI", 9f, FontStyle.Bold),
                Rectangle.Inflate(e.Bounds, -8, 0), t.SurfaceAltText, TextFormatFlags.VerticalCenter | Align(e.Header));
        };
        lv.DrawSubItem += (s, e) =>
        {
            var selected = e.Item?.Selected ?? false;
            var back = selected ? t.SurfaceAlt : t.Surface;
            using (var bg = new SolidBrush(back)) e.Graphics.FillRectangle(bg, e.Bounds);
            var fg = e.Item?.ForeColor ?? t.Ink;
            if (selected) fg = t.SurfaceAltText;
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", e.Item?.Font ?? t.Body,
                Rectangle.Inflate(e.Bounds, -8, 0), fg, TextFormatFlags.VerticalCenter | Align(e.Header) | TextFormatFlags.EndEllipsis);
        };
        lv.DrawItem += (s, e) => { };
        // The header band to the list's edge, and nothing drawn under the last row — on the list's
        // own handle, so a list made after its window was shown (a tool window's, the ticket queue's)
        // gets them too rather than waiting for NativeDark.Apply to find it.
        void Fill() { HeaderFiller.Attach(lv, t); EmptyFiller.Attach(lv); }
        lv.HandleCreated += (_, _) => Fill();
        if (lv.IsHandleCreated) Fill();
        return lv;
    }

    /// <summary>
    /// A plain ListBox in the palette: its rows drawn by us, so the chosen one is the palette's raised
    /// row rather than Windows' blue bar. Put it in a <see cref="FieldFrame"/> for its rounded edge.
    /// </summary>
    public static ListBox Themed(this ListBox lb, Theme t)
    {
        lb.BorderStyle = BorderStyle.None;
        lb.BackColor = t.Surface;
        lb.ForeColor = t.Ink;
        lb.DrawMode = DrawMode.OwnerDrawFixed;
        lb.ItemHeight = TextRenderer.MeasureText("Ag", lb.Font).Height + 8;
        lb.IntegralHeight = false;
        lb.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool on = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(on ? t.SurfaceAlt : t.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, lb.GetItemText(lb.Items[e.Index]), lb.Font, Rectangle.Inflate(e.Bounds, -6, 0),
                on ? t.SurfaceAltText : t.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        return lb;
    }

    /// <summary>A column added with HorizontalAlignment.Right (figures) is drawn on the right; every
    /// other column, as before, on the left.</summary>
    private static TextFormatFlags Align(ColumnHeader? h) =>
        h?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
}
