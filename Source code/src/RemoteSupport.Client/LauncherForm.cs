using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>
/// What a downloaded copy shows first (2026-09-30): the two things the old zip offered as two files
/// to double-click — "just this once" and "install" — as two choices in one window, in the
/// company's own colours. Install runs here, with the Windows prompt as its only question, and says
/// how it went.
///
/// 2026-10-23: Install asks who the computer is for — name, surname and email, all three required (<see cref="GivenPerson"/>).
/// Saved beside the installer, so the installed apps tell the server, and the console shows the name until the machine is
/// linked; the request window starts with it. Get help now asks nothing: its request window asks the name anyway.
/// </summary>
public sealed class LauncherForm : Form
{
    public enum Outcome { Cancelled, RunNow, Installed }
    public Outcome Result { get; private set; } = Outcome.Cancelled;

    private readonly Theme _t;
    private readonly Branding _brand;
    private readonly string _slug;
    private readonly RoundButton _runBtn, _installBtn;
    private readonly CheckBox _unattended;
    private readonly InputBox _first, _surname, _email;
    private readonly Label _status;
    /// <summary>What is on file from the install this one goes over, or null — the boxes start with it.</summary>
    private readonly GivenPerson? _before;

    /// <summary>Who the computer is for, as typed — set when Install is pressed and the three boxes pass.</summary>
    public GivenPerson? Person { get; private set; }
    private bool _busy, _done;

    /// <summary>
    /// Stages and runs the installer; the exit code, or -1 for "No" to the Windows prompt. Replaced
    /// only by tools/oneexecheck, which must not install on the PC it runs on.
    /// </summary>
    public Func<bool, Task<int>> Installer { get; set; }

    /// <summary>Where the last install's words are kept, for a technician on the phone to ask for.</summary>
    public static string InstallLog => Path.Combine(Launcher.CacheRoot, "install.log");

    public LauncherForm(Branding brand, string slug, bool canInstall, bool installed, GivenPerson? before = null)
    {
        _brand = brand; _slug = slug; _before = before;
        _t = Theme.FromBranding(brand, AppSurface.Customer);
        Text = brand.ProductName;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = _t.Mist;
        Font = _t.Body;
        Shown += (_, _) => NativeDark.Apply(this, _t);
        try { if (BrandIcon.Load(brand) is { } ic) Icon = ic; } catch { }

        int line = TextRenderer.MeasureText("Ag", _t.Body).Height;
        int btnH = line + 22;
        const int W = 720, pad = 18;

        Image? logo = null;
        try { if (brand.HasLogo) logo = Image.FromFile(brand.LogoPath); } catch { }
        var header = new BrandHeader(_t, logo, brand.CompanyName, "Remote support", 96) { Dock = DockStyle.Top };

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(pad) };
        var title = new Label
        {
            Text = "How would you like us to help?", Font = _t.H2, ForeColor = _t.Ink, AutoSize = true,
            BackColor = Color.Transparent, Location = new Point(pad, pad), UseMnemonic = false
        };
        body.Controls.Add(title);

        int cardTop = pad + TextRenderer.MeasureText("Ag", _t.H2).Height + 14;
        int cardW = (W - pad * 3) / 2;

        (Card card, int inner, Func<int> y, Action<int> setY) MakeCard(int left)
        {
            var c = new Card(_t, 16) { Location = new Point(left, cardTop), Width = cardW };
            int cur = 16;
            return (c, cardW - 32, () => cur, v => cur = v);
        }
        Label Text_(string text, Font f, Color color, int width) => new()
        {
            Text = text, Font = f, ForeColor = color, BackColor = Color.Transparent, UseMnemonic = false,
            AutoSize = true, MaximumSize = new Size(width, 0)
        };
        void Place(Card c, Control ctl, Func<int> y, Action<int> setY, int gapAfter)
        {
            ctl.Location = new Point(16, y());
            c.Controls.Add(ctl);
            // Its real height when it is sized by hand (the tick box): PreferredSize is one line.
            setY(y() + (ctl.AutoSize ? ctl.PreferredSize.Height : ctl.Height) + gapAfter);
        }

        // ---- get help now ----
        var (runCard, runInner, runY, runSetY) = MakeCard(pad);
        Place(runCard, Text_("Get help now", _t.H3, _t.Ink, runInner), runY, runSetY, 8);
        Place(runCard, Text_("Nothing is installed. A technician can see your screen once you allow it, and fix things with your permission. Close it when you are done.",
            _t.Body, _t.Muted, runInner), runY, runSetY, 14);
        _runBtn = new RoundButton(_t, BtnStyle.Accent) { Text = "Get help now", Size = new Size(Math.Max(170, TextRenderer.MeasureText("Get help now", _t.ButtonFont).Width + 40), btnH) };
        _runBtn.Click += (_, _) => { if (_busy) return; Result = Outcome.RunNow; DialogResult = DialogResult.OK; Close(); };

        // ---- install ----
        var (insCard, insInner, insY, insSetY) = MakeCard(pad * 2 + cardW);
        var insTitle = installed ? $"Update {brand.ProductName}" : $"Install {brand.ProductName}";
        Place(insCard, Text_(insTitle, _t.H3, _t.Ink, insInner), insY, insSetY, 8);
        var insText = !canInstall
            ? $"This copy cannot install. Ask {brand.CompanyName} for the full download."
            : installed
                ? "It is already on this PC; this puts this version over it. You will be asked for administrator permission."
                : $"So {brand.CompanyName} can help whenever you need it — it starts with Windows. You will be asked for administrator permission.";
        Place(insCard, Text_(insText, _t.Body, _t.Muted, insInner), insY, insSetY, 10);

        // Who it is for (2026-10-23): name and surname side by side, the email under them, and why in one line.
        Place(insCard, Text_($"Who is this computer for? So {brand.CompanyName} knows who you are when you ask for help.",
            _t.Small, _t.Muted, insInner), insY, insSetY, 6);
        // The label as tall as it really draws, not the font's line: measured from the line alone, each box sat over its label's foot.
        int labelH; using (var probe = Text_("Ag", _t.Small, _t.Ink, insInner)) labelH = probe.PreferredSize.Height;
        int halfW = (insInner - 10) / 2;
        InputBox Field(string label, int left, int width, string value)
        {
            var l = Text_(label, _t.Small, _t.Ink, width);
            l.Location = new Point(16 + left, insY());
            insCard.Controls.Add(l);
            var box = new InputBox(_t) { Location = new Point(16 + left, insY() + labelH + 2), Width = width, Enabled = canInstall };
            // Measured from the box's own font (§30), as the request window does.
            box.Height = box.Box.PreferredHeight + 16;
            box.Value = value;
            insCard.Controls.Add(box);
            return box;
        }
        _first = Field("Name", 0, halfW, before?.First ?? "");
        _surname = Field("Surname", halfW + 10, insInner - halfW - 10, before?.Surname ?? "");
        insSetY(insY() + labelH + 2 + _first.Height + 6);
        _email = Field("Email address", 0, insInner, before?.Email ?? "");
        insSetY(insY() + labelH + 2 + _email.Height + 12);
        _first.Box.MaxLength = _surname.Box.MaxLength = OwnerNames.MaxLength;
        _email.Box.MaxLength = GivenPerson.EmailMax;
        _first.NextField(_surname.Box);
        _surname.NextField(_email.Box);
        // Not AutoSize: an AutoSize tick box never wraps, and its words were cut off at "…when I" (the
        // oneexecheck picture). Sized for its wrapped text, measured (§30).
        const string unattendedText = "Also let them connect when I am away";
        int boxW = SystemInformation.MenuCheckSize.Width + 8;
        int textH = TextRenderer.MeasureText(unattendedText, _t.Body, new Size(insInner - boxW, 0), TextFormatFlags.WordBreak).Height;
        _unattended = new RoundCheck(_t)
        {
            Text = unattendedText, AutoSize = false, Size = new Size(insInner, textH + 6), Font = _t.Body, ForeColor = _t.Ink,
            BackColor = Color.Transparent, Enabled = canInstall, CheckAlign = ContentAlignment.TopLeft, TextAlign = ContentAlignment.TopLeft
        };
        Place(insCard, _unattended, insY, insSetY, 14);
        _installBtn = new RoundButton(_t, BtnStyle.Neutral)
        {
            Text = installed ? "Update" : "Install", Enabled = canInstall,
            Size = new Size(Math.Max(140, TextRenderer.MeasureText("Update", _t.ButtonFont).Width + 40), btnH)
        };
        // After a good install the same button reads Close, and does only that.
        _installBtn.Click += async (_, _) => { if (_done) Close(); else await InstallAsync(); };
        Installer = async unattended =>
        {
            SetStatus("Getting ready…");
            var staged = await Task.Run(() => Launcher.StageInstall(_slug));
            // Beside the installer, so Install-Client.ps1 copies it into Program Files with everything else.
            Person?.Save(staged);
            SetStatus("Installing… Windows will ask for permission — choose Yes.");
            Directory.CreateDirectory(Launcher.CacheRoot);
            var code = await Launcher.RunInstallerAsync(staged, unattended, InstallLog);
            if (code == 0) try { Directory.Delete(staged, recursive: true); } catch { }
            return code;
        };

        // Both cards as tall as the taller, with their buttons on one line at the bottom.
        int cardH = Math.Max(runY(), insY()) + btnH + 16;
        runCard.Height = insCard.Height = cardH;
        _runBtn.Location = new Point(16, cardH - btnH - 16);
        _installBtn.Location = new Point(16, cardH - btnH - 16);
        runCard.Controls.Add(_runBtn);
        insCard.Controls.Add(_installBtn);
        body.Controls.Add(runCard);
        body.Controls.Add(insCard);

        _status = new Label
        {
            Location = new Point(pad, cardTop + cardH + 12), AutoSize = true, MaximumSize = new Size(W - pad * 2, 0),
            Font = _t.Body, ForeColor = _t.Ink, BackColor = Color.Transparent, UseMnemonic = false
        };
        body.Controls.Add(_status);

        Controls.Add(body);
        Controls.Add(header);
        ClientSize = new Size(W, header.Height + cardTop + cardH + 12 + line * 4 + pad);
    }

    private void SetStatus(string text, bool bad = false)
    {
        _status.Text = text;
        _status.ForeColor = bad ? _t.BadText : _t.Ink;
    }

    private async Task InstallAsync()
    {
        if (_busy || _done) return;
        // All three, before anything is staged or Windows asks anything (2026-10-23).
        var problem = GivenPerson.Problem(_first.Value, _surname.Value, _email.Value);
        if (problem.Length > 0)
        {
            SetStatus(problem, bad: true);
            var first = string.IsNullOrWhiteSpace(_first.Value) ? _first : string.IsNullOrWhiteSpace(_surname.Value) ? _surname : _email;
            try { first.Box.Focus(); } catch { }
            return;
        }
        Person = GivenPerson.Make(_first.Value, _surname.Value, _email.Value, _before);
        _busy = true;
        _runBtn.Enabled = _installBtn.Enabled = _unattended.Enabled = false;
        SetBoxes(false);
        try
        {
            var code = await Installer(_unattended.Checked);
            if (code == 0)
            {
                // Done, and closable. This branch once returned with _busy still set, and
                // OnFormClosing refused Close and the X for ever after a good install.
                _done = true;
                Result = Outcome.Installed;
                SetStatus($"Installed. {_brand.ProductName} is starting — it has an icon by the clock and on your desktop. You can close this window.");
                _installBtn.Text = "Close";
                _installBtn.Enabled = true;
                return;
            }
            SetStatus(code == -1
                ? "Nothing was installed: the Windows permission prompt was declined. You can try again, or get help now instead."
                : $"The install stopped before it finished. {LastLine()} Get help now still works, and {_brand.CompanyName} can see the details in {InstallLog}.", bad: true);
            _runBtn.Enabled = _installBtn.Enabled = _unattended.Enabled = true;
            SetBoxes(true);
        }
        catch (Exception ex)
        {
            SetStatus($"The install could not start: {ex.Message}", bad: true);
            _runBtn.Enabled = _installBtn.Enabled = _unattended.Enabled = true;
            SetBoxes(true);
        }
        finally { _busy = false; }
    }

    private void SetBoxes(bool on) => _first.Enabled = _surname.Enabled = _email.Enabled = on;

    /// <summary>The installer's last words that say something, so a failure says what failed.</summary>
    private static string LastLine()
    {
        try
        {
            var lines = File.ReadAllLines(InstallLog).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("At ") && !l.StartsWith("+")).ToList();
            return lines.Count == 0 ? "" : "It said: " + lines[^1];
        }
        catch { return ""; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Not while the installer is running: it would carry on with nobody to tell how it went.
        if (_busy && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }
}

