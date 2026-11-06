using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// The list that pops up when @ is typed in a ticket's comment box (Clint, 2026-10-21: "putting in the @
/// should list everyone assigned to the task"): the people on the ticket first, then everybody else,
/// narrowing as the name is typed. Up/Down choose, Enter or Tab or a click puts the whole name in, Esc
/// closes it. Every rule is <see cref="MentionPick"/>; the portal's mentionpick.js is the same rule.
///
/// <para>It never takes the keyboard — the person goes on typing in the box (WS_EX_NOACTIVATE, and a
/// click answered with MA_NOACTIVATE). A WinForms drop-down would take the keys, so this is a window of
/// its own, owned by the ticket window and gone with the box.</para>
/// </summary>
public sealed class MentionPopup : Form
{
    private readonly Theme _t;
    private readonly TextBox _box;
    private readonly IReadOnlyList<string> _first, _all;
    private readonly string _me;
    private readonly int _row, _head, _pad;
    private List<MentionPick.Choice> _items = new();
    private int _sel, _shutAt = -1, _hidden;
    private (int At, string Query)? _tok;

    /// <summary>At most this many names at once; the rest are reached by typing more of the name.</summary>
    public const int MaxShown = 10;

    /// <summary>Puts the list on a comment box. <paramref name="first"/> is the people on the ticket.</summary>
    public static MentionPopup Attach(Theme t, TextBox box, IReadOnlyList<string> first, IReadOnlyList<string> all, string me) =>
        new(t, box, first, all, me);

    private MentionPopup(Theme t, TextBox box, IReadOnlyList<string> first, IReadOnlyList<string> all, string me)
    {
        _t = t; _box = box; _first = first; _all = all; _me = me;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = _t.Surface;
        Font = _t.BodyBold;
        DoubleBuffered = true;
        // Heights from the fonts, never as numbers (§30): readable at any display scaling.
        _row = TextRenderer.MeasureText("Ag", _t.BodyBold).Height * 8 / 5;
        _head = TextRenderer.MeasureText("Ag", _t.Small).Height * 3 / 2;
        _pad = Math.Max(4, _row / 6);

        box.TextChanged += (_, _) => Follow();
        box.MouseUp += (_, _) => Follow();
        box.KeyUp += (_, e) => { if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End) Follow(); };
        box.PreviewKeyDown += (_, e) => { if (Visible && e.KeyCode is Keys.Tab or Keys.Enter or Keys.Up or Keys.Down or Keys.Escape) e.IsInputKey = true; };
        box.KeyDown += OnBoxKey;
        box.LostFocus += (_, _) => BeginInvoke(() => { if (!_box.IsDisposed && !_box.Focused) Close_(); });
        box.Disposed += (_, _) => { if (!IsDisposed) Dispose(); };
    }

    /// <summary>The names on show, for tools/ticketwindowprobe.</summary>
    public IReadOnlyList<MentionPick.Choice> Choices => _items;
    public int Selected => _sel;
    /// <summary>How many more matched than are shown.</summary>
    public int Hidden => _hidden;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    /// <summary>Looks at the box again: open, narrow, move or close the list.</summary>
    public void Follow()
    {
        if (_box.IsDisposed) return;
        var tok = _box.SelectionLength == 0 ? MentionPick.Typing(_box.Text, _box.SelectionStart) : null;
        if (tok == null || tok.Value.At == _shutAt) { Close_(); return; }
        _shutAt = -1;
        var all = MentionPick.Matches(tok.Value.Query, _first, _all, _me);
        if (all.Count == 0) { Close_(); return; }
        var same = _tok is { } was && was.At == tok.Value.At;
        _tok = tok;
        _items = all.Take(MaxShown).ToList();
        _hidden = all.Count - _items.Count;
        if (!same || _sel >= _items.Count) _sel = 0;
        Place();
        Invalidate();
    }

    private void Close_() { _tok = null; _items.Clear(); if (Visible) Hide(); }

    private void OnBoxKey(object? sender, KeyEventArgs e)
    {
        if (!Visible || _items.Count == 0) return;
        switch (e.KeyCode)
        {
            case Keys.Down: _sel = (_sel + 1) % _items.Count; Invalidate(); break;
            case Keys.Up: _sel = (_sel - 1 + _items.Count) % _items.Count; Invalidate(); break;
            case Keys.Enter: case Keys.Tab: Pick(_sel); break;
            case Keys.Escape: _shutAt = _tok?.At ?? -1; Close_(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    /// <summary>Puts the whole name in place of what was typed after the @.</summary>
    public void Pick(int i)
    {
        if (_tok is not { } tok || i < 0 || i >= _items.Count) return;
        var (text, caret) = MentionPick.Insert(_box.Text, tok.At, _box.SelectionStart, _items[i].Name);
        Close_();
        _box.Text = text;
        _box.SelectionStart = caret; _box.SelectionLength = 0;
        _box.Focus();
    }

    // ---- drawing ----

    private IEnumerable<(Rectangle R, int Item, string? Head)> Lines()
    {
        int y = _pad; bool? last = null; bool anyOn = _items.Any(c => c.OnTicket);
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].OnTicket != last)
            {
                last = _items[i].OnTicket;
                yield return (new Rectangle(_pad, y, Width - 2 * _pad, _head), -1,
                    _items[i].OnTicket ? "ON THIS TICKET" : anyOn ? "EVERYBODY ELSE" : "EVERYBODY");
                y += _head;
            }
            yield return (new Rectangle(_pad, y, Width - 2 * _pad, _row), i, null);
            y += _row;
        }
        if (_hidden > 0) yield return (new Rectangle(_pad, y, Width - 2 * _pad, _head), -2, $"{_hidden} more — keep typing the name");
    }

    private void Place()
    {
        int w = _items.Select(c => TextRenderer.MeasureText(c.Name, _t.BodyBold).Width).DefaultIfEmpty(0).Max();
        w = Math.Max(w, TextRenderer.MeasureText("EVERYBODY ELSE", _t.Small).Width);
        if (_hidden > 0) w = Math.Max(w, TextRenderer.MeasureText($"{_hidden} more — keep typing the name", _t.Small).Width);
        Width = w + 2 * _pad + _row;
        Height = Lines().Select(l => l.R.Bottom).DefaultIfEmpty(0).Max() + _pad;

        var at = _box.GetPositionFromCharIndex(_tok!.Value.At);   // the @ itself, always in the text
        var line = TextRenderer.MeasureText("Ag", _box.Font).Height;
        var p = _box.PointToScreen(new Point(at.X, at.Y + line + 2));
        var screen = Screen.FromControl(_box).WorkingArea;
        if (p.Y + Height > screen.Bottom) p.Y = _box.PointToScreen(new Point(0, at.Y)).Y - Height - 2;   // no room below: above the line
        p.X = Math.Max(screen.Left, Math.Min(p.X, screen.Right - Width));
        Location = p;
        if (!Visible)
        {
            var owner = _box.FindForm();
            if (owner != null && Owner != owner)
            {
                Owner = owner;
                // Moved, it would be left behind where the box was. Unhooked when this goes: the ticket
                // window makes a new box, and a new list, every time it redraws.
                EventHandler shut = (_, _) => Close_();
                owner.Move += shut; owner.Deactivate += shut;
                Disposed += (_, _) => { owner.Move -= shut; owner.Deactivate -= shut; };
            }
            Show();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var edge = new Pen(_t.FieldBorder)) g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
        foreach (var (r, i, head) in Lines())
        {
            if (head != null)
            {
                TextRenderer.DrawText(g, head, _t.Small, new Rectangle(r.X + _pad, r.Y, r.Width - _pad, r.Height), _t.Muted,
                    TextFormatFlags.Left | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                continue;
            }
            var on = i == _sel;
            if (on)
            {
                using var bg = new SolidBrush(_t.SurfaceAlt);
                using var path = Rounded(r, _row / 4);
                g.FillPath(bg, path);
            }
            TextRenderer.DrawText(g, _items[i].Name, _t.BodyBold, new Rectangle(r.X + _pad, r.Y, r.Width - _pad, r.Height),
                on ? _t.SurfaceAltText : _t.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath(); int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private int ItemAt(Point pt) => Lines().Where(l => l.Item >= 0 && l.R.Contains(pt)).Select(l => l.Item).DefaultIfEmpty(-1).First();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = ItemAt(e.Location);
        if (i >= 0 && i != _sel) { _sel = i; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var i = ItemAt(e.Location);
        if (i >= 0) Pick(i);
    }
}
