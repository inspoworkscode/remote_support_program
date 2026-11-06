using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Client;

/// <summary>Client-facing window: request a support session, chat with technicians, see connection status.</summary>
public sealed class MainForm : Form
{
    private readonly Branding _brand;
    private readonly DeviceIdentity _id;
    private readonly DeviceLink _link;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _wsBase;
    private readonly Theme _t;

    private RoundButton _requestBtn = null!;
    private Pill _statusPill = null!;
    private RichTextBox _chatLog = null!;
    private InputBox _chatInput = null!;
    private NotifyIcon _tray = null!;
    /// <summary>Bottom-right strip so the person at the machine can see a session is live even
    /// when this window is minimised. Created lazily: most of the time there is no session.</summary>
    private SessionBanner? _banner;
    /// <summary>Bottom-right chat, so a technician's message is answerable where it appears.</summary>
    private ChatPopup? _chat;
    private bool _sessionActive, _ringing, _connected;
    /// <summary>True while a technician can hear this machine. Disclosed on the tray window, on
    /// the banner and in the chat log, because nothing else would tell the person at it.</summary>
    private bool _beingListenedTo;
    /// <summary>Who is connected, remembered from the session so the listening messages can name
    /// them rather than saying "somebody".</summary>
    private string _technicianName = "";
    /// <summary>The session is still alive but the technician's connection has dropped, and the
    /// server is holding it open for a short while in case they come back. Said out loud for the
    /// same reason the listening indicator is: for up to ninety seconds the tray app would
    /// otherwise be telling this person somebody is watching a screen nobody is looking at.</summary>
    private bool _sessionHeld;
    /// <summary>The server's own words for why this machine was turned away, or null. Shown on the
    /// status pill rather than the generic "Not connected", because the two have entirely
    /// different fixes and only one of them is the customer's network.</summary>
    private string? _serverRefusal;
    /// <summary>The last name this person gave with a request, kept between runs (2026-10-13, <see cref="LastContact"/>);
    /// before any, the name it was installed for (2026-10-23), then Windows' username.</summary>
    private string _contactName = LastContact.Load(GivenPerson.Installed()?.Name is { Length: > 0 } n ? n : Environment.UserName);

    // ---- unattended access, switched by the person at this machine -------------------------
    // Off until the server says otherwise. The SERVER holds this setting, not us: it is what
    // enforces it, and a permission stored on the machine being protected is one that machine
    // can decide to believe. Everything here is display of, and requests to change, that.
    private ToolStripMenuItem _requestItem = null!;
    private CheckBox _unattendedBox = null!;
    private Label _unattendedNote = null!;
    private ToolStripMenuItem _unattendedItem = null!;
    /// <summary>True while we are setting the checkbox from the server's answer, so the handler
    /// can tell "the server told us" from "the person clicked" and not ask them to confirm a
    /// change they did not make.</summary>
    private bool _applyingUnattended;
    private bool _unattendedOn;
    /// <summary>Whether the server has told us yet. Until it has, the checkbox is disabled: a
    /// tick that cannot be sent anywhere is worse than no tick at all.</summary>
    private bool _unattendedKnown;
    /// <summary>Whether the agent service is connected, as the SERVER sees it. The difference
    /// between "you have not allowed it" and "there is nothing here to allow" needs different
    /// words in front of a customer.</summary>
    private bool _agentOnline;
    /// <summary>Stands in for the confirmation dialog when something is driving this window
    /// without a person at it. Null everywhere except in tools/unattendedcheck; see the comment
    /// where it is used.</summary>
    internal Func<bool>? ConfirmUnattended;

    // ---- the unattended password (2026-10-02) ----
    private ToolStripMenuItem _passwordItem = null!;
    // Outside help (3.9): shown only where the server says this machine may make a code.
    private ToolStripMenuItem _outsiderItem = null!;
    private OutsiderCodeDialog? _codeDialog;
    /// <summary>Who the live code is for, as the server last said; empty when there is none.</summary>
    private string _liveCodeFor = "";
    /// <summary>Whether one is set, as the SERVER holds it — like the tick, never our own last click.</summary>
    private bool _hasPassword;
    /// <summary>Reinstalled, and the IT company has not yet confirmed it is the same computer (2026-10-07).</summary>
    private bool _awaitingConfirm;

    public MainForm(Branding brand)
    {
        _brand = brand;
        _t = Theme.FromBranding(brand, AppSurface.Customer);
        _wsBase = BrandUtil.WsBase(brand.ServerUrl);
        _id = DeviceIdentity.LoadOrCreate();
        _link = new DeviceLink(_wsBase, _id, DeviceRole.Client, brand.TenantKey);
        // The console's Processes and Performance windows, for a machine whose agent service is not
        // connected (§41: often). The server prefers the agent when both are there.
        _link.OfferWatch();
        // And Wake-on-LAN for the other machines on its network (2026-10-06).
        _link.OfferWake();
        // Asks the person whenever the server says to, whatever the branding says (3.9): somebody from
        // another company is let in only after a yes, and the server hands such a session only to a
        // tray app that says this.
        _link.Can.Add(DeviceCan.AlwaysAsk);
        // The company's look as it is now (2026-10-07, roadmap 4.6). Not for a downloaded copy: it fetched its
        // branding from the server as it started, and it is gone when it closes.
        if (!Launcher.IsDownloadedCopy)
        {
            _link.Can.Add(DeviceCan.Branding);
            _link.BrandHash = brand.LiveHash;
        }
        // Who it was installed for (2026-10-23): the agent says it too, so a machine whose agent is down still tells the server.
        _link.InstalledFor = GivenPerson.Installed();

        BuildUi();
        WireLink();
        // Start the server connection only once the window handle exists, otherwise the first
        // (very fast, local) connect callback would try to update a window that isn't there yet.
        HandleCreated += (_, _) => _ = _link.RunAsync(_cts.Token);
    }

    /// <summary>The banner, created on first use and reused for the life of the app.</summary>
    private SessionBanner Banner
    {
        get
        {
            if (_banner == null)
            {
                _banner = new SessionBanner(_t);
                // The one question the banner asks (3.3): may the technician hear you. The answer
                // goes to the hub, which carries it to the host — or refuses it, in an unattended
                // session.
                _banner.MicAnswered += allow => _link.Send(new VoiceMicAnswerMsg { Allow = allow });
            }
            return _banner;
        }
    }

    /// <summary>The microphone question in the middle of the screen (2026-10-02), made on first use.</summary>
    private MicQuestion? _mic;
    private MicQuestion Mic
    {
        get
        {
            if (_mic == null)
            {
                _mic = new MicQuestion(_t, _brand.ProductName);
                // The same answer the banner's buttons give, by the same route.
                _mic.Answered += allow => _link.Send(new VoiceMicAnswerMsg { Allow = allow });
            }
            return _mic;
        }
    }

    /// <summary>A technician's voice is coming out of this computer's speakers.</summary>
    private bool _inCall;
    private string _lastMic = "";

    /// <summary>
    /// A voice call (3.3), as the host reports it. The person at the machine is told who is talking,
    /// and — only when the host is asking — asked whether to use their microphone. Every change is
    /// written in the chat log too, so it is there afterwards.
    /// </summary>
    private void ShowVoice(VoiceStateMsg v)
    {
        if (!v.Call)
        {
            _mic?.Withdraw();
            if (_inCall) AppendSystem("The technician has stopped talking. Your microphone is off.");
            _inCall = false; _lastMic = "";
            Banner.SetVoice(null, null, null);
            return;
        }
        var who = string.IsNullOrWhiteSpace(v.Technicians) ? (string.IsNullOrWhiteSpace(_technicianName) ? "Your technician" : _technicianName) : v.Technicians;
        if (!_inCall)
        {
            AppendSystem($"{who} is talking to you through this computer's speakers.");
            // A balloon only when nothing is being ASKED. When it is, the question has a window of
            // its own in the middle of the screen (MicQuestion), and the balloon used to land on
            // top of the banner's buttons — the very thing it was pointing at (internal testing,
            // 2026-10-02).
            if (v.Mic != VoiceMic.Asking)
                _tray?.ShowBalloonTip(6000, _brand.ProductName, $"{who} is talking to you.", ToolTipIcon.Info);
        }
        if (v.Mic == VoiceMic.Asking) Mic.Ask(who);
        else _mic?.Withdraw();
        _inCall = true;
        if (v.Mic != _lastMic)
        {
            if (v.Mic == VoiceMic.On) AppendSystem($"Your microphone is on. {who} can hear you.");
            else if (v.Mic == VoiceMic.Declined && _lastMic == VoiceMic.On) AppendSystem("Your microphone is off.");
            else if (v.Mic == VoiceMic.Unavailable) AppendSystem($"Your microphone could not be used: {v.Detail}.");
        }
        _lastMic = v.Mic;
        switch (v.Mic)
        {
            case VoiceMic.Asking:
                Banner.SetVoice($"{who} is talking to you. Would you like them to hear you too?", "Use my microphone", "Not now");
                break;
            case VoiceMic.On:
                Banner.SetVoice($"Your microphone is on — {who} can hear you.", null, "Turn my microphone off");
                break;
            case VoiceMic.Declined:
                Banner.SetVoice($"{who} is talking to you. Your microphone is off.", "Use my microphone", null);
                break;
            case VoiceMic.Unavailable:
                Banner.SetVoice($"{who} is talking to you. Your microphone could not be used: {v.Detail}.", "Try again", null);
                break;
            default:   // unattended, or nothing asked yet: told, never asked
                Banner.SetVoice($"{who} is talking to you through this computer's speakers.", null, null);
                break;
        }
    }

    /// <summary>The chat popup, created on first use. Replies go out through the same link as the
    /// tray window's chat box, and are echoed into both so the two never disagree.</summary>
    private ChatPopup Chat
    {
        get
        {
            if (_chat == null)
            {
                _chat = new ChatPopup(_t, _brand.CompanyName);
                // Send only. The server routes the message back to us like any other, and that
                // echo is what writes it into both logs — appending here as well would double it.
                _chat.OnSend += text => _link.SendChat(_contactName, text);
            }
            // Sit above the session banner while one is on screen.
            _chat.ReserveBottom = _banner is { Visible: true } ? _banner.Height + 8 : 0;
            return _chat;
        }
    }

    /// <summary>Marshal to the UI thread without ever throwing into the network loop.</summary>
    private void Inv(Action a)
    {
        try
        {
            if (IsDisposed) return;
            if (!IsHandleCreated) { a(); return; }
            BeginInvoke(a);
        }
        catch { }
    }

    private void BuildUi()
    {
        Text = $"{_brand.CompanyName} — {_brand.ProductName}";
        Size = new Size(460, 660);
        MinimumSize = new Size(420, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, _t);   // dark title bar, scrollbars and list header — see NativeDark
        Font = _t.Body;

        Image? logo = null;
        try { if (_brand.HasLogo) logo = Image.FromFile(_brand.LogoPath); } catch { }
        Controls.Add(new BrandHeader(_t, logo, _brand.CompanyName, _brand.ProductName, 100));

        var canvas = new CanvasPanel(_t) { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 14) };
        Controls.Add(canvas);
        canvas.BringToFront();

        // ---- chat card (fills) ----
        var chatCard = new Card(_t, 14) { Dock = DockStyle.Fill };
        _chatLog = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = _t.Surface, Font = _t.Body, ForeColor = _t.Ink
        };
        var chatRow = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
        _chatInput = new InputBox(_t) { Dock = DockStyle.Fill };
        _chatInput.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendChat(); } };
        var sendBtn = new RoundButton(_t, BtnStyle.Accent) { Text = "Send", Dock = DockStyle.Right, Width = 78, Height = 40 };
        sendBtn.Click += (_, _) => SendChat();
        chatRow.Controls.Add(_chatInput);
        chatRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        chatRow.Controls.Add(sendBtn);
        var chatTitle = new SectionTitle(_t, "Chat with a technician", _t.Purple) { Dock = DockStyle.Top };
        chatCard.Controls.Add(_chatLog);
        chatCard.Controls.Add(chatRow);
        chatCard.Controls.Add(chatTitle);

        // ---- action card (top) ----
        var actionCard = new Card(_t, 16) { Dock = DockStyle.Top, Height = 224 };
        _requestBtn = new RoundButton(_t, BtnStyle.Accent) { Text = "Request Support", Dock = DockStyle.Top, Height = 56, Font = new Font("Segoe UI", 12.5f, FontStyle.Bold) };
        _requestBtn.Click += (_, _) => ToggleRequest();
        // the pill sizes to its text, so it must sit in a host panel rather than dock full-width
        var pillHost = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.Transparent };
        _statusPill = new Pill(_t, "Connecting…", _t.WarnBg, _t.WarnText) { Dock = DockStyle.Left, Width = 200, Height = 26 };
        pillHost.Controls.Add(_statusPill);
        var welcome = new Label
        {
            Text = _brand.WelcomeText, Dock = DockStyle.Top, Height = 76, AutoEllipsis = false,
            ForeColor = _t.Muted, BackColor = Color.Transparent, Font = _t.Body
        };
        // Grow the welcome label (and its card) to fit the tenant's message, however long it is.
        // The guard matters: setting the card height re-raises Resize, which would recurse forever.
        bool fitting = false;
        void FitWelcome()
        {
            if (fitting) return;
            fitting = true;
            try
            {
                int w = actionCard.ClientSize.Width - actionCard.Padding.Horizontal;
                if (w <= 10) return;
                var h = TextRenderer.MeasureText(welcome.Text, welcome.Font, new Size(w, 0), TextFormatFlags.WordBreak).Height + 8;
                // welcome + gap + Request button + gap + status pill.
                int cardH = h + 8 + 56 + 10 + 28 + actionCard.Padding.Vertical;
                if (welcome.Height != h) welcome.Height = h;
                if (actionCard.Height != cardH) actionCard.Height = cardH;
            }
            finally { fitting = false; }
        }
        actionCard.Resize += (_, _) => FitWelcome();
        HandleCreated += (_, _) => FitWelcome();
        // ---- unattended access: NOT on this window --------------------------------------------
        // It was here, under the status pill, for half a day. Clint, 2026-09-20: "there should
        // only ever be the unattended check box in the tray, not as part of the request."
        //
        // He is right, and the reason is what this window is FOR. It is the thing somebody opens
        // when they want help now; a permission that is set once and then left alone for months
        // has no business on it, competing for attention with the button they came to press.
        // The icon by the clock is where a setting lives.
        //
        // The checkbox object still exists, unparented, and is still the one place the confirm /
        // send / put-it-back logic lives — the tray item drives it. That is deliberate: two
        // copies of this particular decision is exactly the bug worth avoiding.
        _unattendedBox = new CheckBox { Text = "Allow unattended access", Enabled = false };
        _unattendedNote = new Label { UseMnemonic = false };   // the wording, read by the tray tooltip
        _unattendedBox.CheckedChanged += (_, _) => UnattendedBoxClicked();

        actionCard.Controls.Add(pillHost);
        actionCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Color.Transparent });
        actionCard.Controls.Add(_requestBtn);
        actionCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Color.Transparent });
        actionCard.Controls.Add(welcome);

        canvas.Controls.Add(chatCard);
        canvas.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 14, BackColor = Color.Transparent });
        canvas.Controls.Add(actionCard);

        // ---- tray ----
        // Customers recognise the tenant logo, not a generic Windows icon, so the tray and window
        // icons are rendered from the same logo the portal and installer use.
        var brandIcon = BrandIcon.Load(_brand);
        _tray = new NotifyIcon { Text = $"{_brand.CompanyName} {_brand.ProductName}", Visible = true, Icon = brandIcon ?? SystemIcons.Application };
        if (brandIcon != null) Icon = brandIcon;
        // The two things a customer ever wants from this icon, and then Exit (stage 3.5.6).
        //
        // It used to be four: Open, Request Support, a separator and Exit. `Open` went because
        // double-clicking the icon already does it and is what people try first; somebody whose
        // machine is misbehaving should not have to work out which line of a menu fetches a
        // person.
        //
        // EXIT STAYS. Clint, 2026-09-18, and he is right: this app runs on somebody else's
        // computer, and a program that watches for support requests and cannot be closed from
        // its own icon is the kind of thing people uninstall rather than trust. It is also the
        // only way to stop it without Task Manager — the installer kills the process itself, and
        // the HKLM Run key starts it again at the next logon, so quitting costs the customer
        // nothing they cannot undo by logging in again.
        var menu = new ContextMenuStrip { Font = _t.Body }.Themed(_t);
        // ONE window, not two. This used to open the tray window and then the request dialog on
        // top of it, so asking for help put two windows on screen — and the one underneath was
        // a window whose only purpose was a button the customer had already pressed. Clint,
        // 2026-09-20, with a screenshot of both: "this should only be one."
        //
        // The dialog is shown with no owner on purpose. Owning it to this window would drag the
        // window up with it, which is the thing being removed.
        _requestItem = new ToolStripMenuItem("Request Support", null, (_, _) => ToggleRequest());
        menu.Items.Add(_requestItem);
        // Opens the chat window itself, rather than opening the tray window so that the chat box
        // inside it can be found. The menu item says chat; it should produce chat.
        menu.Items.Add("Chat with a Technician", null, (_, _) => Chat.Open());
        // The same switch as the one on the window, because this icon is where people who never
        // open the window will look for it. Both drive one path: the box is set from the server's
        // answer, and this item is set from the box.
        _unattendedItem = new ToolStripMenuItem("Allow unattended access") { CheckOnClick = false, Enabled = false };
        _unattendedItem.Click += (_, _) =>
        {
            // Not CheckOnClick: the tick must follow the SERVER, not the click. Driving the
            // window's checkbox means the confirmation, the send and the "put it back if they
            // say no" all live in one place rather than two that can drift.
            _unattendedBox.Checked = !_unattendedBox.Checked;
        };
        menu.Items.Add(_unattendedItem);
        // Right under the switch it qualifies. Set by whoever is at this machine — the person, or a
        // technician working on it in a session — and never from the office (the server refuses).
        _passwordItem = new ToolStripMenuItem("Unattended password…") { Enabled = false };
        _passwordItem.Click += (_, _) => AskUnattendedPassword();
        menu.Items.Add(_passwordItem);
        // Outside help (3.9): a code for somebody outside the IT company — the accountant, another IT
        // company. Hidden, not greyed, where it is not allowed: it is off for most machines, and a
        // greyed item everybody sees and nobody can use is a question nobody can answer.
        _outsiderItem = new ToolStripMenuItem("Let someone else connect…") { Visible = false };
        _outsiderItem.Click += (_, _) => OpenOutsiderCode();
        menu.Items.Add(_outsiderItem);
        // Below a separator, because it is not one of the two: it is the way out, and putting it
        // next to "Request Support" invites the click nobody meant to make.
        menu.Items.Add(new ToolStripSeparator());
        // Exit closes remote support on this machine, not just this window (2026-09-29): the server
        // is told first, so the machine reads as offline everywhere and no technician gets in —
        // not even through the agent service — until this app runs again. Said FIRST and cancelled
        // straight after: cancelling first tears the socket down before the message can go, and the
        // link waits seconds before redialling, so it cannot reconnect in between and undo it.
        // Uninstall (2026-10-06, Clint: "an uninstall option of the client app tray for someone who no longer
        // wants to use the product"). Above Exit, because it is Exit for good.
        menu.Items.Add("Uninstall…", null, async (_, _) => await UninstallAsync());
        menu.Items.Add("Exit", null, async (_, _) =>
        {
            _tray.Visible = false;
            try { await _link.SayClosingAsync().WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
            _cts.Cancel();
            Application.Exit();
        });
        _tray.ContextMenuStrip = menu;
        // The note under the checkbox has to say something from the first moment the window is
        // looked at, not only after the first message from the server arrives.
        UpdateUnattendedUi();
        // "Open" went from the menu but not from the program: double-clicking the icon is how
        // every tray app in Windows opens its window, and it is what people try first.
        _tray.DoubleClick += (_, _) => ShowWindow();

        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Hide();
                _tray.ShowBalloonTip(2000, _brand.ProductName, "Still running in the tray. Right-click the icon to request support.", ToolTipIcon.Info);
            }
        };
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    /// <summary>
    /// The person at the machine no longer wants it (2026-10-06). Asked once; then the SAME Uninstall-Client.ps1
    /// the agent runs when a machine is removed (ClientUninstall), with administrator permission — Windows asks,
    /// and "No" changes nothing. Only once Windows has said yes is the server told (ClientUninstallingMsg), so
    /// the company sees the machine leave its list as "Uninstalled at the machine". The uninstaller then stops
    /// this app along with everything else.
    /// </summary>
    private async Task UninstallAsync()
    {
        if (MessageBox.Show(
                $"Remove {_brand.ProductName} from this computer?\n\n{_brand.CompanyName} will no longer be able to help you "
                + "through it, and will see that it was uninstalled. Windows will ask for administrator permission.",
                $"Uninstall {_brand.ProductName}", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        var (script, args) = ClientUninstall.Command(AppContext.BaseDirectory, Path.GetTempPath());
        if (script == null)
        {
            // An install from before 2026-10-06 did not keep the uninstaller beside the app.
            MessageBox.Show($"This copy cannot uninstall itself. Ask {_brand.CompanyName} to remove it, or install the latest version and uninstall from that.",
                $"Uninstall {_brand.ProductName}", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return; }   // "No" to Windows
        catch (Exception ex)
        {
            MessageBox.Show($"The uninstaller could not start: {ex.Message}", $"Uninstall {_brand.ProductName}", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        // Windows said yes and it is running. Tell the server before it stops this app.
        _tray.Visible = false;
        try { _link.Send(new ClientUninstallingMsg()); await Task.Delay(800); } catch { }
        _cts.Cancel();
        Application.Exit();
    }

    /// <summary>
    /// Is the agent service installed on this machine at all?
    ///
    /// Read from the service's registry key rather than through ServiceController: a standard
    /// user can read HKLM\SYSTEM\CurrentControlSet\Services without any rights being granted to
    /// them, and it needs no extra package. The answer is only used to WORD the explanation —
    /// the server decides what is actually possible, and says so in UnattendedStateMsg.
    /// </summary>
    private static bool AgentServiceInstalled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\RemoteSupportAgent");
            return k != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// The person at this machine ticking or unticking the box.
    ///
    /// Turning it ON asks first, and says plainly what it means, because it is the one setting
    /// here that lets somebody else into this computer while nobody is watching. Turning it OFF
    /// asks nothing: withdrawing permission is always allowed to be easy.
    /// </summary>
    private void UnattendedBoxClicked()
    {
        if (_applyingUnattended) return;          // we set it ourselves from the server's answer

        bool want = _unattendedBox.Checked;
        if (want)
        {
            // A seam, and it exists for one reason: the confirmation is a MODAL DIALOG, and a
            // modal dialog cannot be driven from a test process — it blocks the message loop
            // that the test is pumping. Without this, the single most important behaviour here
            // ("turning it on asks first, and NOT confirming leaves it off") could only ever be
            // checked by hand, which is how it would eventually stop being checked at all.
            // tools/unattendedcheck sets it; nothing in the product does.
            if (ConfirmUnattended != null)
            {
                if (!ConfirmUnattended())
                {
                    _applyingUnattended = true;
                    try { _unattendedBox.Checked = _unattendedOn; } finally { _applyingUnattended = false; }
                    return;
                }
            }
            else
            {
            var answer = MessageBox.Show(this,
                $"Technicians at {_brand.CompanyName} will be able to connect to this computer and "
                + "control it when nobody is sitting at it, without anybody here accepting first.\n\n"
                + "You can switch this off again at any time, from this window or from the icon "
                + "next to the clock.",
                "Allow unattended access?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                // Put the tick back where it was. Guarded, or this would come straight back in
                // here and read as a second click.
                _applyingUnattended = true;
                try { _unattendedBox.Checked = _unattendedOn; } finally { _applyingUnattended = false; }
                return;
            }
            }
        }

        if (!_connected)
        {
            MessageBox.Show(this,
                "This computer is not connected to the support server at the moment, so the setting "
                + "cannot be changed. Try again once the status above says Connected.",
                _brand.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _applyingUnattended = true;
            try { _unattendedBox.Checked = _unattendedOn; } finally { _applyingUnattended = false; }
            return;
        }

        // Ask; do not assume. The tick stays where the person put it so the click feels like it
        // did something, but the SERVER's answer is what lands in _unattendedOn — and if it
        // refuses or never arrives, the next UnattendedStateMsg puts the box back.
        _link.Send(new SetUnattendedMsg { On = want });
    }

    /// <summary>The menu item's click: the password window, then the request.</summary>
    private void AskUnattendedPassword()
    {
        using var dlg = new UnattendedPasswordDialog(_t, _brand, _hasPassword, _unattendedOn);
        if (dlg.ShowDialog() != DialogResult.OK) return;
        SetUnattendedPassword(dlg.Choice == UnattendedPasswordDialog.Outcome.Remove ? "" : dlg.Password);
    }

    /// <summary>
    /// Asks the server to set (or, empty, remove) this machine's unattended password. Separate from
    /// the dialog so tools/unattendedcheck can drive it — the dialog is modal. The server's answer
    /// is what changes <see cref="_hasPassword"/>.
    /// </summary>
    internal bool SetUnattendedPassword(string password)
    {
        if (!_connected)
        {
            MessageBox.Show(this,
                "This computer is not connected to the support server at the moment, so the password "
                + "cannot be changed. Try again once the status says Connected.",
                _brand.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        _link.Send(new SetUnattendedPasswordMsg { Password = password });
        return true;
    }

    // ---- outside help (3.9): a code for somebody outside the IT company ----

    /// <summary>The menu item: the one code window, brought forward if it is already open.</summary>
    private void OpenOutsiderCode()
    {
        if (_codeDialog is { IsDisposed: false }) { _codeDialog.Activate(); return; }
        _codeDialog = new OutsiderCodeDialog(_t, _brand, _contactName);   // the message is signed with their name (§155)
        _codeDialog.MakeCode += who => RequestOutsiderCode(who);
        _codeDialog.StopCode += () => StopOutsiderCode();
        _codeDialog.FormClosed += (_, _) => _codeDialog = null;
        _codeDialog.Show();
    }

    /// <summary>Asks the server for a code. Separate from the window so a harness can drive it.</summary>
    internal bool RequestOutsiderCode(string forWhom)
    {
        if (!_connected)
        {
            _codeDialog?.ShowCode(new OutsiderCodeMsg { Error = "This computer is not connected to the support server at the moment. Try again once it is." });
            return false;
        }
        _link.Send(new OutsiderCodeRequestMsg { For = forWhom });
        return true;
    }

    internal void StopOutsiderCode() { if (_connected) _link.Send(new OutsiderCodeCancelMsg()); }

    private void OnOutsiderCode(OutsiderCodeMsg c)
    {
        if (c.Error.Length == 0 && c.Code.Length > 0)
            AppendSystem($"A code was made for {c.For}. It works once, until {c.ExpiresUtc?.ToLocalTime():HH:mm}.");
        _codeDialog?.ShowCode(c);
    }

    /// <summary>The server's answer: may this machine make a code, and is one live.</summary>
    private void ApplyOutsiderState(OutsiderStateMsg s)
    {
        _outsiderItem.Visible = s.Allowed;
        bool hadLive = _liveCodeFor.Length > 0;
        _liveCodeFor = s.LiveFor;
        // The code on screen stopped working somewhere else: say so on it, rather than leave
        // somebody reading out digits that open nothing.
        if (hadLive && s.LiveFor.Length == 0)
            _codeDialog?.CodeGone(s.Allowed ? "This code no longer works."
                : $"{_brand.CompanyName} has switched this off for this computer, so the code no longer works.");
        else if (!s.Allowed) _codeDialog?.ShowCode(new OutsiderCodeMsg { Error = $"{_brand.CompanyName} has switched this off for this computer." });
    }

    /// <summary>The server's answer: what this machine's unattended access actually is.</summary>
    private void ApplyUnattendedState(UnattendedStateMsg s)
    {
        bool changed = !_unattendedKnown || s.On != _unattendedOn;
        bool pwChanged = _unattendedKnown && s.HasPassword != _hasPassword;
        _hasPassword = s.HasPassword;
        if (s.PasswordProblem.Length > 0) AppendSystem("The unattended password was not changed: " + s.PasswordProblem);
        else if (pwChanged)
            AppendSystem(s.HasPassword
                ? $"An unattended password is set — technicians at {_brand.CompanyName} must type it to connect without you asking."
                : "The unattended password was removed.");
        _unattendedKnown = true;
        _unattendedOn = s.On;
        _agentOnline = s.AgentOnline;
        _awaitingConfirm = s.AwaitingConfirm;

        _applyingUnattended = true;
        try
        {
            _unattendedBox.Checked = s.On;
            _unattendedItem.Checked = s.On;
        }
        finally { _applyingUnattended = false; }

        UpdateUnattendedUi();
        ApplyInstallerRequestOnce();

        // Said out loud in the chat log, which is this app's record of everything that has
        // happened on this machine — and the same log the ticket carries. A permission that
        // changes silently is one nobody can account for afterwards.
        if (changed)
            AppendSystem(s.On
                ? $"Unattended access is ON — technicians at {_brand.CompanyName} can connect when nobody is at this computer."
                : "Unattended access is off — technicians can only connect when you ask for support.");
    }

    /// <summary>
    /// Carries out `Install-Client.ps1 -Unattended` — the one case where unattended access is
    /// allowed without somebody ticking a box, because an administrator asked for it at install
    /// time on a machine their company owns.
    ///
    /// It happens ONCE. The request lives in HKLM, where a standard user cannot forge it, and it
    /// cannot be deleted from here for the same reason — so what is recorded instead is that we
    /// have acted on it, in this user's own folder. Without that record, a customer who turned
    /// unattended access OFF would find it back on at the next logon, for ever, and nothing on
    /// screen would explain why.
    /// </summary>
    private void ApplyInstallerRequestOnce()
    {
        if (_unattendedOn) return;
        try
        {
            var done = Path.Combine(DeviceIdentity.UserDir, "unattended-requested-applied");
            if (File.Exists(done)) return;

            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TheRemoteSupportProgram");
            if (k?.GetValue("UnattendedRequested") is not int v || v != 1) return;

            Directory.CreateDirectory(DeviceIdentity.UserDir);
            File.WriteAllText(done, DateTimeOffset.Now.ToString("o"));
            _link.Send(new SetUnattendedMsg { On = true });
            AppendSystem("This computer was installed with unattended access requested, so it has been switched on.");
        }
        catch { }   // a convenience at install time; never a reason for the app not to run
    }

    /// <summary>
    /// Keeps the checkbox, its explanation and the tray item saying the same thing.
    ///
    /// The decision itself lives in <see cref="UnattendedWording"/>, so that every state it can
    /// be in is reachable from a test on a machine with no agent service installed — which is
    /// every development machine here, and is exactly the state the 2026-09-21 fault lives in.
    /// </summary>
    private void UpdateUnattendedUi()
    {
        var ui = UnattendedWording.For(
            serviceInstalled: AgentServiceInstalled(),
            agentOnline: _agentOnline,
            serverKnows: _unattendedKnown,
            connected: _connected,
            on: _unattendedOn,
            companyName: _brand.CompanyName,
            hasPassword: _hasPassword,
            awaitingConfirm: _awaitingConfirm);

        // Needs only the server: a password can be set before unattended access is switched on.
        _passwordItem.Enabled = _unattendedKnown && _connected;
        _passwordItem.Text = _hasPassword ? "Unattended password — set…" : "Unattended password…";
        _unattendedBox.Enabled = ui.Usable;
        _unattendedItem.Enabled = ui.Usable;
        // The note is set first and the tooltip reads from it — the order the first version of
        // this got wrong, which would have shown the previous state for ever.
        _unattendedNote.Text = ui.Note;
        _unattendedItem.ToolTipText = ui.Note;
        _unattendedItem.Text = ui.MenuText;
    }

    private void WireLink()
    {
        _link.OnConnectedChanged += c => Inv(() =>
        {
            // A successful connection clears any previous refusal: whatever it was has been fixed.
            if (c && _serverRefusal != null) { _serverRefusal = null; AppendSystem("Connected to the support server again."); }
            _connected = c; UpdateStatus();
            // The switch cannot be operated while there is nothing to send it to, and the note
            // under it has to say which of the several reasons that is.
            UpdateUnattendedUi();
        });
        // The server turning this machine away, in its own words. Said once per distinct reason —
        // DeviceLink keeps retrying (more slowly), and repeating the same line every thirty
        // seconds would bury the chat log in it.
        _link.OnServerError += why => Inv(() =>
        {
            if (_serverRefusal == why) return;
            _serverRefusal = why;
            AppendSystem(why);
            _tray?.ShowBalloonTip(6000, _brand.ProductName, why, ToolTipIcon.Warning);
            UpdateStatus();
        });
        _link.OnBranding += m => Inv(() => OnBranding(m));
        // Auto-update (2026-10-16): the agent swaps a newer tray app in beside this running one; restart into it when idle.
        _updateTimer.Tick += (_, _) => UpdateTick();
        _updateTimer.Start();
        _link.OnUnattendedState += s => Inv(() => ApplyUnattendedState(s));
        _link.OnOutsiderState += s => Inv(() => ApplyOutsiderState(s));
        _link.OnOutsiderCode += c => Inv(() => OnOutsiderCode(c));
        _link.OnCallUpdate += cu => Inv(() => OnCallUpdate(cu.Call));
        _link.OnChat += ch => Inv(() =>
        {
            AppendChat(ch);
            // A message from a technician pops the corner window open. Our own echo only goes
            // into it if it is already open — otherwise typing in the tray window would throw a
            // popup in the person's face.
            if (ch.FromTech) { Chat.Incoming(ch); Sounds.Ding(); }
            else if (_chat is { Visible: true }) _chat.Outgoing(ch.Text);
        });
        _link.OnStartSession += s => Inv(() =>
        {
            _sessionActive = true; _ringing = false;
            // A non-empty token means this app is the session host (attended support).
            if (!string.IsNullOrEmpty(s.HostToken))
            {
                // The server can insist on asking (somebody from outside, 3.9); the branding never un-asks.
                bool ask = _brand.RequireConsent || s.AskConsent;
                HostSpawner.Start(_wsBase, s.SessionId, s.HostToken, ask, s.TechnicianName, s.VisitorCompany);
                AppendSystem(ask
                    ? $"{s.TechnicianName} is requesting to connect. Please respond to the prompt on your screen."
                    : $"{s.TechnicianName} has connected and can now see your screen.");
            }
            else AppendSystem($"{s.TechnicianName} has connected and can now see your screen.");
            // The chat log is not a disclosure if the window is minimised, so say it on screen too.
            _technicianName = s.TechnicianName;
            _sessionHeld = false;
            Banner.ShowInProgress(_brand.ProductName, s.TechnicianName);
            UpdateStatus();
        });
        // What is actually happening on this machine, sent by the server whenever it changes —
        // and, crucially, whenever THIS connection appears. DeviceLink reconnects on its own and
        // nothing used to re-send it anything, so a tray app whose socket blinked went on saying
        // "support in progress" long after everybody had gone (BUGS #2).
        _link.OnSessionState += st => Inv(() =>
        {
            if (!st.Active)
            {
                if (!_sessionActive && !_sessionHeld) return;   // nothing to correct
                HostSpawner.Stop(st.SessionId);
                _sessionActive = false; _sessionHeld = false; _beingListenedTo = false;
                _inCall = false; _lastMic = ""; _mic?.Withdraw();
                AppendSystem("The support session has ended.");
                Banner.ShowEnded(_brand.ProductName);
                UpdateStatus();
                return;
            }

            bool wasActive = _sessionActive, wasHeld = _sessionHeld;
            _sessionActive = true;
            _sessionHeld = st.Held;
            if (!string.IsNullOrWhiteSpace(st.TechnicianName)) _technicianName = st.TechnicianName;
            _beingListenedTo = st.Listening;

            if (st.Held && !wasHeld)
            {
                AppendSystem($"{_technicianName}'s connection has dropped. Waiting a moment to see if it comes back.");
                Banner.ShowHeld(_brand.ProductName, _technicianName);
            }
            else if (!st.Held && (wasHeld || !wasActive))
            {
                if (wasHeld) AppendSystem($"{_technicianName} is connected again.");
                if (_beingListenedTo) Banner.ShowListening(_brand.ProductName, _technicianName);
                else Banner.ShowInProgress(_brand.ProductName, _technicianName);
            }
            UpdateStatus();
        });
        // A technician listening to this machine's sound is disclosed while it is happening.
        // The host reports it on the session socket and the hub carries it here; without that
        // relay the person at the machine would have no way at all of knowing.
        _link.OnAudioStatus += a => Inv(() =>
        {
            bool listening = a.Listening && a.Error.Length == 0;
            if (listening == _beingListenedTo) return;   // the host repeats status on device changes
            _beingListenedTo = listening;
            if (listening)
            {
                AppendSystem($"{_technicianName} has started listening to this computer's sound.");
                Banner.ShowListening(_brand.ProductName, _technicianName);
                // A balloon as well: the banner states it, but somebody looking at a full-screen
                // window needs the notification to pull their eye to it.
                _tray?.ShowBalloonTip(5000, _brand.ProductName,
                    $"{_technicianName} can now hear this computer's sound.", ToolTipIcon.Warning);
            }
            else
            {
                AppendSystem($"{_technicianName} has stopped listening to this computer's sound.");
                // Back to the ordinary in-progress message rather than hiding: the session itself
                // is still running, and saying nothing would read as "nobody is connected".
                if (_sessionActive) Banner.ShowInProgress(_brand.ProductName, _technicianName);
            }
            UpdateStatus();
        });
        // Files are logged, not announced. Clint's decision on 2026-09-15, and the reason it is a
        // decision rather than a default: a technician can pull a file off a machine with nothing
        // at all on the customer's screen, which is not obviously different in kind from
        // listening — but a banner for every file would interrupt the person while they are being
        // helped, several times in a normal job. So it lands in the chat log and on the ticket,
        // where somebody can go and read it, and nowhere that steals attention: no banner, no
        // balloon, no change to the status pill.
        _link.OnFileActivity += fa => Inv(() => AppendSystem(fa.Describe(_technicianName)));
        // Being recorded is told to the person at the machine, the same way a file taken off it is
        // (1.6, Clint 2026-09-29): logged here and on the ticket, not announced with a banner.
        _link.OnRecordingState += rs => Inv(() => AppendSystem(rs.On
            ? $"{(string.IsNullOrWhiteSpace(rs.By) ? "The technician" : rs.By)} started recording this session."
            : $"{(string.IsNullOrWhiteSpace(rs.By) ? "The technician" : rs.By)} stopped recording."));
        // Somebody from outside the IT company joining at the technician's request (§155). Clint: the person at the
        // machine is TOLD, not asked — the chat log keeps it and a balloon pulls the eye, as for listening.
        _link.OnOutsiderJoined += oj => Inv(() =>
        {
            AppendSystem(oj.Describe());
            _tray?.ShowBalloonTip(oj.Left ? 4000 : 8000, _brand.ProductName, oj.Describe(), oj.Left ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
        _link.OnVoiceState += v => Inv(() => ShowVoice(v));
        _link.OnSessionEnded += e => Inv(() =>
        {
            _inCall = false; _lastMic = "";
            HostSpawner.Stop(e.SessionId);
            _sessionActive = false; _ringing = false; _sessionHeld = false;
            // Clear the listening state with the session. The host dies with the session and
            // cannot send a closing "no longer listening", so leaving this set would make the
            // tray window claim somebody was listening long after everybody had gone.
            _beingListenedTo = false;
            AppendSystem("The support session has ended.");
            // Say it where the chat log cannot: the strip states it, then takes itself away.
            Banner.ShowEnded(_brand.ProductName);
            _tray?.ShowBalloonTip(4000, _brand.ProductName, "The remote support session has ended.", ToolTipIcon.Info);
            UpdateStatus();
        });
    }

    private void ToggleRequest()
    {
        if (_sessionActive) return;
        if (_ringing) { _link.CancelCall(); _ringing = false; AppendSystem("Support request cancelled."); UpdateStatus(); return; }

        // ShowDialog with no owner: from the tray there IS no window on screen, and giving it
        // this one as an owner would pull the tray window up behind it — the second screen that
        // should not be there.
        // The picture of their screen FIRST (2026-10-09), so it shows the problem rather than our window — this one,
        // when it is up, steps out of the way for the moment it takes. None if the screen cannot be read.
        using var shot = ScreenSnap.Take(Visible ? this : null);
        using var dlg = new RequestDialog(_contactName, _t, _brand, shot, LastContact.EmailFor(_contactName, GivenPerson.Installed()))
        {
            StartPosition = Visible ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen
        };
        if (dlg.ShowDialog(Visible ? this : null) != DialogResult.OK) return;
        _contactName = string.IsNullOrWhiteSpace(dlg.ContactName) ? Environment.UserName : dlg.ContactName;
        // Remembered only once it is actually sent, and only what they typed — a blank is not a name to come back to.
        LastContact.Remember(dlg.ContactName, dlg.ContactEmail);
        _link.RequestCall(_contactName, dlg.Subject, dlg.Details, dlg.Screenshot, dlg.ContactEmail);
        _ringing = true;
        AppendSystem("Requesting support… ringing our technicians.");
        UpdateStatus();

        // The customer asked from the tray and has nothing on screen now, so say what is
        // happening where they are looking. "Please keep this window open" went with the window:
        // there may not be one, and the app keeps ringing from the tray regardless.
        if (!Visible)
            _tray.ShowBalloonTip(4000, _brand.ProductName,
                "Requesting support — ringing the technicians. You can carry on working; "
                + "right-click this icon to cancel.", ToolTipIcon.Info);
    }

    private void OnCallUpdate(CallInfo call)
    {
        switch (call.Status)
        {
            case CallStatus.Accepted: _ringing = false; AppendSystem($"{call.AcceptedBy} accepted your request."); break;
            case CallStatus.Cancelled: if (_ringing) { _ringing = false; AppendSystem("Your request was cancelled."); } break;
            case CallStatus.Ended: _sessionActive = false; break;
        }
        UpdateStatus();
    }

    private void SendChat()
    {
        var text = _chatInput.Value.Trim();
        if (text.Length == 0) return;
        _link.SendChat(_contactName, text);
        _chatInput.Value = "";
    }

    private void AppendChat(ChatMsg m)
    {
        var who = m.FromTech ? m.From : "You";
        var color = m.FromTech ? _t.Grape : Color.FromArgb(0x0F, 0x76, 0x5C);
        _chatLog.SelectionStart = _chatLog.TextLength;
        _chatLog.SelectionColor = _t.Muted;
        _chatLog.SelectionFont = _t.Small;
        _chatLog.AppendText($"{m.Ts.ToLocalTime():HH:mm}  ");
        _chatLog.SelectionColor = color; _chatLog.SelectionFont = _t.BodyBold;
        _chatLog.AppendText($"{who}: ");
        _chatLog.SelectionFont = _t.Body; _chatLog.SelectionColor = _t.Ink;
        _chatLog.AppendText(m.Text + "\n");
        _chatLog.ScrollToCaret();
        if (m.FromTech && !Visible) _tray.ShowBalloonTip(3000, m.From, m.Text, ToolTipIcon.Info);
    }

    private void AppendSystem(string text)
    {
        // Mirror into the corner chat only when it is already open — never open it for this, the
        // banner is what announces a session.
        if (_chat is { Visible: true }) _chat.System_(text);
        _chatLog.SelectionStart = _chatLog.TextLength;
        _chatLog.SelectionColor = _t.Muted;
        _chatLog.SelectionFont = new Font("Segoe UI", 9f, FontStyle.Italic);
        _chatLog.AppendText($"— {text}\n");
        _chatLog.SelectionFont = _t.Body;
        _chatLog.ScrollToCaret();
    }

    /// <summary>Resize the pill to hug its text so it never stretches across the card.</summary>
    private void FitPill()
    {
        using var g = _statusPill.CreateGraphics();
        var w = TextRenderer.MeasureText(g, _statusPill.Text, _statusPill.Font).Width + 26;
        _statusPill.Width = Math.Max(120, Math.Min(w, 320));
    }

    private void UpdateStatus()
    {
        if (_sessionActive && _sessionHeld)
        {
            // Amber, and it says what is true: the session has not ended, but nobody is looking
            // at this screen at this moment.
            _statusPill.Set("● Support session — technician's connection lost", _t.WarnBg, _t.WarnText);
            _requestBtn.Text = "Support In Progress"; _requestBtn.Enabled = false;
        }
        else if (_sessionActive && _beingListenedTo)
        {
            // Louder than the ordinary in-progress state, and amber rather than green: this is
            // the one thing happening on the machine that has no other outward sign at all.
            _statusPill.Set("● Support session — your sound is being heard", _t.WarnBg, _t.WarnText);
            _requestBtn.Text = "Support In Progress"; _requestBtn.Enabled = false;
        }
        else if (_sessionActive)
        {
            _statusPill.Set("● Support session in progress", _t.OkBg, _t.OkText);
            _requestBtn.Text = "Support In Progress"; _requestBtn.Enabled = false;
        }
        else if (_ringing)
        {
            _statusPill.Set("● Ringing technicians…", _t.WarnBg, _t.WarnText);
            _requestBtn.Text = "Cancel Request"; _requestBtn.Enabled = true; _requestBtn.Style = BtnStyle.Danger;
        }
        else if (_serverRefusal != null)
        {
            // The server turned this machine away and said why. Showing the plain "Not connected"
            // here instead would be the difference between a customer who can read the reason out
            // over the phone and one who can only say "it stopped working" — which is all anybody
            // could say before this, on either end.
            _statusPill.Set("● " + _serverRefusal, _t.WarnBg, _t.WarnText);
            _requestBtn.Text = "Request Support"; _requestBtn.Enabled = false; _requestBtn.Style = BtnStyle.Accent;
        }
        else if (_connected)
        {
            _statusPill.Set("● Connected — ready", _t.OkBg, _t.OkText);
            _requestBtn.Text = "Request Support"; _requestBtn.Enabled = true; _requestBtn.Style = BtnStyle.Accent;
        }
        else
        {
            _statusPill.Set("○ Connecting to support server…", _t.WarnBg, _t.WarnText);
            _requestBtn.Text = "Request Support"; _requestBtn.Enabled = false; _requestBtn.Style = BtnStyle.Accent;
        }
        FitPill();
        _requestBtn.Invalidate();

        // The tray item says the same thing as the button, because since the request dialog
        // stopped opening the window there may BE no button on screen. Somebody who asked for
        // help from the icon has to be able to cancel from the icon.
        if (_requestItem != null)
        {
            _requestItem.Text = _sessionActive ? "Support in progress" : _ringing ? "Cancel request" : "Request Support";
            _requestItem.Enabled = _ringing || (_connected && !_sessionActive && _serverRefusal == null);
        }
    }

    // ---- the company's look, changed since this was installed (2026-10-07, roadmap 4.6) ----
    // Every window here is built from the branding once, at start, so a new look is shown by starting again — quietly,
    // from the tray, and only when nobody is using this app: no session, no request waiting, no window or menu open.
    // It restarts for what is ON DISK, never for the message alone: in Program Files only the agent can keep the look,
    // and restarting into the same old branding would restart for ever.

    // ---- a newer tray app on disk (2026-10-16, auto-update stage D) ----
    // The agent puts a release in place by renaming this running program aside and copying the new one in (UpdateSwap). This
    // copy carries on from its renamed file; once a minute it looks at what is in its place now, and restarts into it on
    // the same terms as a new look — nobody in a session, nobody ringing, no window open. Never asked: silent, by decision.
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60_000 };

    private void UpdateTick()
    {
        string onDisk;
        // From the file's bytes, never FileVersion: asked about this program's own path, Windows answers from the copy
        // running in memory — the old version — and the tray app never restarted into an update (2026-10-19).
        try { onDisk = UpdateTrust.FileVersionOnDisk(Path.Combine(AppContext.BaseDirectory, "RemoteSupport.Client.exe")); }
        catch { return; }
        if (!AppVersion.IsStamped(onDisk) || AppVersion.Compare(onDisk, AppVersion.Current) <= 0) return;
        if (_sessionActive || _ringing || AnyWindowShowing()) return;
        _updateTimer.Stop();
        RestartForBranding();
    }

    private System.Windows.Forms.Timer? _brandTimer;
    private DateTime _brandGiveUpUtc;
    /// <summary>How long to look for the agent to have kept a new look. It keeps it within moments; one that never
    /// does (no agent running) leaves this app as it is, and the next connect tries again.</summary>
    private static readonly TimeSpan BrandWait = TimeSpan.FromMinutes(10);

    private void OnBranding(BrandingMsg m)
    {
        if (m.Hash == _brand.LiveHash) return;
        // Kept here only where this app may write — a copy run from a folder of its own. In Program Files this fails,
        // as it should: the agent, running as SYSTEM, has been sent the same and keeps it there.
        BrandRefresh.Save(_brand.BaseDirectory, m, out _);
        _brandGiveUpUtc = DateTime.UtcNow + BrandWait;
        if (_brandTimer == null)
        {
            _brandTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _brandTimer.Tick += (_, _) => BrandTick();
        }
        _brandTimer.Start();
    }

    private void BrandTick()
    {
        var onDisk = BrandRefresh.HashOn(_brand.BaseDirectory);
        if (onDisk.Length == 0 || onDisk == _brand.LiveHash)
        {
            if (DateTime.UtcNow > _brandGiveUpUtc) _brandTimer?.Stop();
            return;
        }
        // A new look is kept: a reconnect before the restart asks for nothing again. Then waits — with no deadline
        // now — until nobody is in the middle of anything.
        _link.BrandHash = onDisk;
        if (_sessionActive || _ringing || AnyWindowShowing()) return;
        _brandTimer?.Stop();
        RestartForBranding();
    }

    /// <summary>A new copy of this app, hidden in the tray, which waits for this one to go (<c>--after</c>). The
    /// server is NOT told this machine is closing — that is Exit, and this is not Exit.</summary>
    private void RestartForBranding()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, $"--tray --after {Environment.ProcessId}")
            { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch { return; }   // no new copy: this one stays, in the old look
        _tray.Visible = false;
        _cts.Cancel();
        Application.Exit();
    }

    /// <summary>Any window of this app on screen — this one, the chat corner, the banner, a question, a message box,
    /// the tray menu. Asked of Windows, so nothing has to remember to say so. Minimised does not count: started in the
    /// tray, this window is "visible" to Windows the whole time, minimised at -32000 (measured, 2026-10-07).</summary>
    private static bool AnyWindowShowing()
    {
        bool any = false;
        EnumThreadWindows(GetCurrentThreadId(), (h, _) => { if (IsWindowVisible(h) && !IsIconic(h)) { any = true; return false; } return true; }, IntPtr.Zero);
        return any;
    }
    private delegate bool EnumThreadWndProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumThreadWndProc callback, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        _tray.Visible = false;
        // These are separate top-level windows; without this they outlive the app on screen.
        try { _banner?.HideNow(); _banner?.Dispose(); } catch { }
        try { _chat?.HideNow(); _chat?.Dispose(); } catch { }
        base.OnFormClosed(e);
    }
}
