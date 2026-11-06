using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Website chat in the console (stage 3.8.3, 2026-09-30): visitors to the company's own website,
/// through the bubble it pastes onto its site. Clint: it "goes through to every logged in technician
/// including supervisors and office administrators ... a great way for someone to answer questions
/// immediately and guide them to the support page". The portal draws the same chats in its Chat
/// corner (<c>wwwroot/chat.js</c>); the server (<c>WebChatService</c>) decides everything.
///
/// <para>The chats going on now on the left; the conversation on the right, with one-click buttons
/// that send the app's download page or browser support. <b>The first person to reply takes it</b>
/// (Clint's answer): everybody else sees who is answering. A new chat brings the window up for
/// everybody WITHOUT taking the keyboard (they may be typing on a customer's machine) and flashes the
/// taskbar; after that a visitor's line brings it up only for whoever is answering — or for everybody
/// while nobody is. One window, made at sign-in and hidden on close, like Office chat.</para>
///
/// <para><b>Hand over…</b> (2026-10-15) gives the chat to somebody else signed in — the person answering, a
/// supervisor or the owner, or anybody while nobody has answered. It comes up and dings for them, the visitor
/// is told by a note line, and the visitor's lines ding for them from then on instead.</para>
/// </summary>
public sealed class WebChatForm : Form
{
    private readonly TechLink _link;
    private readonly Theme _t;
    private readonly Font _smallBold;
    private readonly ListBox _list;
    private readonly Label _title, _sub, _state, _empty;
    private readonly RichTextBox _log;
    private readonly InputBox _input;
    private readonly Label _error;
    private readonly RoundButton _send, _appBtn, _helpBtn, _handBtn, _endBtn;
    private readonly Panel _tools, _conversation;
    /// <summary>Who to hand the open chat to: the people signed in now, asked of the server when Hand over is pressed.</summary>
    private readonly ContextMenuStrip _handMenu;
    private readonly Dictionary<int, WebChatInfo> _chats = new();
    private string _appLink = "", _helpLink = "";
    /// <summary>A supervisor or the owner: may hand over a chat a colleague is answering (the server decides; this hides a refusal).</summary>
    private bool _mayHandOverAny;
    private int _open;
    private long _lastShown;
    private bool _popping;

    /// <summary>Website chat is switched on for the company. The dashboard's button shows only then.</summary>
    public bool Enabled_ { get; private set; }
    /// <summary>Somebody else handed this person a chat (2026-10-15): the dashboard shows a tray balloon.</summary>
    public event Action<WebChatInfo>? HandedToMe;
    /// <summary>How many visitors are waiting for anybody to answer, and whether it is on at all.</summary>
    public event Action<int, bool>? WaitingChanged;

    public WebChatForm(TechLink link, Theme t)
    {
        _link = link; _t = t;
        _smallBold = new Font(t.Small, FontStyle.Bold);
        Text = "Website chat";
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(720, 500);
        MinimumSize = new Size(540, 360);
        BackColor = t.Mist;
        Font = t.Body;
        ShowInTaskbar = true;
        Shown += (_, _) => NativeDark.Apply(this, t);

        int line = TextRenderer.MeasureText("Ag", t.Body).Height;
        int small = TextRenderer.MeasureText("Ag", t.Small).Height;
        int fieldH = line + 20;
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 12;

        // ---- left: the visitors ----
        var left = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Color.Transparent, Padding = new Padding(12, 12, 6, 12) };
        _list = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = t.Surface, ForeColor = t.Ink, Font = t.Body,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = line * 2 + 12, IntegralHeight = false
        };
        _list.DrawItem += DrawRow;
        _list.Click += (_, _) => { if (_list.SelectedItem is int id) Open(id); };
        left.Controls.Add(_list);

        // ---- right: the conversation ----
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(6, 12, 12, 12) };
        var card = new Card(t, 12) { Dock = DockStyle.Fill };
        _conversation = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _title = new Label { Dock = DockStyle.Top, Height = TextRenderer.MeasureText("Ag", t.H3).Height + 6, Font = t.H3, ForeColor = t.Ink, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true };
        _state = new Label { Dock = DockStyle.Top, Height = small + 6, Font = _smallBold, ForeColor = t.Grape, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true };
        _sub = new Label { Dock = DockStyle.Top, Height = small + 8, Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true };
        _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = t.Surface, BorderStyle = BorderStyle.None, Font = t.Body, ForeColor = t.Ink, DetectUrls = true };
        _log.LinkClicked += (_, e) => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.LinkText!) { UseShellExecute = true }); } catch { } };
        _error = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = 0, Font = t.Small, ForeColor = t.BadText, BackColor = Color.Transparent, UseMnemonic = false };

        // One click hands them where to go next — Clint: "guide them to the support page". Each
        // button as wide as its own words (§30).
        _tools = new Panel { Dock = DockStyle.Bottom, Height = btnH + 8, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0) };
        RoundButton Tool(string text, BtnStyle style)
        {
            var b = new RoundButton(t, style) { Text = text, Dock = DockStyle.Left, Width = TextRenderer.MeasureText(text, t.ButtonFont).Width + 32, Height = btnH };
            return b;
        }
        _appBtn = Tool("App link", BtnStyle.Neutral);
        _appBtn.Click += (_, _) => Say($"You can download our support app here: {_appLink}");
        _helpBtn = Tool("Browser support link", BtnStyle.Neutral);
        _helpBtn.Click += (_, _) => Say($"We can look at your screen from your browser here: {_helpLink}");
        _endBtn = Tool("End chat", BtnStyle.Neutral);
        _endBtn.Dock = DockStyle.Right;
        _endBtn.Click += (_, _) => EndChat();
        // Hand over (2026-10-15): asks the server who is signed in now, then offers them as a menu.
        _handBtn = Tool("Hand over…", BtnStyle.Neutral);
        _handBtn.Dock = DockStyle.Right;
        _handBtn.Click += (_, _) => { if (_open != 0) { SetError(""); _link.RequestWebChatPeople(_open); } };
        _handMenu = new ContextMenuStrip { Font = t.Body }.Themed(t);
        _link.OnWebChatPeople += m => Inv(() => OfferPeople(m));
        // Docked in reverse: the last added is outermost, so the app link ends up first on the left,
        // and End chat last on the right with Hand over beside it.
        _tools.Controls.Add(_handBtn);
        _tools.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        _tools.Controls.Add(_endBtn);
        _tools.Controls.Add(_helpBtn);
        _tools.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        _tools.Controls.Add(_appBtn);
        var tips = new ToolTip();
        tips.SetToolTip(_appBtn, "Send them the link to download our support app");
        tips.SetToolTip(_helpBtn, "Send them the link to let us see their screen from their browser");
        tips.SetToolTip(_endBtn, "End the chat — they are told it has ended");
        tips.SetToolTip(_handBtn, "Give this chat to somebody else who is signed in — they are brought it, and the visitor is told");

        var row = new Panel { Dock = DockStyle.Bottom, Height = fieldH + 8, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0) };
        _input = new InputBox(t, height: fieldH) { Dock = DockStyle.Fill };
        _input.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; Send(); } };
        _send = new RoundButton(t, BtnStyle.Accent) { Text = "Send", Dock = DockStyle.Right, Width = TextRenderer.MeasureText("Send", t.ButtonFont).Width + 36, Height = fieldH };
        _send.Click += (_, _) => Send();
        row.Controls.Add(_input);
        row.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        row.Controls.Add(_send);

        _conversation.Controls.Add(_log);
        _conversation.Controls.Add(_sub);
        _conversation.Controls.Add(_state);
        _conversation.Controls.Add(_title);
        _conversation.Controls.Add(_error);
        _conversation.Controls.Add(_tools);
        _conversation.Controls.Add(row);
        _empty = new Label
        {
            Dock = DockStyle.Fill, Text = "Nobody from your website is chatting right now.\nWhen somebody is, they appear here and this window comes up.",
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = t.Muted, BackColor = Color.Transparent, Font = t.Body, UseMnemonic = false
        };
        card.Controls.Add(_conversation);
        card.Controls.Add(_empty);
        right.Controls.Add(card);

        // As narrow as the four buttons on one row allow, measured — not a number that fits one font (§30).
        int toolsW = _appBtn.Width + 8 + _helpBtn.Width + 8 + _handBtn.Width + 8 + _endBtn.Width;
        MinimumSize = new Size(left.Width + toolsW + 80, 360);
        if (ClientSize.Width < MinimumSize.Width) ClientSize = new Size(MinimumSize.Width, ClientSize.Height);
        Controls.Add(right);
        Controls.Add(left);

        _link.OnWebChatList += m => Inv(() => TakeList(m));
        _link.OnWebChatUpdate += m => Inv(() => { if (m.Chat != null) TakeChat(m.Chat); });
        _link.OnWebChatLine += m => Inv(() => TakeLine(m));
        _link.OnWebChatHistory += m => Inv(() => TakeHistory(m));

        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        ResizeEnd += (_, _) => { if (WindowState == FormWindowState.Normal) WindowPlacement.Remember("webchat", Bounds); };
        Load += (_, _) =>
        {
            if (WindowPlacement.Recall("webchat") is { } r) Bounds = WindowPlacement.ClampToScreen(r);
            else { var wa = Screen.PrimaryScreen!.WorkingArea; Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24); }
        };
        Draw();
        // Made at sign-in and kept hidden, so it can pop up — its handle made NOW, on the UI thread.
        _ = Handle;
    }

    /// <summary>Brings it up, asked for — the keyboard comes with it.</summary>
    public void ShowFor(int? chatId = null)
    {
        if (chatId is int id && _chats.ContainsKey(id)) Open(id);
        else if (_open == 0 && _chats.Count > 0) Open(_chats.Keys.Min());
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (_input.Box.Enabled) _input.Box.Focus();
    }

    /// <summary>A popped window must not take the keyboard: see the class summary.</summary>
    protected override bool ShowWithoutActivation => _popping;

    /// <summary>How many visitors are waiting for anybody at all.</summary>
    public int Waiting => _chats.Values.Count(c => !c.Ended && c.AnsweredBy.Length == 0);

    private void Inv(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

    private string Me => _link.DisplayName;

    private string StateText(WebChatInfo c) =>
        c.Ended ? (c.EndedBy == "visitor" ? "The visitor ended the chat." : c.EndedBy == "idle" ? "Ended — the visitor had gone." : $"Ended by {(c.EndedBy == Me ? "you" : c.EndedBy)}.")
        : c.AnsweredBy.Length == 0 ? "Waiting for somebody to answer"
        : (c.AnsweredBy == Me ? "You are answering" : $"{c.AnsweredBy} is answering") + (c.VisitorHere ? "" : " · they have left the page");

    private void TakeList(WebChatListMsg m)
    {
        Enabled_ = m.Enabled; _appLink = m.AppLink; _helpLink = m.HelpLink; _mayHandOverAny = m.MayHandOverAny;
        // The server's list is the truth; only an ENDED chat that is open stays, so its transcript can be read.
        var keep = _open != 0 && _chats.TryGetValue(_open, out var cur) && cur.Ended ? cur : null;
        _chats.Clear();
        foreach (var c in m.Chats) _chats[c.Id] = c;
        if (keep != null && !_chats.ContainsKey(keep.Id)) _chats[keep.Id] = keep;
        if (_open != 0 && !_chats.ContainsKey(_open)) _open = 0;
        Draw();
        // Somebody was already waiting when this person signed in: bring it up, as if it had just arrived.
        if (Waiting > 0) Pop(_chats.Values.First(c => !c.Ended && c.AnsweredBy.Length == 0).Id);
        WaitingChanged?.Invoke(Waiting, Enabled_);
    }

    private void TakeChat(WebChatInfo c)
    {
        bool isNew = !_chats.ContainsKey(c.Id);
        if (isNew && c.Ended) return;
        _chats[c.Id] = c;
        if (isNew) Enabled_ = true;
        if (c.Ended && c.Id != _open) _chats.Remove(c.Id);   // gone from the list; the open one keeps its transcript
        if (c.Ended && c.Id == _open) SetError("");   // the state says it; a refusal saying it again is noise
        Draw();
        if (isNew && c.AnsweredBy.Length == 0) Pop(c.Id);
        // Handed to me by somebody else (2026-10-15): it comes up and dings, and from now on the visitor's lines
        // pop and ding for me — TakeLine keys on AnsweredBy, so they stop for whoever had it.
        if (!c.Ended && c.HandedOverBy.Length > 0 && c.HandedOverBy != Me && c.AnsweredBy == Me)
        {
            Pop(c.Id);
            Sounds.Ding();
            HandedToMe?.Invoke(c);
        }
        WaitingChanged?.Invoke(Waiting, Enabled_);
    }

    /// <summary>May this person hand over this chat? The server's rule, mirrored only to hide a refusal.</summary>
    private bool MayHandOver(WebChatInfo c) => !c.Ended && (c.AnsweredBy.Length == 0 || c.AnsweredBy == Me || _mayHandOverAny);

    /// <summary>The server's answer to Hand over: everybody signed in except whoever has it now, as a menu.</summary>
    private void OfferPeople(WebChatPeopleMsg m)
    {
        if (m.ChatId != _open || !_chats.TryGetValue(_open, out var c) || !MayHandOver(c)) return;
        var names = m.Names.Where(n => n != c.AnsweredBy).ToList();
        if (names.Count == 0) { SetError("Nobody else is signed in right now."); return; }
        _handMenu.Items.Clear();
        int chatId = c.Id;
        foreach (var n in names)
        {
            var item = new ToolStripMenuItem(n == Me ? $"{n} (you)" : n) { Tag = n };
            item.Click += (_, _) => { SetError(""); _link.HandOverWebChat(chatId, n); };
            _handMenu.Items.Add(item);
        }
        if (_handBtn.Visible) _handMenu.Show(_handBtn, new Point(0, _handBtn.Height));
    }

    private void TakeLine(WebChatLineMsg m)
    {
        if (m.Error.Length > 0) { if (m.ChatId == _open) SetError(m.Error); return; }
        if (m.Line is not { } l) return;
        if (_chats.TryGetValue(l.ChatId, out var c)) c.LastText = l.Text;
        if (l.ChatId == _open) { Append(l); if (!l.FromVisitor && l.Name == Me) SetError(""); }
        _list.Invalidate();
        // A visitor's line brings it up for whoever is answering — or everybody while nobody is.
        if (l.FromVisitor && c != null && !c.Ended && (c.AnsweredBy.Length == 0 || c.AnsweredBy == Me)) Pop(l.ChatId);
        // The visitor I am answering dings (2026-10-07). One nobody has answered is ringing already.
        if (l.FromVisitor && c != null && !c.Ended && c.AnsweredBy == Me) Sounds.Ding();
    }

    private void TakeHistory(WebChatHistoryMsg h)
    {
        if (h.ChatId != _open) return;
        _log.Clear(); _lastShown = 0;
        foreach (var l in h.Lines) Append(l);
    }

    /// <summary>Comes up without taking the keyboard, onto this chat unless another is being typed in.</summary>
    private void Pop(int chatId)
    {
        bool busy = Visible && _input.Value.Trim().Length > 0 && _open != chatId;
        if (!Visible || _open == 0 || (!busy && _open != chatId && !ContainsFocus)) Open(chatId);
        if (!Visible) { _popping = true; try { Show(); } finally { _popping = false; } }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!ContainsFocus) Flash();
    }

    private void Draw()
    {
        var ids = _chats.Keys.OrderBy(i => i).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var id in ids) _list.Items.Add(id);
        _list.EndUpdate();
        _list.Invalidate();
        bool any = _open != 0 && _chats.ContainsKey(_open);
        _conversation.Visible = any;
        _empty.Visible = !any;
        if (any) DrawHeader();
    }

    private void DrawHeader()
    {
        if (!_chats.TryGetValue(_open, out var c)) return;
        _title.Text = c.Name;
        _state.Text = StateText(c);
        _state.ForeColor = c.Ended ? _t.Muted : c.AnsweredBy.Length == 0 ? _t.WarnText : _t.Grape;
        var bits = new[] { c.Email, c.Phone }.Where(s => s.Length > 0).ToList();
        if (c.Page.Length > 0) bits.Add("on " + System.Text.RegularExpressions.Regex.Replace(c.Page, "^https?://", ""));
        _sub.Text = string.Join("  ·  ", bits);
        _input.Box.Enabled = _send.Enabled = !c.Ended;
        _input.Box.PlaceholderText = c.Ended ? "This chat has ended." : $"Reply to {c.Name}";
        _tools.Visible = !c.Ended;
        _helpBtn.Visible = _helpLink.Length > 0;
        _appBtn.Visible = _appLink.Length > 0;
        _handBtn.Visible = MayHandOver(c);
    }

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not int id || !_chats.TryGetValue(id, out var c)) return;
        bool current = id == _open;
        using var bg = new SolidBrush(current ? _t.SurfaceAlt : _t.Surface);
        e.Graphics.FillRectangle(bg, e.Bounds);
        bool waiting = !c.Ended && c.AnsweredBy.Length == 0;
        var half = e.Bounds.Height / 2;
        const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        // The name has the whole first line — it was cut to "Vic Vi…" beside the state (the probe's
        // picture). The state starts the second line, in amber while nobody has answered.
        TextRenderer.DrawText(e.Graphics, c.Name, waiting ? _t.BodyBold : _t.Body,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, e.Bounds.Width - 16, half), waiting ? _t.Grape : _t.Ink, left);
        var state = c.Ended ? "ended" : waiting ? "waiting" : c.AnsweredBy == Me ? "you" : c.AnsweredBy;
        var stateFont = waiting ? _smallBold : _t.Small;
        var line2 = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + half, e.Bounds.Width - 16, half - 4);
        TextRenderer.DrawText(e.Graphics, state, stateFont, line2, waiting ? _t.WarnText : _t.Muted, left);
        int used = Math.Min(TextRenderer.MeasureText(e.Graphics, state + " ", stateFont, line2.Size, left).Width, line2.Width);
        TextRenderer.DrawText(e.Graphics, "· " + c.LastText.Replace('\n', ' '), _t.Small,
            new Rectangle(line2.X + used, line2.Y, line2.Width - used, line2.Height), _t.Muted, left);
    }

    private void Open(int chatId)
    {
        if (_open != chatId && _open != 0 && _chats.TryGetValue(_open, out var was) && was.Ended) _chats.Remove(_open);
        _open = chatId;
        _log.Clear();
        _lastShown = 0;
        SetError("");
        Draw();
        _link.RequestWebChatHistory(chatId);
    }

    private void Append(WebChatLineInfo l)
    {
        if (l.Id <= _lastShown) return;
        _lastShown = l.Id;
        _log.SelectionStart = _log.TextLength;
        if (l.Note)
        {
            // A hand-over's line (2026-10-15): nobody said it, so no name — a note in the margin's colour.
            _log.SelectionColor = _t.Muted; _log.SelectionFont = _t.Small;
            _log.AppendText($"{l.SentUtc.ToLocalTime():HH:mm} {l.Text}\n");
            _log.ScrollToCaret();
            return;
        }
        bool mine = !l.FromVisitor && l.Name == Me;
        _log.SelectionStart = _log.TextLength;
        _log.SelectionColor = _t.Muted; _log.SelectionFont = _t.Small;
        _log.AppendText($"{l.SentUtc.ToLocalTime():HH:mm} ");
        _log.SelectionColor = mine ? _t.Grape : l.FromVisitor ? _t.Ink : _t.Blue; _log.SelectionFont = _t.BodyBold;
        _log.AppendText($"{(mine ? "You" : l.Name)}: ");
        _log.SelectionFont = _t.Body; _log.SelectionColor = _t.Ink;
        _log.AppendText(l.Text + "\n");
        _log.ScrollToCaret();
    }

    private void Send()
    {
        var text = _input.Value.Trim();
        if (text.Length == 0 || _open == 0) return;
        Say(text);
        _input.Value = "";
    }

    private void Say(string text)
    {
        if (_open == 0) return;
        SetError("");
        _link.SayWebChat(_open, text);
    }

    private void EndChat()
    {
        if (_open == 0 || !_chats.TryGetValue(_open, out var c)) return;
        if (MessageBox.Show(this, $"End the chat with {c.Name}? They are told it has ended.", "End chat",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _link.EndWebChat(_open);
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
