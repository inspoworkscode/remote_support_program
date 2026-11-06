using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// "Let them in?" (2026-10-22, HANDOVER §155): somebody typed the code this technician made in a session. The
/// technician answers, not the person at the machine, who is told (Clint's answer). Files are a tick, unticked, for
/// this session only — the same rule as the customer's own Allow prompt. Brought to the front: the technician may
/// be in WhatsApp, having just sent the code. The server turns them away by itself when the time is up.
/// </summary>
public sealed class OutsiderAllowDialog : Form
{
    private readonly RoundCheck _files;
    private readonly Label _countdown;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _remaining;

    public string SessionId { get; }
    public bool Files => _files.Checked;

    public OutsiderAllowDialog(Theme t, OutsiderJoinAskMsg ask)
    {
        SessionId = ask.SessionId;
        _remaining = Math.Max(10, ask.Seconds);
        Text = "Outside help — let them in?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true; TopMost = true;
        Icon = BrandIcon.Current;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        const int width = 540;
        int inner = width - canvas.Padding.Horizontal - card.Padding.Horizontal;
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 18;
        Size Fit(string text, int least) => new(Math.Max(least, TextRenderer.MeasureText(text, t.ButtonFont).Width + 36), btnH);
        int Lines(string text, Font f) => TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;
        Label L(string s, Font f, Color c) { var l = new Label { Text = s, Dock = DockStyle.Top, Font = f, ForeColor = c, BackColor = Color.Transparent, UseMnemonic = false }; l.Height = Lines(s, f); return l; }

        var head = L($"{ask.Visitor} wants to join", t.H2, t.Ink);
        var about = L($"They typed the code you made for \"{ask.ForWhom}\" and are waiting to join your session on {ask.MachineName}. "
            + "Let them in only if this is who you gave it to. They get the screen, mouse and keyboard, and the person at "
            + "the computer is told. They leave when you do.", t.Body, t.Muted);
        // As tall as its words at the box's width — they wrap to two lines beside the tick (fitprobe).
        const string filesText = "Also let them copy files to and from this computer";
        int filesH = TextRenderer.MeasureText(filesText, t.Body, new Size(inner - 24, int.MaxValue), TextFormatFlags.WordBreak).Height + 10;
        _files = new RoundCheck(t)
        {
            Text = filesText, Dock = DockStyle.Top, Height = filesH,
            AutoSize = false, Checked = false, Font = t.Body, ForeColor = t.Ink, BackColor = Color.Transparent, UseMnemonic = false
        };
        _countdown = L("Ag", t.Small, t.Muted);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = btnH + 12, BackColor = Color.Transparent,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0)
        };
        var allow = new RoundButton(t, BtnStyle.Accent) { Text = "Let them in", Size = Fit("Let them in", 130), Margin = new Padding(0), DialogResult = DialogResult.OK };
        var deny = new RoundButton(t, BtnStyle.Ghost) { Text = "Turn them away", Size = Fit("Turn them away", 140), Margin = new Padding(0, 0, 10, 0), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(allow); buttons.Controls.Add(deny);

        // Top-docked: the LAST added sits highest.
        card.Controls.Add(_countdown); card.Controls.Add(_files); card.Controls.Add(about); card.Controls.Add(head);
        card.Controls.Add(buttons);
        ClientSize = new Size(width, canvas.Padding.Vertical + card.Padding.Vertical + head.Height + about.Height + _files.Height + _countdown.Height + buttons.Height + 8);
        AcceptButton = allow; CancelButton = deny;

        void Tick()
        {
            _countdown.Text = $"Turned away by itself in {_remaining / 60}:{_remaining % 60:00} if you do not answer.";
            // The server has turned them away already; this window just goes.
            if (_remaining-- <= 0) { _timer.Stop(); TimedOut = true; DialogResult = DialogResult.Abort; Close(); }
        }
        Tick();
        _timer.Tick += (_, _) => { Tick(); ComeForward.StayOnTop(this); };
        Shown += (_, _) => { _timer.Start(); ComeForward.Now(this); try { allow.Focus(); } catch { } };
    }

    /// <summary>Nobody answered in time: the server turned them away, so nothing is sent.</summary>
    public bool TimedOut { get; private set; }

    protected override void OnFormClosed(FormClosedEventArgs e) { _timer.Stop(); _timer.Dispose(); base.OnFormClosed(e); }
}
