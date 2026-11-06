// =====================================================================================
// ticketstepcheck - proves Clint's three ticket changes of 2026-10-13 (HANDOVER 128):
//
//   1. "in the workshop section, where we have Not repairable, Invoice inspection fee, there Should also be a Not
//      Repairable, Damage report needed. This should then have an option for comments, and an option to upload the
//      damage report, which can be mailed directly from there to the client. It then goes to to be invoiced etc."
//   2. (the boxes that grow are proved by eye and by ticketwindowprobe — nothing here)
//   3. "we should have an option to go back at every stage. Something may have changed or a person clicked the next
//      stage by mistake."
//   and, the same day: "an option to upload files or photos on all comments ... This should also be able to be emailed."
//
// The rules first, in-process against the real services (TicketService, TicketAttachmentService, ClientUpdateService);
// then a platform of its own with the FAKE IMAP and SMTP servers from clientupdatecheck — the portal's forms, the
// uploads, the download, the emails as the bytes that would have left, and the console's messages.
//
//   dotnet run -c Debug --project tools/ticketstepcheck/ticketstepcheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
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
void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var dataDir = Path.Combine(Path.GetTempPath(), "rs-ticketstepcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

const string Pw = "ticket-step-2026";
const string Slug = "step-co";
const string Support = "support@stepco.test", SupportPw = "right-pw";
const string Fault = "Dropped down the stairs; will not power on.";
var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");
var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

using var imap = new FakeImap();
using var smtp = new FakeSmtp();
imap.Start(); smtp.Start();
imap.AddUser(Support, SupportPw);
smtp.AddUser(Support, SupportPw);
Console.WriteLine($"ticketstepcheck — damage reports, files on comments, going back; a platform on port {port}, IMAP {imap.Port}, SMTP {smtp.Port}");

Process? server = null;
try
{
    // =========================================================== the rules, with no database
    Head("what may go up");
    Check("a PDF may", TicketAttachmentService.Refusal("report.pdf", 1000) == "");
    Check("a Word document may", TicketAttachmentService.Refusal("report.docx", 1000) == "");
    Check("a program may not", TicketAttachmentService.Refusal("setup.exe", 1000).Contains("not a kind of file"));
    Check("a script may not", TicketAttachmentService.Refusal("fix.ps1", 1000).Contains("not a kind of file"));
    Check($"over {TicketAttachmentService.MaxBytes / (1024 * 1024)} MB may not", TicketAttachmentService.Refusal("big.pdf", TicketAttachmentService.MaxBytes + 1).Contains("at most"));
    Check("a PDF is told by its bytes", TicketAttachmentService.Sniff(pdf) == "application/pdf");
    Check("...a picture too", TicketAttachmentService.Sniff(png) == "image/png");
    Check("...and a renamed program is nothing this server will show", TicketAttachmentService.Sniff(Encoding.ASCII.GetBytes("MZ\x90\0")) == "application/octet-stream");

    Head("what the client is sent from a damage report");
    var dr = new TicketNote { Author = "Tess Tech", FormTitle = Workflow.DamageReportLabel,
        Body = "The board is cracked beside the charging port.\nTime spent: 45 min\nAmount to invoice: 450.00\nTess Tech taken off — in the billing queue." };
    var t0 = new Ticket { Number = 9, Subject = "Laptop", Description = Fault };
    Check("only the comments — cut before the time", ClientUpdateService.Words(dr) == "The board is cracked beside the charging port.", ClientUpdateService.Words(dr));
    var text = ClientUpdateService.Text(new Tenant { CompanyName = "Step Co" }, t0, dr, "Jane");
    Check("it says it is the damage report, and that it is attached", text.Contains("Tess Tech has sent the damage report for ticket #9 — Laptop. It is attached."));
    Check("NOT the time, the amount or who was taken off", !text.Contains("45 min") && !text.Contains("450") && !text.Contains("billing queue"));
    Check("a damage report may be emailed", ClientUpdateService.Sendable(dr));
    Check("...with no comments, if it has its report", ClientUpdateService.Sendable(new TicketNote { FormTitle = Workflow.DamageReportLabel, Body = "Time spent: 5 min" }, hasFiles: true));
    Check("another step's form still may not", !ClientUpdateService.Sendable(new TicketNote { FormTitle = "Inspection findings", Body = "x" }, hasFiles: true));
    Check("a comment of photos alone may", ClientUpdateService.Sendable(new TicketNote { Body = "" }, hasFiles: true));
    Check("...and says the files are attached, with no empty box of words",
        ClientUpdateService.Text(new Tenant { CompanyName = "Step Co" }, t0, new TicketNote { Author = "Tess Tech", Body = "" }, "Jane").Contains("Laptop. The files are attached."));
    var snap = StepSnapshot.Of(new Ticket { Stage = "to-be-invoiced", InvoiceAmount = 450m, CollectedUtc = new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc), Repairable = false });
    var back = StepSnapshot.Read(snap.Json());
    Check("a step's snapshot reads back as it was written", back == snap, back?.ToString() ?? "null");

    // =========================================================== the company, in-process
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl, ["Platform:Region"] = "ZA" }).Build();
    var dp = DataProtectionProvider.Create("ticketstepcheck");
    var tenants = new TenantService(factory, cfg, dataDir, dp);
    var (tenant, _) = await tenants.CreateAsync("Step Co", Slug, 7);
    int tid = tenant!.Id;
    await tenants.AddTechnicianAsync(tid, "owner@stepco.test", Pw, "Olga Owner", TenantRole.Owner | TenantRole.Admin);
    await tenants.AddTechnicianAsync(tid, "tess@stepco.test", Pw, "Tess Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "tim@stepco.test", Pw, "Tim Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "bea@stepco.test", Pw, "Bea Office", TenantRole.Billing);
    var clients = new ClientService(factory, dp);
    var contacts = new ContactService(factory);
    var (acme, _) = await clients.CreateAsync(tid, "Acme Ltd", "", "", "info@acme.test", "", "");
    var (jane, _) = await contacts.CreateAsync(tid, acme!.Id, "Jane Smith", "", "jane@acme.test", "", "");
    var tickets = new TicketService(factory);
    var files = new TicketAttachmentService(factory, NullLogger<TicketAttachmentService>.Instance, dataDir);
    const TenantRole T = TenantRole.Technician, O = TenantRole.Owner | TenantRole.Admin, B = TenantRole.Billing;
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(x => x.Id == id); }
    async Task<List<AvailableAction>> Offered(int id, TenantRole r, string me)
    { var t = await Get(id); return TicketService.ActionsFor(t, r, me, await tickets.BackStepAsync(tid, t, r, me)); }
    Task<Ticket> Workshop(string subject) => tickets.CreateAsync(tid, subject, Fault, "Olga Owner", TicketSource.Manual, contactName: "Jane Smith",
        assignedTo: "Tess Tech", category: TicketCategory.Workshop, clientId: acme.Id, extra: x => x.ContactId = jane!.Id);

    Head("Not repairable — Damage report needed, offered beside the inspection fee");
    var w1 = await Workshop("Laptop — dropped");
    Check("a new job has nowhere to go back to", (await Offered(w1.Id, T, "Tess Tech")).All(a => a.Key != TicketService.GoBackKey));
    Check("inspection", await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, "inspection", new StageInput { Details = "Board cracked." }) == "");
    Check("not repairable", await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, "not-repairable") == "");
    var atNr = await Offered(w1.Id, T, "Tess Tech");
    Check("Damage report needed is offered", atNr.Any(a => a.Key == Workflow.DamageReport && a.Label == "Damage report needed"));
    Check("...beside Invoice inspection fee and Do not invoice", atNr.Any(a => a.Key == "inspection-fee") && atNr.Any(a => a.Key == "do-not-invoice"));
    Check("...asking comments, the report, time, amount and the email — comments first",
        atNr.First(a => a.Key == Workflow.DamageReport).Fields.SequenceEqual(new[] { StageField.Comments, StageField.Files, StageField.Minutes, StageField.Amount, StageField.EmailClient }));
    Check("not after a rejected quote (Clint's answer 4)", Workflow.Action(Workflow.DamageReport)!.From.SequenceEqual(new[] { Workflow.NotRepairableStage }));

    Head("the damage report needs its report");
    Check("without a file: refused, saying so",
        await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, Workflow.DamageReport, new StageInput { Minutes = 45, Amount = 450 }) is var r0 && r0.Contains("Attach the damage report"), r0);
    var (beaFile, _) = await files.AddAsync(tid, w1.Id, pdf, "bea.pdf", "Bea Office");
    Check("with a file somebody ELSE picked: refused — it is not theirs to post",
        (await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, Workflow.DamageReport, new StageInput { Minutes = 45, Amount = 450, AttachmentIds = new() { beaFile!.Id } })).Contains("Attach"));
    var (report, rerr) = await files.AddAsync(tid, w1.Id, pdf, "Damage report 9.pdf", "Tess Tech");
    Check("Tess's own report goes up, told as a PDF by its bytes", report is { ContentType: "application/pdf", NoteId: null }, rerr);
    Check("no time: refused", (await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, Workflow.DamageReport, new StageInput { Amount = 450, AttachmentIds = new() { report!.Id } })).Contains("time"));
    var did = await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, Workflow.DamageReport,
        new StageInput { Comments = "Cracked board beside the charging port; not economical to repair.", Minutes = 45, Amount = 450, AttachmentIds = new() { report.Id, beaFile.Id } });
    Check("comments empty or not, with its report: recorded", did == "", did);
    var w1a = await Get(w1.Id);
    Check("...and it goes to To be invoiced", w1a.Stage == "to-be-invoiced" && w1a.Lane == (int)WorkLane.Office, w1a.Stage);
    Check("...with the time and the amount, as the inspection fee does", w1a.TimeSpentMinutes == 45 && w1a.InvoiceAmount == 450m && w1a.Outcome == "damage-report" && w1a.Repairable == false);
    var step = (await Notes(options, w1.Id)).Last(n => n.FormTitle == Workflow.DamageReportLabel);
    Check("the step reads comments first, then the time and the amount", step.Body.StartsWith("Cracked board") && step.Body.Contains("Time spent: 45 min") && step.Body.Contains("Amount to invoice: 450.00"), step.Body.Replace("\n", " | "));
    var onStep = await Attached(options, step.Id);
    Check("the report is on the step", onStep.Any(a => a.Id == report.Id));
    Check("...Bea's file is not — it stays hers, loose", onStep.All(a => a.Id != beaFile.Id));
    Check("the step kept what the job was before it, for going back", StepSnapshot.Read(step.Before) is { Stage: Workflow.NotRepairableStage, InvoiceAmount: null });

    Head("going back over the damage report");
    var tessSees = (await Offered(w1.Id, T, "Tess Tech")).FirstOrDefault(a => a.Key == TicketService.GoBackKey);
    Check("Tess, who made the step, is offered Back to Not repairable", tessSees?.Label == "Back to Not repairable", tessSees?.Label ?? "none");
    Check("...asking why", tessSees?.Fields.SequenceEqual(new[] { StageField.Reason }) == true);
    Check("...with nothing to warn of — nothing was sent", tessSees?.Warning == "", tessSees?.Warning ?? "");
    Check("Bea (the office, on its own stage) is offered it", (await Offered(w1.Id, B, "Bea Office")).Any(a => a.Key == TicketService.GoBackKey));
    Check("Tim, a technician who is not on the job, is not", await tickets.BackStepAsync(tid, await Get(w1.Id), T, "Tim Tech") == null);
    Check("...and pressing it anyway is refused", (await tickets.ActAsync(tid, w1.Id, "Tim Tech", T, TicketService.GoBackKey, new StageInput { Reason = "x" })).Contains("Going back is for"));
    Check("no reason: refused", (await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, TicketService.GoBackKey, new StageInput { Reason = " " })).Contains("Say why"));
    Check("with a reason: done", await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, TicketService.GoBackKey, new StageInput { Reason = "Pressed the wrong one — it needs the inspection fee." }) == "");
    var w1b = await Get(w1.Id);
    Check("the job is at Not repairable again", w1b.Stage == Workflow.NotRepairableStage && w1b.Lane == (int)WorkLane.Bench && w1b.Status == TicketStatus.InProgress);
    Check("what the step recorded no longer counts: no amount, no time, no outcome", w1b.InvoiceAmount == null && w1b.TimeSpentMinutes == null && w1b.Outcome == "");
    Check("...what was true before it still is: not repairable", w1b.Repairable == false);
    Check("back with Tess — the billing queue had taken her off", w1b.AssignedTo == "Tess Tech", w1b.AssignedTo);
    var notesB = await Notes(options, w1.Id);
    var undoneStep = notesB.First(n => n.Id == step.Id);
    Check("the step stays in the story, marked gone back over, by whom", undoneStep.UndoneUtc != null && undoneStep.UndoneBy == "Tess Tech");
    Check("...with its report still on it", (await Attached(options, step.Id)).Any(a => a.Id == report.Id));
    var wentBack = notesB.Last();
    Check("a 'Back to Not repairable' entry is the newest, with the reason and what it undid",
        wentBack is { WentBack: true, FormTitle: "Back to Not repairable", MovedTo: Workflow.NotRepairableStage } && wentBack.Body.Contains("Reason: Pressed the wrong one")
        && wentBack.Body.Contains("Undid: Damage report needed") && wentBack.Body.Contains("Back with Tess Tech"), wentBack.Body.Replace("\n", " | "));
    var again = (await Offered(w1.Id, T, "Tess Tech")).FirstOrDefault(a => a.Key == TicketService.GoBackKey);
    Check("Back again is one step further: to Inspected", again?.Label == "Back to Inspected", again?.Label ?? "none");
    Check("...and the steps forward are offered again", (await Offered(w1.Id, T, "Tess Tech")).Any(a => a.Key == "inspection-fee"));
    Check("back to Inspected", await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, TicketService.GoBackKey, new StageInput { Reason = "Second opinion first." }) == "");
    var w1c = await Get(w1.Id);
    Check("...and 'repairable or not' is unanswered again", w1c.Stage == Workflow.Inspected && w1c.Repairable == null);
    Check("back to Booked in", await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, TicketService.GoBackKey, new StageInput { Reason = "Booked in on the wrong ticket." }) == "");
    Check("...and from Booked in, nowhere further", await tickets.BackStepAsync(tid, await Get(w1.Id), O, "Olga Owner") == null
        && (await tickets.ActAsync(tid, w1.Id, "Olga Owner", O, TicketService.GoBackKey, new StageInput { Reason = "x" })).Contains("where the job started"));

    Head("forward again, through the office: an invoice issued is warned of, and cleared");
    foreach (var (key, input) in new (string, StageInput)[] { ("inspection", new() { Details = "Board cracked, again." }), ("not-repairable", new()), ("inspection-fee", new() { Minutes = 30, Amount = 300 }) })
        Check($"Tess: {key}", await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, key, input) == "");
    Check("Bea: client invoiced", await tickets.ActAsync(tid, w1.Id, "Bea Office", B, "client-invoiced", new StageInput { Amount = 300, Reference = "INV-77" }) == "");
    var beaBack = (await Offered(w1.Id, B, "Bea Office")).FirstOrDefault(a => a.Key == TicketService.GoBackKey);
    Check("Bea is offered Back to To be invoiced", beaBack?.Label == "Back to To be invoiced", beaBack?.Label ?? "none");
    Check("...warned that invoice INV-77 was issued and to credit it", beaBack?.Warning.Contains("Invoice INV-77") == true && beaBack.Warning.Contains("credit"), beaBack?.Warning ?? "");
    Check("going back", await tickets.ActAsync(tid, w1.Id, "Bea Office", B, TicketService.GoBackKey, new StageInput { Reason = "Wrong amount on the invoice." }) == "");
    var w1d = await Get(w1.Id);
    Check("the invoice number is cleared, the amount to invoice stays", w1d.Stage == "to-be-invoiced" && w1d.InvoiceNumber == "" && w1d.InvoiceAmount == 300m);
    Check("...and the warning is written into the story", (await Notes(options, w1.Id)).Last().Body.Contains("Invoice INV-77"));

    Head("a closed job: reopened only by a supervisor or the owner");
    var g = await tickets.CreateAsync(tid, "Order toner", "For the office.", "Olga Owner", TicketSource.Manual, assignedTo: "Tess Tech", category: TicketCategory.General);
    Check("Tess: done (it closes)", await tickets.ActAsync(tid, g.Id, "Tess Tech", T, "general-done") == "");
    Check("Tess is not offered Back on the closed job", (await Offered(g.Id, T, "Tess Tech")).Count == 0);
    Check("...nor Bea", await tickets.BackStepAsync(tid, await Get(g.Id), B, "Bea Office") == null);
    var olgaBack = (await Offered(g.Id, O, "Olga Owner")).SingleOrDefault();
    Check("the owner is — and it is the only thing offered", olgaBack?.Key == TicketService.GoBackKey && olgaBack.Label == "Back to Open");
    Check("...warned that it reopens a closed job", olgaBack?.Warning.Contains("reopens a closed job") == true);
    Check("Tess pressing it anyway is refused", (await tickets.ActAsync(tid, g.Id, "Tess Tech", T, TicketService.GoBackKey, new StageInput { Reason = "x" })).Contains("Only a supervisor or the owner"));
    Check("the owner reopens it", await tickets.ActAsync(tid, g.Id, "Olga Owner", O, TicketService.GoBackKey, new StageInput { Reason = "The toner was never ordered." }) == "");
    var g2 = await Get(g.Id);
    Check("...open again, no closed date", g2.Stage == Workflow.GeneralFirst && g2.Status == TicketStatus.Open && g2.ClosedUtc == null);

    Head("the edges");
    var hold = await Workshop("On hold");
    await tickets.ActAsync(tid, hold.Id, "Tess Tech", T, "wait-customer");
    var holdActs = await Offered(hold.Id, T, "Tess Tech");
    Check("Waiting on customer: Back to work only — no second button doing the same", holdActs.Any(a => a.Key == "resume") && holdActs.All(a => a.Key != TicketService.GoBackKey));
    var c = await tickets.CreateAsync(tid, "Call out became workshop", "x", "Olga Owner", TicketSource.Manual, assignedTo: "Tess Tech", category: TicketCategory.Workshop);
    await using (var db = new PlatformDb(options))
    {
        db.TicketNotes.Add(new TicketNote { TenantId = tid, TicketId = c.Id, Author = "Tess Tech", FormTitle = "Needs workshop work", Stage = Workflow.CallOutWorkDone, MovedTo = Workflow.First, Body = "Machine: Dell" });
        await db.SaveChangesAsync();
    }
    Check("a call out that became a workshop job never goes back to being one",
        await tickets.BackStepAsync(tid, await Get(c.Id), O, "Olga Owner") == null
        && (await tickets.ActAsync(tid, c.Id, "Olga Owner", O, TicketService.GoBackKey, new StageInput { Reason = "x" })).Contains("cannot go back to being a call out"));
    var old = await Workshop("Before going back existed");
    await tickets.ActAsync(tid, old.Id, "Tess Tech", T, "inspection", new StageInput { Details = "x" });
    await tickets.ActAsync(tid, old.Id, "Tess Tech", T, "not-repairable");
    await tickets.ActAsync(tid, old.Id, "Tess Tech", T, "inspection-fee", new StageInput { Minutes = 10, Amount = 100 });
    await using (var db = new PlatformDb(options))
    {   // as every step written before 2026-10-13 is: no snapshot
        var last = await db.TicketNotes.Where(n => n.TicketId == old.Id && n.MovedTo != "").OrderByDescending(n => n.Id).FirstAsync();
        last.Before = ""; await db.SaveChangesAsync();
    }
    var oldBack = await tickets.BackStepAsync(tid, await Get(old.Id), T, "Tess Tech");
    Check("an older step: offered, and warned that only the stage goes back", oldBack?.Warning.Contains("only the stage goes back") == true, oldBack?.Warning ?? "none");
    Check("...and only the stage goes back", await tickets.ActAsync(tid, old.Id, "Tess Tech", T, TicketService.GoBackKey, new StageInput { Reason = "x" }) == ""
        && await Get(old.Id) is { Stage: Workflow.NotRepairableStage, InvoiceAmount: 100m });

    Head("files on a comment");
    var (p1, _) = await files.AddAsync(tid, w1.Id, png, "progress.png", "Tess Tech");
    var photoNote = await tickets.AddNoteAsync(tid, w1.Id, "Tess Tech", "", false, attachments: new[] { p1!.Id });
    Check("photos alone, no words: a comment", photoNote != null && photoNote.Body == "");
    Check("...carrying the photo", (await Attached(options, photoNote!.Id)).Any(a => a.Id == p1.Id && a.IsImage));
    Check("no words and nobody's own file: nothing is posted", await tickets.AddNoteAsync(tid, w1.Id, "Tess Tech", "", false, attachments: new[] { beaFile.Id }) == null);
    Check("a posted file cannot be taken by another comment", await tickets.AddNoteAsync(tid, w1.Id, "Tess Tech", "", false, attachments: new[] { p1.Id }) == null);
    var (big1, _) = await files.AddAsync(tid, w1.Id, new byte[9 * 1024 * 1024], "big-1.zip", "Tess Tech");
    var (big2, _) = await files.AddAsync(tid, w1.Id, new byte[9 * 1024 * 1024], "big-2.zip", "Tess Tech");
    Check("18 MB of files together are too large to email — said before anything is posted",
        TicketAttachmentService.TooLargeToEmail(await files.LooseAsync(tid, w1.Id, new[] { big1!.Id, big2!.Id }, "Tess Tech")).Contains("at most"));

    // Clint, 2026-10-13: back a step, then "Payment received — close ticket" went round in a loop. Back put back
    // EVERYTHING as it was before the step — including "not collected", when the machine was collected AFTER the step.
    Head("Back never un-collects a machine collected after the step it undoes");
    var outPhotos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var w9 = await Workshop("Laptop — collected, then the invoice put right");
    foreach (var (k, i) in new (string, StageInput?)[] { ("inspection", new StageInput { Details = "DC jack." }), ("repairable", null),
                 ("solved-invoice", new StageInput { Details = "Replaced the jack.", Minutes = 30, Amount = 450 }),
                 ("client-invoiced", new StageInput { Amount = 450, Reference = "INV-1" }) })
        await tickets.ActAsync(tid, w9.Id, "Olga Owner", O, k, i);
    for (int i = 0; i < 3; i++) await outPhotos.AddAsync(tid, png, $"out-{i}.png", PhotoKind.After, "Bea Office", w9.Id, "");
    Check("collected while the payment is awaited", await tickets.ActAsync(tid, w9.Id, "Bea Office", B, TicketService.CollectedKey) == "" && (await Get(w9.Id)).CollectedUtc != null);
    var backOver = await tickets.BackStepAsync(tid, await Get(w9.Id), O, "Olga Owner");
    Check("...going back over Client invoiced does not warn that the machine comes back", backOver != null && !backOver.Warning.Contains("collected"), backOver?.Warning ?? "no Back");
    Check("Back over Client invoiced", await tickets.ActAsync(tid, w9.Id, "Olga Owner", O, TicketService.GoBackKey, new StageInput { Reason = "Wrong invoice number" }) == "");
    var w9b = await Get(w9.Id);
    Check("...the invoice is cleared, and the machine is STILL collected", w9b.InvoiceNumber == "" && w9b.CollectedUtc != null && w9b.CollectedBy == "Bea Office",
        $"invoice '{w9b.InvoiceNumber}', collected {w9b.CollectedUtc?.ToString("u") ?? "no"}");
    await tickets.ActAsync(tid, w9.Id, "Bea Office", B, "client-invoiced", new StageInput { Amount = 450, Reference = "INV-2" });
    Check("Payment received — close ticket then CLOSES it, rather than looping through Awaiting collection",
        await tickets.ActAsync(tid, w9.Id, "Bea Office", B, "payment-received") == "" && (await Get(w9.Id)).Stage == Workflow.Closed, (await Get(w9.Id)).Stage);

    // =========================================================== the platform
    Head("the platform");
    ProcessStartInfo Psi()
    {
        var p = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "run", "-c", "Debug", "--project", proj }) p.ArgumentList.Add(a);
        p.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(p.Environment, dataDir);
        p.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
        p.Environment["RSP_Platform__PublicUrl"] = baseUrl;
        p.Environment["RSP_Testing__IntakeSeconds"] = "2";
        p.Environment["RSP_Urls"] = baseUrl;
        p.Environment["ASPNETCORE_URLS"] = baseUrl;
        return p;
    }
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var built = await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + built); return 1; }
    var psi = Psi(); psi.ArgumentList.Add("--no-build"); psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    var owner = await Portal(port, "owner@stepco.test");
    var tess = await Portal(port, "tess@stepco.test");
    var tim = await Portal(port, "tim@stepco.test");
    string Url(int id) => $"/portal/tickets/{id}";

    Head("Tickets by email switched on, so the client can be emailed");
    var saved = WebUtility.HtmlDecode(await (await PostForm(owner, "/portal/mailintake?handler=Email", new()
    {
        ["emailEnabled"] = "true", ["address"] = Support, ["way"] = "mailbox",
        ["imapHost"] = "127.0.0.1", ["imapPort"] = imap.Port.ToString(), ["imapSecurity"] = "ssl", ["imapUser"] = Support, ["imapPassword"] = SupportPw, ["folder"] = "INBOX",
        ["autoReply"] = "true", ["sendVia"] = "own", ["smtpSameLogin"] = "true",
        ["smtpHost"] = "127.0.0.1", ["smtpPort"] = smtp.Port.ToString(), ["smtpSecurity"] = "ssl", ["smtpUser"] = Support, ["smtpPassword"] = "",
        ["autoReplyText"] = TicketIntake.DefaultReply
    })).Content.ReadAsStringAsync());
    Check("saved and tested", saved.Contains("The test worked"));

    Head("uploading, as the portal does");
    var w2 = await Workshop("Second laptop");
    await tickets.ActAsync(tid, w2.Id, "Tess Tech", T, "inspection", new StageInput { Details = "Hinge snapped." });
    await tickets.ActAsync(tid, w2.Id, "Tess Tech", T, "not-repairable");
    var noHeader = await Upload(tess, w2.Id, pdf, "report.pdf", header: false);
    Check("without the page's X-Attach header: refused (another site can send the cookie, not the header)", noHeader.status == 400, noHeader.status.ToString());
    var up = await Upload(tess, w2.Id, pdf, "Damage report — Acme.pdf");
    Check("with it: kept, and named as it was picked", up.status == 200 && up.id > 0 && await Named(options, up.id) == "Damage report — Acme.pdf", up.json);
    var exe = await Upload(tess, w2.Id, Encoding.ASCII.GetBytes("MZ"), "setup.exe");
    Check("a program is refused, saying what may go up", exe.json.Contains("not a kind of file"), exe.json);
    var hidden = await Workshop("Tim may not see this");
    await using (var db = new PlatformDb(options)) { var h = await db.Tickets.FirstAsync(x => x.Id == hidden.Id); h.AssignedTo = "Tess Tech"; await db.SaveChangesAsync(); }
    Check("onto a ticket the person may not see: not found", (await Upload(tim, hidden.Id, pdf, "x.pdf")).status == 404);

    Head("the damage report's form");
    var pg = WebUtility.HtmlDecode(await tess.GetStringAsync(Url(w2.Id)));
    Check("the step button: Damage report needed", pg.Contains("☞ Damage report needed"));
    var form = Between(pg, "id=\"step-damage-report\"", "</form>") ?? "";
    Check("its form asks comments (optional), the report, time and amount", form.Contains("name=\"comments\"") && form.Contains("Attach the damage report") && form.Contains("name=\"minutes\"") && form.Contains("name=\"amount\""));
    Check("...and Email the damage report to the client, TICKED by default (answer 2), to Jane",
        Regex.IsMatch(form, "name=\"emailClient\" value=\"true\" checked") && form.Contains("Email the damage report to the client") && form.Contains("<option value=\"jane@acme.test\" selected"));
    Check("...saying the time and amount do not go", form.Contains("not the time or the amount"));

    Head("submitting it — recorded, then emailed with the report attached");
    var before = smtp.Count;
    var sub = await PostPairs(tess, Url(w2.Id) + "?handler=Act", new()
    {
        new("id", w2.Id.ToString()), new("action", Workflow.DamageReport), new("comments", "The hinge mount has torn out of the base."),
        new("attach", up.id.ToString()), new("minutes", "0:40"), new("amount", "R 400,00"), new("emailClient", "true"), new("emailTo", "jane@acme.test")
    });
    var subPage = WebUtility.HtmlDecode(await sub.Content.ReadAsStringAsync());
    Check("back by a GET saying it was emailed", sub.RequestMessage!.RequestUri!.Query.Contains("sent=") && subPage.Contains("Emailed to jane@acme.test."), Snip(subPage, "alert"));
    var m = await smtp.WaitFor("jane@acme.test", $"[Ticket #{w2.Number}]");
    Check("Jane gets ONE email", m != null && smtp.Count == before + 1);
    if (m == null) return Done();
    Check("it says it is the damage report", m.Body.Contains("Tess Tech has sent the damage report for ticket #" + w2.Number));
    Check("...with the comments and the fault", m.Body.Contains("The hinge mount has torn out of the base.") && m.Body.Contains(Fault));
    Check("...NOT the time or the amount", !m.Body.Contains("40 min") && !m.Body.Contains("400") && !m.Html.Contains("400.00"));
    Check("...the report ATTACHED, under the name it was picked with, byte for byte",
        m.Files.Any(f => f.name == "Damage report — Acme.pdf" && f.bytes.SequenceEqual(pdf)), string.Join(", ", m.Files.Select(f => f.name)));
    var w2a = await Get(w2.Id);
    Check("the job is at To be invoiced, 40 minutes and 400.00", w2a.Stage == "to-be-invoiced" && w2a.TimeSpentMinutes == 40 && w2a.InvoiceAmount == 400m);
    var after = WebUtility.HtmlDecode(await tess.GetStringAsync(Url(w2.Id)));
    Check("the story shows the report under its step, as a link", after.Contains($"href=\"/portal/f/{await TokenOf(options, up.id)}\"") && after.Contains("Damage report — Acme.pdf"));
    Check("...and that it was emailed, to whom", after.Contains("Emailed to jane@acme.test by Tess Tech"));

    Head("a damage report refused keeps what was typed and picked");
    var w3 = await Workshop("Third laptop");
    await tickets.ActAsync(tid, w3.Id, "Tess Tech", T, "inspection", new StageInput { Details = "Water." });
    await tickets.ActAsync(tid, w3.Id, "Tess Tech", T, "not-repairable");
    var up3 = await Upload(tess, w3.Id, pdf, "water.pdf");
    var refused = WebUtility.HtmlDecode(await (await PostPairs(tess, Url(w3.Id) + "?handler=Act", new()
    {
        new("id", w3.Id.ToString()), new("action", Workflow.DamageReport), new("comments", "Corrosion everywhere."), new("attach", up3.id.ToString()),
        new("minutes", "20"), new("amount", "200"), new("emailClient", "true"), new("emailTo", "other"), new("emailOther", "not an address")
    })).Content.ReadAsStringAsync());
    Check("a bad address: refused, nothing recorded", refused.Contains("does not look like an email address") && refused.Contains("Nothing has been recorded") && (await Get(w3.Id)).Stage == Workflow.NotRepairableStage);
    Check("...the form open again with the comments, time and amount", refused.Contains(">Corrosion everywhere.</textarea>") && refused.Contains("value=\"20\"") && refused.Contains("value=\"200\""));
    Check("...and the report still picked", refused.Contains("water.pdf") && refused.Contains($"name=\"attach\" value=\"{up3.id}\""));

    Head("opening a file");
    var dl = await tess.GetAsync($"/portal/attachment/{up.id}");
    Check("a PDF opens in the browser, as a PDF", dl.IsSuccessStatusCode && dl.Content.Headers.ContentType?.MediaType == "application/pdf"
        && dl.Content.Headers.ContentDisposition?.DispositionType == "inline", $"{(int)dl.StatusCode} {dl.Content.Headers.ContentType} {dl.Content.Headers.ContentDisposition}");
    var doc = await Upload(tess, w2.Id, Encoding.ASCII.GetBytes("<script>alert(1)</script>"), "notes.docx");
    var dl2 = await tess.GetAsync($"/portal/attachment/{doc.id}");
    Check("anything else downloads — never shown as a page of ours", dl2.Content.Headers.ContentType?.MediaType == "application/octet-stream"
        && dl2.Content.Headers.ContentDisposition?.DispositionType == "attachment" && dl2.Headers.TryGetValues("X-Content-Type-Options", out var nos) && nos.Contains("nosniff"));
    var upHidden = await Upload(tess, hidden.Id, pdf, "secret.pdf");
    Check("a file on a ticket Tim may not see: not found for Tim", (await tim.GetAsync($"/portal/attachment/{upHidden.id}")).StatusCode == HttpStatusCode.NotFound);

    Head("a comment with photos, emailed");
    var ph = await Upload(tess, w2.Id, png, "progress-1.png");
    before = smtp.CountTo("jane@acme.test");
    var cm = await PostPairs(tess, Url(w2.Id) + "?handler=Comment", new()
    { new("id", w2.Id.ToString()), new("note", ""), new("attach", ph.id.ToString()), new("emailClient", "true"), new("emailTo", "jane@acme.test") });
    var mp = await smtp.WaitFor("jane@acme.test", "[Ticket #", 20000, after: before);
    Check("photos alone, emailed: the photo attached, the words saying so", mp != null && mp.Files.Any(f => f.name == "progress-1.png") && mp.Body.Contains("The files are attached."));
    var cpage = WebUtility.HtmlDecode(await tess.GetStringAsync(Url(w2.Id)));
    Check("on the ticket, the photo is shown as a picture", cpage.Contains($"<img src=\"/portal/f/{await TokenOf(options, ph.id)}\""));
    var bigA = await Upload(tess, w2.Id, new byte[9 * 1024 * 1024], "a.zip");
    var bigB = await Upload(tess, w2.Id, new byte[9 * 1024 * 1024], "b.zip");
    var n0 = (await Notes(options, w2.Id)).Count;
    var tooBig = WebUtility.HtmlDecode(await (await PostPairs(tess, Url(w2.Id) + "?handler=Comment", new()
    { new("id", w2.Id.ToString()), new("note", "Both disk images"), new("attach", bigA.id.ToString()), new("attach", bigB.id.ToString()), new("emailClient", "true"), new("emailTo", "jane@acme.test") })).Content.ReadAsStringAsync());
    Check("files too large to email together: refused, NOT posted, words and files kept",
        tooBig.Contains("at most") && tooBig.Contains("has not been posted") && (await Notes(options, w2.Id)).Count == n0 && tooBig.Contains(">Both disk images</textarea>") && tooBig.Contains("a.zip"));

    Head("going back, in the portal");
    var bp = WebUtility.HtmlDecode(await tess.GetStringAsync(Url(w2.Id)));
    Check("the Back button is there, plain beside the steps forward", bp.Contains("↶ Back to Not repairable"));
    var bf = Between(bp, "id=\"step-go-back\"", "</form>") ?? "";
    Check("...its form warns that the report was emailed", bf.Contains("tk-warn") && bf.Contains("emailed to jane@acme.test already"), Snip(bf, "tk-warn"));
    await PostPairs(tess, Url(w2.Id) + "?handler=Act", new() { new("id", w2.Id.ToString()), new("action", TicketService.GoBackKey), new("reason", "The customer wants it repaired after all.") });
    var bk = WebUtility.HtmlDecode(await tess.GetStringAsync(Url(w2.Id)));
    Check("back at Not repairable", (await Get(w2.Id)).Stage == Workflow.NotRepairableStage);
    Check("the step is faded and says it was gone back over", bk.Contains("tk-undone") && bk.Contains("Gone back over by Tess Tech"));
    Check("...and the crumb does not claim it came from To be invoiced", !Regex.IsMatch(Between(bk, "tk-crumb", "</div>") ?? "", "To be invoiced"));

    // =========================================================== the console
    Head("the console");
    var con = await Sign(port, "tess@stepco.test", Pw);
    con.Ws.Send(new TicketOpenMsg { TicketId = w2.Id });
    var det = (await con.Next<TicketDetailMsg>(d => d.Detail.Ticket.Id == w2.Id, 10000))?.Detail;
    var dStep = det?.Notes.FirstOrDefault(n => n.FormTitle == Workflow.DamageReportLabel);
    Check("the damage report's step carries its file, and may be emailed again", dStep?.Attachments.Any(a => a.Id == up.id && a.Name == "Damage report — Acme.pdf") == true && dStep.CanEmail);
    Check("...and says it was gone back over", dStep?.Undone.StartsWith("Gone back over by Tess Tech") == true);
    Check("the 'Back to' entry is marked as one", det?.Notes.Any(n => n.WentBack && n.FormTitle == "Back to Not repairable") == true);
    Check("a photo comment carries its picture, emailable", det?.Notes.Any(n => n.Attachments.Any(a => a.Id == ph.id && a.IsImage) && n.CanEmail) == true);
    Check("Back to Inspected is offered to the console too", det?.Actions.Any(a => a.Key == TicketService.GoBackKey && a.Label == "Back to Inspected" && a.Fields.SequenceEqual(new[] { "Reason" })) == true);
    var cfile = await UploadBearer(port, con.Token, w2.Id, pdf, "console report.pdf");
    Check("the console uploads with its bearer token, no header needed", cfile > 0);
    before = smtp.CountTo("ken@acme.test");
    con.Ws.Send(new TicketActMsg { TicketId = w2.Id, Action = Workflow.DamageReport, Comments = "From the console.", AttachmentIds = new() { cfile }, Minutes = "15", Amount = "150", EmailTo = "ken@acme.test" });
    var cm2 = await smtp.WaitFor("ken@acme.test", "[Ticket #", 20000, after: before);
    Check("a damage report from the console is recorded and emailed with its report", cm2 != null && cm2.Files.Any(f => f.name == "console report.pdf") && (await Get(w2.Id)).Stage == "to-be-invoiced");
    var told = await con.Next<TicketNoticeMsg>(n => n.Message.Contains("Emailed to ken@acme.test"), 8000);
    Check("...and the console is told", told != null);
    con.Ws.Send(new TicketActMsg { TicketId = w2.Id, Action = TicketService.GoBackKey, Reason = "From the console, by mistake." });
    var gone = await con.Next<TicketNoticeMsg>(n => n.Message == "Gone back.", 8000);
    Check("Back from the console", gone != null && (await Get(w2.Id)).Stage == Workflow.NotRepairableStage);
    var pfile = await UploadBearer(port, con.Token, w2.Id, png, "bench.png");
    con.Ws.Send(new TicketNoteAddMsg { TicketId = w2.Id, Body = "", AttachmentIds = new() { pfile } });
    await Task.Delay(1500);
    Check("a comment of a photo alone from the console", (await Attached(options, (await Notes(options, w2.Id)).Last().Id)).Any(a => a.Id == pfile));

    return Done();
}
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
}

int Done()
{
    Console.WriteLine();
    Console.WriteLine($"{pass} of {pass + fail} checks passed.");
    return fail == 0 ? 0 : 1;
}

static async Task<List<TicketAttachment>> Attached(DbContextOptions<PlatformDb> o, long noteId)
{ await using var db = new PlatformDb(o); return await db.TicketAttachments.Where(a => a.NoteId == noteId).ToListAsync(); }
static async Task<string> Named(DbContextOptions<PlatformDb> o, long id)
{ await using var db = new PlatformDb(o); return (await db.TicketAttachments.FirstOrDefaultAsync(a => a.Id == id))?.OriginalName ?? ""; }
/// <summary>A file's portal address token: a page names it /portal/f/{token}, never by id (HANDOVER §159).</summary>
static async Task<string> TokenOf(DbContextOptions<PlatformDb> o, long id)
{ await using var db = new PlatformDb(o); return (await db.TicketAttachments.FirstOrDefaultAsync(a => a.Id == id))?.Token ?? ""; }

static async Task<(int status, long id, string json)> Upload(HttpClient c, int ticketId, byte[] bytes, string name, bool header = true)
{
    using var form = new MultipartFormDataContent { { new StringContent(ticketId.ToString()), "ticketId" } };
    var part = new ByteArrayContent(bytes);
    part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    form.Add(part, "files", name);
    using var req = new HttpRequestMessage(HttpMethod.Post, "/api/portal/attachments") { Content = form };
    if (header) req.Headers.Add("X-Attach", "1");
    var res = await c.SendAsync(req);
    var json = await res.Content.ReadAsStringAsync();
    long id = 0;
    try { foreach (var f in JsonDocument.Parse(json).RootElement.GetProperty("files").EnumerateArray()) if (f.TryGetProperty("id", out var i)) id = i.GetInt64(); } catch { }
    return ((int)res.StatusCode, id, json);
}

static async Task<long> UploadBearer(int port, string token, int ticketId, byte[] bytes, string name)
{
    using var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var (_, id, _) = await Upload(c, ticketId, bytes, name, header: false);
    return id;
}

/// <summary>A form with a name more than once — the picked files each carry an "attach".</summary>
static async Task<HttpResponseMessage> PostPairs(HttpClient c, string path, List<KeyValuePair<string, string>> fields)
{
    var page = await c.GetStringAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""));
    fields.Add(new("__RequestVerificationToken", Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? ""));
    return await c.PostAsync(path, new FormUrlEncodedContent(fields));
}
static int CountOf(string s, string what) => Regex.Matches(s, Regex.Escape(what)).Count;

// ---------------------------------------------------------------- messages

static MimeMessage Mime(string from, string to, string subject, string text, string? mid = null, params (string k, string v)[] h)
{
    var m = new MimeMessage();
    m.From.Add(MailboxAddress.Parse(from));
    m.To.Add(MailboxAddress.Parse(to));
    m.Subject = subject;
    m.MessageId = (mid ?? $"<{Guid.NewGuid():N}@sender.test>").Trim('<', '>');
    m.Date = DateTimeOffset.Now;
    foreach (var (k, v) in h)
    {
        if (k == "References") { if (v.Length > 0) { m.References.Add(v.Trim('<', '>')); m.InReplyTo = v.Trim('<', '>'); } }
        else m.Headers.Add(k, v);
    }
    m.Body = new TextPart("plain") { Text = text };
    return m;
}

// ---------------------------------------------------------------- the database, read as it stands

static async Task<int> Count(DbContextOptions<PlatformDb> o, int tenantId, System.Linq.Expressions.Expression<Func<Ticket, bool>> where)
{ await using var db = new PlatformDb(o); return await db.Tickets.Where(x => x.TenantId == tenantId).Where(where).CountAsync(); }
static async Task<List<TicketNote>> Notes(DbContextOptions<PlatformDb> o, int ticketId)
{ await using var db = new PlatformDb(o); return await db.TicketNotes.Where(n => n.TicketId == ticketId).OrderBy(n => n.Id).ToListAsync(); }
static async Task<TicketNote?> WaitNote(DbContextOptions<PlatformDb> o, int ticketId, string text, int ms = 20000)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        var n = (await Notes(o, ticketId)).FirstOrDefault(x => x.Body.Contains(text));
        if (n != null) return n;
        await Task.Delay(300);
    }
    return null;
}
static async Task<int> OpenFlags(DbContextOptions<PlatformDb> o, int tenantId, string person)
{ await using var db = new PlatformDb(o); return await db.TicketMentions.CountAsync(m => m.TenantId == tenantId && m.Person == person && m.AnsweredUtc == null); }

// ---------------------------------------------------------------- the browser and the console

static async Task<HttpResponseMessage> PostForm(HttpClient c, string path, Dictionary<string, string> fields)
{
    // The page is fetched WITHOUT its handler: a GET naming a handler the page does not have runs no handler at all.
    // NOTE: fetching a ticket page is opening it — it clears that person's customer-reply flags.
    var page = await c.GetStringAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""));
    fields["__RequestVerificationToken"] = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
    return await c.PostAsync(path, new FormUrlEncodedContent(fields));
}
static async Task<HttpClient> Portal(int port, string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
    { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "ticket-step-2026" });
    return c;
}
static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal);
    if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal);
    return j < 0 ? null : s[i..j];
}
static string Snip(string html, string near)
{
    var i = html.IndexOf(near, StringComparison.OrdinalIgnoreCase);
    if (i < 0) return "(not found)";
    var s = Regex.Replace(Regex.Replace(html.Substring(i, Math.Min(220, html.Length - i)), "<[^>]+>", " "), "\\s+", " ");
    return s.Trim();
}
static async Task<TechConsole> Sign(int port, string email, string password)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws)) { Token = tok };
    _ = c.Ws.RunAsync(CancellationToken.None);
    return c;
}
static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
static async Task<bool> WaitForPort(int port, int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return true; }
        catch { await Task.Delay(400); }
    }
    return false;
}

file sealed class TechConsole
{
    public WsConnection Ws { get; }
    public string Token { get; init; } = "";
    private readonly List<Msg> _got = new();
    public TechConsole(WsConnection ws) { Ws = ws; ws.OnMessage += m => { lock (_got) _got.Add(m); }; }
    public async Task<T?> Next<T>(Func<T, bool> where, int ms) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got) { var m = _got.OfType<T>().FirstOrDefault(where); if (m != null) return m; }
            await Task.Delay(100);
        }
        return null;
    }
}

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}

// ---------------------------------------------------------------- a mail server's reading half

/// <summary>
/// Just enough IMAP4rev1 for MailKit to sign in, open INBOX and read new mail by UID: LOGIN, LIST,
/// EXAMINE/SELECT, UID SEARCH, UID FETCH (summary or BODY.PEEK[]), LOGOUT. Plain text, on loopback
/// only. Every command is recorded by mailbox, so the harness can say what was — and was NOT — asked.
/// </summary>
file sealed class FakeImap : IDisposable
{
    private TcpListener? _l;
    private readonly Dictionary<string, string> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(uint uid, byte[] raw, MimeMessage m)>> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _next = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _logins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _commands = new(StringComparer.OrdinalIgnoreCase);
    public bool PeekOnly { get; private set; } = true;
    public int Port { get; private set; }

    public void AddUser(string user, string pw) { lock (this) { _users[user] = pw; _boxes[user] = new(); _next[user] = 1; _commands[user] = new(); } }
    public void Deliver(string user, MimeMessage m)
    {
        using var ms = new MemoryStream(); m.WriteTo(ms);
        lock (this) { var u = _next[user]++; _boxes[user].Add((u, ms.ToArray(), m)); }
    }
    public int Logins(string user) { lock (this) return _logins.TryGetValue(user, out var n) ? n : 0; }
    public List<string> Commands(string user) { lock (this) return _commands[user].ToList(); }

    public void Start()
    {
        _l = new TcpListener(IPAddress.Loopback, 0); _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        try
        {
            var s = c.GetStream();
            async Task Send(string line) { var b = Encoding.ASCII.GetBytes(line + "\r\n"); await s.WriteAsync(b); }
            async Task SendBytes(byte[] b) => await s.WriteAsync(b);
            await Send("* OK [CAPABILITY IMAP4rev1] fake imap ready");
            string? user = null;
            while (await ReadLine(s) is { } line)
            {
                var sp = line.IndexOf(' ');
                if (sp < 0) continue;
                var tag = line[..sp]; var rest = line[(sp + 1)..];
                var words = rest.Split(' ', 3);
                var verb = words[0].ToUpperInvariant();
                if (verb == "UID" && words.Length > 1) verb = "UID " + words[1].ToUpperInvariant();
                if (user != null) lock (this) _commands[user].Add(verb);
                switch (verb)
                {
                    case "CAPABILITY": await Send("* CAPABILITY IMAP4rev1"); await Send($"{tag} OK CAPABILITY completed"); break;
                    case "NOOP": case "CHECK": case "ID": case "ENABLE": await Send($"{tag} OK done"); break;
                    case "LOGIN":
                    {
                        var args = Strings(rest[5..]);
                        var u = args.ElementAtOrDefault(0) ?? ""; var p = args.ElementAtOrDefault(1) ?? "";
                        lock (this) _logins[u] = (_logins.TryGetValue(u, out var n) ? n : 0) + 1;
                        bool ok; lock (this) ok = _users.TryGetValue(u, out var want) && want == p;
                        if (ok) { user = u; await Send($"{tag} OK [CAPABILITY IMAP4rev1] LOGIN completed"); }
                        else await Send($"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials");
                        break;
                    }
                    case "LIST": case "LSUB":
                    {
                        var args = Strings(rest[verb.Length..]);
                        var pattern = args.ElementAtOrDefault(1) ?? "";
                        if (pattern == "") await Send($"* {verb} (\\Noselect) \"/\" \"\"");
                        else if (pattern.Equals("INBOX", StringComparison.OrdinalIgnoreCase) || pattern is "*" or "%") await Send($"* {verb} (\\HasNoChildren) \"/\" INBOX");
                        await Send($"{tag} OK {verb} completed");
                        break;
                    }
                    case "EXAMINE": case "SELECT":
                    {
                        if (user == null) { await Send($"{tag} NO not signed in"); break; }
                        List<(uint uid, byte[] raw, MimeMessage m)> box; uint next;
                        lock (this) { box = _boxes[user].ToList(); next = _next[user]; }
                        await Send("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
                        await Send("* OK [PERMANENTFLAGS ()] No permanent flags permitted");
                        await Send($"* {box.Count} EXISTS");
                        await Send("* 0 RECENT");
                        await Send("* OK [UIDVALIDITY 777] UIDs valid");
                        await Send($"* OK [UIDNEXT {next}] Predicted next UID");
                        await Send($"{tag} OK [{(verb == "EXAMINE" ? "READ-ONLY" : "READ-WRITE")}] {verb} completed");
                        break;
                    }
                    case "UID SEARCH":
                    {
                        List<uint> uids; lock (this) uids = _boxes[user!].Select(x => x.uid).ToList();
                        var m = Regex.Match(rest, @"UID\s+([0-9:*,]+)\s*$", RegexOptions.IgnoreCase);
                        var found = m.Success ? InSet(m.Groups[1].Value, uids) : uids;
                        await Send("* SEARCH" + string.Concat(found.Select(u => " " + u)));
                        await Send($"{tag} OK SEARCH completed");
                        break;
                    }
                    case "UID FETCH":
                    {
                        var parts = rest.Split(' ', 4);
                        List<(uint uid, byte[] raw, MimeMessage m)> box; lock (this) box = _boxes[user!].ToList();
                        var want = InSet(parts[2], box.Select(x => x.uid).ToList()).ToHashSet();
                        var items = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
                        if (items.Contains("BODY[]") && !items.Contains("BODY.PEEK[]")) PeekOnly = false;
                        for (int i = 0; i < box.Count; i++)
                        {
                            if (!want.Contains(box[i].uid)) continue;
                            var (uid, raw, msg) = box[i];
                            if (items.Contains("BODY.PEEK[]") || items.Contains("BODY[]"))
                            {
                                await Send($"* {i + 1} FETCH (UID {uid} BODY[] {{{raw.Length}}}");
                                await SendBytes(raw);
                                await Send(")");
                            }
                            else
                            {
                                var from = msg.From.Mailboxes.First();
                                var at = from.Address.LastIndexOf('@');
                                await Send($"* {i + 1} FETCH (UID {uid} RFC822.SIZE {raw.Length} ENVELOPE (NIL {Q(msg.Subject)} (({Q(from.Name)} NIL {Q(from.Address[..at])} {Q(from.Address[(at + 1)..])})) NIL NIL NIL NIL NIL NIL {Q("<" + msg.MessageId + ">")}))");
                            }
                        }
                        await Send($"{tag} OK FETCH completed");
                        break;
                    }
                    case "CLOSE": case "UNSELECT": await Send($"{tag} OK done"); break;
                    case "LOGOUT": await Send("* BYE logging out"); await Send($"{tag} OK LOGOUT completed"); return;
                    default: await Send($"{tag} OK {verb} accepted"); break;
                }
            }
        }
        catch { /* the client went */ }
    }

    private static string Q(string? s)
    {
        if (s == null) return "NIL";
        if (s.Any(ch => ch > 126 || ch < 32)) return "NIL";
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
    private static List<string> Strings(string s) =>
        Regex.Matches(s, "\"((?:[^\"\\\\]|\\\\.)*)\"|(\\S+)").Select(m => m.Groups[1].Success ? Regex.Unescape(m.Groups[1].Value.Replace("\\\"", "\"")) : m.Groups[2].Value).ToList();
    private static List<uint> InSet(string set, List<uint> uids)
    {
        if (uids.Count == 0) return new();
        var max = uids.Max(); var r = new List<uint>();
        foreach (var part in set.Split(','))
        {
            var ab = part.Split(':');
            uint P(string x) => x == "*" ? max : uint.Parse(x) == uint.MaxValue ? max : uint.Parse(x);
            var a = P(ab[0]); var b = ab.Length > 1 ? P(ab[1]) : a;
            if (a > b) (a, b) = (b, a);
            r.AddRange(uids.Where(u => u >= a && u <= b));
        }
        return r.Distinct().OrderBy(x => x).ToList();
    }
    private static async Task<string?> ReadLine(NetworkStream s)
    {
        var sb = new StringBuilder(); var one = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(one);
            if (n == 0) return sb.Length > 0 ? sb.ToString() : null;
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
    }
    public void Dispose() { try { _l?.Stop(); } catch { } }
}

// ---------------------------------------------------------------- a mail server's sending half

/// <summary>Enough SMTP for MailKit: EHLO with AUTH PLAIN, MAIL, RCPT, DATA. Every message is kept
/// with who signed in to send it, and read back with MimeKit as a mail program would.</summary>
file sealed class FakeSmtp : IDisposable
{
    private TcpListener? _l;
    private readonly Dictionary<string, string> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Caught> _mails = new();
    public int Port { get; private set; }
    public int Count { get { lock (_mails) return _mails.Count; } }
    public void AddUser(string u, string p) { lock (_users) _users[u] = p; }

    public void Start()
    {
        _l = new TcpListener(IPAddress.Loopback, 0); _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        try
        {
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.UTF8);
            var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("220 fake ESMTP");
            var to = new List<string>(); string authed = "";
            bool Auth(string b64)
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('\0');
                var (u, p) = (parts.ElementAtOrDefault(1) ?? "", parts.ElementAtOrDefault(2) ?? "");
                lock (_users) if (_users.TryGetValue(u, out var want) && want == p) { authed = u; return true; }
                return false;
            }
            while (await r.ReadLineAsync() is { } line)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO")) { await w.WriteLineAsync("250-fake"); await w.WriteLineAsync("250-8BITMIME"); await w.WriteLineAsync("250 AUTH PLAIN"); }
                else if (cmd.StartsWith("HELO")) await w.WriteLineAsync("250 fake");
                else if (cmd.StartsWith("AUTH PLAIN"))
                {
                    var arg = line.Length > 11 ? line[11..].Trim() : "";
                    if (arg.Length == 0) { await w.WriteLineAsync("334 "); arg = (await r.ReadLineAsync() ?? "").Trim(); }
                    await w.WriteLineAsync(Auth(arg) ? "235 2.7.0 Authentication successful" : "535 5.7.8 Authentication credentials invalid");
                }
                else if (cmd.StartsWith("RCPT TO:")) { to.Add(Regex.Match(line, "<([^>]+)>").Groups[1].Value.ToLowerInvariant()); await w.WriteLineAsync("250 OK"); }
                else if (cmd == "DATA")
                {
                    await w.WriteLineAsync("354 go ahead");
                    var sb = new StringBuilder();
                    while (await r.ReadLineAsync() is { } d && d != ".") sb.Append(d.StartsWith("..") ? d[1..] : d).Append("\r\n");
                    lock (_mails) _mails.Add(new Caught(to.ToList(), authed, MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())))));
                    to.Clear();
                    await w.WriteLineAsync("250 queued");
                }
                else if (cmd == "QUIT") { await w.WriteLineAsync("221 bye"); break; }
                else await w.WriteLineAsync("250 OK");
            }
        }
        catch { }
    }

    /// <param name="after">Only a message beyond this many already sent to <paramref name="to"/> — a NEW one.</param>
    public async Task<Caught?> WaitFor(string to, string subjectPart, int ms = 20000, int after = 0)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_mails)
            {
                var theirs = _mails.Where(x => x.To.Contains(to.ToLowerInvariant())).Skip(after).ToList();
                var m = theirs.LastOrDefault(x => x.Subject.Contains(subjectPart)); if (m != null) return m;
            }
            await Task.Delay(150);
        }
        return null;
    }
    public int CountTo(string to) { lock (_mails) return _mails.Count(x => x.To.Contains(to.ToLowerInvariant())); }
    public void Dispose() { try { _l?.Stop(); } catch { } }
}

file sealed class Caught
{
    public List<string> To { get; }
    public string AuthUser { get; }
    private readonly MimeMessage _m;
    public Caught(List<string> to, string auth, MimeMessage m) { To = to; AuthUser = auth; _m = m; }
    public string Subject => _m.Subject ?? "";
    public string From => _m.From.ToString();
    public string FromAddress => _m.From.Mailboxes.FirstOrDefault()?.Address ?? "";
    public string FromName => _m.From.Mailboxes.FirstOrDefault()?.Name ?? "";
    public string ReplyTo => _m.ReplyTo.ToString();
    public string Body => _m.TextBody ?? "";
    public string Html => _m.HtmlBody ?? "";
    /// <summary>What came attached: each file's name and its bytes, decoded.</summary>
    public List<(string name, byte[] bytes)> Files => _m.Attachments.OfType<MimePart>().Select(p =>
    {
        using var ms = new MemoryStream(); p.Content.DecodeTo(ms); return (p.FileName ?? "", ms.ToArray());
    }).ToList();
    public string Header(string name) => name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase) ? "<" + _m.MessageId + ">"
        : name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) ? "<" + _m.InReplyTo + ">"
        : _m.Headers[name] ?? "";
}
