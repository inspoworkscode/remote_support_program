using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// A small chat window, so a message from a technician is answerable where it appears instead of
/// only inside the tray window nobody has open.
///
/// Unlike <see cref="SessionBanner"/> this one has to be typable, so it may be activated by a
/// click — it must simply not take focus when it appears on its own. Hence ShowWithoutActivation
/// without WS_EX_NOACTIVATE: the banner refuses focus outright, this defers it to the person.
///
/// **Stage 3.5.5: it can be moved, and it stays where it is put.** It used to be pinned to the
/// bottom-right corner and re-pinned there on every message, which is a real fault rather than a
/// preference: the corner of a Windows desktop is where icons are, so the window could sit on top
/// of the very icon the customer had just been asked to double-click — and there was nothing they
/// could do about it. Dragging it away worked until the next message arrived and put it back.
///
/// It sits above the session banner in its default place, which is why the caller sets
/// ReserveBottom rather than the window deciding for itself. Once the customer has moved it,
/// ReserveBottom stops applying: where they put it beats where we would have put it.
/// </summary>
public sealed class ChatPopup : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int Margin_ = 16;

    // Dragging a borderless window is done by telling Windows the mouse went down on a title bar
    // that does not exist. Doing it this way rather than by tracking the mouse ourselves is not
    // laziness: it is what makes the drag feel native — Aero snap, the screen edges, and the
    // Escape key cancelling a drag all come free, and none of them would if we moved the window
    // ourselves from MouseMove.
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcHitTest = 0x0084;
    private const int WmExitSizeMove = 0x0232;
    private const int HtClient = 1, HtCaption = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>The name this window's position is remembered under. One customer machine, one
    /// chat window, so it needs no qualifier.</summary>
    private const string PlacementKey = "client.chat";

    private readonly Theme _t;
    private readonly RichTextBox _log;
    private readonly TextBox _input;
    private readonly Label _title;

    /// <summary>
    /// True once the customer has put this window somewhere themselves — by dragging it now, or
    /// by having dragged it in an earlier session.
    ///
    /// It is the whole of the fix. <see cref="Present"/> repositions the window every time a
    /// message arrives, and without this flag that would drag it back to the corner out from
    /// under somebody who had just moved it, once per message.
    /// </summary>
    private bool _placedByPerson;

    /// <summary>The controls a drag may be started from. Read by tools/chatprobe, which is how
    /// "the reply box is deliberately NOT a drag handle" stays true: selecting text out of the
    /// log is something people do, and a log that moved the window instead would be maddening.</summary>
    internal IReadOnlyList<Control> DragHandles => _dragHandles;
    private readonly List<Control> _dragHandles = new();

    /// <summary>Raised when the person types a reply. The form does not send anything itself.</summary>
    public event Action<string>? OnSend;

    /// <summary>Pixels to leave clear at the bottom, so the session banner can sit below this.</summary>
    public int ReserveBottom { get; set; }

    public ChatPopup(Theme t, string companyName)
    {
        _t = t;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        // Half the original 340: narrow enough to sit out of the way, which is the point of it
        // being in the corner rather than a window you manage.
        Size = new Size(170, 320);
        BackColor = _t.Slate;
        DoubleBuffered = true;
        KeyPreview = true;

        // Laid out by docking rather than fixed coordinates: at this width the reply box and the
        // Send button no longer fit side by side, and hard-coded points would have to be redone
        // every time the size is tuned.
        Padding = new Padding(10, 8, 10, 10);

        var header = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Color.Transparent, Cursor = Cursors.SizeAll };
        _title = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false,
            Font = _t.BodyBold, ForeColor = Color.White, BackColor = Color.Transparent,
            Text = companyName, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            // The company name IS the title bar of this window. The move cursor is the only thing
            // saying so on a borderless popup with no chrome to hint at it.
            Cursor = Cursors.SizeAll
        };
        var close = new Label
        {
            Text = "✕", Dock = DockStyle.Right, Width = 22,
            Font = _t.BodyBold, ForeColor = Color.FromArgb(0x9A, 0xA8, 0xC0),
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand
        };
        close.Click += (_, _) => Hide();
        close.MouseEnter += (_, _) => close.ForeColor = Color.White;
        close.MouseLeave += (_, _) => close.ForeColor = Color.FromArgb(0x9A, 0xA8, 0xC0);
        header.Controls.Add(_title);
        header.Controls.Add(close);

        // Reply box and button stacked, so the whole width is available to each.
        var replyArea = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Color.Transparent };
        // Rounded, like every button in the suite (2026-10-06) - it was a flat Windows button, square.
        var send = new RoundButton(_t, BtnStyle.Accent)
        {
            Text = "Send", Dock = DockStyle.Bottom, Font = _t.BodyBold,
            Height = TextRenderer.MeasureText("Ag", _t.BodyBold).Height + 12
        };
        send.Click += (_, _) => Send();

        var inputWrap = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Color.Transparent };
        _input = new TextBox { Font = _t.Body, BackColor = _t.Surface, ForeColor = _t.Ink };
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Hide(); }
        };
        // In a rounded frame, as every field is; Windows' border was square and near-black.
        var inputFrame = new FieldFrame(_t, _input) { Dock = DockStyle.Fill };
        inputWrap.Controls.Add(inputFrame);

        // The reply box is one line, and a one-line TextBox IGNORES the height it is given: it
        // takes whatever its font needs, which at this theme's body font is 33 px rather than the
        // 26 it was docked into. So it hung 7 px over the Send button below it, and the top strip
        // of that button typed into the box instead of sending — a customer pressing Send and
        // nothing happening, on the window whose whole job is answering the technician.
        //
        // Found by tools/chatprobe measuring what overlaps what, 2026-09-18, while proving 3.5.5.
        // It is invisible in a screenshot, because a text box drawn over a button looks exactly
        // like a text box sitting above one.
        //
        // Asked of the control rather than hard-coded to 33: a tenant with a larger font gets a
        // taller box, and a second magic number would only mean the fault came back for them.
        int line = _input.PreferredHeight + inputFrame.Padding.Vertical;
        inputWrap.Height = line;
        replyArea.Height = line + 6 + send.Height;

        replyArea.Controls.Add(inputWrap);
        replyArea.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Color.Transparent });
        replyArea.Controls.Add(send);

        _log = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(0x17, 0x20, 0x36), ForeColor = _t.ConsoleText,
            Font = _t.Small, TabStop = false
        };

        // Fill is added first so the docked edges claim their space before it takes the rest.
        Controls.Add(_log);
        Controls.Add(replyArea);
        Controls.Add(header);

        // The header is the grab handle, and so is the window's own background — the border strip
        // the padding leaves around the controls, handled in WndProc. The log and the reply box
        // are deliberately NOT handles: a customer reading a technician's instruction will want
        // to select and copy it, and a log that moved the window instead of selecting would be
        // the kind of small maddening thing nobody reports and everybody remembers.
        DragBy(header);
        DragBy(_title);

        // Where they left it last time, if they ever moved it and if that place still exists.
        var remembered = WindowPlacement.Recall(PlacementKey);
        if (remembered is { } r) { Location = r.Location; _placedByPerson = true; }

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    /// <summary>Lets a drag on this control move the whole window, as though it were a title bar.</summary>
    private void DragBy(Control c)
    {
        _dragHandles.Add(c);
        c.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // Hand the drag to Windows. ReleaseCapture first, or the mouse is still captured by
            // the child control and the move loop never starts — the window simply does not move,
            // which is indistinguishable from the handler not being wired up at all.
            try { ReleaseCapture(); SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero); } catch { }
        };
    }

    /// <summary>Appear without stealing focus; the person may be typing somewhere else.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Tool window only: out of the taskbar and Alt-Tab, but still focusable when clicked,
            // which a chat window must be.
            cp.ExStyle |= WsExToolWindow;
            return cp;
        }
    }

    /// <summary>
    /// Drags and moves are reported here when they FINISH, which is the only moment worth
    /// writing a file at: a drag raises a move for every pixel the mouse travels.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        // The window's own background is a title bar. This covers the strip of padding around
        // the controls — there is not much of it, but it means a grab that lands just outside
        // the header still moves the window rather than doing nothing.
        if (m.Msg == WmNcHitTest)
        {
            base.WndProc(ref m);
            if (m.Result == (IntPtr)HtClient) m.Result = (IntPtr)HtCaption;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == WmExitSizeMove) RememberWhereItIs();
    }

    private void RememberWhereItIs()
    {
        _placedByPerson = true;
        WindowPlacement.Remember(PlacementKey, Bounds);
    }

    /// <summary>
    /// The screens changed — a monitor unplugged, a resolution changed, a laptop undocked.
    ///
    /// If the customer never moved this window it goes back to the corner of the new layout. If
    /// they did, their choice is kept and only nudged back into view if it now falls outside it:
    /// throwing away where somebody put a window because they plugged in a projector is the same
    /// fault this item exists to fix, arriving by a different route.
    /// </summary>
    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !Visible) return;
        if (!_placedByPerson) { Reposition(); return; }
        if (WindowPlacement.IsReachable(Bounds)) return;
        Bounds = WindowPlacement.ClampToScreen(Bounds);
        WindowPlacement.Remember(PlacementKey, Bounds);
    }

    private void Reposition()
    {
        // Where the customer put it wins. Present() calls this on every incoming message, and
        // before 3.5.5 that was the bug: a window dragged out of the way came straight back to
        // the corner the moment the technician said anything.
        if (_placedByPerson) return;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        Location = new Point(wa.Right - Width - Margin_, wa.Bottom - Height - Margin_ - ReserveBottom);
    }

    private void Send()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Clear();
        OnSend?.Invoke(text);
    }

    /// <summary>A message from the technician: show the window and bring it to the front.</summary>
    public void Incoming(ChatMsg m)
    {
        Append(m.From, m.Text, _t.Accent, m.Ts.ToLocalTime());
        Present();
    }

    /// <summary>Echo of what the person here sent, so the popup reads as a conversation.</summary>
    public void Outgoing(string text)
    {
        Append("You", text, Color.FromArgb(0x6E, 0xE7, 0xB7), DateTimeOffset.Now);
        Present();
    }

    /// <summary>Session start and end, so the popup carries the same story as the tray window.</summary>
    public void System_(string text)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionColor = Color.FromArgb(0x9A, 0xA8, 0xC0);
        _log.SelectionFont = new Font("Segoe UI", 8.5f, FontStyle.Italic);
        _log.AppendText($"— {text}\n");
        _log.ScrollToCaret();
    }

    private void Append(string who, string text, Color colour, DateTimeOffset ts)
    {
        _log.SelectionStart = _log.TextLength;
        _log.SelectionColor = Color.FromArgb(0x7C, 0x8A, 0xA6);
        _log.SelectionFont = _t.Small;
        _log.AppendText($"{ts:HH:mm}  ");
        _log.SelectionColor = colour; _log.SelectionFont = _t.BodyBold;
        _log.AppendText($"{who}: ");
        _log.SelectionColor = _t.ConsoleText; _log.SelectionFont = _t.Body;
        _log.AppendText(text + "\n");
        _log.ScrollToCaret();
    }

    private void Present()
    {
        Reposition();
        if (!Visible) Show();
        // A full-screen app started after us can otherwise cover it.
        try { TopMost = false; TopMost = true; } catch { }
    }

    /// <summary>
    /// Opens the chat window because the person asked for it — the tray menu's "Chat with a
    /// Technician" (stage 3.5.6).
    ///
    /// This one DOES take the focus, unlike every other way this window appears. Everywhere else
    /// it arrives unbidden and must not swallow what somebody is typing; here they have just
    /// clicked a menu item that says chat, so the caret belongs in the reply box and making them
    /// click again to get it would be the kind of small rudeness this stage exists to remove.
    /// </summary>
    public void Open()
    {
        Present();
        FocusReply();
    }

    /// <summary>Put the caret in the reply box — only ever in response to the person clicking.</summary>
    public void FocusReply() { try { Activate(); _input.Focus(); } catch { } }

    public void HideNow() { if (Visible) Hide(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Color.FromArgb(0x33, 0x41, 0x5A));
        using var bg = new SolidBrush(_t.Slate);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Rounded(r, 12);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
        base.OnPaint(e);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        base.Dispose(disposing);
    }
}
