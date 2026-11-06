using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Office chat in the console (stage 3.8.1, 2026-09-30) — people at the office, never a customer.
/// Clint: "when you chat and do either the @ or select the person it pops up a chat window". The
/// portal's is the same conversation drawn by <c>wwwroot/chat.js</c>; the server
/// (<c>StaffChatService</c>) decides everything, and this draws what the hub sends.
///
/// <para>Everyone and each person on the left, with what is unread; the conversation on the right. A
/// message to this person — or an Everyone message that names them with @ — brings the window up
/// WITHOUT taking the keyboard (they may be typing on a customer's machine) and flashes the taskbar;
/// the rest of Everyone only counts. One window, created once and hidden on close, so its
/// conversations survive being closed.</para>
/// </summary>
public sealed class StaffChatForm : Form
{
    private readonly TechLink _link;
    private readonly Theme _t;
    private readonly ListBox _list;
    private readonly InputBox _find;
    private readonly Label _title;
    private readonly RichTextBox _log;
    private readonly InputBox _input;
    private readonly Label _error;
    private readonly Dictionary<string, StaffChatThreadInfo> _threads = new();
    private List<string> _people = new();
    /// <summary>The open conversation: a person's name, or "" for Everyone.</summary>
    private string _with = "";
    private long _lastShown;
    private bool _popping;

    /// <summary>What is unread in all conversations together — the dashboard's button shows it.</summary>
    public event Action<int>? UnreadChanged;

    public StaffChatForm(TechLink link, Theme t)
    {
        _link = link; _t = t;
        Text = "Office chat";
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(640, 460);
        MinimumSize = new Size(480, 320);
        BackColor = t.Mist;
        Font = t.Body;
        ShowInTaskbar = true;
        Shown += (_, _) => NativeDark.Apply(this, t);

        int line = TextRenderer.MeasureText("Ag", t.Body).Height;
        int fieldH = line + 20;

        // ---- left: find, then Everyone and the people ----
        var left = new Panel { Dock = DockStyle.Left, Width = 210, BackColor = Color.Transparent, Padding = new Padding(12, 12, 6, 12) };
        _find = new InputBox(t, height: fieldH) { Dock = DockStyle.Top };
        _find.Box.PlaceholderText = "Type @ or a name";
        _find.Box.TextChanged += (_, _) => DrawList();
        _find.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || _list.Items.Count == 0) return;
            e.SuppressKeyPress = true;
            Open((string)_list.Items[0]!);
            _input.Box.Focus();
        };
        _list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = t.Surface, ForeColor = t.Ink, Font = t.Body,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = line * 2 + 10, IntegralHeight = false
        };
        _list.DrawItem += DrawRow;
        _list.Click += (_, _) => { if (_list.SelectedItem is string w) Open(w); };
        left.Controls.Add(_list);
        left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.Transparent });
        left.Controls.Add(_find);

        // ---- right: the conversation ----
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(6, 12, 12, 12) };
        var card = new Card(t, 12) { Dock = DockStyle.Fill };
        _title = new Label { Dock = DockStyle.Top, Height = TextRenderer.MeasureText("Ag", t.H3).Height + 10, Font = t.H3, ForeColor = t.Ink, BackColor = Color.Transparent, UseMnemonic = false };
        _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = t.Surface, BorderStyle = BorderStyle.None, Font = t.Body, ForeColor = t.Ink };
        _error = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 0, Font = t.Small, ForeColor = t.BadText, BackColor = Color.Transparent, UseMnemonic = false };
        var row = new Panel { Dock = DockStyle.Bottom, Height = fieldH + 8, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0) };
        _input = new InputBox(t, height: fieldH) { Dock = DockStyle.Fill };
        _input.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); } };
        var sendW = TextRenderer.MeasureText("Send", t.ButtonFont).Width + 36;
        var send = new RoundButton(t, BtnStyle.Accent) { Text = "Send", Dock = DockStyle.Right, Width = sendW, Height = fieldH };
        send.Click += (_, _) => Send();
        row.Controls.Add(_input);
        row.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        row.Controls.Add(send);
        card.Controls.Add(_log);
        card.Controls.Add(_title);
        card.Controls.Add(_error);
        card.Controls.Add(row);
        right.Controls.Add(card);

        Controls.Add(right);
        Controls.Add(left);

        _link.OnStaffChatOverview += o => Inv(() => TakeOverview(o));
        _link.OnStaffChatHistory += h => Inv(() => TakeHistory(h));
        _link.OnStaffChat += m => Inv(() => TakeMessage(m));

        // Closing hides it: the conversations and the live feed stay, and the next open is instant.
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Activated += (_, _) => MarkRead();
        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) WindowPlacement.Remember("staffchat", Bounds); };
        Load += (_, _) =>
        {
            if (WindowPlacement.Recall("staffchat") is { } r) Bounds = WindowPlacement.ClampToScreen(r);
            else { var wa = Screen.PrimaryScreen!.WorkingArea; Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24); }
        };
        _title.Text = "Everyone";
        // Made from sign-in and kept hidden, so it can pop up: its handle is made NOW, on the UI
        // thread, so every message from the link can be marshalled to it (Inv) and none ever touches
        // it from the socket's thread.
        _ = Handle;
        _link.RequestStaffChatOverview();
    }

    /// <summary>Brings it up on a conversation, asked for — the keyboard comes with it.</summary>
    public void ShowFor(string? with = null)
    {
        if (with != null) Open(with);
        else if (_log.TextLength == 0) Open(_with);
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _input.Box.Focus();
    }

    /// <summary>A popped window must not take the keyboard: see the class summary.</summary>
    protected override bool ShowWithoutActivation => _popping;

    private void Inv(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

    private static string Label(string with) => with.Length == 0 ? "Everyone" : with;

    private void TakeOverview(StaffChatOverviewMsg o)
    {
        _people = o.People;
        _threads.Clear();
        foreach (var t in o.Threads) _threads[t.With] = t;
        DrawList();
        UnreadChanged?.Invoke(o.TotalUnread);
    }

    private void DrawList()
    {
        var q = _find.Value.Trim().TrimStart('@');
        long LastId(string w) => _threads.TryGetValue(w, out var t) && t.Last != null ? t.Last.Id : 0;
        var names = new List<string> { "" };
        names.AddRange(_people.OrderByDescending(LastId).ThenBy(p => p));
        var shown = names.Where(n => q.Length == 0 || Label(n).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var n in shown) _list.Items.Add(n);
        _list.EndUpdate();
        _list.Invalidate();
    }

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var w = (string)_list.Items[e.Index]!;
        bool current = w == _with;
        using var bg = new SolidBrush(current ? _t.SurfaceAlt : _t.Surface);
        e.Graphics.FillRectangle(bg, e.Bounds);
        _threads.TryGetValue(w, out var th);
        int unread = th?.Unread ?? 0;
        // The count is drawn on its own at the right, so a long name is what gets shortened — never the
        // count. (It was one string at first, and "Sam Supervisor (1)" drew as "Sam Supervisor…".)
        int countW = 0;
        if (unread > 0)
        {
            var count = unread > 99 ? "99+" : unread.ToString();
            var size = TextRenderer.MeasureText(count, _t.Small);
            countW = size.Width + 12;
            var pill = new Rectangle(e.Bounds.Right - countW - 8, e.Bounds.Y + 5, countW, size.Height + 2);
            using var pb = new SolidBrush(_t.Grape);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                int r = pill.Height;
                path.AddArc(pill.X, pill.Y, r, r, 90, 180);
                path.AddArc(pill.Right - r, pill.Y, r, r, 270, 180);
                path.CloseFigure();
                e.Graphics.FillPath(pb, path);
            }
            TextRenderer.DrawText(e.Graphics, count, _t.Small, pill, Theme.On(_t.Grape),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        var nameRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, e.Bounds.Width - 16 - (countW > 0 ? countW + 6 : 0), e.Bounds.Height / 2);
        TextRenderer.DrawText(e.Graphics, Label(w), unread > 0 ? _t.BodyBold : _t.Body, nameRect,
            unread > 0 ? _t.Grape : _t.Ink, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (th?.Last != null)
        {
            var last = (th.Last.From == _link.DisplayName ? "You: " : w.Length == 0 ? th.Last.From + ": " : "") + th.Last.Text.Replace('\n', ' ');
            var lastRect = new Rectangle(nameRect.X, e.Bounds.Y + e.Bounds.Height / 2, e.Bounds.Width - 16, e.Bounds.Height / 2 - 4);
            TextRenderer.DrawText(e.Graphics, last, _t.Small, lastRect, _t.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private void Open(string with)
    {
        _with = with;
        _title.Text = Label(with);
        _input.Box.PlaceholderText = with.Length == 0 ? "Message everybody — @ and a name to ask someone" : $"Message {with}";
        _log.Clear();
        _lastShown = 0;
        SetError("");
        _list.Invalidate();
        _link.RequestStaffChatHistory(with);
    }

    private void TakeHistory(StaffChatHistoryMsg h)
    {
        if (h.With != _with) return;   // an answer for a conversation that has since been left
        _log.Clear(); _lastShown = 0;
        foreach (var m in h.Messages) Append(m);
        MarkRead();
    }

    private void TakeMessage(StaffChatMsg m)
    {
        if (m.Error.Length > 0) { SetError(m.Error); return; }
        if (m.Message is not { } msg) return;
        var me = _link.DisplayName;
        string thread = msg.To.Length == 0 ? "" : msg.From == me ? msg.To : msg.From;
        bool forMe = msg.From != me && (msg.To == me || m.MentionsMe);
        if (thread == _with) { Append(msg); if (msg.From == me) SetError(""); }
        // Every message somebody else sends that reaches me dings, Everyone's too (2026-10-07).
        if (msg.From != me) Sounds.Ding();
        if (!forMe) { if (thread == _with) MarkRead(); return; }

        // It pops up. Onto that conversation only if nothing else is being read or typed in.
        bool busy = Visible && _input.Value.Trim().Length > 0 && thread != _with;
        if (!Visible || (!busy && thread != _with && !ContainsFocus)) Open(thread);
        if (!Visible)
        {
            _popping = true;
            try { Show(); } finally { _popping = false; }
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!ContainsFocus) Flash();
        else MarkRead();
    }

    private void Append(StaffChatInfo m)
    {
        if (m.Id <= _lastShown) return;
        _lastShown = m.Id;
        bool mine = m.From == _link.DisplayName;
        _log.SelectionStart = _log.TextLength;
        _log.SelectionColor = _t.Muted; _log.SelectionFont = _t.Small;
        _log.AppendText($"{m.SentUtc.ToLocalTime():HH:mm} ");
        _log.SelectionColor = mine ? _t.Grape : _t.Ink; _log.SelectionFont = _t.BodyBold;
        _log.AppendText($"{(mine ? "You" : m.From)}: ");
        _log.SelectionFont = _t.Body; _log.SelectionColor = _t.Ink;
        _log.AppendText(m.Text + "\n");
        _log.ScrollToCaret();
    }

    private void Send()
    {
        var text = _input.Value.Trim();
        if (text.Length == 0) return;
        _link.SendStaffChat(_with, text);
        _input.Value = "";
    }

    private void MarkRead()
    {
        if (!Visible || !ContainsFocus || _lastShown == 0) return;
        if (_threads.TryGetValue(_with, out var t) && t.Unread == 0) return;
        _link.MarkStaffChatRead(_with, _lastShown);
    }

    private void SetError(string text)
    {
        _error.Text = text;
        _error.Height = text.Length == 0 ? 0 : TextRenderer.MeasureText("Ag", _t.Small).Height + 6;
    }

    private void Flash()
    {
        try
        {
            var fw = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = Handle, dwFlags = 3 | 12, uCount = 0, dwTimeout = 0 };
            FlashWindowEx(ref fw);   // FLASHW_ALL | FLASHW_TIMERNOFG: until they look at it
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
