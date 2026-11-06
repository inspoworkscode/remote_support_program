// =====================================================================================
// workflowcheck - proves the Workshop route of stage 3.7 (docs/NEXT-SESSION-WORKFLOWS.md, the
// map Clint approved on 2026-09-24).
//
// Every route is walked end to end through TicketService.ActAsync, never through a page: a button
// that is not drawn is manners, and the rule is whatever runs when the button is pressed anyway -
// from a stale tab, from an old console, or by hand. For each route it asks three things:
//
//   does the job go where the map says, and land on the right desk
//   is every "must be true before it leaves" refused when it is not true
//   can anybody who should NOT press a button press it
//
// Plus the edges: the old status dropdown cannot walk around a stage, a technician never closes
// anything, other categories are untouched, and existing workshop jobs join the route where
// their status says they are.
//
//   dotnet run -c Debug --project tools/workflowcheck/workflowcheck.csproj
// =====================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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

var dir = Path.Combine(Path.GetTempPath(), "workflowcheck-" + Guid.NewGuid().ToString("N")[..8]);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "platform.db");
Console.WriteLine("workflowcheck - the Workshop route, every branch, through the service");
Console.WriteLine();

const string Sue = "Sue Tech", Bob = "Bob Tech", Olive = "Olive Office", Sam = "Sam Supervisor", Owen = "Owen Owner";
// The owner who works the jobs: Owner / Supervisor. An owner-only login works no ticket since 2026-10-15 (rolecheck).
const TenantRole T = TenantRole.Technician, OA = TenantRole.Billing, S = TenantRole.Admin, O = TenantRole.Owner | TenantRole.Admin;

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath};Pooling=False").Options;

    // ---- existing workshop jobs, written before the route existed ----
    bool upgrade = HarnessDb.SqliteUpgradePart("the jobs already on file");
    if (!upgrade)
        await using (var db = new PlatformDb(options)) { await HarnessDb.MigrateAsync(db); await Seed.Tenant(db, 1, "w", "W", "kw"); }
    else
    await using (var db = new PlatformDb(options))
    {
        await db.GetService<IMigrator>().MigrateAsync("20260924074927_OwnerRole");
        await Seed.OldTenant(db, 1, "w", "W", "kw");

        // Rows written the way the product wrote them BEFORE this migration: by SQL against the
        // table as it stood then, because today's model has columns that table does not.
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        var cols = new List<(string name, string type, bool required)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Tickets)";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                cols.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1 && r.IsDBNull(4) && r.GetInt32(5) == 0));
        }
        int n = 100;
        async Task OldRow(string subject, int status, int category)
        {
            var given = new Dictionary<string, object> { ["TenantId"] = 1, ["Number"] = n++, ["Subject"] = subject,
                ["Status"] = status, ["Category"] = category, ["AssignedTo"] = Sue };
            foreach (var c in cols.Where(c => c.required && !given.ContainsKey(c.name)))
                given[c.name] = c.type.StartsWith("INT") ? 0 : c.name.EndsWith("Utc") ? "2026-09-01 00:00:00" : "";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO Tickets ({string.Join(",", given.Keys)}) VALUES ({string.Join(",", given.Keys.Select(k => "@" + k))})";
            foreach (var kv in given) { var p = cmd.CreateParameter(); p.ParameterName = "@" + kv.Key; p.Value = kv.Value; cmd.Parameters.Add(p); }
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var st in Enum.GetValues<TicketStatus>()) await OldRow($"old {st}", (int)st, (int)TicketCategory.Workshop);
        await OldRow("old remote", (int)TicketStatus.InProgress, (int)TicketCategory.RemoteSupport);
        await conn.CloseAsync();
        await HarnessDb.MigrateAsync(db);
    }

    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }
    async Task<Ticket> Old(string subject) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Subject == subject); }

    Console.WriteLine("== jobs already on file (answer 21) ==");
    // Through BOTH migrations: WorkshopWorkflow put them on the bench, TimelineForms (2026-09-27)
    // moved "On the bench" to Inspected, where the next question is Repairable or not.
    if (upgrade) {
    Check("an Open workshop job is Booked in", (await Old("old Open")).Stage == "awaiting-diagnosis");
    Check("an In progress one is Inspected", (await Old("old InProgress")).Stage == "inspected");
    var held = await Old("old OnHold");
    Check("an On hold one is Waiting on customer, and returns to Inspected", held.Stage == "waiting-customer" && held.StageReturn == "inspected");
    var resolved = await Old("old Resolved");
    Check("a Resolved one is back at Inspected — it has not had its decision",
        resolved.Stage == "inspected" && resolved.Status == TicketStatus.InProgress && resolved.ClosedUtc == null, resolved.Status.ToString());
    Check("...and is offered Repairable / Not repairable", TicketService.ActionsFor(resolved, T, Sue).Select(a => a.Key).Intersect(new[] { "repairable", "not-repairable" }).Count() == 2);
    var closedOld = await Old("old Closed");
    Check("a Closed one is closed", closedOld.Stage == "closed" && closedOld.Lane == (int)WorkLane.Done);
    // Every category has its own route since 2026-09-27 (migration EveryCategoryRoute).
    Check("a remote support job joins ITS route, not the workshop's", (await Old("old remote")).Stage == "remote-session");
    }

    // ---- booking in ----
    Console.WriteLine();
    Console.WriteLine("== booking in ==");
    Check("a workshop booking with no technician is REFUSED", TicketService.CheckBookIn(TicketCategory.Workshop, "").Length > 0);
    Check("any other category may still be booked unassigned", TicketService.CheckBookIn(TicketCategory.General, "") == "");
    Check("anybody may name the technician when booking in", Permissions.CanAssignWhenOpening(T) && Permissions.CanAssignWhenOpening(OA));

    async Task<Ticket> Book(string subject)
        => await tickets.CreateAsync(1, subject, "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
    async Task AfterPhotos(int id, int count)
    {
        for (int i = 0; i < count; i++)
            await photos.AddAsync(1, RemoteSupport.Tools.TestImages.Jpeg(), $"out-{i}.jpg", PhotoKind.After, Sue, id, "");
    }
    async Task AddLine(int id, string item)
    {
        await using var db = new PlatformDb(options);
        db.QuoteLines.Add(new QuoteLine { TenantId = 1, TicketId = id, ItemId = 1, ItemName = item, PartNumber = "P-1", AddedBy = Sue });
        await db.SaveChangesAsync();
    }
    string[] Keys(Ticket t, TenantRole r, string me) => TicketService.ActionsFor(t, r, me).Select(a => a.Key).ToArray();
    // The bench, in Clint's order: Inspection findings, then Repairable or not — and then how it ends.
    async Task ToRepairable(int id)
    {
        await tickets.ActAsync(1, id, Sue, T, "inspection", new StageInput { Details = "Looked it over." });
        await tickets.ActAsync(1, id, Sue, T, "repairable");
    }
    async Task ToNotRepairable(int id)
    {
        await tickets.ActAsync(1, id, Sue, T, "inspection", new StageInput { Details = "Board corroded." });
        await tickets.ActAsync(1, id, Sue, T, "not-repairable");
    }

    var job = await Book("Laptop will not charge");
    Check("a new workshop job starts Awaiting diagnosis, on the bench, Open",
        job.Stage == "awaiting-diagnosis" && job.Lane == (int)WorkLane.Bench && job.Status == TicketStatus.Open);
    var general = await tickets.CreateAsync(1, "Printer", "", Olive, TicketSource.Manual);
    Check("a General ticket starts on its own route, not the workshop's", general.Stage == "general-open");

    // ---- the bench ----
    Console.WriteLine();
    Console.WriteLine("== the bench ==");
    // Clint's order, 2026-09-27: Inspection findings → Repairable / Not repairable → how it ends, with
    // what was done written into Solved — to be invoiced.
    Check("the assigned technician is offered Inspection findings, and it is the first step", Keys(job, T, Sue).Contains("inspection") && !Keys(job, T, Sue).Contains("repairable"));
    Check("a colleague is offered nothing", Keys(job, T, Bob).Length == 0, string.Join(",", Keys(job, T, Bob)));
    Check("a colleague pressing it anyway is REFUSED", (await tickets.ActAsync(1, job.Id, Bob, T, "inspection", new StageInput { Details = "x" })).Length > 0);
    Check("the office administrator cannot do the bench work", (await tickets.ActAsync(1, job.Id, Olive, OA, "inspection", new StageInput { Details = "x" })).Length > 0);
    var noFindings = await tickets.ActAsync(1, job.Id, Sue, T, "inspection");
    Check("Inspection findings with nothing written is REFUSED", noFindings.Contains("inspection found"), noFindings);
    Check("Inspection findings → Inspected",
        await tickets.ActAsync(1, job.Id, Sue, T, "inspection", new StageInput { Details = "DC jack loose; charges intermittently." }) == "" && (await Get(job.Id)).Stage == "inspected");
    await using (var db = new PlatformDb(options))
    {
        var form = await db.TicketNotes.Where(n => n.TicketId == job.Id && n.FormTitle != "").OrderBy(n => n.Id).FirstOrDefaultAsync();
        Check("...and the findings are in the timeline as that form", form?.FormTitle == "Inspection findings" && form.Body.Contains("DC jack loose") && form.MovedTo == "inspected");
    }
    Check("a step from the wrong stage is REFUSED — Solved before Repairable",
        (await tickets.ActAsync(1, job.Id, Sue, T, "solved-invoice", new StageInput { Details = "x", Minutes = 5, Amount = 5 })).Length > 0);
    Check("Work done is no longer a step of its own", Workflow.Action("work-done") == null
        && (await tickets.ActAsync(1, job.Id, Sue, T, "work-done", new StageInput { Details = "x" })).Length > 0);
    Check("Inspected offers Repairable and Not repairable", Keys(await Get(job.Id), T, Sue).Intersect(new[] { "repairable", "not-repairable" }).Count() == 2);

    await tickets.UpdateAsync(1, job.Id, Owen, O, TicketStatus.Closed, TicketPriority.High, null, null);
    var j = await Get(job.Id);
    Check("the old status dropdown cannot move it — not even for the owner", j.Stage == "inspected" && j.Status == TicketStatus.InProgress);
    Check("...says why", tickets.LastBlockedReason.Contains("stages"), tickets.LastBlockedReason);
    Check("...and the rest of the update still applies", j.Priority == TicketPriority.High);

    Check("Waiting on customer", await tickets.ActAsync(1, job.Id, Sue, T, "wait-customer") == "" && (await Get(job.Id)).Status == TicketStatus.OnHold);
    Check("Back to work returns to Inspected", await tickets.ActAsync(1, job.Id, Sue, T, "resume") == "" && (await Get(job.Id)).Stage == "inspected");

    Check("Repairable → Repairable, recorded", await tickets.ActAsync(1, job.Id, Sue, T, "repairable") == "" && (await Get(job.Id)) is { Stage: "repairable", Repairable: true });
    var repKeys = Keys(await Get(job.Id), T, Sue);
    Check("Repairable offers the three endings at once — and not the inspection fee",
        new[] { "solved-invoice", "quote-needed", "do-not-invoice" }.All(repKeys.Contains) && !repKeys.Contains("inspection-fee"), string.Join(",", repKeys));
    Check("...in that order, with Waiting on customer after them",
        repKeys.SequenceEqual(new[] { "solved-invoice", "quote-needed", "do-not-invoice", "wait-customer" }), string.Join(",", repKeys));
    Check("Solved asks what was done FIRST, then the time and the amount",
        Workflow.Action("solved-invoice")!.Fields.SequenceEqual(new[] { StageField.Details, StageField.Minutes, StageField.Amount }));
    var noWork = await tickets.ActAsync(1, job.Id, Sue, T, "solved-invoice", new StageInput { Minutes = 30, Amount = 450 });
    Check("Solved to be invoiced without saying what was done is REFUSED", noWork.Contains("was done"), noWork);
    Check("...without the time is REFUSED",
        (await tickets.ActAsync(1, job.Id, Sue, T, "solved-invoice", new StageInput { Details = "x", Amount = 450 })).Length > 0);
    Check("...without the amount is REFUSED",
        (await tickets.ActAsync(1, job.Id, Sue, T, "solved-invoice", new StageInput { Details = "x", Minutes = 30 })).Length > 0);

    // ---- route 1: solved, to be invoiced ----
    Console.WriteLine();
    Console.WriteLine("== solved, to be invoiced ==");
    const string work = "Replaced the DC jack.\nTested on charge for an hour.";
    Check("with what was done, the time and the amount it goes",
        await tickets.ActAsync(1, job.Id, Sue, T, "solved-invoice", new StageInput { Details = work, Minutes = 30, Amount = 450 }) == "");
    await using (var db = new PlatformDb(options))
    {
        var solved = await db.TicketNotes.Where(n => n.TicketId == job.Id && n.IsWorkDone).ToListAsync();
        Check("...written as ONE entry, the Solved form, marked as the work done",
            solved.Count == 1 && solved[0].FormTitle == "Solved — to be invoiced" && solved[0].Body.StartsWith(work) && solved[0].Body.Contains("Time spent: "),
            solved.Count == 1 ? solved[0].Body.Replace("\n", " | ") : $"{solved.Count} entries");
        Check("...and what goes on the invoice is the work alone — both lines, no time, no amount",
            solved.Count == 1 && TicketService.WorkDoneText(solved[0].Body) == work, solved.Count == 1 ? TicketService.WorkDoneText(solved[0].Body).Replace("\n", " | ") : "");
    }
    Check("an older Work done entry, with no time under it, copies whole",
        TicketService.WorkDoneText("Cleaned the fan.\nReplaced the paste.") == "Cleaned the fan.\nReplaced the paste.");
    j = await Get(job.Id);
    Check("...to To be invoiced, in the billing queue, assigned to nobody",
        j.Stage == "to-be-invoiced" && j.Lane == (int)WorkLane.Office && j.AssignedTo == "", $"{j.Stage} / '{j.AssignedTo}'");
    Check("...recording who did it, the time and the amount, and that it was repairable",
        j.WorkedBy == Sue && j.TimeSpentMinutes == 30 && j.InvoiceAmount == 450 && j.Repairable == true && j.Outcome == "solved");

    await using (var db = new PlatformDb(options))
    {
        var sees = await TicketService.VisibleTo(db.Tickets, T, Sue).AnyAsync(t => t.Id == job.Id);
        var bobSees = await TicketService.VisibleTo(db.Tickets, T, Bob).AnyAsync(t => t.Id == job.Id);
        Check("the technician who did the work can still follow it", sees);
        Check("another technician cannot see it in their pool", !bobSees);
    }
    Check("it is in the billing queue", (await tickets.ListAsync(1, "billing", "", Olive, OA)).Any(t => t.Id == job.Id));
    Check("it is NOT in the unassigned pool", !(await tickets.ListAsync(1, "unassigned", "", Olive, OA)).Any(t => t.Id == job.Id));

    Check("the technician cannot press Client invoiced",
        (await tickets.ActAsync(1, job.Id, Sue, T, "client-invoiced", new StageInput { Amount = 450, Reference = "INV-9" })).Length > 0);
    Check("Client invoiced without the invoice number is REFUSED",
        (await tickets.ActAsync(1, job.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 450 })).Length > 0);
    Check("Client invoiced → Invoiced, awaiting payment",
        await tickets.ActAsync(1, job.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 450, Reference = "INV-9" }) == ""
        && (await Get(job.Id)).Stage == "awaiting-payment" && (await Get(job.Id)).InvoiceNumber == "INV-9");
    Check("Payment received with the machine still here → Awaiting collection, not closed",
        await tickets.ActAsync(1, job.Id, Olive, OA, "payment-received") == ""
        && (await Get(job.Id)).Stage == "awaiting-collection" && (await Get(job.Id)).Status != TicketStatus.Closed);
    var closeEarly = await tickets.ActAsync(1, job.Id, Olive, OA, "close");
    Check("closing before it is collected is REFUSED, and says so", closeEarly.Contains("collected"), closeEarly);
    await AfterPhotos(job.Id, 2);
    Check("collecting with two photos is REFUSED", (await tickets.ActAsync(1, job.Id, Sue, T, TicketService.CollectedKey)).Length > 0);
    await AfterPhotos(job.Id, 1);
    Check("with three, the technician who did the work can mark it collected",
        await tickets.ActAsync(1, job.Id, Sue, T, TicketService.CollectedKey) == "" && (await Get(job.Id)).CollectedBy == Sue);
    Check("a technician still cannot close it", (await tickets.ActAsync(1, job.Id, Sue, T, "close")).Length > 0);
    Check("the office administrator closes it",
        await tickets.ActAsync(1, job.Id, Olive, OA, "close") == "" && (await Get(job.Id)).Status == TicketStatus.Closed && (await Get(job.Id)).ClosedUtc != null);
    Check("a closed job offers nobody anything", Keys(await Get(job.Id), O, Owen).Length == 0);

    // ---- route 2: not repairable, invoice inspection fee ----
    Console.WriteLine();
    Console.WriteLine("== not repairable, inspection fee ==");
    var dead = await Book("Water damage");
    await ToNotRepairable(dead.Id);
    Check("goes to To be invoiced, marked not repairable",
        await tickets.ActAsync(1, dead.Id, Sue, T, "inspection-fee", new StageInput { Minutes = 20, Amount = 250 }) == ""
        && (await Get(dead.Id)).Stage == "to-be-invoiced" && (await Get(dead.Id)).Repairable == false);
    await AfterPhotos(dead.Id, 3);
    await tickets.ActAsync(1, dead.Id, Olive, OA, TicketService.CollectedKey);
    await tickets.ActAsync(1, dead.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 250, Reference = "INV-10" });
    Check("collected first, Payment received closes it in one step",
        await tickets.ActAsync(1, dead.Id, Olive, OA, "payment-received") == "" && (await Get(dead.Id)).Stage == "closed");

    // ---- route 3: quote needed, rejected, re-quoted, declined ----
    Console.WriteLine();
    Console.WriteLine("== quote needed: re-quote and rejection ==");
    var q = await Book("Screen cracked");
    await ToRepairable(q.Id);
    Check("Quote needed with no items is REFUSED", (await tickets.ActAsync(1, q.Id, Sue, T, "quote-needed")).Contains("item"));
    await AddLine(q.Id, "Laptop screen 15.6 FHD");
    Check("with an item → Quote needed, in the billing queue",
        await tickets.ActAsync(1, q.Id, Sue, T, "quote-needed") == "" && (await Get(q.Id)).Lane == (int)WorkLane.Office);
    Check("collecting while a quote is outstanding is REFUSED", (await tickets.ActAsync(1, q.Id, Olive, OA, TicketService.CollectedKey)).Length > 0);
    Check("Quote requested with a bad address is REFUSED",
        (await tickets.ActAsync(1, q.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = "not an address" })).Length > 0);
    Check("Quote requested → awaiting supplier",
        await tickets.ActAsync(1, q.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = "sales@supplier.test" }) == ""
        && (await Get(q.Id)).QuoteSupplierEmail == "sales@supplier.test");
    Check("Quote sent needs the amount and reference",
        (await tickets.ActAsync(1, q.Id, Olive, OA, "quote-sent", new StageInput { Amount = 1800 })).Length > 0);
    Check("Quote sent → awaiting client",
        await tickets.ActAsync(1, q.Id, Olive, OA, "quote-sent", new StageInput { Amount = 1800, Reference = "Q-77" }) == ""
        && (await Get(q.Id)).QuoteAmount == 1800 && (await Get(q.Id)).QuoteReference == "Q-77");
    Check("the office administrator is offered accept, reject and re-quote",
        new[] { "quote-accepted", "quote-rejected", "re-quote" }.All(Keys(await Get(q.Id), OA, Olive).Contains));
    Check("Re-quote → back to Quote needed", await tickets.ActAsync(1, q.Id, Olive, OA, "re-quote") == "" && (await Get(q.Id)).Stage == "quote-needed");
    await using (var db = new PlatformDb(options))
        Check("...with the items kept", await db.QuoteLines.CountAsync(l => l.TicketId == q.Id) == 1);
    await tickets.ActAsync(1, q.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = "sales@supplier.test" });
    await tickets.ActAsync(1, q.Id, Olive, OA, "quote-sent", new StageInput { Amount = 1650, Reference = "Q-78" });
    Check("Quote rejected → back to the technician who did the work",
        await tickets.ActAsync(1, q.Id, Olive, OA, "quote-rejected") == ""
        && (await Get(q.Id)).Stage == "quote-rejected" && (await Get(q.Id)).AssignedTo == Sue && (await Get(q.Id)).Lane == (int)WorkLane.Bench);
    var rejectedKeys = Keys(await Get(q.Id), T, Sue);
    Check("...offered ONLY inspection fee or do not invoice (answer 8)",
        rejectedKeys.Contains("declined-inspection-fee") && rejectedKeys.Contains("declined-do-not-invoice") && !rejectedKeys.Contains("solved-invoice"),
        string.Join(",", rejectedKeys));
    Check("...and Solved pressed anyway is REFUSED",
        (await tickets.ActAsync(1, q.Id, Sue, T, "solved-invoice", new StageInput { Minutes = 1, Amount = 1 })).Length > 0);
    Check("Do not invoice without a reason is REFUSED", (await tickets.ActAsync(1, q.Id, Sue, T, "declined-do-not-invoice")).Length > 0);
    Check("Do not invoice → awaiting a supervisor, still marked repairable",
        await tickets.ActAsync(1, q.Id, Sue, T, "declined-do-not-invoice", new StageInput { Reason = "Customer declined; goodwill." }) == ""
        && (await Get(q.Id)).Lane == (int)WorkLane.Supervisor && (await Get(q.Id)).Repairable == true);
    Check("the office administrator cannot approve it", (await tickets.ActAsync(1, q.Id, Olive, OA, "approve-dni")).Length > 0);
    Check("the technician cannot approve their own", (await tickets.ActAsync(1, q.Id, Sue, T, "approve-dni")).Length > 0);
    Check("a supervisor declining needs a reason", (await tickets.ActAsync(1, q.Id, Sam, S, "decline-dni")).Length > 0);
    Check("declined → back at Repairable, with the technician",
        await tickets.ActAsync(1, q.Id, Sam, S, "decline-dni", new StageInput { Reason = "Charge the inspection." }) == ""
        && (await Get(q.Id)).Stage == "repairable" && (await Get(q.Id)).AssignedTo == Sue);

    // ---- route 4: quote accepted, all the way ----
    Console.WriteLine();
    Console.WriteLine("== quote accepted ==");
    var acc = await Book("Needs more memory");
    await ToRepairable(acc.Id);
    await AddLine(acc.Id, "DDR4 SO-DIMM 8GB");
    await tickets.ActAsync(1, acc.Id, Sue, T, "quote-needed");
    // Pass the billing steps between the three roles that may do them, to prove each can.
    await tickets.ActAsync(1, acc.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = "sales@supplier.test" });
    await tickets.ActAsync(1, acc.Id, Sam, S, "quote-sent", new StageInput { Amount = 1200, Reference = "Q-80" });
    await tickets.ActAsync(1, acc.Id, Owen, O, "quote-accepted");
    Check("accepted → Invoice client", (await Get(acc.Id)).Stage == "invoice-client");
    await tickets.ActAsync(1, acc.Id, Olive, OA, "quote-invoiced", new StageInput { Amount = 1200, Reference = "INV-11" });
    Check("Client invoiced → awaiting payment before ordering", (await Get(acc.Id)).Stage == "quote-awaiting-payment");
    await tickets.ActAsync(1, acc.Id, Olive, OA, "order-placed");
    Check("Payment received, order placed", (await Get(acc.Id)).Stage == "order-placed");
    Check("Parts received → back with the technician to fit",
        await tickets.ActAsync(1, acc.Id, Olive, OA, "parts-received") == ""
        && (await Get(acc.Id)).Stage == "parts-received" && (await Get(acc.Id)).AssignedTo == Sue);
    Check("the office administrator cannot say the parts are fitted", (await tickets.ActAsync(1, acc.Id, Olive, OA, "parts-fitted")).Length > 0);
    Check("the technician can", await tickets.ActAsync(1, acc.Id, Sue, T, "parts-fitted") == "" && (await Get(acc.Id)).Lane == (int)WorkLane.Office);
    await AfterPhotos(acc.Id, 3);
    await tickets.ActAsync(1, acc.Id, Olive, OA, TicketService.CollectedKey);
    Check("collected, Ticket complete closes it",
        await tickets.ActAsync(1, acc.Id, Olive, OA, "complete") == "" && (await Get(acc.Id)).Status == TicketStatus.Closed);

    // ---- route 5: do not invoice, approved ----
    Console.WriteLine();
    Console.WriteLine("== do not invoice ==");
    var dni = await Book("Warranty return");
    await ToRepairable(dni.Id);
    Check("Do not invoice needs a reason",
        (await tickets.ActAsync(1, dni.Id, Sue, T, "do-not-invoice")).Length > 0);
    await tickets.ActAsync(1, dni.Id, Sue, T, "do-not-invoice", new StageInput { Reason = "Our fault" });
    Check("the owner may approve", await tickets.ActAsync(1, dni.Id, Owen, O, "approve-dni") == "" && (await Get(dni.Id)).Stage == "dni-to-close");
    Check("the reason is kept", (await Get(dni.Id)).DoNotInvoiceReason == "Our fault");
    Check("closing it before collection → Awaiting collection",
        await tickets.ActAsync(1, dni.Id, Olive, OA, "close-dni") == "" && (await Get(dni.Id)).Stage == "awaiting-collection");

    // ---- abandoned ----
    Console.WriteLine();
    Console.WriteLine("== abandoned ==");
    var gone = await Book("Never collected");
    Check("a technician cannot abandon a job", (await tickets.ActAsync(1, gone.Id, Sue, T, "abandon", new StageInput { Reason = "x" })).Length > 0);
    Check("abandoning needs a reason", (await tickets.ActAsync(1, gone.Id, Olive, OA, "abandon")).Length > 0);
    Check("the office administrator abandons it from any stage, no photos needed",
        await tickets.ActAsync(1, gone.Id, Olive, OA, "abandon", new StageInput { Reason = "No contact for three months." }) == ""
        && (await Get(gone.Id)).Stage == "abandoned" && (await Get(gone.Id)).Status == TicketStatus.Closed);

    // ---- the story ----
    Console.WriteLine();
    Console.WriteLine("== the story ==");
    await using (var db = new PlatformDb(options))
    {
        var notes = await db.TicketNotes.Where(n => n.TicketId == acc.Id).OrderBy(n => n.Id).ToListAsync();
        Check("every step wrote an entry in the timeline", notes.Count(n => n.MovedTo != "") >= 11, $"{notes.Count(n => n.MovedTo != "")} steps");
        Check("the steps that asked for something are their forms, filled in",
            new[] { "Inspection findings", "Quote needed", "Quote sent to client", "Client invoiced" }.All(f => notes.Any(n => n.FormTitle == f)),
            string.Join(", ", notes.Where(n => n.FormTitle != "").Select(n => n.FormTitle)));
        Check("the quote's form lists the item asked for", notes.Any(n => n.FormTitle == "Quote needed" && n.Body.Contains("DDR4 SO-DIMM 8GB")));
        Check("each line is stamped with the stage it was written in", notes.All(n => n.Stage.Length > 0));
        Check("a quoted job has no Work done entry — the work is the quote's", notes.Count(n => n.IsWorkDone) == 0);
    }

    // ---- a job left at the retired Work done stage is not stuck ----
    Console.WriteLine();
    Console.WriteLine("== a job left at Work done ==");
    var left = await Book("Left at Work done by yesterday's build");
    await ToRepairable(left.Id);
    await using (var db = new PlatformDb(options))
        await db.Database.ExecuteSqlRawAsync(HarnessDb.Portable("UPDATE Tickets SET Stage = 'work-done' WHERE Id = {0}"), left.Id);
    var leftKeys = Keys(await Get(left.Id), T, Sue);
    Check("it still offers the three endings", new[] { "solved-invoice", "quote-needed", "do-not-invoice" }.All(leftKeys.Contains), string.Join(",", leftKeys));
    Check("and Solved takes it on", await tickets.ActAsync(1, left.Id, Sue, T, "solved-invoice",
        new StageInput { Details = "Done.", Minutes = 10, Amount = 100 }) == "" && (await Get(left.Id)).Stage == "to-be-invoiced");

    // ---- other categories ----
    Console.WriteLine();
    Console.WriteLine("== other categories keep to their own routes ==");
    Check("a General ticket is offered none of the workshop's steps", !Keys(general, S, Sam).Any(k => k is "inspection" or "repairable" or "solved-invoice"), string.Join(",", Keys(general, S, Sam)));
    await tickets.UpdateAsync(1, general.Id, Sue, T, TicketStatus.Closed, null, null, null);
    Check("...and its status dropdown cannot close it round its route", (await Get(general.Id)).Status != TicketStatus.Closed);
    Check("...it closes by its own Done", await tickets.ActAsync(1, general.Id, Sam, S, "general-done") == "" && (await Get(general.Id)).Status == TicketStatus.Closed);
    Check("pressing a stage button on it is REFUSED", (await tickets.ActAsync(1, general.Id, Sam, S, "start")).Length > 0);
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
