using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Hub;

/// <summary>Owns one TenantHub per tenant and maps live session ids back to their tenant.</summary>
public sealed class HubRegistry
{
    private readonly ConcurrentDictionary<int, TenantHub> _hubs = new();
    private readonly ConcurrentDictionary<string, int> _sessionTenant = new();
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ILogger<HubRegistry> _log;
    private readonly Services.TicketService _tickets;
    private readonly Services.ClientService _clients;
    private readonly Services.PortalHandoffService _handoff;
    private readonly Services.TicketPhotoService _photos;
    private readonly Services.ContactService _contacts;
    private readonly IConfiguration _cfg;

    public HubRegistry(IDbContextFactory<PlatformDb> dbf, ILogger<HubRegistry> log, Services.TicketService tickets,
        Services.ClientService clients, Services.PortalHandoffService handoff, IConfiguration cfg,
        Services.TicketPhotoService photos, Services.ContactService contacts, Services.StaffChatService chat,
        Services.Visitors visitors, Services.WebChatService webChat, Services.StaffPresence presence,
        Services.TicketIntake intake)
    {
        // A ticket opened by email or the website form, or a customer's emailed reply (3.8.4),
        // reaches every console's list at once — the same as a website chat's left message.
        intake.TicketMade += (tenantId, ticketId) => { if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.PushTicketChangedAsync(ticketId); };
        _dbf = dbf; _log = log; _tickets = tickets; _clients = clients; _handoff = handoff; _cfg = cfg; _photos = photos; _contacts = contacts;
        Chat = chat; Visitors = visitors; WebChat = webChat; Presence = presence;
        // Website chat (3.8.3): every console of the company hears every chat — Clint: "every logged
        // in technician including supervisors and office administrators".
        WebChat.Changed += (tenantId, c) => { if (_hubs.TryGetValue(tenantId, out var hub)) hub.PushWebChat(new RemoteSupport.Shared.WebChatUpdateMsg { Chat = c }); };
        WebChat.Line += (tenantId, l) => { if (_hubs.TryGetValue(tenantId, out var hub)) hub.PushWebChat(new RemoteSupport.Shared.WebChatLineMsg { Line = l, ChatId = l.ChatId }); };
        WebChat.TicketMade += (tenantId, ticketId) => { if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.PushTicketChangedAsync(ticketId); };
        // Office chat (3.8.1): a message, or somebody reading one, reaches every console it concerns now.
        Chat.Sent += (tenantId, m) => { if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.PushStaffChatAsync(m); };
        Chat.ReadChanged += (tenantId, person) => { if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.PushChatOverviewAsync(person); };
        // Somebody was @mentioned, or answered: the badge on that person's console moves now.
        _tickets.MentionsChanged += (tenantId, person) =>
        {
            if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.PushMentionCountAsync(person);
        };
        // A job came into the billing queue, or one moved within or out of it: the office's consoles hear now.
        _tickets.BillingChanged += (tenantId, news) => { if (_hubs.TryGetValue(tenantId, out var hub)) hub.PushBilling(news); };
    }

    /// <summary>Sign-in tokens, so a hub can keep a running console's token alive. Set by Program.cs.</summary>
    public Services.AuthService? Auth { get; set; }

    /// <summary>Auto-update (2026-10-16): the rollout's rules, for what a hub tells consoles and takes from agents. Set by Program.cs.</summary>
    public Services.UpdateService? Updates { get; set; }

    /// <summary>Office chat, for the hubs: one service, so the portal and the console share its rules.</summary>
    public Services.StaffChatService Chat { get; }
    public Services.Visitors Visitors { get; }
    /// <summary>Website chat (3.8.3), for the hubs.</summary>
    public Services.WebChatService WebChat { get; }
    /// <summary>Who from the office is signed in — a console here counts, as does an open portal page.</summary>
    public Services.StaffPresence Presence { get; }
    /// <summary>
    /// The knowledge base (3.8.5), for the hubs: what a ticket says about it, and how many articles a
    /// machine's client has. Set by Program.cs like <see cref="Auth"/>, so a harness that builds a
    /// registry of its own needs nothing new; a hub without it simply says nothing about it.
    /// </summary>
    public Services.KnowledgeBaseService? Knowledge { get; set; }

    /// <summary>
    /// Recurring tasks (2026-10-07), for the hubs, set by Program.cs like <see cref="Knowledge"/>. A ticket a
    /// schedule opens on its morning reaches every console's list at once, as an emailed one does.
    /// </summary>
    public Services.RecurringTaskService? Recurring
    {
        get => _recurring;
        set
        {
            _recurring = value;
            if (value != null) value.TicketMade += (tenantId, ticketId) => { if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.PushTicketChangedAsync(ticketId); };
        }
    }
    private Services.RecurringTaskService? _recurring;

    /// <summary>
    /// Latest activity (2026-10-26, HANDOVER §164), for the hubs. Set by Program.cs like <see cref="Knowledge"/>: something
    /// new on a company's tickets moves every count in it, and somebody looking moves their own. A hub without it sends
    /// no counts and answers no list.
    /// </summary>
    public Services.TicketActivityService? Activity
    {
        get => _activity;
        set
        {
            _activity = value;
            if (value == null) return;
            value.Changed += tenantId => { if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.PushActivityCountsAsync(null); };
            value.SeenChanged += (tenantId, person) => { if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.PushActivityCountsAsync(person); };
        }
    }
    private Services.TicketActivityService? _activity;

    /// <summary>Emailing a comment to the client from a ticket (2026-10-09), for the hubs. Set by Program.cs like
    /// <see cref="Knowledge"/>; a hub without it tells the console updates are not offered.</summary>
    public Services.ClientUpdateService? ClientUpdates { get; set; }

    /// <summary>Outside help's codes (roadmap 3.9), for the hubs. Set by Program.cs like <see cref="Knowledge"/>;
    /// a hub without it makes no codes and says so.</summary>
    public Services.OutsiderCodeService? Outsiders { get; set; }

    /// <summary>
    /// The companies, for the hubs: each company's current look for its machines (2026-10-07, roadmap 4.6). Set by
    /// Program.cs like <see cref="Knowledge"/>; a hub without it sends no branding, and its machines keep the look
    /// they were installed with — as every machine did before.
    /// </summary>
    public Services.TenantService? Tenants
    {
        get => _tenants;
        set
        {
            _tenants = value;
            if (value != null) value.BrandingChanged += tenantId => { if (_hubs.TryGetValue(tenantId, out var hub)) _ = hub.BrandingChangedAsync(); };
        }
    }
    private Services.TenantService? _tenants;

    /// <summary>
    /// Guests' tokens (3.9, step 3): token -> who they said they are. A guest has no account, so this is
    /// their ONLY credential, and it opens one thing — the viewer socket of the session their code
    /// started (TenantHub.AdmitsVisitor). Made when the code works; gone when their guest connection
    /// closes (GuestVisitor).
    /// </summary>
    private readonly ConcurrentDictionary<string, Services.TechSession> _guests = new();
    public void AddGuest(string token, Services.TechSession who) => _guests[token] = who;
    public void ForgetGuest(string token) => _guests.TryRemove(token, out _);
    public Services.TechSession? GuestFromToken(string? token) =>
        !string.IsNullOrEmpty(token) && _guests.TryGetValue(token, out var who) ? who : null;

    /// <summary>
    /// A company's hub, loaded ONCE however many ask at the same moment (2026-10-08, BUGS #11). After a restart
    /// every company's machines arrive together; this used to load the hub on every one of their threads at once,
    /// synchronously, and the server stalled until the thread pool grew. The first to ask starts the load; the
    /// rest await that same load and hold no thread while they wait.
    /// </summary>
    public async Task<TenantHub?> GetAsync(int tenantId)
    {
        if (_hubs.TryGetValue(tenantId, out var h)) return h;
        var loading = _loading.GetOrAdd(tenantId, id => new Lazy<Task<TenantHub?>>(() => LoadAsync(id)));
        try
        {
            var hub = await loading.Value;
            // A company not there (yet) is asked about again next time, not remembered as missing.
            if (hub == null) _loading.TryRemove(new KeyValuePair<int, Lazy<Task<TenantHub?>>>(tenantId, loading));
            return hub;
        }
        catch
        {
            // A load that failed (the database away for a moment) is tried again by the next to ask.
            _loading.TryRemove(new KeyValuePair<int, Lazy<Task<TenantHub?>>>(tenantId, loading));
            throw;
        }
    }

    private readonly ConcurrentDictionary<int, Lazy<Task<TenantHub?>>> _loading = new();

    private async Task<TenantHub?> LoadAsync(int tenantId)
    {
        Tenant? t;
        await using (var db = await _dbf.CreateDbContextAsync()) t = await db.Tenants.FindAsync(tenantId);
        if (t == null) return null;
        var hub = await TenantHub.CreateAsync(t, _dbf, this, _log, _tickets, _clients, _handoff, _cfg, _photos, _contacts);
        return _hubs.GetOrAdd(tenantId, hub);
    }

    /// <summary>
    /// The same, for the few callers that cannot await. Once a company's hub is loaded — which the first of its
    /// machines or technicians to connect does, through <see cref="GetAsync"/> — this is a dictionary lookup.
    /// </summary>
    public TenantHub? Get(int tenantId) =>
        _hubs.TryGetValue(tenantId, out var h) ? h : GetAsync(tenantId).GetAwaiter().GetResult();

    /// <summary>The hub only if it is already loaded — for telling the connected something. Not loaded is nobody
    /// connected, and nothing to tell; loading one to tell nobody would be the stall above for no reason.</summary>
    public TenantHub? Loaded(int tenantId) => _hubs.TryGetValue(tenantId, out var h) ? h : null;

    public void RegisterSession(string sessionId, int tenantId) => _sessionTenant[sessionId] = tenantId;
    public void UnregisterSession(string sessionId) => _sessionTenant.TryRemove(sessionId, out _);
    public TenantHub? HubForSession(string sessionId) => _sessionTenant.TryGetValue(sessionId, out var t) ? Get(t) : null;

    public IEnumerable<TenantHub> All => _hubs.Values;

    /// <summary>A company can no longer use the service: everything of its leaves now (TenantHub.TurnAway). Nothing loaded, nothing to do.</summary>
    public (int consoles, int machines) TurnAway(int tenantId, string why) =>
        _hubs.TryGetValue(tenantId, out var hub) ? hub.TurnAway(why) : (0, 0);
}
