using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;
using AppVersion = RemoteSupport.Shared.AppVersion;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Machines, grouped by the customer who owns them, with everything nobody has claimed yet in a
/// bucket at the BOTTOM — the same order as the console's device list. It used to be at the top,
/// as the bucket needing action; technicians asked for the customers first (2026-09-30), and the
/// count of unlinked machines stays in the stats row at the top, so nothing is hidden by it.
/// </summary>
public sealed class DevicesModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ClientService _clients;
    private readonly ContactService _contacts;
    private readonly HubRegistry _hubs;
    public DevicesModel(TenantService t, IDbContextFactory<PlatformDb> dbf, ClientService clients,
        ContactService contacts, HubRegistry hubs) : base(t)
    { _dbf = dbf; _clients = clients; _contacts = contacts; _hubs = hubs; }

    /// <summary>One physical machine: the row that counts, plus older rows for the same name.</summary>
    public sealed class MachineRow
    {
        public Device Current = null!;
        public bool Online;
        /// <summary>Older device records for the same machine name — a reinstall, not a second PC.</summary>
        public List<Device> Duplicates = new();
        /// <summary>Open tickets raised against this machine name.</summary>
        public int OpenTickets;
        /// <summary>Set on an unassigned row when a machine of the same name is already linked to
        /// a client — almost always the same PC after a reinstall, not a machine nobody owns.</summary>
        public Client? SameNameOwnedBy;

        public int DiskUsedPct => Current.DiskTotalGb > 0
            ? (int)Math.Round((Current.DiskTotalGb - Current.DiskFreeGb) * 100.0 / Current.DiskTotalGb) : 0;
        public string Ram => Current.RamMb > 0 ? Math.Round(Current.RamMb / 1024.0) + " GB" : "—";
    }

    /// <summary>A customer and their machines. Client is null for the unassigned bucket.</summary>
    public sealed class Group
    {
        public Client? Client;
        public List<MachineRow> Machines = new();
        /// <summary>Open tickets for the client as a whole, not just these machines.</summary>
        public int OpenTickets;
        public bool IsUnassigned => Client == null;
        public string Name => Client?.Name ?? "Not linked to a client";
        public int OnlineCount => Machines.Count(m => m.Online);
    }

    /// <summary>
    /// Older than the apps this server hands out (2026-10-16, auto-update stage A). Never on an unstamped server — a
    /// development build is 1.0.0.0 and would call nothing older — and never for a machine that has not said.
    /// </summary>
    public static bool Behind(string version) =>
        version.Length > 0 && AppVersion.IsStamped(AppVersion.Current) && AppVersion.Compare(version, AppVersion.Current) < 0;

    /// <summary>
    /// Installed before auto-update (2026-10-16, stage F): its agent has connected and did not say it updates itself, so it
    /// needs ONE update by hand (the console's Scripts tool → Update this machine). A machine whose agent has never said
    /// anything since versions were recorded is left out — there is nothing yet to go on.
    /// </summary>
    public static bool NeedsUpdateByHand(Device d) => !d.SelfUpdates && d.AgentVersion.Length > 0;

    /// <summary>The Version cell's tooltip: each half on its own, and what this server hands out.</summary>
    public static string VersionDetail(Device d)
    {
        var parts = new List<string>();
        if (d.AgentVersion.Length > 0) parts.Add("Background service " + AppVersion.Label(d.AgentVersion));
        if (d.ClientVersion.Length > 0) parts.Add("Tray app " + AppVersion.Label(d.ClientVersion));
        if (parts.Count == 0) parts.Add("Not reported yet — it says at its next connection");
        if (Behind(AppVersion.Oldest(d.AgentVersion, d.ClientVersion)))
            parts.Add("Older than this server's apps, " + AppVersion.Label(AppVersion.Current));
        return string.Join(" · ", parts);
    }

    public List<Group> Groups { get; private set; } = new();
    public List<Client> Clients { get; private set; } = new();
    /// <summary>Every contact, so the owner picker can list people under their company.</summary>
    public List<Contact> Contacts { get; private set; } = new();

    /// <summary>The contacts at one client, for the owner picker's optgroup.</summary>
    public IEnumerable<Contact> ContactsOf(int clientId) => Contacts.Where(c => c.ClientId == clientId);

    /// <summary>
    /// What the owner picker's currently-selected option is for a machine: "p:12" for a person,
    /// "c:3" for the company itself, "0" for nobody. One value rather than two form fields,
    /// because picking an owner is one decision and it should not take two controls.
    /// </summary>
    public static string OwnerValue(Device d) =>
        d.ContactId.HasValue ? $"p:{d.ContactId.Value}"
        : d.ClientId.HasValue ? $"c:{d.ClientId.Value}"
        : "0";

    /// <summary>
    /// What the owner picker shows for its current value, written as clientpick.js writes a choice —
    /// "Bob — Zenith", "Zenith", or empty for nobody — so an untouched box submits unchanged.
    /// </summary>
    public string OwnerLabel(Device d)
    {
        var contact = d.ContactId.HasValue ? Contacts.FirstOrDefault(c => c.Id == d.ContactId.Value) : null;
        var client = Clients.FirstOrDefault(c => c.Id == (contact?.ClientId ?? d.ClientId));
        if (contact != null) return client != null ? $"{contact.Name} — {client.Name}" : contact.Name;
        return client?.Name ?? "";
    }

    /// <summary>Whose it is: the person, else the company, else the name given at a support request, else who is signed in.</summary>
    public string OwnerName(Device d)
    {
        var contact = d.ContactId.HasValue ? Contacts.FirstOrDefault(c => c.Id == d.ContactId.Value) : null;
        var client = d.ClientId.HasValue ? Clients.FirstOrDefault(c => c.Id == d.ClientId.Value) : null;
        return ContactService.OwnerName(contact?.Name ?? "", client?.Name ?? "", d.RequestedBy, d.UserName);
    }

    /// <summary>The name shown is only what somebody typed at a support request (2026-10-18) — marked on the page.</summary>
    public static bool NamedByRequest(Device d) =>
        RemoteSupport.Shared.OwnerNames.IsRequested(d.ContactId.HasValue ? "x" : "", d.ClientId.HasValue ? "x" : "", d.RequestedBy);

    /// <summary>The words under a name given at a support request: what it is, and since when.</summary>
    public static string RequestedTitle(Device d) =>
        "The name they gave when it was installed or when they asked for help"
        + (d.RequestedByUtc.HasValue ? " on " + DateTime.SpecifyKind(d.RequestedByUtc.Value, DateTimeKind.Utc).Local().ToString("d MMM yyyy") : "")
        + (d.RequestedByEmail.Length > 0 ? $", with {d.RequestedByEmail}" : "")
        + " — not checked. Link the machine to make it a client's.";

    /// <summary>People on file whose name fits the one given — offered, never chosen for anybody (2026-10-18).</summary>
    public List<(Contact Contact, string ClientName)> SuggestionsFor(Device d) =>
        ContactService.Suggestions(Contacts, d.RequestedBy, 3)
            .Select(c => (c, Clients.FirstOrDefault(x => x.Id == c.ClientId)?.Name ?? ""))
            .ToList();
    public int TotalMachines => Groups.Sum(g => g.Machines.Count);
    public int TotalOnline => Groups.Sum(g => g.OnlineCount);
    public int UnassignedCount => Groups.FirstOrDefault(g => g.IsUnassigned)?.Machines.Count ?? 0;

    private async Task<IActionResult> RenderAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var online = _hubs.Get(TenantId)?.OnlineDeviceIds() ?? new HashSet<string>();

        Clients = await _clients.AllAsync(TenantId);
        Contacts = await _contacts.AllAsync(TenantId);
        var devices = await db.Devices.Where(d => d.TenantId == TenantId).ToListAsync();

        // Open tickets per machine name and per client, in one read each rather than per row.
        var openTickets = await db.Tickets
            .Where(t => t.TenantId == TenantId && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)
            .Select(t => new { t.ClientId, t.MachineName }).ToListAsync();
        var byMachine = openTickets.Where(t => !string.IsNullOrEmpty(t.MachineName))
            .GroupBy(t => t.MachineName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var byClient = openTickets.Where(t => t.ClientId.HasValue)
            .GroupBy(t => t.ClientId!.Value).ToDictionary(g => g.Key, g => g.Count());

        // Fold rows sharing a machine name into one entry: live wins, else most recently seen.
        List<MachineRow> Fold(IEnumerable<Device> ds) =>
            ds.GroupBy(d => d.MachineName, StringComparer.OrdinalIgnoreCase)
              .Select(g =>
              {
                  var ordered = g.OrderByDescending(d => online.Contains(d.DeviceId))
                                 .ThenByDescending(d => d.LastSeenUtc).ToList();
                  return new MachineRow
                  {
                      Current = ordered[0],
                      Online = online.Contains(ordered[0].DeviceId),
                      Duplicates = ordered.Skip(1).ToList(),
                      OpenTickets = byMachine.TryGetValue(ordered[0].MachineName ?? "", out var n) ? n : 0
                  };
              })
              .OrderByDescending(m => m.Online).ThenByDescending(m => m.Current.LastSeenUtc).ToList();

        var groups = new List<Group>();

        foreach (var c in Clients.OrderBy(c => c.Name))
        {
            var mine = devices.Where(d => d.ClientId == c.Id).ToList();
            if (mine.Count == 0) continue;   // a client with no machines has nothing to show here
            groups.Add(new Group
            {
                Client = c,
                Machines = Fold(mine),
                OpenTickets = byClient.TryGetValue(c.Id, out var n) ? n : 0
            });
        }

        // Machines nobody has linked LAST, as in the console (DeviceList.Build).
        var loose = devices.Where(d => d.ClientId == null).ToList();
        if (loose.Count > 0) groups.Add(new Group { Client = null, Machines = Fold(loose) });

        // A machine name that is both unlinked and owned by somebody is the reinstall case: the
        // same PC, split across two device rows. Say whose it is rather than leaving it stranded
        // in the unassigned bucket looking like a machine nobody has ever claimed.
        var ownerByName = devices.Where(d => d.ClientId != null && !string.IsNullOrEmpty(d.MachineName))
            .GroupBy(d => d.MachineName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(d => d.ClientId).Distinct().Count() == 1)   // never guess between two owners
            .ToDictionary(g => g.Key, g => g.First().ClientId!.Value, StringComparer.OrdinalIgnoreCase);

        var unassigned = groups.FirstOrDefault(g => g.IsUnassigned);
        if (unassigned != null)
            foreach (var m in unassigned.Machines)
                if (ownerByName.TryGetValue(m.Current.MachineName ?? "", out var ownerId))
                    m.SameNameOwnedBy = Clients.FirstOrDefault(c => c.Id == ownerId);

        Groups = groups;
        // Machines taken off the list (2026-10-06): who, when and why, newest first — the history is kept.
        Removals = await db.DeviceRemovals.Where(r => r.TenantId == TenantId).OrderByDescending(r => r.RemovedUtc).Take(100).ToListAsync();
        if (!string.IsNullOrEmpty(Removed))
        {
            var gone = Removals.FirstOrDefault(r => r.DeviceId == Removed);
            if (gone != null) Message = $"Removed {gone.MachineName}.";
        }
        return Page();
    }

    /// <summary>Removed machines, newest first.</summary>
    public List<DeviceRemoval> Removals { get; private set; } = new();
    /// <summary>A supervisor or the owner: each machine has Remove (2026-10-06).</summary>
    public bool CanRemove => Permissions.CanRemoveDevices(MyRole);
    /// <summary>A supervisor or the owner: a reinstalled machine nobody has confirmed has Confirm (2026-10-07).</summary>
    public bool CanConfirmReinstall => Permissions.CanConfirmReinstall(MyRole);

    /// <summary>
    /// This reinstalled machine IS the one on the list (2026-10-07) — by the hub's own rule, as the console's
    /// right-click is, so the live consoles and the agent hear of it at once.
    /// </summary>
    public async Task<IActionResult> OnPostConfirmReinstallAsync(string deviceId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var hub = _hubs.Get(TenantId);
        var why = hub == null ? "The server is not ready; try again in a moment." : await hub.ConfirmReinstallAsync(deviceId ?? "", Me, MyRole);
        if (why.Length > 0) Error = why;
        else Message = $"{await MachineNameAsync(deviceId ?? "")} confirmed as the same machine. Unattended access works on it again as its person set it.";
        return await RenderAsync();
    }
    /// <summary>Back from RemoveDevice: the install id, never words (the name is read from the record).</summary>
    [BindProperty(SupportsGet = true)] public string? Removed { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        return await RenderAsync();
    }

    /// <summary>
    /// Sets who a machine belongs to from the single owner picker.
    ///
    /// <paramref name="owner"/> is "p:12" for a person, "c:3" for the company, "0" for nobody.
    /// Encoding both in one value is what lets this be one dropdown instead of two: a technician
    /// picking "Bob at Zenith" has said everything, and does not then have to confirm Zenith.
    /// </summary>
    public async Task<IActionResult> OnPostAssignAsync(string deviceId, string owner, bool addGiven = false)
    {
        if (!await LoadAsync()) return Redirect("/login");

        int? clientId = null, contactId = null;
        var value = (owner ?? "0").Trim();
        if (value.StartsWith("p:") && int.TryParse(value[2..], out var pid)) contactId = pid;
        else if (value.StartsWith("c:") && int.TryParse(value[2..], out var cid)) clientId = cid;

        // A client chosen with "add them as a contact" ticked (2026-10-23): the person who gave their name — at the install
        // or a support request — is added there with their email, and the machine linked to them.
        var given = clientId.HasValue && addGiven ? await GivenNameAsync(deviceId ?? "") : "";

        // ContactService owns this write, because it is the only thing that keeps a machine's
        // contact and client agreeing with each other. See its class comment.
        var (result, error) = given.Length > 0
            ? await _contacts.LinkDeviceToNewContactAsync(TenantId, deviceId ?? "", clientId!.Value, given)
            : await _contacts.LinkDeviceAsync(TenantId, deviceId ?? "", clientId, contactId);
        if (error.Length > 0) Error = error;
        else
        {
            var machine = await MachineNameAsync(deviceId);
            Message = result.ContactId.HasValue ? $"{machine} linked to {result.ContactName} at {result.ClientName}."
                : result.ClientId.HasValue ? $"{machine} linked to {result.ClientName}."
                : $"{machine} unlinked.";
            // push the change to any technician console that is open
            _hubs.Get(TenantId)?.RefreshDeviceOwner(deviceId, result.ClientId, result.ClientName,
                result.ContactId, result.ContactName);
        }
        return await RenderAsync();
    }

    /// <summary>
    /// Adds the person who asked for help as a contact at the client chosen, and links the machine to them (2026-10-18)
    /// — the name they gave, filled in, so nobody types it twice. A contact of that name there already is reused.
    /// </summary>
    public async Task<IActionResult> OnPostAddContactAsync(string deviceId, int clientId, string name)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var (result, error) = await _contacts.LinkDeviceToNewContactAsync(TenantId, deviceId ?? "", clientId, name ?? "");
        if (error.Length > 0) Error = error;
        else
        {
            Message = $"{await MachineNameAsync(deviceId ?? "")} linked to {result.ContactName} at {result.ClientName}.";
            _hubs.Get(TenantId)?.RefreshDeviceOwner(deviceId ?? "", result.ClientId, result.ClientName,
                result.ContactId, result.ContactName);
        }
        return await RenderAsync();
    }

    /// <summary>The name given on the machine, while nobody is linked as its person — else empty.</summary>
    private async Task<string> GivenNameAsync(string deviceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Devices.Where(d => d.TenantId == TenantId && d.DeviceId == deviceId && d.ContactId == null)
            .Select(d => d.RequestedBy).FirstOrDefaultAsync() ?? "";
    }

    /// <summary>The Set form offers to add the given name as a contact: there is one, and no person is linked (2026-10-23).</summary>
    public static bool OffersGiven(Device d) => !d.ContactId.HasValue && !string.IsNullOrWhiteSpace(d.RequestedBy);

    /// <summary>The words beside that tick: "Add Jane Smith (jane@acme.co.za) as a contact when I choose a client".</summary>
    public static string GivenTickText(Device d) =>
        $"Add {d.RequestedBy}{(d.RequestedByEmail.Length > 0 ? $" ({d.RequestedByEmail})" : "")} as a contact when I choose a client";

    private async Task<string> MachineNameAsync(string deviceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Devices.Where(d => d.TenantId == TenantId && d.DeviceId == deviceId)
            .Select(d => d.MachineName).FirstOrDefaultAsync() ?? "That machine";
    }
}
