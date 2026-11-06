using System.Drawing;
using System.Net.WebSockets;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>
/// The GUEST console (roadmap 3.9, step 3, 2026-10-06): this same .exe, downloaded from /guest by
/// somebody with no account here — Clint's example, a customer's accounting firm. No company, no
/// sign-in, nothing installed: one window asking for their name, their company and the customer's
/// code. The server does the rest (GuestVisitor): the person is asked, the session window opens with
/// screen and control only, and the customer's IT company can watch and end it.
/// </summary>
public sealed class GuestForm : Form
{
    /// <summary>Whether this run is a guest console, and against which server — or null for an ordinary console.
    /// <c>--guest [--server URL]</c>, a download from /guest/console.exe, or the guest file name.</summary>
    public static string? GuestServer(string[] args, string? exePath)
    {
        int s = Array.IndexOf(args, "--server");
        string? given = s >= 0 && s + 1 < args.Length ? args[s + 1] : null;
        bool asked = args.Contains("--guest");
        var zone = DownloadIdentity.GuestServerFromZoneIdentifier(DownloadIdentity.ReadZoneIdentifier(exePath ?? ""));
        if (!asked && zone == null && !DownloadIdentity.IsGuestFileName(exePath)) return null;
        return DownloadIdentity.HttpOrigin(given ?? zone ?? ConsoleDownload.DefaultServer());
    }

    private readonly Theme _t;
    private readonly string _origin, _wsBase;
    private readonly InputBox _name, _company, _code;
    private readonly Label _status;
    private readonly RoundButton _connect;
    private WsConnection? _ws;
    private string _token = "";
    private SessionForm? _session;
    private readonly Branding _brand = new();

    public GuestForm(string server)
    {
        _origin = DownloadIdentity.HttpOrigin(server);
        _wsBase = _origin.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss://" + _origin[8..] : "ws://" + _origin[7..];
        _t = Theme.FromBranding(_brand, AppSurface.Technician);
        Text = "Connect with a code — guest";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        Font = _t.Body; BackColor = _t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, _t);

        var canvas = new CanvasPanel(_t) { Dock = DockStyle.Fill, Padding = new Padding(18) };
        Controls.Add(canvas);
        var card = new Card(_t, 22) { Dock = DockStyle.Fill };
        canvas.Controls.Add(card);

        int btnH = TextRenderer.MeasureText("Ag", _t.ButtonFont).Height + 18;
        int width = 520, inner = width - canvas.Padding.Horizontal - card.Padding.Horizontal;
        // Every height from the font it draws with (§30).
        int Lines(string text, Font f) => TextRenderer.MeasureText(text, f, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 6;
        Label Text_(string s, Font f, Color c) { var l = new Label { Text = s, Dock = DockStyle.Top, Font = f, ForeColor = c, BackColor = Color.Transparent, UseMnemonic = false }; l.Height = Lines(s, f); return l; }
        Label Caption(string s) => new()
        {
            Text = s, Dock = DockStyle.Top, Font = _t.BodyBold, ForeColor = _t.Ink, BackColor = Color.Transparent, UseMnemonic = false,
            TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(0, 0, 0, 4), Height = TextRenderer.MeasureText("Ag", _t.BodyBold).Height + 14
        };
        InputBox Box(string hint, int max) { var b = new InputBox(_t) { Dock = DockStyle.Top }; b.Box.PlaceholderText = hint; b.Box.MaxLength = max; b.Height = b.Box.PreferredHeight + 16; return b; }

        var head = Text_("Connect with a code", _t.H2, _t.Ink);
        var about = Text_("Type the nine-digit code the person you are helping gave you. They are shown your name and asked to allow you; "
            + "their IT company is told and can watch and end it. You get their screen, mouse and keyboard — no files or tools.", _t.Small, _t.Muted);
        var nameLbl = Caption("Your name");
        _name = Box("e.g. Jane Smith", GuestRules.MaxName);
        var coLbl = Caption("Your company (if any)");
        _company = Box("e.g. Smith & Co Accountants", GuestRules.MaxCompany);
        var codeLbl = Caption("The code");
        _code = Box("123 456 789", 15);
        _status = new Label { Text = "", Dock = DockStyle.Top, Font = _t.Small, ForeColor = _t.Muted, BackColor = Color.Transparent, UseMnemonic = false };
        _status.Height = Lines("Ag\nAg\nAg", _t.Small);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = btnH + 12, BackColor = Color.Transparent, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 10, 0, 0) };
        _connect = new RoundButton(_t, BtnStyle.Accent) { Text = "Connect", Size = new Size(Math.Max(120, TextRenderer.MeasureText("Connect", _t.ButtonFont).Width + 36), btnH), Margin = new Padding(0) };
        _connect.Click += (_, _) => Connect();
        buttons.Controls.Add(_connect);

        // Top-docked: the LAST added sits highest.
        foreach (var c in new Control[] { _status, _code, codeLbl, _company, coLbl, _name, nameLbl, about, head }) card.Controls.Add(c);
        card.Controls.Add(buttons);
        ClientSize = new Size(width, canvas.Padding.Vertical + card.Padding.Vertical + head.Height + about.Height + nameLbl.Height + _name.Height
            + coLbl.Height + _company.Height + codeLbl.Height + _code.Height + _status.Height + buttons.Height + 8);
        AcceptButton = _connect;
        _name.NextField(_company.Box); _company.NextField(_code.Box);
        Shown += (_, _) => { try { _name.Box.Focus(); } catch { } };
        FormClosed += (_, _) => { try { _ws?.CloseAsync("closed").Wait(1000); } catch { } };
    }

    private void Say(string text, bool bad = false) { _status.Text = text; _status.ForeColor = bad ? _t.BadText : _t.Muted; }

    /// <summary>What is wrong with the three boxes, or empty — the server's own rules. Public for fitprobe.</summary>
    public static string Problem(string name, string company, string code)
    {
        var p = GuestRules.Problem(name, company);
        if (p.Length > 0) return p;
        return CodeEntryDialog.Problem(code);
    }

    private async void Connect()
    {
        var problem = Problem(_name.Box.Text, _company.Box.Text, _code.Box.Text);
        if (problem.Length > 0) { Say(problem, bad: true); return; }
        if (SecureTransport.Refusal(_origin) is { } insecure) { Say(insecure, bad: true); return; }
        _connect.Enabled = false;
        Say("Connecting…");
        try
        {
            if (_ws is not { IsOpen: true })
            {
                var cws = new ClientWebSocket();
                await cws.ConnectAsync(new Uri($"{_wsBase}/ws/guest"), CancellationToken.None);
                _ws = new WsConnection(cws);
                _ws.OnMessage += m => { try { BeginInvoke(() => OnMessage(m)); } catch { } };
                _ws.OnClosed += _ => { try { BeginInvoke(() => { if (_session == null) { Say("The connection to the server was lost.", bad: true); _connect.Enabled = true; } }); } catch { } };
                _ = _ws.RunAsync(CancellationToken.None);
            }
            _ws.Send(new GuestConnectMsg { Code = OutsiderAccess.Clean(_code.Box.Text), Name = _name.Box.Text.Trim(), Company = _company.Box.Text.Trim() });
        }
        catch (Exception ex) { Say("Could not reach the server: " + ex.Message, bad: true); _connect.Enabled = true; }
    }

    private void OnMessage(Msg m)
    {
        switch (m)
        {
            case ErrorMsg e: Say(e.Message, bad: true); _connect.Enabled = true; break;
            case GuestTicketMsg t: _token = t.Token; break;
            case SessionReadyMsg r: Follow(r.Session); break;
        }
    }

    /// <summary>The session as it moves: waiting for the person, allowed (the session window opens), over.</summary>
    private void Follow(SessionInfo s)
    {
        if (s.Ended)
        {
            Say("It has ended: " + (s.EndReason.Length > 0 ? s.EndReason : "the session is over.") + " A new code is needed to connect again.");
            _connect.Enabled = true; _code.Box.Text = "";
            return;
        }
        // A technician's code (§155): THEY let you in, not the person at the machine.
        if (s.AwaitingConsent && s.AwaitingTechnician.Length > 0) { Say($"Waiting for {s.AwaitingTechnician} to let you in…"); return; }
        if (s.AwaitingConsent || !s.HostConnected) { Say($"Waiting for the person at {s.MachineName} to allow you…"); return; }
        if (_session is { IsDisposed: false }) return;
        Say($"Connected to {s.MachineName}. Close the session window when you are done.");
        _session = new SessionForm(_wsBase, _token, s, _t, _brand);
        _session.FormClosed += (_, _) => { _session = null; Say("The session window was closed. A new code is needed to connect again."); _connect.Enabled = true; };
        _session.Show();
    }
}
