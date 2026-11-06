// =====================================================================================
// routecheck - proves the last three routes (Clint, 2026-09-27): Walk in, Remote support and
// General, and the two things that came with them — linking any ticket to a client at any stage,
// and a remote session only joining a ticket nobody has decided on yet.
//
//   Walk in: "Once the details are captured, there should be two options, Repaired at Counter or
//   Needs to be booked in. If repaired at counter is selected, Work done and Time spent need to
//   appear. After that, it's To be Invoiced, Invoiced Paid at Counter, or No Invoice."
//
//   Remote support: "After the session is completed, it should have To be Invoiced, Do not Invoice
//   (SLA Client), Do not Invoice or link to Parent Ticket, with the option to link it to another
//   ticket that is currently open under that specific client."
//
//   General: "no need to link to a client ... three options, general ticket, quote needed and To be
//   invoiced ... there should always be the option to link to clients at any stage."
//
// And his four answers: SLA goes straight to the office, plain Do not invoice needs a supervisor;
// a linked remote ticket CLOSES (until 2026-10-09: now it stays open, and its job waits for it); General's choice
// is buttons after creating; Done closes it.
// Every step is walked through TicketService.ActAsync, never a page.
//
//   dotnet run -c Debug --project tools/routecheck/routecheck.csproj
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

var dir = Path.Combine(Path.GetTempPath(), "routecheck-" + Guid.NewGuid().ToString("N")[..8]);
var dataDir = Path.Combine(dir, "data");
Directory.CreateDirectory(dataDir);
Console.WriteLine("routecheck - Walk in, Remote support and General, through the service");
Console.WriteLine();

const string Sue = "Sue Tech", Bob = "Bob Tech", Olive = "Olive Office", Sam = "Sam Supervisor";
const TenantRole T = TenantRole.Technician, OA = TenantRole.Billing, S = TenantRole.Admin;

try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    int zenith, acme;
    await using (var db = new PlatformDb(options))
    {
        await HarnessDb.MigrateAsync(db);
        db.Tenants.Add(new Tenant { Id = 1, Slug = "r", CompanyName = "Route IT", TenantKey = "kr" });
        await db.SaveChangesAsync();
        var z = new Client { TenantId = 1, Name = "Zenith Accounting" };
        var a = new Client { TenantId = 1, Name = "Acme Stores" };
        db.Clients.AddRange(z, a);
        await db.SaveChangesAsync();
        zenith = z.Id; acme = a.Id;
    }
    var factory = new Factory(options);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(t => t.Id == id); }
    string[] Keys(Ticket t, TenantRole r, string me) => TicketService.ActionsFor(t, r, me).Select(a => a.Key).ToArray();
    async Task<Ticket> New(TicketCategory c, string subject, int? client = null, string device = "")
        => await tickets.CreateAsync(1, subject, "", Olive, TicketSource.Manual, device, device.Length > 0 ? "PC-" + device : "", "",
            TicketPriority.Normal, Sue, c, client);
    async Task Photos(int id) { for (int i = 0; i < 3; i++) await photos.AddAsync(1, RemoteSupport.Tools.TestImages.Jpeg(), $"in-{i}.jpg", PhotoKind.Before, Sue, id, ""); }

    // ================================================================ walk in
    Console.WriteLine("== walk in ==");
    var w = await New(TicketCategory.WalkIn, "Screen flickers", zenith);
    Check("a walk in starts At the counter", w.Stage == "walkin-booked" && w.Status == TicketStatus.Open);
    Check("two options: Repaired at counter, or Needs to be booked in",
        Keys(w, T, Sue).SequenceEqual(new[] { "walkin-repaired", Workflow.BookInFromWalkIn }), string.Join(",", Keys(w, T, Sue)));
    Check("Repaired at counter asks the work done and the time spent",
        Workflow.Action("walkin-repaired")!.Fields.SequenceEqual(new[] { StageField.Details, StageField.Minutes }));
    Check("...refused without the time", (await tickets.ActAsync(1, w.Id, Sue, T, "walkin-repaired", new StageInput { Details = "x" })).Length > 0);
    Check("...and with both → Repaired at counter",
        await tickets.ActAsync(1, w.Id, Sue, T, "walkin-repaired", new StageInput { Details = "Reseated the display cable.", Minutes = 20 }) == ""
        && (await Get(w.Id)) is { Stage: "walkin-repaired", TimeSpentMinutes: 20 });
    Check("then To be invoiced, Invoiced — paid at counter, or No invoice",
        Keys(await Get(w.Id), T, Sue).SequenceEqual(new[] { "walkin-invoice", "walkin-paid-counter", "walkin-no-invoice" }), string.Join(",", Keys(await Get(w.Id), T, Sue)));
    Check("To be invoiced asks the amount and follows the usual route — and closes on payment",
        await tickets.ActAsync(1, w.Id, Sue, T, "walkin-invoice", new StageInput { Amount = 350 }) == ""
        && (await Get(w.Id)).Stage == "to-be-invoiced"
        && await tickets.ActAsync(1, w.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 350, Reference = "INV-1" }) == ""
        && await tickets.ActAsync(1, w.Id, Olive, OA, "payment-received") == ""
        && (await Get(w.Id)).Stage == "closed", (await Get(w.Id)).Stage);

    var wc = await New(TicketCategory.WalkIn, "Keyboard sticky", zenith);
    await tickets.ActAsync(1, wc.Id, Sue, T, "walkin-repaired", new StageInput { Details = "Cleaned it.", Minutes = 10 });
    Check("Invoiced — paid at counter → Paid cash or Paid card machine",
        await tickets.ActAsync(1, wc.Id, Sue, T, "walkin-paid-counter") == ""
        && (await Get(wc.Id)).Stage == "paid-counter" && Keys(await Get(wc.Id), T, Sue).SequenceEqual(new[] { "paid-cash", "paid-card" }));
    Check("Paid card machine needs the reference", (await tickets.ActAsync(1, wc.Id, Sue, T, "paid-card", new StageInput { Amount = 120 })).Length > 0);
    Check("...and lands on the COUNTER's to-be-closed, not the call out's",
        await tickets.ActAsync(1, wc.Id, Sue, T, "paid-card", new StageInput { Amount = 120, Reference = "7788" }) == ""
        && (await Get(wc.Id)) is { Stage: "paid-counter-to-close", Lane: (int)WorkLane.Office, PaymentMethod: "card", PaymentReference: "7788" });
    Check("its facts say Paid at counter", TicketService.WorkflowFacts(await Get(wc.Id)).Any(f => f.Label == "Paid at counter" && f.Value.StartsWith("120.00 · card machine")));
    Check("the office closes it", await tickets.ActAsync(1, wc.Id, Olive, OA, "close-paid-onsite") == "" && (await Get(wc.Id)).Stage == "closed");

    var wn = await New(TicketCategory.WalkIn, "Warranty check", zenith);
    await tickets.ActAsync(1, wn.Id, Sue, T, "walkin-repaired", new StageInput { Details = "Checked it over.", Minutes = 5 });
    Check("No invoice asks why", (await tickets.ActAsync(1, wn.Id, Sue, T, "walkin-no-invoice")).Contains("why"));
    Check("...and, like the workshop, waits for a supervisor",
        await tickets.ActAsync(1, wn.Id, Sue, T, "walkin-no-invoice", new StageInput { Reason = "Under warranty." }) == ""
        && (await Get(wn.Id)) is { Stage: "dni-approval", Lane: (int)WorkLane.Supervisor });
    Check("a supervisor declining sends it back to Repaired at counter",
        await tickets.ActAsync(1, wn.Id, Sam, S, "decline-dni", new StageInput { Reason = "Charge it." }) == ""
        && (await Get(wn.Id)).Stage == "walkin-repaired", (await Get(wn.Id)).Stage);

    var wb = await New(TicketCategory.WalkIn, "Will not boot", zenith);
    Check("Needs to be booked in is refused without the three photos",
        (await tickets.ActAsync(1, wb.Id, Sue, T, Workflow.BookInFromWalkIn)).Contains("photos"));
    await Photos(wb.Id);
    Check("with them it becomes the SAME ticket as a Workshop job at Booked in",
        await tickets.ActAsync(1, wb.Id, Sue, T, Workflow.BookInFromWalkIn, new StageInput { AssetMake = "Dell" }) == ""
        && (await Get(wb.Id)) is { Category: TicketCategory.Workshop, Stage: "awaiting-diagnosis", AssetMake: "Dell" }
        && (await Get(wb.Id)).Number == wb.Number);
    Check("...and follows the workshop route from there", Keys(await Get(wb.Id), T, Sue).Contains("inspection"));

    // ================================================================ remote support
    Console.WriteLine();
    Console.WriteLine("== remote support ==");
    var r = await New(TicketCategory.RemoteSupport, "Outlook will not open", zenith, "dev-1");
    Check("a remote support ticket starts at Remote session", r.Stage == "remote-session");
    Check("its options: To be invoiced, Do not invoice (SLA client), Do not invoice, Link to parent ticket",
        Keys(r, T, Sue).SequenceEqual(new[] { "remote-invoice", "remote-sla", "remote-dni", Workflow.LinkParent }), string.Join(",", Keys(r, T, Sue)));
    Check("To be invoiced asks what was done and the amount",
        Workflow.Action("remote-invoice")!.Fields.SequenceEqual(new[] { StageField.Details, StageField.Amount }));

    Check("a new session within 30 minutes joins it while nobody has decided",
        (await tickets.ActiveForDeviceAsync(1, "dev-1"))?.Id == r.Id);
    Check("To be invoiced → the usual route",
        await tickets.ActAsync(1, r.Id, Sue, T, "remote-invoice", new StageInput { Details = "Rebuilt the profile.", Amount = 450 }) == ""
        && (await Get(r.Id)).Stage == "to-be-invoiced");
    Check("...and once decided, a new session does NOT join it, however recent", await tickets.ActiveForDeviceAsync(1, "dev-1") == null);

    var sla = await New(TicketCategory.RemoteSupport, "Printer mapping", zenith, "dev-2");
    Check("Do not invoice (SLA client) asks nothing and goes straight to the office (Clint's choice)",
        Workflow.Action("remote-sla")!.Fields.Length == 0
        && await tickets.ActAsync(1, sla.Id, Sue, T, "remote-sla") == ""
        && (await Get(sla.Id)) is { Stage: "dni-to-close", Lane: (int)WorkLane.Office, DoNotInvoiceReason: "SLA client" });
    Check("...where the office closes it", await tickets.ActAsync(1, sla.Id, Olive, OA, "close-dni") == "" && (await Get(sla.Id)).Status == TicketStatus.Closed);

    var dni = await New(TicketCategory.RemoteSupport, "Two-minute fix", zenith, "dev-3");
    Check("plain Do not invoice asks why", (await tickets.ActAsync(1, dni.Id, Sue, T, "remote-dni")).Length > 0);
    Check("...and waits for a supervisor",
        await tickets.ActAsync(1, dni.Id, Sue, T, "remote-dni", new StageInput { Reason = "Two minutes." }) == ""
        && (await Get(dni.Id)).Lane == (int)WorkLane.Supervisor);
    Check("declined, it goes back to the remote session",
        await tickets.ActAsync(1, dni.Id, Sam, S, "decline-dni", new StageInput { Reason = "Bill it." }) == "" && (await Get(dni.Id)).Stage == "remote-session");

    // ---- link to parent ticket ----
    var job = await New(TicketCategory.General, "Office move — all PCs", zenith);
    var otherClients = await New(TicketCategory.General, "Acme's job", acme);
    var child = await New(TicketCategory.RemoteSupport, "Mapped drives after the move", zenith, "dev-4");
    var choices = await tickets.ParentChoicesAsync(1, child);
    Check("the choices are this client's OPEN tickets only — not another client's, not itself",
        choices.Any(c => c.Id == job.Id) && !choices.Any(c => c.Id == otherClients.Id || c.Id == child.Id || c.Id == w.Id),
        string.Join(", ", choices.Select(c => $"#{c.Number}")));
    Check("linking with nothing chosen is REFUSED", (await tickets.ActAsync(1, child.Id, Sue, T, Workflow.LinkParent)).Length > 0);
    Check("linking to ANOTHER client's ticket is REFUSED",
        (await tickets.ActAsync(1, child.Id, Sue, T, Workflow.LinkParent, new StageInput { ParentTicketId = otherClients.Id })).Length > 0);
    Check("linking to a CLOSED ticket is REFUSED",
        (await tickets.ActAsync(1, child.Id, Sue, T, Workflow.LinkParent, new StageInput { ParentTicketId = w.Id })).Contains("closed"));
    // Linking used to CLOSE the child (2026-09-27). That is how a parent came to be "closed while its child was still
    // open, closing both": the child had closed the moment it was linked. Clint, 2026-10-09, chose the other way:
    // the child stays open, finished and closed on its own, and the job cannot close until it is.
    Check("linking to one of theirs files it under that job and LEAVES IT OPEN where it was (Clint, 2026-10-09)",
        await tickets.ActAsync(1, child.Id, Sue, T, Workflow.LinkParent, new StageInput { ParentTicketId = job.Id }) == ""
        && (await Get(child.Id)) is { Stage: "remote-session", Status: not TicketStatus.Closed } && (await Get(child.Id)).ParentTicketId == job.Id,
        $"{(await Get(child.Id)).Stage} / {(await Get(child.Id)).Status}");
    Check("the parent lists it, and its story says so",
        (await tickets.ChildrenAsync(1, job.Id)).Any(c => c.Id == child.Id)
        && await Note(job.Id, $"#{child.Number} filed under this job."));
    Check("once filed it is not offered Link to parent again — Not part of it first",
        !Keys(await Get(child.Id), T, Sue).Contains(Workflow.LinkParent), string.Join(",", Keys(await Get(child.Id), T, Sue)));
    Check("...and pressing it anyway is refused, saying so",
        (await tickets.ActAsync(1, child.Id, Sue, T, Workflow.LinkParent, new StageInput { ParentTicketId = job.Id })).Contains("already part of another job"));
    Check("Done on the job is a step that closes it; Abandoned is not",
        TicketService.ClosesJob("general-done", await Get(job.Id)) && !TicketService.ClosesJob("abandon", await Get(job.Id)));
    var blockedWhy = await tickets.ActAsync(1, job.Id, Sam, S, "general-done");
    Check("the job CANNOT close while the child is open", blockedWhy.Length > 0 && (await Get(job.Id)).Status != TicketStatus.Closed, blockedWhy);
    Check("...and says which ticket, by number and subject, and to close it first",
        blockedWhy.Contains($"#{child.Number} Mapped drives after the move") && blockedWhy.Contains("Close it first"), blockedWhy);
    Check("...in the same words the portal's and the console's pop-ups use",
        blockedWhy == RemoteSupport.Shared.JobParts.Refusal(new[] { (child.Number, "Mapped drives after the move") }));
    Check("the child is still open — nothing closed it with the job",
        (await Get(child.Id)).Status != TicketStatus.Closed);
    Check("the child goes its own way: SLA, and the office closes it",
        await tickets.ActAsync(1, child.Id, Sue, T, "remote-sla") == ""
        && await tickets.ActAsync(1, child.Id, Olive, OA, "close-dni") == "" && (await Get(child.Id)).Status == TicketStatus.Closed,
        (await Get(child.Id)).Stage);
    Check("...and then the job closes",
        await tickets.ActAsync(1, job.Id, Sam, S, "general-done") == "" && (await Get(job.Id)).Status == TicketStatus.Closed);
    var loose = await New(TicketCategory.RemoteSupport, "No client yet", null, "dev-5");
    var looseWhy = await tickets.ActAsync(1, loose.Id, Sue, T, Workflow.LinkParent, new StageInput { ParentTicketId = job.Id });
    Check("a ticket with no client is told to link one first", looseWhy.Contains("client first"), looseWhy);

    // ================================================================ general
    Console.WriteLine();
    Console.WriteLine("== general ==");
    var g = await New(TicketCategory.General, "Order more toner for the office");
    Check("a General ticket needs no client", g.ClientId == null && g.Stage == "general-open");
    Check("its three options: Done, Quote needed, To be invoiced",
        Keys(g, T, Sue).SequenceEqual(new[] { "general-done", "quote-needed", "general-invoice" }), string.Join(",", Keys(g, T, Sue)));
    Check("Done is the person it is for's, and it CLOSES (Clint's choice)",
        (await tickets.ActAsync(1, g.Id, Bob, T, "general-done")).Length > 0
        && await tickets.ActAsync(1, g.Id, Sue, T, "general-done") == "" && (await Get(g.Id)).Status == TicketStatus.Closed);

    var gq = await New(TicketCategory.General, "Quote a new switch");
    Check("Quote needed with no items is REFUSED", (await tickets.ActAsync(1, gq.Id, Sue, T, "quote-needed")).Contains("item"));
    Check("its items may be added while it is Open", QuoteService.MayEditLines(await Get(gq.Id), T, Sue));
    await using (var db = new PlatformDb(options))
    {
        db.QuoteLines.Add(new QuoteLine { TenantId = 1, TicketId = gq.Id, ItemId = 1, ItemName = "24-port switch", AddedBy = Sue });
        await db.SaveChangesAsync();
    }
    Check("with an item it goes to the office, with no client at all",
        await tickets.ActAsync(1, gq.Id, Sue, T, "quote-needed") == "" && (await Get(gq.Id)) is { Stage: "quote-needed", Lane: (int)WorkLane.Office, ClientId: null });
    await tickets.ActAsync(1, gq.Id, Olive, OA, "quote-requested", new StageInput { SupplierEmail = "sales@supplier.test" });
    await tickets.ActAsync(1, gq.Id, Olive, OA, "quote-sent", new StageInput { Amount = 4200, Reference = "Q-9" });
    Check("a rejected General quote offers Do not invoice — and no inspection fee, there is no machine",
        await tickets.ActAsync(1, gq.Id, Olive, OA, "quote-rejected") == ""
        && Keys(await Get(gq.Id), T, Sue).SequenceEqual(new[] { "declined-do-not-invoice" }), string.Join(",", Keys(await Get(gq.Id), T, Sue)));
    Check("...and an inspection fee pressed anyway is REFUSED",
        (await tickets.ActAsync(1, gq.Id, Sue, T, "declined-inspection-fee", new StageInput { Minutes = 1, Amount = 1 })).Length > 0);
    await tickets.ActAsync(1, gq.Id, Sue, T, "declined-do-not-invoice", new StageInput { Reason = "Customer bought elsewhere." });
    Check("a declined Do not invoice goes back to the rejected quote",
        await tickets.ActAsync(1, gq.Id, Sam, S, "decline-dni", new StageInput { Reason = "No." }) == "" && (await Get(gq.Id)).Stage == "quote-rejected");

    var gi = await New(TicketCategory.General, "Set up the boardroom screen");
    Check("To be invoiced asks what was done, the time and the amount",
        Workflow.Action("general-invoice")!.Fields.SequenceEqual(new[] { StageField.Details, StageField.Minutes, StageField.Amount }));
    Check("...and follows the usual route, closing on payment",
        await tickets.ActAsync(1, gi.Id, Sue, T, "general-invoice", new StageInput { Details = "Mounted and cabled it.", Minutes = 60, Amount = 900 }) == ""
        && await tickets.ActAsync(1, gi.Id, Olive, OA, "client-invoiced", new StageInput { Amount = 900, Reference = "INV-2" }) == ""
        && await tickets.ActAsync(1, gi.Id, Olive, OA, "payment-received") == ""
        && (await Get(gi.Id)).Stage == "closed");

    // ================================================================ a client, at any stage
    Console.WriteLine();
    Console.WriteLine("== linking a client, at any stage ==");
    var lc = await New(TicketCategory.General, "Internal note that turned into a job");
    Check("a ticket with no client can be linked to one",
        await tickets.SetClientAsync(1, lc.Id, acme, "Pat at Acme", Olive) == "" && (await Get(lc.Id)) is { ClientId: var cid, ContactName: "Pat at Acme" } && cid == acme);
    Check("...and says so in its story", await Note(lc.Id, "Linked to Acme Stores — Pat at Acme."));
    await tickets.ActAsync(1, lc.Id, Sue, T, "general-invoice", new StageInput { Details = "x", Minutes = 5, Amount = 50 });
    Check("...and changed in the billing queue too — at any stage",
        await tickets.SetClientAsync(1, lc.Id, zenith, null, Olive) == "" && (await Get(lc.Id)).ClientId == zenith);
    Check("a client from another company is REFUSED", (await tickets.SetClientAsync(1, lc.Id, 999, null, Olive)).Length > 0);
    Check("unlinking works", await tickets.SetClientAsync(1, lc.Id, null, null, Olive) == "" && (await Get(lc.Id)).ClientId == null);

    async Task<bool> Note(int ticketId, string body)
    {
        await using var db = new PlatformDb(options);
        return await db.TicketNotes.AnyAsync(n => n.TicketId == ticketId && n.Body == body);
    }
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
