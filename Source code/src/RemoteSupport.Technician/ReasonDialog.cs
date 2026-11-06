using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// "Why are you connecting to this machine?" — asked before every unattended session, and the
/// answer becomes the ticket's description.
///
/// It is the other half of 3.5.7. A customer asking for help has had to say what is wrong since
/// then; a technician letting themselves into somebody's machine with nobody there should have to
/// say why at least as much. The ticket exists either way — this decides whether it says anything.
///
/// Deliberately the same shape as the customer's own `RequestDialog`: the complaint appears inside
/// the window rather than in a message box on top of it, and clears the moment they start typing.
/// A message box to say "you did not fill in the box" is two dismissals for one mistake.
/// </summary>
public sealed class ReasonDialog : Form
{
    /// <summary>
    /// Three characters, matching the customer's dialog and the server's own floor.
    ///
    /// Any rule strong enough to reject "asdf" also rejects somebody in a hurry writing something
    /// true and short, and the person it would obstruct is by definition already busy. It exists
    /// to stop a keystroke dismissing the question, not to grade the answer (§30).
    /// </summary>
    public const int MinLength = 3;

    private readonly InputBox _reason;
    private readonly InputBox? _password;
    private readonly Label _complaint;
    private readonly bool _askReason;

    public string Reason { get; private set; } = "";
    /// <summary>The machine's unattended password as typed, or empty when it was not asked for.</summary>
    public string Password { get; private set; } = "";

    /// <param name="askPassword">The machine has an unattended password (2026-10-02): a box for it
    /// goes under the reason, in the SAME window — one question, not two dialogs in a row.</param>
    /// <param name="reason">What was written last time, when the server refused the password and
    /// this is asking again — so a wrong password never costs them the reason too.</param>
    /// <param name="complaint">The server's words, shown at once.</param>
    /// <param name="askReason">False on the free version (2026-10-06): it has no tickets, so there is nothing for a
    /// reason to describe, and the window asks only the password.</param>
    public ReasonDialog(Theme t, string machineName, string ownerName, bool askPassword = false, string reason = "", string complaint = "",
        bool askReason = true)
    {
        _askReason = askReason;
        Text = askReason ? "Why are you connecting?" : "Unattended password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        if (BrandIcon.Current is { } icon) Icon = icon;   // a fixed dialog takes no default icon
        Font = t.Body; BackColor = t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, t);

        var canvas = new CanvasPanel(t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);

        var card = new Card(t, 18) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        // Every height below is measured from the font it will draw with. §30 of the handover:
        // three faults in a row came from pixel constants written against a 100% display, two of
        // them invisible on the developer's own screen.
        int line = TextRenderer.MeasureText("Ag", t.Body).Height;
        int headLine = TextRenderer.MeasureText("Ag", t.H3).Height;

        var who = string.IsNullOrWhiteSpace(ownerName) || ownerName == "—" ? machineName : $"{ownerName} · {machineName}";
        var head = new Label
        {
            Dock = DockStyle.Top, Text = who, Font = t.H3, ForeColor = t.Ink,
            BackColor = Color.Transparent, Height = headLine + 6, UseMnemonic = false
        };
        var hint = new Label
        {
            Dock = DockStyle.Top,
            Text = "Nobody has asked for this session, so it needs a reason. What you write here becomes the ticket's description.",
            Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent,
            // Two lines of the small font, because that sentence is two lines at any sane width.
            Height = TextRenderer.MeasureText("Ag", t.Small).Height * 2 + 6
        };

        _complaint = new Label
        {
            Dock = DockStyle.Bottom, Text = "", Font = t.Small, ForeColor = Color.FromArgb(0xC0, 0x39, 0x2B),
            BackColor = Color.Transparent, Visible = false,
            // Two lines when a password is asked: the server's "wrong password" words are long.
            Height = TextRenderer.MeasureText("Ag", t.Small).Height * (askPassword ? 2 : 1) + 4
        };

        _reason = new InputBox(t, multiline: true, height: line * 3 + 14) { Dock = DockStyle.Top };
        _reason.Box.TextChanged += (_, _) => { if (_complaint.Visible) { _complaint.Visible = false; } };
        if (reason.Length > 0) _reason.Box.Text = reason;

        Label? pwLabel = null; Panel? pwGap = null;
        if (askPassword)
        {
            pwGap = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent };
            pwLabel = new Label
            {
                Dock = DockStyle.Top, Text = "This machine has an unattended password. Type it to connect.",
                Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent,
                Height = TextRenderer.MeasureText("Ag", t.Small).Height + 6, UseMnemonic = false
            };
            _password = new InputBox(t, password: true) { Dock = DockStyle.Top };
            // From the box's own font, not a constant (§30).
            _password.Height = _password.Box.PreferredHeight + 16;
            _password.Box.TextChanged += (_, _) => { if (_complaint.Visible) { _complaint.Visible = false; } };
        }
        if (complaint.Length > 0) { _complaint.Text = complaint; _complaint.Visible = true; }

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            BackColor = Color.Transparent, Height = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 26,
            Padding = new Padding(0, 8, 0, 0)
        };
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 16;
        var ok = new RoundButton(t, BtnStyle.Accent) { Text = "Connect", Size = new Size(140, btnH), Margin = new Padding(0) };
        var cancel = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = new Size(100, btnH), DialogResult = DialogResult.Cancel, Margin = new Padding(10, 0, 0, 0) };
        ok.Click += (_, _) => Submit();
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);

        // Docked to the top, so the LAST added sits highest: the password goes in first to sit lowest.
        if (_password != null) { card.Controls.Add(_password); card.Controls.Add(pwLabel); card.Controls.Add(pwGap); }
        if (askReason) { card.Controls.Add(_reason); card.Controls.Add(hint); }
        card.Controls.Add(head);
        card.Controls.Add(_complaint);
        card.Controls.Add(buttons);

        AcceptButton = ok; CancelButton = cancel;
        // Sized from its own contents rather than a guessed constant, so it fits at any scaling.
        ClientSize = new Size(560, canvas.Padding.Vertical + card.Padding.Vertical
            + head.Height + (askReason ? hint.Height + _reason.Height : 0) + _complaint.Height + buttons.Height + 10
            + (_password == null ? 0 : pwGap!.Height + pwLabel!.Height + _password.Height));
        // Asked again with the reason kept: straight to the password.
        Shown += (_, _) => { try { (_password != null && (reason.Length > 0 || !askReason) ? _password.Box : _reason.Box).Focus(); } catch { } };
    }

    private void Submit()
    {
        var text = _reason.Value.Trim();
        if (_askReason && text.Length < MinLength)
        {
            _complaint.Text = "Please say why you are connecting.";
            _complaint.Visible = true;
            try { _reason.Box.Focus(); } catch { }
            return;
        }
        if (_password != null && _password.Box.Text.Length == 0)
        {
            _complaint.Text = "Type the machine's unattended password.";
            _complaint.Visible = true;
            try { _password.Box.Focus(); } catch { }
            return;
        }
        Reason = text;
        Password = _password?.Box.Text ?? "";
        DialogResult = DialogResult.OK;
        Close();
    }
}
