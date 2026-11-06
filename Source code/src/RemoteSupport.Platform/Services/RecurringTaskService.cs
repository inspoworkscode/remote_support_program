using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>What a person typed to set up or change a recurring task. Read by <see cref="RecurringTaskService.Problem"/>.</summary>
public sealed class RecurringInput
{
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public string Kind { get; set; } = "";
    public int? ClientId { get; set; }
    /// <summary>Everybody it goes to: "Jason, Ryan".</summary>
    public string AssignedTo { get; set; } = "";
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public Repeats Repeats { get; set; }
    public bool WeekdaysOnly { get; set; }
    public int Days { get; set; }
    public int MonthDay { get; set; }

    /// <summary>The calendar half, as a schedule, so the same arithmetic answers for a form and a row.</summary>
    public RecurringTask Calendar() => new() { Repeats = Repeats, WeekdaysOnly = WeekdaysOnly, Days = Days, MonthDay = MonthDay };
}

/// <summary>One recurring task for a list: the row, and the words around it.</summary>
public sealed record RecurringRow(RecurringTask Task, string ClientName, string Repeats, int Opened, int StillOpen, bool MayChange);

/// <summary>
/// Recurring tasks (Clint, 2026-10-07; <c>docs/NEXT-SESSION-WORKFLOWS.md</c>, "Recurring task"). A
/// schedule is set up once and opens an ordinary <see cref="TicketCategory.Recurring"/> ticket on
/// every day it falls due, in the MORNING of that day in the company's own time zone (his answer 5),
/// already given to its people. The ticket then goes the route General goes: Done, or To be invoiced.
///
/// <para>EVERY rule is here — what a schedule must say, which days it falls on, who may change or
/// cancel it, and when a ticket opens — so the portal, the console and the morning sweep cannot
/// disagree. Three of them are Clint's answers and easy to "improve" by accident:</para>
/// <list type="bullet">
/// <item>A ticket opens on its day even if last time's is still open (answer 3); the old one shows
/// as overdue rather than holding the new one back. <see cref="IsOverdue"/>.</item>
/// <item>Anybody may set one up; only the people on it, a supervisor or the owner may change or
/// cancel it (answer 4). <see cref="MayChange"/>.</item>
/// <item>Cancelling stops FUTURE tickets only. One already open stays open, with a note saying so.</item>
/// </list>
///
/// <para>A day missed while the server was down is opened late rather than skipped — a monthly
/// billing run must not quietly vanish — up to <see cref="CatchUpLimit"/> per schedule per pass.</para>
/// </summary>
public sealed class RecurringTaskService
{
    /// <summary>The hour of the company's morning at which the day's tickets open (answer 5).</summary>
    public const int MorningHour = 6;
    /// <summary>The most days one schedule catches up in one pass, so a long outage cannot flood a queue.</summary>
    public const int CatchUpLimit = 31;
    /// <summary>What every company starts with (answer 1). It may add its own beside them.</summary>
    public static readonly string[] DefaultKinds = { "Maintenance", "Billing", "Readings" };

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly TicketService _tickets;
    /// <summary>One sweep at a time: the minute's sweep and a schedule saved "for today" must not both open the day.</summary>
    private static readonly SemaphoreSlim Opening = new(1, 1);

    public RecurringTaskService(IDbContextFactory<PlatformDb> dbf, TicketService tickets) { _dbf = dbf; _tickets = tickets; }

    /// <summary>A ticket was opened by a schedule: tenant, ticket id. The hub tells every console.</summary>
    public event Action<int, int>? TicketMade;

    // ================================================================ the calendar

    private static int Bit(DayOfWeek d) => 1 << (int)d;
    public static readonly DayOfWeek[] Week =
        { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

    /// <summary>Whether a schedule falls on this day (a company date). The 31st in a 30-day month is its 30th — never skipped.</summary>
    public static bool Falls(RecurringTask r, DateTime day) => r.Repeats switch
    {
        Repeats.Daily => !r.WeekdaysOnly || day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
        Repeats.Weekly => (r.Days & Bit(day.DayOfWeek)) != 0,
        Repeats.Monthly => day.Day == Math.Min(r.MonthDay is >= 1 and <= 31 ? r.MonthDay : 31, DateTime.DaysInMonth(day.Year, day.Month)),
        _ => false
    };

    /// <summary>
    /// A company's calendar date as it is stored: midnight, marked UTC. NOT because it is a UTC time —
    /// it has no zone at all — but because PlatformDb converts every DateTime that is not marked UTC
    /// with ToUniversalTime(), which asks the SERVER's zone: on this PC (South Africa) Wednesday the 7th
    /// was written as 22:00 on Tuesday the 6th. recurringcheck found it; the live server, on UTC, hid it.
    /// </summary>
    public static DateTime AsDate(DateTime d) => DateTime.SpecifyKind(d.Date, DateTimeKind.Utc);

    /// <summary>The first day on or after <paramref name="from"/> that the schedule falls on, as <see cref="AsDate"/> stores it.</summary>
    public static DateTime NextOnOrAfter(RecurringTask r, DateTime from)
    {
        var d = AsDate(from);
        // Every schedule Problem() accepts falls at least once a month; the bound is only a guard.
        for (int i = 0; i < 400; i++, d = d.AddDays(1))
            if (Falls(r, d)) return d;
        return AsDate(from).AddYears(1);
    }

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>"Every Monday and Thursday", "Monthly on the last day" — what every screen says a schedule is.</summary>
    public static string Describe(RecurringTask r)
    {
        switch (r.Repeats)
        {
            case Repeats.Daily:
                return r.WeekdaysOnly ? "Every weekday (Monday to Friday)" : "Every day";
            case Repeats.Weekly:
                var days = Week.Where(d => (r.Days & Bit(d)) != 0).Select(d => d.ToString()).ToList();
                if (days.Count == 7) return "Every day of the week";
                return "Every " + (days.Count <= 1 ? days.FirstOrDefault() ?? "—" : string.Join(", ", days.Take(days.Count - 1)) + " and " + days[^1]);
            default:
                if (r.MonthDay is < 1 or > 31) return "Monthly on the last day";
                return $"Monthly on the {Ordinal(r.MonthDay)}" + (r.MonthDay >= 29 ? " (the last day in shorter months)" : "");
        }
    }

    /// <summary>A company date as every screen writes it: "Monday 12 October 2026".</summary>
    public static string Day(DateTime d) => d.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What is wrong with a schedule as typed, or empty. The one check the portal and the console both meet.</summary>
    public static string Problem(RecurringInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Title)) return "Please enter a subject.";
        if (string.IsNullOrWhiteSpace(i.Kind)) return "Choose the type of task, or type a new one.";
        if (Assignees.Parse(i.AssignedTo).Count == 0) return "Tick who this task goes to — at least one person.";
        return i.Repeats switch
        {
            Repeats.Daily => "",
            Repeats.Weekly => (i.Days & 127) == 0 ? "Tick at least one day of the week." : "",
            Repeats.Monthly => i.MonthDay is < 0 or > 31 ? "Choose the day of the month." : "",
            _ => "Choose daily, weekly or monthly."
        };
    }

    /// <summary>
    /// A recurring ticket still waiting on its people after the day it was due. Only at Due: once it
    /// has gone to be invoiced the people on it have done their part. Asked of the company's clock,
    /// which the request or the hub has set.
    /// </summary>
    public static bool IsOverdue(Ticket t, DateTime? companyToday = null) =>
        t.RecurringTaskId != null && t.DueOn != null && t.Stage == Workflow.RecurringFirst
        && t.DueOn.Value.Date < (companyToday ?? CompanyClock.Now).Date;

    /// <summary>Who may change or cancel a schedule (answer 4): the people on it, a supervisor or the owner.</summary>
    public static bool MayChange(RecurringTask r, TenantRole role, string me) =>
        Permissions.CanStepInAnywhere(role) || (me.Length > 0 && Assignees.Has(r.AssignedTo, me));

    /// <summary>Who may see a schedule in the list: whoever sees every ticket, and the people on it.</summary>
    public static bool MaySee(RecurringTask r, TenantRole role, string me) =>
        Permissions.CanSeeAllTickets(role) || MayChange(r, role, me);

    // ================================================================ the types

    /// <summary>The company's types, defaults first then its own by name. The defaults are written the first time anybody asks.</summary>
    public async Task<List<string>> KindsAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var rows = await db.RecurringKinds.Where(k => k.TenantId == tenantId).ToListAsync();
        if (rows.Count == 0)
        {
            foreach (var name in DefaultKinds)
            {
                var k = new RecurringKind { TenantId = tenantId, Name = name, MatchKey = QuoteService.MatchKey(name), CreatedBy = "" };
                db.RecurringKinds.Add(k); rows.Add(k);
            }
            await db.SaveChangesAsync();
        }
        return rows.OrderBy(k => Array.IndexOf(DefaultKinds, k.Name) is var i && i >= 0 ? i : 99).ThenBy(k => k.Name).Select(k => k.Name).ToList();
    }

    /// <summary>The type as the company spells it: an existing one if it is the same words in any order or case, else a new one, kept.</summary>
    private static async Task<string> KindFor(PlatformDb db, int tenantId, string typed, string by)
    {
        var name = string.Join(" ", typed.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var key = QuoteService.MatchKey(name);
        var have = await db.RecurringKinds.FirstOrDefaultAsync(k => k.TenantId == tenantId && k.MatchKey == key);
        if (have != null) return have.Name;
        db.RecurringKinds.Add(new RecurringKind { TenantId = tenantId, Name = name, MatchKey = key, CreatedBy = by });
        return name;
    }

    // ================================================================ setting up, changing, cancelling

    /// <summary>
    /// Sets one up. The first ticket is due on the first day it falls on from today; if that is today
    /// and the morning has come, it opens now, and is returned.
    /// </summary>
    public async Task<(RecurringTask? task, string error, Ticket? first)> CreateAsync(int tenantId, string by, RecurringInput input, DateTime? utcNow = null)
    {
        var problem = Problem(input);
        if (problem.Length > 0) return (null, problem, null);
        // Switched off in the company's Job types: no new schedules (the ones on file are kept).
        var off = await _tickets.CategoryRefusalAsync(tenantId, TicketCategory.Recurring);
        if (off.Length > 0) return (null, off, null);
        var zone = await CompanyClock.ForTenantAsync(_dbf, tenantId);
        var today = (utcNow ?? DateTime.UtcNow).Local(zone).Date;

        await using var db = await _dbf.CreateDbContextAsync();
        var r = new RecurringTask { TenantId = tenantId, CreatedBy = by };
        await Apply(db, r, input, by);
        r.NextDue = NextOnOrAfter(r, today);
        db.RecurringTasks.Add(r);
        await db.SaveChangesAsync();

        await OpenDueAsync(tenantId, utcNow);
        var first = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.TenantId == tenantId && t.RecurringTaskId == r.Id);
        return (r, "", first);
    }

    private static async Task Apply(PlatformDb db, RecurringTask r, RecurringInput i, string by)
    {
        r.Title = i.Title.Trim();
        r.Details = (i.Details ?? "").Trim();
        r.Kind = await KindFor(db, r.TenantId, i.Kind, by);
        r.ClientId = i.ClientId is > 0 ? i.ClientId : null;
        r.AssignedTo = Assignees.Normalise(i.AssignedTo);
        r.Priority = i.Priority;
        r.Repeats = i.Repeats;
        // Only what the chosen frequency reads is kept, so the row says one thing.
        r.WeekdaysOnly = i.Repeats == Repeats.Daily && i.WeekdaysOnly;
        r.Days = i.Repeats == Repeats.Weekly ? i.Days & 127 : 0;
        r.MonthDay = i.Repeats == Repeats.Monthly ? i.MonthDay : 0;
        r.UpdatedUtc = DateTime.UtcNow;
    }

    public async Task<RecurringTask?> GetAsync(int tenantId, int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.RecurringTasks.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId);
    }

    /// <summary>
    /// Changes one without cancelling it. The next due day is worked out again from today — but never
    /// a day a ticket was already opened for, so moving Monday to Monday-and-Tuesday on a Monday does
    /// not open Monday twice.
    /// </summary>
    public async Task<string> UpdateAsync(int tenantId, int id, string by, TenantRole role, RecurringInput input, DateTime? utcNow = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var r = await db.RecurringTasks.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (r == null) return "That recurring task is not on file.";
        if (!MayChange(r, role, by)) return "Only the people on this task, a supervisor or the owner may change it.";
        if (r.CancelledUtc != null) return "This recurring task was cancelled. Set up a new one instead.";
        var problem = Problem(input);
        if (problem.Length > 0) return problem;

        var zone = await CompanyClock.ForTenantAsync(_dbf, tenantId);
        var today = (utcNow ?? DateTime.UtcNow).Local(zone).Date;
        await Apply(db, r, input, by);
        var from = r.LastOpenedFor.HasValue && r.LastOpenedFor.Value.Date >= today ? r.LastOpenedFor.Value.Date.AddDays(1) : today;
        r.NextDue = NextOnOrAfter(r, from);
        await db.SaveChangesAsync();
        await OpenDueAsync(tenantId, utcNow);
        return "";
    }

    /// <summary>
    /// Stops it opening any more tickets. Asks why; keeps the row, marked, so its tickets still say
    /// where they came from. A ticket already open stays open, told in its story.
    /// </summary>
    public async Task<string> CancelAsync(int tenantId, int id, string by, TenantRole role, string? reason)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var r = await db.RecurringTasks.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId);
        if (r == null) return "That recurring task is not on file.";
        if (!MayChange(r, role, by)) return "Only the people on this task, a supervisor or the owner may cancel it.";
        if (r.CancelledUtc != null) return "This recurring task is already cancelled.";
        if (string.IsNullOrWhiteSpace(reason)) return "Say why it is being cancelled.";

        r.CancelledUtc = DateTime.UtcNow;
        r.CancelledBy = by;
        r.CancelReason = reason.Trim();
        r.UpdatedUtc = DateTime.UtcNow;
        var open = await db.Tickets.Where(t => t.TenantId == tenantId && t.RecurringTaskId == id
                && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved).ToListAsync();
        foreach (var t in open)
            db.TicketNotes.Add(new TicketNote { TenantId = tenantId, TicketId = t.Id, Author = by, IsSystem = true, Stage = t.Stage,
                Body = $"Recurring task cancelled: {r.CancelReason}. No more will be opened; this one stays open until it is done." });
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>Every schedule this person may see, active first, with what each has opened.</summary>
    public async Task<List<RecurringRow>> ListAsync(int tenantId, TenantRole role, string me)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var all = await db.RecurringTasks.Where(r => r.TenantId == tenantId).ToListAsync();
        var mine = all.Where(r => MaySee(r, role, me)).ToList();
        var ids = mine.Select(r => r.Id).ToList();
        var counts = await db.Tickets.Where(t => t.TenantId == tenantId && t.RecurringTaskId != null && ids.Contains(t.RecurringTaskId.Value))
            .GroupBy(t => t.RecurringTaskId!.Value)
            .Select(g => new { Id = g.Key, All = g.Count(), Open = g.Count(t => t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved) })
            .ToDictionaryAsync(x => x.Id);
        var clientIds = mine.Where(r => r.ClientId != null).Select(r => r.ClientId!.Value).Distinct().ToList();
        var names = await db.Clients.Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name);
        return mine
            .OrderBy(r => r.CancelledUtc != null).ThenBy(r => r.CancelledUtc == null ? r.NextDue : DateTime.MaxValue).ThenByDescending(r => r.CancelledUtc)
            .Select(r => new RecurringRow(r, r.ClientId is int c && names.TryGetValue(c, out var n) ? n : "", Describe(r),
                counts.TryGetValue(r.Id, out var k) ? k.All : 0, counts.TryGetValue(r.Id, out var k2) ? k2.Open : 0, MayChange(r, role, me)))
            .ToList();
    }

    /// <summary>The tickets a schedule has opened, newest first.</summary>
    public async Task<List<Ticket>> TicketsOfAsync(int tenantId, int id, int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Tickets.Where(t => t.TenantId == tenantId && t.RecurringTaskId == id)
            .OrderByDescending(t => t.DueOn).ThenByDescending(t => t.Id).Take(take).ToListAsync();
    }

    /// <summary>What a ticket shows about the schedule it came from, for this person.</summary>
    public async Task<RecurringInfo?> InfoAsync(Ticket t, TenantRole role, string me)
    {
        if (t.RecurringTaskId == null) return null;
        var r = await GetAsync(t.TenantId, t.RecurringTaskId.Value);
        if (r == null) return null;
        return new RecurringInfo
        {
            TaskId = r.Id, TaskNumber = r.Number, Kind = r.Kind, Repeats = Describe(r), Cancelled = r.CancelledUtc != null,
            Next = r.CancelledUtc != null
                ? $"Cancelled {r.CancelledUtc.Value.Local():d MMMM yyyy} by {r.CancelledBy}: {r.CancelReason}"
                : $"Next: {Day(r.NextDue)}",
            MayChange = r.CancelledUtc == null && MayChange(r, role, me)
        };
    }

    // ================================================================ the morning

    /// <summary>
    /// Opens every ticket that is due: for each schedule not cancelled, each day from its next due
    /// day up to today — today only once the company's morning has come. One company, or all of them.
    /// Free and suspended companies are passed over (the free version has no tickets).
    /// </summary>
    public async Task<int> OpenDueAsync(int? tenantId = null, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        await Opening.WaitAsync();
        try
        {
            int made = 0;
            await using var db = await _dbf.CreateDbContextAsync();
            // Every zone is within a day of UTC's date, so this asks for no more than could be due.
            var horizon = now.Date.AddDays(1);
            var due = await db.RecurringTasks
                .Where(r => r.CancelledUtc == null && r.NextDue <= horizon && (tenantId == null || r.TenantId == tenantId))
                .ToListAsync();
            foreach (var group in due.GroupBy(r => r.TenantId))
            {
                var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == group.Key);
                if (tenant == null || FreePlan.Is(tenant.Plan) || tenant.Status is not (TenantStatus.Active or TenantStatus.Trial)) continue;
                // Recurring tasks switched off (Job types): the days still pass, so switching them on again opens the
                // next one on its own day and never a pile of the ones missed.
                bool opening = JobTypes.CategoryOn(tenant.JobTypesOff, TicketCategory.Recurring);
                var zone = CompanyClock.Find(tenant.TimeZone);
                var local = now.Local(zone);
                foreach (var r in group)
                {
                    for (int n = 0; n < CatchUpLimit; n++)
                    {
                        var day = AsDate(r.NextDue);
                        if (day > local.Date || (day == local.Date && local.Hour < MorningHour)) break;
                        if (opening && await OpenOneAsync(db, r, day, zone)) made++;
                        if (opening) r.LastOpenedFor = day;
                        r.NextDue = NextOnOrAfter(r, day.AddDays(1));
                        await db.SaveChangesAsync();
                    }
                }
            }
            return made;
        }
        finally { Opening.Release(); }
    }

    /// <summary>Opens the ticket for one day, unless that day's is already on file. True if one was opened.</summary>
    private async Task<bool> OpenOneAsync(PlatformDb db, RecurringTask r, DateTime day, TimeZoneInfo zone)
    {
        if (await db.Tickets.AnyAsync(t => t.TenantId == r.TenantId && t.RecurringTaskId == r.Id && t.DueOn == day)) return false;
        var client = r.ClientId != null ? await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.ClientId) : null;
        var t = await _tickets.CreateAsync(r.TenantId, r.Title, r.Details, "Recurring task", TicketSource.Recurring,
            contactName: client?.ContactName ?? "", priority: r.Priority, assignedTo: r.AssignedTo,
            category: TicketCategory.Recurring, clientId: client?.Id, extra: x => { x.RecurringTaskId = r.Id; x.DueOn = day; x.DueDate = day; });   // its due date is its day (§165)
        var people = Assignees.Parse(r.AssignedTo);
        db.TicketNotes.Add(new TicketNote
        {
            // Automatic: Latest activity has the new ticket itself (HANDOVER §164).
            TenantId = r.TenantId, TicketId = t.Id, Author = "Recurring task", IsSystem = true, Automatic = true, Stage = t.Stage,
            Body = $"{r.Kind} — due {Day(day)}. {Describe(r)}" + (client != null ? $", for {client.Name}" : "") +
                   (people.Count > 0 ? $". Assigned to {Assignees.Say(people)}." : ".")
        });
        await db.SaveChangesAsync();
        TicketMade?.Invoke(r.TenantId, t.Id);
        return true;
    }
}

/// <summary>Opens the day's recurring tickets: once a minute, so a company's morning is met within a minute of it coming.</summary>
public sealed class RecurringTaskSweep : BackgroundService
{
    private readonly RecurringTaskService _tasks;
    private readonly ILogger<RecurringTaskSweep> _log;
    public RecurringTaskSweep(RecurringTaskService tasks, ILogger<RecurringTaskSweep> log) { _tasks = tasks; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // At start-up as well: a server restarted at 07:00 opens the morning's at once, not a minute later.
        try { await _tasks.OpenDueAsync(); } catch (Exception ex) { _log.LogWarning(ex, "recurring task sweep"); }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await _tasks.OpenDueAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "recurring task sweep"); }
        }
    }
}
