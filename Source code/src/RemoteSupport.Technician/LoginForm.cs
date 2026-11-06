using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

public sealed class LoginForm : Form
{
    private readonly Branding _brand;
    private readonly Theme _t;
    private readonly InputBox _user, _pass;
    private readonly RoundButton _login;
    private readonly Label _msg;
    public TechLink? Link { get; private set; }

    public LoginForm(Branding brand)
    {
        _brand = brand;
        _t = Theme.FromBranding(brand, AppSurface.Technician);

        Text = $"{brand.CompanyName} — Technician";
        ClientSize = new Size(440, 540);   // the height is put right below, once the parts are measured
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        if (BrandIcon.Load(brand) is { } icon) Icon = icon;   // a fixed dialog takes no default icon
        BackColor = _t.Mist; Font = _t.Body;
        Shown += (_, _) => NativeDark.Apply(this, _t);   // dark title bar, scrollbars and list header — see NativeDark

        Image? logo = null;
        try { if (brand.HasLogo) logo = Image.FromFile(brand.LogoPath); } catch { }
        Controls.Add(new BrandHeader(_t, logo, brand.CompanyName, "Technician Console", 112));

        var canvas = new CanvasPanel(_t) { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 18) };
        Controls.Add(canvas);
        canvas.BringToFront();

        var card = new Card(_t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        // EVERY height here is measured from the font it draws with (HANDOVER §30). They were pixel
        // numbers written against a 100% display, and internal testing (2026-10-02) sent a
        // screenshot from a scaled one: the labels' text grew with Windows' scaling and the 26 px
        // they sat in did not, so "Password" lost its bottom half.
        var loginFont = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        int Line(Font f) => TextRenderer.MeasureText("Ag", f).Height;
        int labelH = Line(_t.BodyBold) + 10;
        int inputH = Math.Max(42, Line(new Font("Segoe UI", 10.5f)) + 20);
        int headH = Line(_t.H2) + 14;
        int msgH = Line(_t.Body) * 2 + 8;
        int loginH = Math.Max(48, Line(loginFont) + 22);
        int linkH = Line(_t.Small) + 12;

        _msg = new Label { Dock = DockStyle.Bottom, Height = msgH, ForeColor = _t.BadText, BackColor = Color.Transparent, Font = _t.Body, TextAlign = ContentAlignment.MiddleCenter };
        _login = new RoundButton(_t, BtnStyle.Accent) { Text = "Sign In", Dock = DockStyle.Bottom, Height = loginH, Font = loginFont };
        _login.Click += async (_, _) => await DoLogin();

        _pass = new InputBox(_t, password: true) { Dock = DockStyle.Top, Height = inputH };
        _pass.Box.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DoLogin(); } };
        var passLbl = new Label { Text = "Password", Dock = DockStyle.Top, Height = labelH, Font = _t.BodyBold, ForeColor = _t.Ink, BackColor = Color.Transparent, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4) };
        _user = new InputBox(_t) { Dock = DockStyle.Top, Height = inputH };
        var userLbl = new Label { Text = "Technician username or email", Dock = DockStyle.Top, Height = labelH, Font = _t.BodyBold, ForeColor = _t.Ink, BackColor = Color.Transparent, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4) };
        var head = new Label { Text = "Sign in", Dock = DockStyle.Top, Height = headH, Font = _t.H2, ForeColor = _t.Ink, BackColor = Color.Transparent };

        // "Forgot your password?" (2026-10-01): the server's own page, in the browser — it mails a
        // link to choose a new one. NOT AutoSize: an auto-sized label shrinks to its own text, so
        // MiddleCenter had nothing to centre in and it sat at the left (internal testing,
        // 2026-10-02). Full width of the card, centred, its height from its font.
        var forgot = new LinkLabel
        {
            Text = "Forgot your password?", Dock = DockStyle.Bottom, AutoSize = false, Height = linkH, Font = _t.Small,
            LinkColor = _t.Accent, ActiveLinkColor = _t.Accent, BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter
        };
        forgot.LinkClicked += (_, _) => OpenForgot();

        // Bottom-docked: the LAST added is the one nearest the bottom edge (docking runs from the
        // highest index down). So, from the bottom up: the link, the message, the button.
        card.Controls.Add(_login);
        card.Controls.Add(_msg);
        card.Controls.Add(forgot);
        // then top-docked, in reverse visual order
        card.Controls.Add(_pass);
        card.Controls.Add(passLbl);
        card.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent });
        card.Controls.Add(_user);
        card.Controls.Add(userLbl);
        card.Controls.Add(head);

        // Tall enough for what it holds, at whatever scaling — plus a little air above the button.
        ClientSize = new Size(440, 112 + canvas.Padding.Vertical + card.Padding.Vertical
            + headH + labelH + inputH + 10 + labelH + inputH + 18 + loginH + msgH + linkH);

        AcceptButton = _login;
        _user.NextField(_pass.Box);
        // The last username that worked here, so only the password needs typing — and the cursor
        // starts where the typing is. See LastSignIn.
        _user.Value = LastSignIn.For(brand.ServerUrl);
        Shown += (_, _) => { try { (_user.Value.Length > 0 ? _pass.Box : _user.Box).Focus(); } catch { } };
    }

    /// <summary>The server's Forgot password page, with the username already typed in.</summary>
    private void OpenForgot()
    {
        var s = _brand.ServerUrl.Trim().TrimEnd('/');
        if (s.StartsWith("wss://")) s = "https://" + s[6..];
        else if (s.StartsWith("ws://")) s = "http://" + s[5..];
        var url = $"{s}/forgot?email={Uri.EscapeDataString(_user.Value.Trim())}";
        if (SecureTransport.Refusal(url) is { } why) { _msg.ForeColor = _t.BadText; _msg.Text = why; return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { _msg.ForeColor = _t.BadText; _msg.Text = "Could not open the browser: " + ex.Message; }
    }

    private async Task DoLogin()
    {
        if (_user.Value.Trim().Length == 0 || _pass.Value.Length == 0) { _msg.ForeColor = _t.BadText; _msg.Text = "Enter your username and password."; return; }
        _login.Enabled = false; _msg.ForeColor = _t.Muted; _msg.Text = "Signing in…";
        var link = new TechLink(_brand.ServerUrl);
        // What the last code left behind (2026-10-02): within seven days of it, the password alone.
        var step = await link.LoginAsync(_user.Value.Trim(), _pass.Value, TrustedSignIn.For(_brand.ServerUrl, _user.Value));
        // The second step (2026-10-01): the code from their authenticator app, or setting one up.
        if (!step.Ok && step.Next.Length > 0)
        {
            _msg.Text = "";
            using var second = new TwoFactorDialog(_t, link, step);
            if (second.ShowDialog(this) != DialogResult.OK) { _login.Enabled = true; _pass.Value = ""; _pass.Box.Focus(); return; }
            step = new TechLink.LoginStep { Ok = true };
        }
        if (!step.Ok) { _msg.ForeColor = _t.BadText; _msg.Text = step.Error; _login.Enabled = true; return; }
        LastSignIn.Remember(_brand.ServerUrl, _user.Value);
        // A code was given just now (the server only sends one then): keep it for the next week.
        if (link.Trust.Length > 0) TrustedSignIn.Remember(_brand.ServerUrl, _user.Value, link.Trust);
        Link = link;
        DialogResult = DialogResult.OK;
        Close();
    }
}
