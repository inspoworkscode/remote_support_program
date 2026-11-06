using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// "Let someone else connect…" (roadmap 3.9, step 1, 2026-10-06). The person at this computer makes a
/// code for somebody OUTSIDE the IT company — their accounting firm, another IT company — to connect
/// with. Clint's design: the customer makes it; nine digits, ten minutes, once; they are still asked to
/// Allow when it is used; the IT company is told and can watch and end it.
///
/// <para>Two faces in one window: first the WARNING and who it is for, then the code. The warning comes
/// before the code on purpose — "read me the code on your screen" is how the scam goes, and this is the
/// one moment the person is certainly reading.</para>
///
/// <para>Not modal: the code arrives from the server after the request, so the window stays open and
/// <see cref="ShowCode"/> fills it in. One window at a time (MainForm keeps it).</para>
/// </summary>
public sealed class OutsiderCodeDialog : Form
{
    private readonly Theme _t;
    private readonly Branding _brand;
    private readonly Panel _ask, _show;
    private readonly InputBox _for;
    private readonly Label _askHint, _code, _codeFor, _codeUntil, _sent;
    private readonly RoundButton _make, _copy, _whatsApp, _email;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private DateTime? _until;
    /// <summary>Who is asking, for the message: the name last given at Request Support (or the Windows sign-in).</summary>
    private readonly string _asker;
    private string _message = "";

    /// <summary>"Make a code" for this person or company.</summary>
    public event Action<string>? MakeCode;
    /// <summary>"Stop this code".</summary>
    public event Action? StopCode;

    public OutsiderCodeDialog(Theme t, Branding brand, string asker = "")
    {
        _t = t; _brand = brand; _asker = asker.Trim();
        Text = "Let someone else connect";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 18;
        Size Fit(string text, int least) => new(Math.Max(least, TextRenderer.MeasureText(text, t.ButtonFont).Width + 36), btnH);
        int row = Math.Max(Fit("Stop this code", 140).Width + 10 + Fit("Make a new code", 150).Width + 10 + Fit("Close", 100).Width,
                           Fit("Copy the message", 150).Width + 10 + Fit("WhatsApp", 110).Width + 10 + Fit("Email", 100).Width);
        int width = Math.Max(520, row + canvas.Padding.Horizontal + card.Padding.Horizontal + 8);
        int inner = width - canvas.Padding.Horizontal - card.Padding.Horizontal;
        // Every height from the font it draws with (§30).
        int Lines(string text, Font f) => TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;
        Label Text_(string s, Font f, Color c) { var l = new Label { Text = s, Dock = DockStyle.Top, Font = f, ForeColor = c, BackColor = Color.Transparent, UseMnemonic = false }; l.Height = Lines(s, f); return l; }
        FlowLayoutPanel Buttons() => new()
        {
            Dock = DockStyle.Bottom, Height = btnH + 12, BackColor = Color.Transparent,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0)
        };

        // ---- first face: the warning, and who it is for ----
        _ask = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        var askHead = Text_("Let someone else connect", t.H2, t.Ink);
        var warn = Text_("Only give a code to somebody YOU contacted — your accountant, a company you asked for help. "
            + "Nobody legitimate phones you out of the blue and asks for a code. If somebody did, close this window.", t.BodyBold, t.BadText);
        var about = Text_($"They can connect once, within ten minutes, and you will still be asked to Allow them. "
            + $"{brand.CompanyName} will be told and can watch and end it.", t.Small, t.Muted);
        var forLbl = new Label
        {
            Text = "Who is it for?", Dock = DockStyle.Top, Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent,
            UseMnemonic = false, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4),
            Height = TextRenderer.MeasureText("Ag", t.BodyBold).Height + 14
        };
        _for = new InputBox(t) { Dock = DockStyle.Top };
        _for.Box.PlaceholderText = "e.g. Smith & Co Accountants";
        _for.Box.MaxLength = OutsiderAccess.MaxForLength;
        _for.Height = _for.Box.PreferredHeight + 16;
        _askHint = new Label
        {
            Text = "", Dock = DockStyle.Bottom, Font = t.Small, ForeColor = t.BadText, BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Height = Lines("Ag\nAg", t.Small)
        };
        var askButtons = Buttons();
        _make = new RoundButton(t, BtnStyle.Accent) { Text = "Make a code", Size = Fit("Make a code", 130), Margin = new Padding(0) };
        _make.Click += (_, _) => Ask();
        var cancel = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = Fit("Cancel", 100), Margin = new Padding(0, 0, 10, 0) };
        cancel.Click += (_, _) => Close();
        askButtons.Controls.Add(_make); askButtons.Controls.Add(cancel);
        // Top-docked: the LAST added sits highest.
        _ask.Controls.Add(_for); _ask.Controls.Add(forLbl); _ask.Controls.Add(about); _ask.Controls.Add(warn); _ask.Controls.Add(askHead);
        _ask.Controls.Add(askButtons); _ask.Controls.Add(_askHint);
        int askH = askHead.Height + warn.Height + about.Height + forLbl.Height + _for.Height + _askHint.Height + askButtons.Height;

        // ---- second face: the code ----
        _show = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Visible = false };
        var showHead = Text_("Give them this code", t.H2, t.Ink);
        var codeFont = new Font("Segoe UI", 30f, FontStyle.Bold);
        _code = new Label { Dock = DockStyle.Top, Font = codeFont, ForeColor = t.Ink, BackColor = Color.Transparent, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleCenter, Height = TextRenderer.MeasureText("000 000 000", codeFont).Height + 16 };
        _codeFor = Text_("For", t.BodyBold, t.Ink);
        _codeFor.TextAlign = ContentAlignment.MiddleCenter;
        _codeUntil = Text_("Works once, until 00:00 — 10 minutes left", t.Small, t.Muted);
        _codeUntil.TextAlign = ContentAlignment.MiddleCenter;
        // Said again where they are looking now: the Allow is the last door, and it is theirs.
        var whenThey = Text_($"When they connect, you will be asked to Allow them, by name. "
            + $"If it is not who you gave the code to, press Deny — and tell {brand.CompanyName}.", t.Small, t.Muted);
        whenThey.Padding = new Padding(0, 14, 0, 0);
        whenThey.Height += 14;
        var showButtons = Buttons();
        var close = new RoundButton(t, BtnStyle.Accent) { Text = "Close", Size = Fit("Close", 100), Margin = new Padding(0) };
        close.Click += (_, _) => Close();
        var again = new RoundButton(t, BtnStyle.Ghost) { Text = "Make a new code", Size = Fit("Make a new code", 150), Margin = new Padding(0, 0, 10, 0) };
        again.Click += (_, _) => { _show.Visible = false; _ask.Visible = true; _askHint.Text = ""; _for.Box.Focus(); };
        var stop = new RoundButton(t, BtnStyle.Danger) { Text = "Stop this code", Size = Fit("Stop this code", 140), Margin = new Padding(0, 0, 10, 0) };
        stop.Click += (_, _) => { StopCode?.Invoke(); Close(); };
        showButtons.Controls.Add(close); showButtons.Controls.Add(again); showButtons.Controls.Add(stop);
        // Send it (Clint, 2026-10-22: "it just simplifies the process of getting assistance"): the same message, links and
        // buttons a technician's code has (§155) — the browser link fills the code in. Reading it out still works.
        var sendRow = Buttons();
        _email = new RoundButton(t, BtnStyle.Neutral) { Text = "Email", Size = Fit("Email", 100), Margin = new Padding(0) };
        _whatsApp = new RoundButton(t, BtnStyle.Neutral) { Text = "WhatsApp", Size = Fit("WhatsApp", 110), Margin = new Padding(0, 0, 10, 0) };
        _copy = new RoundButton(t, BtnStyle.Neutral) { Text = "Copy the message", Size = Fit("Copy the message", 150), Margin = new Padding(0, 0, 10, 0) };
        _copy.Click += (_, _) => CopyMessage();
        _whatsApp.Click += (_, _) => Open(OutsideHelpMessage.WhatsAppUrl(_message), "WhatsApp");
        _email.Click += (_, _) => Open(OutsideHelpMessage.MailUrl(_message), "your email");
        sendRow.Controls.Add(_email); sendRow.Controls.Add(_whatsApp); sendRow.Controls.Add(_copy);
        _sent = Text_("Or send it: the message has the code and how to connect.", t.Small, t.Muted);
        _sent.TextAlign = ContentAlignment.MiddleCenter;
        _show.Controls.Add(whenThey); _show.Controls.Add(_sent); _show.Controls.Add(_codeUntil); _show.Controls.Add(_codeFor); _show.Controls.Add(_code); _show.Controls.Add(showHead);
        _show.Controls.Add(sendRow); _show.Controls.Add(showButtons);
        int showH = showHead.Height + _code.Height + _codeFor.Height + _codeUntil.Height + _sent.Height + whenThey.Height + sendRow.Height + showButtons.Height + 12;

        card.Controls.Add(_ask);
        card.Controls.Add(_show);
        ClientSize = new Size(width, canvas.Padding.Vertical + card.Padding.Vertical + Math.Max(askH, showH) + 8);

        AcceptButton = _make;
        _for.Box.TextChanged += (_, _) => _askHint.Text = "";
        _tick.Tick += (_, _) => DrawUntil();
        FormClosed += (_, _) => _tick.Dispose();
        Shown += (_, _) => { try { if (_ask.Visible) _for.Box.Focus(); } catch { } };
    }

    private void Ask()
    {
        var problem = OutsiderAccess.ForProblem(_for.Box.Text);
        if (problem.Length > 0) { _askHint.Text = problem; _for.Box.Focus(); return; }
        _make.Enabled = false;
        _askHint.ForeColor = _t.Muted;
        _askHint.Text = "Making the code…";
        MakeCode?.Invoke(_for.Box.Text.Trim());
    }

    /// <summary>The server's answer: the code, or why not.</summary>
    public void ShowCode(OutsiderCodeMsg m)
    {
        _make.Enabled = true;
        if (m.Error.Length > 0 || m.Code.Length == 0)
        {
            _show.Visible = false; _ask.Visible = true;
            _askHint.ForeColor = _t.BadText;
            _askHint.Text = m.Error.Length > 0 ? m.Error : "No code came back. Try again.";
            return;
        }
        _code.Text = OutsiderAccess.Format(m.Code);
        _codeFor.Text = "For " + m.For;
        _until = m.ExpiresUtc;
        _message = OutsideHelpMessage.Text(OutsideHelpMessage.WebBase(_brand.ServerUrl), m.Code, m.For, _asker, "");
        foreach (var b in new[] { _copy, _whatsApp, _email }) b.Enabled = true;
        _code.ForeColor = _t.Ink;
        _sent.ForeColor = _t.Muted; _sent.Text = "Or send it: the message has the code and how to connect.";
        DrawUntil();
        _tick.Start();
        _ask.Visible = false; _show.Visible = true;
        AcceptButton = null;
    }

    /// <summary>Told by the server that this code no longer works (stopped, used, or switched off).</summary>
    public void CodeGone(string why)
    {
        if (!_show.Visible) return;
        _tick.Stop();
        _code.ForeColor = _t.Muted;
        _codeUntil.Text = why;
        // Nothing worth sending any more.
        foreach (var b in new[] { _copy, _whatsApp, _email }) b.Enabled = false;
    }

    private void CopyMessage()
    {
        try { Clipboard.SetText(_message); _sent.ForeColor = _t.Ink; _sent.Text = "Copied. Paste it into a message to them."; }
        catch { _sent.ForeColor = _t.BadText; _sent.Text = "It could not be copied. Read them the code instead."; }
    }

    private void Open(string url, string what)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); _sent.ForeColor = _t.Ink; _sent.Text = $"Opened {what} with the message in it. Choose who to send it to."; }
        catch { _sent.ForeColor = _t.BadText; _sent.Text = $"Could not open {what}. Use Copy the message instead."; }
    }

    private void DrawUntil()
    {
        if (_until is not { } u) return;
        var left = u - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) { CodeGone("This code has run out. Make a new one if they still need to connect."); return; }
        int mins = (int)Math.Ceiling(left.TotalMinutes);
        _codeUntil.Text = $"Works once, until {u.ToLocalTime():HH:mm} — {mins} minute{(mins == 1 ? "" : "s")} left";
    }
}
