using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// The second step of signing in to the console (2026-10-01): the six digits from an
/// authenticator app — or, the first time, setting one up by scanning a QR code. On success the
/// link holds its token and the dialog closes OK; after a set-up it shows the recovery codes once
/// first (<see cref="RecoveryCodesDialog"/>).
///
/// The QR code comes from the server, already drawn (QrImage), so the console carries no QR encoder.
///
/// Laid out by flow and AutoSize, every height taken from the control's own font — HANDOVER §30:
/// do not write a control height as a number. The window is as tall as what it holds.
/// </summary>
public sealed class TwoFactorDialog : Form
{
    private readonly TechLink _link;
    private readonly Theme _t;
    private TechLink.LoginStep _step;
    private readonly TextBox _code;
    private readonly Label _msg;
    private readonly RoundButton _go;

    public TwoFactorDialog(Theme t, TechLink link, TechLink.LoginStep step)
    {
        _t = t; _link = link; _step = step;
        bool enrol = step.Next == "enrol";
        Text = enrol ? "Set up two-factor sign-in" : "Two-factor sign-in";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = t.Mist; Font = t.Body;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Shown += (_, _) => NativeDark.Apply(this, t);

        const int width = 380;
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24, 20, 24, 20), BackColor = t.Mist
        };
        Controls.Add(flow);

        Label Text_(string s, Font f, Color c) => new()
        {
            Text = s, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(width, 0),
            UseMnemonic = false, Margin = new Padding(0, 0, 0, 10), BackColor = Color.Transparent
        };

        flow.Controls.Add(Text_(enrol ? "Set up two-factor sign-in" : "Enter your code", t.H2, t.Ink));
        if (enrol)
        {
            flow.Controls.Add(Text_("Your account now needs a code from an authenticator app as well as your password. " +
                "Install Microsoft Authenticator or Google Authenticator on your phone, tap Add account, and scan this:", t.Body, t.Ink));
            if (step.QrPng.Length > 0)
            {
                Image? qr = null;
                try { using var ms = new MemoryStream(step.QrPng); qr = Image.FromStream(ms); } catch { }
                if (qr != null)
                {
                    // The code is drawn on white whatever the theme: phones read dark-on-light.
                    var box = new PictureBox
                    {
                        Image = qr, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(220, 220), BackColor = Color.White,
                        Padding = new Padding(8), Margin = new Padding((width - 220) / 2, 0, 0, 10)
                    };
                    flow.Controls.Add(box);
                }
            }
            flow.Controls.Add(Text_("Can't scan? Choose \"enter a key\" in the app and type:", t.Small, t.Muted));
            var key = new TextBox
            {
                Text = step.SecretGrouped, ReadOnly = true, Font = t.Mono, Width = width,
                BackColor = t.Surface, ForeColor = t.Ink, Margin = new Padding(0, 0, 0, 14)
            };
            flow.Controls.Add(new FieldFrame(t, key) { Width = width });   // rounded, like every field (2026-10-06)
            flow.Controls.Add(Text_("Then type the six-digit code the app shows:", t.BodyBold, t.Ink));
        }
        else
        {
            // Asked even on a trusted PC: they are signed in on another computer (2026-10-05).
            if (step.Notice.Length > 0) flow.Controls.Add(Text_(step.Notice, t.BodyBold, t.Ink));
            flow.Controls.Add(Text_("Type the six-digit code from your authenticator app. This PC then asks only for your password for the next 7 days. Lost your phone? Type one of your recovery codes instead.", t.Body, t.Ink));
        }

        _code = new TextBox
        {
            Font = new Font("Segoe UI", 16f), Width = width, MaxLength = 16,
            BackColor = t.Surface, ForeColor = t.Ink, TextAlign = HorizontalAlignment.Center, Margin = new Padding(0, 0, 0, 8)
        };
        flow.Controls.Add(new FieldFrame(t, _code) { Width = width });

        _msg = Text_(step.Error, t.Body, t.BadText);
        _msg.MinimumSize = new Size(width, 0);
        flow.Controls.Add(_msg);

        _go = new RoundButton(t, BtnStyle.Accent)
        {
            Text = enrol ? "Finish setting up" : "Sign in", Width = width,
            Height = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 22, Font = t.ButtonFont, Margin = new Padding(0)
        };
        _go.Click += async (_, _) => await Submit();
        flow.Controls.Add(_go);
        AcceptButton = _go;
        Shown += (_, _) => _code.Focus();
    }

    private async Task Submit()
    {
        var code = _code.Text.Trim();
        if (code.Length == 0) { _msg.Text = "Type the code first."; return; }
        _go.Enabled = false; _msg.ForeColor = _t.Muted; _msg.Text = "Checking…";
        var r = _step.Next == "enrol" ? await _link.EnrolAsync(_step.Pending, code) : await _link.CodeAsync(_step.Pending, code);
        _go.Enabled = true;
        if (r.Ok)
        {
            if (r.RecoveryCodes.Count > 0)
            {
                using var saved = new RecoveryCodesDialog(_t, r.RecoveryCodes);
                saved.ShowDialog(this);
            }
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        _msg.ForeColor = _t.BadText;
        if (r.Next.Length == 0)
        {
            // Refused outright: expired, too many wrong codes, or locked. Back to the password.
            MessageBox.Show(this, r.Error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }
        // Same sign-in, another try: the server keeps the pending id; the QR code on screen stays.
        _step = new TechLink.LoginStep { Next = r.Next, Pending = r.Pending, QrPng = _step.QrPng, SecretGrouped = _step.SecretGrouped };
        _msg.Text = r.Error.Length > 0 ? r.Error : "That code is not right.";
        _code.SelectAll(); _code.Focus();
    }
}

/// <summary>
/// The ten recovery codes, once, straight after setting up an authenticator. Copy puts them on the
/// clipboard for a password manager; the window will not close by Enter alone, so they cannot be
/// dismissed unread by the same keypress that finished the set-up.
/// </summary>
public sealed class RecoveryCodesDialog : Form
{
    public RecoveryCodesDialog(Theme t, IReadOnlyList<string> codes)
    {
        Text = "Save your recovery codes";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = t.Mist; Font = t.Body;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Shown += (_, _) => NativeDark.Apply(this, t);

        const int width = 380;
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24, 20, 24, 20), BackColor = t.Mist
        };
        Controls.Add(flow);
        flow.Controls.Add(new Label { Text = "Two-factor sign-in is set up", Font = t.H2, ForeColor = t.OkText, AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
        flow.Controls.Add(new Label
        {
            Text = "Save these recovery codes somewhere safe — a password manager, or printed and locked away. " +
                   "If you lose your phone, each one lets you sign in once in place of a code. They will not be shown again.",
            AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = t.Ink, Margin = new Padding(0, 0, 0, 10)
        });
        var list = new TextBox
        {
            Text = string.Join(Environment.NewLine, codes), Multiline = true, ReadOnly = true, Font = new Font("Consolas", 12f),
            Width = width, BackColor = t.Surface, ForeColor = t.Ink, Margin = new Padding(0, 0, 0, 12)
        };
        list.Height = TextRenderer.MeasureText(list.Text, list.Font).Height + 12;
        flow.Controls.Add(new FieldFrame(t, list) { Width = width });

        int bh = TextRenderer.MeasureText("Ag", t.ButtonFont).Height + 22;
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = t.Mist };
        var copy = new RoundButton(t, BtnStyle.Ghost) { Text = "Copy", Width = 120, Height = bh, Font = t.ButtonFont, Margin = new Padding(0, 0, 10, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(list.Text); copy.Text = "Copied"; } catch { } };
        var done = new RoundButton(t, BtnStyle.Accent) { Text = "I have saved them", Width = width - 130, Height = bh, Font = t.ButtonFont, Margin = new Padding(0) };
        done.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        row.Controls.Add(copy); row.Controls.Add(done);
        flow.Controls.Add(row);
        Shown += (_, _) => copy.Focus();
    }
}
