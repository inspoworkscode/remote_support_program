using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// What a downloaded copy shows on a FREE company's computer (2026-10-06, docs/NEXT-SESSION-FREE.md Stage
/// D), in place of <see cref="LauncherForm"/>: no "Get help now" — the free version has no attended
/// access — only "add this computer", with the unattended password typed twice.
///
/// <para>Clint's answers: the password is chosen here, at install; unattended access is switched on
/// here; nothing of ours stays on the screen afterwards; changing the password is running this again.
/// In order: ask the server whether this computer may be added (a third is told why and nothing is
/// installed), install the agent service (the Windows prompt is the only other question), then connect
/// once as this machine's client and set the password (<see cref="FreeSetup"/>).</para>
///
/// <para><b>The Basic version (2026-10-25, HANDOVER §163)</b> connects attended too, so here the password is OPTIONAL: left blank,
/// unattended access is switched off and whoever is at this computer is asked to allow each connection — by the agent service,
/// still with nothing of ours on the screen. The Free version still needs one.</para>
/// </summary>
public sealed class FreeInstallForm : Form
{
    private readonly Theme _t;
    private readonly Branding _brand;
    private readonly string _slug, _origin;
    private readonly InputBox _pw, _again;
    private readonly Label _status;
    private readonly RoundButton _go;
    private bool _busy, _done;

    /// <summary>
    /// Stages and runs the installer with -Free; the exit code, or -1 for "No" to the Windows prompt.
    /// Replaced only by a harness, which must not install on the PC it runs on.
    /// </summary>
    public Func<Task<int>> Installer { get; set; }

    /// <summary>The steps around the install, replaceable for the same reason.</summary>
    public Func<Task<string?>> MayAdd { get; set; }
    public Func<string, Task<string>> SetPassword { get; set; }
    /// <summary>The Basic version with no password: unattended access off (<see cref="FreeSetup.SetAttendedOnlyAsync"/>).</summary>
    public Func<Task<string>> SetAttendedOnly { get; set; }
    /// <summary>The Basic version (§163): the password may be left blank.</summary>
    private readonly bool _basic;
    /// <summary>The server said this computer was installed before and waits for the owner to confirm it (2026-10-07).</summary>
    private bool _awaitingConfirm;

    public FreeInstallForm(Branding brand, string slug, string origin, bool installed)
    {
        _brand = brand; _slug = slug; _origin = origin; _basic = brand.Basic;
        _t = Theme.FromBranding(brand, AppSurface.Customer);
        Text = brand.ProductName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        Font = _t.Body; BackColor = _t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, _t);
        try { if (BrandIcon.Load(brand) is { } ic) Icon = ic; } catch { }

        var canvas = new CanvasPanel(_t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(_t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        int btnH = TextRenderer.MeasureText("Ag", _t.ButtonFont).Height + 18;
        Size Fit(string text, int least) => new(Math.Max(least, TextRenderer.MeasureText(text, _t.ButtonFont).Width + 36), btnH);
        const int width = 560;
        int inner = width - canvas.Padding.Horizontal - card.Padding.Horizontal;
        // Every height from the font it draws with (§30).
        int Lines(string text, Font f) =>
            TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;

        var head = new Label
        {
            Text = installed ? (_basic ? "Change how this computer is reached" : "Change this computer's password") : "Add this computer",
            Dock = DockStyle.Top, Font = _t.H2, ForeColor = _t.Ink, BackColor = Color.Transparent, UseMnemonic = false
        };
        head.Height = Lines(head.Text, _t.H2) + 4;

        var about = new Label
        {
            Text = (_basic
                    ? (installed ? "This computer is already added. " : "So you can reach this computer from your console. ")
                    + "Choose an unattended password to connect with nobody here — you type it once in the console, and it is remembered for you. "
                    + "Or leave both boxes blank: whoever is at this computer is then asked to allow each connection."
                    : installed
                    ? "This computer is already added. Choose its new unattended password — whoever had the old one saved will be asked for the new one."
                    : $"So you can reach this computer from your console, with nobody here. Choose its unattended password: "
                    + "you type it once in the console, and it is remembered for you.")
                 + "\n\nWhile you are connected, a notice saying \"Being controlled remotely\" shows on this screen. "
                 + "Windows will ask for administrator permission.",
            Dock = DockStyle.Top, Font = _t.Small, ForeColor = _t.Muted, BackColor = Color.Transparent, UseMnemonic = false
        };
        about.Height = Lines(about.Text, _t.Small) + 6;

        Label Caption(string s) => new()
        {
            Text = s, Dock = DockStyle.Top, Font = _t.BodyBold, ForeColor = _t.Ink, BackColor = Color.Transparent,
            UseMnemonic = false, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4),
            Height = TextRenderer.MeasureText("Ag", _t.BodyBold).Height + 12
        };
        var pwLbl = Caption(_basic ? $"Unattended password ({UnattendedPasswords.MinLength}+ characters, or blank)" : $"Unattended password ({UnattendedPasswords.MinLength}+ characters)");
        _pw = new InputBox(_t, password: true) { Dock = DockStyle.Top };
        _pw.Height = _pw.Box.PreferredHeight + 16;
        var againLbl = Caption("Type it again");
        _again = new InputBox(_t, password: true) { Dock = DockStyle.Top };
        _again.Height = _again.Box.PreferredHeight + 16;

        // Room for what the server or the installer may say, which can be two or three lines.
        _status = new Label
        {
            Text = "", Dock = DockStyle.Bottom, Font = _t.Small, ForeColor = _t.Ink, BackColor = Color.Transparent,
            UseMnemonic = false, TextAlign = ContentAlignment.TopLeft, Padding = new Padding(0, 8, 0, 0),
            Height = Lines("a\nb\nc", _t.Small) + 8
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = btnH + 12, BackColor = Color.Transparent,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0)
        };
        var goText = installed ? (_basic ? "Save" : "Change password") : "Add this computer";
        _go = new RoundButton(_t, BtnStyle.Accent) { Text = goText, Size = Fit(goText, 150), Margin = new Padding(0) };
        var cancel = new RoundButton(_t, BtnStyle.Ghost) { Text = "Cancel", Size = Fit("Cancel", 100), Margin = new Padding(10, 0, 0, 0) };
        cancel.Click += (_, _) => { if (!_busy) Close(); };
        buttons.Controls.Add(_go);
        buttons.Controls.Add(cancel);

        card.Controls.Add(_again);
        card.Controls.Add(againLbl);
        card.Controls.Add(_pw);
        card.Controls.Add(pwLbl);
        card.Controls.Add(about);
        card.Controls.Add(head);
        card.Controls.Add(buttons);
        card.Controls.Add(_status);

        ClientSize = new Size(width, canvas.Padding.Vertical + card.Padding.Vertical + head.Height + about.Height
            + pwLbl.Height + _pw.Height + againLbl.Height + _again.Height + _status.Height + buttons.Height + 12);

        _go.Click += async (_, _) => { if (_done) Close(); else await AddAsync(); };
        AcceptButton = _go;
        _pw.Box.TextChanged += (_, _) => { if (!_busy) SetStatus(""); };
        _again.Box.TextChanged += (_, _) => { if (!_busy) SetStatus(""); };
        _pw.NextField(_again.Box);
        Shown += (_, _) => { try { _pw.Box.Focus(); } catch { } };

        MayAdd = () => FreeSetup.MayAddAsync(_origin, _slug);
        Installer = async () =>
        {
            var staged = await Task.Run(() => Launcher.StageInstall(_slug));
            Directory.CreateDirectory(Launcher.CacheRoot);
            var code = await Launcher.RunInstallerAsync(staged, unattended: false, LauncherForm.InstallLog, free: true);
            if (code == 0) try { Directory.Delete(staged, recursive: true); } catch { }
            return code;
        };
        SetPassword = async pw =>
        {
            var id = await FreeSetup.WaitForIdentityAsync(TimeSpan.FromSeconds(45));
            if (id == null) return "The support service did not start. Restart the computer and run this again.";
            return await FreeSetup.SetPasswordAsync(_brand.ServerUrl, _brand.TenantKey, id, pw, TimeSpan.FromSeconds(45),
                awaitingConfirm: w => _awaitingConfirm = w);
        };
        SetAttendedOnly = async () =>
        {
            var id = await FreeSetup.WaitForIdentityAsync(TimeSpan.FromSeconds(45));
            if (id == null) return "The support service did not start. Restart the computer and run this again.";
            return await FreeSetup.SetAttendedOnlyAsync(_brand.ServerUrl, _brand.TenantKey, id, TimeSpan.FromSeconds(45));
        };
    }

    private void SetStatus(string text, bool bad = false)
    {
        _status.Text = text;
        _status.ForeColor = bad ? _t.BadText : _t.Ink;
    }

    private async Task AddAsync()
    {
        if (_busy || _done) return;
        // The Basic version may leave both blank (§163): attended, the person here asked each time.
        bool attended = _basic && _pw.Box.Text.Length == 0 && _again.Box.Text.Length == 0;
        var problem = attended ? "" : UnattendedPasswordDialog.Problem(_pw.Box.Text, _again.Box.Text);
        if (problem.Length > 0) { SetStatus(problem, bad: true); return; }
        _busy = true;
        _go.Enabled = _pw.Enabled = _again.Enabled = false;
        try
        {
            SetStatus("Checking this computer can be added…");
            if (await MayAdd() is { } no) { SetStatus(no, bad: true); return; }

            SetStatus("Installing… Windows will ask for permission — choose Yes.");
            var code = await Installer();
            if (code == -1) { SetStatus("Nothing was installed: the Windows permission prompt was declined. You can try again.", bad: true); return; }
            if (code != 0) { SetStatus($"The install stopped before it finished. The details are in {LauncherForm.InstallLog}.", bad: true); return; }

            SetStatus(attended ? "Finishing…" : "Setting the password…");
            var why = attended ? await SetAttendedOnly() : await SetPassword(_pw.Box.Text);
            if (why.Length > 0) { SetStatus(why, bad: true); return; }

            _done = true;
            // Installed before and recognised by its hardware (2026-10-07): the owner says it is theirs first.
            SetStatus(_awaitingConfirm
                ? $"Done. {Environment.MachineName} was on your list before, so confirm it is the same computer first: in your "
                  + "console, right-click it and choose \"Confirm it is the same machine\". Then connect as usual."
                : attended
                ? $"Done. {Environment.MachineName} is ready: connect to it from your console, and whoever is here is asked to allow it. Nothing of ours stays on this screen."
                : $"Done. {Environment.MachineName} is ready: connect to it from your console. Nothing of ours stays on this screen.");
            _go.Text = "Close";
        }
        catch (Exception ex) { SetStatus($"That did not work: {ex.Message}", bad: true); }
        finally
        {
            _busy = false;
            _go.Enabled = true;
            _pw.Enabled = _again.Enabled = !_done;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }
}
