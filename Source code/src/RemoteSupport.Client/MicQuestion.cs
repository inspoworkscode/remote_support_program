using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// "Jason is talking to you. Would you like them to hear you too?" — in the middle of the screen.
///
/// Internal testing, 2026-10-02: the question used to be asked only on the corner banner, and the
/// Windows notification announcing the call landed on top of it, covering the very buttons it was
/// pointing at. A question the person has to answer belongs where they are looking, so it is a
/// window of its own, centred and on top. The banner keeps saying what is happening; this asks.
///
/// It is not modal and has no default button: somebody typing when it appears must not answer it
/// with the Enter key they meant for something else. Closing it with the X is "Not now".
/// </summary>
public sealed class MicQuestion : Form
{
    private readonly Label _text;
    private bool _answered;

    /// <summary>true = use my microphone, false = not now.</summary>
    public event Action<bool>? Answered;

    public MicQuestion(Theme t, string productName)
    {
        Text = productName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = true; TopMost = true;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(t, 20) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        // Measured from the fonts (§30): written as numbers these clip at 150%.
        const int width = 440;
        int headLine = TextRenderer.MeasureText("Ag", t.H3).Height;
        int bodyLine = TextRenderer.MeasureText("Ag", t.Body).Height;
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 18;

        var head = new Label
        {
            Dock = DockStyle.Top, Text = "Can your technician hear you?", Font = t.H3, ForeColor = t.Ink,
            BackColor = Color.Transparent, Height = headLine + 10, UseMnemonic = false
        };
        _text = new Label
        {
            Dock = DockStyle.Top, Font = t.Body, ForeColor = t.Ink, BackColor = Color.Transparent,
            Height = bodyLine * 3 + 10, UseMnemonic = false
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            BackColor = Color.Transparent, Height = btnH + 12, Padding = new Padding(0, 10, 0, 0)
        };
        var yes = new RoundButton(t, BtnStyle.Accent) { Text = "Use my microphone", Margin = new Padding(0) };
        var no = new RoundButton(t, BtnStyle.Ghost) { Text = "Not now", Margin = new Padding(10, 0, 0, 0) };
        foreach (var b in new[] { yes, no })
            b.Size = new Size(TextRenderer.MeasureText(b.Text, t.ButtonFont).Width + 40, btnH);
        yes.Click += (_, _) => Answer(true);
        no.Click += (_, _) => Answer(false);
        buttons.Controls.Add(yes);
        buttons.Controls.Add(no);

        card.Controls.Add(_text);
        card.Controls.Add(head);
        card.Controls.Add(buttons);

        ClientSize = new Size(width + canvas.Padding.Horizontal + card.Padding.Horizontal,
            canvas.Padding.Vertical + card.Padding.Vertical + head.Height + _text.Height + buttons.Height + 6);
    }

    /// <summary>Who is talking, in the words the banner uses.</summary>
    public void Ask(string who)
    {
        _text.Text = $"{who} is talking to you through this computer's speakers. Would you like them to hear you too? "
                   + "Your microphone stays off unless you say yes.";
        _answered = false;
        if (Visible) return;   // already in front of them: a repeat of the same question is not news
        Show();
        // On top of a full-screen window that came up after it, and brought to them.
        try { TopMost = false; TopMost = true; Activate(); } catch { }
    }

    /// <summary>Taken away without an answer — the call ended, or they answered on the banner.</summary>
    public void Withdraw() { _answered = true; if (Visible) Hide(); }

    private void Answer(bool allow)
    {
        if (_answered) return;
        _answered = true;
        Hide();
        Answered?.Invoke(allow);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X is "Not now", and the window is kept for the next question.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Answer(false); return; }
        base.OnFormClosing(e);
    }
}
