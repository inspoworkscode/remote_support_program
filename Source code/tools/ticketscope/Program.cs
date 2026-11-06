// =====================================================================================
// ticketscope - proves stage 3.6.4: the roles mean something, and they mean it in the
// SERVICE rather than in a Razor page.
//
// The distinction is the whole point of this harness. Hiding a dropdown from the office
// administrator is a courtesy to the person using the page; the rule is whatever runs when
// somebody posts the form anyway - by keeping a stale tab open, by using the console
// instead of the browser, or by typing a URL. So every check here goes through
// TicketService and Permissions, never through a page. If a check here passes and the page
// still shows the control, that is a cosmetic bug. If a check here fails, the rule does not
// exist however the page looks.
//
// Five claims, from docs/NEXT-SESSION-DASHBOARD.md:
//
//   a technician cannot read a COLLEAGUE'S ticket through the API, not merely through the page
//   a technician CAN see an unassigned one and claim it
//   a technician cannot claim one to somebody else
//   Billing can open, assign, reassign and note, and cannot change status or priority
//   Admin can do all of it
//
//   dotnet run -c Release --project tools/ticketscope/ticketscope.csproj
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

const string Sue = "Sue Jones", Bob = "Bob Smith", Owner = "The Owner", Office = "The Office";

var dir = Path.Combine(Path.GetTempPath(), "ticketscope-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var dbPath = Path.Combine(dir, "platform.db");

Console.WriteLine("ticketscope - stage 3.6.4, roles that are enforced rather than hidden");
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

    // Since 2026-09-27 every category has a route, and a ticket on a route moves by its buttons
    // only. What this harness proves is who may change a STATUS and an assignment — a rule that
    // still stands for any ticket without a stage (one written before the routes, which the
    // migration left alone). So these are made stage-less on purpose, the one kind the status
    // dropdown still moves; the routes are workflowcheck's and calloutcheck's to prove.
    async Task<int> Make(string subject, string assignedTo)
    {
        var t = await tickets.CreateAsync(1, subject, "", Owner, TicketSource.Manual, assignedTo: assignedTo);
        await using var db = new PlatformDb(options);
        await db.Database.ExecuteSqlRawAsync(HarnessDb.Portable("UPDATE Tickets SET Stage = '', Lane = 0 WHERE Id = {0}"), t.Id);
        return t.Id;
    }

    int sues = await Make("Sue's job", Sue);
    int bobs = await Make("Bob's job", Bob);
    int pool = await Make("Nobody's job", "");

    Console.WriteLine("== what a technician may reach ==");

    async Task<bool> CanSee(TenantRole role, string me, int ticketId)
    {
        await using var db = new PlatformDb(options);
        return await TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == 1), role, me).AnyAsync(t => t.Id == ticketId);
    }

    Check("a technician reaches their own ticket", await CanSee(TenantRole.Technician, Sue, sues));
    Check("a technician reaches an unassigned ticket", await CanSee(TenantRole.Technician, Sue, pool));
    Check("a technician CANNOT reach a colleague's ticket through the API",
        !await CanSee(TenantRole.Technician, Sue, bobs));
    Check("the owner reaches a technician's ticket", await CanSee(TenantRole.Admin, Owner, bobs));
    Check("the office administrator reaches a technician's ticket", await CanSee(TenantRole.Billing, Office, bobs));

    // The list is the other way in, and it must be scoped the same way.
    var sueList = (await tickets.ListAsync(1, "all", "", Sue, TenantRole.Technician)).Select(t => t.Subject).ToList();
    Check("asking the LIST for everything still excludes a colleague's",
        !sueList.Contains("Bob's job") && sueList.Contains("Sue's job") && sueList.Contains("Nobody's job"),
        string.Join(", ", sueList));

    Console.WriteLine();
    Console.WriteLine("== status is the technician's, assignment is the office's ==");

    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }

    // The office administrator: may note and assign, may not declare a machine fixed.
    var (_, officeStatus) = await tickets.UpdateAsync(1, sues, Office, TenantRole.Billing,
        TicketStatus.Closed, TicketPriority.Urgent, null, null);
    Check("the office administrator cannot change a status", (await Get(sues)).Status != TicketStatus.Closed,
        (await Get(sues)).Status.ToString());
    Check("the office administrator cannot change a priority", (await Get(sues)).Priority != TicketPriority.Urgent);
    Check("...and is told nothing changed rather than that it did", officeStatus.Count == 0, string.Join(", ", officeStatus));

    var (_, officeAssign) = await tickets.UpdateAsync(1, sues, Office, TenantRole.Billing, null, null, Bob, null);
    Check("the office administrator CAN reassign a ticket already with somebody",
        (await Get(sues)).AssignedTo == Bob, (await Get(sues)).AssignedTo);
    Check("...and the reassignment is written into the history", officeAssign.Count == 1, string.Join(", ", officeAssign));
    await tickets.UpdateAsync(1, sues, Office, TenantRole.Billing, null, null, Sue, null);   // put it back

    await tickets.AddNoteAsync(1, sues, Office, "Any update on this one?", false);
    await using (var db = new PlatformDb(options))
        Check("the office administrator can ask for an update",
            await db.TicketNotes.AnyAsync(n => n.TicketId == sues && n.Author == Office));

    // The technician: may move their own status, may not hand work to a colleague.
    var (_, techStatus) = await tickets.UpdateAsync(1, sues, Sue, TenantRole.Technician,
        TicketStatus.InProgress, null, null, null);
    Check("a technician can move their own ticket's status",
        (await Get(sues)).Status == TicketStatus.InProgress && techStatus.Count == 1);

    // Changed 2026-09-24, by Clint: "if that technician can't do it, they have the ability to
    // reassign to someone else." Their OWN work only — taking a colleague's is still refused.
    await tickets.UpdateAsync(1, sues, Sue, TenantRole.Technician, null, null, Bob, null);
    Check("a technician CAN pass their own ticket to a colleague", (await Get(sues)).AssignedTo == Bob,
        (await Get(sues)).AssignedTo);
    await tickets.UpdateAsync(1, sues, Sue, TenantRole.Technician, null, null, Sue, null);
    Check("...but cannot take a colleague's ticket back off them", (await Get(sues)).AssignedTo == Bob,
        (await Get(sues)).AssignedTo);
    await tickets.UpdateAsync(1, sues, Office, TenantRole.Billing, null, null, Sue, null);   // put it back

    Console.WriteLine();
    Console.WriteLine("== the pool ==");

    await tickets.UpdateAsync(1, pool, Sue, TenantRole.Technician, null, null, Sue, null);
    Check("a technician can claim an unassigned ticket for themselves", (await Get(pool)).AssignedTo == Sue);

    int pool2 = await Make("Another loose one", "");
    await tickets.UpdateAsync(1, pool2, Sue, TenantRole.Technician, null, null, Bob, null);
    Check("a technician cannot claim an unassigned ticket FOR SOMEBODY ELSE",
        (await Get(pool2)).AssignedTo == "", $"\"{(await Get(pool2)).AssignedTo}\"");

    // Once it is somebody's, a second technician cannot take it off them.
    await tickets.UpdateAsync(1, pool, Bob, TenantRole.Technician, null, null, Bob, null);
    Check("a technician cannot take a claimed ticket off a colleague", (await Get(pool)).AssignedTo == Sue);

    Console.WriteLine();
    Console.WriteLine("== the owner ==");

    await tickets.UpdateAsync(1, bobs, Owner, TenantRole.Admin, TicketStatus.Resolved, TicketPriority.High, Sue, null);
    var after = await Get(bobs);
    Check("the owner can change a status", after.Status == TicketStatus.Resolved);
    Check("the owner can change a priority", after.Priority == TicketPriority.High);
    Check("the owner can reassign", after.AssignedTo == Sue);

    Console.WriteLine();
    Console.WriteLine("== the questions themselves ==");

    Check("everybody may open a ticket",
        Permissions.CanOpenTicket(TenantRole.Technician) && Permissions.CanOpenTicket(TenantRole.Billing)
        && Permissions.CanOpenTicket(TenantRole.Admin));
    Check("everybody may comment",
        Permissions.CanComment(TenantRole.Technician) && Permissions.CanComment(TenantRole.Billing)
        && Permissions.CanComment(TenantRole.Admin));
    Check("only the owner and the office administrator see the whole board",
        Permissions.CanSeeAllTickets(TenantRole.Admin) && Permissions.CanSeeAllTickets(TenantRole.Billing)
        && !Permissions.CanSeeAllTickets(TenantRole.Technician));
    Check("only the owner and the office administrator see who has what",
        Permissions.CanSeeWorkload(TenantRole.Admin) && Permissions.CanSeeWorkload(TenantRole.Billing)
        && !Permissions.CanSeeWorkload(TenantRole.Technician));
    Check("the office administrator is told why they cannot move a status",
        Permissions.WhyNotStatus(TenantRole.Billing).Length > 0
        && Permissions.WhyNotStatus(TenantRole.Admin).Length == 0);

    // The legacy overload is what every existing caller uses. It must stay wide open, or session
    // and call handling - which act as the SERVER and not as a person - would quietly stop working.
    int server = await Make("Opened by the server", "");
    await tickets.UpdateAsync(1, server, "system", TicketStatus.InProgress, null, Sue, null);
    Check("the server's own updates are not role-limited",
        (await Get(server)).Status == TicketStatus.InProgress && (await Get(server)).AssignedTo == Sue);
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
