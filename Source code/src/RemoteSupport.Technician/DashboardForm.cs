using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>Technician home: incoming calls, devices (attended + unattended), active sessions and chat.</summary>
public sealed class DashboardForm : Form
{
    private readonly Branding _brand;
    private readonly TechLink _link;
    private readonly CancellationTokenSource _cts = new();
    private readonly Theme _t;

    private ListView _calls = null!;
    private ListView _devices = null!;
    private ListView _sessions = null!;
    private RichTextBox _chatLog = null!;
    private InputBox _chatInput = null!;
    private ComboBox _chatTarget = null!;
    private Pill _statusPill = null!;
    private Label _techsLabel = null!;
    private Card _callsCard = null!;
    private RoundButton _tabAttended = null!, _tabUnattended = null!;
    /// <summary>Disabled while a portal hand-off is in flight, so two clicks do not open two tabs.</summary>
    private RoundButton _ticketsBtn = null!;
    /// <summary>Latest activity (2026-10-26, HANDOVER §164): every update on the tickets, with what is new to me since I looked.</summary>
    private RoundButton _activityBtn = null!;
    private RoundButton _staffChatBtn = null!;
    /// <summary>Office chat: made at sign-in and kept, hidden, so a message can pop it up.</summary>
    private StaffChatForm _staffChat = null!;
    private RoundButton _newTicketBtn = null!;
    /// <summary>Light or dark, the person's own. Offers whichever one they have NOT saved.</summary>
    private RoundButton _modeBtn = null!;
    /// <summary>Mute or unmute the ding and the ring (2026-10-07). Worded as what it does, like Light/Dark.</summary>
    private RoundButton _soundBtn = null!;
    private ToolStripMenuItem _traySoundItem = null!;
    private RoundButton _signOutBtn = null!;
    private RoundButton _webChatBtn = null!;
    /// <summary>The knowledge base (3.8.5), opened in the browser as this person.</summary>
    private RoundButton _kbBtn = null!;
    /// <summary>The free version only: to the portal's Upgrade page.</summary>
    private RoundButton _upgradeBtn = null!;
    /// <summary>Auto-update (2026-10-16): a newer console is ready for this company; restarts the console to take it.</summary>
    private RoundButton _updateBtn = null!;
    private bool _updateReady;
    /// <summary>The computers the Basic version pays for (StateMsg.MaxMachines, 2026-10-25).</summary>
    private int _basicMachines = FreePlan.MaxMachines;
    /// <summary>The free version (2026-10-06, StateMsg.Free): only what it has is drawn — see ApplyFree.</summary>
    private bool _free;
    /// <summary>The one-person plan is the Basic version, not the Free one (StateMsg.Basic, §163): attended computers too.</summary>
    private bool _basic;
    /// <summary>A supervisor or the owner on the full version: Remove, with a reason (StateMsg.MayRemoveDevices).</summary>
    private bool _mayRemove;
    /// <summary>A supervisor or the owner: may confirm a reinstalled machine is the same one (StateMsg.MayConfirmReinstall).</summary>
    private bool _mayConfirmReinstall;
    private Card _chatCard = null!;
    private RoundButton _codeBtn = null!;
    private SectionTitle _devicesTitle = null!;
    /// <summary>Light/Dark on the tray menu, so it is still there when the header has no room for its button.</summary>
    private ToolStripMenuItem _trayModeItem = null!;
    /// <summary>
    /// The least room the header keeps for its words before Light/Dark gives up its place: enough
    /// for the start of the company's name and "Technician Console", measured from the body font.
    /// </summary>
    private int HeaderWordsRoom => TextRenderer.MeasureText("Technician Console", _t.Body).Width;
    /// <summary>Every session window open, so the knowledge base's answer reaches the right one.</summary>
    private readonly List<SessionForm> _sessionWindows = new();
    /// <summary>Website chat (3.8.3): made at sign-in and kept, hidden, so a visitor can pop it up.</summary>
    private WebChatForm _webChat = null!;
    /// <summary>Lays the header's buttons out again — when one appears or goes, not only on a resize.</summary>
    private Action _layoutHeader = () => { };
    private int _webWaiting;
    /// <summary>Whether the Website chat button is meant to show. A flag, not Visible, which reads
    /// false for every control until the window is on screen.</summary>
    private bool _webChatShown;
    /// <summary>Set by Sign out, and read by Program once the window has closed: start again at the
    /// sign-in window rather than leaving.</summary>
    public bool SignedOut { get; private set; }
    private readonly ToolTip _modeTip = new();
    /// <summary>What a card says instead of an empty table, so it can shrink to one line.</summary>
    private Label _callsQuiet = null!, _sessionsQuiet = null!;
    /// <summary>Re-shares the left column's height. Set once the layout exists; called whenever a
    /// card's contents change, because a card with nothing in it wants far less room.</summary>
    private Action _relayout = () => { };
    /// <summary>Which of the two device tabs is showing. Attended first: it is the larger list on
    /// a normal day, and it is the one a machine starts on before anybody allows anything.</summary>
    private DeviceTab _tab = DeviceTab.Attended;
    /// <summary>Which customers the technician has collapsed, remembered across refreshes — a
    /// device update arriving must not re-open a block somebody deliberately folded away.</summary>
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, CallInfo> _callMap = new();
    private readonly Dictionary<string, DeviceInfo> _deviceMap = new();
    private readonly Dictionary<string, SessionInfo> _sessionMap = new();
    private readonly HashSet<string> _openSessions = new();
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 600 };
    private bool _blinkOn;

    private IncomingCallPopup? _popup;
    private NotifyIcon _tray = null!;
    /// <summary>True only while the tray menu Exit is closing the window. The X button hides
    /// instead of closing, and without this flag there would be no way out at all.</summary>
    private bool _exiting;
    /// <summary>Calls this technician chose to dismiss — the alert stays hidden for them.</summary>
    private readonly HashSet<string> _dismissedCalls = new();

    public DashboardForm(Branding brand, TechLink link)
    {
        _brand = brand; _link = link;
        _t = Theme.FromBranding(brand, AppSurface.Technician);
        BuildUi();
        BuildTray();
        Wire();
        _ = _link.RunAsync(_cts.Token);
        _blink.Tick += (_, _) => BlinkCalls();
        _blink.Start();
    }

    /// <summary>
    /// The main window's title, with this console's own version on the end (2026-10-16, auto-update stage A) — the one
    /// place a technician can read which build they are running. Left off a development build, which says 1.0.0.0.
    /// </summary>
    private string ConsoleTitle =>
        $"{_brand.CompanyName} — Technician Console  ({_link.DisplayName})"
        + (AppVersion.IsStamped(AppVersion.Current) ? $"  ·  version {AppVersion.Current}" : "");

    private void BuildUi()
    {
        Text = ConsoleTitle;
        Size = new Size(1240, 820);
        StartPosition = FormStartPosition.CenterScreen;
        Font = _t.Body;
        BackColor = _t.Mist;
        Shown += (_, _) =>
        {
            NativeDark.Apply(this, _t);   // dark title bar, scrollbars and list header — see NativeDark
            // ...and then take the column rules back off the three lists. Apply is what puts them
            // there, so this has to come after it. See NativeDark.NoColumnRules.
            NativeDark.NoColumnRules(_calls);
            NativeDark.NoColumnRules(_devices);
            NativeDark.NoColumnRules(_sessions);
            // The customer headings are Windows' to draw, and it drew them black on the dark list
            // (technicians, 2026-09-30). See GroupHeaders.
            GroupHeaders.Attach(_devices, _t);
        };
        WindowState = FormWindowState.Maximized;

        Image? logo = null;
        try { if (_brand.HasLogo) logo = Image.FromFile(_brand.LogoPath); } catch { }
        var header = new BrandHeader(_t, logo, _brand.CompanyName, $"Technician Console — {_link.DisplayName}", 84);
        Controls.Add(header);

        _statusPill = new Pill(_t, "connecting…", _t.WarnBg, _t.WarnText) { Width = 190, Height = 26, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        header.Controls.Add(_statusPill);
        // Wide enough for "Tickets (99)", measured (§30), so the header does not shuffle when an
        // @mention arrives and the count appears.
        var ticketsWidth = Math.Max(110, TextRenderer.MeasureText("Tickets (99)", _t.ButtonFont).Width + 36);
        _ticketsBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Tickets", Size = new Size(ticketsWidth, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _ticketsBtn.Click += (_, _) => OpenTickets();
        header.Controls.Add(_ticketsBtn);
        // Latest activity (2026-10-26, HANDOVER §164), beside Tickets: what was posted on the tickets, newest first. The
        // count is what is new since I last looked; measured for its widest wording so the header does not shuffle.
        var activityWidth = TextRenderer.MeasureText("Latest activity (99+)", _t.ButtonFont).Width + 36;
        _activityBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Latest activity", Size = new Size(activityWidth, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _activityBtn.Click += (_, _) => ShowActivity();
        header.Controls.Add(_activityBtn);
        _activityTip.SetToolTip(_activityBtn, "Every update on the tickets as it is posted — comments, stage changes, customer replies, assignments");
        // Office chat (3.8.1): people at the office, not customers — the customer chat is the card on
        // the right. Measured for its widest wording, like Tickets.
        var chatWidth = Math.Max(120, TextRenderer.MeasureText("Office chat (99)", _t.ButtonFont).Width + 36);
        _staffChatBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Office chat", Size = new Size(chatWidth, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _staffChatBtn.Click += (_, _) => _staffChat.ShowFor();
        header.Controls.Add(_staffChatBtn);
        // Website chat (3.8.3): visitors to the company's own website. Shown only while the company has
        // it switched on, so a console that does not use it has one button fewer. Measured for its
        // widest wording, like the others (§30).
        var webWidth = TextRenderer.MeasureText("Website chat (99)", _t.ButtonFont).Width + 36;
        _webChatBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Website chat", Size = new Size(webWidth, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right, Visible = false };
        _webChatBtn.Click += (_, _) => _webChat.ShowFor();
        header.Controls.Add(_webChatBtn);
        // The knowledge base (3.8.5) — Clint: "an option on all user's dashboards". It lives in the
        // browser, where articles are written with pictures; this signs them straight in to it.
        _kbBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Knowledge base", Size = new Size(TextRenderer.MeasureText("Knowledge base", _t.ButtonFont).Width + 36, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _kbBtn.Click += (_, _) => _link.RequestPortalLink("/portal/kb");
        header.Controls.Add(_kbBtn);
        // The free version's way to the full one (Stage G, 2026-10-06): the portal's Upgrade page, signed straight in.
        _upgradeBtn = new RoundButton(_t, BtnStyle.Accent) { Text = "Upgrade", Size = new Size(TextRenderer.MeasureText("Upgrade", _t.ButtonFont).Width + 36, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right, Visible = false };
        _upgradeBtn.Click += (_, _) => _link.RequestPortalLink("/portal/upgrade");
        header.Controls.Add(_upgradeBtn);
        // Auto-update (2026-10-16, stage E): shown only while a newer console is ready. A restart takes it — the check at start
        // updates before the sign-in window — so it asks first, and warns when that would end a session.
        _updateBtn = new RoundButton(_t, BtnStyle.Accent) { Text = "Update ready", Size = new Size(TextRenderer.MeasureText("Update ready", _t.ButtonFont).Width + 36, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right, Visible = false };
        _updateBtn.Click += (_, _) => RestartToUpdate();
        header.Controls.Add(_updateBtn);
        // Booking a machine in at the counter stays in the console. It used to live inside
        // TicketsForm, which nothing opens any more, so without this the console lost its ability
        // to raise a job at all — and Clint asked for BOTH ways of booking a workshop machine in.
        _newTicketBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "New ticket", Size = new Size(126, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _newTicketBtn.Click += (_, _) => NewTicket();
        header.Controls.Add(_newTicketBtn);
        // Their own light or dark, the same setting as the portal's switch. Worded as the thing it
        // DOES, like the portal's — "Light mode" on a dark console — and wide enough for either
        // wording, measured, so the header does not shuffle when it flips.
        var modeWidth = new[] { "Light mode", "Dark mode" }.Max(s => TextRenderer.MeasureText(s, _t.ButtonFont).Width) + 36;
        _modeBtn = new RoundButton(_t, BtnStyle.Neutral) { Size = new Size(modeWidth, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _modeBtn.Click += async (_, _) => await SwitchAppearance();
        header.Controls.Add(_modeBtn);
        ShowModeOffer();
        // The ding and the ring (2026-10-07), muted or not on this PC. A bell, not words: the header
        // was already full, and "Mute sounds" in words pushed Light/Dark off it on a 1920 px screen at
        // 150%. The tooltip and the tray menu carry the words. Square, from the button's own height.
        int soundSide = ButtonHeight(_t.ButtonFont, 10);
        _soundBtn = new RoundButton(_t, BtnStyle.Neutral) { Size = new Size(soundSide + 8, soundSide), Anchor = AnchorStyles.Top | AnchorStyles.Right, AccessibleName = "Mute sounds" };
        _soundBtn.Click += (_, _) => Sounds.Muted = !Sounds.Muted;
        header.Controls.Add(_soundBtn);
        Sounds.MutedChanged += ShowSoundOffer;
        ShowSoundOffer();
        // Sign out (technicians, 2026-09-30: "there is also no logout for technicians. closing the
        // console simply minimizes it"). The X still goes to the tray, on purpose — see BuildTray —
        // so this is the visible way out: back to the sign-in window, username already filled in.
        // Far right, where a sign-out is looked for; measured for its words (§30).
        _signOutBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Sign out", Size = new Size(TextRenderer.MeasureText("Sign out", _t.ButtonFont).Width + 36, ButtonHeight(_t.ButtonFont, 10)), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _signOutBtn.Click += (_, _) => SignOut();
        header.Controls.Add(_signOutBtn);
        void LayoutHeader()
        {
            _signOutBtn.Left = header.Width - _signOutBtn.Width - 20;
            _signOutBtn.Top = (header.Height - _signOutBtn.Height) / 2;
            _statusPill.Left = _signOutBtn.Left - _statusPill.Width - 14;
            _statusPill.Top = (header.Height - _statusPill.Height) / 2;
            // Right to left, each beside the last one SHOWN: the free version (2026-10-06) hides the
            // ticket, chat and knowledge-base buttons, and a gap where each was would read as a fault.
            int top = (header.Height - _ticketsBtn.Height) / 2;
            int next = _statusPill.Left - 4;
            foreach (var b in new[] { _updateBtn, _upgradeBtn, _ticketsBtn, _activityBtn, _staffChatBtn, _webChatBtn, _kbBtn, _newTicketBtn })
            {
                bool shown = b == _updateBtn ? _updateReady : b == _upgradeBtn ? _free : !_free && (b != _webChatBtn || _webChatShown);
                b.Visible = shown;
                if (!shown) continue;
                b.Left = next - b.Width - 10;
                b.Top = top;
                next = b.Left;
            }
            // Mute sounds steps out second, Light/Dark first: both are on the tray menu too.
            _soundBtn.Left = next - _soundBtn.Width - 10;
            _soundBtn.Top = top;
            // The free version has no chat and no calls, so nothing to mute. Flags, never Visible read
            // back: Visible reads false for every control until the window is on screen, and the first
            // layout runs before that — it hid Light/Dark for good on a window never resized after.
            bool soundShown = !_free && _soundBtn.Left >= header.TextLeft + HeaderWordsRoom;
            _soundBtn.Visible = soundShown;
            if (_traySoundItem != null) _traySoundItem.Visible = !_free;
            if (soundShown) next = _soundBtn.Left;
            _modeBtn.Left = next - _modeBtn.Width - 10;
            _modeBtn.Top = top;
            // A full header on a laptop screen (2026-10-05, measured by dashtabs once Knowledge base
            // made it seven buttons): Light/Dark is the one used least, so it steps out first — it
            // is on the tray menu too, so it is never lost — and the header's own words stop short of
            // the first button in "…" rather than being drawn underneath it.
            bool modeShown = (_free || soundShown) && _modeBtn.Left >= header.TextLeft + HeaderWordsRoom;
            _modeBtn.Visible = modeShown;
            header.TextLimit = (modeShown ? _modeBtn.Left : next) - 14;
        }
        header.Resize += (_, _) => LayoutHeader();
        _layoutHeader = LayoutHeader;

        var canvas = new CanvasPanel(_t) { Dock = DockStyle.Fill, Padding = new Padding(14) };
        Controls.Add(canvas);
        canvas.BringToFront();

        // ---- right: chat ----
        var chatCard = new Card(_t, 14) { Dock = DockStyle.Right, Width = 380 };
        _chatCard = chatCard;
        _chatLog = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = _t.Surface, Font = _t.Body, ForeColor = _t.Ink };
        var chatBottom = new Panel { Dock = DockStyle.Bottom, Height = 92, BackColor = Color.Transparent };
        // OwnerDrawFixed because a DropDownList combo ignores BackColor once visual styles are on
        // — it paints itself with the system theme, which is white however dark the window is.
        // ThemedCombo draws its own rows now, border and arrow included — this one used to do its
        // rows by hand at a fixed 20 px, and still wore the white outline every flat dropdown had.
        _chatTarget = new ThemedCombo(_t) { Dock = DockStyle.Top, Font = _t.Body };
        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
        _chatInput = new InputBox(_t) { Dock = DockStyle.Fill };
        // Pinned again after construction: this one field kept coming back white on a dark ground
        // where the identical InputBox on the login form did not.
        _chatInput.Box.BackColor = _t.Surface; _chatInput.Box.ForeColor = _t.Ink;
        _chatInput.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendChat(); } };
        var send = new RoundButton(_t, BtnStyle.Accent) { Text = "Send", Dock = DockStyle.Right, Width = 76, Height = 40 };
        send.Click += (_, _) => SendChat();
        inputRow.Controls.Add(_chatInput);
        inputRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        inputRow.Controls.Add(send);
        chatBottom.Controls.Add(inputRow);
        chatBottom.Controls.Add(_chatTarget);
        chatCard.Controls.Add(_chatLog);
        chatCard.Controls.Add(chatBottom);
        chatCard.Controls.Add(new SectionTitle(_t, "Team & client chat", _t.Pink) { Dock = DockStyle.Top });

        // ---- left column ----
        var left = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(0, 0, 14, 0) };

        _callsCard = new Card(_t, 14) { Dock = DockStyle.Top, Height = 250 };
        _calls = new ListView().Themed(_t);
        _calls.Dock = DockStyle.Fill;
        _calls.Columns.Add("From", 150); _calls.Columns.Add("Machine", 150); _calls.Columns.Add("Issue", 240); _calls.Columns.Add("Waiting", 90);
        _calls.DoubleClick += (_, _) => AcceptSelected();
        var acceptFont = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        var acceptBtn = new RoundButton(_t, BtnStyle.Success) { Text = "Accept Call", Dock = DockStyle.Bottom, Height = ButtonHeight(acceptFont, 18), Font = acceptFont };
        acceptBtn.Click += (_, _) => AcceptSelected();
        // What the card says instead of a table when nothing is ringing.
        _callsQuiet = QuietLine("No calls waiting.");
        var callsSpacer = new Panel { Dock = DockStyle.Bottom, Height = 8, BackColor = Color.Transparent };
        _callsCard.Controls.Add(_callsQuiet);
        _callsCard.Controls.Add(_calls);
        _callsCard.Controls.Add(callsSpacer);
        _callsCard.Controls.Add(acceptBtn);
        var callsTitle = new SectionTitle(_t, "Incoming calls", _t.Green) { Dock = DockStyle.Top };
        _callsCard.Controls.Add(callsTitle);

        var devicesCard = new Card(_t, 14) { Dock = DockStyle.Fill };
        _devices = new ListView().Themed(_t);
        _devices.Dock = DockStyle.Fill;
        // No vertical rules between columns. They read as things you can grab, so the eye goes to
        // them when you are trying to widen a column or open a customer — and they are not what
        // does either. The list is quieter without them and nothing is lost: the columns are still
        // obvious from their headings.
        _devices.GridLines = false;
        // Machines gather under the customer who owns them, and a customer's block collapses.
        // ListView's own groups, not a hand-built tree: they collapse natively, they keep the
        // keyboard behaviour a technician already knows, and the alternative was several hundred
        // lines of owner-drawn tree that would have had to earn all of that back.
        _devices.ShowGroups = true;
        // Only the rows that carry one set a tooltip, so this costs nothing on an ordinary list.
        _devices.ShowItemToolTips = true;
        // The dot column carries no text — it is painted in DeviceDot below. It is first because
        // "can I reach this machine" is the question being asked while the eye is still on the
        // left edge.
        _devices.Columns.Add("", DotColumnWidth);
        // Each column as wide as a long value of its own, measured in the list's font (2026-10-14). They were pixel
        // numbers, which do not grow with Windows' display scaling: at 150% "HV-FRONTDESK" read "HV-FRONT…" and
        // "16 GB" read "16…". In a window too narrow for all of them they share what there is (FitDeviceColumns):
        // past the list's edge the list scrolls sideways and the customer's fold chevron goes out of sight.
        int Col(string header, string sample) => Math.Max(TextRenderer.MeasureText(header, _t.Body).Width, TextRenderer.MeasureText(sample, _t.Body).Width) + 22;
        _deviceColumnWants = new[]
        {
            ("Who", Col("Who", "Dr Lindiwe Mokoena")), ("Machine", Col("Machine", "DESKTOP-ABC1234")),
            ("Operating system", Col("Operating system", "Windows Server 2022")), ("Processor", Col("Processor", "Intel Core i7-1255U")),
            ("RAM", Col("RAM", "128 GB")), ("Disk", Col("Disk", "1234/1863 GB (66%)")),
            // Which build the machine runs (2026-10-16, auto-update stage A) — last, as the column looked at least.
            ("Version", Col("Version", "2026.12.31.2359")),
        };
        foreach (var (title, width) in _deviceColumnWants) _devices.Columns.Add(title, width);
        _devices.Resize += (_, _) => FitDeviceColumns();
        // Status and Access are gone. Status is the dot, and Access is which tab you are looking
        // at — a column that repeats the tab heading on every single row is a column of noise.
        _devices.DrawSubItem += DeviceDot;
        // Remember what the technician folded away, keyed on the customer's name rather than the
        // group's heading — the heading carries a machine count and changes under them.
        _devices.GroupCollapsedStateChanged += (_, e) =>
        {
            var g = e.GroupIndex >= 0 && e.GroupIndex < _devices.Groups.Count ? _devices.Groups[e.GroupIndex] : null;
            if (g?.Tag is not string name) return;
            // The event args carry only which group moved, so the new state is read off the group
            // itself — it has already been applied by the time this fires.
            if (g.CollapsedState == ListViewGroupCollapsedState.Collapsed) _collapsed.Add(name);
            else _collapsed.Remove(name);
        };
        _devices.DoubleClick += (_, _) => ConnectSelectedDevice();
        // Right-click a machine to say whose it is. A menu rather than a fourth button under the
        // list: the list already carries Connect, and the section title says the menu is there —
        // a right-click nobody is told about is a feature nobody finds.
        var devMenu = new ContextMenuStrip { Font = _t.Body }.Themed(_t);
        var linkItem = new ToolStripMenuItem("Link to a customer…");
        linkItem.Click += (_, _) => LinkSelectedDevice();
        // Unlinking said plainly (Clint, 2026-10-09: "there is no unlinking option"). It always was in Link's
        // window, as its first row, where nobody looked for it.
        var unlinkItem = new ToolStripMenuItem("Unlink from its customer…") { Visible = false };
        unlinkItem.Click += (_, _) => UnlinkSelectedDevice();
        var connectItem = new ToolStripMenuItem("Connect");
        connectItem.Click += (_, _) => ConnectSelectedDevice();
        // Chat (Clint, 2026-10-05): straight from the machine rather than hunting for it in the chat
        // card's dropdown. It does not open anything new — it points the SAME chat card at this
        // machine and puts the cursor in the message box, so there is still one chat, not two.
        // Wake-on-LAN (roadmap 1.8, 2026-10-06): only for a machine that is off. The server asks another
        // of the same customer's machines on that network to send the wake-up, and says which.
        var wakeItem = new ToolStripMenuItem("Wake up");
        wakeItem.Click += (_, _) => { var d = SelectedDevice(); if (d != null) _link.WakeMachine(d.DeviceId); };
        var chatItem = new ToolStripMenuItem("Chat");
        chatItem.Click += (_, _) => ChatWithSelectedDevice();
        // Processes and Performance (Clint, 2026-10-05): a look at the machine BEFORE deciding to
        // connect. Straight through on any online machine — no prompt, no password — and look only.
        // Both open the same window, on the page chosen.
        var processesItem = new ToolStripMenuItem("Processes");
        processesItem.Click += (_, _) => WatchSelectedDevice(MachineWatchForm.Page.Processes);
        var performanceItem = new ToolStripMenuItem("Performance");
        performanceItem.Click += (_, _) => WatchSelectedDevice(MachineWatchForm.Page.Performance);
        // A reinstalled machine waits for the office (2026-10-07, HANDOVER §111): first in the menu while it waits,
        // because nothing unattended works on it until somebody answers. A supervisor's or the owner's; anybody
        // else sees whose it is, greyed.
        var confirmItem = new ToolStripMenuItem("Confirm it is the same machine…");
        confirmItem.Click += (_, _) => ConfirmSelectedReinstall();
        var confirmSep = new ToolStripSeparator();
        devMenu.Items.Add(confirmItem);
        devMenu.Items.Add(confirmSep);
        devMenu.Items.Add(connectItem);
        devMenu.Items.Add(wakeItem);
        devMenu.Items.Add(chatItem);
        devMenu.Items.Add(processesItem);
        devMenu.Items.Add(performanceItem);
        // Remembered unattended passwords (2026-10-06): one's own to forget; a supervisor or the owner
        // sees who has it saved — names, never the password — and may clear any of them. The list is
        // asked for as the menu opens and fills in when the server answers (FillSavedWho).
        var forgetItem = new ToolStripMenuItem("Forget saved password");
        forgetItem.Click += (_, _) => { var d = SelectedDevice(); if (d != null) _link.ForgetSavedPassword(d.DeviceId); };
        _savedWhoItem = new ToolStripMenuItem("Saved passwords");
        _savedWhoItem.DropDown.Themed(_t);   // filled later, so the menu's own Opening never sees it with items
        devMenu.Items.Add(forgetItem);
        devMenu.Items.Add(_savedWhoItem);
        // The free version's Remove (2026-10-06): frees the computer's place, twice a calendar month, by the hub's rule.
        var removeItem = new ToolStripMenuItem("Remove this computer…") { Visible = false };
        removeItem.Click += (_, _) => RemoveSelectedDevice();
        devMenu.Items.Add(removeItem);
        devMenu.Items.Add(new ToolStripSeparator());
        devMenu.Items.Add(linkItem);
        devMenu.Items.Add(unlinkItem);

        // The monthly health check, beside Link because Clint asked for it there (2026-09-28). The
        // customer's tick in the portal is the rule; this is the exception for one machine. The top
        // line is not a button: it says where the machine stands, including what the MACHINE last
        // reported, so a choice of "on" is never mistaken for a machine that has it (§41).
        var hcItem = new ToolStripMenuItem("Monthly health check");
        var hcNow = new ToolStripMenuItem("") { Enabled = false };
        var hcAsClient = new ToolStripMenuItem("");
        var hcOn = new ToolStripMenuItem("On for this machine");
        var hcOff = new ToolStripMenuItem("Off for this machine");
        hcItem.DropDownItems.AddRange(new ToolStripItem[] { hcNow, new ToolStripSeparator(), hcAsClient, hcOn, hcOff });
        void SetHealth(int mode)
        {
            var d = SelectedDevice();
            // The same choice again is a retry when the machine has not managed it (bug 9).
            if (d == null || !HealthCheckModes.WorthSending(d.HealthCheckMode, mode, d.HealthCheckWanted, d.HealthCheckInstalled)) return;
            _link.SetDeviceHealthCheck(d.DeviceId, mode);
        }
        hcAsClient.Click += (_, _) => SetHealth(HealthCheckModes.AsClient);
        hcOn.Click += (_, _) => SetHealth(HealthCheckModes.On);
        hcOff.Click += (_, _) => SetHealth(HealthCheckModes.Off);
        devMenu.Items.Add(hcItem);
        // Outside help (3.9): may the person at this machine give somebody outside the company a code?
        // The company's switch is on the portal's Outside help page; this is the exception for one
        // machine. A supervisor's or the owner's to change — the server refuses anybody else, in words.
        var ohItem = new ToolStripMenuItem("Outside help");
        var ohNow = new ToolStripMenuItem("") { Enabled = false };
        var ohAsCompany = new ToolStripMenuItem("");
        var ohOn = new ToolStripMenuItem("On for this machine");
        var ohOff = new ToolStripMenuItem("Off for this machine");
        ohItem.DropDownItems.AddRange(new ToolStripItem[] { ohNow, new ToolStripSeparator(), ohAsCompany, ohOn, ohOff });
        void SetOutsiders(int mode)
        {
            var d = SelectedDevice();
            if (d == null || OutsiderAccess.Normalise(d.OutsiderMode) == mode) return;
            _link.SetDeviceOutsiderMode(d.DeviceId, mode);
        }
        ohAsCompany.Click += (_, _) => SetOutsiders(OutsiderAccess.AsCompany);
        ohOn.Click += (_, _) => SetOutsiders(OutsiderAccess.On);
        ohOff.Click += (_, _) => SetOutsiders(OutsiderAccess.Off);
        devMenu.Items.Add(ohItem);
        // Both items act on the SELECTED row, so a right-click selects what is under the pointer
        // first. Without this, right-clicking one machine and choosing Link would quietly relabel
        // whichever machine happened to be selected before — the worst kind of wrong.
        _devices.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _devices.HitTest(e.Location).Item;
            if (hit != null) hit.Selected = true;
        };
        devMenu.Opening += (_, e) =>
        {
            var d = SelectedDevice();
            if (d == null) { e.Cancel = true; return; }
            connectItem.Enabled = d.Online;
            connectItem.Text = d.Online ? "Connect" : "Connect  (offline)";
            wakeItem.Visible = !d.Online;
            wakeItem.Enabled = d.CanWake;
            wakeItem.Text = d.CanWake ? "Wake up" : "Wake up  (its network card is not known yet)";
            // The chat dropdown only holds machines that are online, so an offline one cannot be
            // chatted to — said the same way Connect says it.
            chatItem.Enabled = d.Online;
            chatItem.Text = d.Online ? "Chat" : "Chat  (offline)";
            // An online machine on an older client cannot send the figures: said, never a blank window.
            string watchWhy = !d.Online ? "  (offline)" : !d.CanWatch ? "  (update the support client)" : "";
            processesItem.Enabled = performanceItem.Enabled = watchWhy.Length == 0;
            processesItem.Text = "Processes" + watchWhy;
            performanceItem.Text = "Performance" + watchWhy;
            forgetItem.Visible = _savedPw.Contains(d.DeviceId);
            _savedWhoItem.Visible = _mayManageSaved && d.UnattendedPassword;
            if (_savedWhoItem.Visible)
            {
                _savedWhoItem.Tag = d.DeviceId;
                _savedWhoItem.DropDownItems.Clear();
                _savedWhoItem.DropDownItems.Add(new ToolStripMenuItem("Asking the server…") { Enabled = false });
                _link.AskWhoSavedPassword(d.DeviceId);
            }
            hcNow.Text = HealthCheckModes.Describe(d.HealthCheckAvailable, d.HealthCheckWanted,
                d.HealthCheckInstalled, d.HealthCheckNote, d.AgentOnline);
            hcAsClient.Text = HealthCheckModes.AsClientLabel(d.ClientId, d.ClientName, d.ClientHealthCheck);
            hcAsClient.Checked = HealthCheckModes.Normalise(d.HealthCheckMode) == HealthCheckModes.AsClient;
            hcOn.Checked = d.HealthCheckMode == HealthCheckModes.On;
            hcOff.Checked = d.HealthCheckMode == HealthCheckModes.Off;
            ohNow.Text = OutsiderAccess.Describe(d.OutsiderCompanyOn, d.OutsiderMode);
            ohAsCompany.Text = d.OutsiderCompanyOn ? "As the company  (on)" : "As the company  (off)";
            ohAsCompany.Checked = OutsiderAccess.Normalise(d.OutsiderMode) == OutsiderAccess.AsCompany;
            ohOn.Checked = d.OutsiderMode == OutsiderAccess.On;
            ohOff.Checked = d.OutsiderMode == OutsiderAccess.Off;
            // The free version: Connect, Forget saved password and Remove — nothing else it has (2026-10-06).
            // The free version's own Remove, or a supervisor's or the owner's on the full version (2026-10-06).
            removeItem.Visible = _free || _mayRemove;
            foreach (ToolStripItem it in devMenu.Items)
            {
                if (it == connectItem || it == forgetItem || it == removeItem || it == wakeItem || it == _savedWhoItem
                    || it == confirmItem || it == confirmSep) continue;
                it.Visible = !_free;
            }
            confirmItem.Visible = confirmSep.Visible = d.ReinstallUnconfirmed;
            confirmItem.Enabled = _mayConfirmReinstall;
            confirmItem.Text = _mayConfirmReinstall
                ? "Confirm it is the same machine…"
                : "Reinstalled — a supervisor or the owner confirms it";
            if (_free) { wakeItem.Visible = false; _savedWhoItem.Visible = false; }
            bool linked = d.ClientId > 0;
            linkItem.Text = linked ? "Change the customer…" : "Link to a customer…";
            unlinkItem.Visible = !_free && linked;
            unlinkItem.Text = linked ? $"Unlink from {(d.ClientName.Length > 0 ? d.ClientName.Replace("&", "&&") : "its customer")}…" : "Unlink from its customer…";
        };
        _devices.ContextMenuStrip = devMenu;
        var connectBtn = new RoundButton(_t, BtnStyle.Accent) { Text = "Connect", Dock = DockStyle.Fill, Height = ButtonHeight(_t.ButtonFont, 18) };
        connectBtn.Click += (_, _) => ConnectSelectedDevice();
        // Outside help (3.9): a computer that is NOT this company's, reached with the code its owner
        // gave. Beside Connect because it IS connecting — just to a machine that is not on the list.
        var codeBtn = new RoundButton(_t, BtnStyle.Neutral)
        {
            Text = "Connect with a code…", Dock = DockStyle.Right, Height = connectBtn.Height,
            Width = TextRenderer.MeasureText("Connect with a code…", _t.ButtonFont).Width + 36
        };
        codeBtn.Click += (_, _) => ConnectWithCode();
        _codeBtn = codeBtn;
        var connectRow = new Panel { Dock = DockStyle.Bottom, Height = connectBtn.Height, BackColor = Color.Transparent };
        connectRow.Controls.Add(connectBtn);
        connectRow.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Color.Transparent });
        connectRow.Controls.Add(codeBtn);
        devicesCard.Controls.Add(_devices);
        devicesCard.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8, BackColor = Color.Transparent });
        devicesCard.Controls.Add(connectRow);
        devicesCard.Controls.Add(BuildDeviceTabs());
        _devicesTitle = new SectionTitle(_t, "Machines  (double-click to connect, right-click for more)", _t.Blue) { Dock = DockStyle.Top };
        devicesCard.Controls.Add(_devicesTitle);

        var sessionsCard = new Card(_t, 14) { Dock = DockStyle.Bottom, Height = 180 };
        _sessions = new ListView().Themed(_t);
        _sessions.Dock = DockStyle.Fill;
        _sessions.Columns.Add("Machine", 190); _sessions.Columns.Add("Technicians", 250); _sessions.Columns.Add("Host", 80);
        _sessions.DoubleClick += (_, _) => JoinSelectedSession();
        _sessionsQuiet = QuietLine("No sessions running.");
        sessionsCard.Controls.Add(_sessionsQuiet);
        sessionsCard.Controls.Add(_sessions);
        var sessionsTitle = new SectionTitle(_t, "Active sessions  (double-click to join)", _t.Purple) { Dock = DockStyle.Top };
        sessionsCard.Controls.Add(sessionsTitle);

        left.Controls.Add(devicesCard);
        left.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent });
        left.Controls.Add(sessionsCard);
        left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Color.Transparent });
        left.Controls.Add(_callsCard);

        // The calls and sessions cards are fixed heights and the devices card takes what is left,
        // so on a short screen the devices card absorbs the entire shortfall and collapses. It was
        // measured at ONE PIXEL high on a 1360x768 secondary monitor, holding all eleven devices
        // and showing none of them (tools/dashprobe).
        //
        // Maximising is what triggers it, which is the opposite of what anyone expects. A restored
        // window can be taller than the screen and hang off the bottom; maximising clamps it to the
        // working area, so on that monitor maximising LOSES about 84 pixels of height rather than
        // gaining any.
        //
        // So share the space rather than assuming it: below a certain height the two fixed cards
        // give way proportionally, and the devices list — the one thing the console exists to show
        // — keeps a floor it can actually display rows in.
        //
        // AND: a card with nothing in it asks for almost nothing. Two empty lists were holding
        // 430 px of a 900 px window while the machine list — the thing the console is for — got
        // four rows. An empty "Incoming calls" does not need a table with four headings and an
        // Accept button; it needs one line saying nothing is waiting. The moment a call arrives
        // the card opens to its full size again, which is exactly when the space is worth
        // spending. This is 3.5's bargain applied to the home screen: the console gets out of
        // the way until it has something to say.
        const int Gap = 12, MinDevices = 240, FloorCalls = 96, FloorSessions = 84;
        int FullCalls = 250, FullSessions = 180;
        // Added up from the REAL parts rather than from a formula about them: the card's own
        // padding, the heading control's measured height, and one line of the quiet label
        // including its own padding. The first attempt at this computed the heading's height a
        // second way, the two ways disagreed by eight pixels, and "No calls waiting." was cut in
        // half by the bottom of the card — the same measure-one-way-draw-another mistake §30 is
        // about, made while fixing an instance of it.
        int QuietHeight(Control title, Control quiet) =>
            _callsCard.Padding.Vertical + title.Height
            + quiet.Padding.Vertical + TextRenderer.MeasureText("Ag", _t.Body).Height + 6;
        int QuietCalls = QuietHeight(callsTitle, _callsQuiet);
        int QuietSessions = QuietHeight(sessionsTitle, _sessionsQuiet);

        void ShareTheHeight()
        {
            int available = left.ClientSize.Height - Gap * 2;
            if (available <= 0) return;
            bool anyCalls = _callMap.Values.Any(c => c.Status == CallStatus.Ringing);
            bool anySessions = _sessionMap.Count > 0;
            int WantCalls = anyCalls ? FullCalls : QuietCalls;
            int WantSessions = anySessions ? FullSessions : QuietSessions;

            // The contents of a quiet card are hidden rather than squashed: a list two pixels
            // high with a scrollbar is worse than no list.
            _calls.Visible = anyCalls; acceptBtn.Visible = anyCalls; callsSpacer.Visible = anyCalls;
            _callsQuiet.Visible = !anyCalls;
            _sessions.Visible = anySessions;
            _sessionsQuiet.Visible = !anySessions;

            int calls = WantCalls, sessions = WantSessions;
            int spare = available - MinDevices;
            if (spare < WantCalls + WantSessions)
            {
                double scale = Math.Max(0, (double)spare / (WantCalls + WantSessions));
                calls = Math.Max(FloorCalls, (int)(WantCalls * scale));
                sessions = Math.Max(FloorSessions, (int)(WantSessions * scale));
            }
            // Never let the two of them exceed the panel: a negative Fill height is what produced
            // the one-pixel list in the first place.
            if (calls + sessions > available - 40)
            {
                int room = Math.Max(0, available - 40);
                double share = calls + sessions == 0 ? 0 : (double)room / (calls + sessions);
                calls = (int)(calls * share);
                sessions = (int)(sessions * share);
            }
            // The free version has no calls: nobody at its computers can ask (2026-10-06).
            _callsCard.Visible = !_free;
            if (_free) calls = 0;
            if (_callsCard.Height != calls) _callsCard.Height = calls;
            if (sessionsCard.Height != sessions) sessionsCard.Height = sessions;
        }
        left.Resize += (_, _) => ShareTheHeight();
        _relayout = ShareTheHeight;
        ShareTheHeight();

        canvas.Controls.Add(left);
        canvas.Controls.Add(chatCard);

        _techsLabel = new Label { Dock = DockStyle.Bottom, Height = 26, ForeColor = _t.Muted, BackColor = Color.Transparent, Font = _t.Small, Text = "Technicians online: —", Padding = new Padding(4, 4, 0, 0) };
        canvas.Controls.Add(_techsLabel);
    }

    private void Wire()
    {
        _link.OnConnectedChanged += c => Inv(() => _statusPill.Set(c ? "● connected" : "○ reconnecting…", c ? _t.OkBg : _t.WarnBg, c ? _t.OkText : _t.WarnText));
        _link.OnState += s => Inv(() => LoadState(s));
        _link.OnRing += call => Inv(() => { UpsertCall(call); if (call.Status == CallStatus.Ringing) NotifyIncoming(call); });
        _link.OnCallUpdate += call => Inv(() => UpsertCall(call));
        _link.OnDeviceUpdate += d => Inv(() => { _deviceMap[d.DeviceId] = d; RefreshDevices(); WatchedDeviceChanged(d); });
        // Removed (the free version), or the old id of a machine that was reinstalled: the row goes.
        _link.OnDeviceGone += g => Inv(() => { if (_deviceMap.Remove(g.DeviceId)) RefreshDevices(); });
        _link.OnWatchSample += m => Inv(() => { if (_watchWindows.TryGetValue(m.DeviceId, out var w) && !w.IsDisposed) w.Apply(m.Sample); });
        _link.OnWatchEnded += m => Inv(() => { if (_watchWindows.TryGetValue(m.DeviceId, out var w) && !w.IsDisposed) w.EndWith(m.Reason); });
        _link.OnSessionReady += s => Inv(() => OnSessionReady(s));
        _link.OnPresence += list => Inv(() => _techsLabel.Text = $"Technicians online: {string.Join(", ", list)}");
        _link.OnChat += ch => Inv(() => AppendChat(ch));
        _link.OnPortalLink += pl => Inv(() => OnPortalLink(pl));
        _link.OnMentionCount += n => Inv(() => ShowMentionCount(n));
        _link.OnBillingQueue += m => Inv(() => OnBillingQueue(m));
        _link.OnActivityCount += m => Inv(() => OnActivityCount(m));
        _link.OnOutsiderVisit += v => Inv(() => ShowVisitor(v));
        // A code made in a session (§155): to that session's window, by session id.
        _link.OnOutsiderInvite += m => Inv(() =>
        {
            foreach (var w in _sessionWindows.Where(w => !w.IsDisposed && w.SessionId == m.SessionId).ToList()) w.ShowOutsideHelp(m);
        });
        _link.OnOutsiderJoinAsk += ask => Inv(() =>
        {
            var w = _sessionWindows.FirstOrDefault(w => !w.IsDisposed && w.SessionId == ask.SessionId);
            if (w != null) { w.AskToLetIn(ask); return; }
            // Their window has gone but they are still in the session elsewhere — asked from here all the same.
            using var dlg = new OutsiderAllowDialog(_t, ask);
            var answer = dlg.ShowDialog(this);
            if (!dlg.TimedOut)
                _link.Send(new OutsiderJoinAnswerMsg { SessionId = ask.SessionId, Allow = answer == DialogResult.OK, Files = answer == DialogResult.OK && dlg.Files });
        });
        // The knowledge base's answer for a machine just connected to (3.8.5), to its session window.
        _link.OnKnowledgeCount += kc => Inv(() =>
        {
            foreach (var w in _sessionWindows.Where(w => !w.IsDisposed && w.DeviceId == kc.DeviceId).ToList()) w.ShowKnowledge(kc);
        });
        _staffChat = new StaffChatForm(_link, _t);
        _staffChat.UnreadChanged += n =>
        {
            _staffChatBtn.Text = n > 0 ? $"Office chat ({n})" : "Office chat";
            _staffChatBtn.Style = n > 0 ? BtnStyle.Danger : BtnStyle.Neutral;
            _staffChatBtn.Invalidate();
        };
        // Website chat (3.8.3). The button shows while the company has it on (or somebody is still
        // waiting), counts who is waiting, and goes red while anybody is; a new visitor while the
        // console is in the tray is a balloon, the same as a call.
        _webChat = new WebChatForm(_link, _t);
        _webChat.WaitingChanged += (n, enabled) =>
        {
            var show = enabled || n > 0;
            _webChatBtn.Text = n > 0 ? $"Website chat ({n})" : "Website chat";
            _webChatBtn.Style = n > 0 ? BtnStyle.Danger : BtnStyle.Neutral;
            _webChatBtn.Invalidate();
            if (show != _webChatShown) { _webChatShown = show; _webChatBtn.Visible = show; _layoutHeader(); }
            if (n > _webWaiting) TrayAlert("Website chat", "Somebody on your website wants to chat.", ToolTipIcon.Info);
            _webWaiting = n;
            UpdateRinging();
        };
        _webChat.HandedToMe += c => TrayAlert("Website chat", $"{c.HandedOverBy} handed you the chat with {c.Name}.", ToolTipIcon.Info);
        _link.OnError += e => Inv(() => MessageBox.Show(this, e, "Server", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        // A recurring task set up from New ticket (2026-10-07): when its first ticket opens.
        _link.OnRecurringSet += r => Inv(() => MessageBox.Show(this, r.Message, "Recurring task", MessageBoxButtons.OK, MessageBoxIcon.Information));
        _link.OnWakeResult += r => Inv(() => MessageBox.Show(this, r.Message, "Wake up",
            MessageBoxButtons.OK, r.Sent ? MessageBoxIcon.Information : MessageBoxIcon.Warning));
        _link.OnPasswordRefused += (id, why, none) => Inv(() => OnPasswordRefused(id, why, none));
        _link.OnReasonNeeded += id => Inv(() => OnReasonNeeded(id));
        _link.OnSavedPasswords += m => Inv(() =>
        {
            _savedPw = new HashSet<string>(m.DeviceIds, StringComparer.OrdinalIgnoreCase);
            _mayManageSaved = m.MayManage;
            RefreshDevices();
        });
        _link.OnSavedPasswordWho += m => Inv(() => FillSavedWho(m));
        _link.OnSignedOut += why => Inv(() => SignedOutByServer(why));
    }

    /// <summary>
    /// The server signed this person out (2026-10-01): removed, their password, roles or two-factor
    /// changed, or their sign-in ended. Said once, then the same way out as Sign out — back to the
    /// sign-in window in a fresh process — without asking, because there is nothing left to keep:
    /// the server has already closed every session this console had open.
    /// </summary>
    private void SignedOutByServer(string why)
    {
        if (SignedOut) return;
        if (!Visible) ShowWindow();
        MessageBox.Show(this, why, "Signed out", MessageBoxButtons.OK, MessageBoxIcon.Information);
        SignedOut = true;
        _exiting = true;
        _tray.Visible = false;
        Trace.Write("dashboard: signed out by the server — " + why);
        Close();
    }

    private void Inv(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

    /// <summary>
    /// The free version (2026-10-06, docs/NEXT-SESSION-FREE.md Stage F): one person, two unattended computers,
    /// no tickets. The console draws only that — no Tickets, Office chat, Website chat, Knowledge base or New
    /// ticket, no customer chat, no calls, no "Connect with a code", no Attended tab. The hub refuses all of
    /// them whatever is drawn (TenantHub.Free.cs); this is so nobody clicks a button that does nothing.
    /// Told with every state, so a company that upgrades gets the full console at its next sign-in.
    /// </summary>
    private void ApplyFree(bool free)
    {
        _free = free;
        _chatCard.Visible = !free;
        _codeBtn.Visible = !free;
        // One kind of computer, so no tabs at all — a lone tab is a switch with nothing to switch to.
        _tabAttended.Parent!.Visible = !free;
        // One list of everything (§163): the Basic version's attended computers belong in it as much as the unattended ones.
        if (free) _tab = DeviceTab.All;
        else if (_tab == DeviceTab.All) _tab = DeviceTab.Unattended;
        _devicesTitle.Text = "     " + (free ? "Your computers  (double-click to connect, right-click for more)" : "Machines  (double-click to connect, right-click for more)");
        // One person: nobody else to be online.
        _techsLabel.Visible = !free;
        _layoutHeader();
        _relayout();
    }

    private void LoadState(StateMsg s)
    {
        _callMap.Clear(); foreach (var c in s.Calls) _callMap[c.CallId] = c;
        _deviceMap.Clear(); foreach (var d in s.Devices) _deviceMap[d.DeviceId] = d;
        _sessionMap.Clear(); foreach (var ss in s.Sessions) _sessionMap[ss.SessionId] = ss;
        _basic = s.Basic;
        ApplyFree(s.Free);
        // The computers a Basic company pays for (2026-10-25); an older server says nothing, which is the three in its price.
        _basicMachines = s.MaxMachines > 0 ? s.MaxMachines : FreePlan.MaxMachines;
        _mayRemove = s.MayRemoveDevices;
        _mayConfirmReinstall = s.MayConfirmReinstall;
        // A newer console for this company (auto-update): the header says so until the console restarts into it.
        var ready = ConsoleUpdate.Offers(s.ConsoleUpdate);
        if (ready != _updateReady) { _updateReady = ready; _layoutHeader(); }
        _techsLabel.Text = $"Technicians online: {string.Join(", ", s.OnlineTechnicians)}";
        RefreshCalls(); RefreshDevices(); RefreshSessions(); RefreshChatTargets();
        _chatLog.Clear();
        foreach (var c in s.RecentChat) AppendChat(c, live: false);
        // The console has (re)connected, and the server forgot this console's Processes and
        // Performance windows when the old connection went: ask again for every one still open.
        foreach (var w in _watchWindows.Values.Where(w => !w.IsDisposed)) w.Start();
    }

    // ---- Processes and Performance (2026-10-05) ----
    /// <summary>The open Processes/Performance windows, one per machine.</summary>
    private readonly Dictionary<string, MachineWatchForm> _watchWindows = new();

    /// <summary>Opens the machine's Processes/Performance window on the page chosen — or, when it is
    /// already open, brings it forward on that page rather than opening a second.</summary>
    private void WatchSelectedDevice(MachineWatchForm.Page page)
    {
        var d = SelectedDevice();
        if (d == null) return;
        if (_watchWindows.TryGetValue(d.DeviceId, out var open) && !open.IsDisposed)
        {
            open.ShowPage(page);
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }
        var w = new MachineWatchForm(_link, _t, d, page);
        w.FormClosed += (_, _) => { if (_watchWindows.TryGetValue(d.DeviceId, out var x) && x == w) _watchWindows.Remove(d.DeviceId); };
        _watchWindows[d.DeviceId] = w;
        w.Show();
    }

    /// <summary>A watched machine came back (or was updated) after its figures stopped: start again
    /// by itself, so a machine that rebooted fills the window again without anybody reopening it.</summary>
    private void WatchedDeviceChanged(DeviceInfo d)
    {
        if (!_watchWindows.TryGetValue(d.DeviceId, out var w) || w.IsDisposed) return;
        w.Rename(d.MachineName);
        if (w.EndedReason != null && d.Online && d.CanWatch) w.Start();
    }

    // ---- calls ----
    private void UpsertCall(CallInfo call)
    {
        if (call.Status is CallStatus.Ringing or CallStatus.Accepted) _callMap[call.CallId] = call;
        else _callMap.Remove(call.CallId);
        RefreshCalls();
    }

    private void RefreshCalls()
    {
        _calls.BeginUpdate(); _calls.Items.Clear();
        foreach (var c in _callMap.Values.Where(c => c.Status == CallStatus.Ringing).OrderBy(c => c.Created))
        {
            var wait = DateTimeOffset.UtcNow - c.Created;
            _calls.Items.Add(new ListViewItem(new[] { c.ContactName, c.MachineName, c.Issue, $"{(int)wait.TotalSeconds}s" }) { Tag = c });
        }
        _calls.EndUpdate();
        bool ringing = _callMap.Values.Any(c => c.Status == CallStatus.Ringing);
        Text = ringing ? $"● Incoming call — {_brand.ProductName}" : ConsoleTitle;
        if (!ringing) { _calls.BackColor = _t.Surface; _callsCard.Invalidate(); }
        // A call arriving opens this card to full size, and the last one clearing shrinks it back
        // and hands the room to the machine list.
        _relayout();
        UpdatePopup();
    }

    /// <summary>
    /// Keeps the centre-screen alert in sync: shows the longest-waiting call the technician has not
    /// dismissed, and hides itself as soon as nothing is ringing.
    /// </summary>
    private void UpdatePopup()
    {
        var waiting = _callMap.Values
            .Where(c => c.Status == CallStatus.Ringing && !_dismissedCalls.Contains(c.CallId))
            .OrderBy(c => c.Created).ToList();

        // forget dismissals for calls that are no longer ringing, so the set cannot grow forever
        _dismissedCalls.RemoveWhere(id => !_callMap.TryGetValue(id, out var c) || c.Status != CallStatus.Ringing);
        UpdateRinging();

        if (waiting.Count == 0) { _popup?.HideCall(); return; }

        if (_popup == null || _popup.IsDisposed)
        {
            _popup = new IncomingCallPopup(_t, _brand);
            _popup.Accepted += id => { try { BeginInvoke(() => { if (!CallOverSessionLimit(id)) _link.Accept(id); }); } catch { } };
            _popup.Dismissed += id => { _dismissedCalls.Add(id); try { BeginInvoke(UpdatePopup); } catch { } };
        }
        _popup.ShowCall(waiting[0], waiting.Count - 1, this);
    }

    /// <summary>
    /// Puts the console in the tray and makes the X button hide it rather than close it.
    ///
    /// A technician's console is not a document window: closing it stops calls ringing and stops
    /// chat arriving, and that is never what the X button was meant to say. The customer's tray
    /// app has worked this way since the start; this is the same bargain on the other side of the
    /// wire. It also means an accidental close costs nothing, which is the reason Clint asked.
    ///
    /// Exit is on the tray menu, deliberately and only there, so leaving really is a decision.
    /// </summary>
    private void BuildTray()
    {
        // The tenant's own logo, the same one the portal and the installer use — a technician
        // running two resellers' consoles needs to tell them apart in the tray at a glance.
        var brandIcon = BrandIcon.Load(_brand);
        if (brandIcon != null) Icon = brandIcon;
        _tray = new NotifyIcon
        {
            Text = $"{_brand.CompanyName} — Technician Console",
            Visible = true,
            Icon = brandIcon ?? SystemIcons.Application,
        };
        var menu = new ContextMenuStrip { Font = _t.Body }.Themed(_t);
        menu.Items.Add("Open console", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        // Its words come from ShowModeOffer, once the header (built after the tray) has its button.
        _trayModeItem = new ToolStripMenuItem(_modeBtn?.Text ?? "");
        _trayModeItem.Click += async (_, _) => await SwitchAppearance();
        menu.Items.Add(_trayModeItem);
        _traySoundItem = new ToolStripMenuItem(_soundBtn?.AccessibleName ?? "");
        _traySoundItem.Click += (_, _) => Sounds.Muted = !Sounds.Muted;
        menu.Items.Add(_traySoundItem);
        menu.Items.Add("Sign out", null, (_, _) => SignOut());
        menu.Items.Add("Exit", null, (_, _) => ExitForReal());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        FormClosing += (_, e) =>
        {
            // Only a person clicking X. Windows shutting down, the app exiting and our own Exit
            // must all be allowed through, or the console becomes impossible to close at all —
            // the same trap the session window's pop-out handler already fell into once.
            if (_exiting || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(3000, $"{_brand.CompanyName} Technician Console",
                "Still running in the tray. Calls and chat will still reach you. Right-click the icon to sign out or exit.",
                ToolTipIcon.Info);
            Trace.Write("dashboard: closed to tray");
        };
    }

    private void ShowWindow()
    {
        Show();
        // It opens maximised, so a window restored from the tray should come back the same way
        // rather than as a small box in the corner of the screen.
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        Activate();
        BringToFront();
    }

    /// <summary>Leaving for real. Nothing else may take this path.</summary>
    private void ExitForReal()
    {
        _exiting = true;
        _tray.Visible = false;
        Trace.Write("dashboard: exit from the tray menu");
        Close();
    }

    /// <summary>How many session windows are open — what signing out would end.</summary>
    public static int OpenSessionWindows() => Application.OpenForms.OfType<SessionForm>().Count();

    /// <summary>
    /// Signs out: the same way out as Exit, and then Program starts the console again at the
    /// sign-in window (username filled in, <see cref="LastSignIn"/>). A fresh process rather than a
    /// fresh window, so every session window, tool window, socket and hidden form of the person
    /// who left goes with them — nothing of theirs can be left open for the next person to find.
    ///
    /// Asks first only when it would end something: a session window open to a customer's machine.
    /// </summary>
    /// <summary>
    /// "Update ready" (auto-update, 2026-10-16): the same fresh start as Sign out — the check at start then installs the new
    /// console before the sign-in window. Asked first, with a warning when it would end a session.
    /// </summary>
    private void RestartToUpdate()
    {
        int open = OpenSessionWindows();
        var sure = MessageBox.Show(this,
            (open > 0 ? "You are connected to a customer's machine. Updating ends that session.\n\n" : "")
            + "The console restarts to update itself, then asks you to sign in again.\n\nUpdate now?",
            "Update the console", MessageBoxButtons.YesNo, MessageBoxIcon.Question, open > 0 ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1);
        if (sure != DialogResult.Yes) return;
        SignedOut = true;
        _exiting = true;
        _tray.Visible = false;
        Trace.Write($"dashboard: restarting to update ({open} session window(s) open)");
        Close();
    }

    private void SignOut()
    {
        int open = OpenSessionWindows();
        if (open > 0)
        {
            if (!Visible) ShowWindow();   // from the tray: show what is about to close
            var sure = MessageBox.Show(this,
                open == 1 ? "You are connected to a customer's machine. Signing out ends that session.\n\nSign out anyway?"
                          : $"You are connected to {open} customers' machines. Signing out ends those sessions.\n\nSign out anyway?",
                "Sign out", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (sure != DialogResult.Yes) return;
        }
        SignedOut = true;
        _exiting = true;
        _tray.Visible = false;
        Trace.Write($"dashboard: signed out ({open} session window(s) open)");
        Close();
    }

    /// <summary>
    /// A balloon for anything that arrives while the console is hidden. Without it, closing to
    /// the tray would quietly turn a ringing call into nothing at all — which is worse than the
    /// accidental close this whole change exists to prevent.
    /// </summary>
    private void TrayAlert(string title, string text, ToolTipIcon icon)
    {
        if (Visible && WindowState != FormWindowState.Minimized) return;
        try { _tray.ShowBalloonTip(6000, title, text, icon); } catch { }
    }

    private void BlinkCalls()
    {
        if (!_callMap.Values.Any(c => c.Status == CallStatus.Ringing)) return;
        _blinkOn = !_blinkOn;
        _calls.BackColor = _blinkOn ? _t.AlertFlash : _t.Surface;
        foreach (ListViewItem it in _calls.Items) if (it.Tag is CallInfo c) it.SubItems[3].Text = $"{(int)(DateTimeOffset.UtcNow - c.Created).TotalSeconds}s";
    }

    private void NotifyIncoming(CallInfo call)
    {
        // The sound is the ring, which UpdateRinging keeps going while the call waits.
        FlashWindow();
        // FlashWindow does nothing for a window that is not on the taskbar, so a console sitting
        // in the tray needs telling a different way. The centre-screen alert still appears; this
        // is for the case where it is behind something.
        TrayAlert("Incoming support request",
            (string.IsNullOrWhiteSpace(call.ContactName) ? "Someone" : call.ContactName)
            + (string.IsNullOrWhiteSpace(call.MachineName) ? "" : " on " + call.MachineName)
            + " is asking for help.", ToolTipIcon.Warning);
    }

    private void AcceptSelected()
    {
        if (_calls.SelectedItems.Count == 0) { if (_calls.Items.Count > 0) _calls.Items[0].Selected = true; else return; }
        if (_calls.SelectedItems.Count == 0) return;
        if (_calls.SelectedItems[0].Tag is CallInfo c && !CallOverSessionLimit(c.CallId)) _link.Accept(c.CallId);
    }

    // ---- two machines at once, at most (2026-10-15) ----
    // Clint: two sessions side by side — copy on one office machine, paste on the other — and no more than two. The
    // server refuses a third too (TenantHub.SessionLimit.cs); asking here first means the third machine's person is
    // never prompted and no ticket is opened. The number and the words are SessionLimit's, shared with the server.

    /// <summary>The machines this console is connected to, or waiting on somebody to accept, by device id.</summary>
    private Dictionary<string, string> MachinesOpen()
    {
        var open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in _sessionWindows) if (!w.IsDisposed) open[w.DeviceId] = w.MachineName;
        foreach (var id in _waits.Keys) if (_sessionMap.TryGetValue(id, out var s)) open[s.DeviceId] = s.MachineName;
        return open;
    }

    /// <summary>
    /// True, having said so, when one more session on <paramref name="deviceId"/> would be over the limit. A machine
    /// already open never is: going back to it adds nothing. Empty for a machine this console cannot name yet (a code).
    /// </summary>
    internal bool OverSessionLimit(string deviceId, string title = "Connect")
    {
        var open = MachinesOpen();
        if (deviceId.Length > 0) open.Remove(deviceId);
        var problem = SessionLimit.Refusal(open.Values.ToList());
        if (problem == null) return false;
        Trace.Write($"dashboard: refused a session on {(deviceId.Length > 0 ? deviceId : "a machine by code")} — already on {string.Join(", ", open.Values)}");
        if (!Visible) ShowWindow();
        MessageBox.Show(this, problem, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return true;
    }

    private bool CallOverSessionLimit(string callId) =>
        _callMap.TryGetValue(callId, out var call) && OverSessionLimit(call.DeviceId, "Accept call");

    // ---- devices ----

    /// <summary>
    /// The height of a button that has to hold one line of its own font, plus padding.
    ///
    /// §30 of the handover, in one method: three faults in a row came from pixel constants written
    /// against a 100% display, where one line of 14 pt is 38 px at Clint's 150% and not the ~25 a
    /// constant assumed. Two of the three were invisible on the developer's screen. Nothing in
    /// this form writes a control height as a number any more.
    /// </summary>
    private static int ButtonHeight(Font f, int padding) => TextRenderer.MeasureText("Ag", f).Height + padding;

    /// <summary>What this person has saved, or — if they never chose — what this window is painted in.</summary>
    private string SavedMode => _link.Appearance is "light" or "dark" ? _link.Appearance : _t.IsDark ? "dark" : "light";

    /// <summary>
    /// The button offers the one they have not saved. After a switch that is the palette this window
    /// is already in, which is right: clicking it again takes the switch back.
    /// </summary>
    private void ShowModeOffer()
    {
        var offer = SavedMode == "dark" ? "light" : "dark";
        _modeBtn.Text = offer == "dark" ? "Dark mode" : "Light mode";
        if (_trayModeItem != null) _trayModeItem.Text = _modeBtn.Text;
        var painted = _t.IsDark ? "dark" : "light";
        _modeTip.SetToolTip(_modeBtn, SavedMode == painted
            ? $"Switch your console and the portal to {offer}. It is your choice, not the company's, and it follows you to any machine."
            : $"{Cap(SavedMode)} mode is saved and starts the next time you open the console. Click to go back to {offer}.");
    }

    /// <summary>The button and the tray item offer the opposite of what is set now.</summary>
    private void ShowSoundOffer()
    {
        if (IsDisposed) return;
        // The bell shows what it is NOW — crossed out while muted — as a speaker icon does; the words
        // (tooltip, tray menu) say what a click does.
        _soundBtn.Text = Sounds.Muted ? "🔕" : "🔔";
        _soundBtn.AccessibleName = Sounds.Muted ? "Unmute sounds" : "Mute sounds";
        _soundBtn.Invalidate();
        if (_traySoundItem != null) _traySoundItem.Text = _soundBtn.AccessibleName;
        _modeTip.SetToolTip(_soundBtn, Sounds.Muted
            ? "Sounds are off on this PC. Calls and messages still show, but without the ring or the ding."
            : "Turns off the ring for a waiting call and the ding for a chat message, on this PC.");
    }

    /// <summary>
    /// The ring (2026-10-07): on while any call is waiting that this person has not dismissed, or a
    /// website visitor is waiting for anybody to answer; off the moment none are. Answered anywhere,
    /// cancelled or dismissed here, it stops.
    /// </summary>
    private void UpdateRinging() => Sounds.Ringing(
        _callMap.Values.Any(c => c.Status == CallStatus.Ringing && !_dismissedCalls.Contains(c.CallId)) || _webWaiting > 0);

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>
    /// Saves the other palette against this person. It takes effect the next time the console opens:
    /// every window takes its colours when it is built, and restarting for them would drop any
    /// session they have open. The portal follows at once, the next time it is opened from Tickets.
    /// </summary>
    private async Task SwitchAppearance()
    {
        var wanted = SavedMode == "dark" ? "light" : "dark";
        _modeBtn.Enabled = false;
        var error = await _link.SaveAppearanceAsync(wanted);
        _modeBtn.Enabled = true;
        if (error.Length > 0)
        {
            MessageBox.Show(this, $"Could not save that.\n\n{error}", "Appearance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ShowModeOffer();
        var painted = _t.IsDark ? "dark" : "light";
        if (SavedMode != painted)
            // Closing the window only hides it to the tray, so "next time you open it" would be a
            // promise the next double-click on the tray icon does not keep. Say what does.
            MessageBox.Show(this, $"{Cap(SavedMode)} mode will apply the next time the console starts.\n\n" +
                "To switch now: right-click the console's icon in the tray, choose Exit, and open it again.",
                "Appearance", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// How wide a device tab has to be to show what it says. Measured from the button's own font —
    /// the tabs carry counts, and since 2026-09-21 the Unattended one can carry a warning as well,
    /// so its text is not a length anybody can write down in advance.
    ///
    /// The 150 is a floor, not a size: two tabs that shrink and grow under the mouse as machines
    /// come and go would be its own annoyance.
    /// </summary>
    private int TabWidth(string text) => Math.Max(150, TextRenderer.MeasureText(text, _t.ButtonFont).Width + 28);

    /// <summary>
    /// The single muted line a card shows in place of an empty table. It is a sentence rather than
    /// a dash: "No calls waiting." tells somebody the console is working and there is nothing to
    /// do, which an empty grid does not.
    /// </summary>
    private Label QuietLine(string text) => new()
    {
        Dock = DockStyle.Fill,
        Text = text,
        Font = _t.Body,
        ForeColor = _t.Muted,
        BackColor = Color.Transparent,
        // Measured from the font, never a pixel constant — §30.
        Padding = new Padding(4, TextRenderer.MeasureText("Ag", _t.Body).Height / 3, 0, 0),
        Visible = false
    };

    /// <summary>Wide enough for the dot and the air around it, measured rather than assumed.</summary>
    private int DotColumnWidth => TextRenderer.MeasureText("Ag", _t.Body).Height + 14;

    /// <summary>The machine list's columns as measured, after the dot column (2026-10-14).</summary>
    private (string Title, int Width)[] _deviceColumnWants = Array.Empty<(string, int)>();

    /// <summary>
    /// Each column at its measured width when the list has room for them all; when it has not, every column shrinks by the
    /// same share, so nothing runs past the list's right edge — where the customer's fold chevron is drawn.
    /// </summary>
    private void FitDeviceColumns()
    {
        if (_deviceColumnWants.Length == 0 || _devices.Columns.Count < _deviceColumnWants.Length + 1) return;
        int room = _devices.ClientSize.Width - _devices.Columns[0].Width - 4;
        int want = _deviceColumnWants.Sum(c => c.Width);
        if (room <= 0) return;
        double share = Math.Min(1.0, (double)room / want);
        for (int i = 0; i < _deviceColumnWants.Length; i++)
            _devices.Columns[i + 1].Width = (int)(_deviceColumnWants[i].Width * share);
    }

    /// <summary>
    /// Two tabs above the list: Attended and Unattended, each carrying its own count.
    ///
    /// Two buttons rather than a TabControl. A TabControl on a dark theme paints its own strip
    /// with the system colours and fights every attempt to restyle it — the same fight the chat
    /// ComboBox lost and had to be owner-drawn to win. These are the console's own buttons, they
    /// already wear the tenant's colours, and the selected one is the accent one.
    /// </summary>
    private Panel BuildDeviceTabs()
    {
        var strip = new Panel { Dock = DockStyle.Top, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 8) };
        // ButtonFont, not Body: a RoundButton draws with the theme's button font, and measuring
        // one font while the control draws with another is exactly the mistake §30 is about.
        int h = ButtonHeight(_t.ButtonFont, 16);
        strip.Height = h + strip.Padding.Vertical;

        _tabAttended = new RoundButton(_t, BtnStyle.Accent) { Text = "Attended", Dock = DockStyle.Left, Width = 150, Height = h };
        _tabUnattended = new RoundButton(_t, BtnStyle.Neutral) { Text = "Unattended", Dock = DockStyle.Left, Width = 150, Height = h };
        _tabAttended.Click += (_, _) => ShowTab(DeviceTab.Attended);
        _tabUnattended.Click += (_, _) => ShowTab(DeviceTab.Unattended);

        // Added right-to-left because Left-docked controls stack outward in the order they are
        // added, and Attended must be the left-hand one.
        strip.Controls.Add(_tabUnattended);
        strip.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        strip.Controls.Add(_tabAttended);
        return strip;
    }

    private void ShowTab(DeviceTab tab)
    {
        _tab = tab;
        RefreshDevices();
    }

    /// <summary>
    /// Paints the green or red dot in the first column, and nothing else — every other column is
    /// drawn by the shared themed handler that ran before this one.
    ///
    /// Green is connected right now; red is not. It says the same thing the old Status column said
    /// in two words, at the left edge, without a column heading or a row of repeated text.
    ///
    /// AMBER is the third answer, added 2026-09-21: the machine is up, it is on the Unattended tab,
    /// and unattended access will not work on it because its agent service is not connected. Green
    /// would be a lie — Online is the agent OR the tray app, so a dead agent behind a healthy tray
    /// app is indistinguishable from a working machine — and red would be a different lie, because
    /// the machine can be reached, just not without somebody accepting. The row's tooltip says
    /// which it is in words; a colour on its own is a puzzle, not an answer.
    /// </summary>
    private void DeviceDot(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not DeviceInfo d) return;
        if (e.ColumnIndex == 1 && d.NamedByRequest) { RequestedName(e, d); return; }
        if (e.ColumnIndex != 0) return;
        var back = (e.Item?.Selected ?? false) ? _t.SurfaceAlt : _t.Surface;
        using (var bg = new SolidBrush(back)) e.Graphics.FillRectangle(bg, e.Bounds);

        // Sized from the row, not from a constant: the row is as tall as the font makes it.
        int size = Math.Max(8, Math.Min(e.Bounds.Height - 8, 12));
        var box = new Rectangle(
            e.Bounds.Left + (e.Bounds.Width - size) / 2,
            e.Bounds.Top + (e.Bounds.Height - size) / 2,
            size, size);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var colour = DeviceList.ClaimsUnattendedButCannot(d) ? _t.Orange
                   : d.Online ? _t.Green
                   : Color.FromArgb(0xC0, 0x39, 0x2B);
        using var dot = new SolidBrush(colour);
        e.Graphics.FillEllipse(dot, box);
    }

    /// <summary>
    /// The Who cell of a machine nobody has linked, showing the name its person typed at a support request
    /// (2026-10-18): drawn again over the themed handler's, muted and italic, because it is only what they said.
    /// </summary>
    private void RequestedName(DrawListViewSubItemEventArgs e, DeviceInfo d)
    {
        var selected = e.Item?.Selected ?? false;
        using (var bg = new SolidBrush(selected ? _t.SurfaceAlt : _t.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
        using var italic = new Font(e.Item?.Font ?? _t.Body, FontStyle.Italic);
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? "", italic, Rectangle.Inflate(e.Bounds, -8, 0),
            selected ? _t.SurfaceAltText : _t.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    /// <summary>What the italic name is, in words.</summary>
    private static string RequestedTip(DeviceInfo d) =>
        $"{d.RequestedBy} is the name they gave when it was installed or when they asked for help"
        + (d.RequestedByUtc is { } when ? $" on {when.ToLocalTime():d MMM}" : "")
        + (d.RequestedByEmail.Length > 0 ? $", with {d.RequestedByEmail}" : "")
        + " — not checked.\nRight-click → Link to a customer to file it under a client or person.";

    private void RefreshDevices()
    {
        var all = _deviceMap.Values.ToList();
        _tabAttended.Text = $"Attended  {DeviceList.CountOn(all, DeviceTab.Attended)}";
        // The tab carries the warning too, because the amber dot is only seen by somebody who has
        // already clicked through to the tab and is looking at that row. The count is the thing
        // that makes anybody click.
        int notReady = DeviceList.CountNotReady(all);
        _tabUnattended.Text = notReady > 0
            ? $"Unattended  {DeviceList.CountOn(all, DeviceTab.Unattended)}  ({notReady} not ready)"
            : $"Unattended  {DeviceList.CountOn(all, DeviceTab.Unattended)}";
        // §30, applied sideways: a tab is as wide as its own text makes it. "(3 not ready)" does
        // not fit in the 150 these were built at, and a warning that is clipped off the end of a
        // button is worse than no warning — it reads as a stray bracket.
        _tabAttended.Width = TabWidth(_tabAttended.Text);
        _tabUnattended.Width = TabWidth(_tabUnattended.Text);
        _tabAttended.Style = _tab == DeviceTab.Attended ? BtnStyle.Accent : BtnStyle.Neutral;
        _tabUnattended.Style = _tab == DeviceTab.Unattended ? BtnStyle.Accent : BtnStyle.Neutral;
        _tabAttended.Invalidate(); _tabUnattended.Invalidate();

        _devices.BeginUpdate();
        _devices.Items.Clear();
        _devices.Groups.Clear();

        foreach (var g in DeviceList.Build(all, _tab))
        {
            var header = g.Machines.Count == 1
                ? g.Name
                : $"{g.Name}   ({g.Machines.Count} machines, {g.OnlineCount} online)";
            // The free version has no customers to group by: its own computers, counted against its two.
            if (_free) header = $"Your computers   ({all.Count} of {_basicMachines}, {all.Count(x => x.Online)} online)";
            var lvg = new ListViewGroup(header)
            {
                // Tagged with the customer's name, not the heading: the heading carries a count
                // and changes as machines come and go, and the fold has to survive that.
                Tag = g.Name,
                CollapsedState = _collapsed.Contains(g.Name)
                    ? ListViewGroupCollapsedState.Collapsed
                    : ListViewGroupCollapsedState.Expanded
            };
            _devices.Groups.Add(lvg);

            foreach (var d in g.Machines)
            {
                var it = new ListViewItem(new[]
                {
                    "",                     // the dot, painted in DeviceDot
                    d.OwnerName,            // contact, else client, else the name given at a request, else whoever is signed in
                    d.MachineName,
                    string.IsNullOrWhiteSpace(d.Os) ? "—" : d.Os,
                    d.Spec?.CpuShort ?? "—",
                    d.Spec?.RamText ?? "—",
                    d.Spec?.DiskText ?? "—",
                    AppVersion.Label(AppVersion.Oldest(d.AgentVersion, d.ClientVersion)),
                }) { Tag = d, Group = lvg };
                it.ForeColor = d.Online ? _t.Ink : _t.Muted;
                // The amber dot in words. A colour tells a technician to look; this tells them
                // what to do, and "ask them to restart it" is a different job from "check the
                // network", which is what they would otherwise go and do.
                // A reinstall nobody has confirmed (2026-10-07): said on the row itself, on either tab, because it
                // is the one state here that may mean the computer is not who it says it is.
                if (d.ReinstallUnconfirmed)
                {
                    it.SubItems[2].Text = d.MachineName + "  — reinstalled, not confirmed";
                    it.ToolTipText =
                        "This machine was uninstalled and installed again, and recognised by its hardware. Until a "
                        + "supervisor or the owner confirms it is the same machine (right-click), unattended access waits "
                        + "and connecting asks the person there.";
                }
                else if (DeviceList.ClaimsUnattendedButCannot(d))
                    it.ToolTipText =
                        "Unattended access is allowed on this machine, but its background service is not "
                        + "connected — so connecting will still ask the person there to accept.\n"
                        + "Ask them to restart the computer, or reinstall the support client on it.";
                else if (d.UnattendedEnabled && d.UnattendedPassword)
                    it.ToolTipText = _savedPw.Contains(d.DeviceId)
                        ? "This machine has an unattended password, and yours is saved — Connect will not ask."
                        : "This machine has an unattended password. Connect asks for it once, then remembers it for you.";
                // Installed before auto-update (2026-10-16, stage F): it takes updates only after one more install.
                else if (!d.SelfUpdates && d.AgentVersion.Length > 0)
                    it.ToolTipText = "This machine was installed before automatic updates, so it cannot update itself yet.\n"
                        + "Connect, then Tools → Scripts → Update this machine — once. It updates itself from then on.";
                // The name they gave at a support request (2026-10-18): drawn muted and italic by DeviceDot, and said
                // in words here when nothing more pressing has the tooltip.
                if (d.NamedByRequest && string.IsNullOrEmpty(it.ToolTipText))
                    it.ToolTipText = RequestedTip(d);
                // flag machines that are nearly full — the most common cause of "it's slow"
                if (d.Spec is { DiskTotalGb: > 0 } s && s.DiskUsedPercent >= 90) it.ForeColor = _t.BadText;   // the palette's red: the fixed dark one vanished on a dark list
                _devices.Items.Add(it);
            }
        }

        _devices.EndUpdate();
        RefreshChatTargets();
    }

    /// <summary>
    /// Connects to the selected machine, having first asked why.
    ///
    /// The reason becomes the ticket's description — every session gets its own ticket since
    /// 3.6.6, and without this every one of them would be called "Remote session on
    /// DESKTOP-2N6FVGT" and say nothing about what anybody did. It is the other half of 3.5.7: a
    /// customer has had to say what is wrong since then, and a technician letting themselves into
    /// somebody's machine with nobody there should have to say why at least as much.
    ///
    /// The answer travels WITH the connect message. The ticket is written on the server the moment
    /// the session starts, so a reason sent afterwards would be racing the thing it describes.
    /// </summary>
    /// <summary>The machine the technician has picked, or null.</summary>
    private DeviceInfo? SelectedDevice() =>
        _devices.SelectedItems.Count > 0 && _devices.SelectedItems[0].Tag is DeviceInfo d ? d : null;

    /// <summary>
    /// Says which customer or person a machine belongs to.
    ///
    /// Every role may do this, on purpose — see the server's <c>Permissions.CanLinkDevices</c>.
    /// Until a machine is linked its tickets never reach a client page, and the person looking at
    /// the stray is the one who should be able to fix it.
    ///
    /// Nothing is applied here. The server writes it and broadcasts the machine's new owner to
    /// every open console, this one included, so the row moves by exactly the path a change made
    /// in the portal takes. A local update as well would be a second source of truth for the same
    /// fact, and the two would eventually disagree.
    /// </summary>
    private void LinkSelectedDevice()
    {
        var d = SelectedDevice();
        if (d == null) return;
        using var dlg = new LinkDeviceDialog(_t, _link, d);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _link.LinkDevice(d.DeviceId, dlg.ClientId, dlg.ContactId, dlg.NewContactName);
    }

    /// <summary>The same message Link's "Not linked to anybody" row sends: nobody, company or person.</summary>
    private void UnlinkSelectedDevice()
    {
        var d = SelectedDevice();
        if (d == null || d.ClientId <= 0) return;
        if (MessageBox.Show(this, $"Unlink {d.MachineName} from {d.ClientName}?\n\nIts tickets keep their client. It can be linked again at any time.",
                "Unlink", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _link.LinkDevice(d.DeviceId, 0, 0);
    }

    /// <summary>
    /// The free version's Remove (2026-10-06): asked first, because it is one of only two a month and the
    /// computer stops being reachable. The server decides — the monthly limit refuses in words (OnError).
    /// </summary>
    private void RemoveSelectedDevice()
    {
        var d = SelectedDevice();
        if (d == null) return;
        // The full version (2026-10-06): a supervisor or the owner, with a reason — RemoveDeviceDialog.
        if (!_free)
        {
            using var ask = new RemoveDeviceDialog(_t, d.MachineName, d.AgentOnline);
            if (ask.ShowDialog(this) != DialogResult.OK) return;
            _link.RemoveDevice(d.DeviceId, ask.Reason, ask.Note);
            return;
        }
        if (MessageBox.Show(this,
                $"Remove {d.MachineName}? It will no longer be reachable, and its place is free for another computer.\n\n"
                + $"{(_basic ? "The Basic version" : "The Free version")} can remove {FreePlan.RemovalsPerMonth} computers a month.",
                "Remove this computer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _link.RemoveDevice(d.DeviceId);
    }

    /// <summary>
    /// A reinstalled machine IS the one on the list (2026-10-07). Asked first, in words that say what is at stake:
    /// confirming a stranger's PC would let unattended sessions land on it. The server decides (OnError).
    /// </summary>
    private void ConfirmSelectedReinstall()
    {
        var d = SelectedDevice();
        if (d == null || !d.ReinstallUnconfirmed) return;
        if (MessageBox.Show(this,
                $"{d.MachineName} was uninstalled and installed again, and it was recognised by its hardware.\n\n"
                + (_free
                    ? "Confirm only if you installed it again yourself. If you did not, another computer may be pretending "
                      + "to be this one: leave it, and remove it.\n\n"
                    : "Confirm only if you know about the reinstall — you did it, or the customer told you. If nobody "
                      + "expected it, another computer may be pretending to be this one: leave it, and call the customer.\n\n")
                + "Once confirmed, unattended access works on it again as its person set it.",
                "Confirm it is the same machine", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _link.ConfirmReinstall(d.DeviceId);
    }

    private void ConnectSelectedDevice()
    {
        if (_devices.SelectedItems.Count == 0) return;
        if (_devices.SelectedItems[0].Tag is not DeviceInfo d) return;
        if (!d.Online) { MessageBox.Show(this, "That device is offline.", "Connect"); return; }
        if (OverSessionLimit(d.DeviceId)) return;

        // The password box only when the server says this machine will want it: unattended allowed
        // AND a password set — and not when this technician has typed it before and the server
        // remembers it for them (2026-10-06). Attended machines ask the person, as ever.
        // A reinstall nobody has confirmed goes attended (the person is asked), so no password then.
        if (_free) { AskAndConnect(d, NeedsPasswordBox(d), "", ""); return; }

        // Nothing asked FIRST (2026-10-09). Clint: "Reconnecting within 30 minutes should automatically be
        // part of the same ticket with no reason necessary." Only the server knows whether this machine has
        // a ticket to carry on (TicketService.ActiveForDeviceAsync), so the console asks it by connecting
        // without a reason: carried on, the session simply starts; otherwise the answer is NeedsReason and
        // the window opens as it always has. A password typed earlier this run goes with it; one that is
        // needed and not known comes back as a refusal, and the window then asks for the password alone.
        string pw; lock (_waitLock) { pw = _passwords.TryGetValue(d.DeviceId, out var p) ? p : ""; _continuing.Add(d.DeviceId); }
        _link.ConnectDevice(d.DeviceId, "", pw);
    }

    private bool NeedsPasswordBox(DeviceInfo d) =>
        d.UnattendedEnabled && !d.ReinstallUnconfirmed && d.UnattendedPassword && !_savedPw.Contains(d.DeviceId);

    /// <summary>Machines whose connection went to the server without a reason, hoping to carry on a ticket.
    /// While one is here, a password refusal asks for the password only — there is a ticket, and no reason is
    /// wanted. Cleared when the server says a reason is needed after all.</summary>
    private readonly HashSet<string> _continuing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The server has no ticket to carry on for this machine: ask why, as every connection used to.</summary>
    private void OnReasonNeeded(string deviceId)
    {
        lock (_waitLock) _continuing.Remove(deviceId);
        var d = _devices.Items.Cast<ListViewItem>().Select(i => i.Tag).OfType<DeviceInfo>().FirstOrDefault(x => x.DeviceId == deviceId);
        if (d == null) return;
        AskAndConnect(d, NeedsPasswordBox(d), "", "");
    }

    /// <summary>Machines whose "why, and the password" window is open, so a refusal arriving twice
    /// (a reconnect loop) never stacks two of them.</summary>
    private readonly HashSet<string> _askingPassword = new(StringComparer.OrdinalIgnoreCase);

    private void AskAndConnect(DeviceInfo d, bool askPassword, string reason, string complaint)
    {
        // The free version (2026-10-06) asks no reason — it has no tickets — so with the password saved
        // there is nothing to ask at all: straight in.
        if (_free && !askPassword) { _link.ConnectDevice(d.DeviceId, "", ""); return; }
        if (!_askingPassword.Add(d.DeviceId)) return;
        try
        {
            // Carrying on a ticket (2026-10-09): only the password is wanted, so only the password is asked.
            bool continuing; lock (_waitLock) continuing = _continuing.Contains(d.DeviceId);
            using var ask = new ReasonDialog(_t, d.MachineName, d.OwnerName, askPassword, reason, complaint, askReason: !_free && !continuing);
            if (ask.ShowDialog(this) != DialogResult.OK) return;
            // Remembered so a reconnect after a restart carries the same reason, and password,
            // rather than asking again in the middle of a reboot. Held in memory only.
            lock (_waitLock)
            {
                if (ask.Reason.Length > 0 || !_reasons.ContainsKey(d.DeviceId)) _reasons[d.DeviceId] = ask.Reason;
                if (ask.Password.Length > 0) _passwords[d.DeviceId] = ask.Password; else _passwords.Remove(d.DeviceId);
            }
            _link.ConnectDevice(d.DeviceId, ask.Reason, ask.Password);
        }
        finally { _askingPassword.Remove(d.DeviceId); }
    }

    /// <summary>
    /// The server refused a connection for want of the machine's unattended password, or for a
    /// wrong one. The remembered password is forgotten — a reconnect loop resending a wrong one
    /// would lock the machine out — and the same window comes back with the reason kept and the
    /// server's words in it.
    /// </summary>
    private void OnPasswordRefused(string deviceId, string message, bool noneGiven)
    {
        lock (_waitLock) _passwords.Remove(deviceId);
        var d = _devices.Items.Cast<ListViewItem>().Select(i => i.Tag).OfType<DeviceInfo>().FirstOrDefault(x => x.DeviceId == deviceId);
        if (d == null) { MessageBox.Show(this, message, "Connect", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        string reason; lock (_waitLock) reason = _reasons.TryGetValue(deviceId, out var r) ? r : "";
        // None was sent, which is how the first ask looks when a ticket is being carried on (2026-10-09): the
        // window already says it wants the password, so the server saying so again in red would read as a fault.
        AskAndConnect(d, true, reason, noneGiven ? "" : message);
    }

    /// <summary>Right-click → Saved passwords, for a supervisor or the owner.</summary>
    private ToolStripMenuItem _savedWhoItem = null!;

    /// <summary>
    /// The server's answer to "who has this machine's password saved": a Clear for each person and
    /// one for everybody. Names and when, never the password — the server has none to send.
    /// </summary>
    private void FillSavedWho(SavedPasswordWhoMsg m)
    {
        if (!Equals(_savedWhoItem.Tag, m.DeviceId)) return;   // an answer about a machine no longer pointed at
        var items = _savedWhoItem.DropDownItems;
        items.Clear();
        if (m.People.Count == 0)
        {
            items.Add(new ToolStripMenuItem("Nobody has it saved") { Enabled = false });
            return;
        }
        foreach (var p in m.People)
        {
            var who = p.TechnicianId;
            var item = new ToolStripMenuItem($"Clear {p.Name}'s  (saved {p.SavedUtc.ToLocalTime():d MMM yyyy})");
            item.Click += (_, _) => _link.ForgetSavedPassword(m.DeviceId, who);
            items.Add(item);
        }
        items.Add(new ToolStripSeparator());
        var all = new ToolStripMenuItem("Clear everybody's");
        all.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Everybody will have to type {m.MachineName}'s unattended password again. Clear them all?",
                    "Saved passwords", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                _link.ForgetSavedPassword(m.DeviceId, 0, all: true);
        };
        items.Add(all);
    }

    // ---- sessions ----
    private void OnSessionReady(SessionInfo s)
    {
        TrackPending(s);
        ShowWaitingFor(s);
        // Somebody from outside on one of ours (3.9): their pop-up follows the session — allowed, ended.
        if (_visitorPopups.TryGetValue(s.SessionId, out var vp) && !vp.IsDisposed) vp.Follow(s);

        // Anyone waiting for a session on this machine — the Reconnect button in a session
        // window, or a window waiting for a machine to come back from a restart. Answered before
        // the auto-open test below, so a window that asked for this session does not get a
        // second one opened next to it.
        if (s.HostConnected || s.Technicians.Count > 0)
        {
            List<TaskCompletionSource<SessionInfo?>> waiting;
            lock (_waitLock)
            {
                waiting = _awaitingSession.TryGetValue(s.DeviceId, out var w) ? w.ToList() : new();
                _awaitingSession.Remove(s.DeviceId);
            }
            if (waiting.Count > 0)
            {
                _sessionMap[s.SessionId] = s; _openSessions.Add(s.SessionId); RefreshSessions();
                foreach (var tcs in waiting) tcs.TrySetResult(s);
                return;
            }
        }
        if (s.Technicians.Count == 0 && !s.HostConnected) { _sessionMap.Remove(s.SessionId); _openSessions.Remove(s.SessionId); RefreshSessions(); return; }
        _sessionMap[s.SessionId] = s;
        RefreshSessions();
        // Open automatically for whoever started the session; at that moment nobody has joined
        // as a viewer yet, so Technicians is still empty and cannot be the only test.
        bool mine = s.StartedBy == _link.DisplayName || s.Technicians.Contains(_link.DisplayName);
        if (mine && !_openSessions.Contains(s.SessionId))
        {
            _openSessions.Add(s.SessionId);
            OpenSession(s);
        }
    }

    // ---- waiting for the person at the machine (internal testing, 2026-10-02) ----

    /// <summary>Sessions this technician started that have not been answered yet, by id.</summary>
    private readonly Dictionary<string, ConsentWaitForm> _waits = new();

    /// <summary>A session started but not yet under way on each machine — so a Reconnect waits
    /// for a person deciding rather than timing out under them. Guarded by _waitLock.</summary>
    private readonly Dictionary<string, SessionInfo> _pending = new();

    private static bool NotYetUnderWay(SessionInfo s) => !s.Ended && !s.HostConnected && s.Technicians.Count == 0;

    private void TrackPending(SessionInfo s)
    {
        List<TaskCompletionSource<SessionInfo?>> giveUp = new();
        lock (_waitLock)
        {
            bool wasPending = _pending.TryGetValue(s.DeviceId, out var p) && p.SessionId == s.SessionId;
            if (NotYetUnderWay(s)) _pending[s.DeviceId] = s;
            else if (wasPending) _pending.Remove(s.DeviceId);
            // The session a Reconnect was waiting for ended before anybody got in — declined, or
            // nobody answered. Its answer is "no", now, rather than at the end of a timeout.
            if (s.Ended && wasPending && _awaitingSession.Remove(s.DeviceId, out var w)) giveUp = w;
        }
        foreach (var tcs in giveUp) tcs.TrySetResult(null);
    }

    /// <summary>Opens, updates or closes the "waiting for them to accept" window for a session of ours.</summary>
    private void ShowWaitingFor(SessionInfo s)
    {
        if (_waits.TryGetValue(s.SessionId, out var wf))
        {
            if (!wf.Update(s)) { _waits.Remove(s.SessionId); wf.Close(); return; }
            // On screen at once when the question is, or when it is over and says why.
            if (!wf.Visible && (s.AwaitingConsent || s.Ended)) try { wf.Show(this); } catch { }
            if (s.Ended) _waits.Remove(s.SessionId);
            return;
        }
        if (s.StartedBy != _link.DisplayName || !NotYetUnderWay(s)) return;

        wf = new ConsentWaitForm(_t, s);
        wf.CancelRequested += id => _link.EndSession(id);
        wf.FormClosed += (_, _) => _waits.Remove(s.SessionId);
        _waits[s.SessionId] = wf;
        if (s.AwaitingConsent) { wf.Show(this); return; }
        // Not asking (yet): an unattended host connects within a second or two, and a window that
        // flashes up and vanishes is worse than none. Shown only if it is still waiting by then.
        var later = new System.Windows.Forms.Timer { Interval = 1500 };
        later.Tick += (_, _) =>
        {
            later.Stop(); later.Dispose();
            if (_waits.TryGetValue(s.SessionId, out var still) && still == wf && !wf.Visible && !wf.IsDisposed)
                try { wf.Show(this); } catch { }
        };
        later.Start();
    }

    private void RefreshSessions()
    {
        _sessions.BeginUpdate(); _sessions.Items.Clear();
        foreach (var s in _sessionMap.Values)
            _sessions.Items.Add(new ListViewItem(new[] { s.MachineName, string.Join(", ", s.Technicians), s.HostConnected ? "✔" : "…" }) { Tag = s });
        _sessions.EndUpdate();
        _relayout();
    }

    private void JoinSelectedSession()
    {
        if (_sessions.SelectedItems.Count == 0) return;
        if (_sessions.SelectedItems[0].Tag is not SessionInfo s) return;
        // Already open here: bring it forward rather than a second window on the same session.
        var already = _sessionWindows.FirstOrDefault(w => !w.IsDisposed && w.DeviceId == s.DeviceId);
        if (already != null) { if (already.WindowState == FormWindowState.Minimized) already.WindowState = FormWindowState.Normal; already.Activate(); return; }
        if (OverSessionLimit(s.DeviceId, "Join session")) return;
        _openSessions.Add(s.SessionId); OpenSession(s);
    }

    /// <summary>
    /// Opens the technician's tickets IN THE CONSOLE — rebuilt for the workflows on 2026-09-24.
    ///
    /// From 3.6.3 to then this opened the portal in a browser, because the old two-pane window was
    /// cramped. Clint reversed that on 2026-09-20 once the New ticket dialog had been rebuilt, with
    /// the window itself to be rebuilt when the workflows were written: a technician's day is the
    /// stage buttons, Work done and the quote, and they belong where the technician works. The
    /// browser is still one click away, signed in — "Open in browser" in the window — for the
    /// photographs and for Request quote, which is the office's.
    ///
    /// One window, brought to the front if it is already open. With somebody waiting on a reply it
    /// opens on "Waiting on my reply", which is the first thing to see.
    /// </summary>
    /// <param name="filter">A queue to open on; null chooses: a reply waiting first, then the billing queue.</param>
    /// <param name="ticketId">A ticket to show at once — the billing pop-up's Open.</param>
    private void OpenTickets(string? filter = null, int ticketId = 0)
    {
        filter ??= _mentionCount > 0 ? "asked" : _billingCount > 0 ? "billing" : null;
        if (_ticketsForm is { IsDisposed: false })
        {
            if (_ticketsForm.WindowState == FormWindowState.Minimized) _ticketsForm.WindowState = FormWindowState.Normal;
            _ticketsForm.Activate();
            if (filter != null) _ticketsForm.ShowFilter(filter);
        }
        else
        {
            _ticketsForm = new TicketsForm(_link, _t, _brand, filter ?? "mine");
            _ticketsForm.Show();
        }
        if (ticketId > 0) _link.OpenTicket(ticketId);
    }

    private TicketsForm? _ticketsForm;
    private System.Windows.Forms.Timer? _pendingTicketsTimer;

    // ---- @mentions (stage 3.7) and the billing queue (2026-10-05) ----
    private int _mentionCount, _billingCount;
    private readonly ToolTip _ticketsTip = new();

    /// <summary>
    /// The badge: how many tickets have somebody waiting for this technician's reply. It stays until
    /// they reply on the ticket (Clint, answer 18) — the server says when that happens.
    /// </summary>
    private void ShowMentionCount(int n) { _mentionCount = n; DrawTicketsBadge(); }

    /// <summary>
    /// The billing queue, for the people told when a job lands in it (the server sends this to nobody
    /// else): the count joins the Tickets button's, and a job that has just arrived pops up.
    /// </summary>
    private void OnBillingQueue(BillingQueueMsg m)
    {
        _billingCount = m.Count;
        DrawTicketsBadge();
        if (m.Arrived == null) return;
        if (_billingPopup is not { IsDisposed: false })
        {
            _billingPopup = new BillingPopup(_t);
            _billingPopup.OpenTicket += id => OpenTickets("billing", id);
        }
        _billingPopup.Arrived(m.Arrived, this);
    }

    private BillingPopup? _billingPopup;

    // ---- Latest activity (2026-10-26, HANDOVER §164) ----
    private ActivityForm? _activity;
    private readonly ToolTip _activityTip = new();
    /// <summary>All or Mine, as the server remembers it for me — the window opens on it.</summary>
    private bool _activityMine;

    private void ShowActivity()
    {
        if (_activity is not { IsDisposed: false })
        {
            _activity = new ActivityForm(_link, _t, _activityMine);
            _activity.OpenTicket += OpenTicketFromActivity;
        }
        _activity.ShowFor();
    }

    /// <summary>A line of Latest activity: its ticket in the ticket window, which is opened for it if it is not open.</summary>
    private void OpenTicketFromActivity(int ticketId)
    {
        if (_ticketsForm is not { IsDisposed: false }) { _ticketsForm = new TicketsForm(_link, _t, _brand, "mine"); _ticketsForm.Show(); }
        else { if (_ticketsForm.WindowState == FormWindowState.Minimized) _ticketsForm.WindowState = FormWindowState.Normal; _ticketsForm.Activate(); }
        _link.OpenTicket(ticketId);
    }

    /// <summary>
    /// What is new to me since I last looked, in what I chose (All or Mine): on the button, never red — updates are the
    /// ordinary run of a day, not something waiting on me (that is the Tickets button's count). The open window takes it
    /// as "fetch again".
    /// </summary>
    private void OnActivityCount(ActivityCountMsg m)
    {
        _activityMine = m.Mine;
        _activityBtn.Text = m.Count > 0 ? $"Latest activity ({ActivityWords.Count(m.Count)})" : "Latest activity";
        _activityBtn.Invalidate();
        _activityTip.SetToolTip(_activityBtn, (m.Count > 0 ? $"{ActivityWords.Count(m.Count)} new on {(m.Mine ? "your tickets" : "the tickets")} since you last looked.\n" : "")
            + "Every update on the tickets as it is posted — comments, stage changes, customer replies, assignments");
        _activity?.CountChanged(m);
    }

    // ---- outside help (3.9) ----

    /// <summary>Connect with a code: a computer of ANOTHER company's customer. The server answers with a
    /// session started by this technician (the usual waiting window, then the session window) or in words.</summary>
    private void ConnectWithCode()
    {
        if (OverSessionLimit("", "Connect with a code")) return;
        using var dlg = new CodeEntryDialog(_t);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _link.ConnectWithCode(dlg.Code);
    }

    /// <summary>One pop-up per visit, by session, so each follows its own session.</summary>
    private readonly Dictionary<string, VisitorPopup> _visitorPopups = new();

    /// <summary>Somebody from another company is connecting to one of OUR machines with a code.</summary>
    private void ShowVisitor(OutsiderVisitMsg v)
    {
        if (_visitorPopups.ContainsKey(v.SessionId)) return;
        var p = new VisitorPopup(_t, v);
        p.WatchClicked += id =>
        {
            // Joining a session in progress is opening its window: the session list's own way in.
            if (_sessionMap.TryGetValue(id, out var s) && !_openSessions.Contains(id) && !OverSessionLimit(s.DeviceId, "Watch"))
            { _openSessions.Add(id); OpenSession(s); }
        };
        p.EndClicked += id =>
        {
            // Brought in by one of ours (§155): Remove takes them out and the session goes on — it is ours, not theirs.
            if (v.InvitedBy.Length > 0)
            {
                if (MessageBox.Show(this, $"Take {v.Visitor} out of the session on {v.MachineName}? {v.InvitedBy} stays connected.", "Outside help",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    _link.Send(new OutsiderRemoveMsg { SessionId = id });
                return;
            }
            if (MessageBox.Show(this, $"End {v.Visitor}'s session on {v.MachineName}?", "Outside help",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                _link.EndSession(id);
        };
        p.FormClosed += (_, _) => _visitorPopups.Remove(v.SessionId);
        _visitorPopups[v.SessionId] = p;
        p.ShowNear(this, _visitorPopups.Count - 1);
    }

    /// <summary>
    /// ONE count on the Tickets button — Clint's rule, one control beats two — and the tooltip says
    /// what it is made of. Both kinds stay until they are dealt with: a reply, or the office's step.
    /// </summary>
    private void DrawTicketsBadge()
    {
        int n = _mentionCount + _billingCount;
        _ticketsBtn.Text = n > 0 ? $"Tickets ({n})" : "Tickets";
        _ticketsBtn.Style = n > 0 ? BtnStyle.Danger : BtnStyle.Neutral;
        var why = new List<string>();
        if (_mentionCount > 0) why.Add($"Somebody asked you something on {_mentionCount} ticket{(_mentionCount == 1 ? "" : "s")}. Reply there and this clears.");
        if (_billingCount > 0) why.Add($"{_billingCount} job{(_billingCount == 1 ? " is" : "s are")} in the billing queue waiting for the office.");
        _ticketsTip.SetToolTip(_ticketsBtn, string.Join("\n", why));
        _ticketsBtn.Invalidate();
    }

    private void OnPortalLink(PortalLinkMsg m)
    {
        _pendingTicketsTimer?.Stop(); _pendingTicketsTimer?.Dispose(); _pendingTicketsTimer = null;
        _ticketsBtn.Enabled = true;

        if (m.Error.Length > 0 || string.IsNullOrWhiteSpace(m.Url))
        {
            MessageBox.Show(this, m.Error.Length > 0 ? m.Error : "The server did not send a link.",
                "Tickets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var url = _link.ResolvePortalUrl(m.Url);
        try
        {
            // UseShellExecute so it opens in whatever browser they actually use.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.Write($"tickets: cannot open browser — {ex.Message}");
            MessageBox.Show(this, "Could not open your browser.", "Tickets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// Books a job in from the console. A workshop booking will not be accepted without three
    /// photos of the machine as it arrived; the dialog says so, and the SERVER enforces it.
    /// </summary>
    private void NewTicket()
    {
        using var dlg = new NewTicketDialog(_t, _link);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Result != null) _link.CreateTicket(dlg.Result);
    }

    private void OpenSession(SessionInfo s)
    {
        // Whose machine it is, as this list says it: its identity is pinned under that too (end-to-end encryption).
        var f = new SessionForm(_link.WsBase, _link.Token, s, _t, _brand, _free, _deviceMap.TryGetValue(s.DeviceId, out var owned) ? owned.OwnerName : "");
        // Remote printing names the printer on their computer for whoever is signed in here.
        f.Technician = _link.DisplayName;
        // A session window holds a session socket; only this window holds the dashboard socket a
        // session can be STARTED from. It needs that twice: the Reconnect button after a session
        // has genuinely ended, and a machine coming back from a restart with a new host.
        f.StartFreshSession = StartSessionOnAsync;
        // The knowledge base (3.8.5): the window says what there is for this machine's client, and
        // opens it in the browser through this window's signed-in socket — a session socket cannot.
        f.OpenPortal = path => _link.RequestPortalLink(path);
        // Get outside help (§155): on this window's signed-in socket. Not on the free version, which has no outside help.
        if (!_free)
        {
            f.SendToServer = _link.Send;
            f.ServerBase = _link.ResolvePortalUrl("/").TrimEnd('/');
            f.CompanyName = _brand.CompanyName;
        }
        _sessionWindows.Add(f);
        f.FormClosed += (_, _) => { _openSessions.Remove(s.SessionId); _sessionWindows.Remove(f); };
        f.Show();
        _link.RequestKnowledgeCount(s.DeviceId);
    }

    private readonly object _waitLock = new();
    private readonly Dictionary<string, List<TaskCompletionSource<SessionInfo?>>> _awaitingSession = new();

    /// <summary>
    /// What this technician last said they were connecting to each machine for, so a reconnect can
    /// carry it forward instead of asking again.
    /// </summary>
    private readonly Dictionary<string, string> _reasons = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Unattended passwords typed this run, by machine, for the same reconnects. Never written
    /// anywhere; forgotten the moment the server refuses one.</summary>
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Machines whose unattended password the SERVER remembers for this technician (2026-10-06), so
    /// Connect leaves the box out. The server still decides: a password changed at the machine since
    /// comes back as a refusal, and the box opens as it always has.
    /// </summary>
    private HashSet<string> _savedPw = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A supervisor or the owner: may see who has a machine's password saved, and clear it.</summary>
    private bool _mayManageSaved;

    /// <summary>
    /// The reason to send with a reconnect. The original where we have one — and where we do not,
    /// an honest sentence rather than an empty string, which the server would refuse.
    ///
    /// The fallback only happens on a console that was restarted mid-job, because this path is
    /// reached exclusively from a session window that already exists. It is not a way past the
    /// prompt: nothing calls it to START a session.
    /// </summary>
    private string ReasonFor(string deviceId)
    {
        lock (_waitLock)
            if (_reasons.TryGetValue(deviceId, out var r) && r.Length > 0) return r;
        return "Reconnecting to a session already in progress.";
    }

    /// <summary>
    /// Ask the server to start a session on a machine, and wait for it. Null means it did not
    /// happen inside the timeout — the machine is off, offline, or still restarting. The caller
    /// decides what that means; a machine on its way back from a reboot is expected to say no
    /// several times before it says yes.
    /// </summary>
    private async Task<SessionInfo?> StartSessionOnAsync(string deviceId, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SessionInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitLock)
        {
            if (!_awaitingSession.TryGetValue(deviceId, out var list)) _awaitingSession[deviceId] = list = new();
            list.Add(tcs);
        }
        try
        {
            // An offline device answers with an ErrorMsg rather than a SessionReadyMsg, so the
            // timeout is the real answer here. Kept short: the caller retries.
            //
            // The ORIGINAL reason is sent again rather than asking for a new one. This path is the
            // Reconnect button and a machine coming back from a restart — the same job, continuing
            // — and a dialog appearing in the middle of a reboot loop would be asked over and over
            // for something nobody has changed their mind about. Within 30 minutes the server
            // reuses the existing ticket anyway; past that it opens a new one, which is correct
            // and now says why instead of "Remote session on …".
            var reason = ReasonFor(deviceId);
            string password; lock (_waitLock) password = _passwords.TryGetValue(deviceId, out var pw) ? pw : "";
            Inv(() => _link.ConnectDevice(deviceId, reason, password));
            while (true)
            {
                var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
                if (done == tcs.Task) return tcs.Task.Result;
                // The person at the machine is still reading the question. Their answer is worth
                // waiting for — giving up under them would leave a "yes" that nobody is there for.
                bool asking; lock (_waitLock) asking = _pending.TryGetValue(deviceId, out var p) && p.AwaitingConsent;
                if (!asking) return null;
            }
        }
        catch { return null; }
        finally
        {
            lock (_waitLock)
                if (_awaitingSession.TryGetValue(deviceId, out var list))
                { list.Remove(tcs); if (list.Count == 0) _awaitingSession.Remove(deviceId); }
        }
    }

    // ---- chat ----
    private void RefreshChatTargets()
    {
        var current = _chatTarget.SelectedItem as ChatTarget;
        _chatTarget.BeginUpdate(); _chatTarget.Items.Clear();
        foreach (var d in _deviceMap.Values.Where(d => d.Online).OrderBy(d => d.MachineName))
            _chatTarget.Items.Add(new ChatTarget(d.DeviceId, $"{d.MachineName} ({d.OwnerName})"));   // whose, as the list says it (2026-10-18)
        if (_chatTarget.Items.Count > 0)
        {
            int idx = current != null ? _chatTarget.Items.Cast<ChatTarget>().ToList().FindIndex(t => t.DeviceId == current.DeviceId) : -1;
            _chatTarget.SelectedIndex = idx >= 0 ? idx : 0;
        }
        _chatTarget.EndUpdate();
    }

    /// <summary>The machine list's right-click Chat: the chat card's dropdown set to that machine and
    /// the cursor in the message box, ready to type.</summary>
    private void ChatWithSelectedDevice()
    {
        var d = SelectedDevice();
        if (d == null) return;
        int idx = _chatTarget.Items.Cast<ChatTarget>().ToList().FindIndex(t => t.DeviceId == d.DeviceId);
        if (idx < 0) return;
        _chatTarget.SelectedIndex = idx;
        _chatInput.Box.Focus();
    }

    private void SendChat()
    {
        var t = _chatInput.Value.Trim(); if (t.Length == 0) return;
        if (_chatTarget.SelectedItem is not ChatTarget target) { MessageBox.Show(this, "No online client selected to chat with.", "Chat"); return; }
        _link.SendChat(target.DeviceId, t);
        _chatInput.Value = "";
    }

    /// <param name="live">False for the recent chat a (re)connect hands back: drawn, never dinged or ballooned again.</param>
    private void AppendChat(ChatMsg m, bool live = true)
    {
        var machine = m.DeviceId != null && _deviceMap.TryGetValue(m.DeviceId, out var d) ? d.MachineName : "";
        _chatLog.SelectionStart = _chatLog.TextLength;
        _chatLog.SelectionColor = _t.Muted; _chatLog.SelectionFont = _t.Small;
        _chatLog.AppendText($"{m.Ts.ToLocalTime():HH:mm} {(machine.Length > 0 ? "[" + machine + "] " : "")}");
        _chatLog.SelectionColor = m.FromTech ? _t.Grape : Color.FromArgb(0x0F, 0x76, 0x5C); _chatLog.SelectionFont = _t.BodyBold;
        _chatLog.AppendText($"{m.From}: ");
        _chatLog.SelectionFont = _t.Body; _chatLog.SelectionColor = _t.Ink;
        _chatLog.AppendText(m.Text + "\n"); _chatLog.ScrollToCaret();
        if (!m.FromTech && live)
        {
            Sounds.Ding();
            TrayAlert(m.From.Length > 0 ? m.From : "Message from a customer", m.Text, ToolTipIcon.Info);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool FlashWindow(IntPtr hwnd, bool invert);
    private void FlashWindow() { try { FlashWindow(Handle, true); } catch { } }

    private sealed record ChatTarget(string DeviceId, string Label) { public override string ToString() => Label; }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel(); _blink.Stop();
        // The tray icon outlives the window unless it is told not to, and a leftover icon that
        // does nothing when clicked is its own small support call.
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_popup is { IsDisposed: false }) { _popup.HideCall(); _popup.Dispose(); }
        base.OnFormClosed(e);
    }
}
