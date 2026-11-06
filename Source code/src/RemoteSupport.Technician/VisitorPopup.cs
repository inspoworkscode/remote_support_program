using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// "Somebody from outside is connecting" (roadmap 3.9, step 2, 2026-10-06) — to every console of the
/// machine's OWN company. Clint: "the local tech can watch what is going on and terminate the connection
/// if necessary." Watch opens the session alongside them; End ends it for everybody. Bottom right, on
/// top, and it never takes the keyboard. It follows the session — waiting, allowed, ended — and stays
/// until dismissed, so whoever was away from their desk still sees it happened.
/// </summary>
public sealed class VisitorPopup : Form
{
    private readonly Theme _t;
    private readonly Label _title, _line, _state;
    private readonly RoundButton _watch, _end;
    private readonly int _headH;
    /// <summary>Brought in by one of this company's technicians (§155), not with the customer's code.</summary>
    private readonly string _invitedBy;
    private readonly string _visitor;

    public string SessionId { get; }
    public event Action<string>? WatchClicked;
    public event Action<string>? EndClicked;

    public VisitorPopup(Theme t, OutsiderVisitMsg v)
    {
        _t = t; SessionId = v.SessionId; _invitedBy = v.InvitedBy; _visitor = v.Visitor;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true; ShowInTaskbar = false;
        Text = "Somebody from outside";
        BackColor = _t.Mist; Font = _t.Body;
        Shown += (_, _) => NativeDark.Apply(this, _t);

        const int width = 420, pad = 16;
        int inner = width - pad * 2;
        int Lines(Font f, int n) => TextRenderer.MeasureText(string.Join("\n", Enumerable.Repeat("Ag", n)), f,
            new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
        _headH = Lines(_t.BodyBold, 1) + 14;

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(pad, _headH + 8, pad, pad) };
        Controls.Add(body);
        Label L(Font f, Color c, int lines) => new() { Dock = DockStyle.Top, Height = Lines(f, lines), Font = f, ForeColor = c, BackColor = Color.Transparent, UseMnemonic = false };
        _title = L(_t.BodyBold, _t.Ink, 3);   // a person and their company: three lines (fitprobe, a long one needed them)
        _line = L(_t.Small, _t.Muted, 3);   // "Brought in by {a long name}, with a code made for {a long company}" needs three (fitprobe)
        _state = L(_t.Body, _t.Ink, 2);
        _title.Text = $"{v.Visitor} — on {v.MachineName}";
        _line.Text = _invitedBy.Length > 0
            ? $"Brought in by {_invitedBy}, with a code made for \"{v.ForWhom}\"."
            : $"With a code {(v.MadeBy.Length > 0 ? v.MadeBy + " " : "")}made for \"{v.ForWhom}\".";
        _state.Text = _invitedBy.Length > 0 ? $"{_invitedBy} let them in. The person at the machine was told." : "Waiting for the person at the machine to allow it.";

        int buttonH = TextRenderer.MeasureText("Ag", _t.ButtonFont).Height + 14;
        Size Fit(string s, int least) => new(Math.Max(least, TextRenderer.MeasureText(s, _t.ButtonFont).Width + 36), buttonH);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = buttonH, BackColor = Color.Transparent, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var dismiss = new RoundButton(_t, BtnStyle.Ghost) { Text = "Dismiss", Size = Fit("Dismiss", 100), Margin = new Padding(0) };
        dismiss.Click += (_, _) => Close();
        // Somebody one of ours brought in is Removed; the session is ours and goes on (§155).
        // "Remove", not "Remove them": three buttons share 388 px, and the longer words pushed Watch off the edge (fitprobe).
        var endText = _invitedBy.Length > 0 ? "Remove" : "End it";
        _end = new RoundButton(_t, BtnStyle.Danger) { Text = endText, Size = Fit(endText, 100), Margin = new Padding(0, 0, 10, 0) };
        _end.Click += (_, _) => EndClicked?.Invoke(SessionId);
        _watch = new RoundButton(_t, BtnStyle.Accent) { Text = "Watch", Size = Fit("Watch", 100), Margin = new Padding(0, 0, 10, 0) };
        _watch.Click += (_, _) => WatchClicked?.Invoke(SessionId);
        buttons.Controls.Add(dismiss); buttons.Controls.Add(_end); buttons.Controls.Add(_watch);

        // Top-docked: the LAST added sits highest.
        body.Controls.Add(buttons);
        body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent });
        body.Controls.Add(_state); body.Controls.Add(_line); body.Controls.Add(_title);
        ClientSize = new Size(width, body.Padding.Top + _title.Height + _line.Height + _state.Height + 10 + buttonH + body.Padding.Bottom);
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && e.Y < _headH) { try { ReleaseCapture(); SendMessage(Handle, 0xA1, 0x2, 0); } catch { } } };
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Shown bottom right — above any other of these already up, so several never sit on each other.</summary>
    public void ShowNear(IWin32Window? near, int stacked)
    {
        var screen = near is Control c && c.IsHandleCreated ? Screen.FromControl(c) : Screen.PrimaryScreen;
        var wa = (screen ?? Screen.PrimaryScreen)!.WorkingArea;
        Location = new Point(wa.Right - Width - 16, Math.Max(wa.Top + 16, wa.Bottom - (Height + 12) * (stacked + 1) - 4));
        Show();
        try { SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); } catch { }
    }

    /// <summary>The session moved on: allowed, or over and why.</summary>
    public void Follow(SessionInfo s)
    {
        if (s.Ended)
        {
            _state.Text = "Ended: " + (s.EndReason.Length > 0 ? s.EndReason : "the session is over.");
            _state.ForeColor = _t.Muted;
            _watch.Visible = _end.Visible = false;
        }
        else if (_invitedBy.Length > 0)
        {
            // The session goes on after they have gone: their leaving is the pop-up's end, said so.
            if (s.Visitor != _visitor)
            {
                _state.Text = $"{_visitor} has left. The session goes on.";
                _state.ForeColor = _t.Muted;
                _watch.Visible = _end.Visible = false;
            }
            else _state.Text = s.VisitorFiles ? $"{_invitedBy} let them in — with files. The person at the machine was told." : $"{_invitedBy} let them in, without files. The person at the machine was told.";
        }
        else if (s.HostConnected && !s.AwaitingConsent)
            _state.Text = s.VisitorFiles ? "The person at the machine allowed it — and ticked that they may copy files." : "The person at the machine allowed it, without files. They are connected now.";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        // Red: somebody outside the company is on a customer's machine — the one pop-up to look at at once.
        var red = Color.FromArgb(0xB9, 0x1C, 0x1C);
        using (var head = new SolidBrush(red)) g.FillRectangle(head, 0, 0, Width, _headH);
        TextRenderer.DrawText(g, "Somebody from outside is connecting", _t.BodyBold, new Rectangle(16, 0, Width - 32, _headH), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        using var pen = new Pen(red, 2f);
        g.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
