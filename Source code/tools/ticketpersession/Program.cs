// =====================================================================================
// ticketpersession - proves stage 3.6.6 and 3.6.7: every remote session gets its own ticket
// and its own number, and a ticket can be part of another one.
//
// This is the stage that UNDOES behaviour that was deliberate. ActiveForDeviceAsync used to
// reuse a device's newest open ticket with no time limit, and OpenTicketForCallAsync added a
// note instead of opening a second ticket. Both were right when a ticket meant "this machine
// has an open problem". They are wrong now that a ticket means "this was a piece of work
// somebody did", and the thing most likely to happen to this code is somebody quietly
// widening that window back because a hundred tickets a day looks like a lot. It is supposed
// to look like a lot. That is what a hundred sessions is.
//
// The window is faked by ageing rows rather than by waiting thirty minutes, which is the only
// honest way to test a rule about elapsed time in under a second.
//
//   dotnet run -c Release --project tools/ticketpersession/ticketpersession.csproj
// =====================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Tools;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

var dir = Path.Combine(Path.GetTempPath(), "tps-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);

Console.WriteLine("ticketpersession - stages 3.6.6 and 3.6.7");
Console.WriteLine();

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")}").Options;
    await using (var seed = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(seed);
        seed.Tenants.Add(new Tenant { Slug = "check", CompanyName = "Check IT", TenantKey = Guid.NewGuid().ToString("N") });
        await seed.SaveChangesAsync();
    }

    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);

    const string Sue = "Sue Jones", Device = "dev-1", Machine = "ZEN-SUE";

    async Task<Ticket> Session(string reason)
    {
        // Exactly what TenantHub.AttachSessionToTicketAsync does: reuse inside the window, a new
        // ticket outside it.
        var open = await tickets.ActiveForDeviceAsync(1, Device);
        if (open != null) return open;
        return await tickets.CreateAsync(1, reason, reason, Sue, TicketSource.Unattended,
            Device, Machine, "", TicketPriority.Normal, Sue, TicketCategory.RemoteSupport);
    }
    // Closing is by the route since 2026-09-27: the SLA way out, then the office closes it.
    async Task<string> Close(int id)
    {
        var why = await tickets.ActAsync(1, id, Sue, TenantRole.Technician, "remote-sla");
        return why.Length > 0 ? why : await tickets.ActAsync(1, id, "Olive Office", TenantRole.Billing, "close-dni");
    }

    /// Makes a ticket LOOK like it was opened this many minutes ago.
    async Task Age(int ticketId, int minutes)
    {
        await using var db = new PlatformDb(options);
        var t = await db.Tickets.FirstAsync(x => x.Id == ticketId);
        t.CreatedUtc = DateTime.UtcNow.AddMinutes(-minutes);
        await db.SaveChangesAsync();
    }

    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }

    Console.WriteLine("== one session, one ticket ==");

    Check("the reuse window is 30 minutes",
        TicketService.ReuseWindow == TimeSpan.FromMinutes(30), TicketService.ReuseWindow.ToString());

    var first = await Session("Sage will not open");
    var immediately = await Session("Sage will not open");
    Check("two sessions two minutes apart share one ticket", immediately.Id == first.Id, $"#{first.Number} / #{immediately.Number}");

    await Age(first.Id, 31);
    var later = await Session("Printer is offline");
    Check("two sessions 31 minutes apart produce TWO ticket numbers",
        later.Id != first.Id && later.Number != first.Number, $"#{first.Number} then #{later.Number}");

    // The property this stage exists for.
    await Age(later.Id, 31);
    var third = await Session("Backup failed overnight");
    await Age(third.Id, 31);
    var fourth = await Session("Outlook keeps asking for a password");
    Check("four sessions across a day are four tickets",
        new[] { first.Id, later.Id, third.Id, fourth.Id }.Distinct().Count() == 4);
    Check("...with four different numbers",
        new[] { first.Number, later.Number, third.Number, fourth.Number }.Distinct().Count() == 4,
        string.Join(", ", new[] { first.Number, later.Number, third.Number, fourth.Number }.Select(n => "#" + n)));

    // A ticket somebody has DECIDED on is never reused, however recent (2026-09-27): sent to be
    // invoiced, or not, it is finished work, and a new session is new work.
    await tickets.ActAsync(1, fourth.Id, Sue, TenantRole.Technician, "remote-sla");
    var afterDecided = await Session("And now the screen flickers");
    Check("a ticket already decided on is never reused, however recent", afterDecided.Id != fourth.Id,
        $"#{fourth.Number} is at {(await Get(fourth.Id)).Stage}");
    await tickets.ActAsync(1, fourth.Id, "Olive Office", TenantRole.Billing, "close-dni");
    var afterClose = await Session("And now the keyboard");
    Check("a closed ticket is never reused, however recent", afterClose.Id != fourth.Id);

    // A DIFFERENT machine never shares a ticket, whatever the timing.
    var otherMachine = await tickets.ActiveForDeviceAsync(1, "dev-2");
    Check("another machine's session does not join this one's ticket", otherMachine == null);

    // ---- the 30 minutes count from the end of the LAST SESSION (2026-10-09) ----
    // Clint: "Reconnecting within 30 minutes should automatically be part of the same ticket with no reason necessary."
    // They were counted from when the ticket was OPENED, so a reconnect five minutes after a two-hour session was a
    // new ticket. The window moved; it did not widen.
    Console.WriteLine();
    Console.WriteLine("== thirty minutes from the end of the last session ==");
    const string Long = "dev-3";
    var longJob = await tickets.CreateAsync(1, "Server migration", "", Sue, TicketSource.Unattended, Long, "ZEN-SRV", "",
        TicketPriority.Normal, Sue, TicketCategory.RemoteSupport);
    await Age(longJob.Id, 150);
    async Task SessionOn(int ticketId, int startedMinsAgo, int? endedMinsAgo)
    {
        await using var db = new PlatformDb(options);
        db.Sessions.RemoveRange(db.Sessions.Where(s => s.TicketId == ticketId));
        db.Sessions.Add(new SessionLog { TenantId = 1, TicketId = ticketId, SessionId = Guid.NewGuid().ToString("N"), DeviceId = Long,
            StartedUtc = DateTime.UtcNow.AddMinutes(-startedMinsAgo), EndedUtc = endedMinsAgo is int e ? DateTime.UtcNow.AddMinutes(-e) : null });
        await db.SaveChangesAsync();
    }
    Check("opened 2½ hours ago with no session since: a new session is new work", await tickets.ActiveForDeviceAsync(1, Long) == null);
    await SessionOn(longJob.Id, 150, 5);
    Check("a two-and-a-half-hour session that ended 5 minutes ago: the reconnect carries on that ticket",
        (await tickets.ActiveForDeviceAsync(1, Long))?.Id == longJob.Id);
    await SessionOn(longJob.Id, 150, 29);
    Check("...ended 29 minutes ago: still the same ticket", (await tickets.ActiveForDeviceAsync(1, Long))?.Id == longJob.Id);
    await SessionOn(longJob.Id, 150, 31);
    Check("...ended 31 minutes ago: a new ticket — still thirty minutes, not wider", await tickets.ActiveForDeviceAsync(1, Long) == null);
    await SessionOn(longJob.Id, 150, null);
    Check("a session with no end recorded (the server stopped under it) counts from its START, never as still going",
        await tickets.ActiveForDeviceAsync(1, Long) == null);
    await SessionOn(longJob.Id, 10, null);
    Check("...so one that started 10 minutes ago and has no end still carries on", (await tickets.ActiveForDeviceAsync(1, Long))?.Id == longJob.Id);
    await SessionOn(longJob.Id, 150, 5);
    await tickets.ActAsync(1, longJob.Id, Sue, TenantRole.Technician, "remote-sla");
    Check("...and a ticket somebody has decided on is never carried on, however recent its session",
        await tickets.ActiveForDeviceAsync(1, Long) == null, (await Get(longJob.Id)).Stage);

    Console.WriteLine();
    Console.WriteLine("== the reason becomes the description ==");

    Check("the session's ticket carries the reason as its description",
        (await Get(later.Id)).Description == "Printer is offline", (await Get(later.Id)).Description);
    Check("...and as its subject, so the queue reads as work rather than as machine names",
        (await Get(later.Id)).Subject == "Printer is offline");
    Check("a ticket opened from an unattended session is marked as one",
        (await Get(later.Id)).Source == TicketSource.Unattended);

    // The console asks, and the server refuses without one. The floor matches the customer's own
    // dialog: three characters, enough to stop a keystroke dismissing the question.
    Check("the console's floor is three characters",
        RemoteSupport.Technician.ReasonDialog.MinLength == 3);

    Console.WriteLine();
    Console.WriteLine("== a ticket can be part of another ==");

    var parent = await Get(first.Id);
    var child = await Get(later.Id);

    Check("filing one ticket under another succeeds",
        (await tickets.SetParentAsync(1, child.Id, parent.Id, Sue)).Length == 0);
    Check("the child says whose job it is part of", (await Get(child.Id)).ParentTicketId == parent.Id);
    Check("the parent lists it", (await tickets.ChildrenAsync(1, parent.Id)).Any(c => c.Id == child.Id));
    Check("the child keeps its own number", (await Get(child.Id)).Number == child.Number);

    await using (var db = new PlatformDb(options))
        Check("both sides get a note saying so",
            await db.TicketNotes.AnyAsync(n => n.TicketId == child.Id && n.Body.Contains($"#{parent.Number}"))
            && await db.TicketNotes.AnyAsync(n => n.TicketId == parent.Id && n.Body.Contains($"#{child.Number}")));

    // One level, and nothing deeper. This is the rule that stops it becoming a tree.
    Check("a ticket cannot be its own parent",
        (await tickets.SetParentAsync(1, parent.Id, parent.Id, Sue)).Length > 0);
    var deeper = await tickets.SetParentAsync(1, third.Id, child.Id, Sue);
    Check("a ticket cannot be filed under one that is itself filed under something", deeper.Length > 0, deeper);
    var wouldBeGrandparent = await tickets.SetParentAsync(1, parent.Id, third.Id, Sue);
    Check("a ticket with children cannot become somebody's child", wouldBeGrandparent.Length > 0, wouldBeGrandparent);
    Check("...and the refusal changed nothing", (await Get(parent.Id)).ParentTicketId == null);

    Check("unfiling works", (await tickets.SetParentAsync(1, child.Id, null, Sue)).Length == 0);
    Check("...and leaves the child standing alone", (await Get(child.Id)).ParentTicketId == null);
    await tickets.SetParentAsync(1, child.Id, parent.Id, Sue);   // put it back

    Console.WriteLine();
    Console.WriteLine("== a job is not finished while part of it is running ==");

    // Asked of the ROUTE now (2026-09-27): the step that would close the parent is refused.
    var blocked = await Close(parent.Id);
    Check("a parent with an OPEN child cannot be closed",
        (await Get(parent.Id)).Status != TicketStatus.Closed, (await Get(parent.Id)).Stage);
    Check("...and says which one", blocked.Contains($"#{child.Number}"), blocked);

    Check("the child closes on its own", await Close(child.Id) == "" && (await Get(child.Id)).Status == TicketStatus.Closed);
    Check("once it is closed the parent closes too",
        await tickets.ActAsync(1, parent.Id, "Olive Office", TenantRole.Billing, "close-dni") == "" && (await Get(parent.Id)).Status == TicketStatus.Closed);

    // Closing a parent must NOT change its children — each is its own record of work done.
    var p2 = await tickets.CreateAsync(1, "Two day job", "", Sue, TicketSource.Manual, assignedTo: Sue);
    var c2 = await tickets.CreateAsync(1, "Day two", "", Sue, TicketSource.Manual, assignedTo: Sue);
    await tickets.SetParentAsync(1, c2.Id, p2.Id, Sue);
    await tickets.ActAsync(1, c2.Id, Sue, TenantRole.Technician, "general-done");
    var c2Closed = (await Get(c2.Id)).ClosedUtc;
    await tickets.ActAsync(1, p2.Id, Sue, TenantRole.Technician, "general-done");
    Check("closing a parent leaves its children as they were",
        (await Get(p2.Id)).Status == TicketStatus.Closed && (await Get(c2.Id)).ClosedUtc == c2Closed, (await Get(p2.Id)).Stage);
    Check("...and abandoning one is allowed with a child still open — walking away is not claiming it is done",
        await AbandonWithOpenChild());
    async Task<bool> AbandonWithOpenChild()
    {
        var p3 = await tickets.CreateAsync(1, "Given up", "", Sue, TicketSource.Manual, assignedTo: Sue);
        var c3 = await tickets.CreateAsync(1, "Still open", "", Sue, TicketSource.Manual, assignedTo: Sue);
        await tickets.SetParentAsync(1, c3.Id, p3.Id, Sue);
        return await tickets.ActAsync(1, p3.Id, "Olive Office", TenantRole.Billing, "abandon", new StageInput { Reason = "Customer gone." }) == "";
    }

    Console.WriteLine();
    Console.WriteLine("== the job's time ==");

    await using (var db = new PlatformDb(options))
    {
        var start = DateTime.UtcNow.AddHours(-3);
        db.Sessions.Add(new SessionLog { TenantId = 1, TicketId = p2.Id, SessionId = "s1", DeviceId = Device, StartedUtc = start, EndedUtc = start.AddMinutes(20) });
        db.Sessions.Add(new SessionLog { TenantId = 1, TicketId = c2.Id, SessionId = "s2", DeviceId = Device, StartedUtc = start.AddHours(1), EndedUtc = start.AddHours(1).AddMinutes(35) });
        await db.SaveChangesAsync();

        var ids = (await tickets.ChildrenAsync(1, p2.Id)).Select(c => c.Id).Append(p2.Id).ToList();
        var all = await db.Sessions.Where(s => s.TenantId == 1 && s.TicketId != null && ids.Contains(s.TicketId!.Value) && s.EndedUtc != null).ToListAsync();
        Check("the parent's total is its own time plus its children's",
            TicketService.TotalMinutes(all) == 55, $"{TicketService.TotalMinutes(all)} min");
    }
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

/// <summary>The context factory the services want, over one throwaway SQLite file.</summary>
file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
