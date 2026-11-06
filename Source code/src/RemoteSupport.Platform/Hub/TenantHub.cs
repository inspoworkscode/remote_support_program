using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Live state for ONE tenant: its signed-in technicians, connected devices, ringing calls, active
/// sessions and the host↔viewer relay. Tenants never share a hub, so isolation is structural.
/// Devices, chat and session history are persisted to the database.
/// </summary>
public sealed partial class TenantHub
{
    public int TenantId { get; }
    public string TenantName { get; private set; }

    private readonly object _lock = new();
    private readonly ILogger _log;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly HubRegistry _registry;

    private readonly Dictionary<string, string> _deviceKeys = new();
    private readonly Dictionary<string, DeviceInfo> _devices = new();

    /// <summary>
    /// Machines whose person chose Exit in the tray app (<see cref="ClientClosingMsg"/>). Offline
    /// to everyone and refused to every session while in here, whatever is still connected; the
    /// tray app saying hello again takes a machine out. Mirrors <c>Device.ClosedByUserUtc</c>.
    /// Guarded by <c>_lock</c>.
    /// </summary>
    private readonly HashSet<string> _closedByUser = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Machines with an unattended password, as (salt, hash) — the stored <c>Device</c> columns,
    /// held here so a connect is checked without a database round trip. Kept OUT of DeviceInfo,
    /// which is broadcast to every console; that carries only whether one is set. Guarded by
    /// <c>_lock</c>.
    /// </summary>
    private readonly Dictionary<string, (string Salt, string Hash)> _unattendedPw = new();

    /// <summary>
    /// Wrong unattended passwords per MACHINE — it is the machine being guessed at, from however
    /// many consoles. Five, and every attempt on it is refused for fifteen minutes, the right
    /// password included. In memory, as <c>SignInGuard</c> is: a restart forgives, and a restart
    /// is not something a guesser can cause. Guarded by <c>_lock</c>.
    /// </summary>
    private readonly Dictionary<string, (int Wrong, DateTime LockedUntil)> _pwGuesses = new();
    private const int PasswordTries = 5;
    private static readonly TimeSpan PasswordLockout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Online and Unattended from what is connected AND whether the person at the machine closed
    /// the tray app. The ONE place they are worked out, so a closed machine cannot read online
    /// through a path that forgot it. The connection flags stay true: the agent really is there,
    /// and the health check menu and tools/agentcheck rely on that being said honestly.
    /// </summary>
    private void Recompute(DeviceInfo info)
    {
        bool closed = _closedByUser.Contains(info.DeviceId);
        info.Unattended = !closed && info.AgentOnline && MayGoUnattended(info);
        info.Online = !closed && (info.AgentOnline || info.ClientOnline);
    }

    /// <summary>
    /// THE question for every door unattended access opens: the person at the machine allowed it AND, if the
    /// machine came back from a reinstall matched only by its hardware, somebody in the office has confirmed it
    /// is the same machine (2026-10-07, ConfirmReinstallAsync). Ask this, never <c>UnattendedEnabled</c> alone —
    /// that is the customer's tick, which whoever presents the fingerprint holds as well.
    /// </summary>
    private static bool MayGoUnattended(DeviceInfo info) => info.UnattendedEnabled && !info.ReinstallUnconfirmed;

    private sealed class TechConn { public TechSession Who = null!; public WsConnection Ws = null!; }
    private sealed class DeviceConn
    {
        public string DeviceId = ""; public DeviceRole Role; public WsConnection Ws = null!;
        /// <summary>An agent that said <see cref="DeviceCan.AskConsent"/> in its hello.</summary>
        public bool CanAskConsent;
        /// <summary>Said <see cref="DeviceCan.Watch"/>: can send figures for Processes and Performance.</summary>
        public bool CanWatch;
        /// <summary>Said <see cref="DeviceCan.Wake"/>: can send a Wake-on-LAN wake-up for another machine.</summary>
        public bool CanWake;
        /// <summary>A tray app that said <see cref="DeviceCan.AlwaysAsk"/>: it asks the person whenever told to.</summary>
        public bool AlwaysAsk;
        /// <summary>Said <see cref="DeviceCan.Branding"/>: takes the company's current look (TenantHub.Branding.cs).</summary>
        public bool CanBranding;
        /// <summary>An agent that said <see cref="DeviceCan.Update"/>: it updates its machine itself (TenantHub.Update.cs).</summary>
        public bool CanUpdate;
        /// <summary>The look it said it has, then the last one sent to it. Guarded by <c>_lock</c>.</summary>
        public string BrandHash = "";
    }

    private readonly List<TechConn> _techs = new();
    private readonly List<DeviceConn> _deviceConns = new();
    private readonly Dictionary<string, CallInfo> _calls = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly LinkedList<ChatMsg> _chat = new();

    private sealed class Session
    {
        public SessionInfo Info = new();
        public string HostToken = "";
        public WsConnection? Host;
        public readonly Dictionary<WsConnection, string> Viewers = new();
        /// <summary>This company's technicians among <see cref="Viewers"/>, by id — what the two-session limit counts
        /// (TenantHub.SessionLimit.cs). A visitor from another company is never in it.</summary>
        public readonly Dictionary<WsConnection, int> ViewerIds = new();
        public string? CallId;
        public string StartedBy = "";
        public bool Unattended;
        /// <summary>
        /// The host announces its monitors and system info once, as soon as it connects. A technician
        /// who joins after that moment would otherwise never receive them, leaving a blank screen with
        /// no controls, so we keep the last copy and replay it to every new viewer.
        /// </summary>
        public MonitorsMsg? LastMonitors;
        public SysInfoMsg? LastSysInfo;
        /// <summary>When the host was last asked for a complete picture (Environment.TickCount64). See AskHostForKeyFrame.</summary>
        public long LastKeyFrameRequestMs = long.MinValue / 2;
        /// <summary>The privacy screen as the host last reported it, for a technician who joins later.</summary>
        public PrivacyStateMsg? LastPrivacy;

        /// <summary>
        /// The session's ticket, kept once the session has ENDED (3.5.3, 2026-09-29). EndSession
        /// takes the session out of <c>_sessionTickets</c> at once, but the host is told and then
        /// given a moment to send what its clipboard ledger still holds — and that last line has
        /// to know where to go. A network away, it always arrived after the lookup had gone.
        /// </summary>
        public int EndedTicketId;

        /// <summary>
        /// Viewers that said "end" before their socket closed. A dropped Wi-Fi packet and a
        /// technician clicking Leave / End used to be the same event here, which is exactly why
        /// a client-side retry loop could never have worked on its own: by the time it dialled
        /// back in the session was gone. A deliberate end must still end it at once.
        /// </summary>
        public readonly HashSet<WsConnection> Leaving = new();

        /// <summary>
        /// Bumped every time the viewer population changes. A grace timer that fires holding a
        /// stale token has been overtaken — a viewer came back, or left again and started a
        /// newer timer — and must do nothing. Cheaper and less error-prone than cancelling
        /// timers, and it cannot race with a reconnect that lands mid-tick.
        /// </summary>
        public int GraceToken;
        public DateTime? HeldSince;

        /// <summary>Whether a technician is listening to this machine's sound. Kept here so a
        /// tray app that reconnects mid-session can be told — the host reports it once and the
        /// customer's window is the one place that must never miss it.</summary>
        public bool Listening;

        /// <summary>The host's last word on a voice call (3.3), as passed on — so a tray app that
        /// reconnects mid-call is asked about its microphone again rather than left with nothing,
        /// and so the ticket is written only when the customer's microphone actually changes.</summary>
        public VoiceStateMsg? LastVoice;
        /// <summary>Technicians whose Talk is on, for the ticket's lines.</summary>
        public readonly HashSet<string> Talking = new();

        /// <summary>
        /// An attended session the AGENT is hosting, after asking (2026-10-02). The tray app is told
        /// a technician is connected only once the person has said yes — until then "X has
        /// connected and can now see your screen" would be untrue.
        /// </summary>
        public bool TellTrayWhenAllowed;

        /// <summary>
        /// Set when the session was opened with the machine's unattended password: the technicians
        /// who typed it. Nobody else may join — "nobody without it" would mean little if a colleague
        /// could open the running session from the Sessions list. Null for every other session.
        /// </summary>
        public HashSet<int>? Admitted;

        /// <summary>
        /// Somebody from ANOTHER company, let in by the person at the machine with a code (3.9). Only they
        /// may join from outside, they may do only what <see cref="VisitorMaySend"/> allows, they see only
        /// what <see cref="VisitorMaySee"/> allows, and when they go the session goes. Null otherwise.
        /// </summary>
        public Visitor? Visitor;
        /// <summary>The visitor's viewer sockets — the ones the filters apply to.</summary>
        public readonly HashSet<WsConnection> VisitorSockets = new();
        /// <summary>Bumped when the visitor comes or goes, for the visitor's own grace timer.</summary>
        public int VisitorGraceToken;

        /// <summary>
        /// The visitor was brought in by this company's technician, named here, with a code made in the session
        /// (2026-10-22, TenantHub.Invite.cs) — not by the person at the machine. Then leaving does not end the
        /// session, and nobody of the company left in it means the visitor goes. Null otherwise.
        /// </summary>
        public string? InvitedBy;
        public int InvitedById;
        /// <summary>Who typed a technician's code and is waiting for them to answer; the code they spent.</summary>
        public Visitor? Pending;
        public OutsiderRedeemed? PendingHit;
        /// <summary>Bumped when a wait is answered or given up, so its timer does nothing.</summary>
        public int PendingToken;
        /// <summary>The technician whose code for this session is live (0: none), so it stops when they leave.</summary>
        public int InviteMakerId;

        /// <summary>End-to-end encryption (2026-10-07, TenantHub.E2e.cs): viewer sockets that said E2eHelloMsg,
        /// when each viewer joined, and the mode last told to the host (null: not yet told).</summary>
        public readonly HashSet<WsConnection> E2eViewers = new();
        public readonly Dictionary<WsConnection, DateTime> JoinedAt = new();
        public bool? E2eSealed;
    }

    /// <summary>
    /// Why recently ended sessions ended, for a console that comes asking afterwards. A session
    /// leaves the registry the moment it ends, and a viewer dialling in after that used to get a
    /// bare 404 — which its retry loop reads as "not yet", so it kept dialling for two minutes and
    /// then said "Could not get back to the session after 124 seconds" about a customer who had
    /// simply not answered (internal testing, 2026-10-02, item 5). Kept for ten minutes.
    /// </summary>
    private readonly Dictionary<string, (string Reason, DateTime At)> _endedReasons = new();
    private static readonly TimeSpan EndedRemembered = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a session is held open after the last viewer's socket goes without an
    /// EndSessionMsg. Clint's call on 2026-09-13: long enough for a Wi-Fi roam or a VPN re-dial,
    /// short enough that a host does not linger unnoticed on a customer's machine. An explicit
    /// Leave / End is not affected — that still ends the session immediately.
    /// </summary>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(90);

    private readonly TicketService _tickets;
    private readonly ClientService _clients;
    private readonly PortalHandoffService _handoff;
    private readonly TicketPhotoService _photos;
    private readonly ContactService _contacts;
    private readonly IConfiguration _cfg;
    /// <summary>sessionId -> ticket the session's activity is logged against.</summary>
    private readonly Dictionary<string, int> _sessionTickets = new();

    // ---- the monthly health check (2026-09-28) ----
    /// <summary>The company switch AND its mail finished — Tenant.HealthCheckReady.</summary>
    private bool _healthAvailable;
    private int _healthDay;
    /// <summary>clientId -> the customer's tick.</summary>
    private readonly Dictionary<int, bool> _clientHealth = new();
    /// <summary>
    /// Devices whose STORED record has been read. An agent is told nothing until its machine's is.
    ///
    /// A machine reinstalled with a new device id arrives looking like a stranger — no customer,
    /// no override — and is only recognised as itself when PersistDeviceAsync finds its hardware.
    /// Telling the agent in between would say "off", it would remove the health check, and a
    /// second later be told "on" and put it back. Harmless-looking, and exactly the kind of flap
    /// that one day removes something for real.
    /// </summary>
    private readonly HashSet<string> _healthLoaded = new();
    /// <summary>
    /// Devices whose hello has been taken but whose stored record PersistDeviceAsync has not yet put back into
    /// the live one (2026-10-07). Found while making a reinstall wait for the office: a tick or a password from
    /// the person at the machine that arrived in that gap was made to a fresh in-memory record, NOT stored (the
    /// row had no new device id yet), and then overwritten by the stored one — so the free installer could be
    /// told "done" and leave a machine with no password. Fast on a local file; a real database opens the gap.
    /// </summary>
    private readonly Dictionary<string, RecordGate> _recordRead = new();

    /// <summary>
    /// One machine's hellos still being put together with its stored record. A new machine's tray app and agent say
    /// hello at the same moment (a first install starts both — 2026-10-12, found by the load test on PostgreSQL):
    /// both found no row and both added one, the second save broke the unique index, and its half's details were
    /// lost. So they are taken in turn (<see cref="Last"/>: the second finds the row the first wrote), and the
    /// wait opens when BOTH are done — opened by the first, a tick or password arriving next was overwritten by
    /// the second's copy of the record, which it had read before them (6 of 40 machines lost the password from
    /// the live record; 37 of 40 when the second merely retried its save instead). tools/firsthello. Under <c>_lock</c>.
    /// </summary>
    private sealed class RecordGate
    {
        public readonly TaskCompletionSource Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pending;
        public Task Last = Task.CompletedTask;
    }

    /// <summary>Waits (at most ten seconds) until this device's stored record is in the live one.</summary>
    private async Task WhenRecordReadAsync(string deviceId)
    {
        Task t;
        lock (_lock) t = _recordRead.TryGetValue(deviceId, out var g) ? g.Read.Task : Task.CompletedTask;
        await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    /// <summary>One hello's record is in; once per PersistDeviceAsync. The wait opens with the last of them.</summary>
    private void RecordRead(string deviceId)
    {
        lock (_lock)
            if (_recordRead.TryGetValue(deviceId, out var g) && --g.Pending <= 0)
            {
                _recordRead.Remove(deviceId);
                g.Read.TrySetResult();
            }
    }

    /// <summary>This hello's PersistDeviceAsync, after the one before it for the same machine (RecordGate).</summary>
    private async Task PersistInTurnAsync(Task before, HelloMsg h, bool isNew, string publicAddress)
    {
        if (!before.IsCompleted)
        {
            _log.LogInformation("[{Tenant}] {Name} ({Role}) waits for its other half's hello to be stored", TenantName, h.MachineName, h.Role);
            await before;   // never faults: PersistDeviceAsync catches everything
        }
        await PersistDeviceAsync(h, isNew, publicAddress);
    }

    private TenantHub(Tenant tenant, IDbContextFactory<PlatformDb> dbf, HubRegistry registry, ILogger log,
        TicketService tickets, ClientService clients, PortalHandoffService handoff, IConfiguration cfg,
        TicketPhotoService photos, ContactService contacts)
    {
        TenantId = tenant.Id; TenantName = tenant.CompanyName;
        _dbf = dbf; _registry = registry; _log = log;
        _tickets = tickets; _clients = clients; _handoff = handoff; _cfg = cfg; _photos = photos; _contacts = contacts;
        // The free version has no health check (Stage B): nothing is offered to its machines.
        _healthAvailable = tenant.HealthCheckReady && !FreePlan.Is(tenant.Plan);
        _healthDay = tenant.HealthCheckDayOfMonth;
        _outsidersOn = tenant.OutsidersAllowed;
        _invitesOn = tenant.TechnicianInvitesAllowed;
        _jobTypesOff = tenant.JobTypesOff ?? "";
    }

    /// <summary>
    /// A company's hub, made and filled from the database. ONLY through <see cref="HubRegistry"/>, which makes each
    /// company's ONCE however many of its machines arrive together (2026-10-08, BUGS #11): this used to run in the
    /// constructor, synchronously, as many times at once as machines arrived before the first finished — after a
    /// restart, every company's machines at once — and every one of those queries held a thread. 5,000 machines
    /// in 30 s stalled the whole server for up to 30 s at 1% processor; with each hub loaded first, 3 ms each.
    /// </summary>
    public static async Task<TenantHub> CreateAsync(Tenant tenant, IDbContextFactory<PlatformDb> dbf, HubRegistry registry, ILogger log,
        TicketService tickets, ClientService clients, PortalHandoffService handoff, IConfiguration cfg,
        TicketPhotoService photos, ContactService contacts)
    {
        var hub = new TenantHub(tenant, dbf, registry, log, tickets, clients, handoff, cfg, photos, contacts);
        await hub.LoadAsync(tenant);
        return hub;
    }

    private async Task LoadAsync(Tenant tenant)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await LoadFreeAsync(tenant, db);
        var clientNames = await db.Clients.Where(c => c.TenantId == TenantId).ToDictionaryAsync(c => c.Id, c => c.Name);
        foreach (var c in await db.Clients.Where(c => c.TenantId == TenantId && c.HealthCheck).Select(c => c.Id).ToListAsync())
            _clientHealth[c] = true;
        var contactNames = await db.Contacts.Where(c => c.TenantId == TenantId).ToDictionaryAsync(c => c.Id, c => c.Name);
        foreach (var d in await db.Devices.Where(d => d.TenantId == TenantId).ToListAsync())
        {
            _deviceKeys[d.DeviceId] = d.DeviceKey;
            _devices[d.DeviceId] = new DeviceInfo
            {
                DeviceId = d.DeviceId, MachineName = d.MachineName, UserName = d.UserName, Os = d.Os, LastSeen = d.LastSeenUtc,
                AgentVersion = d.AgentVersion, ClientVersion = d.ClientVersion, SelfUpdates = d.SelfUpdates,
                ClientId = d.ClientId ?? 0,
                ClientName = d.ClientId.HasValue && clientNames.TryGetValue(d.ClientId.Value, out var cn) ? cn : "",
                // The contact is loaded HERE as well as in PersistDeviceAsync. Stage 3.6.1 set it
                // only there, so a machine that had not reconnected since the last server restart
                // showed its client's name in the console's first column instead of the person's —
                // and would have gone on doing so until somebody rebooted it.
                ContactId = d.ContactId ?? 0,
                ContactName = d.ContactId.HasValue && contactNames.TryGetValue(d.ContactId.Value, out var pn) ? pn : "",
                // The name given at its last support request (2026-10-18), shown while nobody has linked it.
                RequestedBy = d.RequestedBy, RequestedByUtc = AsUtc(d.RequestedByUtc), RequestedByEmail = d.RequestedByEmail,
                // The stored unattended permission, so a restarted server does not show every
                // machine as attended until somebody toggles something.
                UnattendedEnabled = d.UnattendedEnabled,
                UnattendedPassword = d.UnattendedPasswordHash.Length > 0,
                ReinstallUnconfirmed = d.ReinstallUnconfirmed,
                HealthCheckMode = HealthCheckModes.Normalise(d.HealthCheckMode),
                HealthCheckInstalled = d.HealthCheckInstalled,
                HealthCheckNote = d.HealthCheckNote,
                OutsiderMode = OutsiderAccess.Normalise(d.OutsiderMode),
                Spec = new SystemSpec { Cpu = d.Cpu, Cores = d.Cores, RamMb = d.RamMb, DiskTotalGb = d.DiskTotalGb, DiskFreeGb = d.DiskFreeGb }
            };
            if (d.ClosedByUserUtc != null) _closedByUser.Add(d.DeviceId);
            if (d.HardwareId.Length > 0) _hardware[d.DeviceId] = d.HardwareId;
            if (d.UnattendedPasswordHash.Length > 0) _unattendedPw[d.DeviceId] = (d.UnattendedPasswordSalt, d.UnattendedPasswordHash);
            ApplyHealth(_devices[d.DeviceId]);
            ApplyOutsider(_devices[d.DeviceId]);
            _healthLoaded.Add(d.DeviceId);
            // Wake-on-LAN: what it last said about its networks, for when it is switched off.
            RememberNetwork(d.DeviceId, ReadCards(d.NetworkCardsJson), d.PublicAddress);
        }
        await LoadSavedPasswordsAsync(db);
        foreach (var c in (await db.Chat.Where(c => c.TenantId == TenantId).OrderByDescending(c => c.TsUtc).Take(200).ToListAsync()).OrderBy(c => c.TsUtc))
            _chat.AddLast(new ChatMsg { DeviceId = c.DeviceId, From = c.From, FromTech = c.FromTech, Text = c.Text, Ts = c.TsUtc });
    }

    public void Rename(string name) => TenantName = name;

    // ------------------------------------------------------------------ technician socket
    /// <summary>
    /// Everything this connection shows or reads is in the company's own time zone (CompanyClock,
    /// 2026-10-05) — set at the start of each connection, so it flows to every message handled on it.
    /// </summary>
    private async Task UseCompanyClockAsync() => CompanyClock.Set(await CompanyClock.ForTenantAsync(_dbf, TenantId));

    /// <summary>Everybody in a seat, by name, for handing a ticket to anyone.</summary>
    private async Task<List<string>> RosterAsync()
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            return await db.Technicians
                .Where(t => t.TenantId == TenantId && !t.Disabled && (t.Role & TenantRoles.Seated) != 0)
                .OrderBy(t => t.DisplayName).Select(t => t.DisplayName).ToListAsync();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Everything a console is told when it signs in — and again when the company's plan changes
    /// (an upgrade, 2026-10-06). Under <c>_lock</c>.</summary>
    private StateMsg StateFor(TechSession who, List<string> roster) => new()
    {
        You = who.DisplayName,
        OnlineTechnicians = _techs.Select(t => t.Who.DisplayName).Distinct().ToList(),
        AllTechnicians = roster,
        Devices = _devices.Values.OrderByDescending(d => d.Online).ThenBy(d => d.MachineName).ToList(),
        Calls = _calls.Values.Where(c => c.Status is CallStatus.Ringing or CallStatus.Accepted).ToList(),
        Sessions = _sessions.Values.Select(s => s.Info).ToList(),
        RecentChat = _chat.TakeLast(200).ToList(),
        Free = Free,
        MaxMachines = Free ? FreePlan.MachinesFor(_plan, _basicMachines) : 0,
        // Which one-person plan (2026-10-25, §163): the Basic version connects attended too, the Free version never.
        Basic = FreePlan.IsBasic(_plan),
        MayRemoveDevices = !Free && Permissions.CanRemoveDevices(who.Role),
        MayConfirmReinstall = Permissions.CanConfirmReinstall(who.Role),
        ConsoleUpdate = _registry.Updates?.ConsoleOfferNow(_ring),
        // Job types (§168): what New ticket and Edit offer, and what a booking asks for.
        CategoriesOff = CategoriesOff(),
        BookingPhotos = BookingPhotos()
    };

    public async Task RunTechnicianAsync(TechSession who, WsConnection ws, CancellationToken ct)
    {
        await UseCompanyClockAsync();
        // full roster so any technician can hand a ticket to any colleague, online or not
        var roster = await RosterAsync();

        var tc = new TechConn { Who = who, Ws = ws };
        lock (_lock)
        {
            _techs.Add(tc);
            ws.Send(StateFor(who, roster));
        }
        ws.Send(MineFor(who));
        BroadcastTechPresence();
        _ = PushMentionCountAsync(who.DisplayName);
        _ = PushBillingCountAsync(who.DisplayName);
        _ = PushActivityCountsAsync(who.DisplayName);
        _ = PushChatOverviewAsync(who.DisplayName);
        _ = SendWebChatListAsync(tc);
        // Website chat asks whether anybody is here to answer (3.8.3); a console is somebody.
        using var here = _registry.Presence.Arrive(TenantId, who.DisplayName);
        _log.LogInformation("[{Tenant}] technician {Name} signed in", TenantName, who.DisplayName);
        ws.OnMessage += m => HandleTechMessage(tc, m);
        await ws.RunAsync(ct);
        lock (_lock) _techs.Remove(tc);
        ForgetWatcher(tc);
        ForgetWakes(tc);
        BroadcastTechPresence();
    }

    /// <summary>
    /// This person has been signed out everywhere (AuthService.SignedOut): removed, their password,
    /// boxes or two-factor changed, or their token expired. Their consoles are told why and closed,
    /// and they leave every session — a session they were alone in ends now rather than waiting
    /// out its reconnect grace for a console that can never come back.
    ///
    /// <para>With <paramref name="keepToken"/> (2026-10-05, one sign-in at a time): they signed in on
    /// another console, which is kept. Every other console of theirs goes, and so does every session
    /// window — the new console has just signed in and cannot have opened one yet.</para>
    /// </summary>
    public void SignOutTechnician(int technicianId, string name, string why, string? keepToken = null)
    {
        List<TechConn> consoles; List<WsConnection> viewers = new(); List<string> endNow = new();
        lock (_lock)
        {
            consoles = _techs.Where(t => t.Who.TechnicianId == technicianId && (keepToken == null || t.Who.Token != keepToken)).ToList();
            foreach (var (id, s) in _sessions)
            {
                var theirs = s.Viewers.Where(v => v.Value == name).Select(v => v.Key).ToList();
                if (theirs.Count == 0) continue;
                if (s.Viewers.Count == theirs.Count) endNow.Add(id);
                else viewers.AddRange(theirs);
            }
        }
        foreach (var id in endNow) EndSession(id, $"{name} was signed out.");
        foreach (var v in viewers) { v.Send(new SignedOutMsg { Reason = why }); _ = v.CloseAsync("signed out"); }
        foreach (var c in consoles) { c.Ws.Send(new SignedOutMsg { Reason = why }); _ = c.Ws.CloseAsync("signed out"); }
        _log.LogInformation("[{Tenant}] {Name} signed out by the server ({Consoles} console(s), {Ended} session(s) ended): {Why}",
            TenantName, name, consoles.Count, endNow.Count, why);
    }

    private void HandleTechMessage(TechConn tc, Msg m)
    {
        // Anything from a running console (its ping every 15 s included) keeps its token alive.
        _registry.Auth?.Touch(tc.Who.Token);
        // The free version (2026-10-06): the console may ask only for what is in it. Said in words —
        // an older or tampered console sending a ticket message must see why nothing happened.
        if (Free && !ConsoleMaySendOnFree(m))
        {
            tc.Ws.Send(new ErrorMsg { Message = $"That is part of the full version, not {FreePlan.TheNameOf(_plan)}." });
            return;
        }
        try
        {
            switch (m)
            {
                case AcceptCallMsg a: AcceptCall(tc, a.CallId); break;
                case ConnectDeviceMsg c: ConnectDevice(tc, c.DeviceId, c.Reason, c.Password); break;
                case EndSessionMsg e: EndSession(e.SessionId, $"Ended by {tc.Who.DisplayName}"); break;
                case ChatMsg chat: chat.From = tc.Who.DisplayName; chat.FromTech = true; chat.Ts = DateTimeOffset.UtcNow; RouteChat(chat); break;
                case TicketListRequestMsg tl: _ = SendTicketListAsync(tc, tl.Filter, tl.Search, tl.Sort); break;
                case ActivityRequestMsg ar: _ = SendActivityAsync(tc, ar); break;
                case TicketOpenMsg to: _ = SendTicketDetailAsync(tc, to.TicketId); break;
                case TicketCreateMsg tcm: _ = CreateTicketAsync(tc, tcm); break;
                case RecurringKindsRequestMsg: _ = SendRecurringKindsAsync(tc); break;
                case RecurringCancelMsg rc: _ = CancelRecurringAsync(tc, rc); break;
                case TicketUpdateMsg tu: _ = UpdateTicketAsync(tc, tu); break;
                case TicketSetClientMsg sc: _ = SetTicketClientAsync(tc, sc); break;
                case TicketSetParentMsg sp: _ = SetTicketParentAsync(tc, sp); break;
                case TicketEditMsg te: _ = EditTicketAsync(tc, te); break;
                case TicketNoteAddMsg tn: _ = AddTicketNoteAsync(tc, tn); break;
                case TicketEmailNoteMsg en: _ = EmailTicketNoteAsync(tc, en); break;
                case ClientSearchMsg cs: _ = SendClientsAsync(tc, cs.Term, cs.Take); break;
                case DeviceLinkMsg dl: _ = LinkDeviceAsync(tc, dl); break;
                case DeviceHealthCheckMsg dh:
                    if (!Permissions.CanSetHealthCheck(tc.Who.Role))
                    { tc.Ws.Send(new ErrorMsg { Message = "You cannot change which machines get the health check." }); break; }
                    _ = SetDeviceHealthCheckAsync(dh.DeviceId, dh.Mode, tc.Who.DisplayName);
                    break;
                case OutsiderConnectMsg oc: _ = ConnectWithCodeAsync(tc, oc.Code); break;
                // A technician bringing somebody into their session (§155) — see TenantHub.Invite.cs.
                case OutsiderInviteRequestMsg oi: _ = MakeInviteAsync(tc, oi); break;
                case OutsiderInviteCancelMsg ox: _ = CancelInviteAsync(tc, ox); break;
                case OutsiderJoinAnswerMsg oa: AnswerJoin(tc, oa); break;
                case OutsiderRemoveMsg orm: RemoveAsked(tc, orm); break;
                case DeviceOutsiderModeMsg om:
                    if (!Permissions.CanAllowOutsiders(tc.Who.Role))
                    { tc.Ws.Send(new ErrorMsg { Message = "Only a supervisor or the owner can let people from outside the company in." }); break; }
                    _ = SetDeviceOutsiderModeAsync(om.DeviceId, om.Mode, tc.Who.DisplayName);
                    break;
                case PortalLinkRequestMsg pl: SendPortalLink(tc, pl.Path); break;
                case KnowledgeCountRequestMsg kc: _ = SendKnowledgeCountAsync(tc, kc.DeviceId); break;
                case TicketActMsg ta: _ = ActOnTicketAsync(tc, ta); break;
                case TicketWorkDoneMsg wd: _ = AddWorkDoneAsync(tc, wd); break;
                case ItemSuggestMsg its: _ = SuggestItemsAsync(tc, its.Term); break;
                case QuoteLineAddMsg qa: _ = AddQuoteLineAsync(tc, qa); break;
                case QuoteLineRemoveMsg qr: _ = RemoveQuoteLineAsync(tc, qr); break;
                case QuoteLinePhotosMsg qp: _ = AddQuoteLinePhotosAsync(tc, qp); break;
                case QuoteMachineMsg qm: _ = SetQuoteMachineAsync(tc, qm); break;
                // Office chat (3.8.1). The sender is always who the socket signed in as, never a name
                // the console claims.
                case StaffChatSendMsg cs2: _ = SendStaffChatAsync(tc, cs2); break;
                case StaffChatHistoryRequestMsg ch: _ = SendChatHistoryAsync(tc, ch.With); break;
                case StaffChatReadMsg cr: _ = _registry.Chat.MarkReadAsync(TenantId, tc.Who.DisplayName, cr.With, cr.UpToId); break;
                case StaffChatOverviewRequestMsg: _ = PushChatOverviewAsync(tc.Who.DisplayName); break;
                // Website chat (3.8.3), as the person the socket signed in as.
                case WebChatSayMsg ws2: _ = WebChatSayAsync(tc, ws2); break;
                case WebChatEndMsg we: _ = WebChatEndAsync(tc, we); break;
                case WebChatHistoryRequestMsg wh: _ = WebChatHistoryAsync(tc, wh.ChatId); break;
                case WebChatHandOverMsg who2: _ = WebChatHandOverAsync(tc, who2); break;
                case WebChatPeopleRequestMsg wp: tc.Ws.Send(new WebChatPeopleMsg { ChatId = wp.ChatId, Names = _registry.WebChat.HereNow(TenantId) }); break;
                // Processes and Performance (2026-10-05): any console, any online machine — see TenantHub.Watch.cs.
                case WatchMachineMsg wm: WatchMachine(tc, wm); break;
                // Wake-on-LAN (2026-10-06): through another machine on the same network — see TenantHub.Wake.cs.
                case WakeRequestMsg wk: WakeMachine(tc, wk); break;
                // Remembered unattended passwords (2026-10-06) — see TenantHub.SavedPasswords.cs.
                case ForgetSavedPasswordMsg fs: _ = ForgetSavedPasswordAsync(tc, fs); break;
                case SavedPasswordWhoRequestMsg sw: _ = SendSavedPasswordWhoAsync(tc, sw.DeviceId); break;
                // The free version's Remove (2026-10-06) — see TenantHub.Free.cs.
                case DeviceRemoveMsg dr: _ = RemoveDeviceForConsoleAsync(tc, dr); break;
                case ConfirmReinstallMsg cr: _ = ConfirmReinstallForConsoleAsync(tc, cr); break;
            }
        }
        catch (Exception ex) { _log.LogError(ex, "[{Tenant}] tech message", TenantName); tc.Ws.Send(new ErrorMsg { Message = ex.Message }); }
    }

    private void BroadcastTechPresence()
    {
        List<string> names; List<TechConn> techs;
        lock (_lock) { names = _techs.Select(t => t.Who.DisplayName).Distinct().ToList(); techs = _techs.ToList(); }
        foreach (var t in techs) t.Ws.Send(new TechPresenceMsg { OnlineTechnicians = names });
    }

    /// <summary>Changed-ticket messages whose "Customer replied" has been asked, on their way out (§165).</summary>
    private readonly HashSet<Msg> _replyAsked = new(ReferenceEqualityComparer.Instance);

    private void BroadcastTechs(Msg m)
    {
        // A changed ticket says whether the customer's reply is its newest entry (§165) — asked HERE, once, because fourteen
        // places send one, and a row that lost "Customer replied" the moment the reply arrived would be the wrong way round.
        bool asked; lock (_replyAsked) asked = _replyAsked.Contains(m);
        if (m is TicketChangedMsg { Ticket: { } changed } && !asked)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var db = await _dbf.CreateDbContextAsync();
                    changed.CustomerReplied = (await TicketService.CustomerRepliedAsync(db, TenantId, new[] { changed.Id })).Count > 0;
                }
                catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] customer replied", TenantName); }
                lock (_replyAsked) _replyAsked.Add(m);
                try { BroadcastTechs(m); } finally { lock (_replyAsked) _replyAsked.Remove(m); }
            });
            return;
        }
        List<TechConn> techs; lock (_lock) techs = _techs.ToList();
        foreach (var t in techs) t.Ws.Send(m);
        // A session somebody from another company started (3.9): its changes reach THEIR console too,
        // which is on another company's hub and hears none of this company's broadcasts.
        if (m is SessionReadyMsg sr) TellVisitor(sr.Session);
    }

    /// <summary>
    /// The number of tickets with an @mention waiting for this person, sent to each console they have
    /// open. On sign-in, and whenever TicketService says it changed (HubRegistry wires that).
    /// </summary>
    public async Task PushMentionCountAsync(string person)
    {
        List<TechConn> theirs;
        lock (_lock) theirs = _techs.Where(t => t.Who.DisplayName == person).ToList();
        if (theirs.Count == 0) return;
        try
        {
            var count = (await _tickets.OpenMentionsAsync(TenantId, person)).Select(m => m.TicketId).Distinct().Count();
            foreach (var t in theirs) t.Ws.Send(new MentionCountMsg { Count = count });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] mention count", TenantName); }
    }

    /// <summary>
    /// The billing queue's count, at sign-in, to this person's consoles — if they are one of the
    /// people told (TicketService.BillingBadgeForAsync). Nothing is sent to anybody else.
    /// </summary>
    public async Task PushBillingCountAsync(string person)
    {
        List<TechConn> theirs;
        lock (_lock) theirs = _techs.Where(t => t.Who.DisplayName == person).ToList();
        if (theirs.Count == 0) return;
        try
        {
            if (await _tickets.BillingBadgeForAsync(TenantId, person) is not { } count) return;
            foreach (var t in theirs) t.Ws.Send(new BillingQueueMsg { Count = count });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] billing count", TenantName); }
    }

    /// <summary>
    /// Something moved in or out of the billing queue (HubRegistry wires TicketService.BillingChanged
    /// here): the new count to every console of the people told, and the job that arrived to each of
    /// them but whoever put it there.
    /// </summary>
    public void PushBilling(Services.BillingNews n)
    {
        List<TechConn> told;
        lock (_lock) told = _techs.Where(t => n.Notify.Contains(t.Who.DisplayName)).ToList();
        foreach (var t in told)
            t.Ws.Send(new BillingQueueMsg
            {
                Count = n.Count,
                Arrived = n.Arrived == null || n.Arrived.By == t.Who.DisplayName ? null : new BillingArrivalInfo
                {
                    TicketId = n.Arrived.TicketId, Number = n.Arrived.Number, Subject = n.Arrived.Subject,
                    Stage = n.Arrived.Stage, By = n.Arrived.By
                }
            });
    }

    // ------------------------------------------------------------------ office chat (3.8.1)

    private async Task SendStaffChatAsync(TechConn tc, StaffChatSendMsg m)
    {
        var (_, error) = await _registry.Chat.SendAsync(TenantId, tc.Who.DisplayName, m.To, m.Text);
        // A saved message comes back through PushStaffChatAsync like everybody else's copy; only a
        // refusal is answered here, to the console that sent it.
        if (error.Length > 0) tc.Ws.Send(new StaffChatMsg { Error = error });
    }

    private async Task SendChatHistoryAsync(TechConn tc, string with)
    {
        var rows = await _registry.Chat.HistoryAsync(TenantId, tc.Who.DisplayName, with);
        tc.Ws.Send(new StaffChatHistoryMsg { With = with ?? "", Messages = rows.Select(StaffChatService.Info).ToList() });
    }

    /// <summary>A message, to every console of everybody it concerns, and their unread counts with it.</summary>
    public async Task PushStaffChatAsync(StaffMessage m)
    {
        List<TechConn> techs;
        lock (_lock) techs = _techs.Where(t => StaffChatService.Involves(m, t.Who.DisplayName)).ToList();
        var info = StaffChatService.Info(m);
        foreach (var t in techs)
        {
            var me = t.Who.DisplayName;
            t.Ws.Send(new StaffChatMsg
            {
                Message = info,
                MentionsMe = m.To.Length == 0 && m.From != me && StaffChatService.Mentions(m.Text, me)
            });
        }
        foreach (var person in techs.Select(t => t.Who.DisplayName).Distinct())
            await PushChatOverviewAsync(person);
    }

    /// <summary>This person's conversations and unread counts, to each console they have open.</summary>
    public async Task PushChatOverviewAsync(string person)
    {
        List<TechConn> theirs;
        lock (_lock) theirs = _techs.Where(t => t.Who.DisplayName == person).ToList();
        if (theirs.Count == 0) return;
        try
        {
            var o = await _registry.Chat.OverviewAsync(TenantId, person);
            foreach (var t in theirs) t.Ws.Send(o);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] chat overview", TenantName); }
    }

    // ------------------------------------------------------------------ device socket
    public async Task RunDeviceAsync(HelloMsg hello, WsConnection ws, CancellationToken ct, Task receiveLoop, string publicAddress = "")
    {
        await UseCompanyClockAsync();
        DeviceConn dc;
        bool bad = false; string? notAdmitted = null;
        lock (_lock)
        {
            if (_deviceKeys.TryGetValue(hello.DeviceId, out var key) && key != hello.DeviceKey) bad = true;
            // The free version (2026-10-06): a third computer, or one that was removed — checked in the
            // same lock that adds a machine, so two arriving together cannot both take the last place.
            else if ((notAdmitted = FreeRefusesHello(hello)) != null) { }
            else
            {
                if (!string.IsNullOrEmpty(hello.HardwareId)) _hardware[hello.DeviceId] = hello.HardwareId;
                bool isNew = !_deviceKeys.ContainsKey(hello.DeviceId);
                _deviceKeys[hello.DeviceId] = hello.DeviceKey;
                if (!_devices.TryGetValue(hello.DeviceId, out var info))
                {
                    _devices[hello.DeviceId] = info = new DeviceInfo { DeviceId = hello.DeviceId };
                    ApplyHealth(info);
                    ApplyOutsider(info);
                }
                info.MachineName = hello.MachineName; info.LastSeen = DateTimeOffset.UtcNow;
                // prefer the friendly edition ("Windows 11 Pro 24H2") over the raw version string
                info.Os = !string.IsNullOrWhiteSpace(hello.Spec?.Os) ? hello.Spec!.Os : hello.Os;
                if (!string.IsNullOrEmpty(hello.UserName)) info.UserName = hello.UserName;
                if (hello.Role == DeviceRole.Agent) info.AgentOnline = true; else info.ClientOnline = true;
                // Which build each half is (2026-10-16, auto-update stage A). A helper (a session's host) is not the machine.
                if (hello.Role == DeviceRole.Agent) { info.AgentVersion = hello.Version; info.SelfUpdates = hello.Can?.Contains(DeviceCan.Update) == true; }
                else if (hello.Role == DeviceRole.Client) info.ClientVersion = hello.Version;
                // The tray app running again is what reopens a machine its person closed — at the
                // next logon, or from the shortcut. The agent reconnecting never does.
                if (hello.Role == DeviceRole.Client) _closedByUser.Remove(hello.DeviceId);
                // Unattended means "a technician can get in with nobody here", which needs both
                // halves: an agent to let them in, and permission from the person at the machine.
                // It used to mean only the first, because there was no way to withhold the second.
                Recompute(info);
                if (hello.Spec != null) info.Spec = hello.Spec;
                RememberNetwork(hello.DeviceId, hello.Cards, publicAddress);
                // Until PersistDeviceAsync has read the stored record into `info`, a change from the person at
                // the machine would be made to a record about to be overwritten — see WhenRecordReadAsync.
                // Its tray app and agent say hello together: one at a time, and the wait counts both (RecordGate).
                if (!_recordRead.TryGetValue(hello.DeviceId, out var gate)) _recordRead[hello.DeviceId] = gate = new RecordGate();
                gate.Pending++;
                gate.Last = PersistInTurnAsync(gate.Last, hello, isNew, publicAddress);
            }
        }
        if (bad)
        {
            // Logged, and worded so the person reading it knows what to do. This is one of the
            // three ways a machine can be turned away and never appear in the device list, and it
            // used to say nothing at all on either end.
            _log.LogWarning("[{Tenant}] REFUSED {Machine}: it presented device id {Id} with the wrong key. " +
                            "Usually a machine restored from an image, or a device.json copied between machines — reinstall the client on it.",
                TenantName, hello.MachineName, hello.DeviceId);
            ws.Send(new ErrorMsg { Message = "This machine's identity does not match our records. Please reinstall the support client." });
            await ws.CloseAsync("bad key"); return;
        }
        if (notAdmitted != null)
        {
            _log.LogWarning("[{Tenant}] REFUSED {Machine} ({Role}, {Id}) on the free version: {Why}",
                TenantName, hello.MachineName, hello.Role, hello.DeviceId, notAdmitted);
            ws.Send(new ErrorMsg { Message = notAdmitted, NotAdmitted = true });
            await ws.CloseAsync("not admitted"); return;
        }

        dc = new DeviceConn
        {
            DeviceId = hello.DeviceId, Role = hello.Role, Ws = ws,
            CanAskConsent = hello.Can?.Contains(DeviceCan.AskConsent) == true,
            AlwaysAsk = hello.Can?.Contains(DeviceCan.AlwaysAsk) == true,
            CanWatch = hello.Can?.Contains(DeviceCan.Watch) == true,
            CanWake = hello.Can?.Contains(DeviceCan.Wake) == true,
            CanBranding = hello.Can?.Contains(DeviceCan.Branding) == true,
            CanUpdate = hello.Role == DeviceRole.Agent && hello.Can?.Contains(DeviceCan.Update) == true,
            BrandHash = hello.BrandHash ?? ""
        };
        lock (_lock)
        {
            _deviceConns.RemoveAll(d => d.DeviceId == dc.DeviceId && d.Role == dc.Role);
            _deviceConns.Add(dc);
            if (_devices.TryGetValue(dc.DeviceId, out var withConn)) RecomputeCanWatch(withConn);
        }
        ws.OnMessage += m => HandleDeviceMessage(dc, m);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(hello.DeviceId) });
        _log.LogInformation("[{Tenant}] device {Name} ({Role}) online", TenantName, hello.MachineName, hello.Role);

        // A device connection arriving in the middle of a session used to be told nothing at all.
        // DeviceLink reconnects on its own, so a tray app whose socket blinked went on claiming
        // support was in progress and never learned the session had ended — the leading
        // hypothesis for BUGS #2. Tell it what is true the moment it appears.
        if (dc.Role == DeviceRole.Client)
        {
            Session? live; lock (_lock) live = _sessions.Values.FirstOrDefault(x => x.Info.DeviceId == dc.DeviceId);
            if (live != null) NotifyDeviceSessionState(live);
            else ws.Send(new SessionStateMsg { Active = false });

            // AND its unattended state, from HERE — after the connection has been registered.
            //
            // This is the whole of Clint's "the unattended box in the tray is greyed out",
            // 2026-09-20, and it was a race I put in the day before. PersistDeviceAsync also
            // sends this, and it is started up at the hello — BEFORE `dc` is added to
            // _deviceConns. Against a local SQLite file it finishes in a millisecond or two, so
            // it looked for a client connection to tell, found none, and sent nothing. The tray
            // app then waits for a message that has already not been sent, and its switch stays
            // disabled for the life of the session: it will not offer a tick it cannot deliver.
            //
            // A race that the fast path loses every time is not a race anybody catches by
            // running it once. The send now happens where the connection is known to exist, and
            // PersistDeviceAsync's send stays as the correction once the stored value is read —
            // this one can only be as right as whatever is in memory at this moment.
            TellDeviceItsUnattendedState(dc.DeviceId);
            // And whether it may give somebody outside a code (3.9) — from here, for the same reason.
            _ = TellDeviceItsOutsiderStateAsync(dc.DeviceId);
        }

        // The agent is told whether the monthly health check belongs on its machine — from HERE,
        // after the connection is registered, for the reason set out just above: the copy sent at
        // the end of PersistDeviceAsync can finish before this socket is in the list. Both are
        // sent; the agent does nothing the second time.
        if (dc.Role == DeviceRole.Agent) TellAgentItsHealthCheck(dc.DeviceId);

        // And the company's look, if it has changed since this machine was installed or last told (roadmap 4.6) —
        // from here too, once the connection is registered, so a save made in this very moment cannot miss it.
        _ = TellDeviceItsBrandingAsync(dc);

        await receiveLoop;
        if (ws.DroppedForSilence)
            _log.LogInformation("[{Tenant}] device {Name} ({Role}) went silent — switched off, asleep or off its network; now offline",
                TenantName, hello.MachineName, hello.Role);

        lock (_lock)
        {
            _deviceConns.Remove(dc);
            if (_devices.TryGetValue(dc.DeviceId, out var info))
            {
                if (dc.Role == DeviceRole.Agent) info.AgentOnline = _deviceConns.Any(d => d.DeviceId == dc.DeviceId && d.Role == DeviceRole.Agent);
                else info.ClientOnline = _deviceConns.Any(d => d.DeviceId == dc.DeviceId && d.Role == DeviceRole.Client);
                // Online is the AGENT, not the permission: a machine with unattended access
                // switched off is still a machine that is switched on, and a device list that
                // called it offline would be lying about something a technician acts on. (Closed
                // by its person is the exception, and Recompute says so.)
                Recompute(info);
                RecomputeCanWatch(info);
                info.LastSeen = DateTimeOffset.UtcNow;
            }
            if (dc.Role == DeviceRole.Client)
                foreach (var c in _calls.Values.Where(c => c.DeviceId == dc.DeviceId && c.Status == CallStatus.Ringing).ToList())
                { c.Status = CallStatus.Cancelled; BroadcastTechs(new CallUpdateMsg { Call = c }); }
        }
        WatchSourceGone(dc);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(dc.DeviceId) });
    }

    private async Task PersistDeviceAsync(HelloMsg h, bool isNew, string publicAddress = "")
    {
        // This hello's share of the wait (RecordGate), given back once: when the record is in, or on the way out.
        bool read = false;
        void Read() { if (!read) { read = true; RecordRead(h.DeviceId); } }
        try
        {
            // For tools/healthcheckwire alone, never set in production. On a local SQLite file this
            // finishes before the socket is even registered, so the window the health check guard
            // exists for — a reinstalled machine connected but not yet recognised — never opens on a
            // development machine, and a harness could not tell the guard from its absence (it
            // could not: 52 of 52 with the guard removed). A slower database opens it for real.
            var delay = _cfg.GetValue<int>("Testing:PersistDeviceDelayMs");
            if (delay > 0) await Task.Delay(delay);

            await using var db = await _dbf.CreateDbContextAsync();
            var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.DeviceId == h.DeviceId);
            string? reinstalledFrom = null;

            if (d == null && !string.IsNullOrEmpty(h.HardwareId))
            {
                // Unknown DeviceId, but this hardware is already on file: the machine was
                // reinstalled and lost device.json. Take over the existing row so the client link,
                // the ticket history and the name stay with the machine instead of splitting off
                // into a fresh record (handover §15).
                //
                // Only when exactly one row matches. Two would mean the fingerprint is not unique
                // on this hardware, and merging the wrong pair is far worse than an extra row.
                var candidates = await db.Devices
                    .Where(x => x.TenantId == TenantId && x.HardwareId == h.HardwareId)
                    .Take(2).ToListAsync();
                if (candidates.Count == 1)
                {
                    d = candidates[0];
                    var previous = d.DeviceId;
                    reinstalledFrom = previous;
                    d.DeviceId = h.DeviceId;
                    d.DeviceKey = h.DeviceKey;
                    d.ReinstallCount++;
                    d.LastReinstallUtc = DateTime.UtcNow;
                    // Matched by hardware ALONE (2026-10-07, roadmap 4.4, §72's "left for soon after" item 1).
                    // The fingerprint is the board's UUID and Windows' MachineGuid, which any program on the
                    // machine can read; with the company's public key, a stranger's PC could say it is this
                    // one reinstalled and take its row — unattended permission, password and client link
                    // with it — and a technician would connect to the wrong computer. So the row is taken
                    // over (the client link and history SHOULD follow a real reinstall) but unattended access
                    // waits until a supervisor or the owner confirms it (ConfirmReinstallAsync). The tick is
                    // kept: it was the real customer's, and the impostor holds the tray app anyway.
                    d.ReinstallUnconfirmed = true;
                    _log.LogWarning("[{Tenant}] {Name} reinstalled — device {Old} is now {New}, record kept; unattended access "
                        + "waits until a supervisor or the owner confirms it is the same machine",
                        TenantName, h.MachineName, previous, h.DeviceId);
                }
            }

            if (d == null) { d = new Device { TenantId = TenantId, DeviceId = h.DeviceId, DeviceKey = h.DeviceKey }; db.Devices.Add(d); }
            if (!string.IsNullOrEmpty(h.HardwareId)) d.HardwareId = h.HardwareId;
            d.MachineName = h.MachineName; d.LastSeenUtc = DateTime.UtcNow;
            // The tray app is running again, so the machine is open again (ClientClosingMsg).
            if (h.Role == DeviceRole.Client) d.ClosedByUserUtc = null;
            d.Os = !string.IsNullOrWhiteSpace(h.Spec?.Os) ? h.Spec!.Os : h.Os;
            if (!string.IsNullOrEmpty(h.UserName)) d.UserName = h.UserName;
            if (h.Role == DeviceRole.Agent) { d.AgentVersion = h.Version; d.SelfUpdates = h.Can?.Contains(DeviceCan.Update) == true; }
            else if (h.Role == DeviceRole.Client) d.ClientVersion = h.Version;
            // Wake-on-LAN (2026-10-06): kept for when the machine is off. An older build sends no cards;
            // what an earlier one said is then left alone.
            if (h.Cards != null) d.NetworkCardsJson = Json.Serialize(Clean(h.Cards));
            if (publicAddress.Length > 0) d.PublicAddress = publicAddress;
            // Who it was installed for (2026-10-23): the installer window's name and email, kept ONCE per id — here, where the
            // stored record is read and the machine's two hellos are already taken in turn (RecordGate), so neither the
            // first install's race nor a later reconnect can put it back over a newer support request's name.
            ContactService.KeepInstalledFor(d, h.InstalledForId, h.InstalledForName, h.InstalledForEmail, DateTime.UtcNow);
            if (h.Spec != null)
            {
                d.Cpu = h.Spec.Cpu; d.Cores = h.Spec.Cores; d.RamMb = h.Spec.RamMb;
                d.DiskTotalGb = h.Spec.DiskTotalGb; d.DiskFreeGb = h.Spec.DiskFreeGb;
            }
            // For tools/firsthello alone, never set in production: holds the gap between looking the row up and
            // saving it open, which a local SQLite file closes too fast for a machine's two hellos to meet in.
            var gap = _cfg.GetValue<int>("Testing:PersistDeviceGapMs");
            if (gap > 0) await Task.Delay(gap);
            await db.SaveChangesAsync();
            // The old install id is gone from the database; take it out of every console's list too.
            if (reinstalledFrom != null) ForgetReinstalledId(reinstalledFrom, d.MachineName);

            // The stored unattended permission, put back into the live record. This runs as a
            // detached task, so it can finish AFTER the first DeviceUpdateMsg has gone out —
            // hence the broadcast below rather than relying on the one at connect time. Without
            // it a restarted server shows every machine as attended until somebody toggles
            // something, which is the wrong way round for a permission.
            lock (_lock)
                if (_devices.TryGetValue(h.DeviceId, out var live))
                {
                    live.UnattendedEnabled = d.UnattendedEnabled;
                    live.ReinstallUnconfirmed = d.ReinstallUnconfirmed;
                    // The name its person last gave stays with the MACHINE through a reinstall, as its owner does.
                    live.RequestedBy = d.RequestedBy; live.RequestedByUtc = AsUtc(d.RequestedByUtc); live.RequestedByEmail = d.RequestedByEmail;
                    Recompute(live);
                    // And its password, which stays with the MACHINE through a reinstall: the row
                    // taken over above is that machine's, under a new device id.
                    if (d.UnattendedPasswordHash.Length > 0) _unattendedPw[h.DeviceId] = (d.UnattendedPasswordSalt, d.UnattendedPasswordHash);
                    else _unattendedPw.Remove(h.DeviceId);
                    live.UnattendedPassword = d.UnattendedPasswordHash.Length > 0;
                }
            // The live record is the stored one now: a tick or a password waiting on it may go ahead.
            Read();
            // And who has it remembered, which a reinstall keeps with the row as it keeps the password.
            if (await ReloadSavedPasswordsAsync(db, d.Id, h.DeviceId)) PushSavedPasswords();
            TellDeviceItsUnattendedState(h.DeviceId);

            // publish the stored owner — company and person — back to the technicians' device list
            if (d.ClientId.HasValue || d.ContactId.HasValue)
            {
                var client = d.ClientId.HasValue
                    ? await db.Clients.FirstOrDefaultAsync(c => c.Id == d.ClientId.Value && c.TenantId == TenantId) : null;
                var contact = d.ContactId.HasValue
                    ? await db.Contacts.FirstOrDefaultAsync(c => c.Id == d.ContactId.Value && c.TenantId == TenantId) : null;
                lock (_lock)
                    if (_devices.TryGetValue(h.DeviceId, out var live))
                    {
                        live.ClientId = d.ClientId ?? 0; live.ClientName = client?.Name ?? "";
                        live.ContactId = d.ContactId ?? 0; live.ContactName = contact?.Name ?? "";
                    }
            }

            // The stored health check choice and the machine's last report, then — only now that
            // the record is known — the agent is told. See _healthLoaded.
            lock (_lock)
            {
                if (_devices.TryGetValue(h.DeviceId, out var live))
                {
                    live.HealthCheckMode = HealthCheckModes.Normalise(d.HealthCheckMode);
                    live.HealthCheckInstalled = d.HealthCheckInstalled;
                    live.HealthCheckNote = d.HealthCheckNote;
                    ApplyHealth(live);
                    // Outside help stays with the MACHINE through a reinstall, as its health check does.
                    live.OutsiderMode = OutsiderAccess.Normalise(d.OutsiderMode);
                    ApplyOutsider(live);
                }
                _healthLoaded.Add(h.DeviceId);
            }
            TellAgentItsHealthCheck(h.DeviceId);
            _ = TellDeviceItsOutsiderStateAsync(h.DeviceId);

            BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(h.DeviceId) });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] persist device", TenantName); }
        finally { Read(); }
    }

    private DeviceInfo Snapshot(string id) { lock (_lock) return _devices.TryGetValue(id, out var d) ? Json.Deserialize<DeviceInfo>(Json.Serialize(d))! : new DeviceInfo { DeviceId = id }; }

    /// <summary>
    /// Tells a machine's TRAY APP what the server holds for it. Sent when it connects and after
    /// every change, because the checkbox shows the server's answer rather than its own last
    /// click — a machine that was off while somebody changed this has to come back telling the
    /// truth, and the customer must never be looking at a tick that means nothing.
    /// </summary>
    private void TellDeviceItsUnattendedState(string deviceId, string passwordProblem = "", string requestId = "")
    {
        DeviceInfo? info; List<DeviceConn> clients;
        lock (_lock)
        {
            _devices.TryGetValue(deviceId, out info);
            clients = _deviceConns.Where(d => d.DeviceId == deviceId && d.Role == DeviceRole.Client).ToList();
        }
        if (info == null) return;
        foreach (var c in clients)
            c.Ws.Send(new UnattendedStateMsg
            {
                On = info.UnattendedEnabled, AgentOnline = info.AgentOnline,
                HasPassword = info.UnattendedPassword, PasswordProblem = passwordProblem, RequestId = requestId,
                AwaitingConfirm = info.ReinstallUnconfirmed
            });
    }

    /// <summary>
    /// The person at the machine setting, changing or removing its unattended password (2026-10-02).
    /// Only the CLIENT connection, for the reason SetUnattendedAsync gives: the agent is the thing
    /// the password guards, and nothing from the office may set or clear it.
    /// </summary>
    private async Task SetUnattendedPasswordAsync(DeviceConn dc, string password, string requestId = "")
    {
        if (dc.Role != DeviceRole.Client)
        {
            _log.LogWarning("[{Tenant}] a {Role} connection tried to change the unattended password on {Id} — refused",
                TenantName, dc.Role, dc.DeviceId);
            return;
        }
        bool remove = string.IsNullOrEmpty(password);
        var problem = remove ? "" : UnattendedPasswords.Problem(password);
        if (problem.Length > 0) { TellDeviceItsUnattendedState(dc.DeviceId, problem, requestId); return; }
        await WhenRecordReadAsync(dc.DeviceId);

        var (salt, hash) = remove ? ("", "") : Passwords.Create(password);
        string who;
        lock (_lock)
        {
            if (!_devices.TryGetValue(dc.DeviceId, out var info)) return;
            if (remove) _unattendedPw.Remove(dc.DeviceId); else _unattendedPw[dc.DeviceId] = (salt, hash);
            info.UnattendedPassword = !remove;
            // A new password starts the count again; so does removing it.
            _pwGuesses.Remove(dc.DeviceId);
            who = string.IsNullOrWhiteSpace(info.UserName) ? info.MachineName : $"{info.UserName} at {info.MachineName}";
        }

        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.DeviceId == dc.DeviceId);
            if (d != null)
            {
                d.UnattendedPasswordSalt = salt;
                d.UnattendedPasswordHash = hash;
                d.UnattendedPasswordChangedUtc = DateTime.UtcNow;
                d.UnattendedPasswordChangedBy = who;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] storing the unattended password for {Id}", TenantName, dc.DeviceId); }

        // A new password, or none: everybody who had the old one remembered must type the new one.
        await ForgetEveryoneAsync(dc.DeviceId);
        _log.LogInformation("[{Tenant}] unattended password {State} on {Who}", TenantName, remove ? "REMOVED" : "set", who);
        TellDeviceItsUnattendedState(dc.DeviceId, "", requestId);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(dc.DeviceId) });
    }

    /// <summary>Whether connecting to this machine without anybody asking takes its password: it
    /// allows unattended access AND has one. Attended machines ask the person instead, as ever.</summary>
    private bool NeedsUnattendedPassword(string deviceId)
    {
        lock (_lock)
            return _unattendedPw.ContainsKey(deviceId)
                && _devices.TryGetValue(deviceId, out var info) && MayGoUnattended(info);
    }

    /// <summary>
    /// Checks a technician's typed password for a machine and answers the console when it will not
    /// do. Empty is "I do not know it": refused, and NOT counted, so a console's reconnect loop can
    /// never lock a machine. A wrong one is counted against the machine.
    /// </summary>
    private bool PasswordLets(TechConn tc, string deviceId, string password)
    {
        (string Salt, string Hash) pw; string name; DateTime lockedUntil = default; bool locked;
        lock (_lock)
        {
            if (!_unattendedPw.TryGetValue(deviceId, out pw)) return true;
            name = _devices.TryGetValue(deviceId, out var info) ? info.MachineName : deviceId;
            // Typed right before by THIS technician, against this same password (2026-10-06): let
            // through without asking, and ahead of the lockout — the lockout stops guessing, and
            // somebody else guessing must not lock out the people who already know it.
            if (RemembersPassword(tc.Who.TechnicianId, deviceId)) return true;
            if (_pwGuesses.TryGetValue(deviceId, out var g) && g.Wrong >= PasswordTries && g.LockedUntil <= DateTime.UtcNow)
                _pwGuesses.Remove(deviceId);
            locked = _pwGuesses.TryGetValue(deviceId, out g) && g.Wrong >= PasswordTries;
            if (locked) lockedUntil = g.LockedUntil;
        }

        void Refuse(string message, bool noneGiven = false) =>
            tc.Ws.Send(new ErrorMsg { Message = message, NeedsPassword = true, NoPasswordGiven = noneGiven, DeviceId = deviceId });

        if (locked)
        {
            int mins = Math.Max(1, (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalMinutes));
            Refuse($"Too many wrong passwords for {name}. Connecting to it is refused for {mins} more minute{(mins == 1 ? "" : "s")}.");
            return false;
        }
        if (string.IsNullOrEmpty(password))
        {
            Refuse($"{name} has an unattended password. Type it to connect. "
                 + "(If your console has no box for it, update the console.)", noneGiven: true);
            return false;
        }
        if (Passwords.Verify(password, pw.Salt, pw.Hash))
        {
            lock (_lock) _pwGuesses.Remove(deviceId);
            RememberPassword(tc, deviceId, pw.Salt);
            return true;
        }

        int wrong;
        lock (_lock)
        {
            _pwGuesses.TryGetValue(deviceId, out var g);
            wrong = g.Wrong + 1;
            _pwGuesses[deviceId] = (wrong, wrong >= PasswordTries ? DateTime.UtcNow + PasswordLockout : default);
        }
        _log.LogWarning("[{Tenant}] {Tech} typed the WRONG unattended password for {Name} ({Wrong} of {Tries})",
            TenantName, tc.Who.DisplayName, name, wrong, PasswordTries);
        int left = PasswordTries - wrong;
        Refuse(left > 0
            ? $"That is not the unattended password for {name}. {left} more tr{(left == 1 ? "y" : "ies")} before it refuses for {PasswordLockout.TotalMinutes:0} minutes."
            : $"That is not the unattended password for {name}. Connecting to it is now refused for {PasswordLockout.TotalMinutes:0} minutes.");
        return false;
    }

    /// <summary>
    /// The person at the machine allowing, or withdrawing, unattended access.
    ///
    /// Only the CLIENT connection may do this — the agent is the thing being permitted and must
    /// not be able to permit itself, and a technician cannot grant themselves access to a machine
    /// they are not allowed into. That is the whole security property here, and it is one line.
    /// </summary>
    /// <summary>
    /// The person at the machine chose Exit in the tray app (<see cref="ClientClosingMsg"/>): the
    /// machine goes offline to everyone, any session on it ends, and nothing reaches it again until
    /// the tray app does. Clint, 2026-09-29: Exit "should close the program completely and show as
    /// offline on the dashboard and console" — the agent had gone on hosting as if nothing happened.
    /// </summary>
    private async Task CloseByUserAsync(DeviceConn dc)
    {
        // Only the tray app speaks for the person at the machine, as with SetUnattendedMsg.
        if (dc.Role != DeviceRole.Client)
        {
            _log.LogWarning("[{Tenant}] a {Role} connection tried to close {Id} for its user — refused", TenantName, dc.Role, dc.DeviceId);
            return;
        }

        List<string> sessions; string name;
        lock (_lock)
        {
            if (!_devices.TryGetValue(dc.DeviceId, out var info)) return;
            _closedByUser.Add(dc.DeviceId);
            Recompute(info);
            name = info.MachineName;
            sessions = _sessions.Values.Where(x => x.Info.DeviceId == dc.DeviceId).Select(x => x.Info.SessionId).ToList();
            foreach (var c in _calls.Values.Where(c => c.DeviceId == dc.DeviceId && c.Status == CallStatus.Ringing).ToList())
            { c.Status = CallStatus.Cancelled; BroadcastTechs(new CallUpdateMsg { Call = c }); }
        }
        _log.LogInformation("[{Tenant}] {Name}: the support app was closed by the person at it — offline until it is opened again", TenantName, name);
        foreach (var id in sessions) EndSession(id, $"The person at {name} closed the support app.");
        EndWatching(dc.DeviceId, $"The person at {name} closed the support app.");
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(dc.DeviceId) });

        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.DeviceId == dc.DeviceId);
            if (d != null) { d.ClosedByUserUtc = DateTime.UtcNow; await db.SaveChangesAsync(); }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] could not store that {Name} was closed", TenantName, name); }
    }

    private async Task SetUnattendedAsync(DeviceConn dc, bool on)
    {
        if (dc.Role != DeviceRole.Client)
        {
            _log.LogWarning("[{Tenant}] a {Role} connection tried to change unattended access on {Id} — refused",
                TenantName, dc.Role, dc.DeviceId);
            return;
        }
        await WhenRecordReadAsync(dc.DeviceId);

        string who = "";
        lock (_lock)
        {
            if (!_devices.TryGetValue(dc.DeviceId, out var info)) return;
            if (info.UnattendedEnabled == on) { TellDeviceItsUnattendedState(dc.DeviceId); return; }
            info.UnattendedEnabled = on;
            Recompute(info);
            who = string.IsNullOrWhiteSpace(info.UserName) ? info.MachineName : $"{info.UserName} at {info.MachineName}";
        }

        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.DeviceId == dc.DeviceId);
            if (d != null)
            {
                d.UnattendedEnabled = on;
                d.UnattendedChangedUtc = DateTime.UtcNow;
                d.UnattendedChangedBy = who;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] storing unattended access for {Id}", TenantName, dc.DeviceId); }

        // Said plainly in the log, because this is the one setting in the product that changes
        // who can get into somebody's computer.
        _log.LogInformation("[{Tenant}] unattended access {State} on {Who}", TenantName, on ? "ENABLED" : "disabled", who);
        TellDeviceItsUnattendedState(dc.DeviceId);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(dc.DeviceId) });
    }

    private void HandleDeviceMessage(DeviceConn dc, Msg m)
    {
        try
        {
            switch (m)
            {
                // Auto-update (2026-10-16): how an offered update is going, from the agent alone (TenantHub.Update.cs).
                case UpdateReportMsg ur:
                    TakeUpdateReport(dc, ur);
                    break;

                case SetUnattendedMsg su:
                    _ = SetUnattendedAsync(dc, su.On);
                    break;

                case SetUnattendedPasswordMsg sp:
                    _ = SetUnattendedPasswordAsync(dc, sp.Password, sp.RequestId ?? "");
                    break;

                // The one-person plans (2026-10-06; Free and Basic since 2026-10-25): nobody at the machine asks for help or lets
                // anybody in. The Basic version is attended only in the other direction — a technician connects and the person is asked.
                case RequestCallMsg or OutsiderCodeRequestMsg when Free:
                    dc.Ws.Send(new ErrorMsg { Message = FreePlan.NoAskingFor(_plan) });
                    break;

                case OutsiderCodeRequestMsg oq:
                    _ = MakeOutsiderCodeAsync(dc, oq.For ?? "");
                    break;

                case OutsiderCodeCancelMsg:
                    _ = CancelOutsiderCodeAsync(dc);
                    break;

                // The person at the machine uninstalled it from the tray app (2026-10-06) — TenantHub.Free.cs.
                case ClientUninstallingMsg:
                    _ = UninstalledAtMachineAsync(dc);
                    break;

                case ClientClosingMsg:
                    _ = CloseByUserAsync(dc);
                    break;

                case HealthCheckStatusMsg hs:
                    _ = RecordHealthCheckStatusAsync(dc, hs);
                    break;

                case WatchSampleMsg wsm:
                    RelayWatchSample(dc, wsm);
                    break;

                case WakeSentMsg wks:
                    WakeAnswered(dc, wks);
                    break;

                case RequestCallMsg r:
                {
                    CallInfo call;
                    // A subject and a description (2026-10-09) — and an older tray app's one box tidied into both.
                    var (subject, details) = SupportRequest.Words(r.Issue, r.Details);
                    // The picture stays HERE, for the ticket: the call that rings every console carries only that it exists.
                    var shot = r.Screenshot is { Length: > 0 } s && s.Length <= TicketPhotoService.MaxBytes ? s : null;
                    lock (_lock)
                    {
                        foreach (var old in _calls.Values.Where(c => c.DeviceId == dc.DeviceId && c.Status == CallStatus.Ringing)) old.Status = CallStatus.Cancelled;
                        call = new CallInfo
                        {
                            CallId = Guid.NewGuid().ToString("N"), DeviceId = dc.DeviceId, MachineName = _devices[dc.DeviceId].MachineName,
                            ContactName = r.ContactName, Issue = subject, Details = details, HasScreenshot = shot != null, Status = CallStatus.Ringing
                        };
                        _calls[call.CallId] = call;
                    }
                    dc.Ws.Send(new CallUpdateMsg { Call = call });
                    BroadcastTechs(new RingMsg { Call = call });
                    // every support request opens a ticket automatically
                    _ = OpenTicketForCallAsync(call, shot);
                    // and the name they gave becomes the machine's, until somebody links it (2026-10-18)
                    _ = RememberRequestedByAsync(dc.DeviceId, r.ContactName, r.ContactEmail);
                    break;
                }
                case CancelCallMsg c:
                {
                    List<CallInfo> cancelled = new();
                    lock (_lock)
                        foreach (var call in _calls.Values.Where(x => x.DeviceId == dc.DeviceId && x.Status == CallStatus.Ringing && (c.CallId == null || x.CallId == c.CallId)))
                        { call.Status = CallStatus.Cancelled; cancelled.Add(call); }
                    foreach (var call in cancelled) { BroadcastTechs(new CallUpdateMsg { Call = call }); dc.Ws.Send(new CallUpdateMsg { Call = call }); }
                    break;
                }
                // The person at the machine answering about their microphone (3.3), from the tray
                // app. Carried to the session's host — the only thing on the machine that can open
                // it — unless the session is unattended, where Clint's rule is that the microphone
                // is never opened, whatever arrives. Only the CLIENT connection may answer: the
                // agent is SYSTEM, and nobody clicked anything there.
                case VoiceMicAnswerMsg va when dc.Role == DeviceRole.Client:
                {
                    Session? live; lock (_lock) live = _sessions.Values.FirstOrDefault(x => x.Info.DeviceId == dc.DeviceId);
                    if (live == null) break;
                    if (live.Info.Unattended)
                    {
                        _log.LogWarning("[{Tenant}] a microphone answer from {Machine} was refused: the session is unattended", TenantName, live.Info.MachineName);
                        break;
                    }
                    live.Host?.Send(va);
                    break;
                }

                case ChatMsg chat:
                    chat.DeviceId = dc.DeviceId; chat.FromTech = false; chat.Ts = DateTimeOffset.UtcNow;
                    if (string.IsNullOrWhiteSpace(chat.From)) chat.From = _devices[dc.DeviceId].MachineName;
                    RouteChat(chat);
                    break;

                // The agent answering a Ctrl+Alt+Del. It arrives on the DEVICE socket because
                // that is the only connection a session-0 service has, so it has to be carried
                // across to the technicians watching this machine. No session id is needed: a
                // device has at most one session, which is also why StartSession hands back the
                // existing one rather than making a second.
                case SysActionResultMsg r:
                {
                    List<WsConnection> viewers;
                    lock (_lock)
                        viewers = _sessions.Values.Where(x => x.Info.DeviceId == dc.DeviceId)
                                           .SelectMany(x => x.Viewers.Keys.Where(k => !x.VisitorSockets.Contains(k))).ToList();   // never a visitor (3.9)
                    foreach (var v in viewers) v.Send(r);
                    _log.LogInformation("[{Tenant}] {Machine} reports {Action}: ok={Ok} {Detail}",
                        TenantName, _devices.TryGetValue(dc.DeviceId, out var d) ? d.MachineName : dc.DeviceId, r.Action, r.Ok, r.Detail);
                    break;
                }
            }
        }
        catch (Exception ex) { _log.LogError(ex, "[{Tenant}] device message", TenantName); }
    }

    // ------------------------------------------------------------------ chat
    private void RouteChat(ChatMsg chat)
    {
        List<TechConn> techs; List<DeviceConn> clients; List<WsConnection> viewers;
        lock (_lock)
        {
            _chat.AddLast(chat); while (_chat.Count > 500) _chat.RemoveFirst();
            techs = _techs.ToList();
            clients = _deviceConns.Where(d => d.DeviceId == chat.DeviceId && d.Role == DeviceRole.Client).ToList();
            if (WebConn(chat.DeviceId ?? "") is { } web) clients.Add(web);
            // Session windows listen on their own viewer socket, not on the technician's dashboard
            // connection, so a reply that only went to _techs never reached the window it belongs to.
            viewers = _sessions.Values.Where(s => s.Info.DeviceId == chat.DeviceId)
                               .SelectMany(s => s.Viewers.Keys.Where(k => !s.VisitorSockets.Contains(k))).ToList();   // the customer's chat with THIS company: never a visitor's (3.9)
        }
        foreach (var t in techs) t.Ws.Send(chat);
        foreach (var c in clients) c.Ws.Send(chat);
        foreach (var v in viewers) v.Send(chat);
        // keep the conversation with the customer on their ticket, as a normal message rather than
        // a system entry so it reads as the transcript it is
        if (!string.IsNullOrEmpty(chat.DeviceId))
            _ = LogToDeviceTicketAsync(chat.DeviceId!, chat.From, chat.Text, isSystem: false);
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbf.CreateDbContextAsync();
                db.Chat.Add(new ChatLine { TenantId = TenantId, DeviceId = chat.DeviceId ?? "", From = chat.From, FromTech = chat.FromTech, Text = chat.Text, TsUtc = chat.Ts.UtcDateTime });
                await db.SaveChangesAsync();
            }
            catch { }
        });
    }

    // ------------------------------------------------------------------ calls & sessions
    private void AcceptCall(TechConn tc, string callId)
    {
        CallInfo? call; Session? existing = null;
        // Two machines at once, at most (2026-10-15). Before the call is marked taken, so it keeps ringing for a colleague.
        string? callDevice; lock (_lock) callDevice = _calls.TryGetValue(callId, out var ringing) ? ringing.DeviceId : null;
        if (callDevice != null && RefusedOverSessionLimit(tc, callDevice)) return;
        lock (_lock)
        {
            if (!_calls.TryGetValue(callId, out call)) { tc.Ws.Send(new ErrorMsg { Message = "Call no longer exists." }); return; }
            if (call.Status == CallStatus.Accepted && call.SessionId != null && _sessions.TryGetValue(call.SessionId, out existing)) { tc.Ws.Send(new SessionReadyMsg { Session = existing.Info }); return; }
            if (call.Status != CallStatus.Ringing) { tc.Ws.Send(new ErrorMsg { Message = "Call is no longer ringing." }); return; }
            call.Status = CallStatus.Accepted; call.AcceptedBy = tc.Who.DisplayName;
        }
        var session = StartSession(call.DeviceId, tc, call);
        if (session == null) { lock (_lock) { call.Status = CallStatus.Ringing; call.AcceptedBy = null; } tc.Ws.Send(new ErrorMsg { Message = "The client is no longer connected." }); return; }
        call.SessionId = session.Info.SessionId;
        BroadcastTechs(new CallUpdateMsg { Call = call });
        foreach (var d in DeviceConns(call.DeviceId, DeviceRole.Client)) d.Ws.Send(new CallUpdateMsg { Call = call });
    }

    /// <summary>
    /// The shortest thing that can be a reason. Three characters, the same floor the customer's
    /// own request dialog uses and for the same argument (§30): any rule strong enough to reject
    /// "asdf" also rejects somebody in a hurry writing something true and short. It exists to stop
    /// a keystroke dismissing the question, not to grade the answer.
    /// </summary>
    private const int MinReason = 3;

    /// <summary>
    /// A connection with no reason (2026-10-09). Clint: "Reconnecting within 30 minutes should automatically be part of
    /// the same ticket with no reason necessary." So the console no longer asks first: it connects without one, and
    /// if the machine has a ticket to carry on — <see cref="TicketService.ActiveForDeviceAsync"/>, the same question
    /// AttachSessionToTicketAsync asks a moment later — the session simply starts on it. If not, the console is told
    /// a reason is needed (<see cref="ErrorMsg.NeedsReason"/>) and asks, as every connection used to.
    /// </summary>
    private async Task ConnectWithoutReasonAsync(TechConn tc, string deviceId, string password)
    {
        try
        {
            if (await _tickets.ActiveForDeviceAsync(TenantId, deviceId) != null)
            {
                ConnectDevice(tc, deviceId, "", password, carryingOn: true);
                return;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] looking for a ticket to carry on", TenantName); }
        tc.Ws.Send(new ErrorMsg { Message = "Say why you are connecting — it becomes the ticket's description.", NeedsReason = true, DeviceId = deviceId });
    }

    private void ConnectDevice(TechConn tc, string deviceId, string reason, string password = "", bool carryingOn = false)
    {
        // Two machines at once, at most (2026-10-15): asked first, so a third machine's person is never prompted and
        // no ticket is opened for a session that will not be. Going back to a machine already open is never refused.
        if (RefusedOverSessionLimit(tc, deviceId)) return;
        Session? existing; bool admitted = true;
        lock (_lock)
        {
            existing = _sessions.Values.FirstOrDefault(s => s.Info.DeviceId == deviceId);
            if (existing?.Admitted != null) admitted = existing.Admitted.Contains(tc.Who.TechnicianId);
        }
        if (existing != null)
        {
            // A session opened with the machine's password admits only those who typed it.
            if (!admitted)
            {
                if (!PasswordLets(tc, deviceId, password)) return;
                lock (_lock) existing.Admitted?.Add(tc.Who.TechnicianId);
            }
            tc.Ws.Send(new SessionReadyMsg { Session = existing.Info });
            return;
        }

        // No reason, no session. The console asks before it sends, so reaching here without one is
        // an old console or somebody driving the socket by hand — and the ticket this is about to
        // open would otherwise say nothing at all about why anybody went in. Checked on the SERVER
        // because that is where the ticket is written.
        // The free version has no tickets, so there is nothing for a reason to describe and it is not asked.
        // Nor is it when there is a ticket to carry on (2026-10-09) — which only the database can say.
        if (!Free && !carryingOn && (reason ?? "").Trim().Length < MinReason)
        {
            _ = ConnectWithoutReasonAsync(tc, deviceId, password);
            return;
        }

        // THE UNATTENDED PASSWORD (2026-10-02). A machine that allows unattended access and has a
        // password lets nobody in without it — the owner and supervisors included, by Clint's
        // decision: it exists for a person's own machine, which the company does not get round.
        // Checked HERE, the one door a technician-started session comes through; a call is the
        // person at the machine asking, and never needs it.
        // The Free version (2026-10-06): unattended, and only with a password. Its installer sets both; this
        // is the same rule kept where it cannot be got round. The Basic version (2026-10-25, §163) is not stopped here: without
        // both it goes attended, the person asked (StartSession).
        if (UnattendedOnly)
        {
            bool on, hasPw, unconfirmed;
            lock (_lock)
            {
                on = _devices.TryGetValue(deviceId, out var fi) && fi.UnattendedEnabled;
                unconfirmed = fi?.ReinstallUnconfirmed ?? false;
                hasPw = _unattendedPw.ContainsKey(deviceId);
            }
            if (unconfirmed) { tc.Ws.Send(new ErrorMsg { Message = ReinstallUnconfirmedWords }); return; }
            if (!on || !hasPw) { tc.Ws.Send(new ErrorMsg { Message = on ? FreePlan.NeedsPassword : FreePlan.NoAttended }); return; }
        }
        bool withPassword = NeedsUnattendedPassword(deviceId);
        if (withPassword && !PasswordLets(tc, deviceId, password)) return;

        if (StartSession(deviceId, tc, null, (reason ?? "").Trim(), withPassword) != null) return;

        // "Device is offline" was the only answer this could give, and since unattended access
        // became something a customer can withhold it is often the wrong one. A machine that is
        // switched on, connected, and simply has not granted unattended access is a completely
        // different situation with a completely different fix — one of them is "ask them to tick
        // the box", and a technician told "offline" will go and check the network instead.
        bool agentOnline, allowed, clientOnline, closed, unconfirmedHere;
        lock (_lock)
        {
            closed = _closedByUser.Contains(deviceId);
            _devices.TryGetValue(deviceId, out var info);
            agentOnline = info?.AgentOnline ?? false;
            allowed = info?.UnattendedEnabled ?? false;
            unconfirmedHere = info?.ReinstallUnconfirmed ?? false;
            clientOnline = info?.ClientOnline ?? false;
        }
        tc.Ws.Send(new ErrorMsg
        {
            Message = closed
                ? "The person at that machine closed the support app, so it cannot be reached. It comes back "
                  + "when they open it again, from the desktop shortcut or by signing in to Windows."
                : agentOnline && allowed && unconfirmedHere && !clientOnline && !Free
                ? ReinstallUnconfirmedWords
                // The Basic version (§163) asks through the agent alone; one from before 2 October cannot ask.
                : agentOnline && Free
                ? "The support service on that computer is too old to ask the person there. Run the installer on it again."
                : agentOnline && !allowed && !clientOnline
                ? "That machine has not allowed unattended access. Ask the person at it to tick "
                  + "\"Allow unattended access\" in the support app, or ask them to request support."
                : "Device is offline."
        });
    }

    private Session? StartSession(string deviceId, TechConn tc, CallInfo? call, string reason = "", bool withPassword = false)
    {
        DeviceConn? hostConn; Session s; bool unattendedWasWanted = false, askConsent = false;
        lock (_lock)
        {
            // THE ENFORCEMENT POINT for unattended access, and the reason the permission is
            // stored on the server rather than on the machine.
            //
            // The distinction is not "agent or client", it is WHO ASKED. A call means the person
            // at the machine asked for help and is sitting there, so the agent may host it — and
            // it should, because an agent-hosted session survives UAC and the logon screen.
            // A technician connecting out of the blue is the thing unattended access permits, so
            // without permission the agent is not offered: the session falls back to the tray
            // app, which puts a consent prompt in front of a human, or it does not happen.
            // Closed by the person at it: nobody gets in, by any route, until they open it again.
            if (_closedByUser.Contains(deviceId)) return null;
            bool mayUseAgent = call != null || (_devices.TryGetValue(deviceId, out var di) && MayGoUnattended(di)
                // A one-person plan goes unattended only with a password (2026-10-06); the Basic version without one is asked (§163).
                && (!Free || _unattendedPw.ContainsKey(deviceId)));
            hostConn = (mayUseAgent ? _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Agent) : null);
            // The Free version: the agent with permission, or nobody — never a consent prompt, never the tray app.
            if (UnattendedOnly && hostConn == null) return null;
            // ATTENDED, THROUGH THE AGENT (internal testing, 2026-10-02). Without permission the
            // agent used not to be offered at all, so every attended session was hosted by the
            // tray app, as the person — and a session run as the person cannot see a UAC prompt
            // (black), cannot touch an elevated window (Task Manager would not close), and runs
            // no script as administrator (DISM: error 740). The permission was never about WHICH
            // process hosts; it is about whether anybody is ASKED. So an agent that can ask is
            // handed the session with AskConsent set: its SYSTEM host puts the same prompt in
            // front of the person and connects nothing until they allow it. An older agent that
            // has not said it can ask would start the session WITHOUT asking, so it is never
            // offered one, and the tray app hosts as before.
            if (hostConn == null)
            {
                hostConn = _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Agent && d.CanAskConsent);
                askConsent = hostConn != null;
            }
            // The Basic version (2026-10-25, §163): attended ONLY through an agent that asks — no tray app on its computers, and
            // a tray app left from another plan must never host a session nobody was asked about.
            if (Free && hostConn == null) return null;
            hostConn ??= _deviceConns.FirstOrDefault(d => d.DeviceId == deviceId && d.Role == DeviceRole.Client)
                    // A browser caller hosts from their own page, and only when they rang (3.8.2).
                    ?? (call != null ? WebConn(deviceId) : null);
            if (hostConn == null) return null;
            bool web = IsWeb(deviceId);

            // The permission was granted, the agent was allowed, and there was no agent to use.
            // The session still happens — attended, with a consent prompt the customer was told
            // they would not get — and until 2026-09-21 that fell through in total silence: no
            // log line, nothing in the device list, and a technician watching a session start
            // normally. It is the whole of Clint's report, and the server is the one place that
            // can see both halves at once, so it says so here.
            unattendedWasWanted = mayUseAgent && call == null && hostConn.Role == DeviceRole.Client;
            // An attended session the agent hosts is still ATTENDED: after a restart somebody has
            // to be there to say yes again, which is what this flag tells the console.
            bool unattended = hostConn.Role == DeviceRole.Agent && !askConsent;
            s = new Session
            {
                HostToken = Passwords.RandomToken(), CallId = call?.CallId, StartedBy = tc.Who.DisplayName, Unattended = unattended,
                TellTrayWhenAllowed = askConsent,
                Admitted = withPassword ? new HashSet<int> { tc.Who.TechnicianId } : null,
                Info = new SessionInfo
                {
                    SessionId = Guid.NewGuid().ToString("N"), DeviceId = deviceId,
                    MachineName = web ? WebMachineName(deviceId) : _devices[deviceId].MachineName,
                    StartedBy = tc.Who.DisplayName,
                    // Carried to the console so a reboot can say whether a host will come back on
                    // its own. An attended-only machine needs somebody sitting at it to accept.
                    Unattended = unattended,
                    ViewOnly = web,
                    HostedByApp = hostConn.Role == DeviceRole.Client && !web
                }
            };
            _sessions[s.Info.SessionId] = s;
        }
        _registry.RegisterSession(s.Info.SessionId, TenantId);
        // A call carries the CUSTOMER's own reason and already has a ticket from it; only an
        // unattended connection brings the technician's.
        _ = AttachSessionToTicketAsync(s.Info.SessionId, deviceId, s.Info.MachineName, tc.Who.DisplayName, call != null, reason);
        hostConn.Ws.Send(new StartSessionMsg { SessionId = s.Info.SessionId, HostToken = s.HostToken, TechnicianName = tc.Who.DisplayName, AskConsent = askConsent, ShowNotice = Free });
        if (hostConn.Role == DeviceRole.Agent && !askConsent)
            TellTrayConnected(s);
        tc.Ws.Send(new SessionReadyMsg { Session = s.Info });
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        _ = LogSessionAsync(s, null);
        _log.LogInformation("[{Tenant}] session {Id} on {Name} by {Tech} (host role: {Role}{Asking})",
            TenantName, s.Info.SessionId, s.Info.MachineName, tc.Who.DisplayName, hostConn.Role, askConsent ? ", attended — asking first" : "");
        if (unattendedWasWanted)
            _log.LogWarning("[{Tenant}] {Name} has unattended access ALLOWED but no agent service connected, so this "
                          + "session is attended and the person there is being asked to accept. The agent is installed "
                          + "by every client install since 2026-09-20 — check it is running on that machine.",
                TenantName, s.Info.MachineName);

        // Unattended hosting depends on the service launching a process into the user's desktop,
        // which can fail on a locked-down machine. If the agent's host does not appear, fall back
        // to the attended client app rather than leaving the technician with a dead session.
        if (hostConn.Role == DeviceRole.Agent) _ = FallBackToClientAsync(s, tc.Who.DisplayName);
        return s;
    }

    /// <summary>The tray app's "a technician is connected" — for a session the AGENT is hosting,
    /// which the tray app would otherwise know nothing about.</summary>
    private void TellTrayConnected(Session s)
    {
        foreach (var c in DeviceConns(s.Info.DeviceId, DeviceRole.Client))
            c.Ws.Send(new StartSessionMsg { SessionId = s.Info.SessionId, HostToken = "", TechnicianName = s.StartedBy });
    }

    // A browser caller (3.8.2) is the "client" of its own pretend device: see TenantHub.Web.cs.
    private List<DeviceConn> DeviceConns(string deviceId, DeviceRole role)
    {
        lock (_lock)
        {
            var list = _deviceConns.Where(d => d.DeviceId == deviceId && d.Role == role).ToList();
            if (role == DeviceRole.Client && WebConn(deviceId) is { } w) list.Add(w);
            return list;
        }
    }

    /// <summary>
    /// Waits for the agent-launched host to connect. If it never does, asks the user-session client
    /// app to host instead, so the session still works.
    /// </summary>
    private async Task FallBackToClientAsync(Session s, string tech)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12));
            bool alive, hosted;
            lock (_lock) { alive = _sessions.ContainsKey(s.Info.SessionId); hosted = s.Host != null; }
            if (!alive || hosted) return;

            // The free version has no tray app to fall back to, and must never be hosted as the person asked
            // nothing (2026-10-06): the session ends, saying why.
            if (Free)
            {
                EndSession(s.Info.SessionId, "The support service on that computer did not start the session. Restart the computer and connect again.");
                return;
            }

            // Somebody from another company (3.9) may only fall back to a tray app that ALWAYS asks — an
            // older one decides from the branding, and might not. None: the session ends, said plainly.
            var client = DeviceConns(s.Info.DeviceId, DeviceRole.Client).FirstOrDefault(c => s.Visitor == null || c.AlwaysAsk);
            if (client == null)
            {
                _log.LogWarning("[{Tenant}] session {Id}: the unattended agent's host never connected and no client app is available",
                    TenantName, s.Info.SessionId);
                if (s.Visitor != null) EndSession(s.Info.SessionId, "The support app on that computer could not ask the person there, so nobody was let in.");
                return;
            }
            _log.LogWarning("[{Tenant}] session {Id}: unattended host did not connect, falling back to the client app",
                TenantName, s.Info.SessionId);
            // The tray app is hosting after all, so nothing will bring a host back by itself
            // after a reboot. The console shows that, so it has to be corrected here too.
            s.Unattended = false; s.Info.Unattended = false;
            // And it runs as the person now, with everything that costs; the tray app hosting is
            // told by the StartSessionMsg below, so nothing is left waiting for a "yes" to tell it.
            s.Info.HostedByApp = true; s.TellTrayWhenAllowed = false;
            BroadcastTechs(new SessionReadyMsg { Session = s.Info });
            client.Ws.Send(new StartSessionMsg { SessionId = s.Info.SessionId, HostToken = s.HostToken, TechnicianName = tech, AskConsent = s.Visitor != null, VisitorCompany = s.Visitor?.Company ?? "" });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] host fallback", TenantName); }
    }

    public void EndSession(string sessionId, string reason)
    {
        Session? s; CallInfo? call = null;
        lock (_lock)
        {
            if (!_sessions.Remove(sessionId, out s)) return;
            if (s.CallId != null && _calls.TryGetValue(s.CallId, out call)) call.Status = CallStatus.Ended;
            var now = DateTime.UtcNow;
            foreach (var old in _endedReasons.Where(kv => now - kv.Value.At > EndedRemembered).Select(kv => kv.Key).ToList()) _endedReasons.Remove(old);
            _endedReasons[sessionId] = (reason, now);
            // Said on the broadcast below, so a console that was still WAITING — nobody had joined
            // yet — can tell "over, and why" from "not started yet". Both used to be no host and
            // no technicians.
            s.Info.Ended = true; s.Info.EndReason = reason; s.Info.AwaitingConsent = false;
        }
        _registry.UnregisterSession(sessionId);
        _log.LogInformation("[{Tenant}] session {Id} ended: {Reason}", TenantName, sessionId, reason);
        // A copy straight to or from this machine stops with it, and both ends are told before the host is.
        BridgesLost(s, $"the session on {s.Info.MachineName} ended");
        // A technician's code for it stops, and anybody waiting on one is told (§155).
        InviteSessionEnded(s, reason);
        var ended = new SessionEndedMsg { SessionId = sessionId, Reason = reason };
        int ticketId; lock (_lock) { _sessionTickets.TryGetValue(sessionId, out ticketId); _sessionTickets.Remove(sessionId); s.EndedTicketId = ticketId; }
        // The host is TOLD and left to close itself, rather than cut off: it sends what its
        // clipboard ledger still holds first (3.5.3), and a close sent straight after "ended" raced
        // that last line and won. An older host that ignores the message is closed after a moment.
        s.Host?.Send(ended);
        var hostClosed = CloseHostAfterAsync(s.Host);
        foreach (var v in s.Viewers.Keys.ToList()) { v.Send(ended); _ = v.CloseAsync("ended"); }
        foreach (var d in DeviceConns(s.Info.DeviceId, DeviceRole.Client)) d.Ws.Send(ended);
        foreach (var d in DeviceConns(s.Info.DeviceId, DeviceRole.Agent)) d.Ws.Send(ended);
        if (call != null) BroadcastTechs(new CallUpdateMsg { Call = call });
        s.Info.HostConnected = false; s.Info.Technicians.Clear();
        // Overtake any grace timer still counting this session down, and drop the held flag so a
        // dashboard row cannot be left saying "reconnecting" about a session that is over.
        lock (_lock) { s.GraceToken++; s.HeldSince = null; s.Info.Held = false; s.Listening = false; }
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        _ = LogSessionAsync(s, reason);

        if (ticketId != 0)
        {
            var mins = (int)(DateTime.UtcNow - s.Info.Started.UtcDateTime).TotalMinutes;
            // Written once the host has gone, so its last clipboard line is above this one and the
            // ticket reads in the order things happened.
            _ = Task.Run(async () =>
            {
                await hostClosed;
                await LogToTicketAsync(ticketId, s.StartedBy, $"Session ended after {Math.Max(1, mins)} min. {reason}");
            });
        }
    }

    /// <summary>How long an ended session's host is given to say its last and close by itself.</summary>
    private static readonly TimeSpan HostLastWords = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Waits for the host to close its own socket after being told the session ended, then closes
    /// it anyway. A host already gone, or none at all, costs nothing.
    /// </summary>
    private static async Task CloseHostAfterAsync(WsConnection? host)
    {
        if (host == null) return;
        var until = DateTime.UtcNow + HostLastWords;
        while (DateTime.UtcNow < until && host.IsOpen) await Task.Delay(50);
        // The line it sent just before closing is relayed by its receive loop; a beat for that to
        // reach the database keeps "Session ended" below it.
        await Task.Delay(250);
        try { await host.CloseAsync("ended"); } catch { }
    }

    private async Task LogSessionAsync(Session s, string? endReason)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var row = await db.Sessions.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.SessionId == s.Info.SessionId);
            if (row == null) { row = new SessionLog { TenantId = TenantId, SessionId = s.Info.SessionId, DeviceId = s.Info.DeviceId, MachineName = s.Info.MachineName, StartedBy = s.StartedBy, Unattended = s.Unattended }; db.Sessions.Add(row); }
            row.Technicians = string.Join(", ", s.Info.Technicians);
            if (endReason != null) { row.EndedUtc = DateTime.UtcNow; row.EndReason = endReason; }
            // Somebody from another company (3.9): their company keeps its own line of it too.
            if (s.Visitor != null) await RecordForVisitorAsync(db, s, endReason);
            await db.SaveChangesAsync();
        }
        catch { }
    }

    // ------------------------------------------------------------------ session relay
    /// <param name="asking">The host connected with <c>consent=asking</c>: the question is on the
    /// person's screen, and the session is not "connected" until it sends ConsentGrantedMsg.</param>
    /// <param name="askSeconds">How long that question waits before declining by itself.</param>
    public async Task RunSessionHostAsync(string sessionId, string hostToken, WsConnection ws, CancellationToken ct, bool asking = false, int askSeconds = 0)
    {
        await UseCompanyClockAsync();
        Session? s; bool bad;
        lock (_lock)
        {
            bad = !_sessions.TryGetValue(sessionId, out s) || s!.HostToken != hostToken;
            if (!bad)
            {
                s!.Host = ws;
                // Not "connected" while the person is deciding: an older console opens the session
                // window on HostConnected, and it used to open it only once they had said yes.
                s.Info.HostConnected = !asking;
                s.Info.AwaitingConsent = asking;
                s.Info.ConsentSeconds = asking ? Math.Clamp(askSeconds, 0, 3600) : 0;
            }
        }
        if (bad)
        {
            _log.LogWarning("[{Tenant}] a host tried to join session {Id} with a bad/expired token", TenantName, sessionId);
            ws.Send(new ErrorMsg { Message = "Invalid session" }); await ws.CloseAsync(); return;
        }
        _log.LogInformation("[{Tenant}] host connected to session {Id}{Asking}", TenantName, sessionId, asking ? " — asking the person at the machine first" : "");
        ws.OnMessage += m =>
        {
            if (m is DeclineSessionMsg d) { EndSession(sessionId, string.IsNullOrWhiteSpace(d.Reason) ? "The client declined the session." : d.Reason); return; }
            if (m is ConsentGrantedMsg granted)
            {
                bool tellTray, files;
                lock (_lock)
                {
                    if (!s!.Info.AwaitingConsent) return;
                    s.Info.AwaitingConsent = false; s.Info.HostConnected = true;
                    // Somebody from outside (3.9): files only if the person ticked it, for this session only.
                    files = s.Visitor != null && granted.Files;
                    s.Info.VisitorFiles = files;
                    tellTray = s.TellTrayWhenAllowed; s.TellTrayWhenAllowed = false;
                }
                _log.LogInformation("[{Tenant}] session {Id}: the person at {Machine} allowed it", TenantName, sessionId, s.Info.MachineName);
                _ = LogToSessionTicketAsync(sessionId, s.StartedBy, $"The person at {s.Info.MachineName} allowed the connection"
                    + (s.Visitor == null ? "." : files ? " — and ticked that they may copy files." : ", without files."));
                if (tellTray) TellTrayConnected(s);
                // End-to-end encryption: a host acts on nothing before the yes, so it is told whether to seal now.
                RecomputeE2e(s, force: true);
                SendParticipants(s);
                BroadcastTechs(new SessionReadyMsg { Session = s.Info });
                return;
            }
            // Nothing else is believed from a host that has not been allowed in yet.
            bool waiting; lock (_lock) waiting = s!.Info.AwaitingConsent;
            if (waiting) return;
            // A copy straight to or from another machine is over, as this host sees it (TenantHub.Bridge.cs).
            if (m is BridgeEndMsg bridgeEnd) { BridgeEndFrom(s!, bridgeEnd); return; }
            // remember the host's one-off announcements so late joiners still get them
            if (m is MonitorsMsg mm) { lock (_lock) s!.LastMonitors = mm; }
            else if (m is SysInfoMsg si) { lock (_lock) s!.LastSysInfo = si; }
            // The privacy screen (1.7): remembered for late joiners, and written on the ticket -
            // blacking out a customer's monitors is something done TO their machine, like a reboot.
            else if (m is PrivacyStateMsg pst)
            {
                bool changed; lock (_lock) { changed = s!.LastPrivacy?.On != pst.On || pst.Error.Length > 0; s.LastPrivacy = pst; }
                int tid; lock (_lock) _sessionTickets.TryGetValue(sessionId, out tid);
                if (tid != 0 && changed)
                {
                    var who = s!.Info.Technicians.LastOrDefault() ?? s.StartedBy;
                    _ = LogToTicketAsync(tid, who,
                        pst.Error.Length > 0 ? $"The privacy screen could not be turned on: {pst.Error}."
                        : pst.On ? $"Privacy screen on: {s.Info.MachineName}'s screen was blacked out while the work was done."
                        : "Privacy screen off.");
                }
            }
            // A technician listening to the machine's sound is disclosed to the person at it.
            // The host reports it on the session socket, which only technicians can see, so the
            // hub has to carry it to the customer's tray app on the device connection — the same
            // route chat already takes. An unattended machine can be listened to with nobody
            // there, and a machine that can hear music can hear a speakerphone call; the customer
            // being able to see that is the point.
            if (m is AudioStatusMsg au)
            {
                // Remembered as well as relayed: a tray app that reconnects mid-session gets the
                // current state back (SessionStateMsg), and a disclosure that is lost by a blink
                // of the customer's own socket is worse than one that was never built.
                lock (_lock) s!.Listening = au.Listening && au.Error.Length == 0;
                foreach (var c in DeviceConns(s!.Info.DeviceId, DeviceRole.Client)) c.Ws.Send(m);
            }
            // A voice call (3.3) takes the same route as the listening disclosure, for the same
            // reason and one more: the person at the machine is ASKED here, about their microphone.
            if (m is VoiceStateMsg vs) PassOnVoiceState(s!, sessionId, vs);
            // A file moving on or off the machine takes the same route, for a weaker version of
            // the same reason. Clint's decision on 2026-09-15 was that this is LOGGED rather than
            // announced: the tray app writes it into its chat log and the ticket records it, with
            // no banner and no balloon, so it is there afterwards without interrupting the person
            // being helped. The host is the sender because it is the only end that knows what
            // actually moved rather than what was asked for.
            if (m is FileActivityMsg fa)
            {
                // Straight to or from another machine (TenantHub.Bridge.cs): the person here is told "another computer" —
                // it may be another customer's — and the ticket and the technicians get its name, filled in HERE.
                fa.OtherMachine = "";
                foreach (var c in DeviceConns(s!.Info.DeviceId, DeviceRole.Client)) c.Ws.Send(m);
                fa.OtherMachine = OtherMachineOf(s!, fa) ?? "";
                int tid; lock (_lock) { if (!_sessionTickets.TryGetValue(sessionId, out tid)) tid = s!.EndedTicketId; }
                // Technicians is cleared when the session ends; the one who started it is who
                // the host's last line is about.
                var who = s!.Info.Technicians.LastOrDefault() ?? s.StartedBy;
                var line = fa.Describe(who).Replace("this computer", s.Info.MachineName);
                if (tid != 0) _ = LogToTicketAsync(tid, who, line);
                // The ticket is written as the session starts, and a line sent at once can arrive before
                // it is known. It used to be dropped without a word; PostgreSQL's few milliseconds more
                // lost it every time (clipledger, HANDOVER §119). Wait for the ticket, as a script line does.
                else if (!Free) _ = LogToSessionTicketAsync(sessionId, who, line);
            }
            foreach (var v in ViewersOf(s!)) if (MaySee(s!, v, m)) v.Send(m);
        };
        ws.OnBinary += b =>
        {
            var arr = b.ToArray();
            // A copy straight to another machine: to the other end of its bridge only, never to a viewer.
            if (arr.Length > 0 && BinKind.Of(arr[0]) == BinKind.Bridge) { HostBridgeFrame(s!, arr); return; }
            foreach (var v in ViewersOf(s!))
            {
                if (!MaySeeBinary(s!, v, arr) || v.SendBinary(arr)) continue;
                // Dropped for a viewer whose link is behind. A video frame lost is that viewer's picture broken
                // until the next key frame, and those are a minute apart now: ask the host for one (§142).
                if (BinaryRelay.LossNeedsKeyFrame(arr)) AskHostForKeyFrame(s!);
                else if (!BinaryRelay.Send(v.SendBinary, arr)) _log.LogWarning("[{Tenant}] session {Id}: a file chunk could not be passed to a viewer in {S}s; that transfer will fail rather than arrive corrupt", TenantName, sessionId, BinaryRelay.Deadline.TotalSeconds);
            }
        };
        SendParticipants(s!);
        // End-to-end encryption: whether to seal (TenantHub.E2e.cs). A host still asking hears it with the yes.
        if (!asking) RecomputeE2e(s!, force: true);
        BroadcastTechs(new SessionReadyMsg { Session = s!.Info });
        await ws.RunAsync(ct);
        _log.LogInformation("[{Tenant}] host DISCONNECTED from session {Id}", TenantName, sessionId);
        bool still; lock (_lock) { still = _sessions.ContainsKey(sessionId) && s.Host == ws; if (still) { s.Host = null; s.Info.HostConnected = false; } }
        if (still) EndSession(sessionId, "Client disconnected");
    }

    public async Task RunSessionViewerAsync(string sessionId, TechSession who, WsConnection ws, CancellationToken ct)
    {
        await UseCompanyClockAsync();
        Session? s; bool missing, wasHeld = false, newcomer = false, shutOut = false, stranger = false; int ticketId = 0;
        string? overLimit = null;
        // From ANOTHER company (3.9): Program.cs let them this far only if a session here was opened for
        // them with a code; it is checked again here, against the session they actually asked for.
        bool visiting = who.TenantId != TenantId;
        string label = who.DisplayName;
        lock (_lock)
        {
            missing = !_sessions.TryGetValue(sessionId, out s);
            // Two machines at once, at most (2026-10-15; TenantHub.SessionLimit.cs). This company's own people only.
            if (!missing && !visiting) overLimit = SessionLimitProblem(who.TechnicianId, s!.Info.DeviceId);
            if (overLimit != null) { /* refused below */ }
            else if (!missing && visiting && !IsTheVisitor(s!, who)) stranger = true;
            // Opened with the machine's unattended password: only those who typed it. Everybody
            // else is sent to the machine list, where Connect asks them for it.
            else if (!missing && s!.Admitted != null && !s.Admitted.Contains(who.TechnicianId)) shutOut = true;
            else if (!missing)
            {
                if (visiting) { label = s!.Visitor!.Label; s.VisitorSockets.Add(ws); s.VisitorGraceToken++; }
                s!.Viewers[ws] = label;
                if (!visiting) s.ViewerIds[ws] = who.TechnicianId;
                if (!s.Info.Technicians.Contains(label)) { s.Info.Technicians.Add(label); newcomer = true; }
                _sessionTickets.TryGetValue(sessionId, out ticketId);
                // Overtake any grace timer that is counting this session down. See Session.GraceToken.
                s.GraceToken++;
                wasHeld = s.HeldSince != null;
                s.HeldSince = null; s.Info.Held = false;
            }
        }
        if (overLimit != null)
        {
            _log.LogInformation("[{Tenant}] {Tech} tried to join session {Id} on {Name} while already in {Max} — refused",
                TenantName, who.DisplayName, sessionId, s!.Info.MachineName, SessionLimit.PerTechnician);
            ws.Send(new SessionEndedMsg { SessionId = sessionId, Reason = overLimit });
            await ws.CloseAsync(); return;
        }
        if (stranger)
        {
            _log.LogWarning("[{Tenant}] {Tech} of another company tried to join session {Id} on {Name}, which was not opened for them — refused",
                TenantName, who.DisplayName, sessionId, s!.Info.MachineName);
            ws.Send(new SessionEndedMsg { SessionId = sessionId, Reason = "That session is not yours to join." });
            await ws.CloseAsync(); return;
        }
        if (shutOut)
        {
            _log.LogWarning("[{Tenant}] {Tech} tried to join session {Id} on {Name}, which was opened with its unattended password — refused",
                TenantName, who.DisplayName, sessionId, s!.Info.MachineName);
            ws.Send(new SessionEndedMsg
            {
                SessionId = sessionId,
                Reason = $"{s!.Info.MachineName} has an unattended password. To join, select it in the machine list, choose Connect and type the password."
            });
            await ws.CloseAsync(); return;
        }
        if (missing)
        {
            // The session is genuinely over — held open for its grace period and nobody came
            // back, or ended deliberately. Say so as a session end rather than a bare error:
            // a reconnecting console needs to tell "not yet" apart from "never again", and an
            // ErrorMsg reads as the first when it means the second.
            _log.LogInformation("[{Tenant}] a viewer tried to rejoin session {Id}, which no longer exists", TenantName, sessionId);
            string? why; lock (_lock) why = _endedReasons.TryGetValue(sessionId, out var e) ? e.Reason : null;
            ws.Send(new SessionEndedMsg { SessionId = sessionId, Reason = why ?? "That session has already ended." });
            await ws.CloseAsync(); return;
        }
        if (wasHeld)
        {
            _log.LogInformation("[{Tenant}] {Tech} came back to session {Id} inside the grace window", TenantName, who.DisplayName, sessionId);
            NotifyDeviceSessionState(s!);
        }
        // A second technician joining a session in progress is working the same job, so they go on
        // its ticket too (2026-09-28). The one who started it is put on by AttachSessionToTicketAsync.
        // A visitor is never put ON this company's ticket — they are not one of its people — but the line is written.
        if (newcomer && ticketId != 0 && !visiting) _ = JoinSessionTicketAsync(ticketId, who.DisplayName, s!.Info.MachineName);
        if (newcomer && visiting) _ = LogToSessionTicketAsync(sessionId, label, $"{label} is on {s!.Info.MachineName}'s screen.");

        // replay whatever the host already announced, so joining late is indistinguishable from
        // joining first
        MonitorsMsg? mons; SysInfoMsg? sys; PrivacyStateMsg? priv;
        lock (_lock) { mons = s!.LastMonitors; sys = s.LastSysInfo; priv = s.LastPrivacy; }
        if (mons != null) ws.Send(mons);
        if (sys != null) ws.Send(sys);
        if (priv is { On: true }) ws.Send(priv);
        // End-to-end encryption: this viewer has a few seconds to say it can open sealed content (TenantHub.E2e.cs).
        E2eViewerJoined(s!, ws);
        ws.OnMessage += m =>
        {
            // Somebody from another company (3.9): screen and control, and nothing else — an ALLOW-list, so a
            // message added to the protocol next year is refused to them until somebody decides otherwise.
            if (visiting)
            {
                // Removed, or somebody else brought in since (§155): what this socket still sends goes nowhere.
                if (NoLongerTheVisitor(s!, who)) return;
                // Brought in by a technician (§155), they leave and the session goes on; with the customer's code, it ends.
                if (m is EndSessionMsg) { VisitorLeaves(s!, who, label); return; }
                if (!VisitorMaySend(s!, m)) { RefusedFromVisitor(s!, label, m); return; }
            }
            // The free version (2026-10-06): control, Files, clipboard and printing — an allow-list (FreePlan.MaySend).
            if (Free && m is not EndSessionMsg && !FreePlan.MaySend(m)) { RefusedOnFree(s!, m); return; }
            // Copy on one machine, paste on the other, straight between them (TenantHub.Bridge.cs) — the hub's to decide.
            if (m is BridgeAskMsg bridgeAsk) { if (!visiting) AskBridge(s!, ws, bridgeAsk); return; }
            if (m is BridgeEndMsg bridgeEnd) { if (!visiting) BridgeEndFrom(s!, bridgeEnd, who.TechnicianId); return; }
            // End-to-end encryption (2026-10-07): this viewer can open sealed content. Counted, then passed to the host.
            if (m is E2eHelloMsg) E2eHello(s!, ws);
            // The ticket's line for a script, sent beside the sealed script itself — which the hub cannot read.
            if (m is ScriptAuditMsg sa0)
            {
                int tid; lock (_lock) _sessionTickets.TryGetValue(sessionId, out tid);
                var first = (sa0.FirstLine ?? "").Trim();
                if (first.Length > 120) first = first[..120] + "…";
                if (tid != 0) _ = LogToTicketAsync(tid, who.DisplayName, $"Ran {sa0.Language} script: {first}");
                return;
            }
            if (m is ChatMsg c)
            {
                // Chat typed in a session window must reach the customer's tray app, which is on the
                // device connection — not the host process, which has no idea what to do with it.
                c.From = who.DisplayName; c.FromTech = true; c.DeviceId = s!.Info.DeviceId; c.Ts = DateTimeOffset.UtcNow;
                RouteChat(c);
                return;
            }
            // "Leave / End" on the session toolbar. It arrives on THIS socket rather than the
            // dashboard one, because the session window is the thing being closed. Marking the
            // socket is what lets the close below tell a deliberate end from a dropped packet;
            // with more than one technician in the session it means "I am leaving", and the
            // others keep it.
            if (m is EndSessionMsg)
            {
                // With somebody a technician brought in (§155), the last of THIS company leaving ends it: the outsider
                // never stays on alone, and Leave with only them left means "we are done".
                bool last; lock (_lock) { s!.Leaving.Add(ws); last = s.InvitedBy != null ? !s.Viewers.Keys.Any(k => k != ws && !s.VisitorSockets.Contains(k)) : s.Viewers.Count <= 1; }
                if (last) EndSession(sessionId, $"Ended by {who.DisplayName}");
                return;
            }
            // record maintenance actions on the ticket so there is an audit trail of what was run
            if (m is ScriptRunMsg sr)
            {
                int tid; lock (_lock) _sessionTickets.TryGetValue(sessionId, out tid);
                var first = (sr.Script ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
                if (first.Length > 120) first = first[..120] + "…";
                if (tid != 0) _ = LogToTicketAsync(tid, who.DisplayName, $"Ran {sr.Language} script: {first}");
            }
            // Recording (1.6): on the ticket, and to the person at the machine through their tray
            // app — being recorded is disclosed, like a file taken off their machine. Not for the
            // host, which has nothing to do with it: the console records what it is shown.
            if (m is RecordingStateMsg rs)
            {
                rs.By = who.DisplayName;
                int tid; lock (_lock) _sessionTickets.TryGetValue(sessionId, out tid);
                if (tid != 0) _ = LogToTicketAsync(tid, who.DisplayName, rs.On ? $"{who.DisplayName} started recording the session." : $"{who.DisplayName} stopped recording.");
                foreach (var tray in DeviceConns(s!.Info.DeviceId, DeviceRole.Client)) tray.Ws.Send(rs);
                return;
            }
            // A reboot, a lock or a Ctrl+Alt+Del deserves the same audit line a script gets, and
            // for the same reason: it is something that was done TO the customer's machine.
            if (m is SysActionMsg sa)
            {
                int tid; lock (_lock) _sessionTickets.TryGetValue(sessionId, out tid);
                var what = sa.Action switch
                {
                    "reboot" => "Restarted the machine",
                    "lock" => "Locked the machine",
                    "ctrl_alt_del" => "Sent Ctrl+Alt+Del",
                    _ => "Sent system action " + sa.Action
                };
                _log.LogInformation("[{Tenant}] {Tech} sent {Action} to {Machine}", TenantName, who.DisplayName, sa.Action, s!.Info.MachineName);
                if (tid != 0) _ = LogToTicketAsync(tid, who.DisplayName, what + ".");

                // Ctrl+Alt+Del goes to the AGENT, not the host. SendSAS is only accepted from a
                // service running as LocalSystem; the host is LocalSystem but is a process
                // launched into the interactive session, and Windows silently ignores it there.
                // Measured on 2026-09-15: the host reported success and nothing happened, for
                // both values of AsUser. The agent is a real session-0 service.
                if (sa.Action == "ctrl_alt_del")
                {
                    var agents = DeviceConns(s.Info.DeviceId, DeviceRole.Agent);
                    if (agents.Count == 0)
                    {
                        // Attended-only install. Say so rather than sending it somewhere it
                        // cannot work and letting the technician wonder.
                        ws.Send(new SysActionResultMsg
                        {
                            Action = sa.Action, Ok = false,
                            Detail = "this machine has no unattended agent, and only a Windows service can send Ctrl+Alt+Del"
                        });
                        _log.LogWarning("[{Tenant}] Ctrl+Alt+Del asked for on {Machine}, which has no agent", TenantName, s.Info.MachineName);
                    }
                    else foreach (var a in agents) a.Ws.Send(sa);
                    return;
                }
            }
            // The customer's answer about their MICROPHONE is theirs alone, and it reaches the host
            // only from their tray app (HandleDeviceMessage). A technician's socket may never carry
            // one: everything else on this socket is passed to the host, so without this line a
            // technician could open the customer's microphone themselves — attended or not. Found by
            // voicewire on its first run, 2026-09-30: the microphone opened.
            if (m is VoiceMicAnswerMsg)
            {
                _log.LogWarning("[{Tenant}] {Tech} sent a microphone answer for {Machine}; only the customer may, and it was dropped",
                    TenantName, who.DisplayName, s!.Info.MachineName);
                return;
            }
            // Talk (3.3): stamped with who it came from — a console cannot speak in somebody
            // else's name — and written on the ticket, because a voice coming out of a customer's
            // speakers is something done to their machine. Passed on to the host below.
            if (m is VoiceTalkMsg vt)
            {
                vt.Technician = who.DisplayName;
                bool changed; lock (_lock) changed = vt.On ? s!.Talking.Add(who.DisplayName) : s!.Talking.Remove(who.DisplayName);
                if (changed)
                    _ = LogToSessionTicketAsync(sessionId, who.DisplayName, vt.On
                        ? $"{who.DisplayName} started talking to the customer through {s!.Info.MachineName}'s speakers."
                        : $"{who.DisplayName} stopped talking.");
            }
            // The person at the machine has not said yes yet. Their host acts on nothing before
            // then either; this is the same rule kept in a second place, so neither end alone is
            // what stands between a console and a machine nobody agreed to.
            bool waiting; lock (_lock) waiting = s!.Info.AwaitingConsent;
            if (waiting) return;
            s!.Host?.Send(m);
        };
        // A visitor sends binary only for files they were allowed (3.9): binary from a viewer is files and voice.
        // Binary from a viewer: file chunks and voice, and sealed messages by their visible type (TenantHub.E2e.cs).
        ws.OnBinary += b => { if (!ViewerBinaryMayPass(s!, visiting, b.Span) || (visiting && NoLongerTheVisitor(s!, who))) return; var host = s!.Host; if (host != null && !s.Info.AwaitingConsent && !BinaryRelay.Send(host.SendBinary, b.ToArray())) _log.LogWarning("[{Tenant}] session {Id}: a file chunk could not be passed to the host in {S}s; that transfer will fail rather than arrive corrupt", TenantName, sessionId, BinaryRelay.Deadline.TotalSeconds); };
        SendParticipants(s!);
        BroadcastTechs(new SessionReadyMsg { Session = s!.Info });
        _ = LogSessionAsync(s, null);
        await ws.RunAsync(ct);
        bool empty, deliberate, alive;
        lock (_lock)
        {
            s.Viewers.Remove(ws); s.ViewerIds.Remove(ws); if (!s.Viewers.ContainsValue(label)) s.Info.Technicians.Remove(label);
            if (visiting) s.VisitorSockets.Remove(ws);
            empty = s.Viewers.Count == 0;
            deliberate = s.Leaving.Remove(ws);
            alive = _sessions.ContainsKey(sessionId);
        }
        // A copy straight to or from this machine needs its technician in both sessions.
        BridgesLost(s, $"{label} closed the window for {s.Info.MachineName}");
        if (!alive) return;                       // already ended, by this socket's own EndSessionMsg or otherwise
        // An old console leaving may let the session seal again.
        E2eViewerLeft(s, ws);
        // The visitor's connection went without them saying End: they may come back within the grace
        // period, and if they do not, the session they were let in for is over, watchers or not.
        if (visiting) VisitorMayReturn(s, sessionId);
        // One of ours went (§155): their code stops, and nobody of ours left means the outsider they brought goes too.
        else InviteViewerLeft(s, who.TechnicianId);
        if (empty && deliberate) EndSession(sessionId, "All technicians left");
        else if (empty) BeginGrace(s, sessionId);
        else { SendParticipants(s); BroadcastTechs(new SessionReadyMsg { Session = s.Info }); }
    }

    /// <summary>
    /// The last viewer's socket has gone and nobody said "end". Hold the session — host still
    /// running, still registered — for <see cref="GracePeriod"/> so a technician whose Wi-Fi
    /// blinked can come back to the session they were in rather than to nothing.
    ///
    /// This is the half a client-side retry loop cannot do for itself: the session used to be
    /// destroyed the instant the socket closed, so a retry would have dialled back into a
    /// registry entry that no longer existed. A deliberate Leave / End never reaches here.
    /// </summary>
    private void BeginGrace(Session s, string sessionId)
    {
        int token;
        lock (_lock) { token = ++s.GraceToken; s.HeldSince = DateTime.UtcNow; s.Info.Held = true; }
        _log.LogInformation("[{Tenant}] session {Id} on {Machine}: the last technician's socket dropped — holding it for {Secs}s",
            TenantName, sessionId, s.Info.MachineName, (int)GracePeriod.TotalSeconds);
        // The person at the machine is told their technician's connection has gone. For up to
        // ninety seconds the tray app would otherwise be claiming somebody was watching a screen
        // nobody is looking at — the same disclosure argument that put the listening banner there.
        NotifyDeviceSessionState(s);
        // And the HOST is told too, for the same reason and in the same breath.
        //
        // Every other branch of the viewer-disconnect path calls SendParticipants; this one — the
        // one where the LAST viewer went — did not, so the host was the only party never informed
        // that nobody was watching any more. It went on believing the subscription that viewer
        // made was live, because a socket that drops never sends On=false, and it kept pushing
        // the customer's clipboard into a session with nobody in it (HANDOVER §40).
        //
        // Technicians is already empty here: the name is removed as the last of that
        // technician's viewers goes, and this branch is only reached with no viewers at all.
        SendParticipants(s);
        BroadcastTechs(new SessionReadyMsg { Session = s.Info });
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(GracePeriod);
                bool end;
                lock (_lock) end = _sessions.ContainsKey(sessionId) && s.GraceToken == token && s.Viewers.Count == 0;
                if (end) EndSession(sessionId, "The technician's connection was lost and did not come back.");
            }
            catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] grace timer for session {Id}", TenantName, sessionId); }
        });
    }

    /// <summary>
    /// Tell the customer's tray app what is happening on its own machine. Called when a session
    /// starts to be held, when a technician comes back, when a session ends, and — the case that
    /// is probably BUGS #2 — whenever a device connection appears, because a tray app that
    /// reconnected used to be told nothing and stayed on "support in progress" for ever.
    /// </summary>
    private void NotifyDeviceSessionState(Session s)
    {
        SessionStateMsg msg;
        lock (_lock)
            msg = new SessionStateMsg
            {
                SessionId = s.Info.SessionId, Active = _sessions.ContainsKey(s.Info.SessionId),
                Held = s.HeldSince != null, TechnicianName = s.Info.Technicians.FirstOrDefault() ?? s.StartedBy,
                Listening = s.Listening
            };
        VoiceStateMsg? voice; lock (_lock) voice = s.LastVoice;
        foreach (var c in DeviceConns(s.Info.DeviceId, DeviceRole.Client))
        {
            c.Ws.Send(msg);
            // A call in progress is part of "what is happening on this machine": a tray app that
            // blinked must still show who is talking and still be able to answer about its mic.
            if (voice is { Call: true } && msg.Active) c.Ws.Send(voice);
        }
    }

    /// <summary>
    /// The host's account of a voice call (3.3), made true and passed on: to the person at the
    /// machine through their tray app, and — by the caller, with every other host message — to the
    /// technicians. The message is changed IN PLACE, so both see the same thing.
    ///
    /// In an unattended session "asking" becomes "unattended": the host cannot know which kind of
    /// session it is in, the hub can, and Clint's rule is that the microphone is never asked for
    /// there. The tray app is still told a technician is talking — their voice is coming out of
    /// the speakers either way — it is simply not offered a microphone to allow.
    ///
    /// The ticket gets a line when the customer's microphone actually changes, and only then.
    /// </summary>
    private void PassOnVoiceState(Session s, string sessionId, VoiceStateMsg vs)
    {
        if (s.Info.Unattended && vs.Mic == VoiceMic.Asking)
        {
            vs.Mic = VoiceMic.Unattended;
            vs.Detail = "This is an unattended session, so the customer's microphone is never asked for.";
        }
        string? line = null;
        lock (_lock)
        {
            var before = s.LastVoice?.Mic ?? VoiceMic.Off;
            s.LastVoice = vs;
            if (vs.Call && before != vs.Mic)
                line = vs.Mic switch
                {
                    VoiceMic.On => "The customer allowed their microphone; the technician can hear them.",
                    VoiceMic.Declined when before == VoiceMic.On => "The customer turned their microphone off.",
                    VoiceMic.Declined => "The customer chose not to use their microphone.",
                    VoiceMic.Unavailable => $"The customer allowed their microphone, but it could not be used: {vs.Detail}.",
                    _ => null
                };
        }
        if (line != null) _ = LogToSessionTicketAsync(sessionId, s.Info.Technicians.LastOrDefault() ?? s.StartedBy, line);
        foreach (var c in DeviceConns(s.Info.DeviceId, DeviceRole.Client)) c.Ws.Send(vs);
    }

    /// <summary>
    /// A line on the session's ticket, waiting up to five seconds for the ticket to exist. A
    /// session's ticket is attached a moment AFTER it starts (AttachSessionToTicketAsync), and a
    /// line written in that moment used to find no ticket and be dropped without a word — voicewire
    /// found "started talking" missing from every call begun straight away. What was done to a
    /// customer's machine, their microphone above all, is not something the record may lose.
    /// </summary>
    private async Task LogToSessionTicketAsync(string sessionId, string who, string line)
    {
        for (int i = 0; i < 20; i++)
        {
            int tid; lock (_lock) { if (!_sessionTickets.TryGetValue(sessionId, out tid)) tid = 0; }
            if (tid != 0) { await LogToTicketAsync(tid, who, line); return; }
            await Task.Delay(250);
        }
        _log.LogWarning("[{Tenant}] session {Id} has no ticket to write on: {Line}", TenantName, sessionId, line);
    }

    private List<WsConnection> ViewersOf(Session s) { lock (_lock) return s.Viewers.Keys.ToList(); }

    /// <summary>
    /// Asks the host for a complete picture, at most once every <see cref="BinaryRelay.KeyFrameRequestEveryMs"/>
    /// per session (HANDOVER §142). Called from the host's receive loop, which is one thread per session, but
    /// the compare-exchange keeps it to one request whatever calls it.
    /// </summary>
    private void AskHostForKeyFrame(Session s)
    {
        long now = Environment.TickCount64, last = Interlocked.Read(ref s.LastKeyFrameRequestMs);
        if (now - last < BinaryRelay.KeyFrameRequestEveryMs) return;
        if (Interlocked.CompareExchange(ref s.LastKeyFrameRequestMs, now, last) != last) return;
        s.Host?.Send(new KeyFrameRequestMsg());
    }

    private void SendParticipants(Session s)
    {
        ParticipantsMsg p; List<WsConnection> viewers; WsConnection? host;
        lock (_lock) { p = new ParticipantsMsg { Technicians = s.Info.Technicians.ToList(), HostConnected = s.Host != null }; viewers = s.Viewers.Keys.ToList(); host = s.Host; }
        foreach (var v in viewers) v.Send(p);
        host?.Send(p);
    }

    /// <summary>
    /// Links a starting session to a ticket: the device's open one, or a fresh ticket for an
    /// unattended/ad-hoc connection that nobody raised a call for.
    /// </summary>
    private async Task AttachSessionToTicketAsync(string sessionId, string deviceId, string machineName, string tech,
        bool fromCall, string reason = "")
    {
        // The free version has no ticketing (2026-10-06): a session is just a session.
        if (Free) return;
        try
        {
            // EVERY SESSION GETS ITS OWN TICKET (3.6.6). A hundred sessions to one machine in a
            // day is a hundred tickets, because a ticket is now the record of a piece of work
            // somebody did rather than a note that a machine has a problem.
            //
            // The only exception is ActiveForDeviceAsync's 30-minute window, which absorbs a
            // dropped connection and a technician reconnecting straight away. Work that genuinely
            // carries on past it is LINKED — see TicketService.SetParentAsync — not merged.
            var t = await _tickets.ActiveForDeviceAsync(TenantId, deviceId);
            if (t == null)
            {
                // The technician's own words, since 3.6.6, exactly as a customer's have been the
                // description since 3.5.7. A subject built from the reason reads as a job rather
                // than as "Remote session on DESKTOP-2N6FVGT" a hundred times over.
                var subject = string.IsNullOrWhiteSpace(reason)
                    ? $"Remote session on {machineName}"
                    : reason.Trim().Length <= 70 ? reason.Trim() : reason.Trim()[..67] + "…";
                t = await _tickets.CreateAsync(TenantId, subject, reason ?? "", tech,
                    fromCall ? TicketSource.Call : TicketSource.Unattended, deviceId, machineName, "", TicketPriority.Normal, tech,
                    TicketCategory.RemoteSupport, await ClientForDeviceAsync(deviceId));
            }
            else if (!Assignees.Has(t.AssignedTo, tech))
            {
                // Whoever connects to the machine is working the job, so they go ON it beside anybody
                // already there (2026-09-28) — the record says who actually did the work. The wide-open
                // overload: this is the server recording a fact, not a person asking permission.
                await _tickets.UpdateAsync(TenantId, t.Id, tech, TicketStatus.InProgress, null, Assignees.With(t.AssignedTo, tech), null);
            }
            else
            {
                await _tickets.UpdateAsync(TenantId, t.Id, tech, TicketStatus.InProgress, null, null, null);
            }
            lock (_lock) _sessionTickets[sessionId] = t.Id;
            await LinkSessionRowAsync(sessionId, t.Id);
            await _tickets.AddNoteAsync(TenantId, t.Id, tech, $"{tech} connected to {machineName}.", true, automatic: true);
            var fresh = await _tickets.GetAsync(TenantId, t.Id);
            if (fresh != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(fresh) });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] attach session to ticket", TenantName); }
    }

    private async Task OpenTicketForCallAsync(CallInfo call, byte[]? screenshot = null)
    {
        try
        {
            // A repeat request opens a SECOND ticket, unless it is inside the 30-minute window —
            // which is what a customer double-clicking Request Support, or calling straight back
            // after a dropped connection, looks like. Outside it, somebody asking for help again
            // is asking about something else, and it gets its own number.
            var existing = await _tickets.ActiveForDeviceAsync(TenantId, call.DeviceId);
            if (existing != null)
            {
                // What they said this time, too: it may be more than the first time said.
                var again = "Customer requested support again" + (call.Issue.Length > 0 ? $": {call.Issue}" : ".")
                            + (call.Details.Length > 0 ? $"\n{call.Details}" : "");
                await _tickets.AddNoteAsync(TenantId, existing.Id, call.ContactName, again, true, automatic: true);
                await AttachScreenshotAsync(existing.Id, call, screenshot);
                var refreshed = await _tickets.GetAsync(TenantId, existing.Id);
                if (refreshed != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(refreshed) });
                return;
            }
            // The subject is the subject (2026-10-09) and the description what they wrote under it — or, with nothing
            // under it, the subject again, which is what "Fault reported" has always shown.
            var subject = string.IsNullOrWhiteSpace(call.Issue) ? $"Support request from {call.MachineName}" : call.Issue;
            var t = await _tickets.CreateAsync(TenantId, subject, call.Details.Length > 0 ? call.Details : call.Issue ?? "", call.ContactName, TicketSource.Call,
                call.DeviceId, call.MachineName, call.ContactName, TicketPriority.Normal, "",
                TicketCategory.RemoteSupport, await ClientForDeviceAsync(call.DeviceId));
            await _tickets.AddNoteAsync(TenantId, t.Id, call.ContactName, "Customer requested support.", true, automatic: true);
            await AttachScreenshotAsync(t.Id, call, screenshot);
            var fresh = await _tickets.GetAsync(TenantId, t.Id);
            if (fresh != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(fresh) });
            _log.LogInformation("[{Tenant}] ticket #{No} opened from call on {Machine}", TenantName, t.Number, call.MachineName);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] open ticket for call", TenantName); }
    }

    /// <summary>The picture of their screen that came with a request (2026-10-09), kept on its ticket as a photo of its own
    /// kind. A picture refused (not an image, too large) is said in the ticket's story rather than lost in silence.</summary>
    private async Task AttachScreenshotAsync(int ticketId, CallInfo call, byte[]? screenshot)
    {
        if (screenshot == null) return;
        var (photo, error) = await _photos.AddAsync(TenantId, screenshot, "screenshot.jpg", PhotoKind.Screenshot, call.ContactName, ticketId, "");
        if (photo == null)
            await _tickets.AddNoteAsync(TenantId, ticketId, call.ContactName, $"A picture of their screen came with the request but could not be kept: {error}", true, automatic: true);
    }

    // ------------------------------------------------------------------ ticketing
    private async Task SendTicketListAsync(TechConn tc, string filter, string search, string? sort = null)
    {
        var list = await _tickets.ListAsync(TenantId, filter ?? "open", search ?? "", tc.Who.DisplayName, tc.Who.Role, sort: sort ?? "");
        // resolve client names in one query so the queue can show who each job belongs to
        var clientIds = list.Where(t => t.ClientId.HasValue).Select(t => t.ClientId!.Value).Distinct().ToList();
        var names = new Dictionary<int, string>();
        await using var db = await _dbf.CreateDbContextAsync();
        if (clientIds.Count > 0)
            names = await db.Clients.Where(c => c.TenantId == TenantId && clientIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name);
        var replied = await TicketService.CustomerRepliedAsync(db, TenantId, list.Select(t => t.Id).ToList());
        tc.Ws.Send(new TicketListMsg
        {
            Filter = filter ?? "open", Sort = sort ?? "",
            Tickets = list.Select(t =>
            {
                var s = TicketService.ToSummary(t, t.ClientId.HasValue && names.TryGetValue(t.ClientId.Value, out var n) ? n : "");
                s.CustomerReplied = replied.Contains(t.Id);
                return s;
            }).ToList()
        });
    }

    /// <summary>
    /// Whether this console user is allowed to see a given ticket at all — their own, or one
    /// nobody has been given. Out of scope answers "not found", the same as a ticket that does not
    /// exist: whether a colleague's ticket exists is not their business either.
    /// </summary>
    private async Task<bool> MayTouchTicketAsync(TechConn tc, int ticketId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == TenantId), tc.Who.Role, tc.Who.DisplayName)
            .AnyAsync(t => t.Id == ticketId);
    }

    private async Task SendTicketDetailAsync(TechConn tc, int ticketId)
    {
        if (!await MayTouchTicketAsync(tc, ticketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var t = await _tickets.GetAsync(TenantId, ticketId);
        if (t == null) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var client = t.ClientId.HasValue ? await _clients.GetAsync(TenantId, t.ClientId.Value) : null;
        var pw = t.DevicePasswordProtected ? _clients.Unprotect(t.DevicePasswordEnc) : "";
        var detail = TicketService.ToDetail(t, await _tickets.NotesAsync(TenantId, ticketId), client?.Name ?? "", pw);
        // The workflow, as ANSWERS: stage, buttons, fields. The console draws them and decides nothing.
        await _tickets.FillWorkflowAsync(detail, t, tc.Who.Role, tc.Who.DisplayName, _clients.Unprotect);
        // The knowledge base (3.8.5): the client's count, what was written up from it, and whether
        // this person is offered "Add to knowledge base" — answers, like the workflow's.
        if (_registry.Knowledge is { } kb) detail.Knowledge = await kb.InfoForTicketAsync(TenantId, t, tc.Who.Role, tc.Who.DisplayName);
        // A recurring task's ticket says which schedule it came from, and whether this person may stop it.
        if (_registry.Recurring is { } rec) detail.Recurring = await rec.InfoAsync(t, tc.Who.Role, tc.Who.DisplayName);
        // Emailing a comment to the client (2026-10-09): whether it is set up and to whom, and what each comment
        // has been sent as — the same answers and words the portal draws.
        if (_registry.ClientUpdates is { } cu)
        {
            var offer = await cu.OfferAsync(TenantId, t, tc.Who.Role);
            detail.ClientUpdate = new ClientUpdateInfo
            {
                Ready = offer.Ready, WhyNot = offer.WhyNot, Default = offer.Default, From = offer.From,
                Choices = offer.Choices.Select(r => new ClientAddressInfo { Address = r.Address, Label = r.Label }).ToList()
            };
            var sent = await cu.ForTicketAsync(TenantId, ticketId);
            foreach (var n in detail.Notes)
                if (sent.TryGetValue(n.Id, out var rows))
                {
                    n.Emailed = rows.Select(ClientUpdateService.Marker).ToList();
                    n.EmailFailed = rows.Any(r => !r.Sent);
                }
        }
        tc.Ws.Send(new TicketDetailMsg { Detail = detail });
        // Opening it is seeing the customer's reply: the flag clears now, after this view has said so.
        if (detail.RepliedBy.Count > 0) await _tickets.SeenCustomerRepliesAsync(TenantId, ticketId, tc.Who.DisplayName);
    }

    /// <summary>
    /// How many articles the knowledge base holds for the client a machine belongs to (3.8.5), so a
    /// session window can say so as it connects — Clint's example is exactly this: the next technician
    /// on that client's machine finds out how it was done last time without having to think to look.
    /// </summary>
    private async Task SendKnowledgeCountAsync(TechConn tc, string deviceId)
    {
        var answer = new KnowledgeCountMsg { DeviceId = deviceId };
        if (_registry.Knowledge is { } kb && await ClientForDeviceAsync(deviceId) is int clientId)
        {
            answer.ClientId = clientId;
            answer.ClientName = await kb.ClientNameAsync(TenantId, clientId);
            await using (var db = await _dbf.CreateDbContextAsync()) answer.ClientNumber = await PortalAddresses.ClientNumberAsync(db, TenantId, clientId);
            answer.Count = await kb.CountForClientAsync(TenantId, clientId);
        }
        tc.Ws.Send(answer);
    }

    /// <summary>The ticketing client that owns a machine, so tickets raised from it are filed correctly.</summary>
    /// <summary>
    /// Hands the console a one-use link that signs this technician in to the portal as themselves.
    ///
    /// The socket it arrives on is already authenticated, which is the entire mechanism: the
    /// server knows who is asking without anybody typing a password a second time. The token is
    /// spent the moment the browser opens it and expires in a minute regardless — see
    /// PortalHandoffService.
    /// </summary>
    private void SendPortalLink(TechConn tc, string? path)
    {
        try
        {
            var token = _handoff.Issue(tc.Who, path);
            tc.Ws.Send(new PortalLinkMsg { Url = $"/portal/enter?t={Uri.EscapeDataString(token)}" });
            _log.LogInformation("[{Tenant}] portal hand-off issued for {Who}", TenantName, tc.Who.DisplayName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[{Tenant}] portal hand-off", TenantName);
            tc.Ws.Send(new PortalLinkMsg { Error = "Could not open the portal just now." });
        }
    }

    private async Task<int?> ClientForDeviceAsync(string deviceId)
    {
        if (WebClientId(deviceId) is { } webClient) return webClient;
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            return await db.Devices.Where(d => d.TenantId == TenantId && d.DeviceId == deviceId)
                .Select(d => d.ClientId).FirstOrDefaultAsync();
        }
        catch { return null; }
    }

    private async Task SendClientsAsync(TechConn tc, string term, int take = 12)
    {
        var list = await _clients.SearchAsync(TenantId, term ?? "", Math.Clamp(take, 1, 1000));
        // The people at each company travel with it, so the owner picker can offer "Bob at Zenith"
        // without a second round trip in the middle of the dialog. One read for all of them rather
        // than one per client.
        var contacts = await _contacts.AllAsync(TenantId);
        tc.Ws.Send(new ClientListMsg
        {
            Term = term ?? "",
            Clients = list.Select(c => new ClientInfo
            {
                Id = c.Id, Name = c.Name, ContactName = c.ContactName, Phone = c.Phone, Email = c.Email, Address = c.Address,
                Contacts = contacts.Where(p => p.ClientId == c.Id)
                    .Select(p => new ContactInfo { Id = p.Id, ClientId = p.ClientId, Name = p.Name, Phone = p.Phone, Email = p.Email })
                    .ToList()
            }).ToList()
        });
    }

    /// <summary>
    /// Says who a machine belongs to, asked for by the console.
    ///
    /// Every role may do this — <see cref="Permissions.CanLinkDevices"/> — which is the whole
    /// point of the change: an unlinked machine stops its tickets reaching a client page, and the
    /// person who notices is whoever is looking at the list.
    ///
    /// The write goes through <see cref="ContactService.LinkDeviceAsync"/> and nowhere else. It is
    /// the only thing that keeps a machine's contact and its client agreeing, and a second write
    /// path here is exactly how they would come to disagree.
    /// </summary>
    private async Task LinkDeviceAsync(TechConn tc, DeviceLinkMsg m)
    {
        if (!Permissions.CanLinkDevices(tc.Who.Role))
        { tc.Ws.Send(new ErrorMsg { Message = "You cannot change which customer a machine belongs to." }); return; }

        // A new contact by the name they gave (2026-10-18): added at the chosen client, then linked — ContactService
        // again, so the two halves still cannot disagree.
        var (owner, error) = m.ContactId <= 0 && !string.IsNullOrWhiteSpace(m.NewContactName)
            ? await _contacts.LinkDeviceToNewContactAsync(TenantId, m.DeviceId, m.ClientId, m.NewContactName)
            : await _contacts.LinkDeviceAsync(TenantId, m.DeviceId,
                m.ClientId > 0 ? m.ClientId : null, m.ContactId > 0 ? m.ContactId : null);
        if (error.Length > 0) { tc.Ws.Send(new ErrorMsg { Message = error }); return; }

        // Same call the portal makes, so a machine linked from the console moves under its new
        // customer on every open console — including the one that did it, which is how the
        // technician sees that it worked.
        RefreshDeviceOwner(m.DeviceId, owner.ClientId, owner.ClientName, owner.ContactId, owner.ContactName);
    }

    private async Task CreateTicketAsync(TechConn tc, TicketCreateMsg m)
    {
        var wanted = TicketService.CategoryOf(m.Category);
        // A category the company has switched off (Job types): an older console still offers every one.
        var off = await _tickets.CategoryRefusalAsync(TenantId, wanted);
        if (off.Length > 0) { tc.Ws.Send(new ErrorMsg { Message = off }); return; }
        // A recurring task is a SCHEDULE, set up once; the tickets are opened by it, each on its morning.
        if (wanted == TicketCategory.Recurring) { await SetUpRecurringAsync(tc, m); return; }

        // Three photos of the machine as it arrived, or it is not booked in — the same gate the
        // browser's booking form meets, checked before anything is written so a refused booking
        // does not leave a new customer record behind for a job that never opened.
        var photoProblem = await _photos.CheckBookInAsync(TenantId, wanted, m.PhotoStageId ?? "", tc.Who.DisplayName);
        if (photoProblem.Length > 0) { tc.Ws.Send(new ErrorMsg { Message = photoProblem }); return; }
        var bookInAssignee = !string.IsNullOrWhiteSpace(m.AssignTo) ? m.AssignTo : m.AssignToMe ? tc.Who.DisplayName : "";
        var bookIn = TicketService.CheckBookIn(wanted, bookInAssignee);
        if (bookIn.Length > 0) { tc.Ws.Send(new ErrorMsg { Message = bookIn }); return; }
        if (TicketDue.Problem(TicketDue.Day(m.DueDate)) is { Length: > 0 } late) { tc.Ws.Send(new ErrorMsg { Message = late }); return; }

        // link to an existing client, or create one from what was typed (reusing an exact name match)
        int? clientId = m.ClientId > 0 ? m.ClientId : null;
        Client? client = clientId.HasValue ? await _clients.GetAsync(TenantId, clientId.Value) : null;
        if (client == null && !string.IsNullOrWhiteSpace(m.NewClientName))
        {
            var (created, _) = await _clients.CreateAsync(TenantId, m.NewClientName, m.ContactName ?? "",
                m.NewClientPhone, m.NewClientEmail, m.NewClientAddress, "");
            client = created; clientId = created?.Id;
        }

        var category = TicketService.CategoryOf(m.Category);
        var assignee = !string.IsNullOrWhiteSpace(m.AssignTo) ? Assignees.Normalise(m.AssignTo)
                     : m.AssignToMe ? tc.Who.DisplayName : "";

        var t = await _tickets.CreateAsync(TenantId, m.Subject, m.Description, tc.Who.DisplayName, TicketSource.Manual,
            m.DeviceId ?? "", m.MachineName ?? "", m.ContactName ?? "", (TicketPriority)Math.Clamp(m.Priority, 0, 3),
            assignee, category, clientId, t =>
            {
                t.SiteAddress = m.SiteAddress ?? "";
                t.AppointmentUtc = m.AppointmentUtc;
                t.DueDate = TicketDue.Day(m.DueDate);
                t.AssetMake = m.AssetMake ?? ""; t.AssetModel = m.AssetModel ?? "";
                t.AssetSerial = m.AssetSerial ?? ""; t.Accessories = m.Accessories ?? "";
                t.DevicePasswordProtected = m.DevicePasswordProtected;
                t.DevicePasswordEnc = m.DevicePasswordProtected ? _clients.Protect(m.DevicePassword ?? "") : "";
            });

        // The photographs follow the ticket that was just created.
        var attached = await _photos.AttachStagedAsync(TenantId, t.Id, m.PhotoStageId ?? "", tc.Who.DisplayName);
        if (attached > 0)
            await _tickets.AddNoteAsync(TenantId, t.Id, tc.Who.DisplayName,
                $"{attached} photo{(attached == 1 ? "" : "s")} of the machine as it arrived.", true, automatic: true);

        await _tickets.AddNoteAsync(TenantId, t.Id, tc.Who.DisplayName,
            $"{TicketService.CategoryName(category)} ticket opened{(client != null ? $" for {client.Name}" : "")}" +
            (string.IsNullOrEmpty(assignee) ? "" : $", assigned to {assignee}") + ".", true, automatic: true);

        BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(t, client?.Name ?? "") });
        await SendTicketDetailAsync(tc, t.Id);
    }

    // ------------------------------------------------------------------ the workflow, from the console
    //
    // Every one of these goes through the same service calls as the portal, scoped by the same
    // MayTouchTicketAsync. The console is not a second door (§34): it presses the same buttons, and
    // the service re-checks every rule whatever the console thought it was allowed to show.

    private QuoteService? _quotes;
    private QuoteService Quotes => _quotes ??= new QuoteService(_dbf);

    /// <summary>After a change: the open console gets the fresh ticket, every console its list row.</summary>
    private async Task AfterWorkflowChangeAsync(TechConn tc, int ticketId, string error, string done)
    {
        tc.Ws.Send(new TicketNoticeMsg { TicketId = ticketId, Error = error, Message = error.Length == 0 ? done : "" });
        var t = await _tickets.GetAsync(TenantId, ticketId);
        if (t != null && error.Length == 0) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(t) });
        await SendTicketDetailAsync(tc, ticketId);
    }

    private async Task ActOnTicketAsync(TechConn tc, TicketActMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var input = new StageInput
        {
            // Read by the same parser the portal uses, so "1:30" and "R 1 250,00" mean the same here.
            Minutes = TicketService.ParseMinutes(m.Minutes), Amount = TicketService.ParseMoney(m.Amount),
            Reference = m.Reference, Reason = m.Reason, Repairable = m.Repairable, Details = m.Details,
            AssetMake = m.AssetMake, AssetModel = m.AssetModel, AssetSerial = m.AssetSerial, Accessories = m.Accessories,
            DevicePasswordEnc = string.IsNullOrEmpty(m.DevicePassword) ? "" : _clients.Protect(m.DevicePassword),
            ParentTicketId = m.ParentTicketId > 0 ? m.ParentTicketId : null,
            PhotoStageId = m.PhotoStageId, SameJob = m.SameJob,
            AppointmentUtc = m.AppointmentUtc is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
            Comments = m.Comments, AttachmentIds = m.AttachmentIds ?? new(), EmailTo = m.EmailTo
        };
        // The damage report, emailed from the step (2026-10-13): an address that cannot be used, or a report too large
        // to email, refuses the step BEFORE it moves anything — the console keeps what was typed.
        bool emailing = m.Action == Workflow.DamageReport && (m.EmailTo ?? "").Trim().Length > 0;
        if (emailing && _registry.ClientUpdates is { } check
            && await check.CheckWithFilesAsync(TenantId, m.TicketId, m.EmailTo, tc.Who.Role, m.AttachmentIds, tc.Who.DisplayName) is { Length: > 0 } refused)
        {
            await AfterWorkflowChangeAsync(tc, m.TicketId, refused + " Nothing has been recorded.", "");
            return;
        }
        var why = await _tickets.ActAsync(TenantId, m.TicketId, tc.Who.DisplayName, tc.Who.Role, m.Action, input);
        string done = m.Action == TicketService.GoBackKey ? "Gone back." : "Done.";
        if (why.Length == 0 && emailing && _registry.ClientUpdates is { } cu)
        {
            // Recorded whatever the email does; how it went is what the console is told.
            var (ok, words, _) = await cu.SendStepAsync(TenantId, m.TicketId, Workflow.DamageReportLabel, m.EmailTo, tc.Who.DisplayName, tc.Who.Role);
            if (ok) done = "Recorded. " + words;
            else
            {
                // The step DID happen ("The damage report is saved, but…"): the error first, then everybody gets the
                // moved ticket as a success would send it.
                tc.Ws.Send(new TicketNoticeMsg { TicketId = m.TicketId, Error = words });
                if (await _tickets.GetAsync(TenantId, m.TicketId) is { } moved) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(moved) });
                await SendTicketDetailAsync(tc, m.TicketId);
                return;
            }
        }
        await AfterWorkflowChangeAsync(tc, m.TicketId, why, done);
    }

    private async Task AddWorkDoneAsync(TechConn tc, TicketWorkDoneMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var why = await _tickets.AddWorkDoneAsync(TenantId, m.TicketId, tc.Who.DisplayName, tc.Who.Role, m.Body);
        await AfterWorkflowChangeAsync(tc, m.TicketId, why, "Work done recorded.");
    }

    private async Task SuggestItemsAsync(TechConn tc, string term)
    {
        var items = await Quotes.BrowseAsync(TenantId, term);
        tc.Ws.Send(new ItemListMsg
        {
            Term = term ?? "",
            Items = items.Select(i => new CatalogItemInfo { Id = i.Id, Name = i.Name, LastPartNumber = i.LastPartNumber }).ToList()
        });
    }

    private async Task AddQuoteLineAsync(TechConn tc, QuoteLineAddMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        int itemId = m.ItemId;
        if (itemId == 0)
        {
            var (item, err, similar) = await Quotes.AddItemAsync(TenantId, m.ItemName, tc.Who.DisplayName, m.ConfirmNew);
            if (item == null)
            {
                if (similar.Count > 0)
                {
                    // "Did you mean" — the console asks, and sends again with a chosen item or ConfirmNew.
                    tc.Ws.Send(new QuoteSimilarMsg
                    {
                        TicketId = m.TicketId, Typed = m.ItemName, PartNumber = m.PartNumber, SerialNumber = m.SerialNumber, Quantity = m.Quantity,
                        PhotoId = m.PhotoId, PhotoIds = m.PhotoIds,
                        Similar = similar.Select(i => new CatalogItemInfo { Id = i.Id, Name = i.Name, LastPartNumber = i.LastPartNumber }).ToList()
                    });
                    return;
                }
                tc.Ws.Send(new TicketNoticeMsg { TicketId = m.TicketId, Error = err });
                return;
            }
            itemId = item.Id;
        }
        // An older console names one photo; a newer one names them all, the first twice.
        var photos = new List<long>(m.PhotoIds ?? new());
        if (m.PhotoId is long one && !photos.Contains(one)) photos.Insert(0, one);
        var (line, lerr) = await Quotes.AddLineAsync(TenantId, m.TicketId, itemId, m.PartNumber, m.Quantity, photos, tc.Who.DisplayName, tc.Who.Role, m.SerialNumber);
        await AfterWorkflowChangeAsync(tc, m.TicketId, line == null ? lerr : "", line == null ? "" : $"Added {line.ItemName}.");
    }

    private async Task AddQuoteLinePhotosAsync(TechConn tc, QuoteLinePhotosMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        // The line must be on the ticket the console names, as for Remove.
        await using (var db = await _dbf.CreateDbContextAsync())
            if (!await db.QuoteLines.AnyAsync(l => l.Id == m.LineId && l.TicketId == m.TicketId && l.TenantId == TenantId))
            { tc.Ws.Send(new TicketNoticeMsg { TicketId = m.TicketId, Error = "That item is not on this quote." }); return; }
        var why = await Quotes.AddLinePhotosAsync(TenantId, m.LineId, m.PhotoIds, tc.Who.DisplayName, tc.Who.Role);
        var n = m.PhotoIds?.Count ?? 0;
        await AfterWorkflowChangeAsync(tc, m.TicketId, why, n == 1 ? "Photo added." : $"{n} photos added.");
    }

    private async Task SetQuoteMachineAsync(TechConn tc, QuoteMachineMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var why = await Quotes.SetMachineAsync(TenantId, m.TicketId, m.Type, m.Make, m.Model, m.Serial, tc.Who.DisplayName, tc.Who.Role);
        await AfterWorkflowChangeAsync(tc, m.TicketId, why, "Machine saved.");
    }

    private async Task RemoveQuoteLineAsync(TechConn tc, QuoteLineRemoveMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        // The line must belong to the ticket the console says it does — otherwise a line number
        // from a ticket they may NOT see could be removed by naming one they may.
        await using (var db = await _dbf.CreateDbContextAsync())
            if (!await db.QuoteLines.AnyAsync(l => l.Id == m.LineId && l.TicketId == m.TicketId && l.TenantId == TenantId))
            { tc.Ws.Send(new TicketNoticeMsg { TicketId = m.TicketId, Error = "That line is not on this quote." }); return; }
        var why = await Quotes.RemoveLineAsync(TenantId, m.LineId, tc.Who.DisplayName, tc.Who.Role);
        await AfterWorkflowChangeAsync(tc, m.TicketId, why, "Removed.");
    }

    private async Task UpdateTicketAsync(TechConn tc, TicketUpdateMsg m)
    {
        // The console goes through the same rules as the browser. A permission enforced only in a
        // Razor page is a permission with a second door standing open beside it.
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var (t, _) = await _tickets.UpdateAsync(TenantId, m.TicketId, tc.Who.DisplayName, tc.Who.Role,
            m.Status.HasValue ? (TicketStatus)m.Status.Value : null,
            m.Priority.HasValue ? (TicketPriority)m.Priority.Value : null,
            m.AssignedTo, m.Subject);
        if (t == null) return;
        BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(t) });
        await SendTicketDetailAsync(tc, t.Id);
    }

    /// <summary>"Edit the ticket" in the console (2026-10-13): the same EditAsync the portal uses — anybody who may see the
    /// ticket may change its subject, description, category and appointment, and every change goes in its story.</summary>
    private async Task EditTicketAsync(TechConn tc, TicketEditMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var (why, changes) = await _tickets.EditAsync(TenantId, m.TicketId, tc.Who.DisplayName, tc.Who.Role, new TicketEdit(
            m.Subject, m.Description, m.Category, m.AppointmentGiven, m.AppointmentUtc, m.DueGiven, m.DueDate));
        await AfterWorkflowChangeAsync(tc, m.TicketId, why, changes.Count > 0 ? "Saved." : "Nothing changed.");
    }

    /// <summary>"Link to a client…" on a ticket — anybody who may see the ticket may say whose it is.</summary>
    private async Task SetTicketClientAsync(TechConn tc, TicketSetClientMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var why = await _tickets.SetClientAsync(TenantId, m.TicketId, m.ClientId > 0 ? m.ClientId : null, m.ContactName, tc.Who.DisplayName);
        await AfterWorkflowChangeAsync(tc, m.TicketId, why, "Client linked.");
        if (why.Length == 0)
        {
            // The queue shows the client's name, so every console's list needs the new one.
            var t = await _tickets.GetAsync(TenantId, m.TicketId);
            if (t != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(t) });
        }
    }

    /// <summary>"Not part of it" in the console (2026-10-09): the same SetParentAsync the portal uses, so the one-level rule
    /// and the note on the ticket are the same whichever was used. Filing under a job is the route's Link to parent.</summary>
    private async Task SetTicketParentAsync(TechConn tc, TicketSetParentMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var before = await _tickets.GetAsync(TenantId, m.TicketId);
        var why = await _tickets.SetParentAsync(TenantId, m.TicketId, m.ParentTicketId > 0 ? m.ParentTicketId : null, tc.Who.DisplayName);
        await AfterWorkflowChangeAsync(tc, m.TicketId, why, m.ParentTicketId > 0 ? "Filed under that job." : "No longer part of another job.");
        if (why.Length > 0) return;
        // The other end of the link too — the job it left or joined; this ticket AfterWorkflowChangeAsync has sent.
        foreach (var id in new[] { before?.ParentTicketId ?? 0, m.ParentTicketId }.Where(i => i > 0 && i != m.TicketId).Distinct())
            if (await _tickets.GetAsync(TenantId, id) is { } t) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(t) });
    }

    private async Task AddTicketNoteAsync(TechConn tc, TicketNoteAddMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        // A box ticked with an address that cannot be used — or files too large to email together: nothing posted, and
        // the console keeps the words.
        if (m.EmailTo.Trim().Length > 0 && _registry.ClientUpdates is { } cu
            && await cu.CheckWithFilesAsync(TenantId, m.TicketId, m.EmailTo, tc.Who.Role, m.AttachmentIds, tc.Who.DisplayName) is { Length: > 0 } refused)
        {
            tc.Ws.Send(new TicketNoticeMsg { TicketId = m.TicketId, Error = refused + " Your comment has not been posted." });
            return;
        }
        // With its files and photos (2026-10-13), which may be all it is.
        var note = await _tickets.AddNoteAsync(TenantId, m.TicketId, tc.Who.DisplayName, m.Body, false, attachments: m.AttachmentIds);
        // "Also email this to the client" (2026-10-09): the comment is saved first, whatever the email does.
        if (note != null && m.EmailTo.Trim().Length > 0) await EmailNoteAsync(tc, m.TicketId, note.Id, m.EmailTo);
        var t = await _tickets.GetAsync(TenantId, m.TicketId);
        if (t != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(t) });
        await SendTicketDetailAsync(tc, m.TicketId);
    }

    /// <summary>"Email to the client…" on a comment already posted (2026-10-09).</summary>
    private async Task EmailTicketNoteAsync(TechConn tc, TicketEmailNoteMsg m)
    {
        if (!await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        await EmailNoteAsync(tc, m.TicketId, m.NoteId, m.To);
        await SendTicketDetailAsync(tc, m.TicketId);
    }

    /// <summary>One comment to the client, and the console told how it went. Scoping is the caller's.</summary>
    private async Task EmailNoteAsync(TechConn tc, int ticketId, long noteId, string to)
    {
        if (_registry.ClientUpdates is not { } cu)
        { tc.Ws.Send(new TicketNoticeMsg { TicketId = ticketId, Error = "Emailing the client is not available on this server." }); return; }
        var (ok, words) = await cu.SendAsync(TenantId, ticketId, noteId, to, tc.Who.DisplayName, tc.Who.Role);
        tc.Ws.Send(new TicketNoticeMsg { TicketId = ticketId, Error = ok ? "" : words, Message = ok ? words : "" });
    }

    /// <summary>Log an automatic entry against whatever ticket the device currently has open.</summary>
    /// <summary>Stamp the session-history row with its ticket so billing can total time per ticket.</summary>
    private async Task LinkSessionRowAsync(string sessionId, int ticketId)
    {
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var row = await db.Sessions.FirstOrDefaultAsync(s => s.TenantId == TenantId && s.SessionId == sessionId);
            if (row == null)
            {
                // the session row is written moments later; retry once
                await Task.Delay(700);
                row = await db.Sessions.FirstOrDefaultAsync(s => s.TenantId == TenantId && s.SessionId == sessionId);
            }
            if (row != null) { row.TicketId = ticketId; await db.SaveChangesAsync(); }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] link session to ticket", TenantName); }
    }

    private async Task LogToDeviceTicketAsync(string deviceId, string author, string body, bool isSystem = true)
    {
        try
        {
            var t = await _tickets.ActiveForDeviceAsync(TenantId, deviceId);
            if (t != null) await LogToTicketAsync(t.Id, author, body, isSystem);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] ticket log", TenantName); }
    }

    /// <summary>Puts a technician who joined a session in progress on that session's ticket.</summary>
    private async Task JoinSessionTicketAsync(int ticketId, string tech, string machineName)
    {
        try
        {
            var t = await _tickets.GetAsync(TenantId, ticketId);
            // Only while it is on the bench: a job already sent to billing belongs to nobody in
            // particular, and putting a name on it would take it out of the office's queue.
            if (t == null || Assignees.Has(t.AssignedTo, tech) || t.Lane != (int)WorkLane.Bench) return;
            await _tickets.UpdateAsync(TenantId, ticketId, tech, null, null, Assignees.With(t.AssignedTo, tech), null);
            await LogToTicketAsync(ticketId, tech, $"{tech} joined the session on {machineName}.");
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] joining a session's ticket", TenantName); }
    }

    /// <summary>A recording has been uploaded and kept (1.6): one line on its ticket saying where to watch it.</summary>
    public void RecordingSaved(SessionRecording rec) =>
        _ = LogToTicketAsync(rec.TicketId, rec.RecordedBy,
            $"Recording saved: {RecordingService.Length(rec.Seconds)} of the session on {rec.MachineName} — watch it under Recordings on this ticket.");

    private async Task LogToTicketAsync(int ticketId, string author, string body, bool isSystem = true)
    {
        try
        {
            await _tickets.AddNoteAsync(TenantId, ticketId, author, body, isSystem, automatic: true);
            var fresh = await _tickets.GetAsync(TenantId, ticketId);
            if (fresh != null) BroadcastTechs(new TicketChangedMsg { Ticket = TicketService.ToSummary(fresh) });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] ticket note", TenantName); }
    }

    /// <summary>
    /// The name typed into Request Support, kept on the machine and shown on every open console at once (2026-10-18).
    /// Waits for the machine's stored record first: a first install asks for help seconds after its first hello, and
    /// a name written before the record is read would be overwritten by it (§111's lesson).
    /// </summary>
    private async Task RememberRequestedByAsync(string deviceId, string typed, string email)
    {
        try
        {
            if (OwnerNames.Clean(typed).Length == 0) return;
            await WhenRecordReadAsync(deviceId);
            var now = DateTime.UtcNow;
            var kept = await _contacts.RememberRequestedByAsync(TenantId, deviceId, typed, now, email);
            if (kept.Name.Length == 0) return;
            lock (_lock)
            {
                if (!_devices.TryGetValue(deviceId, out var live)) return;
                live.RequestedBy = kept.Name; live.RequestedByEmail = kept.Email; live.RequestedByUtc = new DateTimeOffset(now, TimeSpan.Zero);
            }
            BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(deviceId) });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] keeping the name from a support request", TenantName); }
    }

    private static DateTimeOffset? AsUtc(DateTime? utc) =>
        utc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc)) : null;

    /// <summary>
    /// Pushes a machine's new owner — company, person, or nobody — to every open console, so the
    /// device list changes under the technician's eyes rather than at their next sign-in.
    /// </summary>
    public void RefreshDeviceOwner(string deviceId, int? clientId, string clientName, int? contactId, string contactName)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var info)) return;
            info.ClientId = clientId ?? 0;
            info.ClientName = clientId.HasValue ? clientName : "";
            info.ContactId = contactId ?? 0;
            info.ContactName = contactId.HasValue ? contactName : "";
            // A new customer may mean a different health check: a machine that follows its
            // customer and moves from an SLA client to one without an SLA loses it here.
            ApplyHealth(info);
        }
        TellAgentItsHealthCheck(deviceId);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(deviceId) });
    }

    // ------------------------------------------------------------------ the monthly health check
    //
    // Clint, 2026-09-28: not every customer should get the health check — SLA clients and the ones
    // chosen. So it is a tick on the CUSTOMER, which every machine of theirs follows (including
    // machines added later), with a per-machine override for the odd one out.
    //
    // The hub holds the decision and the AGENT carries it out, because only a SYSTEM service can
    // register a SYSTEM scheduled task. The agent is told the state wanted, not "install now", so
    // a machine that was off at the time catches up when it connects. What the agent reports back
    // is kept separately from what was wanted, and both are shown — §41's rule.

    /// <summary>Works out what a machine's health check comes to. Call under _lock.</summary>
    private void ApplyHealth(DeviceInfo info)
    {
        info.ClientHealthCheck = info.ClientId > 0 && _clientHealth.TryGetValue(info.ClientId, out var on) && on;
        info.HealthCheckAvailable = _healthAvailable;
        info.HealthCheckWanted = HealthCheckModes.Wanted(_healthAvailable, info.HealthCheckMode, info.ClientHealthCheck);
    }

    /// <summary>
    /// Tells a machine's agent what is wanted. Nothing is sent until the machine's stored record has
    /// been read (<see cref="_healthLoaded"/>) — a stranger's default is "off", and "off" removes.
    /// </summary>
    private void TellAgentItsHealthCheck(string deviceId)
    {
        DeviceInfo? info; List<DeviceConn> agents; int day;
        lock (_lock)
        {
            if (!_healthLoaded.Contains(deviceId) || !_devices.TryGetValue(deviceId, out info)) return;
            agents = _deviceConns.Where(d => d.DeviceId == deviceId && d.Role == DeviceRole.Agent).ToList();
            day = _healthDay;
        }
        foreach (var a in agents)
            a.Ws.Send(new HealthCheckWantedMsg { On = info.HealthCheckWanted, DayOfMonth = day });
    }

    /// <summary>
    /// One machine's choice: follow its customer, on, or off. From the console's right-click and
    /// from the portal's client page — the ONE write path for it, so the two cannot disagree.
    /// Returns "" or why not.
    /// </summary>
    public async Task<string> SetDeviceHealthCheckAsync(string deviceId, int mode, string who)
    {
        mode = HealthCheckModes.Normalise(mode);
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.DeviceId == deviceId);
            if (d == null) return "That machine is not on record.";
            d.HealthCheckMode = mode;
            await db.SaveChangesAsync();
        }
        string name;
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var info)) return "";
            info.HealthCheckMode = mode;
            ApplyHealth(info);
            name = info.MachineName;
        }
        _log.LogInformation("[{Tenant}] health check on {Machine} set to {Mode} by {Who}", TenantName, name,
            mode switch { HealthCheckModes.On => "ON", HealthCheckModes.Off => "OFF", _ => "as its customer" }, who);
        TellAgentItsHealthCheck(deviceId);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(deviceId) });
        return "";
    }

    /// <summary>A customer's tick, from the portal. Every machine that follows them moves with it.</summary>
    public async Task<string> SetClientHealthCheckAsync(int clientId, bool on, string who)
    {
        string name;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var c = await db.Clients.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.Id == clientId);
            if (c == null) return "Client not found.";
            c.HealthCheck = on;
            name = c.Name;
            await db.SaveChangesAsync();
        }
        List<string> affected;
        lock (_lock)
        {
            if (on) _clientHealth[clientId] = true; else _clientHealth.Remove(clientId);
            affected = _devices.Values.Where(d => d.ClientId == clientId).Select(d => d.DeviceId).ToList();
            foreach (var id in affected) ApplyHealth(_devices[id]);
        }
        _log.LogInformation("[{Tenant}] health check for client {Client} {State} by {Who}", TenantName, name, on ? "ON" : "off", who);
        foreach (var id in affected)
        {
            TellAgentItsHealthCheck(id);
            BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(id) });
        }
        return "";
    }

    /// <summary>The company switch or its mail changed (portal, Health check settings).</summary>
    public void RefreshHealthCheckAvailability(bool available, int dayOfMonth)
    {
        List<string> all;
        lock (_lock)
        {
            if (_healthAvailable == available && _healthDay == dayOfMonth) return;
            _healthAvailable = available; _healthDay = dayOfMonth;
            all = _devices.Keys.ToList();
            foreach (var id in all) ApplyHealth(_devices[id]);
        }
        foreach (var id in all)
        {
            TellAgentItsHealthCheck(id);
            BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(id) });
        }
    }

    /// <summary>
    /// What the machine says is on it. Only the AGENT may say it — it is the one thing on the
    /// machine that can see a SYSTEM scheduled task, and the only one that acts on it.
    /// </summary>
    private async Task RecordHealthCheckStatusAsync(DeviceConn dc, HealthCheckStatusMsg m)
    {
        if (dc.Role != DeviceRole.Agent) return;
        var note = (m.Note ?? "").Trim();
        if (note.Length > 400) note = note[..400];
        lock (_lock)
        {
            if (!_devices.TryGetValue(dc.DeviceId, out var info)) return;
            info.HealthCheckInstalled = m.Installed;
            info.HealthCheckNote = note;
        }
        try
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.DeviceId == dc.DeviceId);
            if (d != null)
            {
                d.HealthCheckInstalled = m.Installed;
                d.HealthCheckNote = note;
                d.HealthCheckReportedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] storing health check status for {Id}", TenantName, dc.DeviceId); }
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(dc.DeviceId) });
    }

    /// <summary>A copy of the live record for one machine, or null — for the portal, which is the
    /// record and otherwise cannot see whether a machine's agent is connected.</summary>
    public DeviceInfo? LiveDevice(string deviceId)
    {
        lock (_lock) if (!_devices.ContainsKey(deviceId)) return null;
        return Snapshot(deviceId);
    }

    /// <summary>Device ids with a live connection right now, so the portal can show online counts.</summary>
    public HashSet<string> OnlineDeviceIds()
    {
        lock (_lock) return _devices.Values.Where(d => d.Online).Select(d => d.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public object Status()
    {
        lock (_lock) return new
        {
            tenant = TenantName,
            technicians = _techs.Select(t => t.Who.DisplayName).Distinct().ToList(),
            devicesOnline = _devices.Values.Count(d => d.Online), devicesKnown = _devices.Count,
            ringing = _calls.Values.Count(c => c.Status == CallStatus.Ringing),
            sessions = _sessions.Count
        };
    }
}
