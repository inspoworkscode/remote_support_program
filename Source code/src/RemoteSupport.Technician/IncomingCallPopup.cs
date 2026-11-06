using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Always-on-top "incoming support request" alert, shown centre-screen so a technician cannot miss a
/// call when the console is minimised and the sound is off. It deliberately does NOT steal keyboard
/// focus, so it can never swallow what someone is typing elsewhere; it pulses and flashes the taskbar
/// instead.
/// </summary>
public sealed class IncomingCallPopup : Form
{
    private readonly Theme _t;
    private readonly Image? _logo;
    private readonly string _company;

    private readonly Label _who = new();
    private readonly Label _machine = new();
    private readonly Label _issue = new();
    private readonly Label _waiting = new();
    private readonly Label _more = new();
    private readonly RoundButton _accept;
    private readonly RoundButton _dismiss;

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    private CallInfo? _call;
    /// <summary>The description as the customer wrote it. The label may be showing a trimmed
    /// version of it, so re-fitting always starts from this rather than from what is on screen —
    /// trimming an already-trimmed string would eat another word on every resize.</summary>
    private string _issueRaw = "";
    private float _pulse;
    private bool _pulseUp = true;

    public event Action<string>? Accepted;      // callId
    public event Action<string>? Dismissed;     // callId

    public IncomingCallPopup(Theme t, Branding brand)
    {
        _t = t; _company = brand.CompanyName;
        try { if (brand.HasLogo) _logo = Image.FromFile(brand.LogoPath); } catch { }

        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(480, 320);
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = true;
        Text = "Incoming support request";
        BackColor = _t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, _t);   // dark title bar, scrollbars and list header — see NativeDark
        Font = _t.Body;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(26, 96, 26, 22) };
        Controls.Add(body);

        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.Transparent };
        _accept = new RoundButton(_t, BtnStyle.Success) { Text = "Accept Call", Dock = DockStyle.Fill, Height = 52, Font = new Font("Segoe UI", 12.5f, FontStyle.Bold) };
        _accept.Click += (_, _) => { var id = _call?.CallId; HideCall(); if (id != null) Accepted?.Invoke(id); };
        _dismiss = new RoundButton(_t, BtnStyle.Ghost) { Text = "Dismiss", Dock = DockStyle.Right, Width = 116, Height = 52 };
        _dismiss.Click += (_, _) => { var id = _call?.CallId; HideCall(); if (id != null) Dismissed?.Invoke(id); };
        buttons.Controls.Add(_accept);
        buttons.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Color.Transparent });
        buttons.Controls.Add(_dismiss);

        // ---- what a ringing call says, in the order it should be read (stage 3.5.7) ----------
        // WHAT IS WRONG comes first and largest. It used to be third, in body text, under a name
        // in 17 pt bold — so the biggest thing on a support alert was the one fact that changes
        // nothing about how you answer it. The reason is what decides whether this is a two
        // minute fix or the rest of the morning, whether it can wait for the call you are on,
        // and who should take it.
        //
        // The name is not demoted to unimportant, only to second: it is still Ink and still
        // bold enough to read at a glance, which is all it was doing at 17 pt.
        _more = new Label { Dock = DockStyle.Bottom, Height = 18, ForeColor = _t.Muted, Font = _t.Small, BackColor = Color.Transparent, UseMnemonic = false };
        _waiting = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = _t.Muted, Font = _t.Small, BackColor = Color.Transparent, UseMnemonic = false };
        _machine = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = _t.Muted, Font = _t.Small, BackColor = Color.Transparent, UseMnemonic = false };
        _who = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = _t.Ink, Font = _t.BodyBold, BackColor = Color.Transparent, UseMnemonic = false };
        // Two lines at this size. NOT AutoEllipsis: a Label with AutoEllipsis stops wrapping and
        // puts everything on one line, which turned "Sage will not open — it says the licence
        // server cannot be found" into "Sage will not open — it says ..." and threw away the half
        // that says what to bring. The trimming is done by hand in <see cref="FitIssue"/>
        // instead, so it wraps AND admits when it has been cut.
        _issue = new Label
        {
            Dock = DockStyle.Bottom, Height = 64, ForeColor = _t.Ink,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold), BackColor = Color.Transparent,
            UseMnemonic = false
        };

        // ---- sized from the FONT, not from magic numbers ------------------------------------
        // The heights above were pixel constants written against a 100% display, and fonts are
        // in POINTS: at Clint's 150% one line of this 14 pt bold is 38 px, not the ~25 the
        // constants assumed. So the alert had been quietly clipping its own text on the machine
        // it was developed on, and adding a two-line description was what finally made it
        // obvious — the description came out cut to one line and the trimming took the blame.
        //
        // Measured once here instead. The alert then grows to fit whatever the technician's
        // display and the tenant's font actually need, which is the only version of this that is
        // right on somebody else's laptop too.
        // Measured with the SAME flags the Label renders with, and that is not a detail. The
        // first version measured with NoPadding and sized the box from it; the Label then drew
        // with padding, wrapped a word earlier, needed a third line and silently ate "found" off
        // the end of "the licence server cannot be found". A budget and a fit test that disagree
        // by a few pixels produce exactly that: text that is cut with nothing to say it was.
        int TextWidth = 480 - body.Padding.Horizontal;
        int Measure(string s, Font f) =>
            TextRenderer.MeasureText(s, f, new Size(TextWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;

        // THREE lines, not two. Two held about 56 characters at this size, and a real description
        // is a sentence: "Sage will not open — it says the licence server cannot be found" lost
        // the word naming what could not be found. The alert grows instead — it is centred on an
        // otherwise empty screen and has the room, and a technician being able to read one
        // sentence is the whole point of the change.
        _issue.Height = Measure("Ag\nAg\nAg", _issue.Font) + 2;
        _who.Height = Measure("Ag", _who.Font) + 2;
        _machine.Height = Measure("Ag", _machine.Font) + 2;
        _waiting.Height = Measure("Ag", _waiting.Font) + 2;
        _more.Height = Measure("Ag", _more.Font);
        ClientSize = new Size(480, body.Padding.Top + _issue.Height + _who.Height + _machine.Height
                                   + _waiting.Height + _more.Height + buttons.Height + body.Padding.Bottom);

        // all bottom-docked: the earliest added sits highest, so add top-to-bottom in reading order
        body.Controls.Add(_issue);
        body.Controls.Add(_who);
        body.Controls.Add(_machine);
        body.Controls.Add(_waiting);
        body.Controls.Add(_more);
        body.Controls.Add(buttons);

        // NO re-fit from Resize, and this is worth the paragraph. Setting a control's Text from
        // inside its own Resize mutates it in the middle of the layout pass, and the pass then
        // does not finish applying the dock: the label kept the 100 px a Label is born with while
        // its five siblings all measured 428. Everything downstream then followed honestly from a
        // width that was never real — the text was trimmed to fit 100 px, so a whole description
        // came out as "Sage…" and the trimming looked like the fault.
        //
        // It is not needed anyway: this alert is a fixed 480x320 and cannot be resized, so the
        // label's width changes exactly once, during the first layout. ShowCall re-fits after
        // Show() for that.

        _tick.Tick += (_, _) => OnTick();

        // let the alert be dragged out of the way by its header
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && e.Y < 88) DragFrom(); };
    }

    /// <summary>Show without taking keyboard focus from whatever the technician is doing.</summary>
    protected override bool ShowWithoutActivation => true;

    public string? CurrentCallId => _call?.CallId;

    public void ShowCall(CallInfo call, int alsoWaiting, IWin32Window? near)
    {
        bool isNew = _call?.CallId != call.CallId;
        _call = call;
        _who.Text = string.IsNullOrWhiteSpace(call.ContactName) ? "Support request" : call.ContactName;
        _machine.Text = string.IsNullOrWhiteSpace(call.MachineName) ? "" : call.MachineName;
        // The description is required of the customer now (stage 3.5.7), so an empty one means an
        // OLD CLIENT APP on that machine rather than somebody who declined to say. Worth saying
        // out loud and worth keeping: the fallback cannot be deleted just because the current
        // client cannot produce it, or the day an old machine rings the alert is blank.
        _issueRaw = string.IsNullOrWhiteSpace(call.Issue)
            ? "No description — ask what is wrong (their client app is an old one)"
            : call.Issue;
        ApplyIssueFit();
        _more.Text = alsoWaiting > 0 ? $"+ {alsoWaiting} more call{(alsoWaiting == 1 ? "" : "s")} waiting" : "";
        UpdateWaiting();

        if (!Visible)
        {
            CentreOn(near);
            Show();
            _tick.Start();
        }
        if (isNew) { CentreOn(near); }
        // Again, now the window has been laid out. The first call above runs before Show(), when
        // the label has not been given its width yet and any measurement of it is a measurement
        // of nothing — which trimmed a description that fitted perfectly well.
        ApplyIssueFit();
        TopMost = true;
        try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
        Flash(true);
        Invalidate();
    }

    /// <summary>Re-fits the description to whatever room the label actually has. Called once when
    /// a call arrives, again once the window has been laid out, and on every resize of the label
    /// — the first of those is always measuring a label that does not have its width yet.</summary>
    private void ApplyIssueFit()
    {
        if (_issueRaw.Length == 0) return;
        var fitted = FitIssue(_issueRaw);
        if (_issue.Text != fitted) _issue.Text = fitted;
    }

    /// <summary>
    /// Trims a description to the two lines the alert has room for, ending in an ellipsis when it
    /// had to cut something.
    ///
    /// Done by hand rather than with AutoEllipsis because the two are exclusive in WinForms: a
    /// Label with AutoEllipsis lays its text out on ONE line and truncates, so a description that
    /// would have fitted across two loses half of itself. What is lost is usually the specific
    /// half — "it says the licence server cannot be found" — which is the part that decides what
    /// the technician does next.
    ///
    /// Cutting at a word boundary and saying so is the point. Text that simply stops mid-word
    /// reads as a bug in the alert rather than as a long sentence.
    /// </summary>
    private string FitIssue(string text)
    {
        text = text.Trim();
        int w = _issue.ClientSize.Width;
        // This alert is a fixed 480 px wide, so the label is 428 or it has not been laid out yet
        // — a Label is born 100 px wide and reports it confidently. Trimming to a width that was
        // never real is how a whole description became "Sage…", so anything implausibly narrow
        // means "do not trim", and the re-fit after layout does the real work.
        if (w < 200) return text;

        // The same flags the label's height was budgeted with, and the same ones the Label draws
        // with. Measuring one way and rendering another is what cut "found" off the end of "the
        // licence server cannot be found" with nothing to say it had been cut.
        bool Fits(string s) =>
            TextRenderer.MeasureText(s, _issue.Font, new Size(w, int.MaxValue),
                                     TextFormatFlags.WordBreak).Height <= _issue.Height;

        if (Fits(text)) return text;

        var words = text.Split(' ');
        var kept = new List<string>();
        foreach (var word in words)
        {
            var trial = string.Join(' ', kept.Append(word)) + "…";
            if (!Fits(trial)) break;
            kept.Add(word);
        }
        // A single word too long for two lines: let it wrap and clip rather than showing nothing
        // but an ellipsis.
        return kept.Count == 0 ? text : string.Join(' ', kept) + "…";
    }

    public void HideCall()
    {
        _call = null;
        _tick.Stop();
        Flash(false);
        if (Visible) Hide();
    }

    private void CentreOn(IWin32Window? near)
    {
        var screen = near is Control c && c.IsHandleCreated ? Screen.FromControl(c) : Screen.PrimaryScreen;
        var wa = (screen ?? Screen.PrimaryScreen)!.WorkingArea;
        Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
    }

    private void OnTick()
    {
        UpdateWaiting();
        _pulse += _pulseUp ? 0.14f : -0.14f;
        if (_pulse >= 1f) { _pulse = 1f; _pulseUp = false; }
        if (_pulse <= 0f) { _pulse = 0f; _pulseUp = true; }
        Invalidate();
    }

    private void UpdateWaiting()
    {
        if (_call == null) return;
        var s = (int)(DateTimeOffset.UtcNow - _call.Created).TotalSeconds;
        _waiting.Text = s < 60 ? $"Waiting {s} second{(s == 1 ? "" : "s")}" : $"Waiting {s / 60}m {s % 60}s";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.Smooth(g);
        var r = ClientRectangle;

        using (var bg = new LinearGradientBrush(r, _t.Peach, _t.Mist, LinearGradientMode.ForwardDiagonal))
            g.FillRectangle(bg, r);

        // branded header band
        var head = new Rectangle(0, 0, Width, 76);
        using (var hb = new LinearGradientBrush(head, _t.Primary, Theme.Lighten(_t.Primary, .16), LinearGradientMode.Horizontal))
            g.FillRectangle(hb, head);
        int x = 18;
        if (_logo != null)
        {
            int h = 46, w = (int)(_logo.Width * (h / (double)_logo.Height));
            w = Math.Min(w, 150);
            g.DrawImage(_logo, new Rectangle(x, (76 - h) / 2, w, h));
            x += w + 14;
        }
        TextRenderer.DrawText(g, "Incoming support request", new Font("Segoe UI", 12f, FontStyle.Bold),
            new Point(x, 27), Theme.On(_t.Primary));

        // pulsing attention border
        var accent = _t.Accent;
        int alpha = (int)(110 + 145 * _pulse);
        using (var pen = new Pen(Color.FromArgb(alpha, accent), 4f))
            g.DrawRectangle(pen, 2, 2, Width - 5, Height - 5);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // never really close — the dashboard owns the lifetime
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideCall(); }
        base.OnFormClosing(e);
    }

    // ---- native helpers ----
    private void DragFrom()
    {
        try { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0); } catch { }
    }

    private void Flash(bool on)
    {
        try
        {
            var fw = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = Handle,
                dwFlags = on ? (FLASHW_ALL | FLASHW_TIMERNOFG) : FLASHW_STOP,
                uCount = on ? uint.MaxValue : 0, dwTimeout = 0
            };
            FlashWindowEx(ref fw);
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    private const uint FLASHW_STOP = 0, FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 0x2;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
}
