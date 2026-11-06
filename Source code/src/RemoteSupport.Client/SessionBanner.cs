using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// A small always-visible strip in the bottom-right corner telling the person at the machine that
/// someone is connected to it. The tray window can be minimised or hidden behind other windows,
/// so the chat log's "session started" line is not on its own an honest disclosure — this is.
///
/// It must never take focus: the customer may be typing when a technician connects, and stealing
/// a keystroke would be both rude and dangerous. Hence WS_EX_NOACTIVATE and ShowWithoutActivation.
/// It is also a tool window, so it never appears in the taskbar or in Alt-Tab.
/// </summary>
public sealed class SessionBanner : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int Margin_ = 16;

    private readonly Theme _t;
    private readonly Label _title;
    private readonly Label _detail;
    /// <summary>A third line, used only when something is being done about what the other two
    /// say. "Your technician cannot see this computer" states a problem and stops; without this
    /// the person at the machine has no idea whether to sit still or give up and phone.</summary>
    private readonly Label _note;
    private readonly System.Windows.Forms.Timer _autoHide;
    private Color _dot = Color.Empty;

    private const int BaseHeight = 74;
    private const int NoteHeight = 96;
    /// <summary>The height of the status part above the voice row: BaseHeight or NoteHeight.</summary>
    private int _topHeight = BaseHeight;

    // ---- a voice call (3.3) ----
    // The only part of this strip with buttons: it ASKS the person something — may the technician
    // hear you — rather than telling them. Clicking a button on a no-activate window works and
    // still takes no focus, which is right: they keep whatever they were typing into.
    private readonly Label _voice;
    private readonly RoundButton _voiceYes, _voiceNo;
    private bool _voiceOn;

    /// <summary>The person clicked a voice button: true = use my microphone, false = do not / stop.</summary>
    public event Action<bool>? MicAnswered;

    public SessionBanner(Theme t)
    {
        _t = t;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(320, BaseHeight);
        BackColor = _t.Slate;
        DoubleBuffered = true;

        _title = new Label
        {
            AutoSize = false, Location = new Point(46, 14), Size = new Size(258, 20),
            Font = _t.BodyBold, ForeColor = Color.White, BackColor = Color.Transparent
        };
        _detail = new Label
        {
            AutoSize = false, Location = new Point(46, 36), Size = new Size(258, 26),
            Font = _t.Small, ForeColor = Color.FromArgb(0xC7, 0xD2, 0xEA), BackColor = Color.Transparent
        };
        _note = new Label
        {
            AutoSize = false, Location = new Point(46, 60), Size = new Size(258, 30), Visible = false,
            Font = _t.Small, ForeColor = Color.FromArgb(0xF5, 0xC2, 0x6B), BackColor = Color.Transparent
        };
        Controls.Add(_title);
        Controls.Add(_detail);
        Controls.Add(_note);

        _voice = new Label
        {
            AutoSize = false, Visible = false, UseMnemonic = false,
            Font = _t.Small, ForeColor = Color.White, BackColor = Color.Transparent
        };
        _voiceYes = new RoundButton(_t, BtnStyle.Accent) { Visible = false };
        _voiceNo = new RoundButton(_t, BtnStyle.Neutral) { Visible = false };
        // The answer is the button's own meaning at the moment it is clicked, set with its text in
        // SetVoice — so a label and what it does can never disagree.
        _voiceYes.Click += (_, _) => MicAnswered?.Invoke(true);
        _voiceNo.Click += (_, _) => MicAnswered?.Invoke(false);
        Controls.Add(_voice);
        Controls.Add(_voiceYes);
        Controls.Add(_voiceNo);

        _autoHide = new System.Windows.Forms.Timer { Interval = 8000 };
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); Hide(); };

        // Follow the taskbar if the screen is rearranged or the resolution changes.
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    /// <summary>Never activate on show — the customer keeps whatever they were typing into.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost;
            return cp;
        }
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    { if (!IsDisposed && Visible) Reposition(); }

    /// <summary>Bottom-right of the working area, so it sits above the taskbar rather than under it.</summary>
    private void Reposition()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1024, 768);
        Location = new Point(wa.Right - Width - Margin_, wa.Bottom - Height - Margin_);
    }

    /// <summary>
    /// The third line, and the banner's height with it. Every state sets this — passing null is
    /// how a state says "there is nothing being done about this", which is the normal case.
    /// The detail label gives up a few pixels when a note is present so the two cannot collide.
    /// </summary>
    private void SetNote(string? text)
    {
        bool has = !string.IsNullOrEmpty(text);
        _note.Text = text ?? "";
        _note.Visible = has;
        _detail.Size = new Size(258, has ? 22 : 26);
        _topHeight = has ? NoteHeight : BaseHeight;
        LayoutVoice();
    }

    /// <summary>
    /// The voice row (3.3). <paramref name="text"/> null hides it. <paramref name="yes"/> is the
    /// label of a button that answers "use my microphone"; <paramref name="no"/> of one that answers
    /// "do not" or "stop". Either may be null for no button.
    /// </summary>
    public void SetVoice(string? text, string? yes, string? no)
    {
        _voiceOn = !string.IsNullOrEmpty(text);
        _voice.Text = text ?? "";
        _voiceYes.Text = yes ?? "";
        _voiceNo.Text = no ?? "";
        _voice.Visible = _voiceOn;
        _voiceYes.Visible = _voiceOn && yes != null;
        _voiceNo.Visible = _voiceOn && no != null;
        LayoutVoice();
        if (_voiceOn) Present();
    }

    /// <summary>
    /// Places the voice row under the status part and sizes the strip to hold it. Every height and
    /// width here is MEASURED from its font (§30 of the handover): a button written as 30 px tall
    /// clips its own text at 150%, and on this strip there is no second chance to read it.
    /// </summary>
    private void LayoutVoice()
    {
        int h = _topHeight;
        if (_voiceOn)
        {
            const int left = 20, gap = 8;
            int textWidth = Width - left * 2;
            int textHeight = TextRenderer.MeasureText(_voice.Text, _voice.Font, new Size(textWidth, 0), TextFormatFlags.WordBreak).Height;
            h += 4;   // clear of the note above it (bannerprobe: "connection lost" ran into it)
            _voice.Location = new Point(left, h);
            _voice.Size = new Size(textWidth, textHeight + 2);
            h += textHeight + 8;
            int btnHeight = TextRenderer.MeasureText("Ag", _voiceYes.Font).Height + 14;
            int x = left;
            foreach (var b in new[] { _voiceYes, _voiceNo })
            {
                if (!b.Visible) continue;
                int w = TextRenderer.MeasureText(b.Text, b.Font).Width + 32;
                b.Bounds = new Rectangle(x, h, Math.Min(w, Width - left - x), btnHeight);
                x += w + gap;
            }
            if (_voiceYes.Visible || _voiceNo.Visible) h += btnHeight + 14;
            else h += 6;
        }
        if (Height != h) Height = h;
        if (Visible) { Reposition(); Invalidate(); }
    }

    /// <summary>A technician is connected right now.</summary>
    public void ShowInProgress(string productName, string technicianName)
    {
        _autoHide.Stop();
        SetNote(null);
        _dot = _t.Green;
        _title.Text = "Remote support in progress";
        _detail.Text = string.IsNullOrWhiteSpace(technicianName)
            ? $"{productName} is connected to this computer."
            : $"{technicianName} is connected to this computer.";
        Present();
    }

    /// <summary>
    /// A technician has started listening to this machine's sound.
    ///
    /// This is a disclosure, not a nicety. An unattended machine can be listened to with nobody
    /// in the room, and a machine that can play music can carry a speakerphone call — so the
    /// person at it is told, in words, while it is happening. It deliberately re-presents the
    /// banner even if it had been dismissed behind other windows: the whole point is that it is
    /// visible at the moment the microphone-equivalent is opened.
    /// </summary>
    public void ShowListening(string productName, string technicianName)
    {
        _autoHide.Stop();
        SetNote(null);
        _dot = _t.Orange;
        _title.Text = "Your sound is being listened to";
        _detail.Text = string.IsNullOrWhiteSpace(technicianName)
            ? $"{productName} can hear this computer's sound."
            : $"{technicianName} can hear this computer's sound.";
        Present();
    }

    /// <summary>
    /// The technician's connection has dropped and the server is holding the session open for a
    /// short while. Said plainly rather than left as "in progress": for as long as this lasts,
    /// nobody is looking at this screen, and the person at the machine is the only one who
    /// cannot tell that from the outside.
    /// </summary>
    public void ShowHeld(string productName, string technicianName)
    {
        _autoHide.Stop();
        _dot = _t.Orange;
        _title.Text = "Connection to your technician lost";
        _detail.Text = string.IsNullOrWhiteSpace(technicianName)
            ? "Nobody can see this computer at the moment."
            : $"{technicianName} cannot see this computer at the moment.";
        // The two lines above state a problem and stop there, which leaves the person at the
        // machine with no idea whether to sit still or give up and phone. This says what is being
        // done and what is expected of them, and it is true for as long as it is shown: the
        // server holds the session for ninety seconds and the console is retrying throughout. If
        // nobody comes back the session ends and ShowEnded replaces this.
        SetNote("Attempting to reconnect — please wait.\nYour session is still open.");
        Present();
    }

    /// <summary>The session has finished. Says so, then takes itself away.</summary>
    public void ShowEnded(string productName)
    {
        // A call cannot outlive its session; the host has already closed the microphone.
        SetVoice(null, null, null);
        SetNote(null);
        _dot = _t.Muted;
        _title.Text = "Remote support session ended";
        _detail.Text = "Nobody is connected to this computer.";
        Present();
        _autoHide.Start();   // the good news does not need to sit there forever
    }

    private void Present()
    {
        Reposition();
        if (!Visible) Show();
        Invalidate();
        // Re-assert topmost: a full-screen app that came up after us can otherwise cover it.
        try { TopMost = false; TopMost = true; } catch { }
    }

    public void HideNow() { _autoHide.Stop(); if (Visible) Hide(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var border = new Pen(Color.FromArgb(0x33, 0x41, 0x5A)))
        using (var bg = new SolidBrush(_t.Slate))
        {
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Rounded(r, 12);
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        // status dot — green while connected, grey once finished
        if (_dot != Color.Empty)
        {
            using var dot = new SolidBrush(_dot);
            // Beside the status lines, not the middle of the strip: the voice row grows it downwards.
            g.FillEllipse(dot, 20, _topHeight / 2 - 6, 12, 12);
        }
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
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _autoHide.Dispose();
        }
        base.Dispose(disposing);
    }
}
