using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// "Connect with a code…" (roadmap 3.9, step 2, 2026-10-06): a technician helping ANOTHER company's
/// customer — Clint: "two signed up tenants may compliment each other in the work they do" — types the
/// code that customer made. Signed in as themselves, so the customer's prompt and the other company's
/// record name a real person at a real company. Every height measured from its font (§30).
/// </summary>
public sealed class CodeEntryDialog : Form
{
    private readonly InputBox _code;
    private readonly Label _hint;

    /// <summary>The nine digits, once Connect has accepted them.</summary>
    public string Code { get; private set; } = "";

    public CodeEntryDialog(Theme t)
    {
        Text = "Connect with a code";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 18;
        Size Fit(string text, int least) => new(Math.Max(least, TextRenderer.MeasureText(text, t.ButtonFont).Width + 36), btnH);
        int width = 500, inner = width - canvas.Padding.Horizontal - card.Padding.Horizontal;
        int Lines(string text, Font f) => TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;
        Label Text_(string s, Font f, Color c) { var l = new Label { Text = s, Dock = DockStyle.Top, Font = f, ForeColor = c, BackColor = Color.Transparent, UseMnemonic = false }; l.Height = Lines(s, f); return l; }

        var head = Text_("Connect with a code", t.H2, t.Ink);
        var about = Text_("For a computer that is NOT one of your company's: type the nine-digit code its owner gave you. "
            + "They will be asked to allow you, by your name and your company's, and their IT company can watch and end it. "
            + "You get the screen, mouse and keyboard — no files or tools.", t.Small, t.Muted);
        var codeFont = new Font("Segoe UI", 18f, FontStyle.Bold);
        _code = new InputBox(t) { Dock = DockStyle.Top };
        _code.Box.Font = codeFont;
        _code.Box.PlaceholderText = "123 456 789";
        _code.Box.MaxLength = 15;
        _code.Height = _code.Box.PreferredHeight + 16;
        _hint = new Label { Text = "", Dock = DockStyle.Top, Font = t.Small, ForeColor = t.BadText, BackColor = Color.Transparent, UseMnemonic = false };
        _hint.Height = Lines("Ag\nAg", t.Small);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = btnH + 12, BackColor = Color.Transparent,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0)
        };
        var go = new RoundButton(t, BtnStyle.Accent) { Text = "Connect", Size = Fit("Connect", 120), Margin = new Padding(0) };
        var cancel = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = Fit("Cancel", 100), DialogResult = DialogResult.Cancel, Margin = new Padding(0, 0, 10, 0) };
        go.Click += (_, _) => Submit();
        buttons.Controls.Add(go); buttons.Controls.Add(cancel);

        // Top-docked: the LAST added sits highest.
        card.Controls.Add(_hint); card.Controls.Add(_code); card.Controls.Add(about); card.Controls.Add(head);
        card.Controls.Add(buttons);
        ClientSize = new Size(width, canvas.Padding.Vertical + card.Padding.Vertical + head.Height + about.Height + _code.Height + _hint.Height + buttons.Height + 8);

        AcceptButton = go; CancelButton = cancel;
        _code.Box.TextChanged += (_, _) => _hint.Text = "";
        Shown += (_, _) => { try { _code.Box.Focus(); } catch { } };
    }

    /// <summary>What is wrong with what was typed, or empty — the server's own reading of it (digits only).</summary>
    public static string Problem(string typed)
    {
        var digits = OutsiderAccess.Clean(typed);
        if (digits.Length == 0) return "Type the code they gave you.";
        return digits.Length == OutsiderAccess.Digits ? "" : $"A code has {OutsiderAccess.Digits} digits. That has {digits.Length}.";
    }

    private void Submit()
    {
        var problem = Problem(_code.Box.Text);
        if (problem.Length > 0) { _hint.Text = problem; _code.Box.Focus(); return; }
        Code = OutsiderAccess.Clean(_code.Box.Text);
        DialogResult = DialogResult.OK;
    }
}
