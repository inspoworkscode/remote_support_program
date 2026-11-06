using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteSupport.UI;

// ---------------------------------------------------------------------------------------------
// The controls Windows draws in its own look, redrawn in ours (2026-10-06).
//
// Clint: "Some boxes are round edges, some have round and square edges and some have black lines
// sticking out… All edges must be rounded with no visible black lines… in light and dark mode."
// After the black corners (RoundButton) and the square dropdowns (ThemedCombo), what was left was
// every control the console took straight from Windows: a tick box, a date picker, a spin box, a
// slider, a progress bar, a menu, and a few text boxes and lists with Windows' square black-ish
// border. Each of those is square, and in the dark theme most of them are WHITE. tools/lookprobe
// lists them window by window; this file is what replaced them.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// The colour on the screen behind a control, for the corners of a control Windows draws square —
/// a dropdown cannot be see-through, so its corners are painted this colour instead.
/// </summary>
public static class Ground
{
    public static Color Behind(Control c, Theme t)
    {
        var mid = new Point(c.Width / 2, c.Height / 2);
        for (var p = c.Parent; p != null; p = p.Parent)
        {
            switch (p)
            {
                case Card card: return card.Fill;
                case CanvasPanel canvas: return canvas.ColourAt(canvas.PointToClient(c.PointToScreen(mid)));
                case FieldFrame or InputBox: return t.Surface;
            }
            if (p.BackColor.A == 255) return p.BackColor;
        }
        return t.Peach;
    }

    /// <summary>
    /// Makes a square control look rounded: the corners outside the shape painted the colour behind
    /// it, then the border drawn round over the top, smooth.
    /// </summary>
    public static void Round(Graphics g, Control c, Theme t, Color border, float width, int? radius = null)
    {
        var r = new Rectangle(0, 0, c.Width - 1, c.Height - 1);
        int rad = Math.Min(radius ?? t.RadiusInput, Math.Max(1, r.Height / 2));
        using var path = Theme.RoundedPath(r, rad);
        g.SmoothingMode = SmoothingMode.None;
        using (var outside = new Region(new Rectangle(0, 0, c.Width, c.Height)))
        using (var ground = new SolidBrush(Behind(c, t)))
        {
            outside.Exclude(path);
            g.FillRegion(ground, outside);
        }
        Theme.Smooth(g);
        using var pen = new Pen(border, width);
        g.DrawPath(pen, path);
    }

    /// <summary>The look shared by every field: the surface, rounded, its border purple-or-signature while it has focus.</summary>
    internal static void Field(Graphics g, Rectangle r, Theme t, bool focus, bool enabled = true, Color? inside = null)
    {
        Theme.Smooth(g);
        using var path = Theme.RoundedPath(r, t.RadiusInput);
        using (var fill = new SolidBrush(enabled ? inside ?? t.Surface : t.DisabledBg)) g.FillPath(fill, path);
        using var pen = new Pen(focus ? t.Purple : t.FieldBorder, focus ? 1.8f : 1.2f);
        g.DrawPath(pen, path);
    }

    internal static int Line(Font f) => TextRenderer.MeasureText("Ag", f).Height;
}

/// <summary>
/// A rounded field round any control Windows draws square — a plain TextBox, a ListBox. The control
/// sits inside with its own border taken off; the frame draws the rounded edge, and the purple
/// (signature) ring while the control inside has the keyboard. Its height follows the control
/// inside, so a one-line text box keeps its natural height.
/// </summary>
public class FieldFrame : Panel
{
    private readonly Theme _t;
    public Control Inner { get; }
    private readonly Color _fill;

    public FieldFrame(Theme t, Control inner, Color? fill = null)
    {
        _t = t; Inner = inner; _fill = fill ?? t.Surface;
        BackColor = Color.Transparent;
        TabStop = false;
        int pad = Math.Max(5, t.RadiusInput / 2 + 2);
        Padding = new Padding(pad + 3, pad, pad + 3, pad);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);

        switch (inner)
        {
            case TextBoxBase tb: tb.BorderStyle = BorderStyle.None; break;
            case ListBox lb: lb.BorderStyle = BorderStyle.None; break;
        }
        inner.BackColor = _fill;
        if (fill == null) inner.ForeColor = t.Ink;
        Margin = inner.Margin;
        inner.Margin = Padding.Empty;
        Size = new Size(inner.Width + Padding.Horizontal, inner.Height + Padding.Vertical);
        inner.Dock = DockStyle.Fill;
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
        Controls.Add(inner);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0) return;
        Ground.Field(e.Graphics, r, _t, Inner.ContainsFocus, Enabled, _fill);
    }
}

/// <summary>A tick box with a rounded box, in the palette — ticked is filled with the tenant's colour.</summary>
public class RoundCheck : CheckBox
{
    private readonly Theme _t;
    private bool _hover;

    public RoundCheck(Theme t)
    {
        _t = t;
        Font = t.Body; ForeColor = t.Ink; BackColor = Color.Transparent; Cursor = Cursors.Hand; AutoSize = true;
        UseMnemonic = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        // A CheckBox is a ButtonBase, which marks itself Opaque — see RoundButton for what that did.
        SetStyle(ControlStyles.Opaque, false);
    }

    private int BoxSize => Math.Max(14, Ground.Line(Font) * 3 / 4);
    private int Gap => Math.Max(6, BoxSize / 2);

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return new Size(BoxSize + Gap + text.Width + 4, Math.Max(BoxSize + 4, text.Height + 4));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        int b = BoxSize;
        // A tick box whose words wrap (the installer's) asks for its box at the top, beside the first line.
        bool top = CheckAlign is ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight;
        var box = new Rectangle(1, top ? Math.Max(1, (Ground.Line(Font) - b) / 2 + 1) : (Height - b) / 2, b - 1, b - 1);
        using (var path = Theme.RoundedPath(box, Math.Max(3, b / 4)))
        {
            Color fill = !Enabled ? _t.DisabledBg : Checked ? _t.Accent : _t.Surface;
            using (var f = new SolidBrush(fill)) g.FillPath(f, path);
            if (!Checked || !Enabled)
            {
                // Muted, not the field border: an empty box in the field border's faint colour all but
                // vanished on the dark card (lookprobe's New ticket picture, 2026-10-06).
                var edge = !Enabled ? _t.FieldBorder : Focused || _hover ? _t.Purple : _t.Muted;
                using var pen = new Pen(edge, Focused ? 1.8f : 1.3f);
                g.DrawPath(pen, path);
            }
            else if (Focused)
            {
                using var ring = new Pen(Color.FromArgb(150, _t.Purple), 1.6f);
                using var outer = Theme.RoundedPath(Rectangle.Inflate(box, 1, 1), Math.Max(3, b / 4) + 1);
                g.DrawPath(ring, outer);
            }
            if (Checked)
            {
                using var tick = new Pen(Enabled ? Theme.On(_t.Accent) : _t.Muted, Math.Max(1.6f, b / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(tick, new[]
                {
                    new PointF(box.Left + b * .24f, box.Top + b * .52f),
                    new PointF(box.Left + b * .42f, box.Top + b * .70f),
                    new PointF(box.Left + b * .76f, box.Top + b * .32f)
                });
            }
        }
        var textRect = new Rectangle(b + Gap, 0, Math.Max(0, Width - b - Gap), Height);
        var flags = (top ? TextFormatFlags.Top : TextFormatFlags.VerticalCenter) | TextFormatFlags.Left | TextFormatFlags.NoPrefix | (AutoSize ? TextFormatFlags.SingleLine : TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : _t.Muted, flags);
    }
}

/// <summary>
/// A whole number typed into an ordinary rounded field — the quantity on a quote line. It was a
/// NumericUpDown, whose spin buttons Windows draws grey and square whatever the theme. Up and Down
/// still count, as the spinner did.
/// </summary>
public class NumberBox : InputBox
{
    public int Minimum { get; set; }
    public int Maximum { get; set; }

    public NumberBox(Theme t, int minimum, int maximum, int value, int height = 40) : base(t, height: height)
    {
        Minimum = minimum; Maximum = maximum;
        Box.TextAlign = HorizontalAlignment.Center;
        Box.Text = Math.Clamp(value, minimum, maximum).ToString(CultureInfo.InvariantCulture);
        Box.KeyPress += (_, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
        Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { Value++; e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Down) { Value--; e.Handled = e.SuppressKeyPress = true; }
        };
        Box.Leave += (_, _) => Value = Value;   // tidy what was typed: never empty, never out of range
    }

    public decimal Value
    {
        get => int.TryParse(Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, Minimum, Maximum) : Minimum;
        set => Box.Text = ((int)Math.Clamp(value, Minimum, Maximum)).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A date, shown in words in a rounded field, picked from a calendar of our own drawing. Windows'
/// DateTimePicker and its month calendar are white in the dark theme and cannot be told otherwise.
/// Up and Down move a day; Space, Enter, F4 or a click opens the calendar.
/// </summary>
public class DateBox : Control
{
    private readonly Theme _t;
    private DateTime _value = DateTime.Today;
    private bool _open, _hover;
    public event EventHandler? ValueChanged;

    /// <summary>How the date is written in the box.</summary>
    public string Format { get; set; } = "dddd d MMMM yyyy";

    public DateBox(Theme t)
    {
        _t = t;
        Font = t.Body; ForeColor = t.Ink; TabStop = true; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        BackColor = Color.Transparent;   // after the style that allows it
        Height = Ground.Line(Font) + 16;
        AccessibleRole = AccessibleRole.ComboBox;
        Text = _value.ToString(Format);
    }

    public DateTime Value
    {
        get => _value;
        set
        {
            var v = value.Date;
            if (v == _value) return;
            _value = v; Text = v.ToString(Format); Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0) return;
        Ground.Field(g, r, _t, Focused || _open || (_hover && Enabled), Enabled);
        int arrowW = Math.Max(18, Height * 2 / 3);
        TextRenderer.DrawText(g, _value.ToString(Format), Font, new Rectangle(12, 0, Width - 12 - arrowW, Height), Enabled ? ForeColor : _t.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        Chevron(g, new Rectangle(Width - arrowW - 4, 0, arrowW, Height), Enabled ? _t.Muted : _t.FieldBorder, DeviceDpi);
    }

    internal static void Chevron(Graphics g, Rectangle r, Color c, int dpi)
    {
        Theme.Smooth(g);
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, s = Math.Max(3f, r.Width / 6f);
        using var pen = new Pen(c, Math.Max(1.5f, dpi / 64f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        Open();
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Up && !e.Alt) { Value = Value.AddDays(1); e.Handled = true; }
        else if (e.KeyCode == Keys.Down && !e.Alt) { Value = Value.AddDays(-1); e.Handled = true; }
        else if (e.KeyCode is Keys.Space or Keys.Enter or Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)) { Open(); e.Handled = e.SuppressKeyPress = true; }
    }

    /// <summary>Opens the calendar under the box.</summary>
    public void Open()
    {
        if (_open || !Enabled) return;
        var cal = new CalendarView(_t, _value) { Font = Font };
        var host = new ToolStripControlHost(cal) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = cal.Size };
        var drop = new ToolStripDropDown { Padding = new Padding(4), AutoClose = true }.Themed(_t);
        drop.Items.Add(host);
        cal.Picked += d => { Value = d; drop.Close(); Focus(); };
        drop.Closed += (_, _) => { _open = false; Invalidate(); BeginInvoke((Action)(() => drop.Dispose())); };
        _open = true; Invalidate();
        drop.Show(this, new Point(0, Height + 2));
        cal.Focus();
    }
}

/// <summary>The month the date box opens on: drawn in the palette, the chosen day in the tenant's colour.</summary>
public sealed class CalendarView : Control
{
    private readonly Theme _t;
    private DateTime _month, _cursor;
    private readonly DateTime _chosen;
    private Point _hoverCell = new(-1, -1);
    public event Action<DateTime>? Picked;

    private readonly int _cell, _row, _head, _pad = 6;
    private readonly DayOfWeek _first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    public CalendarView(Theme t, DateTime chosen)
    {
        _t = t; _chosen = chosen.Date; _cursor = _chosen;
        _month = new DateTime(chosen.Year, chosen.Month, 1);
        Font = t.Body;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = t.Surface;
        int line = Ground.Line(t.Body);
        _cell = Math.Max(line * 2, TextRenderer.MeasureText("Wed", t.Small).Width + 10);
        _row = line + 12;
        _head = line + 18;
        Size = new Size(_pad * 2 + _cell * 7, _pad * 2 + _head + _row * 7);
    }

    private DateTime FirstShown
    {
        get
        {
            int back = ((int)_month.DayOfWeek - (int)_first + 7) % 7;
            return _month.AddDays(-back);
        }
    }

    private Rectangle Prev => new(_pad, _pad, _cell, _head - 6);
    private Rectangle Next => new(Width - _pad - _cell, _pad, _cell, _head - 6);
    private Rectangle CellRect(int col, int row) => new(_pad + col * _cell, _pad + _head + _row * (row + 1), _cell, _row);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        using (var bg = new SolidBrush(_t.Surface)) g.FillRectangle(bg, ClientRectangle);

        TextRenderer.DrawText(g, _month.ToString("MMMM yyyy"), _t.BodyBold, new Rectangle(_pad, _pad, Width - _pad * 2, _head - 6), _t.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Arrow(g, Prev, left: true);
        Arrow(g, Next, left: false);

        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        for (int c = 0; c < 7; c++)
        {
            var name = names[((int)_first + c) % 7];
            var r = new Rectangle(_pad + c * _cell, _pad + _head, _cell, _row);
            TextRenderer.DrawText(g, name.Length > 2 ? name[..2] : name, _t.Small, r, _t.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var day = FirstShown;
        for (int row = 0; row < 6; row++)
            for (int col = 0; col < 7; col++, day = day.AddDays(1))
            {
                var r = Rectangle.Inflate(CellRect(col, row), -2, -2);
                bool chosen = day == _chosen, cursor = day == _cursor && Focused, hover = _hoverCell == new Point(col, row);
                Color fg = day.Month == _month.Month ? _t.Ink : _t.Muted;
                using var path = Theme.RoundedPath(r, Math.Min(r.Height / 2, 10));
                if (chosen)
                {
                    using var f = new SolidBrush(_t.Accent); g.FillPath(f, path);
                    fg = Theme.On(_t.Accent);
                }
                else if (hover || cursor)
                {
                    using var f = new SolidBrush(_t.SurfaceAlt); g.FillPath(f, path);
                    fg = _t.SurfaceAltText;
                }
                if (day == DateTime.Today && !chosen)
                {
                    using var ring = new Pen(_t.Purple, 1.4f); g.DrawPath(ring, path);
                }
                TextRenderer.DrawText(g, day.Day.ToString(CultureInfo.CurrentCulture), day == _chosen ? _t.BodyBold : _t.Body, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
    }

    private void Arrow(Graphics g, Rectangle r, bool left)
    {
        float cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f, s = Math.Max(3.5f, r.Height / 6f);
        using var pen = new Pen(_t.Grape, Math.Max(1.6f, DeviceDpi / 60f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var x = left ? -s / 2 : s / 2;
        g.DrawLines(pen, new[] { new PointF(cx - x, cy - s), new PointF(cx + x, cy), new PointF(cx - x, cy + s) });
    }

    private bool CellAt(Point p, out Point cell)
    {
        cell = new Point(-1, -1);
        for (int row = 0; row < 6; row++)
            for (int col = 0; col < 7; col++)
                if (CellRect(col, row).Contains(p)) { cell = new Point(col, row); return true; }
        return false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        CellAt(e.Location, out var c);
        if (c != _hoverCell) { _hoverCell = c; Invalidate(); }
        Cursor = c.X >= 0 || Prev.Contains(e.Location) || Next.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverCell = new Point(-1, -1); Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Prev.Contains(e.Location)) { Move_(-1); return; }
        if (Next.Contains(e.Location)) { Move_(1); return; }
        if (CellAt(e.Location, out var c)) Picked?.Invoke(FirstShown.AddDays(c.Y * 7 + c.X));
    }

    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Move_(e.Delta > 0 ? -1 : 1); }

    private void Move_(int months) { _month = _month.AddMonths(months); _cursor = _cursor.AddMonths(months); Invalidate(); }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int days = e.KeyCode switch { Keys.Left => -1, Keys.Right => 1, Keys.Up => -7, Keys.Down => 7, _ => 0 };
        if (days != 0)
        {
            _cursor = _cursor.AddDays(days);
            _month = new DateTime(_cursor.Year, _cursor.Month, 1);
            Invalidate(); e.Handled = true;
        }
        else if (e.KeyCode == Keys.PageUp) { Move_(-1); e.Handled = true; }
        else if (e.KeyCode == Keys.PageDown) { Move_(1); e.Handled = true; }
        else if (e.KeyCode is Keys.Enter or Keys.Space) { Picked?.Invoke(_cursor); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
}

/// <summary>
/// A time of day from a dropdown in quarter hours — the same rounded dropdown as every other. A time
/// that is not on a quarter hour (an appointment moved in the portal) is added to the list rather
/// than rounded, so opening a ticket never changes its appointment.
/// </summary>
public class TimeBox : ThemedCombo
{
    public event EventHandler? ValueChanged;

    private sealed class Slot
    {
        public TimeSpan Time;
        public override string ToString() => DateTime.Today.Add(Time).ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public TimeBox(Theme t, int stepMinutes = 15) : base(t)
    {
        for (int m = 0; m < 24 * 60; m += stepMinutes) Items.Add(new Slot { Time = TimeSpan.FromMinutes(m) });
        MaxDropDownItems = 12;
        Value = DateTime.Today.AddHours(9);
        SelectedIndexChanged += (_, _) => ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Today at the time chosen — the date part means nothing, as with DateTimePicker's Time format.</summary>
    public DateTime Value
    {
        get => DateTime.Today.Add(SelectedItem is Slot s ? s.Time : TimeSpan.FromHours(9));
        set
        {
            var want = new TimeSpan(value.Hour, value.Minute, 0);
            int at = 0;
            for (; at < Items.Count; at++)
            {
                var have = ((Slot)Items[at]!).Time;
                if (have == want) { SelectedIndex = at; return; }
                if (have > want) break;
            }
            Items.Insert(at, new Slot { Time = want });
            SelectedIndex = at;
        }
    }
}

/// <summary>A slider in the palette — the session's Quality. Windows' TrackBar is grey on grey, square, in either theme.</summary>
public class RoundSlider : Control
{
    private readonly Theme _t;
    private int _min, _max = 100, _value = 50;
    private bool _drag, _hover;
    public event EventHandler? ValueChanged;

    public RoundSlider(Theme t)
    {
        _t = t;
        TabStop = true; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Height = Math.Max(26, Ground.Line(t.Body) + 8);
        AccessibleRole = AccessibleRole.Slider;
    }

    public int Minimum { get => _min; set { _min = value; Value = _value; Invalidate(); } }
    public int Maximum { get => _max; set { _max = value; Value = _value; Invalidate(); } }
    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, _min, Math.Max(_min, _max));
            if (v == _value) return;
            _value = v; Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int Thumb => Math.Max(14, Height / 2 + 2);
    private int TrackLeft => Thumb / 2 + 1;
    private int TrackRight => Width - Thumb / 2 - 2;
    private float X(int v) => TrackLeft + (TrackRight - TrackLeft) * (_max == _min ? 0 : (v - _min) / (float)(_max - _min));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        int h = Math.Max(4, Height / 7), cy = Height / 2;
        var track = new Rectangle(TrackLeft, cy - h / 2, Math.Max(1, TrackRight - TrackLeft), h);
        using (var path = Theme.RoundedPath(track, h / 2)) using (var b = new SolidBrush(_t.IsDark ? _t.FieldBorder : _t.SurfaceAlt)) g.FillPath(b, path);
        float x = X(_value);
        var done = new Rectangle(track.Left, track.Top, Math.Max(1, (int)(x - track.Left)), h);
        using (var path = Theme.RoundedPath(done, h / 2)) using (var b = new SolidBrush(Enabled ? _t.Accent : _t.Muted)) g.FillPath(b, path);

        int d = Thumb;
        var knob = new RectangleF(x - d / 2f, cy - d / 2f, d, d);
        using (var b = new SolidBrush(Enabled ? (_drag || _hover ? Theme.Lighten(_t.Accent, .1) : _t.Accent) : _t.Muted)) g.FillEllipse(b, knob);
        using (var ring = new Pen(_t.Surface, 2f)) g.DrawEllipse(ring, RectangleF.Inflate(knob, -1.5f, -1.5f));
        if (Focused) { using var focus = new Pen(Color.FromArgb(140, _t.Purple), 1.6f); g.DrawEllipse(focus, RectangleF.Inflate(knob, 2, 2)); }
    }

    private void From(int px) => Value = _min + (int)Math.Round((px - TrackLeft) / (double)Math.Max(1, TrackRight - TrackLeft) * (_max - _min));

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus(); _drag = true; Capture = true; From(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) From(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _drag = false; Capture = false; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = Math.Max(1, (_max - _min) / 20);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Value -= 1; break;
            case Keys.Right or Keys.Up: Value += 1; break;
            case Keys.PageDown: Value -= step; break;
            case Keys.PageUp: Value += step; break;
            case Keys.Home: Value = _min; break;
            case Keys.End: Value = _max; break;
            default: return;
        }
        e.Handled = true;
    }
}

/// <summary>A rounded progress bar in the tenant's colour. Windows' is square and green.</summary>
public class RoundProgress : Control
{
    private readonly Theme _t;
    private int _value, _max = 100;

    public RoundProgress(Theme t)
    {
        _t = t;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public int Maximum { get => _max; set { _max = Math.Max(1, value); Invalidate(); } }
    public int Value { get => _value; set { _value = Math.Clamp(value, 0, _max); Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        int h = Math.Min(Math.Max(6, Height / 3), Height - 2);
        var track = new Rectangle(1, (Height - h) / 2, Math.Max(1, Width - 3), h);
        using (var path = Theme.RoundedPath(track, h / 2)) using (var b = new SolidBrush(_t.IsDark ? _t.FieldBorder : _t.SurfaceAlt)) g.FillPath(b, path);
        int w = (int)(track.Width * (_value / (double)_max));
        if (w < h) return;
        using (var path = Theme.RoundedPath(new Rectangle(track.Left, track.Top, w, h), h / 2))
        using (var b = new SolidBrush(_t.Accent)) g.FillPath(b, path);
    }
}

/// <summary>
/// Menus in the palette, with rounded corners. Windows draws a ContextMenuStrip light grey with a
/// grey gutter down the left whatever the theme — on the dark console that was a white box opening
/// from a dark button.
/// </summary>
public static class ThemedMenus
{
    public static T Themed<T>(this T menu, Theme t) where T : ToolStripDropDown
    {
        menu.Renderer = new ThemedMenuRenderer(t);
        menu.BackColor = t.Surface;
        menu.ForeColor = t.Ink;
        // Items added later (the Tools menu is filled as each tool is made) and their own sub-menus.
        menu.Opening += (_, _) => { foreach (ToolStripItem i in menu.Items) Sub(i, t); };
        menu.HandleCreated += (_, _) => Round(menu, t);
        if (menu.IsHandleCreated) Round(menu, t);
        return menu;
    }

    private static void Sub(ToolStripItem item, Theme t)
    {
        if (item is ToolStripDropDownItem d && d.HasDropDownItems && d.DropDown.Renderer is not ThemedMenuRenderer)
            d.DropDown.Themed(t);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 11 rounds the menu's window and draws its border in our colour; Windows 10
    /// ignores both, and the renderer's own border is drawn instead.</summary>
    internal static bool Rounded { get; } = Environment.OSVersion.Version.Build >= 22000;

    private static void Round(Control menu, Theme t)
    {
        if (!Rounded) return;
        try
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int colour = ColorTranslator.ToWin32(t.FieldBorder);
            DwmSetWindowAttribute(menu.Handle, DWMWA_BORDER_COLOR, ref colour, sizeof(int));
        }
        catch { }
    }
}

public sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly Theme _t;
    public ThemedMenuRenderer(Theme t) : base(new Colours(t)) { _t = t; RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(_t.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (ThemedMenus.Rounded) return;   // Windows 11 draws a rounded border in FieldBorder itself
        using var pen = new Pen(_t.FieldBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(_t.Surface);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        Theme.Smooth(e.Graphics);
        var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = Theme.RoundedPath(r, 6);
        using var b = new SolidBrush(_t.SurfaceAlt);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? _t.Muted : e.Item.Selected ? _t.SurfaceAltText : _t.Ink;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(_t.FieldBorder);
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        Theme.Smooth(e.Graphics);
        var r = e.ImageRectangle;
        float s = Math.Min(r.Width, r.Height);
        using var pen = new Pen(_t.Grape, Math.Max(1.8f, s / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(r.Left + s * .22f, r.Top + s * .52f),
            new PointF(r.Left + s * .42f, r.Top + s * .72f),
            new PointF(r.Left + s * .80f, r.Top + s * .30f)
        });
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? _t.FieldBorder : _t.Muted;
        base.OnRenderArrow(e);
    }

    private sealed class Colours : ProfessionalColorTable
    {
        private readonly Theme _t;
        public Colours(Theme t) { _t = t; UseSystemColors = false; }
        public override Color ToolStripDropDownBackground => _t.Surface;
        public override Color ImageMarginGradientBegin => _t.Surface;
        public override Color ImageMarginGradientMiddle => _t.Surface;
        public override Color ImageMarginGradientEnd => _t.Surface;
        public override Color MenuBorder => _t.FieldBorder;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => _t.SurfaceAlt;
        public override Color SeparatorDark => _t.FieldBorder;
        public override Color SeparatorLight => _t.FieldBorder;
    }
}
