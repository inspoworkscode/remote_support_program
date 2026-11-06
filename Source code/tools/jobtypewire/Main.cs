// =====================================================================================
// jobtypewire - proves Job types against a real database (HANDOVER 166, brief docs/NEXT-SESSION-JOBTYPES.md):
//
//   Clint: a company chooses its categories, and switches off stages "without breaking the flow".
//
// In-process, against the real TenantService, TicketService, TicketPhotoService and RecurringTaskService on a throwaway
// database — SQLite, or PostgreSQL with RSP_TEST_DB=postgres. jobtypecheck proves the routes as a graph; this proves the
// service obeys them: who may save, what is stored, every new ticket's own copy, a job opened before keeping its route,
// a switched-off step refused (not only hidden), the photo gates, the categories asked live, and recurring tasks pausing
// without a pile of tickets when they come back.
//
//   dotnet run -c Debug --project tools/jobtypewire/jobtypewire.csproj
// =====================================================================================

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

var dataDir = Path.Combine(Path.GetTempPath(), "rs-jobtypewire-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
Console.WriteLine($"jobtypewire — Job types against a real database ({HarnessDb.Name})");

var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
var factory = new Factory(options);
var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = "http://127.0.0.1:1", ["Platform:Region"] = "ZA" }).Build();
var tenants = new TenantService(factory, cfg, dataDir, DataProtectionProvider.Create("jobtypewire"));
var (tenant, _) = await tenants.CreateAsync("Job Co", "job-co", 7);
int tid = tenant!.Id;
const string Pw = "job-types-2026";
await tenants.AddTechnicianAsync(tid, "olga@jobco.test", Pw, "Olga Owner", TenantRole.Owner | TenantRole.Admin);
await tenants.AddTechnicianAsync(tid, "tess@jobco.test", Pw, "Tess Tech", TenantRole.Technician);
await tenants.AddTechnicianAsync(tid, "bea@jobco.test", Pw, "Bea Office", TenantRole.Billing);
var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
var tickets = new TicketService(factory, photos);
var rec = new RecurringTaskService(factory, tickets);
const TenantRole T = TenantRole.Technician, O = TenantRole.Owner | TenantRole.Admin, B = TenantRole.Billing;
const string Olga = "Olga Owner", Tess = "Tess Tech";

async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(x => x.Id == id); }
async Task<Tenant> Company() { await using var db = new PlatformDb(options); return await db.Tenants.FirstAsync(x => x.Id == tid); }
Task<Ticket> New(TicketCategory c, string subject = "A job") =>
    tickets.CreateAsync(tid, subject, "It is broken.", Olga, TicketSource.Manual, assignedTo: Tess, category: c, clientId: null);
async Task<HashSet<string>> Offered(int id, TenantRole role = O, string me = Olga) =>
    TicketService.ActionsFor(await Get(id), role, me).Select(a => a.Key).ToHashSet();
Task<string> Act(int id, string key, StageInput? input = null) => tickets.ActAsync(tid, id, Olga, O, key, input);
async Task<string> Stage(int id) => (await Get(id)).Stage;

try
{
    // =========================================================== saving
    Head("saving: a supervisor or the owner, cleaned, and who and when");
    var wOld = await New(TicketCategory.Workshop, "Opened before anything was switched off");
    var gOld = await New(TicketCategory.General, "General, opened before");
    Check("a ticket opened before Job types are chosen copies everything on", (await Get(wOld.Id)).RouteOff == "");
    var why = await tenants.SetJobTypesAsync(tid, T, Tess, new[] { JobTypes.Inspection });
    Check("a technician may not change them", why.Contains("supervisor's or the owner's") && (await Company()).JobTypesOff == "", why);
    why = await tenants.SetJobTypesAsync(tid, B, "Bea Office", new[] { JobTypes.Inspection });
    Check("nor the office administrator", why.Length > 0 && (await Company()).JobTypesOff == "", why);
    var off = new[] { JobTypes.DniApproval, "not-a-switch", JobTypes.Inspection, JobTypes.PhotosIn, JobTypes.PhotosOut, JobTypes.Delivery,
                      JobTypes.TrackPayment, JobTypes.QuotePayFirst, JobTypes.QuoteSuppliers, JobTypes.AddMachine };
    why = await tenants.SetJobTypesAsync(tid, O, Olga, off);
    var co = await Company();
    Check("the owner may, and it is stored cleaned: the unknown key gone, in the page's order", why == "" && co.JobTypesOff == JobTypes.Clean(off)
        && !co.JobTypesOff.Contains("not-a-switch") && co.JobTypesOff.StartsWith(JobTypes.PhotosIn), $"{why} [{co.JobTypesOff}]");
    Check("...with who and when", co.JobTypesChosenBy == Olga && co.JobTypesChosenUtc is DateTime at && (DateTime.UtcNow - at).TotalMinutes < 5);
    var stored = co.JobTypesOff;

    // =========================================================== each ticket's own copy
    Head("each new ticket copies the company's switches; one opened before keeps its route");
    var wNew = await New(TicketCategory.Workshop, "Opened after");
    Check("a new ticket carries the company's list", (await Get(wNew.Id)).RouteOff == stored, (await Get(wNew.Id)).RouteOff);
    Check("the one opened before still carries everything on", (await Get(wOld.Id)).RouteOff == "");

    // =========================================================== skipping Inspection findings
    Head("Inspection findings switched off");
    var nowOffered = await Offered(wNew.Id);
    Check("Booked in offers Repairable and Not repairable, and no Inspection findings",
        nowOffered.IsSupersetOf(new[] { "repairable", "not-repairable" }) && !nowOffered.Contains("inspection"), string.Join(", ", nowOffered));
    why = await Act(wNew.Id, "inspection", new StageInput { Details = "PSU dead." });
    Check("pressing it anyway (a stale page, an old console) is REFUSED, not obeyed", why.Contains("switched off") && await Stage(wNew.Id) == Workflow.First, why);
    Check("the job opened before still has Inspection findings, and it works", (await Offered(wOld.Id)).Contains("inspection")
        && await Act(wOld.Id, "inspection", new StageInput { Details = "PSU dead." }) == "" && await Stage(wOld.Id) == Workflow.Inspected);
    Check("Repairable straight from Booked in", await Act(wNew.Id, "repairable") == "" && await Stage(wNew.Id) == Workflow.RepairableStage);
    Check("...and Back goes back to Booked in", await tickets.BackStepAsync(tid, await Get(wNew.Id), O, Olga) != null
        && await Act(wNew.Id, TicketService.GoBackKey, new StageInput { Reason = "Pressed too soon." }) == "" && await Stage(wNew.Id) == Workflow.First);
    Check("...Repairable again", await Act(wNew.Id, "repairable") == "");

    // =========================================================== photos, delivery, payment
    Head("no photos out, no delivery, no waiting for payment");
    Check("Solved — to be invoiced", await Act(wNew.Id, "solved-invoice", new StageInput { Details = "New PSU.", Minutes = 30, Amount = 650 }) == "");
    nowOffered = await Offered(wNew.Id);
    Check("ready: Machine collected, and no Out for delivery", nowOffered.Contains(TicketService.CollectedKey) && !nowOffered.Contains(TicketService.DeliverKey), string.Join(", ", nowOffered));
    why = await Act(wNew.Id, TicketService.DeliverKey);
    Check("Out for delivery pressed anyway is refused", why.Contains("switched off"), why);
    why = await Act(wNew.Id, TicketService.CollectedKey);
    Check("collected with NO photos out", why == "" && (await Get(wNew.Id)).CollectedUtc != null, why);
    Check("Client invoiced finishes it — no Invoiced — awaiting payment",
        await Act(wNew.Id, "client-invoiced", new StageInput { Amount = 650, Reference = "INV-1" }) == "" && await Stage(wNew.Id) == Workflow.Closed, await Stage(wNew.Id));
    Check("the job opened before still needs its photos out", await Act(wOld.Id, "repairable") == ""
        && await Act(wOld.Id, "solved-invoice", new StageInput { Details = "New PSU.", Minutes = 30, Amount = 650 }) == ""
        && (await Act(wOld.Id, TicketService.CollectedKey)).Contains("photos"));
    Check("...and still waits for its payment",
        await Act(wOld.Id, "client-invoiced", new StageInput { Amount = 650, Reference = "INV-2" }) == "" && await Stage(wOld.Id) == "awaiting-payment");

    // =========================================================== photos in
    Head("no photos when booked in");
    Check("a workshop booking with nothing staged is accepted", await photos.CheckBookInAsync(tid, TicketCategory.Workshop, "nothing-staged", Olga) == "");
    Check("...and refused for a booking that follows everything on (another machine on an older job)",
        (await photos.CheckBookInAsync(tid, TicketCategory.Workshop, "nothing-staged", Olga, "")).Contains("photos"));
    var wNew2 = await New(TicketCategory.Workshop, "Second after");
    Check("Add another machine is switched off: not offered, and refused with the reason",
        !(await Offered(wNew2.Id)).Contains(TicketService.AddMachineKey)
        && (await Act(wNew2.Id, TicketService.AddMachineKey, new StageInput { Details = "Another laptop." })).Contains("switched off"));

    // =========================================================== Do not invoice without approval
    Head("no supervisor approval for Do not invoice");
    var rNew = await New(TicketCategory.RemoteSupport, "Session");
    why = await Act(rNew.Id, "remote-dni", new StageInput { Reason = "Two minutes, goodwill." });
    Check("Do not invoice goes straight to the office to close", why == "" && await Stage(rNew.Id) == "dni-to-close", $"{why} {await Stage(rNew.Id)}");
    Check("...with the reason kept", (await Get(rNew.Id)).DoNotInvoiceReason == "Two minutes, goodwill.");

    // =========================================================== quotes without suppliers or payment first
    Head("a quote with no suppliers asked and no payment before ordering");
    var gNew = await New(TicketCategory.General, "Toner for reception");
    await using (var db = new PlatformDb(options))
    {
        db.QuoteLines.Add(new QuoteLine { TenantId = tid, TicketId = gNew.Id, ItemName = "Toner TN-2420", Quantity = 2 });
        await db.SaveChangesAsync();
    }
    Check("Quote needed", await Act(gNew.Id, "quote-needed") == "" && await Stage(gNew.Id) == "quote-needed");
    nowOffered = await Offered(gNew.Id);
    Check("offers Quote sent to client, and no Quote requested", nowOffered.Contains("quote-sent") && !nowOffered.Contains("quote-requested"), string.Join(", ", nowOffered));
    Check("Quote requested pressed anyway is refused", (await Act(gNew.Id, "quote-requested", new StageInput { SupplierEmail = "s@x.test" })).Contains("switched off"));
    Check("Quote sent", await Act(gNew.Id, "quote-sent", new StageInput { Amount = 1200, Reference = "Q-7" }) == "");
    Check("Quote accepted → order the parts", await Act(gNew.Id, "quote-accepted") == "" && await Stage(gNew.Id) == Workflow.OrderParts, await Stage(gNew.Id));
    Check("Order placed → parts ordered from supplier", await Act(gNew.Id, Workflow.OrderParts) == "" && await Stage(gNew.Id) == Workflow.PartsOnOrder);
    Check("Parts received", await Act(gNew.Id, "parts-received") == "" && await Stage(gNew.Id) == "parts-received");
    Check("Parts fitted → To be invoiced (nothing has been invoiced on this road)",
        await Act(gNew.Id, "parts-fitted") == "" && await Stage(gNew.Id) == "to-be-invoiced", await Stage(gNew.Id));
    Check("...then invoiced, and finished", await Act(gNew.Id, "client-invoiced", new StageInput { Amount = 1200, Reference = "INV-3" }) == ""
        && await Stage(gNew.Id) == Workflow.Closed);

    // =========================================================== categories, asked live
    Head("categories: switched off for new work, finished for old");
    var cOld = await New(TicketCategory.CallOut, "Server room");
    Check("a call out opened while everything was on", await Act(cOld.Id, "callout-work-done", new StageInput { Details = "Replaced the switch.", Minutes = 60 }) == "");
    var schedule = await rec.CreateAsync(tid, Olga, new RecurringInput { Title = "Check the backups", Kind = "Maintenance", AssignedTo = Tess, Repeats = Repeats.Daily },
        new DateTime(2026, 12, 1, 3, 0, 0, DateTimeKind.Utc));
    Check("a daily recurring task set up", schedule.task != null, schedule.error);
    why = await tenants.SetJobTypesAsync(tid, O, Olga, stored.Split(',').Concat(new[] { JobTypes.CallOut, JobTypes.Workshop, JobTypes.Recurring }));
    co = await Company();
    Check("Call out, Workshop and Recurring task switched off — and Workshop's own steps dropped from the list",
        why == "" && co.JobTypesOff.Contains(JobTypes.CallOut) && !co.JobTypesOff.Split(',').Contains(JobTypes.Inspection)
        && co.JobTypesOff.Contains(JobTypes.DniApproval), $"{why} [{co.JobTypesOff}]");
    why = await tickets.CategoryRefusalAsync(tid, TicketCategory.CallOut);
    Check("a new call out is refused, in words that say where to change it", why.Contains("switched Call out off") && why.Contains("Job types"), why);
    Check("General and Remote support are never refused", await tickets.CategoryRefusalAsync(tid, TicketCategory.General) == ""
        && await tickets.CategoryRefusalAsync(tid, TicketCategory.RemoteSupport) == "");
    var (editWhy, _) = await tickets.EditAsync(tid, gOld.Id, Olga, O, new TicketEdit(Category: (int)TicketCategory.CallOut));
    Check("a ticket cannot be changed into a switched-off category", editWhy.Contains("switched Call out off") && (await Get(gOld.Id)).Category == TicketCategory.General, editWhy);
    nowOffered = await Offered(cOld.Id);
    Check("the call out opened before carries on: its endings are still offered", nowOffered.Contains("callout-invoice"), string.Join(", ", nowOffered));
    why = await Act(cOld.Id, Workflow.ToWorkshop, new StageInput());
    Check("...but cannot become a workshop job now Workshop is off — refused in words, not just hidden",
        why.Contains("switched Workshop off") && (await Get(cOld.Id)).Category == TicketCategory.CallOut, why);
    Check("...and finishes", await Act(cOld.Id, "callout-invoice", new StageInput { Amount = 900 }) == "" && await Stage(cOld.Id) == "to-be-invoiced");

    Head("recurring tasks pause, and come back without a pile");
    var refused = await rec.CreateAsync(tid, Olga, new RecurringInput { Title = "Readings", Kind = "Readings", AssignedTo = Tess, Repeats = Repeats.Daily });
    Check("no new schedule while Recurring task is off", refused.task == null && refused.error.Contains("switched Recurring task off"), refused.error);
    async Task<List<Ticket>> Opened() { await using var db = new PlatformDb(options); return await db.Tickets.Where(t => t.RecurringTaskId == schedule.task!.Id).ToListAsync(); }
    int had = (await Opened()).Count;
    int made = await rec.OpenDueAsync(tid, new DateTime(2026, 12, 4, 8, 0, 0, DateTimeKind.Utc));
    Check("three mornings pass: nothing opens", made == 0 && (await Opened()).Count == had, $"{made} opened");
    await using (var db = new PlatformDb(options))
    {
        var r = await db.RecurringTasks.FirstAsync(x => x.Id == schedule.task!.Id);
        Check("...the schedule is kept, and its next day has moved on past them", r.CancelledUtc == null && r.NextDue.Date == new DateTime(2026, 12, 5), r.NextDue.ToString("yyyy-MM-dd"));
    }
    why = await tenants.SetJobTypesAsync(tid, O, Olga, stored.Split(','));
    made = await rec.OpenDueAsync(tid, new DateTime(2026, 12, 5, 8, 0, 0, DateTimeKind.Utc));
    var opened = await Opened();
    Check("switched on again, the next morning opens ONE — that day's, not the ones missed", why == "" && made == 1 && opened.Count == had + 1
        && opened.OrderBy(t => t.Id).Last().DueOn == new DateTime(2026, 12, 5), $"{made} opened");
    Check("...and none for the days it was off", !opened.Any(t => t.DueOn >= new DateTime(2026, 12, 2) && t.DueOn <= new DateTime(2026, 12, 4)));
}
finally
{
    try { Directory.Delete(dataDir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"jobtypewire ({HarnessDb.Name}): {pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
