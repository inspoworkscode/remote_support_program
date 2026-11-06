using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Tools → Get outside help… (2026-10-22, HANDOVER §155). Clint: "sometimes it happens that a tech needs the outside
/// assistance rather than the client" — a code for THIS session, plus the links, to copy or send by WhatsApp or
/// email. Two faces: who it is for, then the code with the message and what has become of it. One per session
/// window, not modal: the server's answers arrive into it. Every height measured from its font (§30).
/// </summary>
public sealed class OutsideHelpForm : Form
{
    private readonly Theme _t;
    private readonly string _serverBase, _technician, _company;
    private readonly Panel _ask, _show;
    private readonly InputBox _for;
    private readonly Label _askHint, _codeHead, _code, _left, _state;
    private readonly TextBox _message;
    private readonly RoundButton _make, _copy, _whatsApp, _email, _stopOrNew, _remove;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private string _codeDigits = "", _forWhom = "", _connected = "";
    private DateTime? _expires;
    private bool _live;

    /// <summary>Ask the server for a code for this person.</summary>
    public event Action<string>? MakeCode;
    /// <summary>Stop the live code.</summary>
    public event Action? StopCode;
    /// <summary>Take the person who is connected out of the session.</summary>
    public event Action? RemoveThem;

    public OutsideHelpForm(Theme t, string machineName, string serverBase, string technician, string company)
    {
        _t = t; _serverBase = serverBase; _technician = technician; _company = company;
        Text = $"Get outside help — {machineName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        const int width = 600;
        int inner = width - canvas.Padding.Horizontal - card.Padding.Horizontal;
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 18;
        Size Fit(string text, int least) => new(Math.Max(least, TextRenderer.MeasureText(text, t.ButtonFont).Width + 36), btnH);
        int Lines(string text, Font f) => TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;
        Label L(string s, Font f, Color c, int? h = null) { var l = new Label { Text = s, Dock = DockStyle.Top, Font = f, ForeColor = c, BackColor = Color.Transparent, UseMnemonic = false }; l.Height = h ?? Lines(s, f); return l; }
        FlowLayoutPanel Row() => new() { Dock = DockStyle.Bottom, Height = btnH + 12, BackColor = Color.Transparent, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };

        // ---- face 1: who is it for ----
        _ask = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var head = L("Get outside help", t.H2, t.Ink);
        var about = L("Make a code for somebody outside your company — another IT company, a supplier, a specialist. "
            + "It works once, for ten minutes, and only for this session. When they type it you are asked to let them in, "
            + "and the person at this computer is told. They get the screen, mouse and keyboard — files only if you tick it "
            + "when you let them in. They leave when you do.", t.Small, t.Muted);
        var forLbl = L("Who is it for?", t.BodyBold, t.Ink);
        _for = new InputBox(t) { Dock = DockStyle.Top };
        _for.Box.MaxLength = OutsiderAccess.MaxForLength;
        _for.Box.PlaceholderText = "Pieter, Smith & Co Networks";
        _for.Height = _for.Box.PreferredHeight + 16;
        _askHint = L("", t.Small, t.BadText, Lines("Ag\nAg", t.Small));
        var askButtons = Row();
        _make = new RoundButton(t, BtnStyle.Accent) { Text = "Make a code", Size = Fit("Make a code", 130), Margin = new Padding(0) };
        var close1 = new RoundButton(t, BtnStyle.Ghost) { Text = "Close", Size = Fit("Close", 100), Margin = new Padding(0, 0, 10, 0) };
        _make.Click += (_, _) => Ask();
        close1.Click += (_, _) => Close();
        askButtons.Controls.Add(_make); askButtons.Controls.Add(close1);
        foreach (var c in new Control[] { _askHint, _for, forLbl, about, head }) _ask.Controls.Add(c);
        _ask.Controls.Add(askButtons);
        int askH = head.Height + about.Height + forLbl.Height + _for.Height + _askHint.Height + askButtons.Height + 8;
        _for.Box.TextChanged += (_, _) => _askHint.Text = "";

        // ---- face 2: the code, the message, and what has become of it ----
        _show = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Visible = false };
        // Two lines: who it is for can be a person AND their company, 80 characters (fitprobe).
        _codeHead = L("Code for", t.H3, t.Ink, Lines("Ag\nAg", t.H3));
        var codeFont = new Font("Segoe UI", 26f, FontStyle.Bold);
        _code = L("000 000 000", codeFont, t.Ink, TextRenderer.MeasureText("000", codeFont).Height + 6);
        _left = L("", t.Small, t.Muted, Lines("Ag", t.Small));
        var msgLbl = L("The message to send them", t.BodyBold, t.Ink);
        _message = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            Font = t.Small, BackColor = t.Surface, ForeColor = t.Ink, Dock = DockStyle.Fill
        };
        var msgFrame = new Panel { Dock = DockStyle.Top, Padding = new Padding(10, 8, 10, 8), BackColor = t.Surface };
        msgFrame.Height = TextRenderer.MeasureText(string.Join("\n", Enumerable.Repeat("Ag", 8)), t.Small).Height + 20;
        msgFrame.Controls.Add(_message);
        _state = L("", t.Body, t.Ink, Lines("Ag\nAg", t.Body));

        var sendRow = Row();
        _email = new RoundButton(t, BtnStyle.Neutral) { Text = "Email", Size = Fit("Email", 100), Margin = new Padding(0) };
        _whatsApp = new RoundButton(t, BtnStyle.Neutral) { Text = "WhatsApp", Size = Fit("WhatsApp", 110), Margin = new Padding(0, 0, 10, 0) };
        _copy = new RoundButton(t, BtnStyle.Accent) { Text = "Copy", Size = Fit("Copy", 100), Margin = new Padding(0, 0, 10, 0) };
        _copy.Click += (_, _) => CopyMessage();
        _whatsApp.Click += (_, _) => Open(OutsideHelpMessage.WhatsAppUrl(_message.Text), "WhatsApp");
        _email.Click += (_, _) => Open(OutsideHelpMessage.MailUrl(_message.Text), "your email");
        sendRow.Controls.Add(_email); sendRow.Controls.Add(_whatsApp); sendRow.Controls.Add(_copy);

        var endRow = Row();
        var close2 = new RoundButton(t, BtnStyle.Ghost) { Text = "Close", Size = Fit("Close", 100), Margin = new Padding(0) };
        close2.Click += (_, _) => Close();
        _stopOrNew = new RoundButton(t, BtnStyle.Neutral) { Text = "Make a new code", Size = Fit("Make a new code", 150), Margin = new Padding(0, 0, 10, 0) };
        _stopOrNew.Click += (_, _) => { if (_live) StopCode?.Invoke(); else MakeCode?.Invoke(_forWhom); };
        _remove = new RoundButton(t, BtnStyle.Danger) { Text = "Remove them", Size = Fit("Remove them", 130), Margin = new Padding(0, 0, 10, 0), Visible = false };
        _remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Take {_connected} out of the session? You stay connected.", "Outside help",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) RemoveThem?.Invoke();
        };
        endRow.Controls.Add(close2); endRow.Controls.Add(_stopOrNew); endRow.Controls.Add(_remove);

        // Top-docked: the LAST added sits highest.
        foreach (var c in new Control[] { _state, msgFrame, msgLbl, _left, _code, _codeHead }) _show.Controls.Add(c);
        _show.Controls.Add(sendRow); _show.Controls.Add(endRow);
        int showH = _codeHead.Height + _code.Height + _left.Height + msgLbl.Height + msgFrame.Height + _state.Height + sendRow.Height + endRow.Height + 8;

        card.Controls.Add(_show); card.Controls.Add(_ask);
        ClientSize = new Size(width, canvas.Padding.Vertical + card.Padding.Vertical + Math.Max(askH, showH));
        AcceptButton = _make;
        _tick.Tick += (_, _) => DrawLeft();
        Shown += (_, _) => { try { _for.Box.Focus(); } catch { } };
    }

    private void Ask()
    {
        var problem = OutsiderAccess.ForProblem(_for.Box.Text);
        if (problem.Length > 0) { _askHint.Text = problem; _for.Box.Focus(); return; }
        _forWhom = _for.Box.Text.Trim();
        _make.Enabled = false;
        _askHint.ForeColor = _t.Muted; _askHint.Text = "Making a code…";
        MakeCode?.Invoke(_forWhom);
    }

    /// <summary>The server's word: the code, why not, or what has become of it.</summary>
    public void Apply(OutsiderInviteMsg m)
    {
        if (m.Error.Length > 0)
        {
            if (_show.Visible) { _state.ForeColor = _t.BadText; _state.Text = m.Error; }
            else { _make.Enabled = true; _askHint.ForeColor = _t.BadText; _askHint.Text = m.Error; }
            return;
        }
        if (m.Code.Length > 0)
        {
            _codeDigits = m.Code; _forWhom = m.For.Length > 0 ? m.For : _forWhom; _expires = m.ExpiresUtc; _live = true;
            _codeHead.Text = $"Code for {_forWhom}";
            _code.Text = OutsiderAccess.Format(m.Code);
            _message.Text = OutsideHelpMessage.Text(_serverBase, m.Code, _forWhom, _technician, _company);
            _ask.Visible = false; _show.Visible = true; AcceptButton = _copy;
            foreach (var b in new[] { _copy, _whatsApp, _email }) b.Enabled = true;
            _tick.Start();
        }
        // Once somebody has typed it, or it was stopped, the code is spent: nothing left to send.
        if (m.Code.Length == 0 && m.State.Length > 0)
        {
            _live = false;
            foreach (var b in new[] { _copy, _whatsApp, _email }) b.Enabled = false;
            _left.Text = "";
            _tick.Stop();
        }
        _connected = m.Connected;
        _remove.Visible = _connected.Length > 0;
        if (_connected.Length > 0) _remove.Text = "Remove them";
        _stopOrNew.Text = _live ? "Stop this code" : "Make a new code";
        _stopOrNew.Visible = _connected.Length == 0;
        _state.ForeColor = _t.Ink;
        _state.Text = m.State;
        DrawLeft();
    }

    private void DrawLeft()
    {
        if (!_live || _expires is not { } until) return;
        var left = until - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _live = false; _tick.Stop();
            _left.Text = "";
            _state.Text = "The code ran out unused.";
            foreach (var b in new[] { _copy, _whatsApp, _email }) b.Enabled = false;
            _stopOrNew.Text = "Make a new code";
            return;
        }
        _left.Text = $"Works once, for {(int)left.TotalMinutes}:{left.Seconds:00} more.";
    }

    private void CopyMessage()
    {
        try { Clipboard.SetText(_message.Text); _state.ForeColor = _t.Ink; _state.Text = "Copied. Paste it into a message to them."; }
        catch { _state.ForeColor = _t.BadText; _state.Text = "The message could not be copied. Select it above and copy it yourself."; }
    }

    private void Open(string url, string what)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); _state.ForeColor = _t.Ink; _state.Text = $"Opened {what} with the message in it. Choose who to send it to."; }
        catch { _state.ForeColor = _t.BadText; _state.Text = $"Could not open {what}. Use Copy and paste it instead."; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { _tick.Stop(); _tick.Dispose(); base.OnFormClosed(e); }
}
