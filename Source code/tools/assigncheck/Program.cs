// =====================================================================================
// assigncheck - proves that several people can be on one ticket (2026-09-28).
//
// Clint: "multiple people must be able to be assigned to the same ticket from the start or
// added at any stage as sometimes more than one person works on the same problem at the same
// time."
//
// The people on a ticket live in the column that always held the one name, joined by ", "
// (Services/Assignees.cs says why). So the thing most likely to be wrong is not the storing but
// every question "is this person on it?" - which used to be `AssignedTo == me` and is false for
// Jason on "Jason, Ryan". Every check goes through TicketService, Workflow and Permissions, never
// a page, and the visibility checks run as SQL against a real SQLite file, because a query that
// passes in memory and cannot be translated is exactly the fault this change could introduce.
//
//   dotnet run -c Release --project tools/assigncheck/assigncheck.csproj
// =====================================================================================

using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

const string Jason = "Jason", Ryan = "Ryan", Anthony = "Anthony", RyanSmith = "Ryan Smith",
             Chantel = "Chantel", Olive = "Olive Office";
const TenantRole T = TenantRole.Technician, S = TenantRole.Admin, OA = TenantRole.Billing;

var dir = Path.Combine(Path.GetTempPath(), "assigncheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var dbPath = Path.Combine(dir, "platform.db");

Console.WriteLine("assigncheck - several people on one ticket");
Console.WriteLine();

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath}").Options;
    await using (var seed = new PlatformDb(options)) await HarnessDb.MigrateAsync(seed);
    var factory = new Factory(options);
    var tickets = new TicketService(factory);
    await using (var seed = new PlatformDb(options))
    {
        seed.Tenants.Add(new Tenant { Slug = "check", CompanyName = "Check IT", TenantKey = Guid.NewGuid().ToString("N") });
        await seed.SaveChangesAsync();
    }

    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }
    async Task<bool> CanSee(TenantRole role, string me, int id)
    {
        await using var db = new PlatformDb(options);
        return await TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == 1), role, me).AnyAsync(t => t.Id == id);
    }
    async Task<string> LastSystemNote(int id)
    {
        await using var db = new PlatformDb(options);
        return await db.TicketNotes.Where(n => n.TicketId == id && n.IsSystem).OrderByDescending(n => n.Id).Select(n => n.Body).FirstOrDefaultAsync() ?? "";
    }
    // A stage-less ticket on the bench: the kind the plain update moves, so only the assignment rule is under test.
    async Task<int> Plain(string subject, string people)
    {
        var t = await tickets.CreateAsync(1, subject, "", Olive, TicketSource.Manual, assignedTo: people);
        await using var db = new PlatformDb(options);
        await db.Database.ExecuteSqlRawAsync(HarnessDb.Portable("UPDATE Tickets SET Stage = '', Lane = 0 WHERE Id = {0}"), t.Id);
        return t.Id;
    }
    async Task<string> Assign(int id, string by, TenantRole role, string people)
    {
        await tickets.UpdateAsync(1, id, by, role, null, null, people, null);
        return (await Get(id)).AssignedTo;
    }

    Console.WriteLine("== the list itself ==");
    Check("a posted list is tidied: spaces, repeats and blanks go", Assignees.Normalise(" Ryan,Jason , Ryan,, ") == "Ryan, Jason", Assignees.Normalise(" Ryan,Jason , Ryan,, "));
    Check("one name is stored exactly as it always was", Assignees.Normalise("Jason") == "Jason");
    Check("Jason is on \"Jason, Ryan\"", Assignees.Has("Jason, Ryan", Jason));
    Check("Ryan is NOT on \"Ryan Smith\" — a name inside a name does not count", !Assignees.Has(RyanSmith, Ryan));
    Check("nobody is on an empty list, and an empty name is on nothing", !Assignees.Has("", Jason) && !Assignees.Has("Jason", ""));
    Check("names read as a sentence", Assignees.Say(["Jason", "Ryan", "Anthony"]) == "Jason, Ryan and Anthony", Assignees.Say(["Jason", "Ryan", "Anthony"]));

    // Who has what (2026-10-21): Clint saw 44 open there against 29 on the Tickets page — the total added
    // up the people's lists, and a shared job is on each of them.
    Console.WriteLine();
    Console.WriteLine("== Who has what ==");
    var openNow = new List<Ticket>
    {
        new() { Id = 901, AssignedTo = "Jason, Ryan" }, new() { Id = 902, AssignedTo = "Jason" },
        new() { Id = 903, AssignedTo = "" }, new() { Id = 904, AssignedTo = "Gone Person" }
    };
    var board = RemoteSupport.Platform.Pages.Portal.WorkloadModel.Board(
        new() { (Jason, false), (Ryan, false), (Anthony, false) }, openNow, new() { "Jason, Ryan" });
    int Own(string who) => board.Loads.First(l => l.Name == who).Open.Count;
    var lists = board.Loads.Sum(l => l.Open.Count) + board.Pool.Count;
    Check("Open altogether counts each ticket once, as the Tickets page does", board.TotalOpen == 4,
        $"{board.TotalOpen}; the lists added up come to {lists}");
    Check("...while a shared job is still on each person's own list", Own(Jason) == 2 && Own(Ryan) == 1 && Own(Anthony) == 0,
        $"Jason {Own(Jason)}, Ryan {Own(Ryan)}, Anthony {Own(Anthony)}");
    Check("...and nothing open is left off the board: the pool, and somebody no longer listed",
        board.Pool.Count == 1 && board.Loads.Any(l => l.Name == "Gone Person" && l.Disabled && l.Open.Count == 1));
    Check("a shared job closed this month counts for both of them", board.Loads.First(l => l.Name == Jason).ClosedThisMonth == 1
        && board.Loads.First(l => l.Name == Ryan).ClosedThisMonth == 1);

    Console.WriteLine();
    Console.WriteLine("== from the start ==");
    var both = await tickets.CreateAsync(1, "Server down", "", Olive, TicketSource.Manual, assignedTo: "Jason,Ryan");
    Check("a ticket opened with two people has both", both.AssignedTo == "Jason, Ryan", both.AssignedTo);
    Check("a workshop booking with two technicians is accepted", TicketService.CheckBookIn(TicketCategory.Workshop, "Jason, Ryan") == "");
    Check("a workshop booking with nobody is still refused", TicketService.CheckBookIn(TicketCategory.Workshop, "") != "");

    Console.WriteLine();
    Console.WriteLine("== who sees it (as SQL) ==");
    int shared = await Plain("Shared job", "Jason, Ryan");
    int smiths = await Plain("Ryan Smith's job", RyanSmith);
    int pool = await Plain("Nobody's job", "");
    Check("Jason sees the shared ticket", await CanSee(T, Jason, shared));
    Check("Ryan sees the shared ticket", await CanSee(T, Ryan, shared));
    Check("Anthony, who is not on it, does not", !await CanSee(T, Anthony, shared));
    Check("Ryan does NOT see Ryan Smith's ticket", !await CanSee(T, Ryan, smiths));
    Check("a technician with no name sees the pool and nothing assigned",
        await CanSee(T, "", pool) && !await CanSee(T, "", shared) && !await CanSee(T, "", smiths));
    var ryansList = await tickets.ListAsync(1, "mine", "", Ryan, T);
    Check("\"My tickets\" for Ryan includes the one he shares", ryansList.Any(t => t.Id == shared), string.Join(", ", ryansList.Select(t => t.Subject)));
    Check("…and not Ryan Smith's", ryansList.All(t => t.Id != smiths));
    var poolList = await tickets.ListAsync(1, "unassigned", "", Jason, S);
    Check("a shared ticket is not in the unassigned pool", poolList.All(t => t.Id != shared) && poolList.Any(t => t.Id == pool));

    Console.WriteLine();
    Console.WriteLine("== added at any stage — by the people who hand work out ==");
    int j1 = await Plain("Office hands out", "");
    Check("the office administrator puts two people on it", await Assign(j1, Olive, OA, "Jason, Ryan") == "Jason, Ryan");
    Check("…and the story says who", (await LastSystemNote(j1)).Contains("added Jason and Ryan to the ticket"), await LastSystemNote(j1));
    Check("a supervisor adds a third", await Assign(j1, Chantel, S, "Jason, Ryan, Anthony") == "Jason, Ryan, Anthony");
    Check("a supervisor takes one off", await Assign(j1, Chantel, S, "Jason, Anthony") == "Jason, Anthony");
    Check("…and the story says who came off", (await LastSystemNote(j1)).Contains("took Ryan off the ticket"), await LastSystemNote(j1));
    int j2 = await Plain("One to one", Jason);
    Check("one person to one person keeps its old words", await Assign(j2, Olive, OA, Ryan) == Ryan && (await LastSystemNote(j2)) == "reassigned from Jason to Ryan", await LastSystemNote(j2));
    Check("posting the same list in another order changes nothing", await Assign(j1, Olive, OA, "Anthony, Jason") == "Jason, Anthony" && !(await LastSystemNote(j1)).Contains("Anthony, Jason"));

    Console.WriteLine();
    Console.WriteLine("== what a technician may do ==");
    int k = await Plain("Jason's job", Jason);
    Check("Anthony may JOIN a colleague's bench job — himself, taking nobody off", await Assign(k, Anthony, T, "Jason, Anthony") == "Jason, Anthony");
    int k2 = await Plain("Jason's other job", Jason);
    Check("Anthony may NOT put Ryan on a job he is not on", await Assign(k2, Anthony, T, "Jason, Ryan") == Jason);
    Check("Anthony may NOT swap himself in for Jason", await Assign(k2, Anthony, T, Anthony) == Jason);
    Check("Jason, on it, may add Ryan to help", await Assign(k2, Jason, T, "Jason, Ryan") == "Jason, Ryan");
    Check("Ryan may NOT take Jason off", await Assign(k2, Ryan, T, Ryan) == "Jason, Ryan");
    Check("Ryan may take HIMSELF off while Jason stays", await Assign(k2, Ryan, T, Jason) == Jason);
    Check("Jason, alone on it, may NOT leave it with nobody", await Assign(k2, Jason, T, "") == Jason);
    Check("Jason may pass it on in one go: Anthony on, himself off", await Assign(k2, Jason, T, Anthony) == Anthony);
    Check("a technician claiming from the pool still works", await Assign(pool, Ryan, T, Ryan) == Ryan);
    int office = await Plain("In the billing queue", "");
    await using (var db = new PlatformDb(options)) await db.Database.ExecuteSqlRawAsync(HarnessDb.Portable("UPDATE Tickets SET Lane = 1 WHERE Id = {0}"), office);
    Check("nobody may join a job in the billing queue", await Assign(office, Anthony, T, Anthony) == "");
    Check("the server's own update is not role-limited (a session adding its technician)",
        (await tickets.UpdateAsync(1, k, "system", null, null, "Jason, Anthony, Ryan", null)).ticket?.AssignedTo == "Jason, Anthony, Ryan");

    Console.WriteLine();
    Console.WriteLine("== the workshop route, two technicians on the bench ==");
    var job = await tickets.CreateAsync(1, "Laptop", "", Olive, TicketSource.Manual, assignedTo: "Jason, Ryan", category: TicketCategory.Workshop);
    Check("Ryan, the second name, may press the bench buttons", Workflow.MayAct(WorkLane.Bench, job, T, Ryan));
    Check("Anthony, not on it, may not", !Workflow.MayAct(WorkLane.Bench, job, T, Anthony));
    Check("Ryan's Inspection findings goes through", await tickets.ActAsync(1, job.Id, Ryan, T, "inspection", new StageInput { Details = "Hinge cracked." }) == "");
    Check("Jason's Repairable goes through", await tickets.ActAsync(1, job.Id, Jason, T, "repairable") == "");
    Check("Anthony pressing anyway is REFUSED", (await tickets.ActAsync(1, job.Id, Anthony, T, "do-not-invoice", new StageInput { Reason = "x" })).Length > 0);
    Check("Ryan asks not to invoice", await tickets.ActAsync(1, job.Id, Ryan, T, "do-not-invoice", new StageInput { Reason = "Warranty." }) == "");
    var waiting = await Get(job.Id);
    Check("with the supervisors it belongs to nobody in particular", waiting.AssignedTo == "", waiting.AssignedTo);
    await using (var db = new PlatformDb(options))
    {
        var said = await db.TicketNotes.Where(n => n.TicketId == job.Id).OrderByDescending(n => n.Id).Select(n => n.Body).FirstAsync();
        Check("…and the story says WHO was taken off, and where it went", said.Contains("Jason and Ryan taken off — with the supervisors for approval."), said);
    }
    Check("…and BOTH are recorded as having done the work", waiting.WorkedBy == "Jason, Ryan", waiting.WorkedBy);
    Check("both can still follow it, and Anthony cannot",
        await CanSee(T, Jason, job.Id) && await CanSee(T, Ryan, job.Id) && !await CanSee(T, Anthony, job.Id));
    Check("either may mark the machine collected", Workflow.MayMarkCollected(waiting, T, Jason) && Workflow.MayMarkCollected(waiting, T, Ryan) && !Workflow.MayMarkCollected(waiting, T, Anthony));
    Check("a supervisor declines it", await tickets.ActAsync(1, job.Id, Chantel, S, "decline-dni", new StageInput { Reason = "Charge the inspection." }) == "");
    var back = await Get(job.Id);
    Check("it comes back to the bench with BOTH of them", back.AssignedTo == "Jason, Ryan" && back.Stage == Workflow.RepairableStage, $"{back.AssignedTo} at {back.Stage}");
    await using (var db = new PlatformDb(options))
    {
        var said = await db.TicketNotes.Where(n => n.TicketId == job.Id).OrderByDescending(n => n.Id).Select(n => n.Body).FirstAsync();
        Check("the story says it is back with both, as a sentence", said.Contains("Back with Jason and Ryan."), said);
    }

    // Clint's ticket #6 (2026-10-02): To be invoiced took Jason off and said only "In the billing
    // queue", so the office putting him back read "reassigned from unassigned to Jason" — true, and
    // taken for a fault, because nothing had said he was taken off.
    Console.WriteLine();
    Console.WriteLine("== the billing queue says who it took off, and who was put back ==");
    var rs = await tickets.CreateAsync(1, "Virus suspected", "", Olive, TicketSource.Manual, assignedTo: Jason, category: TicketCategory.RemoteSupport);
    Check("Jason's To be invoiced goes through",
        await tickets.ActAsync(1, rs.Id, Jason, T, "remote-invoice", new StageInput { Details = "Removed it.", Amount = 300 }) == "");
    await using (var db = new PlatformDb(options))
    {
        var said = await db.TicketNotes.Where(n => n.TicketId == rs.Id).OrderByDescending(n => n.Id).Select(n => n.Body).FirstAsync();
        Check("the step says \"Jason taken off — in the billing queue.\"", said.EndsWith("Jason taken off — in the billing queue."), said);
    }
    Check("Chantel puts Jason back on", await Assign(rs.Id, Chantel, S, Jason) == Jason);
    Check("…and it reads \"assigned to Jason (was in the billing queue)\", never \"from unassigned\"",
        await LastSystemNote(rs.Id) == "assigned to Jason (was in the billing queue)", await LastSystemNote(rs.Id));
    Check("Client invoiced takes him off again and says so",
        await tickets.ActAsync(1, rs.Id, Chantel, S, "client-invoiced", new StageInput { Amount = 300, Reference = "1848" }) == ""
        && (await Get(rs.Id)).AssignedTo == "");
    await using (var db = new PlatformDb(options))
    {
        var said = await db.TicketNotes.Where(n => n.TicketId == rs.Id).OrderByDescending(n => n.Id).Select(n => n.Body).FirstAsync();
        Check("…\"Jason taken off — in the billing queue.\"", said.EndsWith("Jason taken off — in the billing queue."), said);
    }
    Check("a bench job with nobody on it keeps its old words",
        await Assign(await Plain("Pool job", ""), Olive, OA, Ryan) == Ryan, "");
    Check("…\"reassigned from unassigned to Ryan\"", (await LastSystemNote((await tickets.ListAsync(1, "all", "Pool job", Olive, OA))[0].Id)) == "reassigned from unassigned to Ryan");

    Check("the console is told who may assign and who may join",
        Permissions.CanAssignToAnyone(OA) && Permissions.CanAssignToAnyone(S) && !Permissions.CanAssignToAnyone(T) && Permissions.CanClaim(T) && Permissions.CanPassOn(T));
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

/// <summary>The context factory TicketService wants, over one throwaway SQLite file.</summary>
file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
