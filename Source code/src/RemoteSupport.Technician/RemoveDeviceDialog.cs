using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// Removing a machine from the company's list, on the full version (2026-10-06). Clint: "this is important
/// for when machines get upgraded, replaced or a person has been using their own private device." His
/// answers: a supervisor or the owner; a reason picked from a list, with a note (required for Other); the
/// history kept; a machine that is online uninstalls itself. The rule is <see cref="RemovalReasons"/>, asked
/// here first and by the hub again — the server is what decides.
/// </summary>
public sealed class RemoveDeviceDialog : Form
{
    private readonly ThemedCombo _reason;
    private readonly InputBox _note;
    private readonly Label _complaint;

    public string Reason => _reason.SelectedItem as string ?? "";
    public string Note => _note.Value.Trim();

    public RemoveDeviceDialog(Theme t, string machineName, bool online)
    {
        Text = "Remove a machine";
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

        const int width = 520;
        int inner = width - canvas.Padding.Horizontal - card.Padding.Horizontal;
        // Every height from the font it draws with (§30).
        int Lines(string text, Font f) => TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;
        int line = TextRenderer.MeasureText("Ag", t.Body).Height;

        var head = new Label { Dock = DockStyle.Top, Text = $"Remove {machineName}", Font = t.H3, ForeColor = t.Ink, BackColor = Color.Transparent, UseMnemonic = false };
        head.Height = Lines(head.Text, t.H3);
        var about = new Label
        {
            Dock = DockStyle.Top, Font = t.Small, ForeColor = t.Muted, BackColor = Color.Transparent, UseMnemonic = false,
            Text = (online
                    ? "It is online, so the support program uninstalls itself from it now."
                    : "It is offline: it will be refused when it next connects, and the support program stays on it until somebody removes it.")
                 + " Its tickets, sessions and reports are kept. To add it again, install the support program on it again."
        };
        about.Height = Lines(about.Text, t.Small) + 4;

        Label Caption(string s) => new()
        {
            Text = s, Dock = DockStyle.Top, Font = t.BodyBold, ForeColor = t.Ink, BackColor = Color.Transparent,
            UseMnemonic = false, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4),
            Height = TextRenderer.MeasureText("Ag", t.BodyBold).Height + 12
        };
        var reasonLbl = Caption("Why?");
        _reason = new ThemedCombo(t) { Dock = DockStyle.Top, Font = t.Body };
        foreach (var r in RemovalReasons.All) _reason.Items.Add(r);
        var noteLbl = Caption("Note (needed for Other)");
        _note = new InputBox(t, multiline: true, height: line * 3 + 14) { Dock = DockStyle.Top };

        _complaint = new Label
        {
            Dock = DockStyle.Bottom, Text = "", Font = t.Small, ForeColor = t.BadText, BackColor = Color.Transparent,
            UseMnemonic = false, Height = TextRenderer.MeasureText("Ag", t.Small).Height * 2 + 6
        };
        int btnH = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 16;
        Size Fit(string s, int least) => new(Math.Max(least, TextRenderer.MeasureText(s, t.ButtonFont).Width + 36), btnH);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            BackColor = Color.Transparent, Height = btnH + 10, Padding = new Padding(0, 8, 0, 0)
        };
        var ok = new RoundButton(t, BtnStyle.Danger) { Text = "Remove", Size = Fit("Remove", 120), Margin = new Padding(0) };
        var cancel = new RoundButton(t, BtnStyle.Ghost) { Text = "Cancel", Size = Fit("Cancel", 100), DialogResult = DialogResult.Cancel, Margin = new Padding(10, 0, 0, 0) };
        ok.Click += (_, _) => Submit();
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);

        // Docked to the top, so the LAST added sits highest.
        card.Controls.Add(_note);
        card.Controls.Add(noteLbl);
        card.Controls.Add(_reason);
        card.Controls.Add(reasonLbl);
        card.Controls.Add(about);
        card.Controls.Add(head);
        card.Controls.Add(buttons);
        card.Controls.Add(_complaint);

        ClientSize = new Size(width, canvas.Padding.Vertical + card.Padding.Vertical + head.Height + about.Height
            + reasonLbl.Height + _reason.Height + noteLbl.Height + _note.Height + _complaint.Height + buttons.Height + 10);
        AcceptButton = ok; CancelButton = cancel;
        _reason.SelectedIndexChanged += (_, _) => _complaint.Text = "";
        _note.Box.TextChanged += (_, _) => _complaint.Text = "";
        Shown += (_, _) => { try { _reason.Focus(); } catch { } };
    }

    private void Submit()
    {
        var problem = RemovalReasons.Problem(Reason, Note);
        if (problem.Length > 0) { _complaint.Text = problem; return; }
        DialogResult = DialogResult.OK;
        Close();
    }
}
