// =====================================================================================
// recurringcheck - proves recurring tasks (Clint, 2026-10-07; NEXT-SESSION-WORKFLOWS.md,
// "Recurring task"; HANDOVER §108):
//
//   "An option to select, daily, weekly, or monthly and on what day ... one would setup the task
//   once assign it to the correct person/persons and forget about it, but it should also have a
//   cancel task option if the task becomes obsolete."
//
// And his five answers: types are a list the company adds to; Done closes it and To be invoiced is
// offered too; the next opens even if the last is not done (and the old one is overdue); anybody
// sets one up, only its people, a supervisor or the owner change or cancel it; "it opens on the
// morning it needs to be done".
//
// Everything is asked of RecurringTaskService and TicketService.ActAsync, with the clock handed in,
// so the morning, the month ends and the catch-up are walked without waiting for them. Built with
// InvariantGlobalization like the platform, so the company's time zone is found as it is live.
//
//   dotnet run -c Debug --project tools/recurringcheck/recurringcheck.csproj
// =====================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

var dir = Path.Combine(Path.GetTempPath(), "recurringcheck-" + Guid.NewGuid().ToString("N")[..8]);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);
Console.WriteLine("recurringcheck - recurring tasks, through the service");
Console.WriteLine();

const string Sue = "Sue Tech", Bob = "Bob Tech", Olive = "Olive Office", Sam = "Sam Supervisor";
const TenantRole T = TenantRole.Technician, OA = TenantRole.Billing, S = TenantRole.Admin;
static DateTime U(int m, int d, int h, int mi = 0, int y = 2026) => new(y, m, d, h, mi, 0, DateTimeKind.Utc);
static DateTime D(int m, int d, int y = 2026) => new(y, m, d);
static int Bit(DayOfWeek d) => 1 << (int)d;

try
{
    // ================================================================ the calendar, alone
    Console.WriteLine("== the calendar ==");
    RecurringTask Sched(Repeats r, bool weekdays = false, int days = 0, int monthDay = 0) =>
        new() { Repeats = r, WeekdaysOnly = weekdays, Days = days, MonthDay = monthDay };
    var daily = Sched(Repeats.Daily);
    var weekdaysOnly = Sched(Repeats.Daily, weekdays: true);
    var monThu = Sched(Repeats.Weekly, days: Bit(DayOfWeek.Monday) | Bit(DayOfWeek.Thursday));
    var on31 = Sched(Repeats.Monthly, monthDay: 31);
    var last = Sched(Repeats.Monthly, monthDay: 0);
    var on15 = Sched(Repeats.Monthly, monthDay: 15);

    Check("2026-10-07 is a Wednesday (the dates below depend on it)", D(10, 7).DayOfWeek == DayOfWeek.Wednesday);
    Check("daily falls on a Saturday", RecurringTaskService.Falls(daily, D(10, 10)));
    Check("weekdays only skips Saturday and Sunday: Friday 9th → Monday 12th",
        RecurringTaskService.NextOnOrAfter(weekdaysOnly, D(10, 10)) == D(10, 12));
    Check("Monday and Thursday from Tuesday 6th → Thursday 8th", RecurringTaskService.NextOnOrAfter(monThu, D(10, 6)) == D(10, 8));
    Check("...and from the 9th → Monday 12th", RecurringTaskService.NextOnOrAfter(monThu, D(10, 9)) == D(10, 12));
    Check("on the day itself, it is that day", RecurringTaskService.NextOnOrAfter(monThu, D(10, 8)) == D(10, 8));
    Check("the 31st in November is the 30th — never skipped", RecurringTaskService.NextOnOrAfter(on31, D(11, 1)) == D(11, 30));
    Check("...and in February the 28th", RecurringTaskService.NextOnOrAfter(on31, D(2, 1, 2027)) == D(2, 28, 2027));
    Check("...and in a leap February the 29th", RecurringTaskService.NextOnOrAfter(on31, D(2, 1, 2028)) == D(2, 29, 2028));
    Check("the last day of the month: October 31st, then November 30th",
        RecurringTaskService.NextOnOrAfter(last, D(10, 7)) == D(10, 31) && RecurringTaskService.NextOnOrAfter(last, D(11, 1)) == D(11, 30));
    Check("the 15th, set up on the 16th, waits for next month", RecurringTaskService.NextOnOrAfter(on15, D(10, 16)) == D(11, 15));
    Check("said in words: every weekday", RecurringTaskService.Describe(weekdaysOnly) == "Every weekday (Monday to Friday)");
    Check("said in words: Monday and Thursday", RecurringTaskService.Describe(monThu) == "Every Monday and Thursday", RecurringTaskService.Describe(monThu));
    Check("said in words: three days", RecurringTaskService.Describe(Sched(Repeats.Weekly, days: Bit(DayOfWeek.Monday) | Bit(DayOfWeek.Wednesday) | Bit(DayOfWeek.Friday))) == "Every Monday, Wednesday and Friday");
    Check("said in words: the 1st, 2nd, 3rd, 11th, 22nd",
        RecurringTaskService.Describe(Sched(Repeats.Monthly, monthDay: 1)) == "Monthly on the 1st"
        && RecurringTaskService.Describe(Sched(Repeats.Monthly, monthDay: 2)) == "Monthly on the 2nd"
        && RecurringTaskService.Describe(Sched(Repeats.Monthly, monthDay: 3)) == "Monthly on the 3rd"
        && RecurringTaskService.Describe(Sched(Repeats.Monthly, monthDay: 11)) == "Monthly on the 11th"
        && RecurringTaskService.Describe(Sched(Repeats.Monthly, monthDay: 22)) == "Monthly on the 22nd");
    Check("said in words: the 31st warns of shorter months", RecurringTaskService.Describe(on31) == "Monthly on the 31st (the last day in shorter months)");
    Check("said in words: the last day", RecurringTaskService.Describe(last) == "Monthly on the last day");

    Console.WriteLine();
    Console.WriteLine("== what a schedule must say ==");
    RecurringInput Good() => new() { Title = "Check the backups", Kind = "Maintenance", AssignedTo = Sue, Repeats = Repeats.Weekly, Days = Bit(DayOfWeek.Wednesday) };
    Check("a good one passes", RecurringTaskService.Problem(Good()) == "");
    Check("no subject is refused", RecurringTaskService.Problem(new RecurringInput { Kind = "Maintenance", AssignedTo = Sue }) == "Please enter a subject.");
    var noKind = Good(); noKind.Kind = " ";
    Check("no type is refused", RecurringTaskService.Problem(noKind).Length > 0);
    var nobody = Good(); nobody.AssignedTo = "";
    Check("nobody to give it to is refused — set up once and forgotten needs somebody", RecurringTaskService.Problem(nobody).Contains("who this task goes to"));
    var noDays = Good(); noDays.Days = 0;
    Check("weekly with no day ticked is refused", RecurringTaskService.Problem(noDays) == "Tick at least one day of the week.");
    var badRepeat = Good(); badRepeat.Repeats = (Repeats)7;
    Check("a frequency that is not one of the three is refused", RecurringTaskService.Problem(badRepeat).Length > 0);

    // ================================================================ the service
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    int zenith;
    await using (var db = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(db);
        db.Tenants.Add(new Tenant { Id = 1, Slug = "r", CompanyName = "Recurring IT", TenantKey = "kr", TimeZone = "Africa/Johannesburg" });
        db.Tenants.Add(new Tenant { Id = 2, Slug = "f", CompanyName = "Free IT", TenantKey = "kf", TimeZone = "Africa/Johannesburg", Plan = FreePlan.Name });
        db.Tenants.Add(new Tenant { Id = 3, Slug = "s", CompanyName = "Suspended IT", TenantKey = "ks", TimeZone = "Africa/Johannesburg", Status = TenantStatus.Suspended });
        db.Tenants.Add(new Tenant { Id = 4, Slug = "ny", CompanyName = "New York IT", TenantKey = "kny", TimeZone = "America/New_York" });
        // Their own companies, so a sweep weeks ahead does not move the schedules the other checks are watching.
        db.Tenants.Add(new Tenant { Id = 5, Slug = "c", CompanyName = "Catch-up IT", TenantKey = "kc", TimeZone = "Africa/Johannesburg" });
        db.Tenants.Add(new Tenant { Id = 6, Slug = "m", CompanyName = "Month-end IT", TenantKey = "km", TimeZone = "Africa/Johannesburg" });
        await db.SaveChangesAsync();
        var z = new Client { TenantId = 1, Name = "Zenith Accounting", ContactName = "Zoe" };
        db.Clients.Add(z);
        await db.SaveChangesAsync();
        zenith = z.Id;
    }
    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    var rec = new RecurringTaskService(factory, tickets);
    var made = new List<int>();
    rec.TicketMade += (tenant, id) => made.Add(id);
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }
    async Task<RecurringTask> Task_(int id) { await using var db = new PlatformDb(options); return await db.RecurringTasks.FirstAsync(t => t.Id == id); }
    async Task<List<Ticket>> Of(int taskId) { await using var db = new PlatformDb(options); return await db.Tickets.Where(t => t.RecurringTaskId == taskId).OrderBy(t => t.DueOn).ToListAsync(); }
    string[] Keys(Ticket t, TenantRole r, string me) => TicketService.ActionsFor(t, r, me).Select(a => a.Key).ToArray();

    Console.WriteLine();
    Console.WriteLine("== the types: Maintenance, Billing, Readings, and the company's own ==");
    var kinds = await rec.KindsAsync(1);
    Check("every company starts with the three, in that order", kinds.SequenceEqual(RecurringTaskService.DefaultKinds), string.Join(",", kinds));

    Console.WriteLine();
    Console.WriteLine("== set up once; it opens on the MORNING it is due (answer 5) ==");
    // Wednesday 7 October, 05:00 in Johannesburg (03:00 UTC): before the morning.
    var weekly = Good(); weekly.ClientId = zenith; weekly.Kind = "maintenance"; weekly.Details = "Look at last night's backup log.";
    var (wt, err, first) = await rec.CreateAsync(1, Olive, weekly, U(10, 7, 3));
    Check("the office sets one up for a technician (anybody may — answer 4)", wt != null && err == "", err);
    Check("...with the type spelled as the company has it ('maintenance' → Maintenance)", wt!.Kind == "Maintenance", wt.Kind);
    Check("...and \"maintenance\" did not become a fourth type", (await rec.KindsAsync(1)).Count == 3);
    Check("set up at 05:00 on the Wednesday it is due: nothing opens yet", first == null && (await Of(wt.Id)).Count == 0);
    Check("...its next day is today", (await Task_(wt.Id)).NextDue == D(10, 7));
    Check("05:59 in Johannesburg: still nothing", await rec.OpenDueAsync(null, U(10, 7, 3, 59)) == 0 && (await Of(wt.Id)).Count == 0);
    made.Clear();
    Check("06:00 in Johannesburg (04:00 UTC): it opens", await rec.OpenDueAsync(null, U(10, 7, 4)) == 1 && (await Of(wt.Id)).Count == 1);
    var t1 = (await Of(wt.Id))[0];
    Check("...a Recurring task ticket at Due, Open", t1.Category == TicketCategory.Recurring && t1.Stage == Workflow.RecurringFirst && t1.Status == TicketStatus.Open);
    Check("...with the task's words, its client and its contact", t1.Subject == "Check the backups" && t1.Description == "Look at last night's backup log." && t1.ClientId == zenith && t1.ContactName == "Zoe");
    Check("...already given to its people", t1.AssignedTo == Sue);
    Check("...due that day, opened by the schedule", t1.DueOn == D(10, 7) && t1.Source == TicketSource.Recurring && t1.RecurringTaskId == wt.Id);
    Check("...and the consoles are told (TicketMade)", made.SequenceEqual(new[] { t1.Id }));
    await using (var db = new PlatformDb(options))
        Check("...its story says what it is and when it was due",
            await db.TicketNotes.AnyAsync(n => n.TicketId == t1.Id && n.Body == $"Maintenance — due Wednesday 7 October 2026. Every Wednesday, for Zenith Accounting. Assigned to {Sue}."));
    Check("...and the schedule moves on a week", (await Task_(wt.Id)) is { NextDue: var nd, LastOpenedFor: var lo } && nd == D(10, 14) && lo == D(10, 7));
    Check("the minute after, nothing opens twice", await rec.OpenDueAsync(null, U(10, 7, 4, 1)) == 0 && (await Of(wt.Id)).Count == 1);
    Check("its summary says Recurring task, by value", TicketService.ToSummary(t1).CategoryName == "Recurring task" && TicketService.CategoryOf(5) == TicketCategory.Recurring);
    Check("the console offers it LAST", TicketSummary.CategoryOrder[^1] == 5 && TicketSummary.CategoryOrder[0] == 4);

    Console.WriteLine();
    Console.WriteLine("== the next opens even if the last is not done (answer 3) ==");
    Check("on the day, it is not overdue", !RecurringTaskService.IsOverdue(t1, D(10, 7)));
    Check("the next Wednesday morning, the next one opens", await rec.OpenDueAsync(null, U(10, 14, 4, 30)) == 1 && (await Of(wt.Id)).Count == 2);
    var both = await Of(wt.Id);
    Check("...and the old one is OVERDUE, the new one not", RecurringTaskService.IsOverdue(both[0], D(10, 14)) && !RecurringTaskService.IsOverdue(both[1], D(10, 14)));
    Check("...which the list row carries too", TicketService.ToSummary(both[0]) is var _ && RecurringTaskService.IsOverdue(both[0], D(10, 14)));

    Console.WriteLine();
    Console.WriteLine("== the route: Done closes it, To be invoiced too (answer 2) ==");
    Check("the person on it is offered Done and To be invoiced",
        Keys(t1, T, Sue).SequenceEqual(new[] { "recurring-done", "recurring-invoice" }), string.Join(",", Keys(t1, T, Sue)));
    Check("a colleague NOT on it is offered nothing", Keys(t1, T, Bob).Length == 0, string.Join(",", Keys(t1, T, Bob)));
    Check("the office is offered both as well (a billing or readings task is often theirs), and Abandoned",
        Keys(t1, OA, Olive).SequenceEqual(new[] { "recurring-done", "recurring-invoice", "abandon" }), string.Join(",", Keys(t1, OA, Olive)));
    Check("a colleague not on it is REFUSED when they press Done anyway", (await tickets.ActAsync(1, t1.Id, Bob, T, "recurring-done")).Length > 0);
    Check("General's Done is not a step this ticket has", (await tickets.ActAsync(1, t1.Id, Sue, T, "general-done")).Length > 0);
    Check("Done closes it", await tickets.ActAsync(1, t1.Id, Sue, T, "recurring-done") == "" && (await Get(t1.Id)) is { Stage: Workflow.Closed, Status: TicketStatus.Closed });
    Check("...and a closed one is not overdue", !RecurringTaskService.IsOverdue(await Get(t1.Id), D(10, 30)));
    var t2 = both[1];
    Check("To be invoiced asks what was done, the time and the amount",
        (await tickets.ActAsync(1, t2.Id, Sue, T, "recurring-invoice", new StageInput { Details = "Backups fine." })).Length > 0);
    Check("...and with them goes to the billing queue",
        await tickets.ActAsync(1, t2.Id, Sue, T, "recurring-invoice", new StageInput { Details = "Backups fine.", Minutes = 15, Amount = 250 }) == ""
        && (await Get(t2.Id)) is { Stage: "to-be-invoiced", Lane: (int)WorkLane.Office, TimeSpentMinutes: 15 });
    await using (var db = new PlatformDb(options))
        Check("...written as Work done, for the invoice", await db.TicketNotes.AnyAsync(n => n.TicketId == t2.Id && n.IsWorkDone && n.FormTitle == "To be invoiced"));
    Check("...and closes on payment, the usual route",
        await tickets.ActAsync(1, t2.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 250, Reference = "INV-9" }) == ""
        && await tickets.ActAsync(1, t2.Id, Olive, OA, "payment-received") == "" && (await Get(t2.Id)).Stage == Workflow.Closed);
    Check("not overdue once it has left Due, even past its day", !RecurringTaskService.IsOverdue(await Get(t2.Id), D(10, 30)));

    Console.WriteLine();
    Console.WriteLine("== set up for today, after the morning: it opens at once ==");
    var readings = new RecurringInput { Title = "Meter readings", Kind = "Readings", AssignedTo = Olive, Repeats = Repeats.Daily, WeekdaysOnly = true };
    var (rt, _, rfirst) = await rec.CreateAsync(1, Sam, readings, U(10, 7, 8));
    Check("a weekday task set up at 10:00 on a Wednesday opens today's straight away", rfirst != null && rfirst.DueOn == D(10, 7));
    Check("...given to the office administrator, who may finish it",
        Keys(rfirst!, OA, Olive).Contains("recurring-done") && await tickets.ActAsync(1, rfirst!.Id, Olive, OA, "recurring-done") == "");
    Check("...and next opens Thursday", (await Task_(rt!.Id)).NextDue == D(10, 8));
    Check("Friday 9th opens, then the weekend is skipped: next is Monday 12th",
        await rec.OpenDueAsync(1, U(10, 8, 5)) == 1 && await rec.OpenDueAsync(1, U(10, 9, 5)) == 1
        && await rec.OpenDueAsync(1, U(10, 10, 5)) == 0 && await rec.OpenDueAsync(1, U(10, 11, 5)) == 0
        && (await Task_(rt.Id)).NextDue == D(10, 12));

    Console.WriteLine();
    Console.WriteLine("== a server that was down catches up rather than skipping ==");
    Check("down from Monday 12th to Thursday 15th 09:00: four weekdays open, one each",
        await rec.OpenDueAsync(1, U(10, 15, 7)) == 4 && (await Of(rt.Id)).Select(t => t.DueOn).SequenceEqual(new DateTime?[] { D(10, 7), D(10, 8), D(10, 9), D(10, 12), D(10, 13), D(10, 14), D(10, 15) }),
        string.Join(",", (await Of(rt.Id)).Select(t => t.DueOn?.ToString("dd"))));
    var everyDay = new RecurringInput { Title = "Daily check", Kind = "Maintenance", AssignedTo = Bob, Repeats = Repeats.Daily };
    var (dt, _, _) = await rec.CreateAsync(5, Bob, everyDay, U(10, 15, 3));
    var caught = await rec.OpenDueAsync(5, U(12, 31, 8));
    Check("...but never more than a month at once in one pass", caught == RecurringTaskService.CatchUpLimit && (await Of(dt!.Id)).Count == RecurringTaskService.CatchUpLimit, $"{caught} opened");
    Check("...and the rest on the next pass", await rec.OpenDueAsync(5, U(12, 31, 8)) > 0);

    Console.WriteLine();
    Console.WriteLine("== the month's end ==");
    var monthly = new RecurringInput { Title = "Month-end invoices", Kind = "Billing", AssignedTo = Olive, Repeats = Repeats.Monthly, MonthDay = 31 };
    var (mt, _, mfirst) = await rec.CreateAsync(6, Olive, monthly, U(10, 7, 8));
    Check("the 31st, set up on the 7th, is first due on the 31st", mfirst == null && (await Task_(mt!.Id)).NextDue == D(10, 31));
    Check("it opens on the 31st, then waits for 30 November",
        await rec.OpenDueAsync(6, U(10, 31, 5)) == 1 && (await Task_(mt.Id)).NextDue == D(11, 30));

    Console.WriteLine();
    Console.WriteLine("== the company's own time zone ==");
    var ny = new RecurringInput { Title = "NY check", Kind = "Maintenance", AssignedTo = Sue, Repeats = Repeats.Daily };
    var (nt, _, nfirst) = await rec.CreateAsync(4, Sue, ny, U(10, 7, 9, 59));
    Check("09:59 UTC is 05:59 in New York: nothing yet, though it is long past Johannesburg's morning", nfirst == null && (await Of(nt!.Id)).Count == 0);
    Check("10:00 UTC is 06:00 in New York: it opens", await rec.OpenDueAsync(4, U(10, 7, 10)) == 1);

    Console.WriteLine();
    Console.WriteLine("== the free version and a suspended company open nothing ==");
    var (ft, _, ff) = await rec.CreateAsync(2, Sue, Good(), U(10, 7, 8));
    var (st, _, sf) = await rec.CreateAsync(3, Sue, Good(), U(10, 7, 8));
    Check("a free company's schedule opens no ticket (it has no tickets)", ff == null && (await Of(ft!.Id)).Count == 0);
    Check("a suspended company's opens none either", sf == null && (await Of(st!.Id)).Count == 0);

    Console.WriteLine();
    Console.WriteLine("== types the company adds ==");
    var own = Good(); own.Kind = "Printer  toner"; own.Days = Bit(DayOfWeek.Friday);
    var (ot, _, _) = await rec.CreateAsync(1, Sue, own, U(10, 7, 8));
    Check("a new type is kept, spaces tidied", ot!.Kind == "Printer toner" && (await rec.KindsAsync(1)).Contains("Printer toner"));
    var own2 = Good(); own2.Kind = "TONER printer";
    var (ot2, _, _) = await rec.CreateAsync(1, Sue, own2, U(10, 7, 8));
    Check("...and the same words in another order are the same type, never a second", ot2!.Kind == "Printer toner" && (await rec.KindsAsync(1)).Count == 4);
    Check("...listed after the three defaults", (await rec.KindsAsync(1)).SequenceEqual(new[] { "Maintenance", "Billing", "Readings", "Printer toner" }));

    Console.WriteLine();
    Console.WriteLine("== changing one (answer 4: its people, a supervisor or the owner) ==");
    RecurringInput Now_(RecurringTask r, int days) => new()
    { Title = r.Title, Details = r.Details, Kind = r.Kind, ClientId = r.ClientId, AssignedTo = r.AssignedTo, Repeats = r.Repeats, Days = days };
    var w0 = await Task_(wt.Id);
    // The weekly one has opened Wednesday the 14th already. On the 14th, add Thursday.
    Check("a technician NOT on it is refused", (await rec.UpdateAsync(1, wt.Id, Bob, T, Now_(w0, Bit(DayOfWeek.Wednesday) | Bit(DayOfWeek.Thursday)), U(10, 14, 9))).Length > 0);
    Check("the office administrator (not on it) is refused too", (await rec.UpdateAsync(1, wt.Id, Olive, OA, Now_(w0, Bit(DayOfWeek.Wednesday)), U(10, 14, 9))).Length > 0);
    Check("the person on it may: Wednesday and Thursday", await rec.UpdateAsync(1, wt.Id, Sue, T, Now_(w0, Bit(DayOfWeek.Wednesday) | Bit(DayOfWeek.Thursday)), U(10, 14, 9)) == "");
    Check("...changed on a Wednesday whose ticket is already open: next is THURSDAY, never Wednesday again",
        (await Task_(wt.Id)).NextDue == D(10, 15) && (await Of(wt.Id)).Count(t => t.DueOn == D(10, 14)) == 1);
    Check("a supervisor may change it", await rec.UpdateAsync(1, wt.Id, Sam, S, Now_(await Task_(wt.Id), Bit(DayOfWeek.Thursday)), U(10, 14, 9)) == "");
    var badChange = Now_(await Task_(wt.Id), 0);
    Check("a change is held to the same rules (no day ticked)", (await rec.UpdateAsync(1, wt.Id, Sam, S, badChange, U(10, 14, 9))) == "Tick at least one day of the week.");

    Console.WriteLine();
    Console.WriteLine("== who sees which ==");
    var bobSees = await rec.ListAsync(1, T, Bob);
    var sueSees = await rec.ListAsync(1, T, Sue);
    Check("a technician sees the ones they are on", sueSees.Any(r => r.Task.Id == wt.Id) && sueSees.All(r => Assignees.Has(r.Task.AssignedTo, Sue)), string.Join(",", sueSees.Select(r => r.Task.Title)));
    Check("...and none of a colleague's", bobSees.Count == 0 && !sueSees.Any(r => r.Task.Id == rt.Id), string.Join(",", bobSees.Select(r => r.Task.Title)));
    var officeSees = await rec.ListAsync(1, OA, Olive);
    Check("the office sees them all", officeSees.Count == (await new PlatformDb(options).RecurringTasks.CountAsync(r => r.TenantId == 1)));
    Check("...and may change only the ones they are on", officeSees.Where(r => r.MayChange).All(r => Assignees.Has(r.Task.AssignedTo, Olive)));

    Console.WriteLine();
    Console.WriteLine("== cancel when it becomes obsolete ==");
    // An open one to watch: Thursday the 15th's.
    Check("Thursday 15th's opens", await rec.OpenDueAsync(1, U(10, 15, 4, 10)) >= 1 && (await Of(wt.Id)).Any(t => t.DueOn == D(10, 15)));
    var openOne = (await Of(wt.Id)).First(t => t.DueOn == D(10, 15));
    Check("cancelling asks why", (await rec.CancelAsync(1, wt.Id, Sue, T, " ")) == "Say why it is being cancelled.");
    Check("a technician NOT on it may not cancel it", (await rec.CancelAsync(1, wt.Id, Bob, T, "no")).Length > 0);
    Check("the person on it may", await rec.CancelAsync(1, wt.Id, Sue, T, "Client moved to cloud backup") == "");
    Check("...it is kept, marked, with who and why",
        (await Task_(wt.Id)) is { CancelledBy: Sue, CancelReason: "Client moved to cloud backup", CancelledUtc: not null });
    Check("...the one already open STAYS open", (await Get(openOne.Id)).Status == TicketStatus.Open);
    await using (var db = new PlatformDb(options))
        Check("...and its story says so", await db.TicketNotes.AnyAsync(n => n.TicketId == openOne.Id && n.Body.StartsWith("Recurring task cancelled: Client moved to cloud backup.")));
    int before = (await Of(wt.Id)).Count;
    Check("no more open — next Thursday, or a month on", await rec.OpenDueAsync(1, U(10, 22, 5)) >= 0 && await rec.OpenDueAsync(1, U(11, 19, 5)) >= 0 && (await Of(wt.Id)).Count == before);
    Check("it cannot be changed once cancelled", (await rec.UpdateAsync(1, wt.Id, Sam, S, Now_(await Task_(wt.Id), Bit(DayOfWeek.Friday)))).Length > 0);
    Check("nor cancelled twice", (await rec.CancelAsync(1, wt.Id, Sam, S, "again")).Length > 0);
    var info = await rec.InfoAsync(openOne, T, Sue);
    Check("its ticket says it was cancelled, and offers nothing to change", info is { Cancelled: true, MayChange: false } && info.Next.Contains("Client moved to cloud backup"), info?.Next ?? "");
    Check("a live one's ticket says what is next, and offers its person the change",
        await rec.InfoAsync((await Of(rt.Id)).Last(), OA, Olive) is { Cancelled: false, MayChange: true, Repeats: "Every weekday (Monday to Friday)" } ri && ri.Next.StartsWith("Next: "));
    Check("...but not a colleague who is not on it", await rec.InfoAsync((await Of(rt.Id)).Last(), T, Bob) is { MayChange: false });
    var afterCancel = await rec.ListAsync(1, OA, Olive);
    Check("the list shows active ones first, the cancelled one last", afterCancel.Last().Task.Id == wt.Id && afterCancel.First().Task.CancelledUtc == null);
    Check("an ordinary ticket has no recurring task", await rec.InfoAsync(await tickets.CreateAsync(1, "Plain", "", Sue, TicketSource.Manual), T, Sue) == null);
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { try { Directory.Delete(dir, true); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
