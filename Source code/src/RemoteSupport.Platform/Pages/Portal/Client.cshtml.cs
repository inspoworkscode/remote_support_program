using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// One page per customer: their details, their machines and their work. Everything here comes off
/// Device.ClientId and Ticket.ClientId — the relationship, not a search of the client's name.
/// Reception and accounts reach this too, so it stays on the folder's TenantOrBilling policy.
/// </summary>
public sealed class ClientModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ClientService _clients;
    private readonly ContactService _contacts;
    private readonly HubRegistry _hubs;

    public ClientModel(TenantService t, IDbContextFactory<PlatformDb> dbf, ClientService clients,
        ContactService contacts, HubRegistry hubs)
        : base(t) { _dbf = dbf; _clients = clients; _contacts = contacts; _hubs = hubs; }

    public Client Client { get; private set; } = null!;
    /// <summary>The people at this client, for the list and for the machine owner picker.</summary>
    public List<Contact> Contacts { get; private set; } = new();

    /// <summary>
    /// A machine as the customer thinks of it — one entry per machine name, with any older
    /// database rows for the same name folded in underneath rather than listed as separate PCs.
    /// </summary>
    public sealed class Machine
    {
        public Device Current = null!;
        public bool Online;
        /// <summary>Where its monthly health check stands, in words — decision and the machine's own report.</summary>
        public string HealthCheckText = "";
        public bool HealthCheckWanted;
        /// <summary>Older rows with the same machine name, newest first. Left behind by a reinstall.</summary>
        public List<Device> Duplicates = new();
        public string SpecSummary
        {
            get
            {
                var bits = new List<string>();
                if (!string.IsNullOrWhiteSpace(Current.Cpu)) bits.Add(Current.Cpu + (Current.Cores > 0 ? $" ({Current.Cores}c)" : ""));
                if (Current.RamMb > 0) bits.Add($"{Math.Round(Current.RamMb / 1024.0)} GB RAM");
                if (Current.DiskTotalGb > 0) bits.Add($"{Current.DiskTotalGb - Current.DiskFreeGb}/{Current.DiskTotalGb} GB disk");
                return bits.Count == 0 ? "—" : string.Join(" · ", bits);
            }
        }
        public int DiskUsedPct => Current.DiskTotalGb > 0
            ? (int)Math.Round((Current.DiskTotalGb - Current.DiskFreeGb) * 100.0 / Current.DiskTotalGb) : 0;
    }

    public List<Machine> Machines { get; private set; } = new();
    /// <summary>Machines not linked to anyone whose name matches one of this client's — almost
    /// always the leftover row from an uninstall and reinstall on the same PC.</summary>
    public List<Device> LooseMatches { get; private set; } = new();

    public List<Ticket> OpenTickets { get; private set; } = new();
    public List<Ticket> ClosedTickets { get; private set; } = new();
    public int CountOpen, CountInProgress, CountOnHold, CountResolvedThisMonth;

    /// <summary>This client's articles in the knowledge base (3.8.5).</summary>
    public int KbCount { get; private set; }

    private async Task<IActionResult> RenderAsync(int id)
    {
        var c = await _clients.GetAsync(TenantId, id);
        if (c == null) return NotFound();
        Client = c;
        Contacts = await _contacts.ForClientAsync(TenantId, id);
        if (MayRemove) OnIt = (await _clients.WhatIsOnAsync(TenantId, id)).Say();

        await using var db = await _dbf.CreateDbContextAsync();
        KbCount = await db.KbArticles.CountAsync(a => a.TenantId == TenantId && a.ClientId == id && a.DeletedUtc == null);
        var online = _hubs.Get(TenantId)?.OnlineDeviceIds() ?? new HashSet<string>();

        var mine = await db.Devices.Where(d => d.TenantId == TenantId && d.ClientId == id).ToListAsync();

        // Fold rows that share a machine name into one entry. The live one wins; if none is
        // connected, the most recently seen one does.
        Machines = mine.GroupBy(d => d.MachineName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(d => online.Contains(d.DeviceId))
                               .ThenByDescending(d => d.LastSeenUtc).ToList();
                return new Machine
                {
                    Current = ordered[0],
                    Online = online.Contains(ordered[0].DeviceId),
                    Duplicates = ordered.Skip(1).ToList()
                };
            })
            .OrderByDescending(m => m.Online).ThenByDescending(m => m.Current.LastSeenUtc).ToList();

        // The health check line for each machine, by the same two functions the console uses.
        // Whether the agent is connected lives only on the hub; the database holds what it last said.
        var hub = _hubs.Get(TenantId);
        foreach (var m in Machines)
        {
            var d = m.Current;
            m.HealthCheckWanted = HealthCheckModes.Wanted(Tenant.HealthCheckReady, d.HealthCheckMode, c.HealthCheck);
            var live = hub?.LiveDevice(d.DeviceId);
            m.HealthCheckText = HealthCheckModes.Describe(Tenant.HealthCheckReady, m.HealthCheckWanted,
                d.HealthCheckInstalled, d.HealthCheckNote, live?.AgentOnline ?? false);
        }

        var names = Machines.Select(m => m.Current.MachineName).ToList();
        if (names.Count > 0)
            LooseMatches = (await db.Devices.Where(d => d.TenantId == TenantId && d.ClientId == null).ToListAsync())
                .Where(d => names.Contains(d.MachineName, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(d => d.LastSeenUtc).ToList();

        var tickets = await db.Tickets.Where(t => t.TenantId == TenantId && t.ClientId == id)
            .OrderByDescending(t => t.UpdatedUtc).ToListAsync();

        OpenTickets = tickets.Where(t => t.Status is TicketStatus.Open or TicketStatus.InProgress or TicketStatus.OnHold)
            .OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedUtc).ToList();
        ClosedTickets = tickets.Where(t => t.Status is TicketStatus.Resolved or TicketStatus.Closed)
            .OrderByDescending(t => t.ClosedUtc ?? t.UpdatedUtc).Take(25).ToList();

        CountOpen = OpenTickets.Count(t => t.Status == TicketStatus.Open);
        CountInProgress = OpenTickets.Count(t => t.Status == TicketStatus.InProgress);
        CountOnHold = OpenTickets.Count(t => t.Status == TicketStatus.OnHold);

        // "This month" is the calendar month the technician is standing in, converted to stored UTC.
        var monthStart = CompanyClock.ToUtc(new DateTime(CompanyClock.Now.Year, CompanyClock.Now.Month, 1));
        CountResolvedThisMonth = tickets.Count(t => t.ClosedUtc.HasValue && t.ClosedUtc >= monthStart);

        return Page();
    }

    /// <summary>
    /// /portal/c/{the client's NUMBER in this company} (2026-10-23, HANDOVER §159): every handler is given the id looked up
    /// within the company — never one a form posted — and 0, "not found", for a number that is not this company's. The
    /// old /portal/clients/{id} is sent on to the new address (PortalPage).
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        if (await AddressedIdAsync(ctx, PortalAddresses.ClientIdAsync) is not int id) return;
        ctx.HandlerArguments["id"] = id;
        await base.OnPageHandlerExecutionAsync(ctx, next);
    }

    public async Task<IActionResult> OnGetAsync(int id, int? merged)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (merged == 1) Message = "The other client was removed, and everything that was on it is here now.";
        return await RenderAsync(id);
    }

    // ---- removing it (§172) ----

    public bool MayRemove => Permissions.CanRemoveClients(MyRole);
    /// <summary>What would move, in words ("3 tickets and 1 machine"); empty when nothing is on it.</summary>
    public string OnIt { get; private set; } = "";

    /// <summary>
    /// Remove this client, moving everything on it to <paramref name="into"/> first — every rule is
    /// <see cref="ClientService.RemoveAsync"/>. Then the client it went to, or the list.
    /// </summary>
    public async Task<IActionResult> OnPostRemoveAsync(int id, int? into)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var (done, error) = await _clients.RemoveAsync(TenantId, MyRole, id, into, Me);
        if (done == null) { Error = error; return await RenderAsync(id); }
        var hub = _hubs.Get(TenantId);
        foreach (var m in done.Machines)
            hub?.RefreshDeviceOwner(m.DeviceId, m.Owner.ClientId, m.Owner.ClientName, m.Owner.ContactId, m.Owner.ContactName);
        return done.Into != null ? Redirect(PortalAddresses.Client(done.Into.Number) + "?merged=1") : Redirect("/portal/clients?removed=1");
    }

    public async Task<IActionResult> OnPostSaveAsync(int id, string name, string? contact, string? phone,
        string? email, string? address, string? notes)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (string.IsNullOrWhiteSpace(name)) Error = "Client name is required.";
        else
        {
            var err = await _clients.UpdateAsync(TenantId, id, c =>
            {
                c.Name = name.Trim();
                c.ContactName = (contact ?? "").Trim();
                c.Phone = (phone ?? "").Trim();
                c.Email = (email ?? "").Trim();
                c.Address = (address ?? "").Trim();
                c.Notes = (notes ?? "").Trim();
            });
            if (err.Length > 0) Error = err; else Message = "Details saved.";
        }
        return await RenderAsync(id);
    }

    /// <summary>Link a loose machine row to this client, or unlink one of theirs.</summary>
    public async Task<IActionResult> OnPostLinkAsync(int id, string deviceId, bool unlink)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var (owner, error) = await _contacts.LinkDeviceAsync(TenantId, deviceId, unlink ? null : id, null);
        if (error.Length > 0) Error = error;
        else
        {
            Message = unlink ? "Machine unlinked." : $"Machine linked to {owner.ClientName}.";
            _hubs.Get(TenantId)?.RefreshDeviceOwner(deviceId, owner.ClientId, owner.ClientName,
                owner.ContactId, owner.ContactName);
        }
        return await RenderAsync(id);
    }

    /// <summary>
    /// Says which person at this client a machine belongs to — or that it belongs to the company
    /// itself, which is the right answer for a server or the PC at reception.
    /// </summary>
    public async Task<IActionResult> OnPostSetOwnerAsync(int id, string deviceId, int contactId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var (owner, error) = await _contacts.LinkDeviceAsync(TenantId, deviceId, id, contactId > 0 ? contactId : null);
        if (error.Length > 0) Error = error;
        else
        {
            Message = owner.ContactId.HasValue ? $"Machine is now {owner.ContactName}'s."
                : $"Machine now belongs to {owner.ClientName} rather than to one person.";
            _hubs.Get(TenantId)?.RefreshDeviceOwner(deviceId, owner.ClientId, owner.ClientName,
                owner.ContactId, owner.ContactName);
        }
        return await RenderAsync(id);
    }

    // ---- the monthly health check ----

    /// <summary>The customer's tick: every machine of theirs that follows them gets it.</summary>
    public async Task<IActionResult> OnPostHealthCheckAsync(int id, bool on)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanSetHealthCheck(MyRole)) Error = "You cannot change which clients get the health check.";
        else
        {
            var hub = _hubs.Get(TenantId);
            var err = hub == null ? "The server is not ready." : await hub.SetClientHealthCheckAsync(id, on, Me);
            if (err.Length > 0) Error = err;
            else Message = on
                ? (Tenant.HealthCheckReady
                    ? "Monthly health check on. Each machine installs it now, or when its background service next connects."
                    : "Ticked - but the health check is switched off for your company, so nothing will run until it is on under Health check.")
                : "Monthly health check off. Each machine removes it now, or when its background service next connects.";
        }
        return await RenderAsync(id);
    }

    /// <summary>One machine set differently from its customer.</summary>
    public async Task<IActionResult> OnPostMachineHealthCheckAsync(int id, string deviceId, int mode)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanSetHealthCheck(MyRole)) Error = "You cannot change which machines get the health check.";
        else
        {
            var hub = _hubs.Get(TenantId);
            var err = hub == null ? "The server is not ready." : await hub.SetDeviceHealthCheckAsync(deviceId, mode, Me);
            if (err.Length > 0) Error = err; else Message = "Saved.";
        }
        return await RenderAsync(id);
    }

    // ---- contacts ----

    public async Task<IActionResult> OnPostAddContactAsync(int id, string name, string? role, string? phone,
        string? email, string? notes)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var (contact, error) = await _contacts.CreateAsync(TenantId, id, name, phone, email, role, notes);
        if (error.Length > 0) Error = error; else Message = $"{contact!.Name} added.";
        return await RenderAsync(id);
    }

    public async Task<IActionResult> OnPostSaveContactAsync(int id, int contactId, string name, string? role,
        string? phone, string? email)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var error = await _contacts.UpdateAsync(TenantId, contactId, c =>
        {
            c.Name = (name ?? "").Trim();
            c.Role = (role ?? "").Trim();
            c.Phone = (phone ?? "").Trim();
            c.Email = (email ?? "").Trim();
        });
        if (error.Length > 0) Error = error; else Message = "Contact saved.";
        return await RenderAsync(id);
    }

    public async Task<IActionResult> OnPostArchiveContactAsync(int id, int contactId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var error = await _contacts.ArchiveAsync(TenantId, contactId);
        if (error.Length > 0) Error = error;
        else Message = "Contact archived, and taken off any machine they were on.";
        return await RenderAsync(id);
    }

    public static string StatusName(TicketStatus s) => s switch
    { TicketStatus.InProgress => "In progress", TicketStatus.OnHold => "On hold", _ => s.ToString() };

    public static string StatusPillFor(TicketStatus s) => s switch
    {
        TicketStatus.Resolved or TicketStatus.Closed => "pill-ok",
        TicketStatus.OnHold => "pill-muted",
        TicketStatus.InProgress => "pill-muted",
        _ => "pill-warn"
    };

    public static string PriorityPill(TicketPriority p) => p switch
    { TicketPriority.Urgent => "pill-bad", TicketPriority.High => "pill-warn", _ => "pill-muted" };

    public static string AgePill(int days) => days >= 14 ? "pill-bad" : days >= 7 ? "pill-warn" : "pill-muted";
    public static int Age(DateTime utc) => (int)(DateTime.UtcNow - utc).TotalDays;
}
