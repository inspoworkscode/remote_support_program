using System.Drawing;
using System.Windows.Forms;
using RemoteSupport.Shared;
using RemoteSupport.UI;

namespace RemoteSupport.Technician;

/// <summary>A live support session: screen view, control, chat, files, scripts and system info.</summary>
public sealed partial class SessionForm : Form
{
    // Not readonly: a reboot, or a reconnect after the session was lost, re-attaches this window
    // to a NEW session on the same machine. The window, its chat log and its transfer history
    // survive; the session underneath it does not have to. See ReattachAsync.
    private SessionInfo _session;
    private SessionLink _link;
    private CancellationTokenSource _linkCts = new();
    private readonly string _wsBase, _token;
    private readonly CancellationTokenSource _cts = new();
    private readonly Theme _t;

    private ScreenView _view = null!;
    private ComboBox _monitorBox = null!;
    private RoundSlider _quality = null!;
    private CheckBox _controlChk = null!;
    private Pill _connPill = null!;
    // ---- recording (1.6) ----
    private Pill _recPill = null!;
    private ToolStripMenuItem _recordItem = null!;
    // ---- the knowledge base (3.8.5) ----
    private ToolStripMenuItem _kbItem = null!;
    private Pill _kbPill = null!;
    /// <summary>What the server said this machine's client has. Null until it answers.</summary>
    private KnowledgeCountMsg? _kb;
    /// <summary>
    /// Opens a portal page as this technician. Set by the dashboard, which holds the signed-in socket
    /// a hand-off is asked over; a session socket cannot ask for one.
    /// </summary>
    public Action<string>? OpenPortal { get; set; }
    /// <summary>The machine this window is on, so the dashboard can bring it the knowledge base's answer.</summary>
    public string DeviceId => _session.DeviceId;
    /// <summary>Its name, for the two-session limit's words (SessionLimit).</summary>
    public string MachineName => _session.MachineName;

    /// <summary>
    /// The server's answer about the knowledge base: "Knowledge base for Acme (3)" on the Tools menu,
    /// and on the status bar when there is anything to read — the moment the session opens, so the
    /// technician is told rather than having to think to look (Clint's recommendation 2).
    /// </summary>
    public void ShowKnowledge(KnowledgeCountMsg kc)
    {
        _kb = kc;
        if (kc.ClientId <= 0) { _kbItem.Text = "Knowledge base"; _kbPill.Visible = false; return; }
        var words = $"Knowledge base for {kc.ClientName} ({kc.Count})";
        _kbItem.Text = words;
        if (kc.Count <= 0) { _kbPill.Visible = false; return; }
        // Measured from its own font, never a number written down (§30), and never more than a third
        // of the bar, so a long client name cannot push the rest of the status bar off the window —
        // a name that will not fit is left to the Tools menu, which has room for it.
        int room = Math.Max(160, _status.ClientSize.Width / 3);
        var text = "📘 " + words;
        if (TextRenderer.MeasureText(text, _kbPill.Font).Width + 24 > room) text = $"📘 Knowledge base ({kc.Count})";
        _kbPill.Text = text;
        _kbPill.Width = TextRenderer.MeasureText(text, _kbPill.Font).Width + 24;
        _toolTip.SetToolTip(_kbPill, $"How things are done for {kc.ClientName} — opens in your browser");
        _kbPill.Visible = true;
    }

    private void OpenKnowledge()
    {
        if (OpenPortal == null) return;
        // By the client's number in the company (HANDOVER §159); an older server sent only the id, which still lands.
        OpenPortal(_kb is { ClientNumber: > 0 } n ? $"/portal/kb?c={n.ClientNumber}"
            : _kb is { ClientId: > 0 } k ? $"/portal/kb?client={k.ClientId}" : "/portal/kb");
    }
    private readonly System.Windows.Forms.Timer _recTick = new() { Interval = 1000 };
    private SessionRecorder? _recorder;
    private Label _partLabel = null!;

    // ---- end-to-end encryption (2026-10-07; docs/NEXT-SESSION-E2E.md) ----
    /// <summary>Whose machine this is, as the console's list says it — the identity is pinned under it too.</summary>
    private readonly string _owner = "";
    /// <summary>What this session actually is: sealed, sealed but unproved, or not — from what arrived.</summary>
    private Pill _e2ePill = null!;

    private void UpdateE2eBadge(SessionLink link)
    {
        if (link != _link || _e2ePill == null) return;
        var (text, bg, fg, tip) = link.Status switch
        {
            SessionLink.E2eStatus.Encrypted => ("🔒 End-to-end encrypted", _t.OkBg, _t.OkText,
                $"Only this console and {_session.MachineName} can read this session — not our server. {_session.MachineName}'s identity is the one this console knows."),
            SessionLink.E2eStatus.EncryptedUnchecked => ("🔒 Encrypted — identity not checked", _t.WarnBg, _t.WarnText,
                "Sealed so our server cannot read it, but nothing proved which computer sealed it: the support app there is running as the person (an older install), or it is shared from a browser."),
            SessionLink.E2eStatus.NotEncrypted => ("Not end-to-end encrypted", _t.WarnBg, _t.WarnText,
                "Still encrypted on its way (TLS), but readable on our server: the support app on this machine, or a console in this session, needs updating."),
            SessionLink.E2eStatus.Held => ("Identity changed — waiting for you", _t.BadBg, _t.BadText,
                $"{_session.MachineName} is not the computer this console knew. Nothing is shown or sent until you answer."),
            _ => ("", _t.NeutralBg, _t.Ink, ""),
        };
        if (text.Length == 0) { _e2ePill.Visible = false; return; }
        _e2ePill.Set(text, bg, fg);
        _e2ePill.Width = TextRenderer.MeasureText(text, _e2ePill.Font).Width + 24;   // from its font (§30)
        _toolTip.SetToolTip(_e2ePill, tip);
        _e2ePill.Visible = true;
    }

    /// <summary>
    /// The machine's identity is not the one this console knows (Clint's answer 1: never silently). Asked in words that
    /// say what is at stake; End is the default. Yes uses the new identity and remembers it.
    /// </summary>
    private void AskIdentity(SessionLink link, MachinePins.Verdict verdict, MachinePins.Pin? previous, string newPub, Action<bool> answer)
    {
        if (link != _link) return;
        var since = previous != null ? $" (this console has known it since {previous.First.ToLocalTime():d MMMM yyyy})" : "";
        var what = verdict == MachinePins.Verdict.Unsigned
            ? $"{_session.MachineName} did not prove its identity this time{since}."
            : $"{_session.MachineName}'s identity has changed{since}.";
        var body = what + "\n\n"
            + "That is expected only if the support app on it was uninstalled and installed again. If nobody did that, another "
            + "computer — or somebody with control of our server — may be pretending to be it. Nothing from it is shown, and "
            + "nothing you type is sent, until you answer.\n\n"
            + (newPub.Length > 0 ? $"New identity: {E2eHandshake.Fingerprint(newPub)}\n" : "")
            + (previous != null ? $"Known identity: {E2eHandshake.Fingerprint(previous.IdentityPub)}\n\n" : "\n")
            + "Was it reinstalled? Yes continues; No ends the session.";
        var yes = MessageBox.Show(this, body, "Is this the same computer?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        answer(yes);
        if (!yes) EndFromIdentityRefusal();
    }

    private void EndFromIdentityRefusal()
    {
        Trace.Write("form: ending the session — the machine's identity was not accepted");
        try { _link.Send(new EndSessionMsg { SessionId = _session.SessionId }); } catch { }
        MarkDead($"Ended: {_session.MachineName}'s identity was not the one this console knew.");
    }

    private RichTextBox _chatLog = null!;
    private InputBox _chatInput = null!;
    private ListView _fileList = null!;
    private InputBox _pathBox = null!;
    private RichTextBox _scriptOut = null!;
    private TextBox _scriptBox = null!;
    private ComboBox _scriptLang = null!;
    private RichTextBox _infoList = null!;
    private Label _xferLabel = null!;
    private RoundProgress _xferBar = null!;
    private readonly System.Windows.Forms.Timer _xferIdle = new() { Interval = 20000 };

    // ---- hearing the customer's machine -------------------------------------------------
    private readonly AudioPlayer _audio = new();
    /// <summary>Listening is a line on the Tools menu now, not a button on the toolbar
    /// (stage 3.5.4). Clint, 2026-09-18: "listen should be added to the tools bar so that all
    /// tabs sit at the top instead of anything being on the side." It was the one control that
    /// lived in two places at once, and one of the two has gone.</summary>
    private ToolStripMenuItem _listenItem = null!;
    private RoundButton _soundListenBtn = null!;
    private LevelMeter _meter = null!, _soundMeter = null!;
    private RichTextBox _soundReport = null!;
    /// <summary>Whether the Sound tab has already asked once. It asks on first open so the tab is
    /// never an empty box, but not again on every visit — that would be a request per click.</summary>
    private bool _soundAsked;
    /// <summary>One listing per session when the Files tab is first shown, not one per click.</summary>
    private bool _filesAsked;
    /// <summary>
    /// True while the answer to that first automatic listing is outstanding. It is the difference
    /// between "the technician asked for this and deserves the error" and "we asked on their
    /// behalf and can quietly fall back to the drives", and without it an old host, a roaming
    /// profile or a machine at the logon screen would open the tab on a red error line.
    /// </summary>
    private bool _filesOpeningOnDesktop;
    /// <summary>Guards the nested message pump in BeginDragOut against ItemDrag re-entering it.</summary>
    private bool _dragOutBusy;
    /// <summary>
    /// Dragging off the picture (3.5.2): the question asked of their machine when the drag began,
    /// and its answer once it comes. Asked at the START so it is usually back before the pointer
    /// reaches the edge of the window.
    /// </summary>
    private string _selectionAsked = "";
    private SelectionMsg? _selection;
    /// <summary>Positions on the Tools menu. Named rather than written as bare numbers, because
    /// the numbers would drift the moment a tool is inserted and nothing would say so.</summary>
    private const int ChatToolIndex = 0;
    private const int FilesToolIndex = 1;
    private const int ScriptsToolIndex = 2;
    // Was 4. The Clipboard tab that sat between Scripts and Sound is gone (stage 3.5.1): copy and
    // paste is a gesture now, not a place to go.
    private const int SoundToolIndex = 3;
    private const int InfoToolIndex = 4;
    private Label _audioLabel = null!;
    /// <summary>Redraws the level meter. 20 a second is smooth to the eye and free; the audio
    /// itself is never touched from the UI thread.</summary>
    private readonly System.Windows.Forms.Timer _meterTick = new() { Interval = 50 };
    private bool _listening;
    private int _audioFrames;

    // Whole-tree transfers in flight, by set id.
    private readonly Dictionary<string, (FileSetPlan plan, bool setTheirClipboard)> _outgoingSets = new();
    private readonly Dictionary<string, FileSetReceiver> _incomingSets = new();
    /// <summary>Set id whose completion should put the landed paths on the CUSTOMER's clipboard.</summary>
    /// <summary>
    /// The transfers that are pastes rather than plain sends, by SetId. Recorded when the OFFER
    /// goes out, and read when the customer's machine confirms.
    ///
    /// This used to be a single field set from the send task's continuation, once the last chunk
    /// had gone. That is a race, and on 2026-09-18 it was THE bug: a 673-byte file reached the
    /// customer's machine, was written, and came back confirmed before the console's own
    /// "remember this was a paste" callback had run — so the handler found an empty marker,
    /// decided it had been an ordinary send, and never told the host to point the clipboard at
    /// the files. The host's trace shows it exactly: three transfers followed by
    /// "clipboard: put 1 file(s) on the customer's clipboard", and a fourth, smaller one followed
    /// by nothing at all.
    ///
    /// The smaller the file the more reliably it failed, which is the wrong way round for
    /// anything a person would guess at, and is why it read as "copy and paste just does not
    /// work" while the 27 KB screenshots went through.
    ///
    /// Recorded at offer time it cannot race: the answer can only arrive after the offer.
    /// </summary>
    private readonly HashSet<string> _pasteSets = new();

    /// <summary>
    /// What a pull we asked for is FOR, kept per set id rather than in one field.
    ///
    /// It used to be a single bool that nothing ever read, so every tree that arrived went onto
    /// the clipboard whatever it had been fetched for, and two overlapping pulls shared one
    /// answer. A drag needs the opposite of that: its bytes go to a staging folder of its own and
    /// must NOT touch the clipboard, because the technician is in the middle of a gesture and
    /// clobbering what they had copied is a side effect nobody asked for.
    /// </summary>
    private sealed class PullIntent
    {
        /// <summary>Where it must land. Null means the usual dated folder under Downloads.</summary>
        public string? Into;
        public bool OnMyClipboard = true;
        public TaskCompletionSource<FileSetDoneMsg>? Finished;
    }
    private readonly Dictionary<string, PullIntent> _pullIntents = new();

    private readonly ToolTip _toolTip = new();
    /// <summary>One tool: its name on the menu, the page it has always been, and the window that
    /// page now lives in.</summary>
    private sealed class Tool
    {
        public required string Name;
        public required Panel Page;
        public required ToolWindow Win;
        /// <summary>Whether it has been opened once, and therefore whether it has a position of
        /// its own worth keeping. Placed on first open only: moving a window back to where we
        /// would have put it, after somebody has dragged it somewhere, is worse than never
        /// placing it at all.</summary>
        public bool EverShown;
    }

    /// <summary>
    /// The tools, in the order they appear on the Tools menu.
    ///
    /// Stage 3.5.4, and the correction is the whole of it. These were five buttons in a strip
    /// down the right-hand side; then one button and a menu — still down the right-hand side.
    /// Clint, 2026-09-18: "the whole idea is to get rid of the side bar on the right."
    ///
    /// The measurement behind that: the panel cost the picture 400 px of width whether or not
    /// anybody was using it, in every viewing mode, and pop-out and full screen inherited the
    /// cost — so the two modes that exist to give the screen more room gave it less. A window
    /// costs nothing until it is opened, and can be put on a second monitor.
    ///
    /// Every page is built at construction and parented into its window straight away, opened or
    /// not. That is deliberate: the chat log, the script output and the transfer history are
    /// written to by messages arriving off the socket at any moment, and a page that only came
    /// into being when somebody opened it would have lost everything said before they did.
    /// </summary>
    private readonly List<Tool> _tools = new();
    private RoundButton _toolsBtn = null!;
    private ContextMenuStrip _toolsMenu = null!;
    private List<MonitorInfo> _monitors = new();
    private readonly string _downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private readonly Dictionary<string, FileStream> _incoming = new();
    private readonly Dictionary<string, string> _paths = new();
    private string _currentRunId = "";

    /// <summary>Matches ClipboardAccess.MaxChars on the host. Past this the clipboard is not
    /// something anyone meant to move by hand, and shipping it would stall the session.</summary>
    private const int ClipboardLimit = 1024 * 1024;

    // ---- the clipboard mirrors itself, both ways (stage 3.5.1) --------------------------------
    // NEXT-SESSION-IO decided clipboard movement should be button-driven only, because automatic
    // sync surprises people and can leak whatever was copied. Narrowed on 2026-09-16 so that the
    // technician's clipboard followed them onto the customer's machine, and narrowed again on
    // 2026-09-18 so that the customer's comes back: "copy here, paste there" is what everyone
    // arriving from TeamViewer or AnyDesk expects, and a tab to do it in read as a fault rather
    // than as a decision.
    //
    // What keeps §8's original concern answered is NOT that one direction is manual any more. It
    // is WHEN each direction is allowed to move, and both halves are narrow:
    //
    //   THEIRS REACHES US ONLY WHILE WE ARE WATCHING. The host mirrors only while a viewer is
    //   subscribed to the screen (HostEngine.OnCustomerClipboardChanged). The password a customer
    //   copied at nine this morning never crosses; the licence key they copy while being helped
    //   does. Nobody looking, nothing leaves their machine.
    //
    //   OURS REACHES THEM ONLY WHILE THIS WINDOW IS IN FRONT. Unchanged, and still the moment
    //   before a paste. Copying another customer's password while this window sits in the
    //   background does not ship it anywhere.
    //
    //   EVERY MOVEMENT IS ON THE TICKET, as a count of characters and never the characters. The
    //   host keeps that ledger, because it is the end that knows what actually crossed.
    private string _lastSentClip = "";
    /// <summary>The last text the CUSTOMER sent us. Never sent back: their clipboard is put onto
    /// ours, and without this the next focus would bounce it straight back to them.</summary>
    private string _lastReceivedClip = "";
    /// <summary>The selection last sent to the customer, joined into one string. The same files
    /// stay on the clipboard after a copy, so without this every alt-tab would resend them.</summary>
    private string _lastSentFiles = "";

    /// <summary>
    /// The clipboard's sequence number when this console last acted on it.
    ///
    /// THE thing that tells "the clipboard still holds what it held" apart from "they copied the
    /// same thing again", and comparing paths cannot do it. Explorer copying the same file twice
    /// produces the identical path list, so a guard built on paths refuses the second copy — and
    /// every copy after it, for the rest of the session. Clint's trace, 2026-09-18:
    ///
    ///   clip: the same 1 item(s) are still on the clipboard and were already sent
    ///   fileset: already sent to DESKTOP-OPHQIDH — copy something else, or copy these again
    ///
    /// The advice in that message was impossible to follow: copying them again produced the same
    /// paths and hit the same guard. Testing with one file, which is what anybody does, meant copy
    /// and paste worked exactly once and then never again.
    ///
    /// Windows bumps this counter on every clipboard change, whether or not the content differs,
    /// which is precisely the question being asked. The host has used it to watch the customer's
    /// clipboard since this morning; this is the same fact from the other end.
    /// </summary>
    private uint _lastHandledClipSeq;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    /// <summary>A copy too large to move on its own, waiting for a click. One or the other is set,
    /// never both: whichever offer came last is the one the status bar is showing.</summary>
    private List<string>? _pendingSend;
    private List<string>? _pendingFetch;
    /// <summary>The selection currently being walked on a background thread, so a slow one is not
    /// started again by the next activation or the next clipboard change.</summary>
    private string _measuringFiles = "";

    /// <summary>
    /// Lets the same selection be sent again, after a send that did not work.
    ///
    /// <see cref="_lastSentFiles"/> stops a copy being re-sent on every alt-tab, which it must.
    /// But it was set BEFORE the transfer was known to have worked, and Explorer leaves the same
    /// files on the clipboard afterwards — so a failed send poisoned that selection for the rest
    /// of the session. Copying the very same file again, which is exactly what anybody does when
    /// something did not arrive, then did nothing at all and said nothing about it.
    /// </summary>
    /// <summary>
    /// Marks a set of paths as "already sent", for files that arrived FROM the customer and are
    /// being put on this machine's clipboard.
    ///
    /// Without this the mirror bounces. Fetching files off the customer's machine ends by putting
    /// them on the technician's clipboard, so Ctrl+V works here — and that is a clipboard change,
    /// which brings <see cref="SyncMyClipboard"/> straight back round to push those same files to
    /// the customer. They would go back to the machine they had just come from, as a second full
    /// transfer, for nobody.
    ///
    /// Text has had this guard since the beginning (<c>_lastReceivedClip</c>). Files never did,
    /// because until 3.5.1 they only moved when a button was pressed and nothing watched them.
    /// </summary>
    private void RememberAsAlreadySent(IEnumerable<string> paths)
    {
        _lastSentFiles = string.Join("|", paths);
        // Recorded AFTER the caller has written the clipboard, so the write we are about to make
        // is the state being remembered. A later real copy bumps this and goes through.
        try { _lastHandledClipSeq = GetClipboardSequenceNumber(); } catch { }
        Trace.Write("clip: these came FROM the customer — not sending them back");
    }

    /// <summary>
    /// Empties this machine's clipboard because the customer has just copied files, and those are
    /// now what a paste should mean.
    ///
    /// Files cannot be put here until they have been fetched, and a set over the threshold is not
    /// fetched until it is clicked. For all of that time the technician's OWN last copy used to sit
    /// here untouched, so Ctrl+V pasted it — silently, and looking exactly like the customer's copy
    /// had arrived wrong. tools/pullcheck never saw it, because it emptied the clipboard itself
    /// before every step.
    ///
    /// The sequence number is taken after the clear so the change we just made is not read back as
    /// something the technician copied.
    /// </summary>
    private void ForgetMyClipboardForTheirs(int items)
    {
        try
        {
            using (UiWatchdog.Work("writing the clipboard: Clear")) Clipboard.Clear();
            try { _lastHandledClipSeq = GetClipboardSequenceNumber(); } catch { }
            Trace.Write($"clip: they copied {items} item(s); emptied this clipboard so a paste cannot give the old files");
        }
        catch (Exception ex)
        {
            // Another program holding the clipboard open. The fetch still goes ahead and replaces
            // it when it lands; only the gap before that is unguarded, and the trace says so.
            Trace.Write($"clip: they copied {items} item(s) but this clipboard could not be emptied ({ex.GetType().Name}) — until the fetch lands, a paste here gives the old contents");
        }
    }

    private void ForgetLastSentFiles(string reason)
    {
        if (_lastSentFiles.Length == 0) return;
        _lastSentFiles = "";
        Trace.Write($"clip: forgetting what was last sent so it can be tried again — {reason}");
    }
    private bool _clipListening;
    private bool _layingOutToolbar;

    // WM_CLIPBOARDUPDATE rather than a polling timer: a timer either misses a copy made a moment
    // before the window is clicked, or runs often enough to fight every other clipboard user on
    // the machine. This is the notification Windows already sends.
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    public SessionForm(string wsBase, string token, SessionInfo session, Theme theme, Branding brand, bool free = false, string owner = "")
    {
        _session = session; _t = theme; _wsBase = wsBase; _token = token; _owner = owner ?? "";
        Trace.Write($"form: opening session {session.SessionId} for {session.MachineName} (unattended={session.Unattended})");
        BuildUi();
        if (free) KeepOnlyFreeTools();
        Trace.Write($"form: UI built, HandleCreated={IsHandleCreated}");
        _xferIdle.Tick += (_, _) => { _xferIdle.Stop(); _xferLabel.Visible = false; _xferBar.Visible = false; };
        // Clear out what earlier drags staged in temp. At session start, never during one: a drop
        // that has not finished copying is still reading from its folder, and Explorer does that
        // on its own schedule rather than ours.
        DragOut.SweepOlderThan(TimeSpan.FromHours(12));
        _rebootTick.Tick += (_, _) => UpdateRebootBanner();
        UiWatchdog.Start();
        ApplyViewOnly();
        ApplyVisiting();
        _link = StartLink(session.SessionId);
    }

    /// <summary>
    /// Another company's customer's computer, reached with a code they gave (3.9): screen and control,
    /// and nothing else — Clint's rule, enforced by the server whatever this window shows. So the tools
    /// that would be refused are not offered at all; a button that does nothing reads as a fault.
    /// </summary>
    private void ApplyVisiting()
    {
        if (!_session.Visiting) return;
        _sysBtn.Visible = false;
        foreach (ToolStripItem it in _toolsMenu.Items)
            if (it.Text != "Info" && !(it.Text == "Files" && _session.VisitorFiles)) it.Visible = false;
        // Files only if the person at the machine ticked it when they allowed you (3.9) — the server decides either way.
        ShowBanner(_session.VisitorFiles
            ? "You are on another company's customer's computer: the screen, the mouse and keyboard, and Files — they allowed copying files. Their IT company can watch and end it."
            : "You are on another company's customer's computer: you can see the screen and use the mouse and keyboard. Their IT company can watch and end it.",
            reconnect: false, close: true);
        Trace.Write("form: visiting — another company's machine, screen and control only");
    }

    /// <summary>
    /// The customer is sharing their screen from a web browser (3.8.2, 2026-09-30). No browser lets
    /// anybody move the pointer or type on the machine, so nothing that needs the support app is
    /// offered: no control, no System menu, no Files, Scripts, Sound or Listen, and the clipboard is
    /// not sent. What is left works — the picture, Chat, Info, recording — and a banner says why the
    /// rest is missing, so a technician does not go looking for a fault.
    /// </summary>
    private void ApplyViewOnly()
    {
        if (!_session.ViewOnly) return;
        _view.ControlEnabled = false;
        _controlChk.Checked = false;
        _controlChk.Visible = false;
        _sysBtn.Visible = false;
        foreach (ToolStripItem it in _toolsMenu.Items)
            if (it.Text is "Files" or "Scripts" or "Sound" || it == _listenItem) it.Visible = false;
        // Short enough for the window at its default width; the name is in the title bar already.
        ShowBanner("View only — shared from a web browser. You can see and chat; to take control they need the support app.",
            reconnect: false, close: true);
        Trace.Write("form: view only — the host is a web browser");
    }

    /// <summary>
    /// Builds a viewer link for a session id and starts it, with every handler attached BEFORE
    /// the pump runs. That ordering is not a style choice: the server replays its cached
    /// MonitorsMsg within milliseconds of a viewer socket opening, MonitorsMsg is the only thing
    /// that triggers the console's first SubscribeMsg, and a handler attached late misses it for
    /// good — leaving a reconnected viewer on a black screen that looks exactly like a reconnect
    /// that failed. Handlers go on SessionLink, which survives its own retries; this method only
    /// runs again when the window is re-attached to a DIFFERENT session.
    /// </summary>
    private SessionLink StartLink(string sessionId)
    {
        var link = new SessionLink(_wsBase, sessionId, _token)
        {
            // What the machine's identity is pinned under (MachinePins): set before it runs, like every handler.
            DeviceId = _session.DeviceId, MachineName = _session.MachineName, Owner = _owner
        };
        link.OnE2eChanged += _ => { try { BeginInvoke(() => UpdateE2eBadge(link)); } catch { } };
        link.OnIdentityQuestion += (verdict, previous, pub, answer) =>
        {
            try { BeginInvoke(() => AskIdentity(link, verdict, previous, pub, answer)); }
            catch { Trace.Write("form: identity question before the window existed — the session stays held"); }
        };
        // The key is in use now: ask again for a picture this window can open (what was asked before was sent clear,
        // and a host that seals refuses clear content).
        link.OnKeyed += () => { try { BeginInvoke(() => { if (link == _link) { Subscribe(_wantMonitor >= 0 ? _wantMonitor : _view.MonitorIndex); _link.Send(new QualityMsg { Jpeg = _quality.Value, Fps = 12 }); } }); } catch { } };
        link.OnMessage += m =>
        {
            // BeginInvoke throws if the window handle does not exist yet. A message lost here is
            // lost for good — trace it rather than swallowing it.
            try { BeginInvoke(() => HandleMsg(m)); }
            catch (Exception ex) { Trace.Write($"form: LOST inbound {m.GetType().Name} before the UI thread — {ex.GetType().Name} (HandleCreated={IsHandleCreated})"); }
        };
        link.OnBinary += b =>
        {
            try { BeginInvoke(() => HandleBinary(b)); }
            catch (Exception ex) { Trace.Write($"form: LOST inbound binary frame ({b.Length} B) before the UI thread — {ex.GetType().Name} (HandleCreated={IsHandleCreated})"); }
        };
        link.OnConnectedChanged += c => { try { BeginInvoke(() => OnLinkConnectedChanged(link, c)); } catch { } };
        link.OnReconnected += () => { try { BeginInvoke(() => ResumeAfterReconnect(link)); } catch { } };
        link.OnGaveUp += (ended, why) => { try { BeginInvoke(() => OnLinkGaveUp(link, ended, why)); } catch { } };
        _ = link.RunAsync(_linkCts.Token);
        return link;
    }

    private void BuildUi()
    {
        Text = $"Session — {_session.MachineName}";
        Size = new Size(1320, 850);
        StartPosition = FormStartPosition.CenterScreen;
        Font = _t.Body;
        BackColor = _t.Mist;
        Shown += (_, _) => NativeDark.Apply(this, _t);   // dark title bar, scrollbars and list header — see NativeDark
        KeyPreview = false;

        // ---- toolbar ----
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 86, BackColor = Color.Transparent, Padding = new Padding(14, 11, 14, 11) };
        var toolCard = new Card(_t, 8) { Dock = DockStyle.Fill };
        toolbar.Controls.Add(toolCard);

        const int RowY = 26, RowH = 34, LblY = 7;
        _monitorBox = new ThemedCombo(_t) { Width = 186, Font = _t.Body, FlatStyle = FlatStyle.Flat, Location = new Point(14, RowY + 3) };
        _monitorBox.SelectedIndexChanged += (_, _) => SwitchMonitor();
        var monLbl = new Label { Text = "Monitor", Font = _t.Small, ForeColor = _t.Muted, AutoSize = true, Location = new Point(15, LblY), BackColor = Color.Transparent };

        _controlChk = new RoundCheck(_t) { Text = "Control", AutoSize = true, Checked = true, Location = new Point(216, RowY + 6), Font = _t.BodyBold, ForeColor = _t.Ink };
        _controlChk.Top = RowY + (RowH - _controlChk.PreferredSize.Height) / 2;   // centred on the buttons, measured
        _controlChk.CheckedChanged += (_, _) => _view.ControlEnabled = _controlChk.Checked;

        // 330, not 302. The Control checkbox is AutoSize and measures 108 px, so it ran to 324 and
        // the slider's left 22 px sat underneath it — measured by tools/toolbarprobe. Small, and
        // it has always been there, but a click in that strip goes to whichever happens to be on
        // top, which is exactly the kind of thing reported as "the Control tick box is sticky".
        _quality = new RoundSlider(_t) { Minimum = 25, Maximum = 90, Value = 55, Width = 140, Height = RowH, Location = new Point(330, RowY) };
        _quality.ValueChanged += (_, _) => _link.Send(new QualityMsg { Jpeg = _quality.Value, Fps = 12 });
        var qLbl = new Label { Text = "Quality", Font = _t.Small, ForeColor = _t.Muted, AutoSize = true, Location = new Point(334, LblY), BackColor = Color.Transparent };

        // One menu rather than three more buttons. There is no room on this bar for three — the
        // Listen button already proved that at the default 1320 px width, and a button that
        // half-covers another is exactly what viewprobe's screenshot exists to catch. It also
        // groups them honestly: these are the three things you do TO a machine rather than with it.
        var sysBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "System  ▾", Location = new Point(486, RowY), Size = new Size(116, RowH) };
        _toolTip.SetToolTip(sysBtn, "Ctrl+Alt+Del, lock, privacy screen, or restart that machine");
        _sysMenu = new ContextMenuStrip { Font = _t.Body }.Themed(_t);
        _sysMenu.Items.Add("Ctrl+Alt+Del", null, (_, _) => SendSysAction("ctrl_alt_del"));
        // Opens on THEIR screen, not here — it is their Task Manager, driven through the remote
        // view like anything else. Worth having on the menu because getting to it otherwise means
        // Ctrl+Shift+Esc, and a technician pressing that has just opened their OWN task manager.
        _sysMenu.Items.Add("Open Task Manager", null, (_, _) => SendSysAction("task_manager"));
        _sysMenu.Items.Add("Lock the machine", null, (_, _) => SendSysAction("lock"));
        // The privacy screen (1.7). Its tick follows what the HOST says the screen is doing, not
        // what was clicked: believing their screen is private when it is not is the one failure
        // this must not have.
        _privacyItem = new ToolStripMenuItem("Privacy screen — black out their screen") { Font = _t.Body };
        _privacyItem.Click += (_, _) =>
        {
            Trace.Write($"form: privacy screen {(_privacyOn ? "off" : "on")} requested");
            SetTransferStatus(_privacyOn ? "turning the privacy screen off…" : $"blacking out {_session.MachineName}'s screen…");
            _link.Send(new PrivacyScreenMsg { On = !_privacyOn });
        };
        _sysMenu.Items.Add(_privacyItem);
        _sysMenu.Items.Add(new ToolStripSeparator());
        _sysMenu.Items.Add("Restart the machine…", null, (_, _) => RebootClicked());
        sysBtn.Click += (_, _) => _sysMenu.Show(sysBtn, new Point(0, sysBtn.Height));
        _sysBtn = sysBtn;

        // ---- Tools, next to System, and nothing on the right-hand side of the window ---------
        // Stage 3.5.4. The five tabs are five lines on this menu, and each one opens its own
        // movable window. Beside System rather than anywhere else because the pairing is the
        // honest one: System is what you do TO the machine, Tools is what you do WITH it, and
        // both are menus for the same reason — there is no room on this bar for eight buttons,
        // which the Listen button proved twice.
        _toolsBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Tools  ▾", Location = new Point(610, RowY), Size = new Size(104, RowH) };
        _toolTip.SetToolTip(_toolsBtn, "Chat, Files, Scripts, Sound and Info — each opens in its own window");
        _toolsMenu = new ContextMenuStrip { Font = _t.Body }.Themed(_t);
        _toolsBtn.Click += (_, _) => { try { _toolsMenu.Show(_toolsBtn, new Point(0, _toolsBtn.Height)); } catch { } };

        var refreshBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Refresh", Location = new Point(722, RowY), Size = new Size(96, RowH) };
        refreshBtn.Click += (_, _) => { Trace.Write("form: state before Refresh — " + _view.DiagnosticSummary()); Trace.Write($"form: Refresh clicked; re-subscribing to monitor {_view.MonitorIndex}"); Subscribe(_view.MonitorIndex); };

        // The connection pill lives on the STATUS BAR, not the toolbar. Adding the Listen button
        // proved there is no room for a fifth right-hand button at the default window size: the
        // pill was half covered, and viewprobe's screenshot is the only reason that was noticed
        // rather than shipped. Connection state is status, the status bar is where status goes,
        // and it has room to spare.
        _connPill = new Pill(_t, "○ connecting", _t.WarnBg, _t.WarnText) { Size = new Size(140, 22), Dock = DockStyle.Left };

        _fullBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Full screen", Size = new Size(128, RowH) };
        _toolTip.SetToolTip(_fullBtn, "Fill the screen with the remote desktop (F11)");
        _fullBtn.Click += (_, _) => ToggleFullScreen();
        _popBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Pop out", Size = new Size(96, RowH) };
        _popBtn.Click += (_, _) => TogglePopOut();
        _toolTip.SetToolTip(_popBtn, "Move the remote desktop into its own window, e.g. onto a second monitor");

        // Listening used to be a button here, put on the toolbar because "a tab you have to go
        // and find is a tab nobody switches to". It is on the Tools menu now, which is where
        // Clint asked for it and is also the only place it can be: the toolbar had no room for
        // an eighth control at any width this window supports, and the menu has room for ten.
        // The level meter stays on the status bar, so whether anything is arriving is still
        // answered without opening anything.

        // The clipboard sync toggle wanted to live here, beside Listen, and does not: the
        // toolbar has no room for an eighth control at any width this window supports.
        // tools/toolbarprobe measured every attempt overlapping Listen - 96 px at 1280, 48, then
        // 8 - and the layout re-enters itself often enough that deriving a safe width from the
        // cursor, from Listen's position, or from toolCard.Width all produced a button that
        // still ate Listen's clicks. It lives in the Clipboard tab instead, with the rest of the
        // clipboard controls, which is where somebody looking for it would go anyway.

        var endBtn = new RoundButton(_t, BtnStyle.Danger) { Text = "Leave / End", Size = new Size(134, RowH), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        // Deliberate. This is the one thing that must still end the session AT ONCE, now that a
        // dropped socket no longer does — see OnFormClosed, which sends the EndSessionMsg.
        endBtn.Click += (_, _) => { _leaving = true; Close(); };
        // Lay the right-hand buttons out from the right edge so nothing is clipped on a narrow
        // window. The floor is the right edge of Refresh plus a gap, NOT an arbitrary 500: a
        // floor below that lets these buttons slide on top of the left-hand ones, which is what
        // tools/toolbarprobe measured at 1100 px — Refresh and Listen overlapping by 74 px, each
        // eating the other's clicks. MinimumSize below is what stops it being reached at all;
        // this is the belt to that pair of braces.
        const int LeftGroupRight = 722 + 96 + 12;
        // A method, not just a Resize handler: the buttons also have to be re-laid when one of
        // them CHANGES WIDTH, which "Full screen" does when it becomes "Exit full screen". A
        // width change raises no Resize on the card, so relying on the event alone left the wider
        // button growing rightwards into "Pop out".
        _layoutToolbarRight = () =>
        {
            // Guarded like LayoutTabs, and for the same reason: changing a button's width or
            // visibility raises a Resize on the card, which calls straight back in here.
            if (_layingOutToolbar) return;
            _layingOutToolbar = true;
            try
            {
            // Clip sync is the last button on and the first one off. Everything else here is
            // older and load-bearing; a toolbar too narrow for eight controls should drop the
            // newest rather than pile it on Listen, and the same toggle sits in the Clipboard
            // tab so nothing becomes unreachable when it goes.
            //
            // Whether it fits is decided from the TOTAL width, before anything is placed. Two
            // earlier versions asked the running cursor instead, and the cursor lies: every
            // button is clamped to LeftGroupRight, so on a narrow window it walks past the floor
            // while the buttons themselves stack on it. It reported room that was not there, and
            // tools/toolbarprobe measured the result as 96, then 48, then 8 px of overlap.
            int x = toolCard.Width - 14;
            x -= endBtn.Width; endBtn.Location = new Point(Math.Max(LeftGroupRight, x), RowY);
            x -= _popBtn.Width + 8; _popBtn.Location = new Point(Math.Max(LeftGroupRight, x), RowY);
            x -= _fullBtn.Width + 8; _fullBtn.Location = new Point(Math.Max(LeftGroupRight, x), RowY);
            }
            finally { _layingOutToolbar = false; }
        };
        toolCard.Resize += (_, _) => _layoutToolbarRight();
        // Below this the toolbar cannot lay itself out, and what it does instead is pile buttons
        // on top of each other — silently, because an obscured button looks exactly like one that
        // is simply not being pressed. Seven buttons, a monitor list and a slider need the room;
        // the window has always defaulted to 1320 and nothing is lost by refusing to go under it
        // by much.
        MinimumSize = new Size(1280, 720);

        toolCard.Controls.AddRange(new Control[] { monLbl, _monitorBox, _controlChk, qLbl, _quality, sysBtn, _toolsBtn, refreshBtn, _fullBtn, _popBtn, endBtn });
        // The labels are placed in pixels and their font grows with the scaling, so at 150% "Monitor"
        // reaches down over the top of its dropdown — first in the z-order, it painted the card over
        // the dropdown's top edge (lookprobe's picture, 2026-10-06). The fields go in front.
        _monitorBox.BringToFront(); _quality.BringToFront();

        // ---- screen ----
        // 14 on the right, not 7. The 7 was half a gutter shared with the side panel that used to
        // sit beside it; with the panel gone the picture is the only thing on this row and takes
        // the same margin on both sides.
        var screenHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(14, 0, 14, 0) };
        var screenCard = new Card(_t, 8) { Dock = DockStyle.Fill };
        _view = new ScreenView { Dock = DockStyle.Fill };
        _view.OnInput += m => _link.Send(m);
        _view.OnToggleFullScreen += ToggleFullScreen;
        // The console is the only end that knows whether it can decode what it asked for, and it
        // only finds out when the first frame arrives. Ask again without H.264 rather than sitting
        // in front of a black window while a perfectly good stream arrives.
        _view.OnVideoUnavailable += why => BeginInvoke(() =>
        {
            if (!_canDecodeH264) return;
            _canDecodeH264 = false;
            // Not shown as an alert. Re-subscribing makes the host report "this console did not
            // offer H.264" in the Info tab, which is where a technician looks for this and is one
            // account of the fact rather than two that can disagree.
            Trace.Write($"form: H.264 cannot be decoded here ({why}); re-subscribing for JPEG tiles");
            Subscribe(_view.MonitorIndex);
        });

        // Drag files and folders onto the remote desktop to send them, and text onto their
        // clipboard. Dragging OUT, off the picture, is below (3.5.2).
        _view.AllowDrop = true;
        _view.DragEnter += (_, e) =>
        {
            // Traced because a drop that never arrives and a drop that arrives and fails look
            // identical from the technician's chair — nothing happens either way.
            var formats = e.Data?.GetFormats() is { } f ? string.Join(",", f.Take(6)) : "(none)";
            Trace.Write($"drag: DragEnter, formats=[{formats}]");
            // Our own drag OFF the picture passes back over it on its way out; accepting it here
            // would send their files straight back to their desktop.
            if (_dragOutBusy) { e.Effect = DragDropEffects.None; return; }
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                // Copy, not Move: Move would invite Explorer to delete the technician's originals
                // once the drop "succeeded", and nothing here is a real file system move.
                e.Effect = DragDropEffects.Copy;
                SetTransferStatus($"drop to send to {_session.MachineName}'s desktop");
            }
            else if (DroppedText(e.Data) != null)
            {
                e.Effect = DragDropEffects.Copy;
                SetTransferStatus($"drop to put this text on {_session.MachineName}'s clipboard");
            }
            else e.Effect = DragDropEffects.None;
        };
        _view.DragOver += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true || DroppedText(e.Data) != null) e.Effect = DragDropEffects.Copy;
        };
        _view.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] dropped || dropped.Length == 0)
            {
                // Text has nowhere to LAND on a picture - placing it needs a drop target over
                // there, driven by a synthesised mouse we do not control. So it goes onto their
                // clipboard, and one Ctrl+V (already forwarded) puts it where the cursor is: the
                // honest answer the roadmap chose for this one cell of 3.5.0.
                if (DroppedText(e.Data) is string text)
                {
                    _link.Send(new ClipboardMsg { Text = text });
                    Trace.Write($"drag: text dropped, {text.Length} characters, sent to their clipboard");
                    SetTransferStatus($"on {_session.MachineName}'s clipboard — Ctrl+V to place it");
                    return;
                }
                Trace.Write("drag: DragDrop with no FileDrop data");
                SetTransferStatus("that drop carried nothing this can send");
                return;
            }
            Trace.Write($"drag: DragDrop, {dropped.Length} item(s): {string.Join(" | ", dropped.Take(5))}");
            // Dropped onto the picture of their desktop, so that is where it goes. Landing it in
            // Downloads would be defensible and completely unexpected.
            PushFileSet(dropped, thenPutOnTheirClipboard: false, RemotePaths.Desktop);
        };

        // Dragging OFF the picture (3.5.2). The drag on their screen is theirs until it leaves this
        // window; then, if the person there has files selected in the folder window in front, it
        // becomes a drag of those files onto this machine. See PictureDragLeft.
        _view.OnRemoteDragStarted += () =>
        {
            _selection = null;
            _selectionAsked = Guid.NewGuid().ToString("N");
            _link.Send(new SelectionRequestMsg { RequestId = _selectionAsked });
        };
        _view.OnDragOffPicture = PictureDragLeft;
        // The connection banner. It sits across the top of the picture rather than in a tab or a
        // dialog, because a silent recovery is indistinguishable from nothing happening — the
        // lesson drag and drop taught, and reconnect has exactly that shape. Added AFTER the
        // view: docking is resolved from the highest child index down, so the Fill control has
        // to be the one added first or the banner takes the whole card.
        // One line, and AutoEllipsis so overflow says so. A docked Label does not wrap here — the
        // first screenshot of this banner read "…and did not come", cut mid-word with nothing to
        // admit it had been cut, which is the worst way for a status message to fail. The
        // messages are written to fit a line instead; the "…" is the backstop, not the plan.
        _banner = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = _t.WarnBg, Visible = false, Padding = new Padding(12, 6, 12, 6) };
        _bannerText = new Label { Dock = DockStyle.Fill, ForeColor = _t.WarnText, Font = _t.Body, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true };
        _bannerBtn = new RoundButton(_t, BtnStyle.Accent) { Text = "Reconnect", Dock = DockStyle.Right, Width = 116, Visible = false };
        _bannerBtn.Click += (_, _) => ReconnectClicked();
        // "Close", not "Close window": the longer label does not fit the button and renders as
        // "Close win…", which looks like a bug in the button rather than a choice about width.
        _bannerClose = new RoundButton(_t, BtnStyle.Ghost) { Text = "Close", Dock = DockStyle.Right, Width = 84, Visible = false };
        _bannerClose.Click += (_, _) => { _leaving = true; Close(); };
        // For a NOTICE rather than a state (2026-10-02): something worth knowing that nothing can
        // be done about from here. "Close" closes the window, so a notice needs its own button.
        _bannerOk = new RoundButton(_t, BtnStyle.Ghost) { Text = "OK", Dock = DockStyle.Right, Width = 64, Visible = false };
        _bannerOk.Click += (_, _) => HideBanner();
        _banner.Controls.Add(_bannerText);
        _banner.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        _banner.Controls.Add(_bannerOk);
        _banner.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        _banner.Controls.Add(_bannerBtn);
        _banner.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        _banner.Controls.Add(_bannerClose);

        screenCard.Controls.Add(_view);
        screenCard.Controls.Add(_banner);
        screenHost.Controls.Add(screenCard);
        _screenHost = screenHost; _screenCard = screenCard;

        // ---- the tools: five windows, one menu, no side panel (stage 3.5.4) ----
        // The order here IS the order on the menu and the meaning of the *ToolIndex constants;
        // inserting one in the middle renumbers the rest, which is why they are named.
        AddTool("Chat", BuildChatPage(), new Size(430, 520));
        AddTool("Files", BuildFilesPage(), new Size(580, 620));
        AddTool("Scripts", BuildScriptsPage(), new Size(660, 560));
        // No Clipboard tool. Copy and paste is a gesture on both machines now (stage 3.5.1), and
        // a tab for it was the feature admitting it did not work.
        AddTool("Sound", BuildSoundPage(), new Size(500, 540));
        AddTool("Info", BuildInfoPage(), new Size(540, 600));
        // The knowledge base (3.8.5): with the tools because it opens something — in the browser,
        // where articles live. It names the client and the count once the server has answered.
        _kbItem = new ToolStripMenuItem("Knowledge base") { Font = _t.Body, ToolTipText = "How things are done — opens in your browser" };
        _kbItem.Click += (_, _) => OpenKnowledge();
        _toolsMenu.Items.Add(_kbItem);

        // Listening is last, under a separator, because it is the one line here that does
        // something rather than opening something. Its label carries the state, the way the
        // button it replaced did.
        _toolsMenu.Items.Add(new ToolStripSeparator());
        _listenItem = new ToolStripMenuItem("Listen") { Font = _t.Body };
        _listenItem.ToolTipText = "Hear what that machine is playing";
        _listenItem.Click += (_, _) => ToggleListen();
        _toolsMenu.Items.Add(_listenItem);
        // Talking (3.3) under Listen, for the same reason: it does something rather than opening
        // something. SessionForm.Voice.cs.
        BuildTalkItem();
        // Recording (1.6) sits beside Listen for the same reason Listen is here: the toolbar has no
        // room for another control at any width this window supports. While it runs, a red marker
        // on the status bar says so the whole time and stops it with one click.
        _recordItem = new ToolStripMenuItem("Record this session") { Font = _t.Body };
        _recordItem.ToolTipText = "Record what you see; it is saved on the session's ticket when you stop";
        _recordItem.Click += (_, _) => ToggleRecording();
        _toolsMenu.Items.Add(_recordItem);
        // Remote printing (roadmap 1.8): both directions. SessionForm.Print.cs.
        BuildPrintItems();
        // Bringing somebody from outside into this session (§155), last: it lets a person in. SessionForm.OutsideHelp.cs.
        BuildOutsideHelpItem();

        // Nothing is opened for you. A session starts as the picture and nothing else, which is
        // the point of the change — an empty chat panel taking a quarter of the window was the
        // cost being paid before anybody had said a word.

        // ---- status ----
        var status = new Panel { Dock = DockStyle.Bottom, Height = 30, BackColor = Color.Transparent, Padding = new Padding(18, 4, 18, 4) };
        _partLabel = new Label { Text = "Participants: —", Dock = DockStyle.Fill, ForeColor = _t.Muted, Font = _t.Small, BackColor = Color.Transparent };

        // File transfers report here, on the status bar, not inside a tab. A transfer started by
        // dropping a folder onto the remote desktop gives no other feedback at all, and without
        // this it is impossible to tell a transfer in progress from one that never started.
        _xferLabel = new Label
        {
            Dock = DockStyle.Right, Width = 420, AutoEllipsis = true, ForeColor = _t.Ink,
            Font = _t.Small, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleRight,
            Visible = false
        };
        // An offer too large to move on its own is taken by clicking the message itself. The
        // message is the only thing on screen at that moment saying what is on offer, so it is
        // also the only place a click makes sense — a button elsewhere would have to repeat it.
        _xferLabel.Click += (_, _) => TakePendingOffer();
        _xferBar = new RoundProgress(_t) { Dock = DockStyle.Right, Width = 160, Maximum = 1000, Visible = false };

        // The sound of the customer's machine reports here too, for the same reason transfers do:
        // it is visible whatever tab is selected, and the meter is the only evidence a technician
        // with no headphones on has that anything is arriving.
        _meter = new LevelMeter(_t) { Dock = DockStyle.Right, Width = 86 };
        _audioLabel = new Label
        {
            Dock = DockStyle.Right, Width = 260, AutoEllipsis = true, ForeColor = _t.Muted,
            Font = _t.Small, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleRight,
            Visible = false
        };
        _meterTick.Tick += (_, _) => { if (!_listening) return; var lvl = _audio.Level; _meter.SetLevel(lvl); _soundMeter.SetLevel(lvl); };

        status.Controls.Add(_partLabel);
        status.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 10, BackColor = Color.Transparent });
        status.Controls.Add(_connPill);
        _recPill = new Pill(_t, "● Recording", _t.BadBg, _t.BadText) { Size = new Size(230, 22), Dock = DockStyle.Left, Visible = false, Cursor = Cursors.Hand };
        _recPill.Click += (_, _) => ToggleRecording();
        _toolTip.SetToolTip(_recPill, "Click to stop recording; it is saved on the session's ticket");
        status.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        status.Controls.Add(_recPill);
        // End-to-end encryption (2026-10-07): what this session actually is, beside the connection it describes.
        _e2ePill = new Pill(_t, "", _t.NeutralBg, _t.Ink) { Size = new Size(10, 22), Dock = DockStyle.Left, Visible = false };
        status.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        status.Controls.Add(_e2ePill);
        AddVoiceToStatus(status);
        // The knowledge base for this machine's client (3.8.5), shown only when it has something:
        // Clint's example is the next technician on that client's machine finding how it was done.
        _kbPill = new Pill(_t, "", _t.NeutralBg, _t.Ink) { Size = new Size(10, 22), Dock = DockStyle.Left, Visible = false, Cursor = Cursors.Hand };
        _kbPill.Click += (_, _) => OpenKnowledge();
        // After the connection, recording and voice pills rather than before them: docking lays the
        // LAST-added left-most, so these two go in just above the participants line (index 0, the
        // Fill), which puts them innermost — the connection pill stays the first thing on the bar.
        var kbGap = new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent };
        status.Controls.Add(kbGap);
        status.Controls.Add(_kbPill);
        status.Controls.SetChildIndex(_kbPill, 1);
        status.Controls.SetChildIndex(kbGap, 2);
        _recTick.Tick += (_, _) => { if (_recorder != null) _recPill.Text = $"● Recording {_recorder.Elapsed:mm\\:ss} — click to stop"; };
        status.Controls.Add(_xferLabel);
        status.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        status.Controls.Add(_xferBar);
        // Added last, so it docks furthest right: a transfer comes and goes, the sound stays put.
        status.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Color.Transparent });
        status.Controls.Add(_audioLabel);
        status.Controls.Add(_meter);

        var canvas = new CanvasPanel(_t) { Dock = DockStyle.Fill };
        canvas.Controls.Add(screenHost);
        canvas.Controls.Add(status);
        canvas.Controls.Add(toolbar);

        Controls.Add(canvas);
        _toolbar = toolbar; _status = status; _canvas = canvas;
        // Where the toolbar sits among the canvas's children, so a pop-in can put it back exactly
        // there. Docking is resolved in z-order, so re-adding at the end would silently re-lay the
        // whole window — the toolbar would run down the side of the picture instead of across it.
        _toolbarIndex = canvas.Controls.GetChildIndex(toolbar);
    }

    // ---------------------------------------------------------------- viewing modes
    private Panel _toolbar = null!, _status = null!, _screenHost = null!, _canvas = null!;
    /// <summary>Child index of the toolbar within the canvas, so a pop-in can put it back exactly
    /// where it was. Docking is resolved in z-order; re-adding at the end re-lays the window.</summary>
    private int _toolbarIndex;
    /// <summary>Re-lays the right-hand toolbar buttons. Called on resize AND whenever one of them
    /// changes width, which a Resize event does not cover.</summary>
    private Action? _layoutToolbarRight;
    private Card _screenCard = null!;
    private RoundButton _fullBtn = null!, _popBtn = null!;
    private Form? _popped;
    private bool _poppingIn;          // true only while we close the pop-out window ourselves
    private bool _fullScreen;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;
    private Rectangle _savedBounds;
    private Label? _exitHint;

    // ToggleSidePanel has gone with the panel it folded away (stage 3.5.4). It existed to give
    // the picture the room a permanently-docked panel was taking; a tool window takes none until
    // it is opened and is closed by its own close box, so there is nothing left to fold.

    /// <summary>Fills the whole screen with just the remote desktop. F11 or the hint button returns.</summary>
    private void ToggleFullScreen()
    {
        if (_popped != null) return;   // meaningless while the screen is in its own window
        _fullScreen = !_fullScreen;
        SuspendLayout();
        if (_fullScreen)
        {
            _savedBorder = FormBorderStyle; _savedState = WindowState; _savedBounds = Bounds;
            // THE TOOLBAR STAYS. Full screen used to hide it, which meant leaving full screen to
            // send a file, switch monitor, untick Control or send Ctrl+Alt+Del — and leaving full
            // screen is exactly the thing the technician was avoiding. Reported on 2026-09-15.
            // The tools stay reachable too, because Tools is on that toolbar and its windows are
            // owned by this one, so they float above the full-screen picture rather than behind
            // it. That is the thing a docked panel could never do.
            //
            // The status bar stays too: it carries the connection pill, and since reconnect was
            // built that pill is the difference between "the picture is frozen" and "the picture
            // is frozen because the socket went". Hiding it in the mode people watch the screen in
            // would hide it exactly when it matters.
            //
            // What full screen gives up is the window chrome and the padding, and nothing else.
            _status.Visible = true;
            _screenHost.Padding = new Padding(0);
            _screenCard.Radius = 0;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;             // must leave Maximized before going borderless
            Bounds = Screen.FromControl(this).Bounds;
            TopMost = true;

            _exitHint = new Label
            {
                Text = "  Full screen — press F11 to exit  ", AutoSize = true, BackColor = Color.FromArgb(0x0F, 0x17, 0x2A),
                ForeColor = Color.White, Font = _t.BodyBold, Padding = new Padding(6), Cursor = Cursors.Hand
            };
            _exitHint.Click += (_, _) => ToggleFullScreen();
            Controls.Add(_exitHint);
            _exitHint.BringToFront();
            // Below the toolbar, not on top of it: the toolbar now stays in full screen, and a
            // hint that covers the System menu for four seconds is a hint that gets in the way.
            _exitHint.Location = new Point((Width - _exitHint.Width) / 2, _toolbar.Visible ? _toolbar.Height : 0);
            var fade = new System.Windows.Forms.Timer { Interval = 4000 };
            fade.Tick += (_, _) => { fade.Stop(); if (_exitHint != null) _exitHint.Visible = false; };
            fade.Start();
        }
        else
        {
            TopMost = false;
            if (_exitHint != null) { Controls.Remove(_exitHint); _exitHint.Dispose(); _exitHint = null; }
            FormBorderStyle = _savedBorder;
            Bounds = _savedBounds;
            WindowState = _savedState;
            _screenHost.Padding = new Padding(14, 0, 14, 0);
            _screenCard.Radius = _t.RadiusCard;
            _toolbar.Visible = _status.Visible = true;
        }
        // The width follows the text. "Exit full screen" does not fit the 128 px the button is
        // built at and rendered as "Exit full sc…", which looks like a broken button rather than a
        // deliberate label. The toolbar lays the right-hand group out from the right edge on
        // Resize, so changing the width here is enough — but nudge it, because a width that does
        // not change raises no Resize and the group would keep its old positions.
        _fullBtn.Text = _fullScreen ? "Exit full screen" : "Full screen";
        _fullBtn.Width = _fullScreen ? 156 : 128;
        _layoutToolbarRight?.Invoke();
        ResumeLayout();
        _screenCard.Invalidate();
        try { _view.Focus(); } catch { }
    }

    /// <summary>Moves the remote desktop into its own window, so it can live on a second monitor.</summary>
    private void TogglePopOut()
    {
        Trace.Write($"form: TogglePopOut — currently {(_popped == null ? "docked" : "popped out")}, fullScreen={_fullScreen}");
        if (_fullScreen) ToggleFullScreen();
        if (_popped == null)
        {
            _popped = new Form
            {
                Text = $"{_session.MachineName} — screen",
                // Wide enough for the toolbar, which now lives here too. Below about 1280 the
                // toolbar cannot lay itself out and piles its buttons on top of each other —
                // silently, because an obscured button looks exactly like one that is not being
                // pressed. The session window carries the same MinimumSize for the same reason.
                ClientSize = new Size(1360, 820),
                MinimumSize = new Size(1296, 640),
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = Color.Black,
                KeyPreview = false
            };
            _screenCard.Controls.Remove(_view);
            _view.Dock = DockStyle.Fill;
            _popped.Controls.Add(_view);

            // The tools do not have to be moved any more, and that is the point of 3.5.4. The
            // side panel had to travel with the screen, because a pop-out window is owned by this
            // one and Windows keeps an owned window above its owner always — so on a single
            // monitor the pop-out covered the panel and there was no way to raise it past. A tool
            // window is owned by this window too, so it is the pop-out's SIBLING rather than
            // something underneath it: either can be brought to the front, and neither has to be
            // carried anywhere.

            // The TOOLBAR goes, and it must be added LAST. Without it a popped-out screen has
            // no monitor selector, no Control tick box and no System menu, so the technician
            // cannot switch monitors, stop driving the machine or send Ctrl+Alt+Del without
            // popping the screen back in first — the thing they were avoiding. Reported 2026-09-15.
            //
            // The ORDER is not cosmetic. Docking is resolved from the highest child index down,
            // so the last control added is laid out first and gets the full width. Added before
            // the side panel, back when there was one, the toolbar only got the ~960 px left
            // beside it, its right-hand buttons hit the clamp, and "Pop in" and "Leave / End"
            // ended up on top of each other with Full screen pushed off the end entirely.
            // toolbarprobe's screenshot is the only reason that was seen — its own assertions
            // passed, because the buttons were all present and merely on top of one another.
            _canvas.Controls.Remove(_toolbar);
            _popped.Controls.Add(_toolbar);

            // The toolbar lays its right-hand group out from the right edge on Resize, and moving
            // it to a window of a different width may not raise one. Do it by hand.
            _layoutToolbarRight?.Invoke();
            // Closing the pop-out window means "pop back in" — but ONLY when the user closes that
            // window. Two things must not happen here:
            //  * running TogglePopOut inside FormClosing: the pop-in branch calls Close() on this
            //    same window, which re-enters this handler with _popped already null, so it takes
            //    the pop-out branch and builds a second, empty window. That is the black pop-out.
            //  * cancelling for any other reason: the session window owns this one, so cancelling
            //    a FormOwnerClosing close means "Leave / End" can never close the session.
            var win = _popped;
            bool popInQueued = false;
            win.FormClosing += (_, e) =>
            {
                if (_poppingIn) return;                       // our own Close() below, during pop-in
                // A close we did not start and the user did not either — the session window going
                // away, the app exiting, Windows shutting down. Let it through: this window is
                // owned by the session window, so cancelling here cancels "Leave / End" too.
                if (e.CloseReason is CloseReason.FormOwnerClosing or CloseReason.ApplicationExitCall
                                  or CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return;
                // Anything else is the user closing this window, which means "pop back in".
                e.Cancel = true;
                if (popInQueued || _popped != win) return;
                popInQueued = true;
                BeginInvoke(TogglePopOut);                    // never re-enter Close() from inside FormClosing
            };
            _popped.Show(this);
            _popBtn.Text = "Pop in";
            var note = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = _t.Muted,
                Text = "The screen is in its own window.\nUse \"Pop in\" to bring it back.", BackColor = Color.Transparent
            };
            note.Name = "popNote";
            _screenCard.Controls.Add(note);
        }
        else
        {
            var note = _screenCard.Controls["popNote"];
            if (note != null) { _screenCard.Controls.Remove(note); note.Dispose(); }
            // Put the toolbar back at the index it came from, before the window it is sitting in
            // is closed: docking is resolved in z-order, so re-adding at the end would leave it
            // running the full height down the side of the picture.
            _popped.Controls.Remove(_toolbar);
            _canvas.Controls.Add(_toolbar);
            _canvas.Controls.SetChildIndex(_toolbar, _toolbarIndex);
            _layoutToolbarRight?.Invoke();
            _popped.Controls.Remove(_view);
            var w = _popped; _popped = null;
            _poppingIn = true;
            try { w.Close(); w.Dispose(); } finally { _poppingIn = false; }
            _view.Dock = DockStyle.Fill;
            _screenCard.Controls.Add(_view);
            _popBtn.Text = "Pop out";
        }
        try { _view.Focus(); } catch { }
    }

    /// <summary>
    /// Builds one tool: its page, the window the page lives in, and its line on the Tools menu.
    ///
    /// The window is created here and not on first open, and its handle is created with it. That
    /// is what keeps a closed tool honest: <see cref="AppendChat"/>, <see cref="AppendScript"/>
    /// and <see cref="ShowFileList"/> are called from messages arriving off the socket whether or
    /// not anybody has opened anything, and a RichTextBox with no window behind it silently drops
    /// what is appended to it. Five hidden windows cost a handle each.
    /// </summary>
    private void AddTool(string name, Panel page, Size size)
    {
        page.Dock = DockStyle.Fill; page.Visible = true; page.BackColor = Color.Transparent;
        var win = new ToolWindow(_t, $"{name} — {_session.MachineName}", page, size, "tool." + name);

        // Closing a tool window puts it away; it does not destroy it. The chat log, the script
        // output and the file listing are the session's history and closing the window that shows
        // them must not be the thing that throws them away. Anything that is NOT the user closing
        // it — this window going, the app exiting, Windows shutting down — is let through, or the
        // session window could never close.
        win.FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            win.Hide();
            Trace.Write($"form: {name} window closed (kept, with what is in it)");
        };

        int mine = _tools.Count;
        _tools.Add(new Tool { Name = name, Page = page, Win = win });
        var item = new ToolStripMenuItem(name) { Font = _t.Body };
        item.Click += (_, _) => OpenTool(mine);
        _toolsMenu.Items.Add(item);

        _ = win.Handle;   // see the summary: the page has to be real before anyone opens it
    }

    /// <summary>Opens a tool and puts it in front. What the Tools menu does, and what the probes
    /// drive by name.</summary>
    private void OpenTool(int index) => ShowTool(index, activate: true);

    /// <summary>
    /// Shows a tool's window.
    ///
    /// <paramref name="activate"/> is the difference between a technician asking for a tool and
    /// the session putting one in front of them. A file that could not be written has to be
    /// visible, but taking the keyboard away from somebody in the middle of typing on the remote
    /// machine — which is what an activating window does — would be a worse fault than the one
    /// being reported.
    /// </summary>
    private void ShowTool(int index, bool activate)
    {
        if (index < 0 || index >= _tools.Count) return;
        // A message can arrive while this window is going away — the last FileSetDone of a
        // transfer that finished as the technician clicked Leave. Opening a window owned by a
        // form that is being disposed throws, and the exception would be the last thing in the
        // log before a clean exit, which is a fine way to spend a morning on nothing.
        if (IsDisposed || Disposing || !IsHandleCreated) return;

        // Opening Sound asks the machine straight away. Making somebody press Refresh to see
        // anything at all would mean the window's first impression is an empty box, and the
        // request is read-only and takes a moment.
        if (index == SoundToolIndex && !_soundAsked)
        {
            _soundAsked = true;
            RequestAudioDevices();
        }
        // And Files asks for the customer's desktop, for the same reason and one more. It used to
        // show an empty list until somebody typed a path, which is indistinguishable from a broken
        // transfer and was reported as exactly that — "I cannot copy a file from the client
        // desktop" turned out to mean "nobody ever asked the machine anything" (docs/BUGS.md #5).
        // Their desktop is Clint's choice of 2026-09-15: it is where the screenshot or the error
        // log that started the call actually is. %DESKTOP% is resolved on the host, which is the
        // only end that knows the logged-on user's profile.
        if (index == FilesToolIndex && !_filesAsked)
        {
            _filesAsked = true;
            _filesOpeningOnDesktop = true;
            _pathBox.Value = RemotePaths.Desktop;
            _link.Send(new FileListRequestMsg { Path = RemotePaths.Desktop });
            Trace.Write("files: first open, asking for " + RemotePaths.Desktop);
        }

        var tool = _tools[index];
        if (!tool.EverShown) { tool.EverShown = true; PlaceTool(tool.Win, index); }
        try { if (!tool.Win.Visible) tool.Win.Show(this); } catch (ObjectDisposedException) { return; }
        if (activate)
        {
            if (tool.Win.WindowState == FormWindowState.Minimized) tool.Win.WindowState = FormWindowState.Normal;
            try { tool.Win.Activate(); } catch { }
        }
        Trace.Write($"form: {tool.Name} window opened ({(activate ? "asked for" : "brought up by the session")})");
    }

    /// <summary>
    /// Puts a tool window somewhere sensible the first time it is opened, and never again.
    ///
    /// Where the technician last left it wins (stage 3.5.5) — including from a previous run of
    /// the console, and including its size. Failing that: over this window's right-hand side,
    /// which is where the panel these replaced used to be, cascaded so opening two does not hide
    /// one behind the other, and clamped to the working area of the screen this window is on.
    /// </summary>
    private void PlaceTool(ToolWindow win, int index)
    {
        if (win.RestorePlacement()) return;

        var area = Screen.FromControl(this).WorkingArea;
        int x = Bounds.Right - win.Width - 24 + index * 28;
        int y = Bounds.Top + 96 + index * 28;
        x = Math.Max(area.Left, Math.Min(x, area.Right - win.Width));
        y = Math.Max(area.Top, Math.Min(y, area.Bottom - win.Height));
        win.Location = new Point(x, y);
    }

    /// <summary>The Listen line on the Tools menu and the button on the Sound page say the same
    /// thing, so they are set in one place. Two copies of one fact drift.</summary>
    private void SetListenLabel(string text)
    {
        _listenItem.Text = text;
        _soundListenBtn.Text = text;
    }

    private Panel BuildChatPage()
    {
        var page = new Panel();
        _chatLog = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = _t.Surface, BorderStyle = BorderStyle.None, Font = _t.Body, ForeColor = _t.Ink };
        var row = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
        _chatInput = new InputBox(_t) { Dock = DockStyle.Fill };
        _chatInput.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendChat(); } };
        var send = new RoundButton(_t, BtnStyle.Accent) { Text = "Send", Dock = DockStyle.Right, Width = 74, Height = 40 };
        send.Click += (_, _) => SendChat();
        row.Controls.Add(_chatInput);
        row.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8, BackColor = Color.Transparent });
        row.Controls.Add(send);
        page.Controls.Add(_chatLog); page.Controls.Add(row);
        return page;
    }

    /// <summary>
    /// Sends this machine's clipboard to the customer, if it is worth sending. Text, files and
    /// folders alike — a copy is a copy, whatever was copied.
    ///
    /// Files used to be excluded here and left on a button, because a file drop list can run to
    /// gigabytes and starting that because somebody alt-tabbed is not a thing to do on a whim.
    /// That reasoning was right and the button was the wrong answer to it: the cost is the SIZE,
    /// not the kind, so a size rule handles it. Under <see cref="FileSetLimits.AutoFetchBytes"/>
    /// the set goes now and Ctrl+V works on the other side; over it, the status bar says what is
    /// waiting and one click sends it.
    /// </summary>
    private void SyncMyClipboard(string why)
    {
        // A browser cannot be given a clipboard (3.8.2): sending it would only make a promise.
        if (_dead || _session.ViewOnly) return;
        // Another session window's customer copied files, held for this window: they go straight across, machine to
        // machine, never through this PC (SessionForm.Bridge.cs).
        if (TakeOtherWindowsCopyStraight(why)) return;
        // Another session window's customer copied files that have not reached this PC yet: nothing here is what a
        // paste should give, so say so and send nothing until they land (SessionForm.Between.cs).
        if (WaitingForAnotherWindowsCopy(why)) return;

        // NOT re-entrant, and it can be re-entered. The OLE clipboard calls below pump messages
        // while they wait, so a WM_CLIPBOARDUPDATE or an activation arriving during one of them
        // comes straight back in here on the same thread, on top of the call already running. The
        // guards further down are all "have I sent this already", and every one of them reads
        // state the outer call has not finished writing.
        if (_inSyncClipboard) { Trace.Write($"clip: already reading the clipboard; ignoring this one ({why})"); return; }
        _inSyncClipboard = true;
        try { SyncMyClipboardCore(why); }
        finally { _inSyncClipboard = false; }
    }

    private bool _inSyncClipboard;

    private void SyncMyClipboardCore(string why)
    {

        // Files first: copying files in Explorer usually leaves a text form on the clipboard as
        // well, and sending that text would be the less useful half of what was copied.
        //
        // Every exit below says why. This path had five silent returns in it and a report of
        // "copy and paste does nothing" could not be told from any of them — on 2026-09-18 that
        // cost a whole round trip to a machine and back for each guess. One line per decision is
        // nothing to write and is the difference between reading the answer and inferring it.
        try
        {
            bool hasFiles;
            using (UiWatchdog.Work("reading the clipboard: ContainsFileDropList")) hasFiles = Clipboard.ContainsFileDropList();
            if (hasFiles)
            {
                List<string> drop;
                using (UiWatchdog.Work("reading the clipboard: GetFileDropList"))
                    drop = Clipboard.GetFileDropList().Cast<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                Trace.Write($"clip: clipboard holds a file drop list of {drop.Count} usable item(s) ({why})");
                if (drop.Count > 0) { OfferMyFilesToCustomer(drop, why); return; }
            }
            else Trace.Write($"clip: no file drop list on the clipboard; looking for text ({why})");
        }
        catch (Exception ex)
        {
            // Another process holding the clipboard open is the ordinary case and the next focus
            // retries. Saying so is what stops it looking like the feature is missing.
            Trace.Write($"clip: could not read the file drop list ({ex.GetType().Name}: {ex.Message}) — will retry on the next focus");
            return;
        }

        string text;
        try
        {
            using (UiWatchdog.Work("reading the clipboard: GetText"))
                text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (Exception ex) { Trace.Write($"clip: could not read text ({ex.GetType().Name})"); return; }
        if (text.Length == 0) { Trace.Write($"clip: nothing on the clipboard this can send ({why})"); return; }

        // Nothing changed, so nothing to say. Without this every alt-tab would re-send the same
        // text and overwrite whatever the customer had copied in between.
        if (text == _lastSentClip) return;
        // Their own text, mirrored onto this machine a moment ago. Sending it back would be a loop
        // that quietly clobbers their clipboard with what was already on it.
        if (text == _lastReceivedClip) return;

        bool cut = text.Length > ClipboardLimit;
        string send = cut ? text[..ClipboardLimit] : text;
        _link.Send(new ClipboardMsg { Text = send, Truncated = cut });
        _lastSentClip = text;
        _lastSentFiles = "";
        Trace.Write($"clip: auto-sent {send.Length} chars ({why})");
    }

    /// <summary>
    /// A file set copied on THIS machine, on its way to the customer's clipboard: sent at once if
    /// it is small, offered on the status bar if it is not.
    ///
    /// **The measuring happens on a background thread, and that is not a refinement.** Walking a
    /// copied selection means touching every file in it, and this runs from `OnActivated` and from
    /// every clipboard change while the window is in front — so doing it here froze the console
    /// the moment anybody copied a large folder, a network path or a OneDrive folder that had to
    /// be pulled down to be counted. Reported on 2026-09-18 as "I click the session on the taskbar
    /// and it just makes a noise", which is exactly what Windows does when a window's thread is
    /// not pumping: no crash, no error, nothing in the trace, just a dead window and a beep.
    ///
    /// Nothing is remembered until the walk finishes, so a copy that is still being measured is
    /// measured again rather than being silently forgotten.
    /// </summary>
    private void OfferMyFilesToCustomer(List<string> paths, string why)
    {
        var key = string.Join("|", paths);
        // The same selection, still on the clipboard, seen again on the next alt-tab. Re-sending
        // it would be a second transfer of the identical bytes.
        //
        // This is also the most likely reason a technician sees "nothing happened": the files
        // were sent once already and the clipboard has not changed since. It has to say so, or
        // the correct behaviour and a dead feature look identical.
        // Same paths AND the clipboard has not changed since we sent them: this is an alt-tab, not
        // a copy. A fresh Ctrl+C over the very same file bumps the sequence number even though the
        // paths are identical, and that must go — see _lastHandledClipSeq.
        uint seq = 0;
        try { seq = GetClipboardSequenceNumber(); } catch { }
        if (key == _lastSentFiles && seq != 0 && seq == _lastHandledClipSeq)
        {
            Trace.Write($"clip: the clipboard has not changed since those {paths.Count} item(s) were sent — this is a focus change, not a copy ({why})");
            return;
        }
        if (seq != 0) _lastHandledClipSeq = seq;
        // A walk is already running for this selection. Without this, every clipboard change and
        // every activation while a slow one is in flight would start another walk over the same
        // tree, and a folder on a slow share would end up being counted several times at once.
        if (key == _measuringFiles) { Trace.Write($"clip: already measuring those {paths.Count} item(s) ({why})"); return; }
        _measuringFiles = key;

        Trace.Write($"clip: measuring {paths.Count} copied item(s) off the UI thread ({why})");
        _ = Task.Run(() =>
        {
            FileSetPlan? plan = null;
            string? error = null;
            try { plan = FileSetSender.Plan(paths); }
            catch (Exception ex) { error = ex.Message; }

            try
            {
                BeginInvoke(() =>
                {
                    if (_measuringFiles == key) _measuringFiles = "";
                    if (_dead) return;
                    if (error != null || plan == null) { SetTransferStatus("could not read what you copied: " + (error ?? "no reason given")); return; }

                    long bytes = plan.TotalBytes;
                    int files = plan.FileCount;

                    // Checked again here rather than only above, because a NEWER copy can have
                    // arrived while this walk was running — and then this one is stale and the
                    // newer one is already on its way. Compared by sequence number, not by paths:
                    // comparing paths here was the second half of the same fault as above, and it
                    // refused a genuine re-copy of the same file just as thoroughly.
                    if (seq != 0 && _lastHandledClipSeq != seq)
                    {
                        Trace.Write("clip: something else was copied while this was being measured; dropping the older one");
                        return;
                    }
                    _lastSentFiles = key;
                    _lastSentClip = "";

                    if (bytes <= FileSetLimits.AutoFetchBytes)
                    {
                        Trace.Write($"clip: auto-sending {files} file(s), {FileSetLimits.Human(bytes)} ({why})");
                        // The plan we just built, not the paths: PushFileSet would walk the same
                        // tree a second time, on this thread, which is the freeze all over again.
                        PushPlannedFileSet(plan, thenPutOnTheirClipboard: true);
                        return;
                    }

                    // Too big to move on a gesture nobody thought of as a transfer. Say exactly
                    // what it is and what it would cost, and wait to be asked — an offer nobody
                    // understands is a refusal.
                    _pendingSend = paths;
                    OfferOnStatusBar($"you copied {Describe(files, paths.Count)} ({FileSetLimits.Human(bytes)}) — click here to send it to {_session.MachineName}");
                });
            }
            catch { }   // the window went away while we were counting; nothing to report to
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Registered against the window handle, so it has to wait for one to exist.
        try { _clipListening = AddClipboardFormatListener(Handle); } catch { _clipListening = false; }
        // Copy on one customer's machine, paste on another (SessionForm.Between.cs).
        JoinOpenWindows();
        // Same reason: the watchdog needs the raw HWND to ask whether a modal dialog has disabled
        // this window, and that question cannot be asked before there is a window to ask about.
        try { UiWatchdog.SetWindow(Handle); } catch { }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // The moment that matters: the window is in front, so a paste is what happens next.
        SyncMyClipboard("window in front");
    }

    protected override void WndProc(ref Message m)
    {
        // Only while this window is the active one. A copy made anywhere else, with this window
        // in the background, is none of the customer's business until the technician comes back
        // to it - and then OnActivated sends it.
        if (m.Msg == WM_CLIPBOARDUPDATE && ActiveForm == this) SyncMyClipboard("copied while in front");
        base.WndProc(ref m);
    }

    private Panel BuildFilesPage()
    {
        var page = new Panel();
        _pathBox = new InputBox(_t) { Dock = DockStyle.Top, Height = 38 };
        _pathBox.Box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _link.Send(new FileListRequestMsg { Path = _pathBox.Value }); } };
        var bar = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
        var go = new RoundButton(_t, BtnStyle.Neutral) { Text = "Go", Dock = DockStyle.Right, Width = 60, Height = 34 };
        go.Click += (_, _) => _link.Send(new FileListRequestMsg { Path = _pathBox.Value });
        var up = new RoundButton(_t, BtnStyle.Neutral) { Text = "Up", Dock = DockStyle.Right, Width = 58, Height = 34 };
        up.Click += (_, _) => { try { var p = Path.GetDirectoryName(_pathBox.Value.TrimEnd('\\', '/')); _pathBox.Value = p ?? ""; _link.Send(new FileListRequestMsg { Path = _pathBox.Value }); } catch { } };
        bar.Controls.Add(go);
        bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6, BackColor = Color.Transparent });
        bar.Controls.Add(up);

        _fileList = new ListView().Themed(_t);
        _fileList.Dock = DockStyle.Fill;
        _fileList.Columns.Add("Name", 200); _fileList.Columns.Add("Size", 80); _fileList.Columns.Add("Modified", 110);
        // Fixed widths totalled 390 px in a panel about 337 px wide, so the date was cut off and
        // the list scrolled sideways — on the one tab whose whole job is now to be readable the
        // moment it opens. The name takes whatever is left instead. (Widths only: a Resize handler
        // that sets its own control's size recurses instantly, §10.)
        _fileList.Resize += (_, _) =>
        {
            int fixedCols = 66 + 98, gutter = 26;   // size, modified, and room for a scrollbar
            _fileList.Columns[0].Width = Math.Max(110, _fileList.ClientSize.Width - fixedCols - gutter);
            _fileList.Columns[1].Width = 66;
            _fileList.Columns[2].Width = 98;
        };
        _fileList.DoubleClick += (_, _) => OpenSelected();
        _fileList.MultiSelect = true;
        // The panel is narrow, so a long name is shortened on screen; the tooltip is how the whole
        // of it stays readable without stealing width from the date.
        _fileList.ShowItemToolTips = true;
        // Drag OUT. The drop target is the rest of Windows: the desktop, a folder, an email. See
        // DragOut for why the bytes have to be here before the drop completes and how that is the
        // same ordering paste already uses.
        _fileList.ItemDrag += (_, _) => BeginDragOut(SelectedRemotePaths(), _fileList);

        var hint = new Label
        {
            Dock = DockStyle.Bottom, Height = 20, Font = _t.Small, ForeColor = _t.Muted, UseMnemonic = false,
            Text = "Drag anything here onto your desktop, a folder or an email.", AutoEllipsis = true
        };

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Padding = new Padding(0, 6, 0, 0) };
        var download = new RoundButton(_t, BtnStyle.Accent) { Text = "Download", Dock = DockStyle.Left, Width = 108, Height = 36 };
        download.Click += (_, _) => DownloadSelected();
        var upload = new RoundButton(_t, BtnStyle.Ghost) { Text = "Send file…", Dock = DockStyle.Left, Width = 108, Height = 36 };
        upload.Click += (_, _) => UploadFile();
        actions.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6, BackColor = Color.Transparent });
        actions.Controls.Add(upload);
        actions.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 6, BackColor = Color.Transparent });
        actions.Controls.Add(download);

        // Among bottom-docked controls the one added FIRST sits highest, so the hint goes on after
        // the buttons to end up under them.
        page.Controls.Add(_fileList); page.Controls.Add(actions); page.Controls.Add(hint);
        page.Controls.Add(bar); page.Controls.Add(_pathBox);
        return page;
    }

    private Panel BuildScriptsPage()
    {
        var page = new Panel();
        _scriptOut = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = _t.Slate, ForeColor = _t.ConsoleText, Font = _t.Mono, BorderStyle = BorderStyle.None };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 160, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0) };
        _scriptBox = new TextBox { Multiline = true, Font = _t.Mono, ScrollBars = ScrollBars.Vertical };
        var ctl = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0) };
        _scriptLang = new ThemedCombo(_t) { Dock = DockStyle.Left, Width = 148, Font = _t.Body, FlatStyle = FlatStyle.Flat };
        _scriptLang.Items.AddRange(new object[] { "powershell", "batch" }); _scriptLang.SelectedIndex = 0;
        var run = new RoundButton(_t, BtnStyle.Success) { Text = "Run", Dock = DockStyle.Right, Width = 80, Height = 36 };
        run.Click += (_, _) => RunScript();
        var kill = new RoundButton(_t, BtnStyle.Danger) { Text = "Stop", Dock = DockStyle.Right, Width = 70, Height = 36 };
        kill.Click += (_, _) => { if (_currentRunId.Length > 0) _link.Send(new ScriptKillMsg { RunId = _currentRunId }); };
        ctl.Controls.Add(run);
        ctl.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6, BackColor = Color.Transparent });
        ctl.Controls.Add(kill);
        // Auto-update's one update by hand (2026-10-16, stage F): a machine installed before auto-update cannot take an update
        // itself. This fills the box with the script that gives it one — read it, then Run. Never run without being shown.
        var byHand = new RoundButton(_t, BtnStyle.Neutral) { Text = "Update this machine", Dock = DockStyle.Left, Height = 36 };
        byHand.Width = TextRenderer.MeasureText(byHand.Text, _t.ButtonFont).Width + 36;
        byHand.Click += (_, _) =>
        {
            _scriptLang.SelectedIndex = 0;
            _scriptBox.Text = UpdateByHand.Script(DownloadIdentity.HttpOrigin(Branding.Load().ServerUrl)).Replace("\r\n", "\n").Replace("\n", "\r\n");
            _scriptBox.SelectionStart = 0;
        };
        ctl.Controls.Add(byHand);
        ctl.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8, BackColor = Color.Transparent });
        ctl.Controls.Add(_scriptLang);
        // Both in rounded frames (2026-10-06): the output on its own dark console ground, the script
        // box as a field like any other. They were square, the script box with Windows' black border.
        bottom.Controls.Add(new FieldFrame(_t, _scriptBox) { Dock = DockStyle.Fill }); bottom.Controls.Add(ctl);
        page.Controls.Add(new FieldFrame(_t, _scriptOut, _t.Slate) { Dock = DockStyle.Fill }); page.Controls.Add(bottom);
        return page;
    }

    /// <summary>
    /// The Sound tab: hear the machine, and — the half no competitor offers — be told WHY it is
    /// silent. Streaming answers "is there sound"; this answers "why is there none", which is the
    /// question a support call actually starts with.
    /// </summary>
    private Panel BuildSoundPage()
    {
        var page = new Panel { Padding = new Padding(0, 4, 0, 0) };

        // Listening controls sit here as well as on the toolbar. The toolbar button is for
        // somebody watching the screen; this is for somebody who came to the tab to work out why
        // the machine is quiet, and making them go back up to the toolbar would be silly.
        var controls = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Color.Transparent };
        _soundListenBtn = new RoundButton(_t, BtnStyle.Accent) { Text = "Listen", Size = new Size(96, 32), Location = new Point(0, 2) };
        _soundListenBtn.Click += (_, _) => ToggleListen();
        _soundMeter = new LevelMeter(_t) { Location = new Point(104, 2), Size = new Size(120, 32) };
        var refreshBtn = new RoundButton(_t, BtnStyle.Neutral) { Text = "Refresh", Size = new Size(92, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        refreshBtn.Click += (_, _) => RequestAudioDevices();
        controls.Resize += (_, _) => refreshBtn.Location = new Point(Math.Max(230, controls.Width - refreshBtn.Width), 2);
        controls.Controls.AddRange(new Control[] { _soundListenBtn, _soundMeter, refreshBtn });

        // A read-only box rather than a grid: the useful output is a short report with a few
        // lines that matter and a list that usually does not, and 400 px of tab is not enough
        // width for columns that would be worth having.
        _soundReport = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = _t.Surface, Font = _t.Body, ForeColor = _t.Ink, WordWrap = true
        };
        _soundReport.Text = "Press Refresh to ask that machine what its sound hardware is doing.";

        page.Controls.Add(_soundReport);
        page.Controls.Add(controls);
        page.Controls.Add(new SectionTitle(_t, "Why is there no sound?", _t.Orange) { Dock = DockStyle.Top });
        return page;
    }

    /// <summary>Asks the host for the audio picture. Cheap and read-only, so nothing warns first.</summary>
    private void RequestAudioDevices()
    {
        _soundReport.Text = "asking " + _session.MachineName + "…";
        _link.Send(new AudioDevicesRequestMsg());
        Trace.Write("audio: asked the host for its devices");
    }

    /// <summary>
    /// Renders the report, findings first.
    ///
    /// The findings are the product. A technician can read a device list themselves; what earns
    /// its place is the line that says "everything is muted" or "the default output is a
    /// television" without them having to know that Windows keeps three separate defaults.
    /// </summary>
    private void ShowAudioDevices(AudioDevicesMsg m)
    {
        _soundReport.Clear();
        if (m.Error.Length > 0)
        {
            AppendSound(m.Error + "\n\n", _t.BadText, bold: true);
        }

        var play = m.Devices.Where(d => !d.IsCapture).ToList();

        // The rules live in AudioFindings, where they can be exercised against made-up machines
        // that would be a nuisance to arrange in real life. See `audiolocal --findings`.
        var findings = AudioFindings.For(m);

        if (findings.Count == 0)
            AppendSound("Nothing obviously wrong with the sound configuration.\n"
                      + "Everything below is active, audible and pointed somewhere sensible.\n\n", _t.OkText, bold: true);
        else
            foreach (var f in findings) AppendSound("• " + f + "\n\n", _t.BadText, bold: true);

        // ---- the evidence ---------------------------------------------------------------
        AppendSound("Playback devices\n", _t.Muted, bold: true);
        if (play.Count == 0) AppendSound("  none\n", _t.Muted);
        foreach (var d in play)
        {
            var roles = new List<string>();
            if (d.IsDefaultConsole) roles.Add("system sounds");
            if (d.IsDefaultMultimedia) roles.Add("music and video");
            if (d.IsDefaultCommunications) roles.Add("calls");
            bool interesting = d.State != "ACTIVE" || d.Muted;
            AppendSound($"  {d.Name}\n", interesting ? _t.BadText : _t.Ink, bold: roles.Count > 0);
            // Built as a list and joined, so a device with no volume to report does not end up
            // with a dangling comma after its state. It read "unplugged," in the first screenshot.
            var parts = new List<string>();
            if (d.State != "ACTIVE") parts.Add(d.State.ToLowerInvariant());
            if (d.VolumePercent >= 0) parts.Add($"volume {d.VolumePercent}%" + (d.Muted ? ", MUTED" : ""));
            else if (d.Muted) parts.Add("MUTED");
            if (roles.Count > 0) parts.Add("default for " + string.Join(", ", roles));
            if (d.Error.Length > 0) parts.Add(d.Error);
            if (parts.Count > 0) AppendSound("    " + string.Join(" — ", parts) + "\n", _t.Muted);
        }

        if (m.Apps.Count > 0)
        {
            AppendSound("\nApplications using the default device\n", _t.Muted, bold: true);
            foreach (var a in m.Apps)
                AppendSound($"  {a.Name} — {a.State}, mixer {(a.VolumePercent < 0 ? "?" : a.VolumePercent + "%")}"
                          + (a.Muted ? ", MUTED" : "") + "\n", a.Muted ? _t.BadText : _t.Muted);
        }

        var rec = m.Devices.Where(d => d.IsCapture).ToList();
        if (rec.Count > 0)
        {
            AppendSound("\nRecording devices\n", _t.Muted, bold: true);
            foreach (var d in rec)
                AppendSound($"  {d.Name} — {d.State.ToLowerInvariant()}\n", _t.Muted);
        }

        if (m.Services.Count > 0)
        {
            AppendSound("\nAudio services\n", _t.Muted, bold: true);
            foreach (var s in m.Services) AppendSound("  " + s + "\n", _t.Muted);
        }

        _soundReport.SelectionStart = 0;
        _soundReport.ScrollToCaret();
    }

    private void AppendSound(string text, Color colour, bool bold = false)
    {
        _soundReport.SelectionStart = _soundReport.TextLength;
        _soundReport.SelectionLength = 0;
        _soundReport.SelectionColor = colour;
        _soundReport.SelectionFont = bold ? _t.BodyBold : _t.Body;
        _soundReport.AppendText(text);
    }

    private Panel BuildInfoPage()
    {
        var page = new Panel();
        // A themed, wrapping text box like the Sound report. It was a ListBox with the theme's ink
        // on Windows' default WHITE: in the dark theme that is pale grey on white, which internal
        // testing reported as "opens a blank window" (2026-10-02) — and its longest lines ran off
        // the right-hand edge, where a ListBox cannot wrap them.
        _infoList = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = true,
            BackColor = _t.Surface, ForeColor = _t.Ink, Font = _t.Body, ScrollBars = RichTextBoxScrollBars.Vertical,
            Text = "Waiting for that machine to describe itself…"
        };
        page.Controls.Add(_infoList);
        return page;
    }

    // ---------------------------------------------------------------- message handling
    private void HandleMsg(Msg m)
    {
        if (HandlePrintMsg(m)) return;
        switch (m)
        {
            case PrivacyStateMsg ps:
                _privacyOn = ps.On;
                _privacyItem.Checked = ps.On;
                _privacyItem.Text = ps.On ? "Privacy screen is ON — show their screen again" : "Privacy screen — black out their screen";
                SetTransferStatus(ps.Error.Length > 0 ? "The privacy screen did not come on: " + ps.Error
                    : ps.On ? $"Privacy screen on — {_session.MachineName}'s screen is black ({ps.Monitors} monitor{(ps.Monitors == 1 ? "" : "s")}); you still see it"
                    : $"Privacy screen off — {_session.MachineName} can see their screen again");
                Trace.Write($"form: privacy state on={ps.On} monitors={ps.Monitors}{(ps.Error.Length > 0 ? " error=" + ps.Error : "")}");
                break;
            case SelectionMsg sel:
                // Only the answer to this window's own question; another technician in the same
                // session asked their own.
                if (sel.RequestId == _selectionAsked) _selection = sel;
                break;
            case MonitorsMsg mon:
                _monitors = mon.Monitors;
                Trace.Write($"form: MonitorsMsg received — {_monitors.Count} monitor(s): " +
                            string.Join(", ", _monitors.Select(x => $"idx={x.Index} {x.Width}x{x.Height}{(x.Primary ? " primary" : "")}")));
                _monitorBox.Items.Clear();
                foreach (var mi in _monitors) _monitorBox.Items.Add($"{mi.Index + 1}: {mi.Width}×{mi.Height}{(mi.Primary ? " (primary)" : "")}");
                if (_monitorBox.Items.Count > 0 && _monitorBox.SelectedIndex < 0)
                {
                    Trace.Write("form: selecting monitor slot 0 — this is the only thing that sends the first SubscribeMsg");
                    _monitorBox.SelectedIndex = 0;
                }
                else Trace.Write($"form: monitor combo NOT re-selected (items={_monitorBox.Items.Count}, selected={_monitorBox.SelectedIndex}) — no SubscribeMsg from here");
                break;
            case CursorMsg c: if (c.Monitor == _view.MonitorIndex) _view.SetRemoteCursor(c.X, c.Y, c.Visible, c.ShapeId); break;
            case ParticipantsMsg p: _partLabel.Text = $"Participants: {string.Join(", ", p.Technicians)}" + (p.HostConnected ? "" : "  (client offline)"); break;
            case ChatMsg chat: AppendChat(chat); break;
            case FileListMsg fl: ShowFileList(fl); break;
            case FileOfferMsg fo: BeginIncoming(fo); break;
            case FileDoneMsg fd: FinishIncoming(fd); break;
            case ScriptOutputMsg so: AppendScript(so); break;
            case SysInfoMsg si:
                _infoList.Text = string.Join("\n\n", si.Info.Select(kv => $"{kv.Key}: {kv.Value}"));
                // Whether a copy can go straight to or from this machine (SessionForm.Bridge.cs).
                _hostCanBridge = HostCan.Bridge(si);
                break;

            // ---- a copy straight between this machine and the other window's (SessionForm.Bridge.cs) ----
            case BridgeAskMsg bridgeYes: OnBridgeAsked(bridgeYes); break;
            case BridgeProgressMsg bridgeProgress: OnBridgeProgress(bridgeProgress); break;
            case BridgeEndMsg bridgeEnd: OnBridgeEnded(bridgeEnd); break;

            // The customer's clipboard, or a reason we could not get or set it. Only ever
            // arrives because this end asked for it — see BuildClipboardPage for why there is
            // no automatic sync.
            case FileSetOfferMsg fso: OnFileSetOffered(fso); break;
            case FileSetAcceptMsg fsa: OnFileSetAccepted(fsa); break;

            // The host's account of what actually moved. It is on its way to the customer's tray
            // app and the ticket; traced here so the technician's own log and the customer's
            // agree about what was taken, which is the only way to answer "did you copy anything
            // off my machine" afterwards with something better than a memory.
            case FileActivityMsg fa: Trace.Write("activity: " + fa.Describe("the technician")); break;
            case FileSetProgressMsg fsp:
                SetTransferStatus($"{fsp.FilesDone}/{fsp.FilesTotal} files, " +
                                  $"{FileSetLimits.Human(fsp.BytesDone)} of {FileSetLimits.Human(fsp.TotalBytes)} — {fsp.Current}",
                                  fsp.BytesDone, fsp.TotalBytes);
                break;

            case FileSetDoneMsg fsd:
            {
                // The customer's machine confirming a tree WE sent. If that push was a paste,
                // this is the moment their clipboard can be pointed at the files, because only
                // now do the paths exist on their disk.
                bool wasPaste = _pasteSets.Remove(fsd.SetId);
                if (!fsd.Ok) { ForgetLastSentFiles("the customer's machine reported a problem"); SetTransferStatus("the customer's machine reported a problem: " + (fsd.Error ?? "unknown")); }
                else if (wasPaste && fsd.SavedPaths.Count > 0)
                {
                    _link.Send(new ClipboardMsg { Files = fsd.SavedPaths });
                    // The staging folder is not worth naming: it is a temp path the technician
                    // will never visit, and putting it on screen invites them to go looking for
                    // the files there instead of pasting them where they want them.
                    SetTransferStatus(fsd.SavedPaths.Count == 1
                        ? $"copied to {_session.MachineName} — paste it anywhere on their machine now"
                        : $"copied {fsd.SavedPaths.Count} items to {_session.MachineName} — paste them anywhere on their machine now");
                }
                // A paste whose confirmation carried no paths is worth saying out loud rather than
                // reporting as an ordinary send: it means the bytes went somewhere and the
                // clipboard was never pointed at them, which is the failure this whole thing is
                // for and is otherwise completely silent.
                else if (wasPaste) SetTransferStatus($"sent to {fsd.SavedDir}, but their machine named no paths — nothing to paste");
                else SetTransferStatus($"sent to {fsd.SavedDir}");

                if (fsd.Skipped.Count > 0)
                {
                    ShowTool(ScriptsToolIndex, activate: false);
                    AppendScript(new ScriptOutputMsg { Text = "[files] the customer's machine could not write:\n", IsError = true });
                    foreach (var s in fsd.Skipped.Take(20)) AppendScript(new ScriptOutputMsg { Text = "  " + s + "\n", IsError = true });
                }
                break;
            }

            // The customer's clipboard, either because they copied something while we were
            // watching or because this end asked for it. Getting it onto THIS machine's clipboard
            // is the whole job: "paste" has to mean Ctrl+V in Explorer, not a box to read from.
            case ClipboardMsg cb:
            {
                if (!string.IsNullOrEmpty(cb.Error)) { SetTransferStatus(cb.Error); break; }

                if (cb.Files is { Count: > 0 })
                {
                    // Their copy replaces ours NOW, before a byte has moved. Until the fetch lands
                    // — or, over the threshold, until somebody clicks the offer — this clipboard
                    // would otherwise still hold whatever the technician copied last, and Ctrl+V
                    // here would paste THAT. Clint, 2026-09-29: folders copied across worked, and
                    // then copying different folders back "pastes the original files". Nothing to
                    // paste is honest; the wrong files are not.
                    ForgetMyClipboardForTheirs(cb.Files.Count);

                    // Another session window is open: held for it, to go straight across when the technician clicks
                    // into it — not down to this PC (SessionForm.Bridge.cs, Clint's answer 1).
                    if (HoldForTheOtherWindow(cb.Files, cb.Bytes, out var throughPc)) break;
                    var why = throughPc.Length > 0 ? $" ({throughPc})" : "";

                    // Big enough to be worth asking about. A clipboard file list is paths, so
                    // nothing has moved yet and nothing will until this is clicked.
                    if (cb.Bytes > FileSetLimits.AutoFetchBytes)
                    {
                        _pendingFetch = cb.Files;
                        OfferOnStatusBar($"they copied {cb.Files.Count} item(s) ({FileSetLimits.Human(cb.Bytes)}) — click here to fetch it{why}");
                        break;
                    }
                    var size = cb.Bytes > 0 ? $", {FileSetLimits.Human(cb.Bytes)}" : "";
                    SetTransferStatus($"they copied {cb.Files.Count} item(s){size}; fetching…{why}");
                    PullFileSet(cb.Files, thenOnMyClipboard: true);
                    break;
                }

                if (cb.Text.Length == 0) break;
                // Text copied here is the newest copy now: files held for the other window are not what a paste means.
                _copiedHere = null;

                // Remembered so the mirror never sends it back. Their text is about to be put on
                // OUR clipboard, and the next time this window comes to the front that would
                // otherwise look like something new to send them — a loop that overwrites their
                // clipboard with what was already on it.
                _lastReceivedClip = cb.Text;
                try
                {
                    using (UiWatchdog.Work("writing the clipboard: SetText")) Clipboard.SetText(cb.Text);
                    // A push that worked is meant to be invisible: the technician pressed Ctrl+C
                    // on the other machine and expects Ctrl+V to work here, not a report about it.
                    // Something they ASKED for still gets an answer, because they are waiting.
                    if (!cb.Auto)
                        SetTransferStatus($"got {cb.Text.Length:N0} characters and put them on your clipboard" + (cb.Truncated ? " (truncated)" : ""));
                    else
                        Trace.Write($"clip: mirrored {cb.Text.Length} chars from {_session.MachineName}");
                }
                catch (Exception ex)
                {
                    // A clipboard another program is holding open is the one failure that looks
                    // exactly like success from the chair — nothing appears either way.
                    SetTransferStatus($"they copied {cb.Text.Length:N0} characters but your own clipboard refused them ({ex.GetType().Name}) — copy again in a moment");
                }
                break;
            }
            // The host's account of the audio side. Its mute and volume are shown because they
            // are the first things a technician blames for silence — measured on two machines,
            // loopback is taken BEFORE either is applied, so neither one can silence this. Saying
            // so on the status bar is what stops somebody chasing that ghost for an hour.
            case AudioStatusMsg au:
            {
                if (au.Error.Length > 0)
                {
                    SetAudioStatus(au.Error, bad: true);
                    if (_listening) StopListening("stopped: " + au.Error);
                    break;
                }
                if (!au.Listening)
                {
                    if (_listening) StopListening("that machine stopped sending sound");
                    break;
                }
                var mute = au.Muted ? "their speakers are muted — you will still hear them" : $"their volume {au.VolumePercent}%";
                SetAudioStatus($"{au.Device} — {mute}");
                break;
            }

            case AudioDevicesMsg ad: ShowAudioDevices(ad); break;
            case VoiceStateMsg vs: OnVoiceState(vs); break;

            // No longer a dialog and a Close(). The window stays up with its chat, its transfers
            // and its script output still readable, saying plainly that nothing is connected and
            // offering to start a fresh session on the same machine. A window that vanishes is
            // honest about the session but takes everything in it with it.
            case SessionEndedMsg se:
                if (_leaving) break;
                // A restart ENDS the session — the host dies with the machine, so the server
                // tears the session down and tells us. That is expected and is not the window
                // giving up: this used to fall straight through to MarkDead, which set _dead,
                // replaced the countdown with a Reconnect button, and left the polling running
                // behind a UI that said it had stopped. That is exactly what "it only gives you
                // the option to reconnect" looked like from the outside.
                if (_rebooting)
                {
                    Trace.Write("form: session ended because the machine is restarting — holding, not giving up");
                    UpdateRebootBanner();
                    break;
                }
                if (ResumeRestartIfStillGoingDown("the session ended")) break;
                MarkDead(string.IsNullOrWhiteSpace(se.Reason) ? "That session has ended." : se.Reason);
                break;

            // What the machine made of a reboot / lock / Ctrl+Alt+Del. A refused reboot looks
            // exactly like one that is about to happen, and this window would otherwise wait
            // five minutes for a machine that was never going anywhere.
            case SysActionResultMsg sar:
                Trace.Write($"form: SysActionResult {sar.Action} ok={sar.Ok} — {sar.Detail}");
                if (!sar.Ok)
                {
                    SetTransferStatus($"{_session.MachineName} could not do that: {sar.Detail}");
                    if (sar.Action == "reboot" && _rebooting)
                    {
                        _rebooting = false; _rebootTick.Stop(); _view.Stale = false;
                        SetPill("● connected", ok: true);
                        ShowBanner($"{_session.MachineName} refused the restart: {sar.Detail}", reconnect: false, close: true);
                    }
                }
                else SetTransferStatus($"{_session.MachineName}: {sar.Detail}");
                break;
            case ErrorMsg e: AppendScript(new ScriptOutputMsg { Text = "[server] " + e.Message + "\n", IsError = true }); break;
        }
    }

    // ------------------------------------------------------------------ hearing the machine

    /// <summary>
    /// Turns listening on or off. The console opens its own speakers FIRST and only asks the
    /// customer's machine to start once that has worked — otherwise the host streams audio at a
    /// console that cannot play it, and the technician is told nothing.
    /// </summary>
    /// <summary>
    /// Record button (1.6). Starting tells the server, which writes it on the ticket and tells the
    /// person at the machine through their tray app; stopping does the same, then finishes the file
    /// and uploads it to the ticket off the UI thread.
    /// </summary>
    private void ToggleRecording()
    {
        if (_recorder == null)
        {
            var rec = SessionRecorder.Start(_view.RemoteSize, _view.RenderFrame, _session.MachineName, out var why);
            if (rec == null) { SetTransferStatus("Could not start recording: " + why); return; }
            _recorder = rec;
            _link.Send(new RecordingStateMsg { On = true });
            _recordItem.Text = "Stop recording";
            _recPill.Text = "● Recording 00:00 — click to stop";
            _recPill.Visible = true;
            _recTick.Start();
            Trace.Write($"form: recording to {rec.LocalPath}");
            return;
        }
        StopRecording(uploadAsWindowCloses: false);
    }

    /// <summary>
    /// Stops, then finishes and uploads in the background. Also called as the window closes, when
    /// there is no status bar left to report to — the result then goes to the trace, and a failed
    /// upload leaves the file in SessionRecorder.Folder.
    /// </summary>
    private void StopRecording(bool uploadAsWindowCloses)
    {
        var rec = _recorder;
        if (rec == null) return;
        _recorder = null;
        _recTick.Stop();
        try { _link.Send(new RecordingStateMsg { On = false }); } catch { }
        if (!uploadAsWindowCloses)
        {
            _recordItem.Text = "Record this session";
            _recPill.Visible = false;
            SetTransferStatus("saving the recording to the ticket…");
        }
        string wsBase = _wsBase, token = _token, sessionId = _session.SessionId;
        _ = Task.Run(async () =>
        {
            var err = rec.Stop();
            if (err.Length == 0) err = await SessionRecorder.UploadAsync(wsBase, token, sessionId, rec.LocalPath, rec.Seconds);
            var msg = err.Length == 0
                ? $"Recording saved on the ticket ({rec.Elapsed:mm\\:ss})"
                : $"The recording was not saved to the ticket ({err}) — it is at {rec.LocalPath}";
            Trace.Write("recording: " + msg);
            if (uploadAsWindowCloses || IsDisposed) return;
            try { BeginInvoke(() => SetTransferStatus(msg)); } catch { }
        });
    }

    private void ToggleListen()
    {
        if (_listening)
        {
            _wantListen = false;
            _link.Send(new AudioListenMsg { On = false });
            StopListening("stopped listening");
            return;
        }
        _wantListen = true;   // put back after a reconnect: see ResumeAfterReconnect

        if (!_audio.Start())
        {
            SetAudioStatus(_audio.Error ?? "this machine's speakers could not be opened", bad: true);
            return;
        }
        _listening = true;
        _audioFrames = 0;
        SetListenLabel("Listening");
        _meter.Active = _soundMeter.Active = true;
        _meterTick.Start();
        _audioLabel.Visible = true;
        SetAudioStatus($"asking {_session.MachineName} for sound…");
        _link.Send(new AudioListenMsg { On = true });
        Trace.Write("audio: asked the host to start streaming");
    }

    private void StopListening(string why)
    {
        _listening = false;
        _meterTick.Stop();
        _meter.Active = _soundMeter.Active = false;
        _meter.SetLevel(0); _soundMeter.SetLevel(0);
        SetListenLabel("Listen");
        Trace.Write($"audio: {why} — {_audio.DiagnosticSummary()} frames={_audioFrames}");
        _audio.Stop();
        SetAudioStatus(why);
    }

    private void SetAudioStatus(string text, bool bad = false)
    {
        _audioLabel.Visible = true;
        _audioLabel.ForeColor = bad ? _t.BadText : _t.Muted;
        _audioLabel.Text = text;
        Trace.Write("audio: " + text);
    }

    private void HandleBinary(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length < 1) return;
        if (frame.Span[0] == BinKind.Tiles) _view.ApplyTiles(frame);
        else if (frame.Span[0] == BinKind.Video) _view.ApplyVideo(frame);
        else if (frame.Span[0] == BinKind.Cursor) _view.ApplyCursorShape(frame);
        else if (frame.Span[0] == BinKind.Voice) OnVoiceFrame(frame);
        else if (frame.Span[0] == BinKind.Audio)
        {
            if (frame.Length <= Bin.AudioHeader) return;
            var (_, rate, channels, format, pcm) = Bin.ParseAudio(frame);
            // The format travels with the data, so a mid-session device change cannot arrive out
            // of order with the samples it describes. Anything we cannot play is refused out loud
            // rather than played as noise.
            if (format != AudioFormat.Pcm16 || channels != 1 || rate != AudioPlayer.Rate)
            {
                if (_listening)
                {
                    SetAudioStatus($"that machine is sending {rate} Hz {channels}ch format {format}, which this cannot play", bad: true);
                    _link.Send(new AudioListenMsg { On = false });
                    StopListening("stopped: unplayable format");
                }
                return;
            }
            if (++_audioFrames == 1) SetAudioStatus($"hearing {_session.MachineName}");
            _audio.Feed(pcm.Span);
        }
        else if (frame.Span[0] == BinKind.FileChunk)
        {
            var (id, off, data) = Bin.ParseFileChunk(frame);

            // A whole-tree transfer reuses this frame with an id of "<setId>#<entry index>".
            int hash = id.IndexOf('#');
            if (hash > 0)
            {
                var setId = id[..hash];
                if (!int.TryParse(id[(hash + 1)..], out var index)) return;
                if (!_incomingSets.TryGetValue(setId, out var rx)) return;
                ClipboardFetchAlive(setId);
                if (rx.Chunk(index, off, data)) FinishIncomingSet(setId);
                return;
            }

            if (_incoming.TryGetValue(id, out var fs)) { try { fs.Position = off; fs.Write(data.Span); } catch { } }
        }
    }

    private void SwitchMonitor()
    {
        if (_monitorBox.SelectedIndex < 0 || _monitorBox.SelectedIndex >= _monitors.Count)
        {
            Trace.Write($"form: SwitchMonitor ignored (selected={_monitorBox.SelectedIndex}, monitors={_monitors.Count})");
            return;
        }
        var old = _view.MonitorIndex;
        if (old != _monitorBox.SelectedIndex) _link.Send(new SubscribeMsg { Monitor = old, On = false });
        var mi = _monitors[_monitorBox.SelectedIndex];
        _view.MonitorIndex = mi.Index;
        // Remembered so a reconnect can put it back. The server replays the monitor LIST to a
        // viewer that comes back and nothing else — not which one of them was being watched.
        _wantMonitor = mi.Index;
        _view.Reset(mi.Width, mi.Height);
        Trace.Write($"form: SwitchMonitor -> slot {_monitorBox.SelectedIndex}, view.MonitorIndex={mi.Index}, canvas {mi.Width}x{mi.Height}; sending SubscribeMsg(Monitor={mi.Index}, On=true)");
        Subscribe(mi.Index);
        _link.Send(new QualityMsg { Jpeg = _quality.Value, Fps = 12 });
    }

    /// <summary>
    /// Subscribes to a monitor, saying what this console can decode.
    ///
    /// Every subscribe goes through here so the codec offer cannot be forgotten in one of the
    /// three places that subscribe — first connect, monitor switch and reconnect — which is
    /// exactly the sort of omission that produces a session that is fast on Monday and slow on
    /// Tuesday for no visible reason.
    ///
    /// <see cref="_canDecodeH264"/> starts true and is turned off for the rest of the session the
    /// first time the decoder refuses to start on this machine. Offering a codec we cannot decode
    /// is worse than not offering it: the host would send a perfect stream into a black window.
    /// </summary>
    private void Subscribe(int monitor)
    {
        var codecs = new List<string>();
        if (_canDecodeH264) codecs.Add(VideoCodec.H264);
        codecs.Add(VideoCodec.JpegTiles);
        _link.Send(new SubscribeMsg { Monitor = monitor, On = true, Codecs = codecs });
    }

    private bool _canDecodeH264 = true;

    // ---------------------------------------------------------------- system actions
    // The host has handled SysActionMsg since it was written and nothing in the solution had
    // ever sent one. The toolbar's old Ctrl+Alt+Del button synthesised the three keystrokes with
    // KeyMsg, which cannot work and never could: Windows reserves the secure attention sequence
    // and discards a synthesised one silently — the same shape of silent failure as UIPI in §4
    // of the handover. It goes through SysActionMsg now, where the host calls SendSAS as SYSTEM.

    private void SendSysAction(string action)
    {
        if (!Alive($"send {action}")) return;
        Trace.Write($"form: sending SysActionMsg({action})");
        _link.Send(new SysActionMsg { Action = action });
        SetTransferStatus(action switch
        {
            "ctrl_alt_del" => $"sent Ctrl+Alt+Del to {_session.MachineName}",
            "task_manager" => $"opening Task Manager on {_session.MachineName}",
            "lock" => $"asked {_session.MachineName} to lock",
            _ => $"sent {action} to {_session.MachineName}"
        });
    }

    /// <summary>
    /// Restart the machine. Confirmed first, because it is irreversible from this end and the
    /// person at the machine may be halfway through a sentence in a document — and the confirm
    /// is also where the console says honestly whether it will be able to get back in.
    /// </summary>
    private void RebootClicked()
    {
        if (!Alive("restart the machine")) return;
        var back = _session.Unattended
            ? "This window will wait for it and reconnect on its own when it comes back."
            : "This machine has no unattended agent, so nothing can host a session until somebody "
              + "is at it to accept one. This window cannot reconnect by itself.";
        var r = MessageBox.Show(this,
            $"Restart {_session.MachineName}?\n\nAnything unsaved on that machine will be lost.\n\n{back}",
            "Restart the machine", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;

        Trace.Write($"form: reboot confirmed for {_session.MachineName} (unattended={_session.Unattended})");
        _link.Send(new SysActionMsg { Action = "reboot" });
        BeginRebootWait();
    }

    private void SendChat()
    {
        var t = _chatInput.Value.Trim(); if (t.Length == 0) return;
        _link.Send(new ChatMsg { Text = t, FromTech = true });
        _chatInput.Value = "";
    }

    private void AppendChat(ChatMsg m)
    {
        // A message from the customer has to be SEEN, and since 3.5.4 the chat log is a window
        // that may be shut. Bring it up — without taking the focus, because the technician may be
        // typing on the customer's machine at that moment and swallowing their keystrokes into a
        // chat box is a worse fault than a missed message. Their own messages do not reopen it:
        // they were typed in the window, so it was open.
        if (!m.FromTech) ShowTool(ChatToolIndex, activate: false);

        _chatLog.SelectionStart = _chatLog.TextLength;
        _chatLog.SelectionColor = _t.Muted; _chatLog.SelectionFont = _t.Small;
        _chatLog.AppendText($"{m.Ts.ToLocalTime():HH:mm} ");
        _chatLog.SelectionColor = m.FromTech ? _t.Grape : Color.FromArgb(0x0F, 0x76, 0x5C); _chatLog.SelectionFont = _t.BodyBold;
        _chatLog.AppendText($"{m.From}: ");
        _chatLog.SelectionFont = _t.Body; _chatLog.SelectionColor = _t.Ink;
        _chatLog.AppendText(m.Text + "\n"); _chatLog.ScrollToCaret();
    }

    private void ShowFileList(FileListMsg fl)
    {
        // The listing we asked for on the technician's behalf must never be the first thing they
        // see fail. A machine at the logon screen, or one running a host too old to know what
        // %DESKTOP% is, answers with an error; fall back to the drives, which cannot fail, and say
        // so on the status bar rather than silently showing something other than what was asked.
        bool wasAutomatic = _filesOpeningOnDesktop;
        _filesOpeningOnDesktop = false;
        if (wasAutomatic && fl.Error != null)
        {
            Trace.Write($"files: {RemotePaths.Desktop} would not list ({fl.Error}); falling back to the drives");
            SetTransferStatus($"could not open {_session.MachineName}'s desktop ({fl.Error}) — showing its drives");
            _pathBox.Value = "";
            _link.Send(new FileListRequestMsg { Path = "" });
            return;
        }

        _pathBox.Value = fl.Path;
        _fileList.Items.Clear();
        if (fl.Error != null) { _fileList.Items.Add(new ListViewItem(new[] { "[Error] " + fl.Error, "", "" })); return; }
        foreach (var e in fl.Entries.OrderByDescending(x => x.IsDir).ThenBy(x => x.Name))
        {
            var it = new ListViewItem(new[] { (e.IsDir ? "📁 " : "") + e.Name, e.IsDir ? "" : Human(e.Size), e.Modified == default ? "" : e.Modified.ToString("g") }) { Tag = e };
            _fileList.Items.Add(it);
        }
    }

    private void OpenSelected()
    {
        if (_fileList.SelectedItems.Count == 0) return;
        if (_fileList.SelectedItems[0].Tag is FileEntry e && e.IsDir)
        {
            var basePath = string.IsNullOrEmpty(_pathBox.Value) ? e.Name : Path.Combine(_pathBox.Value, e.Name);
            _link.Send(new FileListRequestMsg { Path = basePath });
        }
    }

    /// <summary>
    /// A drag started in the file list: fetch what was grabbed, then hand the shell the paths.
    ///
    /// The order cannot be the other way round. DoDragDrop gives the drop target a CF_HDROP — a
    /// list of paths — and Explorer copies from those paths the instant the button comes up, so
    /// anything still in flight arrives as a failed copy with no useful error. So the bytes come
    /// first, exactly as they do for a paste, and the drag begins once they are on this machine.
    ///
    /// Which means waiting, with the mouse button held, while the fetch runs. Three files took
    /// 280 ms on real hardware, so for ordinary support work this is not felt; for something large
    /// the technician will let go first, and that case is handled rather than left to look broken
    /// — the files are already here, so they go onto the clipboard and the status bar says so.
    /// Nothing that was fetched is ever lost to a mistimed gesture.
    /// </summary>
    /// <summary>
    /// Text in a drop, or null. Unicode first, because the plain Text format is the ANSI code page
    /// and turns "café" into "caf?" on the way. Files win over text when a drop carries both.
    /// Public so tools/toolbarprobe can check it without a real drag.
    /// </summary>
    public static string? DroppedText(IDataObject? data)
    {
        if (data == null || data.GetDataPresent(DataFormats.FileDrop)) return null;
        var t = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        return string.IsNullOrEmpty(t) ? null : t;
    }

    /// <summary>
    /// A drag on the picture has left this window with the button still down (3.5.2). Clint's rule
    /// of 2026-09-18: <b>the files selected in the folder window in front of them, and only
    /// that</b>; anything else is said plainly, never a drag that silently does nothing.
    ///
    /// Their drag is left ALONE unless there are files to take. Moving a window across their
    /// screen and overshooting the edge is an ordinary thing to do, and cancelling it with Escape
    /// on a guess would undo their move. So: wait briefly for the answer asked for when the drag
    /// began; if it names files, cancel the drag on their side and start one here from those
    /// paths; otherwise say why in the status bar and hand the gesture back unchanged.
    /// </summary>
    /// <returns>True when the drag was taken over here.</returns>
    private bool PictureDragLeft()
    {
        if (_dragOutBusy || _selectionAsked.Length == 0) return false;
        var asked = _selectionAsked;
        // Usually already here: it was asked for at the first movement. The helper that reads it
        // is a process started on their machine, so allow it a moment, with the button held.
        var until = DateTime.UtcNow.AddSeconds(3);
        using (UiWatchdog.Modal("waiting for their machine to say what is selected, with the drag held"))
            while (_selection?.RequestId != asked && DateTime.UtcNow < until && !IsDisposed
                   && (ScreenView.ButtonsNow() & MouseButtons.Left) != 0)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }

        var sel = _selection?.RequestId == asked ? _selection : null;
        if ((ScreenView.ButtonsNow() & MouseButtons.Left) == 0) return false;   // they let go; nothing to carry
        if (sel == null)
        {
            SetTransferStatus($"{_session.MachineName} did not say what is selected — use Tools → Files to fetch it");
            Trace.Write("drag-off: no answer about the selection in 3 s; leaving their drag alone");
            return false;
        }
        if (sel.Paths.Count == 0)
        {
            SetTransferStatus("Nothing to drag here: " + sel.Why);
            Trace.Write($"drag-off: nothing to take ({sel.Where}) — {sel.Why}");
            return false;
        }

        Trace.Write($"drag-off: {sel.Paths.Count} item(s) selected {sel.Where} in {sel.Folder}; taking the drag over");
        _view.CancelRemoteDrag();
        BeginDragOut(sel.Paths, _view);
        return true;
    }

    /// <remarks>
    /// Two places start one (3.5.2): a drag in the Files window list, and a drag OFF the picture of
    /// their screen, which brings the paths selected in their front Explorer window. The drag is
    /// begun from <paramref name="source"/>, the control the mouse went down on.
    /// </remarks>
    private void BeginDragOut(List<string> picked, Control source)
    {
        if (_dragOutBusy) return;   // ItemDrag can fire again while the nested loop below runs
        if (picked.Count == 0) return;

        _dragOutBusy = true;
        var staging = "";
        try
        {
            staging = DragOut.NewStagingDir(_session.MachineName);
            Trace.Write($"drag-out: {picked.Count} item(s) -> {staging}");
            SetTransferStatus($"fetching {picked.Count} item(s) from {_session.MachineName} — hold the mouse button…");

            var pull = PullFileSet(picked, thenOnMyClipboard: false, into: staging);

            // A nested pump rather than an await: the drag has to begin from inside this handler,
            // while Windows still considers the mouse to be down. Returning and resuming later
            // would give the shell a gesture that had already ended.
            // Sixty seconds was far too long to hold the window in a nested loop for a gesture
            // nobody is still making. Fifteen is longer than any fetch this is for — a log, a
            // screenshot, a backup — and short enough that a technician who let go does not sit
            // looking at a window that beeps at them.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            using (UiWatchdog.Modal("the nested pump that holds a drag open while its files are fetched"))
            {
                while (!pull.IsCompleted && DateTime.UtcNow < deadline && !IsDisposed)
                {
                    Application.DoEvents();
                    Thread.Sleep(15);
                    // Nothing to hold open any more. Finish the fetch on the ordinary path rather
                    // than keeping the window unusable for a drop that can no longer happen.
                    if (Control.MouseButtons == MouseButtons.None) break;
                }
            }

            if (!pull.IsCompleted)
            {
                // Still coming. The gesture is over — either the button went up or the wait ran
                // out — but the FILES are not in question and a technician who asked for them
                // must not have to ask twice. Let it finish on its own and put them on the
                // clipboard when they land, which is what a refused drop does anyway.
                SetTransferStatus($"still fetching from {_session.MachineName} — they will be on your clipboard when they arrive");
                Trace.Write("drag-out: the gesture ended before the fetch did; finishing it without holding the window");
                var dropped = pull;
                _ = dropped.ContinueWith(t =>
                {
                    try
                    {
                        BeginInvoke(() =>
                        {
                            if (_dead) return;
                            var late = t.IsCompletedSuccessfully ? t.Result : null;
                            if (late == null || late.SavedPaths.Count == 0)
                            {
                                SetTransferStatus($"{_session.MachineName} did not send those files");
                                return;
                            }
                            try
                            {
                                var col = new System.Collections.Specialized.StringCollection();
                                foreach (var p in late.SavedPaths) col.Add(p);
                                using (UiWatchdog.Work("writing the clipboard: SetFileDropList after a late fetch"))
                                    Clipboard.SetFileDropList(col);
                                RememberAsAlreadySent(late.SavedPaths);
                                SetTransferStatus($"{late.SavedPaths.Count} item(s) fetched and put on your clipboard — paste them anywhere");
                            }
                            catch (Exception ex) { SetTransferStatus($"fetched into {staging}, but your clipboard refused them ({ex.GetType().Name})"); }
                        });
                    }
                    catch { }
                });
                return;
            }

            var done = pull.Result;
            if (!done.Ok && done.SavedPaths.Count == 0)
            {
                SetTransferStatus("could not fetch it: " + (done.Error ?? "no reason given"));
                return;
            }

            var data = DragOut.Build(staging, done.SavedPaths, out var why);
            if (data == null)
            {
                // This is the failure the whole class exists to prevent, so it is stated, not
                // swallowed: a drag that hands Explorer nothing looks identical to one that
                // never started.
                Trace.Write("drag-out: refused to start — " + why);
                SetTransferStatus("nothing could be dragged: " + why);
                return;
            }

            var label = done.SavedPaths.Count == 1
                ? Path.GetFileName(done.SavedPaths[0])
                : $"{done.SavedPaths.Count} items";
            SetTransferStatus($"{label} ready — drop it where you want it");

            // DoDragDrop ONLY while a button is still down.
            //
            // The comment below has been right since 2026-09-15: the technician usually lets go
            // while the fetch is running. What was wrong was calling DoDragDrop anyway. A drag
            // with no button held has no gesture to track and no release to end it, and the shell
            // sits in its modal loop with the mouse captured — a window that pumps messages, so it
            // reports itself as responding, but swallows every activation, so the taskbar beeps
            // instead of restoring it. That is the "ding" reported four times on 2026-09-18 and
            // the reason the UI watchdog stayed quiet through all of it.
            //
            // With no button down there is nothing to lose by skipping it: the fallback below is
            // exactly what a refused drop does, and the files are already here.
            DragDropEffects effect;
            if (Control.MouseButtons == MouseButtons.None)
            {
                effect = DragDropEffects.None;
                Trace.Write("drag-out: the mouse button was released during the fetch — not starting a drag that has no gesture left");
            }
            else
            {
                using (UiWatchdog.Modal("DoDragDrop, the shell's own drag loop"))
                    effect = source.DoDragDrop(data, DragDropEffects.Copy);
                Trace.Write($"drag-out: DoDragDrop returned {effect} for {done.SavedPaths.Count} path(s)");
            }

            if (effect == DragDropEffects.None)
            {
                // Almost always: the button was released while the fetch was running, so there
                // was no gesture left to carry. The files are here either way, and a technician
                // who has waited for them must not be told to do it again.
                try
                {
                    var col = new System.Collections.Specialized.StringCollection();
                    foreach (var p in done.SavedPaths) col.Add(p);
                    using (UiWatchdog.Work("writing the clipboard: SetFileDropList after a drag")) Clipboard.SetFileDropList(col);
                    RememberAsAlreadySent(done.SavedPaths);
                    SetTransferStatus($"{label} fetched — the drop did not land, so they are on your clipboard, ready to paste");
                }
                catch
                {
                    SetTransferStatus($"{label} fetched into {staging} — the drop did not land");
                }
            }
            else SetTransferStatus($"{label} copied off {_session.MachineName}");
        }
        catch (Exception ex)
        {
            Trace.Write($"drag-out: failed — {ex.GetType().Name}: {ex.Message}");
            SetTransferStatus("the drag failed: " + ex.Message);
        }
        finally { _dragOutBusy = false; }
    }

    /// <summary>
    /// What is selected in the file list, as absolute paths ON THE CUSTOMER'S MACHINE.
    ///
    /// The drives view has an empty path and entries that are already roots ("C:\"), so combining
    /// them would produce nonsense; and a listing opened on %DESKTOP% has had the sentinel
    /// replaced by the real folder in the reply, so by here there is never a sentinel to join to.
    /// </summary>
    private List<string> SelectedRemotePaths()
    {
        var list = new List<string>();
        foreach (ListViewItem it in _fileList.SelectedItems)
        {
            if (it.Tag is not FileEntry e) continue;   // the "[Error] …" row has no tag
            var bas = _pathBox.Value;
            list.Add(string.IsNullOrEmpty(bas) ? e.Name : Path.Combine(bas, e.Name));
        }
        return list;
    }

    private void DownloadSelected()
    {
        if (_fileList.SelectedItems.Count == 0) return;
        if (_fileList.SelectedItems[0].Tag is FileEntry e && !e.IsDir)
        {
            var full = Path.Combine(_pathBox.Value, e.Name);
            _link.Send(new FilePullMsg { Path = full, FileId = Guid.NewGuid().ToString("N") });
        }
    }

    private void BeginIncoming(FileOfferMsg fo)
    {
        try
        {
            Directory.CreateDirectory(_downloads);
            var path = Path.Combine(_downloads, Path.GetFileName(fo.Name));
            int n = 1; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
            while (File.Exists(path)) path = Path.Combine(_downloads, $"{stem} ({n++}){ext}");
            _incoming[fo.FileId] = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            _paths[fo.FileId] = path;
            ShowTool(ScriptsToolIndex, activate: false);
            AppendScript(new ScriptOutputMsg { Text = $"[file] receiving {fo.Name} ({Human(fo.Size)})…\n" });
        }
        catch (Exception ex) { AppendScript(new ScriptOutputMsg { Text = "[file] " + ex.Message + "\n", IsError = true }); }
    }

    private void FinishIncoming(FileDoneMsg fd)
    {
        if (_incoming.TryGetValue(fd.FileId, out var fs))
        {
            var path = _paths.TryGetValue(fd.FileId, out var p) ? p : "";
            try { fs.Flush(); fs.Dispose(); } catch { }
            _incoming.Remove(fd.FileId); _paths.Remove(fd.FileId);
            if (fd.Ok) AppendScript(new ScriptOutputMsg { Text = $"[file] saved to {path}\n" });
            else AppendScript(new ScriptOutputMsg { Text = $"[file] failed: {fd.Error}\n", IsError = true });
        }
        else if (fd.Ok) AppendScript(new ScriptOutputMsg { Text = $"[file] delivered to client: {fd.SavedPath}\n" });
        else AppendScript(new ScriptOutputMsg { Text = $"[file] failed: {fd.Error}\n", IsError = true });
    }

    // ---- whole trees: files, folders, and folders inside folders --------------------------------
    //
    // Both directions run through the same FileSetSender/FileSetReceiver as the host uses, so
    // there is only one idea of what an empty folder, a locked file or a duplicate name does.

    /// <summary>
    /// Sends files and folders to the customer. Used by a paste, by a drop onto the session view,
    /// and by the Send button.
    ///
    /// <paramref name="thenPutOnTheirClipboard"/> is what makes paste work rather than merely
    /// copying: once the bytes are on the customer's machine the host is told to put the landed
    /// paths on their clipboard, so the customer (or the technician driving them) can paste into
    /// Explorer and get the files.
    /// </summary>
    /// <summary>
    /// Walks the selection and then sends it. The walk is OFF the UI thread, always.
    ///
    /// The freeze of 2026-09-18 was fixed here once and only half of it went: the measuring in
    /// <see cref="OfferMyFilesToCustomer"/> moved to a background thread, and then this method
    /// walked the very same tree again, inline, on the thread that had just been freed. The
    /// console went back to beeping at the taskbar the same afternoon.
    ///
    /// The clipboard path does not come through here at all any more. It has already walked the
    /// selection to decide whether it was small enough to send, so it hands that plan straight to
    /// <see cref="PushPlannedFileSet"/> — one walk per copy rather than two, and none of them here.
    /// </summary>
    private void PushFileSet(IEnumerable<string> paths, bool thenPutOnTheirClipboard, string? targetDir = null)
    {
        var list = paths.ToList();
        SetTransferStatus($"preparing {list.Count} item(s)…");
        _ = Task.Run(() =>
        {
            FileSetPlan plan;
            try { plan = FileSetSender.Plan(list); }
            catch (Exception ex)
            {
                try { BeginInvoke(() => { ForgetLastSentFiles("it could not be read"); SetTransferStatus("could not read what you dropped: " + ex.Message); }); } catch { }
                return;
            }
            try { BeginInvoke(() => PushPlannedFileSet(plan, thenPutOnTheirClipboard, targetDir)); } catch { }
        });
    }

    /// <summary>Sends a selection that has ALREADY been walked. UI thread, and no file system
    /// work of its own — see <see cref="PushFileSet"/> for why that matters.</summary>
    private void PushPlannedFileSet(FileSetPlan plan, bool thenPutOnTheirClipboard, string? targetDir = null)
    {
        if (_dead) return;
        if (plan.Entries.Count == 0)
        {
            ForgetLastSentFiles("there was nothing in it to send");
            SetTransferStatus("nothing to send" + (plan.Skipped.Count > 0 ? $" — {string.Join(", ", plan.Skipped.Take(3))}" : ""));
            return;
        }

        // A PASTE stages under the customer's temp; a SEND lands in a folder.
        //
        // This is the difference between the two things this method does, and getting it wrong is
        // invisible until it works. A clipboard file list is paths, so a paste has to put the
        // bytes somewhere on their disk first — and if that somewhere is their Downloads, then
        // pasting three files onto their desktop leaves three on the desktop AND three in
        // Downloads that nobody asked for. Reported on 2026-09-18, after a fresh install, as
        // "they ended up in the downloads folder instead of the desktop".
        //
        // Staging is the answer the other direction already reached: DragOut.StagingRoot puts
        // fetched files under %TEMP% because they are a byproduct of a gesture, and Explorer
        // copies them out on the drop. Paste is that same gesture pointing the other way.
        //
        // An explicit targetDir always wins — a drag onto the picture of their desktop means
        // their desktop, and that is a destination somebody chose rather than a staging post.
        targetDir ??= thenPutOnTheirClipboard ? RemotePaths.PasteStaging : null;

        var setId = Guid.NewGuid().ToString("N");
        _outgoingSets[setId] = (plan, thenPutOnTheirClipboard);
        // Recorded HERE, with the offer, not when the bytes finish. The confirmation can beat
        // a continuation queued after the last chunk, and for a small file it reliably does.
        if (thenPutOnTheirClipboard) _pasteSets.Add(setId);
        SetTransferStatus($"offering {plan.Label}: {plan.FileCount} file(s), {FileSetLimits.Human(plan.TotalBytes)}…", 0, plan.TotalBytes);
        Trace.Write($"fileset {setId}: offering {plan.Entries.Count} entries, {plan.TotalBytes} bytes, target={targetDir ?? "(their Downloads)"}");
        // A null target leaves it to the host, which puts it in the logged-on user's Downloads —
        // never SYSTEM's profile, which is where it would land if this end guessed a path.
        _link.Send(new FileSetOfferMsg
        {
            SetId = setId, Entries = plan.Entries, TotalBytes = plan.TotalBytes, Label = plan.Label, TargetDir = targetDir
        });

        // If the customer's machine never answers, say so rather than leaving "offering…" up for
        // ever. An older client ignores the message entirely, exactly as it does for clipboard.
        var pending = setId;
        var giveUp = new System.Windows.Forms.Timer { Interval = 20000 };
        giveUp.Tick += (_, _) =>
        {
            giveUp.Stop(); giveUp.Dispose();
            if (!_outgoingSets.ContainsKey(pending)) return;
            _outgoingSets.Remove(pending); _pasteSets.Remove(pending);
            ForgetLastSentFiles("the customer's machine never answered");
            SetTransferStatus("no answer from " + _session.MachineName + " — it may be running an older client");
        };
        giveUp.Start();
    }

    /// <summary>The customer accepted (or refused) a tree we offered. Only now is it safe to stream.</summary>
    private void OnFileSetAccepted(FileSetAcceptMsg a)
    {
        if (!_outgoingSets.TryGetValue(a.SetId, out var pending)) return;
        if (!a.Accept)
        {
            _outgoingSets.Remove(a.SetId); _pasteSets.Remove(a.SetId);
            ForgetLastSentFiles("the customer's machine refused it");
            SetTransferStatus("the customer's machine refused it: " + (a.Error ?? "no reason given"));
            return;
        }

        var (plan, setClipboard) = pending;
        SetTransferStatus($"sending {plan.Label} to {a.TargetDir}…");
        _ = Task.Run(async () =>
        {
            try
            {
                var last = DateTime.UtcNow;
                await FileSetSender.SendAsync(a.SetId, plan, b => _link.SendBinary(b), (sent, files, current) =>
                {
                    if ((DateTime.UtcNow - last).TotalMilliseconds < 400) return;
                    last = DateTime.UtcNow;
                    try
                    {
                        BeginInvoke(() => SetTransferStatus(
                            $"sending {plan.Label}: {files}/{plan.FileCount} files, " +
                            $"{FileSetLimits.Human(sent)} of {FileSetLimits.Human(plan.TotalBytes)}",
                            sent, plan.TotalBytes));
                    }
                    catch { }
                }, _cts.Token);

                try
                {
                    // NOT the place to remember that this was a paste. That is recorded when the
                    // offer goes out, because this callback races the answer — see _pasteSets.
                    BeginInvoke(() =>
                        SetTransferStatus($"sent {plan.Label}; waiting for the customer's machine to confirm…"
                            + (plan.Skipped.Count > 0 ? $"  ({plan.Skipped.Count} could not be read here)" : "")));
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { BeginInvoke(() => { ForgetLastSentFiles("the send failed"); SetTransferStatus("send failed: " + ex.Message); }); } catch { }
            }
            finally { _outgoingSets.Remove(a.SetId); }
        });
    }

    /// <summary>A tree arriving from the customer, because we asked for it.</summary>
    private void OnFileSetOffered(FileSetOfferMsg o)
    {
        try
        {
            var rx = new FileSetReceiver(o);
            // Everything from a customer machine lands under one dated folder per session, so a
            // technician can find what they pulled without hunting through Downloads — unless the
            // pull was asked for with somewhere else in mind, which is what a drag does.
            _pullIntents.TryGetValue(o.SetId, out var intent);
            var into = intent?.Into ?? Path.Combine(_downloads, "Remote Support",
                                    $"{_session.MachineName} {DateTime.Now:yyyy-MM-dd HHmm}");
            if (!rx.Begin(into, out var why))
            {
                rx.Dispose();
                _link.Send(new FileSetAcceptMsg { SetId = o.SetId, Accept = false, Error = why });
                SetTransferStatus("could not receive it: " + why);
                if (_pullIntents.Remove(o.SetId, out var refused))
                    refused.Finished?.TrySetResult(new FileSetDoneMsg { SetId = o.SetId, Ok = false, Error = why });
                ClipboardFetchEnded(o.SetId, landed: false);
                return;
            }
            _incomingSets[o.SetId] = rx;
            ClipboardFetchAlive(o.SetId);
            _link.Send(new FileSetAcceptMsg { SetId = o.SetId, Accept = true, TargetDir = rx.TargetDir });
            SetTransferStatus($"receiving {o.Label}: {rx.FilesTotal} file(s), {FileSetLimits.Human(o.TotalBytes)} into {rx.TargetDir}");
            Trace.Write($"fileset {o.SetId}: receiving {o.Entries.Count} entries into {rx.TargetDir}");
        }
        catch (Exception ex)
        {
            _link.Send(new FileSetAcceptMsg { SetId = o.SetId, Accept = false, Error = ex.Message });
            SetTransferStatus("could not receive it: " + ex.Message);
        }
    }

    private void FinishIncomingSet(string setId)
    {
        if (!_incomingSets.TryGetValue(setId, out var rx)) return;
        _incomingSets.Remove(setId);
        var done = rx.Done();
        _link.Send(done);
        _pullIntents.Remove(setId, out var intent);

        // Put what arrived on the technician's own clipboard, so Ctrl+V in Explorer works. This
        // is the half that turns "the files downloaded" into "paste worked". A drag says no: it
        // is about to hand these same paths to the shell itself, and overwriting the clipboard
        // on the way past would be a side effect of a gesture that never mentioned it.
        string onward = "";
        bool onClipboard = false;
        try
        {
            if (done.SavedPaths.Count > 0 && intent is not { OnMyClipboard: false })
            {
                var col = new System.Collections.Specialized.StringCollection();
                foreach (var p in done.SavedPaths) col.Add(p);
                using (UiWatchdog.Work("writing the clipboard: SetFileDropList after a fetch")) Clipboard.SetFileDropList(col);
                RememberAsAlreadySent(done.SavedPaths);
                onward = " — and on your clipboard, ready to paste";
                onClipboard = true;
            }
        }
        catch (Exception ex) { onward = $" — but your clipboard refused them ({ex.GetType().Name})"; }
        // Another session window may be waiting to paste these on its own machine (SessionForm.Between.cs).
        ClipboardFetchEnded(setId, onClipboard);

        var skipped = done.Skipped.Count > 0 ? $"  {done.Skipped.Count} item(s) could not be copied" : "";
        // A drag reports itself once the shell has the files; saying "received into <temp>" here
        // would name a folder the technician never chose and will never look in.
        if (intent is not { OnMyClipboard: false })
            SetTransferStatus($"received into {done.SavedDir}{onward}{skipped}");
        intent?.Finished?.TrySetResult(done);
        if (done.Skipped.Count > 0)
        {
            ShowTool(ScriptsToolIndex, activate: false);
            AppendScript(new ScriptOutputMsg { Text = "[files] could not be copied:\n", IsError = true });
            foreach (var s in done.Skipped.Take(20)) AppendScript(new ScriptOutputMsg { Text = "  " + s + "\n", IsError = true });
        }
    }

    /// <summary>
    /// Asks the customer for these paths on their machine, folders and all.
    ///
    /// Returns a task that completes when the tree has actually landed here, so a caller that has
    /// to do something with the bytes — a drag, which cannot hand the shell paths to files that do
    /// not exist yet — can wait for them rather than hope.
    /// </summary>
    private Task<FileSetDoneMsg> PullFileSet(IEnumerable<string> remotePaths, bool thenOnMyClipboard, string? into = null)
    {
        var list = remotePaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var tcs = new TaskCompletionSource<FileSetDoneMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (list.Count == 0)
        {
            SetTransferStatus("nothing to fetch");
            tcs.SetResult(new FileSetDoneMsg { Ok = false, Error = "nothing to fetch" });
            return tcs.Task;
        }
        var setId = Guid.NewGuid().ToString("N");
        _pullIntents[setId] = new PullIntent { Into = into, OnMyClipboard = thenOnMyClipboard, Finished = tcs };
        // Bound for this PC's clipboard: another session window may be waiting to paste it (SessionForm.Between.cs).
        if (thenOnMyClipboard && into == null) StartedClipboardFetch(setId);
        SetTransferStatus($"asking {_session.MachineName} for {list.Count} item(s)…");
        _link.Send(new FileSetPullMsg { SetId = setId, Paths = list });

        // An older host, or one that is not there, would otherwise leave the caller waiting for
        // ever — and a drag waiting for ever is a console that looks hung.
        var giveUp = new System.Windows.Forms.Timer { Interval = 40000 };
        giveUp.Tick += (_, _) =>
        {
            giveUp.Stop(); giveUp.Dispose();
            if (!_pullIntents.Remove(setId, out var intent)) return;
            if (!_incomingSets.ContainsKey(setId)) ClipboardFetchEnded(setId, landed: false);
            SetTransferStatus($"no answer from {_session.MachineName} — it may be running an older client");
            intent.Finished?.TrySetResult(new FileSetDoneMsg { SetId = setId, Ok = false, Error = "no answer from the machine" });
        };
        giveUp.Start();
        return tcs.Task;
    }

    /// <summary>
    /// One place for every transfer message, shown on the status bar where it is visible whatever
    /// tab is selected, and echoed into the trace.
    /// </summary>
    private void SetTransferStatus(string text, long done = -1, long total = -1)
    {
        // Any real transfer message supersedes an offer: whatever is now on screen is no longer
        // the sentence the click was attached to, and a click that does something other than what
        // it says is worse than no click at all.
        _pendingSend = null;
        _pendingFetch = null;
        _pendingBridgeFrom = null;
        _pendingViaPcFrom = null;
        _xferLabel.Cursor = Cursors.Default;
        _xferLabel.ForeColor = _t.Ink;

        _xferLabel.Text = text;
        _xferLabel.Visible = true;

        if (done >= 0 && total > 0)
        {
            _xferBar.Visible = true;
            _xferBar.Value = (int)Math.Clamp(done * 1000 / total, 0, 1000);
        }
        else if (done < 0) _xferBar.Visible = false;

        Trace.Write("fileset: " + text);
        // Leave the last message on screen rather than blanking it the moment a transfer ends —
        // a transfer that finishes in half a second would otherwise look like nothing happened.
        _xferIdle.Stop();
        _xferIdle.Start();
    }

    /// <summary>
    /// Shows an offer that waits to be clicked, rather than a report of something that happened.
    ///
    /// It looks different from an ordinary message and it does not time out, because the twenty
    /// second idle that suits "sent, 3 files" would make an offer vanish while it was being read.
    /// </summary>
    private void OfferOnStatusBar(string text)
    {
        _xferLabel.Text = text;
        _xferLabel.Visible = true;
        _xferLabel.Cursor = Cursors.Hand;
        _xferLabel.ForeColor = _t.Accent;
        _xferBar.Visible = false;
        _xferIdle.Stop();
        Trace.Write("offer: " + text);
    }

    /// <summary>Takes whichever offer the status bar is showing. Harmless when there is none.</summary>
    private void TakePendingOffer()
    {
        // A copy to send straight here from the other window's machine, or through this PC after one failed.
        if (TakeBridgeOffer()) return;
        var send = _pendingSend;
        var fetch = _pendingFetch;
        if (send == null && fetch == null) return;
        // "Bring them to this PC": no longer held for the other window — they come here, and from here as before.
        if (fetch != null) _copiedHere = null;

        // Cleared through SetTransferStatus below, but cleared here as well so a double click
        // cannot start the same transfer twice while the first message is still being built.
        _pendingSend = null;
        _pendingFetch = null;

        if (send != null) { SetTransferStatus($"sending {send.Count} item(s) to {_session.MachineName}…"); PushFileSet(send, thenPutOnTheirClipboard: true); }
        else { SetTransferStatus($"fetching {fetch!.Count} item(s) from {_session.MachineName}…"); PullFileSet(fetch, thenOnMyClipboard: true); }
    }

    /// <summary>"3 files", or "2 items" when folders are in it and the file count would mislead.</summary>
    private static string Describe(int files, int roots) =>
        roots == 1 && files == 1 ? "1 file"
        : files >= roots ? $"{files} file{(files == 1 ? "" : "s")}"
        : $"{roots} item{(roots == 1 ? "" : "s")}";

    private void UploadFile()
    {
        using var ofd = new OpenFileDialog { Title = "Send a file to the client machine" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        var fileId = Guid.NewGuid().ToString("N");
        var targetDir = string.IsNullOrWhiteSpace(_pathBox.Value) ? null : _pathBox.Value;
        _ = Task.Run(async () =>
        {
            try
            {
                var fi = new FileInfo(ofd.FileName);
                BeginInvoke(() => { ShowTool(ScriptsToolIndex, activate: false); AppendScript(new ScriptOutputMsg { Text = $"[file] sending {fi.Name} ({Human(fi.Length)})…\n" }); });
                _link.Send(new FileOfferMsg { FileId = fileId, Name = fi.Name, Size = fi.Length, TargetDir = targetDir });
                await using var fs = File.OpenRead(ofd.FileName);
                var buf = new byte[64 * 1024]; long off = 0; int r;
                // Waits for room rather than losing a chunk: nothing acknowledges or re-sends one,
                // so a chunk dropped here is a file that can never complete at the far end. See
                // BUGS #8.
                while ((r = await fs.ReadAsync(buf)) > 0)
                {
                    if (!await FileSetSender.SendChunkAsync(b => _link.SendBinary(b), Bin.FileChunk(fileId, off, buf.AsSpan(0, r)), CancellationToken.None))
                        throw new IOException("the link would not carry the whole file");
                    off += r; await Task.Delay(1);
                }
            }
            catch (Exception ex) { BeginInvoke(() => AppendScript(new ScriptOutputMsg { Text = "[file] " + ex.Message + "\n", IsError = true })); }
        });
    }

    private void RunScript()
    {
        var script = _scriptBox.Text; if (script.Trim().Length == 0) return;
        _currentRunId = Guid.NewGuid().ToString("N");
        _scriptOut.AppendText($"\n> Running {_scriptLang.SelectedItem} on {_session.MachineName}…\n");
        // Sealed end to end, the server cannot read the script to write its ticket line, so the line goes beside it
        // (2026-10-07). Unsealed, the server reads the script itself, as before — never both, or the ticket says it twice.
        if (_link.Keyed) _link.Send(new ScriptAuditMsg { Language = (string)_scriptLang.SelectedItem!, FirstLine = script.Split('\n').FirstOrDefault()?.Trim() ?? "" });
        _link.Send(new ScriptRunMsg { RunId = _currentRunId, Language = (string)_scriptLang.SelectedItem!, Script = script });
    }

    private void AppendScript(ScriptOutputMsg so)
    {
        _scriptOut.SelectionStart = _scriptOut.TextLength;
        _scriptOut.SelectionColor = so.IsError ? Color.Salmon : _t.ConsoleText;
        _scriptOut.AppendText(so.Text);
        _scriptOut.ScrollToCaret();
    }

    private static string Human(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b / 1024.0:0.#} KB" : b < 1073741824 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    // ================================================================ surviving a dropped socket
    //
    // The console used to put a "○ reconnecting" pill on screen at the exact moment SessionLink
    // gave up for good: it promised a recovery that nothing performed. Everything below is that
    // promise being kept, and the pill only ever says "reconnecting" while something is actually
    // trying.
    //
    // The measurement that shaped it: the SERVER used to end the session the instant the last
    // viewer's socket closed, so a retry loop here could never have worked on its own. The grace
    // period in TenantHub.BeginGrace is the other half, and neither half is any use alone.

    private Panel _banner = null!;
    private Label _bannerText = null!;
    private RoundButton _bannerBtn = null!, _bannerClose = null!, _bannerOk = null!;
    /// <summary>The "hosted by the support app" notice has been shown for this session.</summary>
    private string _appNoticeFor = "";
    private RoundButton _sysBtn = null!;
    private ContextMenuStrip _sysMenu = null!;
    private ToolStripMenuItem _privacyItem = null!;

    /// <summary>
    /// The free version (2026-10-06): Tools keeps Files and the two printing items — <see cref="FreePlan.Tools"/>,
    /// what the hub lets through (<see cref="FreePlan.MaySend"/>) — and System loses the privacy screen. The
    /// hub drops anything else whatever this shows; hiding it means nobody clicks something that does nothing.
    /// </summary>
    private void KeepOnlyFreeTools()
    {
        foreach (ToolStripItem item in _toolsMenu.Items)
            item.Visible = item is not ToolStripSeparator && FreePlan.Tools.Any(t => item.Text?.StartsWith(t, StringComparison.Ordinal) == true);
        _privacyItem.Visible = false;
        _toolTip.SetToolTip(_toolsBtn, "Files, and printing both ways — each opens in its own window");
    }
    /// <summary>The privacy screen as their machine last reported it (PrivacyStateMsg).</summary>
    private bool _privacyOn;

    /// <summary>True once the technician has deliberately ended or left. A deliberate end sends
    /// an EndSessionMsg and must still end the session AT ONCE — if a dropped packet and this
    /// stayed indistinguishable, every closed window would leave a host running on a customer's
    /// machine for ninety seconds, which is a disclosure problem and not just an untidy one.</summary>
    private bool _leaving;
    /// <summary>The session underneath this window is gone and nothing is trying to get it back.</summary>
    private bool _dead;
    /// <summary>Waiting for a machine we restarted to come back.</summary>
    private bool _rebooting;
    private DateTime _rebootAskedAt;
    /// <summary>When this window last re-attached to a machine coming back from a restart. A
    /// session that dies within moments of that was a machine still on its way down, not a
    /// recovery that failed — see ResumeRestartIfStillGoingDown.</summary>
    private DateTime _rebootReattachedAt = DateTime.MinValue;
    private readonly System.Windows.Forms.Timer _rebootTick = new() { Interval = 1000 };
    /// <summary>How long to wait for a restarted machine. Clint's call: long enough for a machine
    /// that is applying updates on the way down, short enough to stop pretending eventually.</summary>
    private static readonly TimeSpan RebootWait = TimeSpan.FromMinutes(5);

    /// <summary>Monitor and quality at the moment of a drop, so they can be put back. The server
    /// replays the monitor LIST to a reconnecting viewer and nothing else — not which one was
    /// selected, not the quality, and not whether Listen was on.</summary>
    private int _wantMonitor = -1;
    private bool _wantListen;

    /// <summary>
    /// Starts a fresh session on a machine and returns it, or null. Supplied by DashboardForm,
    /// because the dashboard socket is the only place a session can be started from and this
    /// window holds a session socket. Needed twice: the Reconnect button, and a machine coming
    /// back from a reboot — in both cases the old session is genuinely over and the window is
    /// re-attached to a new one rather than thrown away with its chat and transfer history.
    /// </summary>
    public Func<string, CancellationToken, Task<SessionInfo?>>? StartFreshSession { get; set; }

    /// <summary>Guard for anything that needs a live session. Says why rather than doing nothing,
    /// because a button that silently does nothing is the thing that gets reported as broken.</summary>
    private bool Alive(string what)
    {
        if (_dead || _rebooting) { SetTransferStatus($"cannot {what} — there is no live connection to {_session.MachineName}"); return false; }
        if (!_link.Connected) { SetTransferStatus($"cannot {what} — reconnecting to {_session.MachineName}"); return false; }
        return true;
    }

    private void OnLinkConnectedChanged(SessionLink link, bool connected)
    {
        if (link != _link) return;                     // a superseded link finishing its business
        if (connected)
        {
            _view.Stale = false;
            SetPill("● connected", ok: true);
            HideBanner();
            NoticeIfHostedByApp();
            return;
        }
        if (_leaving || _dead) return;
        // Honest: something IS trying, right now. The pill never says this when nothing is.
        _view.Stale = true;
        SetPill("○ reconnecting", ok: false);
        ShowBanner($"Connection to {_session.MachineName} lost — getting back in. The session is held open for a moment.", reconnect: false, close: false);
    }

    /// <summary>
    /// Back in. The server replays the monitor list, which is what makes joining late work, but
    /// it knows nothing about what the technician had set — and MonitorsMsg no longer triggers a
    /// SubscribeMsg here because the combo box already has a selection. Without this a reconnect
    /// succeeds and the screen stays black, which is indistinguishable from a reconnect that failed.
    /// </summary>
    private void ResumeAfterReconnect(SessionLink link)
    {
        if (link != _link) return;
        var mon = _wantMonitor >= 0 ? _wantMonitor : _view.MonitorIndex;
        Trace.Write($"form: reconnected — re-subscribing to monitor {mon}, quality {_quality.Value}, listen={_wantListen}");
        Subscribe(mon);   // On=true sets _forceFull on the host, so a whole frame comes back
        _link.Send(new QualityMsg { Jpeg = _quality.Value, Fps = 12 });
        if (_wantListen && !_listening)
        {
            // Listening is a DISCLOSURE, so it is put back rather than quietly dropped — the
            // customer's banner would otherwise be the only thing in the system that still knew.
            if (_audio.Start()) { _listening = true; _meterTick.Start(); _meter.Active = _soundMeter.Active = true; SetListenLabel("Listening"); _audioLabel.Visible = true; }
            _link.Send(new AudioListenMsg { On = _listening });
            if (!_listening) SetAudioStatus("could not reopen this machine's speakers after reconnecting — press Listen again", bad: true);
        }
        ResumeVoice();
        SetTransferStatus($"reconnected to {_session.MachineName}");
    }

    private void OnLinkGaveUp(SessionLink link, bool ended, string why)
    {
        if (link != _link) return;
        if (_leaving) return;
        Trace.Write($"form: link gave up (ended={ended}) — {why}");
        if (_rebooting) { OnRebootLinkGone(); return; }
        if (ResumeRestartIfStillGoingDown("the link gave up")) return;
        MarkDead(ended ? "That session has ended." : why);
    }

    /// <summary>
    /// A session that dies within moments of re-attaching to a restarting machine was a machine
    /// still going down, not a recovery that failed. Go back to waiting rather than giving up.
    ///
    /// The settle delay before the first poll handles the ordinary case; this is what makes it
    /// right on a machine that takes its time about shutting down, where the poll can still land
    /// in the gap. Without one of the two, the window declares the restart a failure seconds
    /// after sending it, which is precisely the reported fault.
    /// </summary>
    private bool ResumeRestartIfStillGoingDown(string what)
    {
        if (DateTime.UtcNow - _rebootReattachedAt > TimeSpan.FromSeconds(25)) return false;
        if (DateTime.UtcNow - _rebootAskedAt > RebootWait) return false;
        Trace.Write($"form: {what} moments after re-attaching to a restarting machine — it was still going down; waiting again");
        _rebooting = true;
        _dead = false;
        _rebootTick.Start();
        UpdateRebootBanner();
        _ = PollForMachineBackAsync();
        return true;
    }

    private void MarkDead(string why)
    {
        _dead = true;
        _view.Stale = true;
        SetPill("○ disconnected", ok: false);
        if (_listening) StopListening("the connection to that machine ended");
        _wantListen = false;
        EndTalk("the connection to that machine ended", tell: false);
        // Not closed. The chat, the transfer history and the script output are still worth
        // reading, and a window that vanishes takes them with it. Reconnect starts a NEW session
        // on the same machine, which is the only honest thing it could do.
        ShowBanner(why, reconnect: true, close: true);
    }

    private async void ReconnectClicked()
    {
        if (StartFreshSession == null) { ShowBanner("This window cannot start a new session on its own — close it and connect from the dashboard.", reconnect: false, close: true); return; }
        _bannerBtn.Enabled = false;
        ShowBanner($"Connecting to {_session.MachineName}…", reconnect: true, close: true);
        try
        {
            var fresh = await StartFreshSession(_session.DeviceId, _cts.Token);
            if (fresh == null) { ShowBanner($"{_session.MachineName} is not reachable. It may be off, or offline.", reconnect: true, close: true); return; }
            await ReattachAsync(fresh);
        }
        catch (Exception ex) { Trace.Write("form: reconnect failed — " + ex.Message); ShowBanner("Could not start a session: " + ex.Message, reconnect: true, close: true); }
        finally { _bannerBtn.Enabled = true; }
    }

    /// <summary>
    /// Point this window at a different session on the same machine. The old link is cancelled
    /// first — two viewer sockets from one window would both count as viewers, and the stale one
    /// would keep a dead session's grace period alive.
    /// </summary>
    private async Task ReattachAsync(SessionInfo fresh, bool fromReboot = false)
    {
        // Remembered so a session that dies moments from now is recognised as a machine still
        // going down rather than a recovery that failed. See ResumeRestartIfStillGoingDown.
        if (fromReboot) _rebootReattachedAt = DateTime.UtcNow;
        Trace.Write($"form: re-attaching window from session {_session.SessionId} to {fresh.SessionId}");
        var old = _linkCts;
        _linkCts = new CancellationTokenSource();
        try { old.Cancel(); } catch { }
        await Task.Yield();
        _session = fresh;
        _dead = false; _rebooting = false; _rebootTick.Stop();
        Text = $"Session — {_session.MachineName}";
        // A new session means a new host and a fresh monitor list; let MonitorsMsg drive the
        // first SubscribeMsg the way it does on a first connect, rather than subscribing to a
        // monitor index that belonged to the previous host.
        _monitorBox.Items.Clear();
        _monitors = new List<MonitorInfo>();
        _wantMonitor = -1;
        SetPill("○ connecting", ok: false);
        _link = StartLink(fresh.SessionId);
        try { old.Dispose(); } catch { }
    }

    // ---------------------------------------------------------------- the machine restarting

    private void BeginRebootWait()
    {
        _rebooting = true;
        _rebootAskedAt = DateTime.UtcNow;
        _view.Stale = true;
        SetPill("○ restarting", ok: false);
        if (_listening) { StopListening("the machine is restarting"); _wantListen = false; }
        EndTalk("the machine is restarting", tell: false);
        _rebootTick.Start();
        UpdateRebootBanner();
    }

    private void UpdateRebootBanner()
    {
        var left = RebootWait - (DateTime.UtcNow - _rebootAskedAt);
        if (!_session.Unattended)
        {
            // Said once, plainly, rather than letting the window sit there counting down for
            // something that is not going to happen. An attended-only machine needs a person.
            ShowBanner($"{_session.MachineName} is restarting. With no unattended agent, somebody must be at it to accept.", reconnect: true, close: true);
            _rebootTick.Stop();
            _rebooting = false; _dead = true;
            return;
        }
        if (left <= TimeSpan.Zero)
        {
            _rebootTick.Stop();
            _rebooting = false;
            MarkDead($"{_session.MachineName} did not come back within {RebootWait.TotalMinutes:0} minutes.");
            return;
        }
        ShowBanner($"{_session.MachineName} is restarting — waiting for it to come back ({left.Minutes}:{left.Seconds:00} left).", reconnect: false, close: true);
    }

    /// <summary>
    /// The socket died while we were expecting it to: the machine we restarted has gone. Start
    /// polling for a session on it, because the old session went with the host and only a new
    /// one can be started.
    /// </summary>
    private void OnRebootLinkGone()
    {
        if (!_rebooting) return;
        _ = PollForMachineBackAsync();
    }

    /// <summary>
    /// How long to leave the machine alone before trying to get back in. `shutdown /r /t 5` means
    /// it is still up and still accepting connections for a good few seconds, and the agent keeps
    /// answering right up until the service stops.
    ///
    /// Without this the first poll landed FIVE SECONDS after the restart was sent, succeeded,
    /// re-attached this window to a session on a machine that was already going down, and that
    /// session died a second later — by which point ReattachAsync had cleared _rebooting, so the
    /// failure took the ordinary "dead" path and offered a Reconnect button. Measured from the
    /// session log on 2026-09-15: 08:58:43 restart, 08:58:49 session ends, 08:58:54 a new session
    /// starts, 08:58:55 it dies. That is exactly what "it does not auto reconnect" looked like.
    /// </summary>
    private static readonly TimeSpan RebootSettle = TimeSpan.FromSeconds(30);

    private async Task PollForMachineBackAsync()
    {
        if (StartFreshSession == null) { _rebooting = false; MarkDead("The machine was restarted. Connect to it again from the dashboard."); return; }
        Trace.Write($"form: waiting for {_session.MachineName} to come back from a restart (leaving it alone for {RebootSettle.TotalSeconds:0}s first)");
        try { await Task.Delay(RebootSettle, _cts.Token); } catch { return; }
        while (_rebooting && !_cts.IsCancellationRequested && DateTime.UtcNow - _rebootAskedAt < RebootWait)
        {
            try { await Task.Delay(5000, _cts.Token); } catch { return; }
            if (!_rebooting) return;
            SessionInfo? fresh = null;
            try { fresh = await StartFreshSession(_session.DeviceId, _cts.Token); } catch { }
            if (fresh != null)
            {
                Trace.Write($"form: {_session.MachineName} is back — session {fresh.SessionId}");
                try { BeginInvoke(async () => await ReattachAsync(fresh, fromReboot: true)); } catch { }
                return;
            }
        }
    }

    // ---------------------------------------------------------------- the banner and the pill

    private void SetPill(string text, bool ok) => _connPill.Set(text, ok ? _t.OkBg : _t.WarnBg, ok ? _t.OkText : _t.WarnText);

    private void ShowBanner(string text, bool reconnect, bool close)
    {
        _bannerText.Text = text;
        _toolTip.SetToolTip(_bannerText, "");
        _bannerBtn.Visible = reconnect;
        _bannerClose.Visible = close;
        _bannerOk.Visible = false;
        _banner.Visible = true;
    }

    /// <summary>A banner that only informs, dismissed with OK. The whole of it is in the tooltip,
    /// because a one-line banner on a narrow window ends in "…".</summary>
    private void ShowNotice(string text, string whole)
    {
        ShowBanner(text, reconnect: false, close: false);
        _toolTip.SetToolTip(_bannerText, whole);
        _bannerOk.Visible = true;
    }

    private void HideBanner() { _banner.Visible = false; _bannerBtn.Visible = false; _bannerClose.Visible = false; _bannerOk.Visible = false; }

    /// <summary>
    /// Said once per session, when it is the customer's support app hosting rather than the
    /// background service (internal testing, 2026-10-02). Everything that then fails — a UAC
    /// prompt that shows black, a Task Manager that will not open, a DISM that says "elevation
    /// required" — fails because of this, and nothing on screen used to say so.
    /// </summary>
    private void NoticeIfHostedByApp()
    {
        if (!_session.HostedByApp || _appNoticeFor == _session.SessionId || _dead || _rebooting) return;
        _appNoticeFor = _session.SessionId;
        ShowNotice($"Run by the support app, not the background service: UAC prompts show black and nothing runs as administrator.",
            $"This session on {_session.MachineName} is hosted by the customer's support app, running as the person at the machine, "
            + "because the background service was not available. So a UAC prompt shows black here (it is on their screen), an elevated "
            + "window such as an administrator's Task Manager cannot be controlled, and scripts are not run as administrator. "
            + "Tools → Info says the same. Reinstalling the support app on that machine usually puts the service back.");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Trace.Write("form: session window closing — " + _view.DiagnosticSummary() +
                    (_audio.PacketsIn > 0 ? " " + _audio.DiagnosticSummary() : ""));
        _meterTick.Stop();
        _rebootTick.Stop();
        // A recording running when the window closes is stopped and uploaded, not lost: said to the
        // server while the socket is still open, then finished and sent in the background.
        StopRecording(uploadAsWindowCloses: true);
        // Hand the clipboard notification back. A listener left registered against a destroyed
        // window is a handle Windows keeps posting to.
        if (_clipListening) { try { RemoveClipboardFormatListener(Handle); } catch { } _clipListening = false; }
        LeaveOpenWindows();
        // Tell the host to stop capturing before the socket goes: a host left streaming into a
        // closed session is a machine quietly recording its own speakers for nothing.
        if (_listening) { try { _link.Send(new AudioListenMsg { On = false }); } catch { } }
        // And stop talking, which closes the customer's microphone if it was open.
        EndTalk("the window closed", tell: !_dead);
        // Deliberate: say so, or the server treats this exactly like a dropped Wi-Fi packet and
        // holds the session — with a host still running on the customer's machine — for ninety
        // seconds after the technician has gone. This is the message that keeps those two apart.
        if (!_dead) { try { _link.Send(new EndSessionMsg { SessionId = _session.SessionId }); } catch { } }
        _audio.Dispose();
        // Give the send loop a moment to flush the EndSessionMsg before the socket is torn down.
        // WsConnection.CloseAsync flushes, but cancelling out from under it does not.
        try { Thread.Sleep(120); } catch { }
        _cts.Cancel();
        try { _linkCts.Cancel(); } catch { }
        foreach (var fs in _incoming.Values) try { fs.Dispose(); } catch { }
        // The tool windows are owned by this one, so Windows destroys them with it — but only
        // the ones that were ever shown. A window that was built and never opened has a handle
        // and no owner to take it away, and five of those per session is a leak.
        foreach (var tool in _tools) try { tool.Win.Dispose(); } catch { }
        base.OnFormClosed(e);
    }
}
