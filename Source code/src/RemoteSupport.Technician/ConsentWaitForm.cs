using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// "Waiting for DESKTOP-X to accept" — shown from the moment a technician asks to connect until
/// the person at the machine answers (internal testing, 2026-10-02, items 4 and 5).
///
/// Before it, the console showed NOTHING for up to a minute: the session was only drawn once its
/// host connected, and an asking host connected only after the answer. Then a prompt that timed
/// out produced a blank session window, because the host's "declined" arrived on a socket that
/// looked exactly like a host arriving. Now the hub says when the question is on screen
/// (<see cref="SessionInfo.AwaitingConsent"/>) and when a session is over and why
/// (<see cref="SessionInfo.Ended"/>), and this window is the only thing either of them draws.
/// The session window opens when they say yes, and not before.
/// </summary>
public sealed class ConsentWaitForm : Form
{
    private readonly Theme _t;
    private readonly Label _head, _body;
    private readonly RoundButton _button;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private SessionInfo _session;
    private DateTime _askedAt;
    private bool _over;

    /// <summary>The technician pressed Cancel while it was still waiting: end that session.</summary>
    public event Action<string>? CancelRequested;

    public string SessionId => _session.SessionId;

    public ConsentWaitForm(Theme t, SessionInfo s)
    {
        _t = t; _session = s;
        Text = $"Connecting — {s.MachineName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(t, 18) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        // Measured, never written as numbers (§30): the body is three lines of its font at most.
        const int width = 460;
        int headLine = TextRenderer.MeasureText("Ag", t.H3).Height;
        int bodyLine = TextRenderer.MeasureText("Ag", t.Body).Height;
        _head = new Label { Dock = DockStyle.Top, Font = t.H3, ForeColor = t.Ink, BackColor = Color.Transparent, Height = headLine + 8, UseMnemonic = false, AutoEllipsis = true };
        _body = new Label { Dock = DockStyle.Top, Font = t.Body, ForeColor = t.Muted, BackColor = Color.Transparent, Height = bodyLine * 3 + 8, UseMnemonic = false };

        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 16;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            BackColor = Color.Transparent, Height = btnH + 10, Padding = new Padding(0, 8, 0, 0)
        };
        _button = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = new Size(120, btnH), Margin = new Padding(0) };
        _button.Click += (_, _) =>
        {
            if (!_over) CancelRequested?.Invoke(_session.SessionId);
            Close();
        };
        buttons.Controls.Add(_button);

        card.Controls.Add(_body);
        card.Controls.Add(_head);
        card.Controls.Add(buttons);
        CancelButton = _button;

        ClientSize = new Size(width + canvas.Padding.Horizontal + card.Padding.Horizontal,
            canvas.Padding.Vertical + card.Padding.Vertical + _head.Height + _body.Height + buttons.Height + 6);

        _tick.Tick += (_, _) => Draw();
        _askedAt = DateTime.UtcNow;
        Draw();
        _tick.Start();
    }

    /// <summary>The hub's latest word on this session. Returns false once there is nothing left
    /// to wait for — they said yes — and the caller closes this and opens the session window.</summary>
    public bool Update(SessionInfo s)
    {
        if (_over) return true;
        bool startedAsking = s.AwaitingConsent && !_session.AwaitingConsent;
        _session = s;
        if (startedAsking) _askedAt = DateTime.UtcNow;
        if (s.Ended)
        {
            _over = true; _tick.Stop();
            _head.Text = $"{s.MachineName} did not connect";
            _body.Text = string.IsNullOrWhiteSpace(s.EndReason) ? "The session ended before it started." : s.EndReason;
            _button.Text = "Close";
            return true;
        }
        if (s.HostConnected && !s.AwaitingConsent) return false;
        Draw();
        return true;
    }

    private void Draw()
    {
        if (_over) return;
        var s = _session;
        // Brought in by a technician there (§155): it is THEY who let you in, not the person at the machine.
        if (s.AwaitingConsent && s.AwaitingTechnician.Length > 0)
        {
            _head.Text = $"Waiting for {s.AwaitingTechnician} to let you in";
            var left = s.ConsentSeconds > 0 ? TimeSpan.FromSeconds(s.ConsentSeconds) - (DateTime.UtcNow - _askedAt) : TimeSpan.Zero;
            _body.Text = $"{s.AwaitingTechnician} is in a session on {s.MachineName} and has been asked to let you join."
                + (left > TimeSpan.Zero ? $" If they do not answer you are turned away in {(int)left.TotalMinutes}:{left.Seconds:00}." : "");
        }
        else if (s.AwaitingConsent)
        {
            _head.Text = $"Waiting for {s.MachineName} to accept";
            var left = s.ConsentSeconds > 0 ? TimeSpan.FromSeconds(s.ConsentSeconds) - (DateTime.UtcNow - _askedAt) : TimeSpan.Zero;
            _body.Text = $"The person at {s.MachineName} has been asked to allow the connection."
                + (left > TimeSpan.Zero ? $" If nobody answers it is declined in {(int)left.TotalMinutes}:{left.Seconds:00}." : "");
        }
        else
        {
            _head.Text = $"Connecting to {s.MachineName}…";
            _body.Text = "Starting the support session on that machine.";
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { _tick.Stop(); _tick.Dispose(); base.OnFormClosed(e); }
}
