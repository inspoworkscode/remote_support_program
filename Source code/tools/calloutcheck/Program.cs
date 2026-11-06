// =====================================================================================
// calloutcheck - proves the Call out route (Clint, 2026-09-27), the second category to get one.
//
//   "once the ticket is created, the next stage should be work done. After work done, it should
//   have the options, Ticket Completed to be invoiced, Invoiced Paid Onsite, or Needs Workshop Work.
//   To be invoiced would follow the same route as the workshop to be invoiced. If Invoiced Paid on
//   Site is selected, it should have two options. Paid Cash or Paid Card Machine. If Paid Cash is
//   selected, it must ask for the amount. If Paid Card Machine is selected, it must ask for the
//   amount and Card Machine Reference Number. If Needs Workshop Work is selected, it must follow the
//   workshop workflow."
//
// And his three answers: the SAME ticket goes to the workshop (asking the booking's questions and
// the three photos in); after a payment on site the OFFICE closes it; Work done asks the work AND
// the time. Every route is walked through TicketService.ActAsync, never a page, and the edges that
// sharing the invoicing stages with the workshop could break are asked about directly: a paid call
// out closes without being "collected", and nothing on a call out ever offers Machine collected.
//
//   dotnet run -c Debug --project tools/calloutcheck/calloutcheck.csproj
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

var dir = Path.Combine(Path.GetTempPath(), "calloutcheck-" + Guid.NewGuid().ToString("N")[..8]);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "platform.db");
Console.WriteLine("calloutcheck - the Call out route, every branch, through the service");
Console.WriteLine();

const string Sue = "Sue Tech", Bob = "Bob Tech", Olive = "Olive Office", Sam = "Sam Supervisor";
const TenantRole T = TenantRole.Technician, OA = TenantRole.Billing, S = TenantRole.Admin;

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath};Pooling=False").Options;

    // ---- call outs already on file, written before this route existed ----
    bool upgrade = HarnessDb.SqliteUpgradePart("the call outs already on file");
    if (!upgrade)
        await using (var db = new PlatformDb(options)) { await HarnessDb.MigrateAsync(db); await Seed.Tenant(db, 1, "c", "Call IT", "kc"); }
    else
    await using (var db = new PlatformDb(options))
    {
        await db.GetService<IMigrator>().MigrateAsync("20260927072338_QuoteLineSerial");
        await Seed.OldTenant(db, 1, "c", "Call IT", "kc");
        // By SQL against the table as it stood then: today's model has columns that table does not.
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
        async Task OldRow(string subject, int status, int category, string closedUtc = "")
        {
            var given = new Dictionary<string, object> { ["TenantId"] = 1, ["Number"] = n++, ["Subject"] = subject,
                ["Status"] = status, ["Category"] = category, ["AssignedTo"] = Sue, ["Stage"] = "" };
            if (closedUtc.Length > 0) given["ClosedUtc"] = closedUtc;
            foreach (var c in cols.Where(c => c.required && !given.ContainsKey(c.name)))
                given[c.name] = c.type.StartsWith("INT") ? 0 : c.name.EndsWith("Utc") ? "2026-09-01 00:00:00" : "";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"INSERT INTO Tickets ({string.Join(",", given.Keys)}) VALUES ({string.Join(",", given.Keys.Select(k => "@" + k))})";
            foreach (var kv in given) { var p = cmd.CreateParameter(); p.ParameterName = "@" + kv.Key; p.Value = kv.Value; cmd.Parameters.Add(p); }
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var st in Enum.GetValues<TicketStatus>())
            await OldRow($"old {st}", (int)st, (int)TicketCategory.CallOut, st is TicketStatus.Closed or TicketStatus.Resolved ? "2026-09-02 10:00:00" : "");
        await OldRow("old general", (int)TicketStatus.InProgress, (int)TicketCategory.General);
        await conn.CloseAsync();
        await HarnessDb.MigrateAsync(db);
    }

    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }
    async Task<Ticket> Old(string subject) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Subject == subject); }
    string[] Keys(Ticket t, TenantRole r, string me) => TicketService.ActionsFor(t, r, me).Select(a => a.Key).ToArray();

    Console.WriteLine("== call outs already on file ==");
    if (upgrade) {
    foreach (var st in new[] { TicketStatus.Open, TicketStatus.InProgress, TicketStatus.OnHold, TicketStatus.Resolved })
    {
        var o = await Old($"old {st}");
        Check($"an old {st} call out is at Call out booked, on the bench, Open",
            o.Stage == "callout-booked" && o.Lane == (int)WorkLane.Bench && o.Status == TicketStatus.Open && o.ClosedUtc == null,
            $"{o.Stage} / lane {o.Lane} / {o.Status} / closed {o.ClosedUtc}");
    }
    var oldClosed = await Old("old Closed");
    Check("an old Closed one is closed, and keeps when it closed", oldClosed.Stage == "closed" && oldClosed.Lane == (int)WorkLane.Done && oldClosed.ClosedUtc != null);
    Check("an old General ticket joins ITS route, not the call out's", (await Old("old general")).Stage == "general-open");
    Check("the old ones are offered Work done", Keys(await Old("old InProgress"), T, Sue).Contains("callout-work-done"));
    }

    async Task<Ticket> Book(string subject)
        => await tickets.CreateAsync(1, subject, "Printer jams on every page.", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.CallOut,
            extra: x => { x.SiteAddress = "12 Long Street, Cape Town"; x.AppointmentUtc = DateTime.UtcNow.AddDays(1); });
    async Task ToWorkDone(int id) =>
        await tickets.ActAsync(1, id, Sue, T, "callout-work-done", new StageInput { Details = "Cleared the paper path.", Minutes = 45 });

    // ---- booked, then Work done ----
    Console.WriteLine();
    Console.WriteLine("== booked, then Work done ==");
    var job = await Book("Printer jams");
    Check("a new call out starts at Call out booked, on the bench, Open",
        job.Stage == "callout-booked" && job.Lane == (int)WorkLane.Bench && job.Status == TicketStatus.Open);
    var first = Keys(job, T, Sue);
    Check("Work done is the step forward — \"the next stage should be work done\" — and Reschedule beside it (2026-10-05)",
        first.SequenceEqual(new[] { "callout-work-done", Workflow.Reschedule }), string.Join(",", first));
    Check("a colleague is offered nothing", Keys(job, T, Bob).Length == 0);
    Check("Work done asks the work AND the time (Clint's choice)",
        Workflow.Action("callout-work-done")!.Fields.SequenceEqual(new[] { StageField.Details, StageField.Minutes }));
    var noWork = await tickets.ActAsync(1, job.Id, Sue, T, "callout-work-done", new StageInput { Minutes = 45 });
    Check("Work done without saying what was done is REFUSED", noWork.Contains("was done"), noWork);
    var noTime = await tickets.ActAsync(1, job.Id, Sue, T, "callout-work-done", new StageInput { Details = "x" });
    Check("...without the time is REFUSED", noTime.Contains("time"), noTime);
    await tickets.UpdateAsync(1, job.Id, Sam, S, TicketStatus.Closed, null, null, null);
    Check("the old status dropdown cannot close it round the route", (await Get(job.Id)).Stage == "callout-booked");
    Check("Work done → Work done",
        await tickets.ActAsync(1, job.Id, Sue, T, "callout-work-done", new StageInput { Details = "Cleared the paper path.\nReplaced the pickup roller.", Minutes = 45 }) == ""
        && (await Get(job.Id)) is { Stage: "callout-work-done", TimeSpentMinutes: 45 });
    await using (var db = new PlatformDb(options))
    {
        var w = await db.TicketNotes.Where(x => x.TicketId == job.Id && x.IsWorkDone).ToListAsync();
        Check("...one entry, the Work done form, marked as the work done",
            w.Count == 1 && w[0].FormTitle == "Work done" && w[0].MovedTo == "callout-work-done");
        Check("...and the invoice copy is the work alone, both lines, no time",
            w.Count == 1 && TicketService.WorkDoneText(w[0].Body) == "Cleared the paper path.\nReplaced the pickup roller.",
            w.Count == 1 ? TicketService.WorkDoneText(w[0].Body).Replace("\n", " | ") : "");
    }
    var three = Keys(await Get(job.Id), T, Sue);
    Check("after Work done, exactly the three options, in Clint's order",
        three.SequenceEqual(new[] { "callout-invoice", "callout-paid-onsite", Workflow.ToWorkshop }), string.Join(",", three));

    // ---- reschedule (Clint, 2026-10-05): "Call outs need the option to reschedule" — a new date and
    //      time and why, at Call out booked; the people on it, the office, a supervisor ----
    Console.WriteLine();
    Console.WriteLine("== reschedule ==");
    var moved = await Book("Moved call out");
    // Read back, not the value held in memory: PostgreSQL keeps a time to the microsecond, .NET to the tenth of
    // one, so a time made by DateTime.UtcNow is not quite what was stored (HANDOVER §118). A real appointment
    // comes from a date-and-time box and has no fractions either way.
    var wasAt = (await Get(moved.Id)).AppointmentUtc!.Value;
    Check("Reschedule asks the new date and time, then why",
        Workflow.Action(Workflow.Reschedule)!.Fields.SequenceEqual(new[] { StageField.Appointment, StageField.Reason }));
    Check("the office is offered it — the customer phones the office to move it", Keys(moved, OA, Olive).Contains(Workflow.Reschedule), string.Join(",", Keys(moved, OA, Olive)));
    Check("...and a supervisor", Keys(moved, S, Sam).Contains(Workflow.Reschedule));
    Check("...but not a technician who is not on it", !Keys(moved, T, Bob).Contains(Workflow.Reschedule));
    Check("...and pressing it anyway is REFUSED",
        (await tickets.ActAsync(1, moved.Id, Bob, T, Workflow.Reschedule, new StageInput { AppointmentUtc = DateTime.UtcNow.AddDays(3), Reason = "x" })).Length > 0);
    var noWhen = await tickets.ActAsync(1, moved.Id, Sue, T, Workflow.Reschedule, new StageInput { Reason = "Customer asked." });
    Check("without the new date and time it is REFUSED", noWhen.Contains("date and time"), noWhen);
    var past = await tickets.ActAsync(1, moved.Id, Sue, T, Workflow.Reschedule, new StageInput { AppointmentUtc = DateTime.UtcNow.AddHours(-2), Reason = "Customer asked." });
    Check("a time that has passed is REFUSED", past.Contains("passed"), past);
    var same = await tickets.ActAsync(1, moved.Id, Sue, T, Workflow.Reschedule, new StageInput { AppointmentUtc = wasAt, Reason = "Customer asked." });
    Check("the time it is already booked for is REFUSED", same.Contains("already"), same);
    var noWhy = await tickets.ActAsync(1, moved.Id, Sue, T, Workflow.Reschedule, new StageInput { AppointmentUtc = DateTime.UtcNow.AddDays(3) });
    Check("without saying why it is REFUSED", noWhy.Contains("why"), noWhy);
    Check("nothing refused changed the appointment", (await Get(moved.Id)).AppointmentUtc == wasAt);

    var newAt = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(4).AddHours(8), DateTimeKind.Utc);
    Check("with both, it is moved — and STAYS at Call out booked, with Sue",
        await tickets.ActAsync(1, moved.Id, Sue, T, Workflow.Reschedule, new StageInput { AppointmentUtc = newAt, Reason = " Customer is away on Thursday. " }) == ""
        && (await Get(moved.Id)) is { Stage: "callout-booked", Lane: (int)WorkLane.Bench, Status: TicketStatus.Open, AssignedTo: Sue } && (await Get(moved.Id)).AppointmentUtc == newAt,
        $"{(await Get(moved.Id)).Stage} / {(await Get(moved.Id)).AppointmentUtc:u}");
    await using (var db = new PlatformDb(options))
    {
        var form = await db.TicketNotes.Where(x => x.TicketId == moved.Id && x.FormTitle == "Reschedule").ToListAsync();
        Check("the story has ONE Reschedule form: the new time, what it was, and why",
            form.Count == 1 && form[0].Body.Contains("New appointment: " + TicketService.Appointment(newAt))
            && form[0].Body.Contains("Was: " + TicketService.Appointment(wasAt)) && form[0].Body.Contains("Reason: Customer is away on Thursday."),
            form.Count == 1 ? form[0].Body.Replace("\n", " | ") : $"{form.Count} forms");
        Check("...with no arrow: it moved the appointment, not the job", form.Count == 1 && form[0].MovedTo == "" && form[0].Stage == "callout-booked");
    }
    var again = DateTime.SpecifyKind(newAt.AddDays(1), DateTimeKind.Utc);
    Check("the office may move it again, and Work done is still the step forward",
        await tickets.ActAsync(1, moved.Id, Olive, OA, Workflow.Reschedule, new StageInput { AppointmentUtc = again, Reason = "Technician off sick." }) == ""
        && (await Get(moved.Id)).AppointmentUtc == again && Keys(await Get(moved.Id), T, Sue).First() == "callout-work-done");
    await ToWorkDone(moved.Id);
    Check("after Work done it is no longer offered — somebody has been out", !Keys(await Get(moved.Id), T, Sue).Contains(Workflow.Reschedule));
    Check("...and pressing it is REFUSED",
        (await tickets.ActAsync(1, moved.Id, Sue, T, Workflow.Reschedule, new StageInput { AppointmentUtc = again.AddDays(1), Reason = "x" })).Length > 0);

    // ---- route 1: completed, to be invoiced ----
    Console.WriteLine();
    Console.WriteLine("== completed, to be invoiced ==");
    Check("it asks only for the amount — the time was given at Work done",
        Workflow.Action("callout-invoice")!.Fields.SequenceEqual(new[] { StageField.Amount }));
    Check("without the amount it is REFUSED", (await tickets.ActAsync(1, job.Id, Sue, T, "callout-invoice")).Length > 0);
    Check("with it → To be invoiced, the SAME stage the workshop uses, in the billing queue",
        await tickets.ActAsync(1, job.Id, Sue, T, "callout-invoice", new StageInput { Amount = 650 }) == ""
        && (await Get(job.Id)) is { Stage: "to-be-invoiced", Lane: (int)WorkLane.Office, AssignedTo: "", InvoiceAmount: 650, WorkedBy: Sue });
    Check("it is in the billing queue", (await tickets.ListAsync(1, "billing", "", Olive, OA)).Any(t => t.Id == job.Id));
    Check("the office is NOT offered Machine collected — there is no machine",
        !Keys(await Get(job.Id), OA, Olive).Contains(TicketService.CollectedKey), string.Join(",", Keys(await Get(job.Id), OA, Olive)));
    Check("...and pressing it anyway is REFUSED", (await tickets.ActAsync(1, job.Id, Olive, OA, TicketService.CollectedKey)).Length > 0);
    await tickets.ActAsync(1, job.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 650, Reference = "INV-300" });
    Check("Client invoiced → Awaiting payment", (await Get(job.Id)) is { Stage: "awaiting-payment", InvoiceNumber: "INV-300" });
    Check("Payment received CLOSES it — not Awaiting collection, as a workshop job would be",
        await tickets.ActAsync(1, job.Id, Olive, OA, "payment-received") == ""
        && (await Get(job.Id)) is { Stage: "closed", Status: TicketStatus.Closed } && (await Get(job.Id)).ClosedUtc != null,
        (await Get(job.Id)).Stage);

    // ---- route 2: paid on site ----
    Console.WriteLine();
    Console.WriteLine("== invoiced, paid on site ==");
    var cash = await Book("Paid in cash");
    await ToWorkDone(cash.Id);
    Check("Invoiced — paid on site → its two options, Paid cash and Paid card machine",
        await tickets.ActAsync(1, cash.Id, Sue, T, "callout-paid-onsite") == ""
        && (await Get(cash.Id)).Stage == "paid-onsite"
        && Keys(await Get(cash.Id), T, Sue).SequenceEqual(new[] { "paid-cash", "paid-card" }),
        string.Join(",", Keys(await Get(cash.Id), T, Sue)));
    Check("Paid cash asks for the amount", Workflow.Action("paid-cash")!.Fields.SequenceEqual(new[] { StageField.Amount }));
    Check("...and without it is REFUSED", (await tickets.ActAsync(1, cash.Id, Sue, T, "paid-cash")).Length > 0);
    Check("Paid cash → Paid on site, to be closed, with the office",
        await tickets.ActAsync(1, cash.Id, Sue, T, "paid-cash", new StageInput { Amount = 480 }) == ""
        && (await Get(cash.Id)) is { Stage: "paid-onsite-to-close", Lane: (int)WorkLane.Office, AssignedTo: "", InvoiceAmount: 480, PaymentMethod: "cash" });
    var cashFacts = TicketService.WorkflowFacts(await Get(cash.Id));
    Check("the job's facts say it was paid on site, in cash",
        cashFacts.Any(f => f.Label == "Paid on site" && f.Value == "480.00 · cash") && !cashFacts.Any(f => f.Label == "To invoice"),
        string.Join("; ", cashFacts.Select(f => $"{f.Label}={f.Value}")));
    Check("the technician cannot close it", (await tickets.ActAsync(1, cash.Id, Sue, T, "close-paid-onsite")).Length > 0);
    Check("the office closes it (Clint's choice)",
        Keys(await Get(cash.Id), OA, Olive).Contains("close-paid-onsite")
        && await tickets.ActAsync(1, cash.Id, Olive, OA, "close-paid-onsite") == "" && (await Get(cash.Id)).Status == TicketStatus.Closed);

    var card = await Book("Paid by card");
    await ToWorkDone(card.Id);
    await tickets.ActAsync(1, card.Id, Sue, T, "callout-paid-onsite");
    Check("Paid card machine asks for the amount AND the reference",
        Workflow.Action("paid-card")!.Fields.SequenceEqual(new[] { StageField.Amount, StageField.Reference }));
    var noRef = await tickets.ActAsync(1, card.Id, Sue, T, "paid-card", new StageInput { Amount = 900 });
    Check("...without the reference it is REFUSED, and says which reference", noRef.Contains("card machine"), noRef);
    Check("with both → Paid on site, to be closed",
        await tickets.ActAsync(1, card.Id, Sue, T, "paid-card", new StageInput { Amount = 900, Reference = " 004512 " }) == ""
        && (await Get(card.Id)) is { Stage: "paid-onsite-to-close", PaymentMethod: "card", PaymentReference: "004512", InvoiceAmount: 900, InvoiceNumber: "" });
    await using (var db = new PlatformDb(options))
    {
        var form = await db.TicketNotes.Where(x => x.TicketId == card.Id && x.FormTitle == "Paid card machine").FirstOrDefaultAsync();
        Check("the timeline has the card payment as its form",
            form != null && form.Body.Contains("Amount paid by card: 900.00") && form.Body.Contains("Card machine reference: 004512"),
            form?.Body.Replace("\n", " | ") ?? "none");
    }

    // ---- route 3: needs workshop work ----
    Console.WriteLine();
    Console.WriteLine("== needs workshop work ==");
    var ws = await Book("Laptop will not boot on site");
    await ToWorkDone(ws.Id);
    var noPhotos = await tickets.ActAsync(1, ws.Id, Sue, T, Workflow.ToWorkshop, new StageInput { AssetMake = "HP" });
    Check("without the three photos in it is REFUSED, as a counter booking is", noPhotos.Contains("photos"), noPhotos);
    for (int i = 0; i < 3; i++)
        await photos.AddAsync(1, RemoteSupport.Tools.TestImages.Jpeg(), $"in-{i}.jpg", PhotoKind.Before, Sue, ws.Id, "");
    Check("with them, and the machine details, it goes — the details are optional, as at the counter",
        await tickets.ActAsync(1, ws.Id, Sue, T, Workflow.ToWorkshop, new StageInput
        {
            AssetMake = " HP ", AssetModel = "ProBook 450", AssetSerial = "5CD123", Accessories = "Charger", DevicePasswordEnc = "ENCRYPTED"
        }) == "");
    var now = await Get(ws.Id);
    Check("the SAME ticket is now a Workshop job at Booked in, with the same number and technician",
        now is { Category: TicketCategory.Workshop, Stage: "awaiting-diagnosis", Lane: (int)WorkLane.Bench, AssignedTo: Sue } && now.Number == ws.Number,
        $"{now.Category} / {now.Stage} / #{now.Number}");
    Check("...with the machine details recorded, and the password kept encrypted",
        now is { AssetMake: "HP", AssetModel: "ProBook 450", AssetSerial: "5CD123", Accessories: "Charger", DevicePasswordProtected: true, DevicePasswordEnc: "ENCRYPTED" });
    Check("...and its site still on it", now.SiteAddress == "12 Long Street, Cape Town");
    await using (var db = new PlatformDb(options))
    {
        var notes = await db.TicketNotes.Where(x => x.TicketId == ws.Id).OrderBy(x => x.Id).ToListAsync();
        Check("its story carries on: Work done, then the Needs workshop work form",
            notes.Any(x => x.FormTitle == "Work done") && notes.Last().FormTitle == "Needs workshop work"
            && notes.Last().MovedTo == "awaiting-diagnosis" && notes.Last().Body.Contains("Photos in: 3"),
            notes.Last().Body.Replace("\n", " | "));
    }
    Check("it now follows the workshop workflow: Inspection findings is next",
        Keys(now, T, Sue).Contains("inspection") && !Keys(now, T, Sue).Contains("callout-work-done"));
    await tickets.ActAsync(1, ws.Id, Sue, T, "inspection", new StageInput { Details = "SSD failed." });
    await tickets.ActAsync(1, ws.Id, Sue, T, "repairable");
    await tickets.ActAsync(1, ws.Id, Sue, T, "solved-invoice", new StageInput { Details = "Replaced the SSD.", Minutes = 90, Amount = 1800 });
    await tickets.ActAsync(1, ws.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 1800, Reference = "INV-301" });
    Check("...all the way: paid, it waits to be COLLECTED like any workshop machine",
        await tickets.ActAsync(1, ws.Id, Olive, OA, "payment-received") == "" && (await Get(ws.Id)).Stage == "awaiting-collection");

    var noPw = await Book("No password machine");
    await ToWorkDone(noPw.Id);
    for (int i = 0; i < 3; i++)
        await photos.AddAsync(1, RemoteSupport.Tools.TestImages.Jpeg(), $"in-{i}.jpg", PhotoKind.Before, Sue, noPw.Id, "");
    Check("with nothing but the photos it still books in, not password protected",
        await tickets.ActAsync(1, noPw.Id, Sue, T, Workflow.ToWorkshop) == "" && (await Get(noPw.Id)) is { Category: TicketCategory.Workshop, DevicePasswordProtected: false });

    // ---- the edges ----
    Console.WriteLine();
    Console.WriteLine("== the edges ==");
    var edge = await Book("Edges");
    Check("the office cannot do the technician's Work done", (await tickets.ActAsync(1, edge.Id, Olive, OA, "callout-work-done", new StageInput { Details = "x", Minutes = 5 })).Length > 0);
    Check("a workshop step on a call out is REFUSED", (await tickets.ActAsync(1, edge.Id, Sue, T, "inspection", new StageInput { Details = "x" })).Length > 0);
    Check("the office may abandon a call out, as any workflow job",
        await tickets.ActAsync(1, edge.Id, Olive, OA, "abandon", new StageInput { Reason = "Customer cancelled." }) == "" && (await Get(edge.Id)).Stage == "abandoned");
    var shop = await tickets.CreateAsync(1, "Counter booking", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
    Check("a workshop booking still starts at Booked in", shop.Stage == "awaiting-diagnosis");
    Check("...and is never offered a call out step", !Keys(shop, T, Sue).Any(k => k.StartsWith("callout") || k == Workflow.ToWorkshop));
    var general = await tickets.CreateAsync(1, "Email problem", "", Olive, TicketSource.Manual);
    Check("a General ticket starts on its own route, with none of the call out's steps", general.Stage == "general-open" && !Keys(general, S, Sam).Any(k => k.StartsWith("callout")));
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
