using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Read-only ticket history in the portal. Administrators and billing / reception both reach this,
/// because invoicing needs to see what work was done and how long it took.
/// </summary>
public sealed class TicketsModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public TicketsModel(TenantService t, IDbContextFactory<PlatformDb> dbf) : base(t) => _dbf = dbf;

    public List<Ticket> Tickets { get; private set; } = new();
    public string Filter { get; private set; } = "open";
    public string Search { get; private set; } = "";
    /// <summary>
    /// How the list is ordered: a column and a direction (<see cref="RemoteSupport.Shared.TicketSort"/>), chosen by clicking
    /// its heading (2026-10-26, HANDOVER §165, from SuperOps' list — "Every column ... can be sorted by simply clicking").
    /// It was a Sort box of four dates (2026-10-13), and those keys still work. Remembered in the browser
    /// (<see cref="SortCookie"/>), so it is the order the page opens in next time.
    /// </summary>
    public string Sort { get; private set; } = RemoteSupport.Shared.TicketSort.Of(null);
    /// <summary>Gathered under each customer, busiest first — the view the page had before (§130's "Customer"), a tick now;
    /// within each customer the headings still sort. Its own cookie, <see cref="GroupCookie"/>.</summary>
    public bool Grouped { get; private set; }
    public const string ByCustomer = "customer";
    public const string SortCookie = "rsp_ticket_sort";
    public const string GroupCookie = "rsp_ticket_group";
    /// <summary>The tickets whose newest entry is the customer's emailed reply — "Customer replied" in Status (§165).</summary>
    public HashSet<int> Replied { get; private set; } = new();
    /// <summary>Set when the client page links here — a real relationship, not a name search.</summary>
    public int? ClientFilter { get; private set; }
    /// <summary>The same client's number, as the address asked for it.</summary>
    public int? ClientNumber { get; private set; }
    /// <summary>One machine's history. Matched on the name, so an uninstall/reinstall that split
    /// the machine into two device rows still shows as one history.</summary>
    public string MachineFilter { get; private set; } = "";
    public string FilterLabel { get; private set; } = "";
    public DateTime? From { get; private set; }
    public DateTime? To { get; private set; }
    private Dictionary<int, int> _minutes = new();
    private Dictionary<int, string> _clientNames = new();
    public int TotalMinutes => _minutes.Values.Sum();

    /// <summary>Whether this person is looking at the whole company's work or only their own.</summary>
    public bool CanSeeAll => Permissions.CanSeeAllTickets(MyRole);
    /// <summary>Open tickets nobody has been given. Shown to a technician as work they may take.</summary>
    public int PoolCount { get; private set; }

    /// <summary>
    /// One customer's tickets, as one block in the queue.
    ///
    /// Since 3.6.6 every remote session has its own ticket, so a busy day is a hundred rows. A
    /// flat list of a hundred rows is unreadable, and the whole change then looks like a mistake.
    /// Grouping by customer is what keeps it legible — and it is how somebody thinks about the day
    /// anyway: whose work is this.
    /// </summary>
    public sealed class Group
    {
        public string Name = "";
        public int ClientId;
        public List<Ticket> Tickets = new();
        public int Minutes;
    }

    public List<Group> Groups { get; private set; } = new();

    /// <summary>How many sessions are filed under each ticket, so a parent can say so in the list.</summary>
    private Dictionary<int, int> _childCounts = new();
    public int ChildrenOf(int ticketId) => _childCounts.TryGetValue(ticketId, out var n) ? n : 0;

    public int MinutesFor(int ticketId) => _minutes.TryGetValue(ticketId, out var m) ? m : 0;
    public string ClientNameFor(int? clientId) =>
        clientId.HasValue && _clientNames.TryGetValue(clientId.Value, out var n) ? n : "—";

    public string StatusName(TicketStatus s) => s switch
    { TicketStatus.InProgress => "In progress", TicketStatus.OnHold => "On hold", _ => s.ToString() };

    /// <summary>
    /// A heading's address: this page as it is — filter, search, dates, client, machine, grouping — sorted by that column,
    /// turned round if it is the one in use (§165).
    /// </summary>
    public string SortLink(string column)
    {
        var q = Request.Query.Where(kv => kv.Key is not ("sort" or "group"))
            .Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value.ToString())).ToList();
        q.Add("sort=" + Uri.EscapeDataString(RemoteSupport.Shared.TicketSort.Toggle(Sort, column)));
        return "/portal/tickets?" + string.Join("&", q);
    }

    /// <summary>What a screen reader says the heading does — and the tooltip.</summary>
    public string SortHint(string column, string heading)
    {
        var (col, desc) = RemoteSupport.Shared.TicketSort.Parse(Sort);
        return col == column ? $"Sorted by {heading}{(desc ? ", highest first" : "")} — click to turn it round" : $"Sort by {heading}";
    }

    /// <summary>"2 min ago", "3 h ago", else the date — the list's Last updated, as SuperOps says it; the exact time on hover.</summary>
    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours} h ago";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} ago";
        return utc.Local().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The priority's dot: red urgent, amber high, the brand's normal, grey low.</summary>
    public static string PriorityDot(TicketPriority p) => p switch
    {
        TicketPriority.Urgent => "#dc2626", TicketPriority.High => "#d97706", TicketPriority.Low => "#94a3b8", _ => "var(--grape)"
    };

    public string StatusPill(TicketStatus s) => s switch
    {
        TicketStatus.Resolved or TicketStatus.Closed => "pill-ok",
        TicketStatus.OnHold => "pill-warn",
        TicketStatus.InProgress => "pill-muted",
        _ => "pill-warn"
    };

    public async Task<IActionResult> OnGetAsync(string? filter, string? search, DateTime? from, DateTime? to,
        int? c, string? machine, string? sort, string? group)
    {
        if (!await LoadAsync()) return Redirect("/login");
        // One client's tickets, by the client's NUMBER in the company (HANDOVER §159); the old ?client={id} is sent on.
        if (PortalAddresses.HasOldQuery(Request.Query)) return await FromOldQueryAsync();
        Filter = filter ?? "open"; Search = search ?? ""; From = from; To = to;
        // Chosen now, or the last choice in this browser. "customer" was the grouped view when it was a Sort choice (and
        // what a cookie from before may still hold): grouped, newest update first, as it was.
        var wanted = sort ?? Request.Cookies[SortCookie];
        bool oldGrouped = wanted == ByCustomer;
        Sort = RemoteSupport.Shared.TicketSort.Of(oldGrouped ? null : wanted);
        Grouped = group != null ? group is "1" or "true" or "on" : oldGrouped || Request.Cookies[GroupCookie] == "1";
        var keep = new CookieOptions { Path = "/portal", MaxAge = TimeSpan.FromDays(365), HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps };
        if (sort != null || oldGrouped) Response.Cookies.Append(SortCookie, Sort, keep);
        if (group != null || oldGrouped) Response.Cookies.Append(GroupCookie, Grouped ? "1" : "0", keep);
        MachineFilter = machine ?? "";
        var me = User.Identity?.Name ?? "";

        await using var db = await _dbf.CreateDbContextAsync();
        // A number that is not this company's client is id 0: nothing matches, as another company's id never did.
        ClientNumber = c; ClientFilter = c is int asked ? await PortalAddresses.ClientIdAsync(db, TenantId, asked) : null;
        // Scope first, filter second. A technician reaching this page since 3.6.3 must not be able
        // to read a colleague's work by asking for filter=all — the filter is what they chose, this
        // is what they are allowed.
        var q = TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == TenantId), MyRole, Me);
        q = Filter switch
        {
            "closed" => q.Where(t => t.Status == TicketStatus.Resolved || t.Status == TicketStatus.Closed),
            // the dashboard's "unassigned" and "assigned to me" tiles land here
            // The bench's pool. A job in the billing queue is assigned to nobody too, but it is not
            // waiting for a technician to pick it up.
            "unassigned" => q.Where(t => t.AssignedTo == "" && t.Lane == (int)WorkLane.Bench && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed),
            // Everything waiting on the office administrator or a supervisor, whatever its category.
            "billing" => q.Where(t => t.Lane == (int)WorkLane.Office || t.Lane == (int)WorkLane.Supervisor),
            // The @mentions still waiting for this person to reply.
            "asked" => q.Where(t => t.Mentions.Any(m => m.Person == me && m.AnsweredUtc == null)),
            "mine" => TicketService.OnIt(q, me).Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed),
            // The dashboard's "urgent or high priority" tile: the same open jobs it counts.
            "urgent" => q.Where(t => (t.Priority == TicketPriority.Urgent || t.Priority == TicketPriority.High)
                && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed),
            "all" => q,
            _ => q.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed),
        };
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim();
            q = q.Where(t => t.Subject.Contains(s) || t.MachineName.Contains(s) || t.ContactName.Contains(s));
        }
        if (ClientFilter.HasValue) q = q.Where(t => t.ClientId == ClientFilter.Value);
        if (!string.IsNullOrWhiteSpace(MachineFilter))
        {
            var m = MachineFilter.Trim();
            q = q.Where(t => t.MachineName == m);
        }
        if (From.HasValue) { var f = CompanyClock.ToUtc(From.Value.Date); q = q.Where(t => t.CreatedUtc >= f); }
        if (To.HasValue) { var e = CompanyClock.ToUtc(To.Value.Date.AddDays(1)); q = q.Where(t => t.CreatedUtc < e); }

        Tickets = await TicketService.Sorted(q, Sort).Take(500).ToListAsync();
        Replied = await TicketService.CustomerRepliedAsync(db, TenantId, Tickets.Select(t => t.Id).ToList());

        // Counted separately from the list, because the list is whatever filter they chose and
        // this is the one number a technician needs whichever filter that was: how much work is
        // sitting there that nobody has been given.
        PoolCount = await db.Tickets.CountAsync(t => t.TenantId == TenantId && t.AssignedTo == "" && t.Lane == (int)WorkLane.Bench
            && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);

        var clientIds = Tickets.Where(t => t.ClientId.HasValue).Select(t => t.ClientId!.Value).Distinct().ToList();
        if (clientIds.Count > 0)
            _clientNames = await db.Clients.Where(c => c.TenantId == TenantId && clientIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name);

        // Say out loud what the list has been narrowed to, so a filtered page never reads as the whole list.
        if (ClientFilter.HasValue)
        {
            var cn = await db.Clients.Where(c => c.TenantId == TenantId && c.Id == ClientFilter.Value)
                .Select(c => c.Name).FirstOrDefaultAsync();
            FilterLabel = cn ?? "this client";
        }
        else if (!string.IsNullOrWhiteSpace(MachineFilter)) FilterLabel = MachineFilter;

        var ids = Tickets.Select(t => t.Id).ToList();
        var sessions = await db.Sessions.Where(s => s.TenantId == TenantId && s.TicketId != null && ids.Contains(s.TicketId!.Value) && s.EndedUtc != null).ToListAsync();
        _minutes = sessions.GroupBy(s => s.TicketId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(s => Math.Max(1, (int)(s.EndedUtc!.Value - s.StartedUtc).TotalMinutes)));

        _childCounts = (await db.Tickets
                .Where(t => t.TenantId == TenantId && t.ParentTicketId != null && ids.Contains(t.ParentTicketId!.Value))
                .Select(t => t.ParentTicketId!.Value).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        // Gathered under the customer they belong to. Busiest first, because a queue is read from
        // the top and the customer with eleven open jobs today is the one to look at.
        Groups = Tickets
            .GroupBy(t => t.ClientId ?? 0)
            .Select(g => new Group
            {
                ClientId = g.Key,
                Name = g.Key == 0 ? "Not linked to a client" : ClientNameFor(g.Key),
                Tickets = g.ToList(),   // in the order the headings chose (§165)
                Minutes = g.Sum(t => MinutesFor(t.Id))
            })
            .OrderBy(g => g.ClientId == 0)                  // strays last
            .ThenByDescending(g => g.Tickets.Count)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Page();
    }
}
