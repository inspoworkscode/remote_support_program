// =====================================================================================
// deliverycheck - proves Delivery (Clint, 2026-09-30): a finished workshop machine taken to the
// customer by a technician instead of collected, then "pretty much the call out process".
//
//   "a machine might be booked in at our offices, but instead of it being collected, it needs to
//   be delivered so this needs to be added to the workflow after the work is completed and then
//   pretty much follow the call out process."
//
// His answers: paid BEFORE or ON delivery, both; always a technician; work done is recorded there
// ("a printer needs to be installed"); the photos out are taken at the office. Every branch is
// walked through TicketService.ActAsync, never a page, and each way the billing can stand when the
// machine leaves is asked about: not yet invoiced, invoiced and unpaid, paid, and the quote route.
//
//   dotnet run -c Debug --project tools/deliverycheck/deliverycheck.csproj
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

var dir = Path.Combine(Path.GetTempPath(), "deliverycheck-" + Guid.NewGuid().ToString("N")[..8]);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "platform.db");
Console.WriteLine("deliverycheck - delivery instead of collection, every branch, through the service");
Console.WriteLine();

const string Sue = "Sue Tech", Bob = "Bob Tech", Olive = "Olive Office";
const TenantRole T = TenantRole.Technician, OA = TenantRole.Billing;
const string Send = TicketService.DeliverKey;

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={dbPath};Pooling=False").Options;
    await using (var db = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(db);
        await Seed.OldTenant(db, 1, "d", "Deliver IT", "kd");
    }
    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }
    string[] Keys(Ticket t, TenantRole r, string me) => TicketService.ActionsFor(t, r, me).Select(a => a.Key).ToArray();
    async Task PhotosOut(int id) { for (int i = 0; i < 3; i++) await photos.AddAsync(1, Jpeg(), $"out-{i}.jpg", PhotoKind.After, Olive, id, ""); }

    // A workshop machine repaired and sent to the office to be invoiced.
    async Task<Ticket> Repaired(string subject, decimal amount = 650)
    {
        var t = await tickets.CreateAsync(1, subject, "Needs a new SSD.", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
        await tickets.ActAsync(1, t.Id, Sue, T, "inspection", new StageInput { Details = "SSD failed." });
        await tickets.ActAsync(1, t.Id, Sue, T, "repairable");
        await tickets.ActAsync(1, t.Id, Sue, T, "solved-invoice", new StageInput { Details = "Replaced the SSD.", Minutes = 60, Amount = amount });
        return await Get(t.Id);
    }
    async Task Paid(int id)
    {
        await tickets.ActAsync(1, id, Olive, OA, "client-invoiced", new StageInput { Amount = 650, Reference = "INV-1" });
        await tickets.ActAsync(1, id, Olive, OA, "payment-received");
    }
    async Task<string> Deliver(int id) =>
        await tickets.ActAsync(1, id, Sue, T, "delivered", new StageInput { Details = "Installed the printer on it.", Minutes = 40 });

    // ---- 1: paid before delivery ----
    Console.WriteLine("== paid before delivery ==");
    var paid = await Repaired("Paid before");
    await Paid(paid.Id);
    paid = await Get(paid.Id);
    Check("paid, it waits for the machine to leave", paid.Stage == "awaiting-collection");
    var ko = Keys(paid, OA, Olive);
    Check("Out for delivery is offered beside Machine collected, to the office",
        ko.Contains(TicketService.CollectedKey) && ko.Contains(Send), string.Join(",", ko));
    Check("...and to the technician who did the work", Keys(paid, T, Sue).Contains(Send));
    Check("...but not to a technician who is not on the job", !Keys(paid, T, Bob).Contains(Send));
    Check("a technician not on the job who presses it anyway is REFUSED", (await tickets.ActAsync(1, paid.Id, Bob, T, Send)).Length > 0);
    var noPics = await tickets.ActAsync(1, paid.Id, Olive, OA, Send);
    Check("without the photos out it is REFUSED, and says they are taken at the office", noPics.Contains("photos") && noPics.Contains("office"), noPics);
    await PhotosOut(paid.Id);
    Check("with them, the office sends it out", await tickets.ActAsync(1, paid.Id, Olive, OA, Send) == "", (await Get(paid.Id)).Stage);
    paid = await Get(paid.Id);
    Check("...Out for delivery, on the bench, with the technician who did the work (always a technician)",
        paid is { Stage: "out-for-delivery", Lane: (int)WorkLane.Bench, AssignedTo: Sue, StageReturn: "awaiting-collection" } && paid.CollectedUtc == null,
        $"{paid.Stage} / {paid.AssignedTo} / return {paid.StageReturn}");
    Check("...and neither Collected nor Out for delivery is offered again",
        !Keys(paid, OA, Olive).Contains(Send) && !Keys(paid, OA, Olive).Contains(TicketService.CollectedKey));
    Check("out for delivery, the technician has Delivered and Not delivered",
        Keys(paid, T, Sue).SequenceEqual(new[] { "delivered", "not-delivered" }), string.Join(",", Keys(paid, T, Sue)));
    Check("Delivered asks the work done AND the time, like a call out",
        Workflow.Action("delivered")!.Fields.SequenceEqual(new[] { StageField.Details, StageField.Minutes }));
    Check("...without the work it is REFUSED", (await tickets.ActAsync(1, paid.Id, Sue, T, "delivered", new StageInput { Minutes = 40 })).Length > 0);
    Check("...without the time it is REFUSED", (await tickets.ActAsync(1, paid.Id, Sue, T, "delivered", new StageInput { Details = "x" })).Length > 0);
    Check("the office cannot say it was delivered", (await tickets.ActAsync(1, paid.Id, Olive, OA, "delivered", new StageInput { Details = "x", Minutes = 1 })).Length > 0);
    Check("Delivered → Delivered", await Deliver(paid.Id) == "" && (await Get(paid.Id)).Stage == "delivered");
    paid = await Get(paid.Id);
    Check("...the machine counts as gone: handed over, by whom, and marked delivered",
        paid.CollectedUtc != null && paid.CollectedBy == Sue && paid.Delivered);
    // Found walking it in the browser: the delivery's 40 minutes replaced the bench's hour.
    Check("...and the delivery's time is ADDED to the bench's (60 + 40), not put in its place",
        paid.TimeSpentMinutes == 100, $"{paid.TimeSpentMinutes} min");
    await using (var db = new PlatformDb(options))
    {
        var w = await db.TicketNotes.Where(x => x.TicketId == paid.Id && x.FormTitle == "Delivered — work done").ToListAsync();
        Check("...its form is in the story, and counts as work done for the invoice",
            w.Count == 1 && w[0].IsWorkDone && TicketService.WorkDoneText(w[0].Body) == "Installed the printer on it.",
            w.Count == 1 ? w[0].Body.Replace("\n", " | ") : "none");
    }
    Check("delivered, the three ways it ends, like a call out",
        Keys(paid, T, Sue).SequenceEqual(new[] { "delivery-invoice", "delivery-paid", "delivery-done" }), string.Join(",", Keys(paid, T, Sue)));
    Check("Nothing more to invoice → back to where the billing was",
        await tickets.ActAsync(1, paid.Id, Sue, T, "delivery-done") == ""
        && (await Get(paid.Id)) is { Stage: "awaiting-collection", Lane: (int)WorkLane.Office, AssignedTo: "" });
    Check("...where the office may close it at once — it is not waiting to be collected",
        await tickets.ActAsync(1, paid.Id, Olive, OA, "close") == "" && (await Get(paid.Id)).Status == TicketStatus.Closed);
    var facts = TicketService.WorkflowFacts(await Get(paid.Id));
    Check("the job's facts say Delivered, not Collected",
        facts.Any(f => f.Label == "Delivered") && !facts.Any(f => f.Label == "Collected"), string.Join("; ", facts.Select(f => f.Label)));

    // ---- 2: paid on delivery, nothing invoiced yet ----
    Console.WriteLine();
    Console.WriteLine("== paid on delivery ==");
    var onDel = await Repaired("Paid on delivery");
    await PhotosOut(onDel.Id);
    Check("from To be invoiced, the technician sends it out themselves",
        Keys(onDel, T, Sue).Contains(Send) && await tickets.ActAsync(1, onDel.Id, Sue, T, Send) == ""
        && (await Get(onDel.Id)) is { Stage: "out-for-delivery", AssignedTo: Sue, StageReturn: "to-be-invoiced" });
    await Deliver(onDel.Id);
    Check("Invoiced — paid on delivery → Paid cash or Paid card machine",
        await tickets.ActAsync(1, onDel.Id, Sue, T, "delivery-paid") == ""
        && Keys(await Get(onDel.Id), T, Sue).SequenceEqual(new[] { "paid-cash", "paid-card" }), string.Join(",", Keys(await Get(onDel.Id), T, Sue)));
    Check("Paid card machine → to the office to close",
        await tickets.ActAsync(1, onDel.Id, Sue, T, "paid-card", new StageInput { Amount = 950, Reference = "7731" }) == ""
        && (await Get(onDel.Id)) is { Stage: "paid-onsite-to-close", Lane: (int)WorkLane.Office, InvoiceAmount: 950, PaymentMethod: "card" });
    Check("the facts say paid on delivery",
        TicketService.WorkflowFacts(await Get(onDel.Id)).Any(f => f.Label == "Paid on delivery" && f.Value.Contains("950.00")),
        string.Join("; ", TicketService.WorkflowFacts(await Get(onDel.Id)).Select(f => $"{f.Label}={f.Value}")));
    Check("the office closes it",
        await tickets.ActAsync(1, onDel.Id, Olive, OA, "close-paid-onsite") == "" && (await Get(onDel.Id)).Status == TicketStatus.Closed);

    // ---- 3: invoiced and not yet paid ----
    Console.WriteLine();
    Console.WriteLine("== an invoice already out, unpaid ==");
    var unpaid = await Repaired("Invoice out");
    await tickets.ActAsync(1, unpaid.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 650, Reference = "INV-2" });
    await PhotosOut(unpaid.Id);
    Check("Awaiting payment still offers Out for delivery", Keys(await Get(unpaid.Id), OA, Olive).Contains(Send));
    await tickets.ActAsync(1, unpaid.Id, Olive, OA, Send);
    await Deliver(unpaid.Id);
    var ku = Keys(await Get(unpaid.Id), T, Sue);
    Check("To be invoiced is NOT offered while that invoice is out",
        !ku.Contains("delivery-invoice") && ku.Contains("delivery-paid") && ku.Contains("delivery-done"), string.Join(",", ku));
    var second = await tickets.ActAsync(1, unpaid.Id, Sue, T, "delivery-invoice", new StageInput { Amount = 100 });
    Check("...and pressing it anyway is REFUSED, saying why", second.Contains("unpaid"), second);
    Check("Nothing more to invoice → back to Awaiting payment, invoice kept",
        await tickets.ActAsync(1, unpaid.Id, Sue, T, "delivery-done") == ""
        && (await Get(unpaid.Id)) is { Stage: "awaiting-payment", InvoiceNumber: "INV-2", InvoiceAmount: 650 });
    Check("Payment received CLOSES it — the machine is already with the customer",
        await tickets.ActAsync(1, unpaid.Id, Olive, OA, "payment-received") == "" && (await Get(unpaid.Id)).Stage == "closed");

    // ---- 4: the delivery's work goes on the invoice ----
    Console.WriteLine();
    Console.WriteLine("== the delivery's work invoiced ==");
    var addOn = await Repaired("Add to invoice", 650);
    await PhotosOut(addOn.Id);
    await tickets.ActAsync(1, addOn.Id, Sue, T, Send);
    await Deliver(addOn.Id);
    Check("To be invoiced asks the amount", Workflow.Action("delivery-invoice")!.Fields.SequenceEqual(new[] { StageField.Amount }));
    Check("not yet invoiced: the delivery is ADDED to the invoice (650 + 300)",
        await tickets.ActAsync(1, addOn.Id, Sue, T, "delivery-invoice", new StageInput { Amount = 300 }) == ""
        && (await Get(addOn.Id)) is { Stage: "to-be-invoiced", Lane: (int)WorkLane.Office, InvoiceAmount: 950, WorkedBy: Sue },
        $"{(await Get(addOn.Id)).Stage} / {(await Get(addOn.Id)).InvoiceAmount}");
    await tickets.ActAsync(1, addOn.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 950, Reference = "INV-3" });
    Check("...invoiced and paid, it closes",
        await tickets.ActAsync(1, addOn.Id, Olive, OA, "payment-received") == "" && (await Get(addOn.Id)).Stage == "closed");

    var afterPaid = await Repaired("Paid, then delivery work");
    await Paid(afterPaid.Id);
    await PhotosOut(afterPaid.Id);
    await tickets.ActAsync(1, afterPaid.Id, Olive, OA, Send);
    await Deliver(afterPaid.Id);
    Check("already paid: the delivery's work is an invoice of its OWN",
        await tickets.ActAsync(1, afterPaid.Id, Sue, T, "delivery-invoice", new StageInput { Amount = 300 }) == ""
        && (await Get(afterPaid.Id)) is { Stage: "to-be-invoiced", InvoiceAmount: 300, InvoiceNumber: "" });

    // ---- 5: not delivered ----
    Console.WriteLine();
    Console.WriteLine("== not delivered ==");
    var back = await Repaired("Nobody home");
    await Paid(back.Id);
    await PhotosOut(back.Id);
    await tickets.ActAsync(1, back.Id, Olive, OA, Send);
    Check("Not delivered without saying why is REFUSED", (await tickets.ActAsync(1, back.Id, Sue, T, "not-delivered")).Length > 0);
    Check("with why → back to the office where it was, not gone",
        await tickets.ActAsync(1, back.Id, Sue, T, "not-delivered", new StageInput { Reason = "Nobody at the office." }) == ""
        && (await Get(back.Id)) is { Stage: "awaiting-collection", Lane: (int)WorkLane.Office, Delivered: false } && (await Get(back.Id)).CollectedUtc == null);
    var kb = Keys(await Get(back.Id), OA, Olive);
    Check("...and it can be collected or sent out again", kb.Contains(Send) && kb.Contains(TicketService.CollectedKey), string.Join(",", kb));
    Check("...but not closed, because it has not left", (await tickets.ActAsync(1, back.Id, Olive, OA, "close")).Length > 0);

    // ---- 6: the quote route ----
    Console.WriteLine();
    Console.WriteLine("== the quote route ==");
    var q = await tickets.CreateAsync(1, "Quote job", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
    await using (var db = new PlatformDb(options))
    {
        db.QuoteLines.Add(new QuoteLine { TenantId = 1, TicketId = q.Id, ItemName = "SSD 1TB", Quantity = 1 });
        await db.SaveChangesAsync();
    }
    await tickets.ActAsync(1, q.Id, Sue, T, "inspection", new StageInput { Details = "SSD failed." });
    await tickets.ActAsync(1, q.Id, Sue, T, "repairable");
    await tickets.ActAsync(1, q.Id, Sue, T, "quote-needed");
    await tickets.ActAsync(1, q.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = "parts@example.com" });
    await tickets.ActAsync(1, q.Id, Olive, OA, "quote-sent", new StageInput { Amount = 2000, Reference = "Q-1" });
    await tickets.ActAsync(1, q.Id, Olive, OA, "quote-accepted");
    await tickets.ActAsync(1, q.Id, Olive, OA, "quote-invoiced", new StageInput { Amount = 2000, Reference = "INV-4" });
    await tickets.ActAsync(1, q.Id, Olive, OA, "order-placed");
    await tickets.ActAsync(1, q.Id, Olive, OA, "parts-received");
    await tickets.ActAsync(1, q.Id, Sue, T, "parts-fitted");
    Check("parts fitted, it can go out for delivery",
        (await Get(q.Id)).Stage == "parts-fitted" && Keys(await Get(q.Id), T, Sue).Contains(Send), (await Get(q.Id)).Stage);
    await PhotosOut(q.Id);
    await tickets.ActAsync(1, q.Id, Sue, T, Send);
    await Deliver(q.Id);
    await tickets.ActAsync(1, q.Id, Sue, T, "delivery-done");
    Check("delivered, nothing more to invoice → Parts fitted, and Complete closes it outright",
        (await Get(q.Id)).Stage == "parts-fitted" && await tickets.ActAsync(1, q.Id, Olive, OA, "complete") == "" && (await Get(q.Id)).Stage == "closed",
        (await Get(q.Id)).Stage);

    // ---- the edges ----
    Console.WriteLine();
    Console.WriteLine("== the edges ==");
    var callout = await tickets.CreateAsync(1, "Call out", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.CallOut);
    await tickets.ActAsync(1, callout.Id, Sue, T, "callout-work-done", new StageInput { Details = "x", Minutes = 5 });
    await tickets.ActAsync(1, callout.Id, Sue, T, "callout-invoice", new StageInput { Amount = 100 });
    Check("a call out is never offered Out for delivery — there is no machine", !Keys(await Get(callout.Id), OA, Olive).Contains(Send));
    Check("...and pressing it anyway is REFUSED", (await tickets.ActAsync(1, callout.Id, Olive, OA, Send)).Length > 0);
    var early = await tickets.CreateAsync(1, "Not finished", "", Olive, TicketSource.Manual, assignedTo: Sue, category: TicketCategory.Workshop);
    Check("a machine still being worked on cannot go out", (await tickets.ActAsync(1, early.Id, Sue, T, Send)).Length > 0 && !Keys(early, T, Sue).Contains(Send));
    var collected = await Repaired("Collected the usual way");
    await PhotosOut(collected.Id);
    Check("Machine collected still works as it did",
        await tickets.ActAsync(1, collected.Id, Olive, OA, TicketService.CollectedKey) == ""
        && (await Get(collected.Id)) is { Delivered: false } && (await Get(collected.Id)).CollectedUtc != null);
    Check("...and once collected, it cannot also go out for delivery", (await tickets.ActAsync(1, collected.Id, Olive, OA, Send)).Length > 0);
    var gone = await Repaired("Customer moved");
    await PhotosOut(gone.Id);
    await tickets.ActAsync(1, gone.Id, Sue, T, Send);
    Check("the office may abandon a job that is out for delivery",
        await tickets.ActAsync(1, gone.Id, Olive, OA, "abandon", new StageInput { Reason = "Customer moved away." }) == "" && (await Get(gone.Id)).Stage == "abandoned");
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { try { Directory.Delete(dir, true); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

static byte[] Jpeg() => RemoteSupport.Tools.TestImages.Jpeg();

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
