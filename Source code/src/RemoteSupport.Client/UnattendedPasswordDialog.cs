using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// Setting, changing or removing this machine's unattended password (2026-10-02).
///
/// Clint: handy for somebody connecting to their own personal machine from the office. With
/// unattended access on and no password, every technician at the company can get in; with one,
/// only somebody who types it can — the owner and supervisors included, by his decision.
///
/// Typed twice, because nobody can read it back: the server keeps only a one-way hash. A forgotten
/// one is removed HERE, which needs nothing but being at the machine.
/// </summary>
public sealed class UnattendedPasswordDialog : Form
{
    public enum Outcome { None, Set, Remove }

    private readonly InputBox _pw;
    private readonly InputBox _again;
    private readonly Label _hint;

    public Outcome Choice { get; private set; }
    public string Password => _pw.Box.Text;

    public UnattendedPasswordDialog(Theme t, Branding brand, bool hasPassword, bool unattendedOn)
    {
        Text = "Unattended password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 18;
        // Widths from the words too: "Remove password" in a 170 px constant was cut off at 150%.
        Size Fit(string text, int least) => new(Math.Max(least, TextRenderer.MeasureText(text, t.ButtonFont).Width + 36), btnH);
        // And the window as wide as its row of buttons needs: three buttons that each fit their words
        // still ran off the left of a 480 px window at 150%.
        int row = Fit("Save", 110).Width + 10 + Fit("Cancel", 100).Width + (hasPassword ? 10 + Fit("Remove password", 150).Width : 0);
        int width = Math.Max(480, row + canvas.Padding.Horizontal + card.Padding.Horizontal + 8);
        int inner = width - canvas.Padding.Horizontal - card.Padding.Horizontal;
        // Every height from the font it draws with (§30): a constant written at 100% cuts text in
        // half at 150%.
        int Lines(string text, Font f) =>
            TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;

        var head = new Label
        {
            Text = hasPassword ? "Change the password" : "Set a password",
            Dock = DockStyle.Top, Font = t.H2, ForeColor = t.Ink, BackColor = Color.Transparent, UseMnemonic = false
        };
        head.Height = Lines(head.Text, t.H2) + 4;

        var about = new Label
        {
            Text = $"Technicians at {brand.CompanyName} who connect to this computer without you asking for "
                 + "support will have to type this password. Nobody there can see it or get round it — "
                 + "if it is forgotten, remove it here."
                 + (unattendedOn ? "" : "\n\nIt only matters while unattended access is allowed."),
            Dock = DockStyle.Top, Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent, UseMnemonic = false
        };
        about.Height = Lines(about.Text, t.Small) + 6;

        Label Caption(string s) => new()
        {
            Text = s, Dock = DockStyle.Top, Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent,
            UseMnemonic = false, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4),
            Height = TextRenderer.MeasureText("Ag", t.BodyBold).Height + 12
        };
        var pwLbl = Caption(hasPassword ? "New password" : "Password");
        _pw = new InputBox(t, password: true) { Dock = DockStyle.Top };
        _pw.Height = _pw.Box.PreferredHeight + 16;
        var againLbl = Caption("Type it again");
        _again = new InputBox(t, password: true) { Dock = DockStyle.Top };
        _again.Height = _again.Box.PreferredHeight + 16;

        _hint = new Label
        {
            Text = "", Dock = DockStyle.Bottom, Font = t.Small, ForeColor = t.BadText,
            BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
            Height = TextRenderer.MeasureText("Ag", t.Small).Height + 8
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = btnH + 12, BackColor = Color.Transparent,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0)
        };
        // No DialogResult on Save: it has to be able to object first.
        var save = new RoundButton(t, BtnStyle.Accent) { Text = "Save", Size = Fit("Save", 110), Margin = new Padding(0) };
        var cancel = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = Fit("Cancel", 100), DialogResult = DialogResult.Cancel, Margin = new Padding(10, 0, 0, 0) };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        if (hasPassword)
        {
            // Removing asks nothing more, as switching unattended access off asks nothing:
            // withdrawing protection from your own machine is yours to do.
            var remove = new RoundButton(t, BtnStyle.Ghost) { Text = "Remove password", Size = Fit("Remove password", 150), Margin = new Padding(10, 0, 0, 0) };
            remove.Click += (_, _) => { Choice = Outcome.Remove; DialogResult = DialogResult.OK; };
            buttons.Controls.Add(remove);
        }

        // Docked to the top, so the LAST added sits highest.
        card.Controls.Add(_again);
        card.Controls.Add(againLbl);
        card.Controls.Add(_pw);
        card.Controls.Add(pwLbl);
        card.Controls.Add(about);
        card.Controls.Add(head);
        card.Controls.Add(buttons);
        card.Controls.Add(_hint);

        ClientSize = new Size(width, canvas.Padding.Vertical + card.Padding.Vertical + head.Height + about.Height
            + pwLbl.Height + _pw.Height + againLbl.Height + _again.Height + _hint.Height + buttons.Height + 12);

        save.Click += (_, _) => Submit();
        AcceptButton = save; CancelButton = cancel;
        _pw.Box.TextChanged += (_, _) => _hint.Text = "";
        _again.Box.TextChanged += (_, _) => _hint.Text = "";
        _pw.NextField(_again.Box);
        Shown += (_, _) => { try { _pw.Box.Focus(); } catch { } };
    }

    /// <summary>Why these two boxes cannot be saved, or empty — the server's own rule first, so
    /// the two can never disagree. Public for tools/unattendedcheck.</summary>
    public static string Problem(string pw, string again)
    {
        var p = UnattendedPasswords.Problem(pw);
        if (p.Length > 0) return p;
        return pw == again ? "" : "The two passwords are not the same.";
    }

    private void Submit()
    {
        var problem = Problem(_pw.Box.Text, _again.Box.Text);
        if (problem.Length > 0)
        {
            _hint.Text = problem;
            try { (_pw.Box.Text.Length < UnattendedPasswords.MinLength ? _pw.Box : _again.Box).Focus(); } catch { }
            return;
        }
        Choice = Outcome.Set;
        DialogResult = DialogResult.OK;
    }
}
