using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// What one server says about itself (2026-10-13, the control dashboard, stage B): its machine, how busy it is, and
/// every company on it with its people, technicians and machines. The control server reads its own directly and every
/// other server's from <c>GET /api/control/status</c> — the same record both ways, so the dashboard draws them alike.
/// </summary>
public sealed record ServerReport
{
    public string Name { get; init; } = "";
    public string Region { get; init; } = "";
    public string Address { get; init; } = "";
    public string Version { get; init; } = "";
    public string Database { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public DateTime AtUtc { get; init; } = DateTime.UtcNow;
    public int Cores { get; init; }
    public long MemoryTotalMb { get; init; }
    public long MemoryAvailableMb { get; init; }
    public double CpuPercent { get; init; }
    public double DiskTotalGb { get; init; }
    public double DiskFreeGb { get; init; }
    /// <summary>Consoles + machine connections + two per session: the connections this server is holding now.</summary>
    public int ConnectionsNow { get; init; }
    public int SessionsNow { get; init; }
    public List<CompanyRow> Companies { get; init; } = new();
    public List<WaitingRow> Waiting { get; init; } = new();
    /// <summary>This server's open places (2026-10-22, <see cref="OpenPlaces"/>), or null from a server older than that.</summary>
    public OpenPlaces.State? Places { get; init; }
    /// <summary>
    /// The version of the apps this server hands out (2026-10-16, auto-update stage A). build.ps1 stamps the platform
    /// and the apps it carries with one version, so it is this server's own (<see cref="RemoteSupport.Shared.AppVersion.Current"/>). Empty
    /// from a server older than this — the dashboard then says nothing about versions for it.
    /// </summary>
    public string AppVersion { get; init; } = "";
    /// <summary>Every machine on this server by its version (the older of its agent's and tray app's), newest first.</summary>
    public List<VersionCount> MachineVersions { get; init; } = new();
    /// <summary>How this server's release is going out (auto-update, 2026-10-16), or null: not a stamped build, or a server older than this.</summary>
    public UpdateService.Summary? Update { get; init; }
}

/// <summary>How many machines run one version; <see cref="Version"/> is empty for machines that have not said.</summary>
public sealed record VersionCount(string Version, int Machines);

/// <summary>One company as the dashboard shows it, wherever it lives.</summary>
public sealed record CompanyRow
{
    public int Id { get; init; }
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string ContactName { get; init; } = "";
    public string ContactNumber { get; init; } = "";
    public string SupportEmail { get; init; } = "";
    public string Country { get; init; } = "";
    public string Region { get; init; } = "";
    public bool Free { get; init; }
    /// <summary>Which one-person plan, as a person reads it — "Free" or "Basic" (§163); empty for the full version, and from an
    /// older server, where <see cref="Free"/> meant the Basic version.</summary>
    public string PlanName { get; init; } = "";
    public string Status { get; init; } = "";
    public bool TrialExpired { get; init; }
    public DateTime? TrialEndsUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public bool ServiceOn { get; init; }
    public int Seats { get; init; }
    /// <summary>The most seats it may have (2026-10-25, <see cref="Pricing.MostSeatsFor"/>); 0 from an older server, which means its package.</summary>
    public int MostSeats { get; init; }
    /// <summary>What it asked us for, in all (0 = nothing), when, why and who — optional, so an older server's report still reads.</summary>
    public int SeatsAskedFor { get; init; }
    public DateTime? SeatsAskedUtc { get; init; }
    public string SeatsAskedNote { get; init; } = "";
    public string SeatsAskedBy { get; init; } = "";
    /// <summary>People in seats now, by <see cref="Pricing.PeopleInSeats"/>.</summary>
    public int InSeats { get; init; }
    public bool Pilot { get; init; }
    public bool NotBilled { get; init; }
    public string PriceLabel { get; init; } = "";
    /// <summary>Everybody on the People page (not removed).</summary>
    public int People { get; init; }
    /// <summary>Of them, how many are ticked Technician — Clint's question.</summary>
    public int Technicians { get; init; }
    /// <summary>Of them, how many use the console (Technician or Supervisor) — what capacity counts.</summary>
    public int ConsoleUsers { get; init; }
    public int Machines { get; init; }
    public int MachinesOnline { get; init; }
    public int ConsolesOnline { get; init; }
    public int SessionsNow { get; init; }
    public List<PersonRow> Owners { get; init; } = new();
    /// <summary>
    /// A full-version signup whose card is at PayFast (2026-10-13, stage B): PayFast's notice switches it on, so Approvals
    /// shows it waiting for the card and offers no Approve — a person must not switch on a company that has not paid.
    /// </summary>
    public bool CardAtPayfast { get; init; }
    /// <summary>How the company pays by card (stage D, CompanyBilling.PaymentWords): "", "Card", "Card, unpaid since …", "Cancelled, ends …".</summary>
    public string Payment { get; init; } = "";

    /// <summary>The status as a person reads it: "Trial ended" rather than "Trial" once it has.</summary>
    public string StatusText => TrialExpired ? "Trial ended" : Status;
}

public sealed record PersonRow(string Name, string Email);
/// <summary>One waiting-list entry. <see cref="Full"/>, <see cref="PlaceUntilUtc"/> and <see cref="PlaceUsed"/> since 2026-10-22 (<see cref="OpenPlaces"/>) — absent, so false and null, from an older server.</summary>
public sealed record WaitingRow(int Id, string Region, string Country, string CompanyName, string ContactName, string Email, string Phone, DateTime CreatedUtc,
    bool Full = false, DateTime? PlaceUntilUtc = null, bool PlaceUsed = false);

/// <summary>Builds THIS server's report.</summary>
public sealed class ServerReporter
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly HubRegistry _hubs;
    private readonly ServerReadings _readings;
    private readonly PlatformAdmins _admins;
    private readonly IConfiguration _cfg;
    private readonly string _database;
    private readonly UpdateService _updates;

    public ServerReporter(IDbContextFactory<PlatformDb> dbf, HubRegistry hubs, ServerReadings readings, PlatformAdmins admins, IConfiguration cfg, string database, UpdateService updates)
    { _dbf = dbf; _hubs = hubs; _readings = readings; _admins = admins; _cfg = cfg; _database = database; _updates = updates; }

    public async Task<ServerReport> BuildAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var tenants = await db.Tenants.Include(t => t.Technicians).OrderBy(t => t.CompanyName).ToListAsync();
        var machines = await db.Devices.GroupBy(d => d.TenantId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var waiting = await db.WaitingList.OrderBy(w => w.CreatedUtc).ToListAsync();
        var live = _hubs.All.ToDictionary(h => h.TenantId, h => h.LoadNow());
        var atPayfast = (await db.PayfastSubscriptions.Where(s => s.Purpose == "signup" && s.Status == PayfastStatus.AwaitingCard)
            .Select(s => s.TenantId).Distinct().ToListAsync()).ToHashSet();
        var cards = (await db.PayfastSubscriptions.AsNoTracking().Where(s => s.Purpose != "test").ToListAsync())
            .GroupBy(s => s.TenantId)
            .ToDictionary(g => g.Key, g => g.Where(s => s.Status == PayfastStatus.Active).OrderByDescending(s => s.Id).FirstOrDefault() ?? g.OrderByDescending(s => s.Id).First());

        var rows = tenants.Select(t =>
        {
            var people = t.Technicians.Where(p => !p.Disabled).ToList();
            live.TryGetValue(t.Id, out var l);
            return new CompanyRow
            {
                Id = t.Id, Slug = t.Slug, Name = t.CompanyName, ContactName = t.ContactName, ContactNumber = t.ContactNumber,
                SupportEmail = t.SupportEmail, Country = t.Country, Region = t.Region, Free = FreePlan.Is(t.Plan), PlanName = FreePlan.Is(t.Plan) ? FreePlan.ShortNameOf(t.Plan) : "",
                Status = t.Status.ToString(), TrialExpired = t.TrialExpired, TrialEndsUtc = t.TrialEndsUtc, CreatedUtc = t.CreatedUtc,
                ServiceOn = t.IsServiceEnabled, Seats = t.Seats, NotBilled = t.NotBilled, PriceLabel = t.PriceLabel,
                MostSeats = Pricing.MostSeatsFor(t), SeatsAskedFor = t.SeatsAskedFor, SeatsAskedUtc = t.SeatsAskedUtc,
                SeatsAskedNote = t.SeatsAskedNote, SeatsAskedBy = t.SeatsAskedBy, InSeats = Pricing.PeopleInSeats(t.Technicians), Pilot = t.Pilot,
                CardAtPayfast = t.Status == TenantStatus.Pending && atPayfast.Contains(t.Id),
                Payment = CompanyBilling.PaymentWords(cards.GetValueOrDefault(t.Id)),
                People = people.Count,
                Technicians = people.Count(p => (p.Role & TenantRole.Technician) != 0),
                ConsoleUsers = people.Count(p => (p.Role & TenantRoles.Seated) != 0),
                Machines = machines.GetValueOrDefault(t.Id), MachinesOnline = l.machinesOnline, ConsolesOnline = l.consoles, SessionsNow = l.sessions,
                Owners = people.Where(p => (p.Role & TenantRoles.Owners) != 0 && p.Email.Contains('@')).OrderBy(p => p.DisplayName)
                    .Select(p => new PersonRow(p.DisplayName, p.Email)).ToList()
            };
        }).ToList();

        var versions = (await db.Devices.AsNoTracking().Select(d => new { d.AgentVersion, d.ClientVersion }).ToListAsync())
            .GroupBy(d => RemoteSupport.Shared.AppVersion.Oldest(d.AgentVersion, d.ClientVersion))
            .Select(g => new VersionCount(g.Key, g.Count()))
            .OrderByDescending(v => v.Version, Comparer<string>.Create(RemoteSupport.Shared.AppVersion.Compare)).ToList();

        var r = _readings.Now();
        return new ServerReport
        {
            Name = _admins.ServerName, Region = Pricing.For(_cfg["Platform:Region"]).Code,
            Address = (_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/'), Version = BuildStamp.Value, Database = _database,
            StartedUtc = r.StartedUtc, Cores = r.Cores, MemoryTotalMb = r.MemoryTotalMb, MemoryAvailableMb = r.MemoryAvailableMb,
            CpuPercent = r.CpuPercent, DiskTotalGb = r.DiskTotalGb, DiskFreeGb = r.DiskFreeGb,
            ConnectionsNow = live.Values.Sum(x => x.consoles + x.machineConnections + 2 * x.sessions),
            SessionsNow = live.Values.Sum(x => x.sessions),
            Companies = rows,
            AppVersion = RemoteSupport.Shared.AppVersion.Current, MachineVersions = versions, Update = await _updates.SummaryAsync(),
            Waiting = waiting.Select(w => new WaitingRow(w.Id, w.Region, w.Country, w.CompanyName, w.ContactName, w.Email, w.Phone, w.CreatedUtc,
                w.Full, w.PlaceExpiresUtc, w.PlaceUsedUtc != null)).ToList(),
            Places = await OpenPlaces.StateAsync(db)
        };
    }
}

/// <summary>
/// How full a server is (2026-10-13) — MEASURED, not guessed (HANDOVER §126): on <c>s-g-4cpu-8gb</c> (7,940 MB) one
/// server carries about <see cref="MeasuredConnections"/> connections comfortably, a company of Clint's profile (10
/// technicians, 500 machines each with its agent, 10 sessions) being ~1,030 of them. Memory is the limit once Caddy
/// reaches the platform through its socket, so a bigger server carries more in proportion to its memory — an estimate
/// from the measurement, which a server's own <c>CapacityConnections</c> replaces once one is measured.
///
/// <para>Five readings, and the WORST decides the level: planned (from headcount — what the companies on it would hold
/// on a busy day), connections now, memory in use, processor over five minutes, disk. Amber at 70%, red at 85% (Clint's
/// answer). In the load test 70% memory was ~20 companies and 85% ~25 — where it bent at 27.</para>
/// </summary>
public static class ServerCapacity
{
    public const int MeasuredConnections = 18_500;
    public const double MeasuredMemoryMb = 7_940;
    public const double Amber = 70, Red = 85;

    public enum Level { Green = 0, Amber = 1, Red = 2, Unreachable = 3 }

    public sealed record Measure(string Name, double Percent, string Detail);
    public sealed record Assessment(Level Level, int Capacity, int Planned, List<Measure> Measures, string Worst, string Advice);

    /// <summary>What one company would hold on a busy day: each console user's console and session (host and viewer), and each machine's tray app and agent.</summary>
    public static int PlannedConnections(CompanyRow c) => c.ServiceOn ? 3 * c.ConsoleUsers + 2 * c.Machines : 0;

    public static int CapacityOf(ServerReport r, int ownFigure) =>
        ownFigure > 0 ? ownFigure : r.MemoryTotalMb <= 0 ? MeasuredConnections : (int)Math.Round(MeasuredConnections * r.MemoryTotalMb / MeasuredMemoryMb);

    public static Assessment Assess(ServerReport r, int ownFigure = 0)
    {
        var cap = CapacityOf(r, ownFigure);
        var planned = r.Companies.Sum(PlannedConnections);
        var memUsed = r.MemoryTotalMb > 0 ? 100.0 * (r.MemoryTotalMb - r.MemoryAvailableMb) / r.MemoryTotalMb : 0;
        var diskUsed = r.DiskTotalGb > 0 ? 100.0 * (r.DiskTotalGb - r.DiskFreeGb) / r.DiskTotalGb : 0;
        var m = new List<Measure>
        {
            new("Planned", Pct(planned, cap), $"{planned:N0} of {cap:N0} connections if every company were busy"),
            new("Connections", Pct(r.ConnectionsNow, cap), $"{r.ConnectionsNow:N0} open now"),
            new("Memory", Math.Round(memUsed, 1), $"{(r.MemoryTotalMb - r.MemoryAvailableMb) / 1024.0:0.0} of {r.MemoryTotalMb / 1024.0:0.0} GB in use"),
            new("Processor", Math.Round(r.CpuPercent, 1), $"{r.Cores} cores, average over 5 minutes"),
            new("Disk", Math.Round(diskUsed, 1), $"{r.DiskTotalGb - r.DiskFreeGb:0} of {r.DiskTotalGb:0} GB used")
        };
        var worst = m.OrderByDescending(x => x.Percent).First();
        var level = worst.Percent >= Red ? Level.Red : worst.Percent >= Amber ? Level.Amber : Level.Green;
        var region = Pricing.For(r.Region).Name;
        var advice = level switch
        {
            Level.Green => "Room to spare.",
            _ when worst.Name == "Disk" => level == Level.Red
                ? "The disk is nearly full — add space to its volume now."
                : "The disk is filling — plan more space for its volume.",
            Level.Amber => $"Getting full ({worst.Name.ToLowerInvariant()}). Plan the next step: a bigger size for this server, or a new server for {region}.",
            _ => $"Full ({worst.Name.ToLowerInvariant()}). New companies should go elsewhere — upgrade this server now, or set up a new server for {region}."
        };
        return new Assessment(level, cap, planned, m, worst.Name, advice);
    }

    public static string Label(Level l) => l switch { Level.Green => "OK", Level.Amber => "Nearing capacity", Level.Red => "At capacity", _ => "Not answering" };

    private static double Pct(double n, double of) => of <= 0 ? 0 : Math.Round(100.0 * n / of, 1);
}
