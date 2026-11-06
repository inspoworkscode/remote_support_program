// =====================================================================================
// ticketeditcheck - proves Clint's ticket changes of 2026-10-13 (HANDOVER 130):
//
//   "Tickets, Subjects (names), scheduled appointment times, category etc cannot be edited and needs to be fixed and
//    we need the ability to sort tickets by date."  His answers: subject, appointment, category and description; a
//   category change starts the new route at its first stage; anybody who can see the ticket; a Sort box.
//   And the same day: "On the dashboard it shows 6 unassigned, but when you click on unassigned, it shows nothing."
//
// The rules in-process against the real TicketService and DashboardService; then a platform of its own — the portal's
// Edit form, the sorted list and its remembered choice, the dashboard's tile, the photo upload, and the console's
// TicketEditMsg and sorted TicketListRequestMsg over a real socket.
//
//   dotnet run -c Debug --project tools/ticketeditcheck/ticketeditcheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var dataDir = Path.Combine(Path.GetTempPath(), "rs-ticketeditcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
const string Pw = "ticket-edit-2026";
var jpeg = RemoteSupport.Tools.TestImages.Jpeg();
Console.WriteLine($"ticketeditcheck — editing tickets, sorting them, the unassigned tile; a platform on port {port}");

Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl, ["Platform:Region"] = "ZA" }).Build();
    var dp = DataProtectionProvider.Create("ticketeditcheck");
    var tenants = new TenantService(factory, cfg, dataDir, dp);
    var (tenant, _) = await tenants.CreateAsync("Edit Co", "edit-co", 7);
    int tid = tenant!.Id;
    // Job types chosen (everything on), as for any company already set up: an owner's first dashboard visit would
    // otherwise be sent to the Job types page once (HANDOVER §167), and this harness is about the dashboard.
    await tenants.SetJobTypesAsync(tid, TenantRole.Owner, "Set up", Array.Empty<string>());
    await tenants.AddTechnicianAsync(tid, "owner@editco.test", Pw, "Olga Owner", TenantRole.Owner | TenantRole.Admin);
    await tenants.AddTechnicianAsync(tid, "tess@editco.test", Pw, "Tess Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "tim@editco.test", Pw, "Tim Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "bea@editco.test", Pw, "Bea Office", TenantRole.Billing);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    const TenantRole T = TenantRole.Technician, O = TenantRole.Owner | TenantRole.Admin, B = TenantRole.Billing;
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(x => x.Id == id); }
    async Task<List<TicketNote>> Notes(int id) { await using var db = new PlatformDb(options); return await db.TicketNotes.Where(n => n.TicketId == id).OrderBy(n => n.Id).ToListAsync(); }
    async Task Set(int id, Action<Ticket> change) { await using var db = new PlatformDb(options); var t = await db.Tickets.FirstAsync(x => x.Id == id); change(t); await db.SaveChangesAsync(); }
    Task<Ticket> New(string subject, TicketCategory c, string who = "Tess Tech", string description = "It will not print.") =>
        tickets.CreateAsync(tid, subject, description, "Olga Owner", TicketSource.Manual, assignedTo: who, category: c);
    var future = DateTime.UtcNow.AddDays(3);
    future = new DateTime(future.Year, future.Month, future.Day, 9, 30, 0, DateTimeKind.Utc);

    // =========================================================== the subject and the description
    Head("the subject and the description");
    var g1 = await New("Printer", TicketCategory.General);
    var (why, said) = await tickets.EditAsync(tid, g1.Id, "Bea Office", B, new TicketEdit(Subject: "Printer in reception jams on every page"));
    Check("the office may change a subject (anybody who can see it)", why == "" && (await Get(g1.Id)).Subject == "Printer in reception jams on every page", why);
    var n1 = (await Notes(g1.Id)).Last();
    Check("...and the story says who, and what it was", n1.Author == "Bea Office" && n1.FormTitle == TicketService.DetailsChangedTitle
        && n1.Body.Contains("Subject: Printer in reception jams on every page (was: Printer)"), n1.Body);
    Check("...an entry that moves nothing (no arrow, nothing for Back)", n1.MovedTo == "" && n1.Before == "");
    (why, _) = await tickets.EditAsync(tid, g1.Id, "Tess Tech", T, new TicketEdit(Subject: "   ", Description: "new words"));
    Check("an empty subject is refused...", why.Contains("needs a subject"), why);
    Check("...and NOTHING of the same edit is applied", (await Get(g1.Id)).Description == "It will not print.");
    (why, _) = await tickets.EditAsync(tid, g1.Id, "Tess Tech", T, new TicketEdit(Subject: new string('x', TicketService.SubjectMax + 1)));
    Check($"a subject over {TicketService.SubjectMax} characters is refused", why.Contains("Keep the subject"), why);
    (why, said) = await tickets.EditAsync(tid, g1.Id, "Tess Tech", T, new TicketEdit(Description: "It jams on every page.\r\nSince the toner change."));
    var n2 = (await Notes(g1.Id)).Last();
    Check("a description may be changed", why == "" && (await Get(g1.Id)).Description == "It jams on every page.\nSince the toner change.");
    Check("...and the old words are kept in the story, so nothing typed is lost", n2.Body.Contains("Description changed. It was:\nIt will not print."), n2.Body);
    int before = (await Notes(g1.Id)).Count;
    (why, said) = await tickets.EditAsync(tid, g1.Id, "Tess Tech", T, new TicketEdit(Subject: "Printer in reception jams on every page", Description: "It jams on every page.\nSince the toner change."));
    Check("the same values again are not a change — nothing written", why == "" && said.Count == 0 && (await Notes(g1.Id)).Count == before);

    // =========================================================== the appointment
    Head("the appointment");
    var c1 = await New("Server room", TicketCategory.CallOut);
    (why, _) = await tickets.EditAsync(tid, c1.Id, "Tess Tech", T, new TicketEdit(AppointmentGiven: true, AppointmentUtc: future));
    Check("a call out's appointment may be set", why == "" && (await Get(c1.Id)).AppointmentUtc == future, why);
    Check("...said in the story with what it was", (await Notes(c1.Id)).Last().Body.Contains("(was not scheduled)"));
    (why, _) = await tickets.EditAsync(tid, c1.Id, "Tess Tech", T, new TicketEdit(AppointmentGiven: true, AppointmentUtc: DateTime.UtcNow.AddHours(-2)));
    Check("a time already gone is refused", why.Contains("already passed") && (await Get(c1.Id)).AppointmentUtc == future, why);
    before = (await Notes(c1.Id)).Count;
    (why, said) = await tickets.EditAsync(tid, c1.Id, "Tess Tech", T, new TicketEdit(AppointmentGiven: true, AppointmentUtc: future.AddSeconds(20)));
    Check("the same minute is not a change (a form shows minutes, not seconds)", why == "" && said.Count == 0 && (await Notes(c1.Id)).Count == before);
    (why, said) = await tickets.EditAsync(tid, c1.Id, "Tess Tech", T, new TicketEdit(Subject: "Server room — new switch"));
    Check("an edit that does not send the appointment leaves it alone", why == "" && (await Get(c1.Id)).AppointmentUtc == future);
    (why, _) = await tickets.EditAsync(tid, c1.Id, "Tess Tech", T, new TicketEdit(AppointmentGiven: true, AppointmentUtc: null));
    Check("it may be taken away", why == "" && (await Get(c1.Id)).AppointmentUtc == null && (await Notes(c1.Id)).Last().Body.Contains("Appointment removed"));

    // =========================================================== the category
    Head("the category: the new route starts at its first stage");
    var g2 = await New("Mouse", TicketCategory.General);
    (why, _) = await tickets.EditAsync(tid, g2.Id, "Tess Tech", T, new TicketEdit(Category: (int)TicketCategory.CallOut));
    var g2b = await Get(g2.Id);
    Check("General → Call out: a call out now, at Call out booked", why == "" && g2b.Category == TicketCategory.CallOut && g2b.Stage == Workflow.CallOutFirst, $"{why} {g2b.Category} {g2b.Stage}");
    var cn = (await Notes(g2.Id)).Last();
    Check("...the story says so: from what, and where it starts again", cn.FormTitle == TicketService.CategoryChangedTitle
        && cn.Body.Contains("Category: Call out (was General). It starts again at"), cn.Body);
    Check("...and offers that route's steps", TicketService.ActionsFor(g2b, T, "Tess Tech").Any(a => a.Key == "callout-work-done"));
    (why, _) = await tickets.EditAsync(tid, g2.Id, "Tess Tech", T, new TicketEdit(Category: (int)TicketCategory.Recurring));
    Check("a ticket cannot be turned into a recurring task", why.Contains("Recurring tasks page"), why);
    (why, _) = await tickets.EditAsync(tid, g2.Id, "Tess Tech", T, new TicketEdit(Category: 99));
    Check("a number that is no category is refused, not guessed", why.Contains("not one of the ticket categories"), why);

    Head("to Workshop: the booking's two needs first");
    var g3 = await New("Laptop — wrong type", TicketCategory.General, who: "");
    (why, _) = await tickets.EditAsync(tid, g3.Id, "Bea Office", B, new TicketEdit(Category: (int)TicketCategory.Workshop));
    Check("refused with nobody on it — a workshop job is booked in to a technician", why.Contains("booked in to a technician") && (await Get(g3.Id)).Category == TicketCategory.General, why);
    await Set(g3.Id, x => x.AssignedTo = "Tess Tech");
    await photos.AddAsync(tid, jpeg, "in-1.jpg", PhotoKind.Before, "Tess Tech", g3.Id, "");
    (why, _) = await tickets.EditAsync(tid, g3.Id, "Bea Office", B, new TicketEdit(Category: (int)TicketCategory.Workshop));
    Check("refused with one photo as it arrived, and says how many", why.Contains($"{TicketPhotoService.Required} photos") && why.Contains("one has been added"), why);
    for (int i = 2; i <= TicketPhotoService.Required; i++) await photos.AddAsync(tid, jpeg, $"in-{i}.jpg", PhotoKind.Before, "Tess Tech", g3.Id, "");
    (why, _) = await tickets.EditAsync(tid, g3.Id, "Bea Office", B, new TicketEdit(Category: (int)TicketCategory.Workshop));
    var g3b = await Get(g3.Id);
    Check("with both, it is a workshop job at Booked in", why == "" && g3b.Category == TicketCategory.Workshop && g3b.Stage == Workflow.First, $"{why} {g3b.Stage}");

    Head("Back never crosses a category change");
    var w1 = await New("Desktop", TicketCategory.Workshop);
    Check("inspection", await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, "inspection", new StageInput { Details = "PSU dead." }) == "");
    Check("...Back is offered after it", await tickets.BackStepAsync(tid, await Get(w1.Id), T, "Tess Tech") != null);
    (why, _) = await tickets.EditAsync(tid, w1.Id, "Tess Tech", T, new TicketEdit(Category: (int)TicketCategory.WalkIn));
    var w1b = await Get(w1.Id);
    Check("Workshop → Walk in, at the walk in's first stage", why == "" && w1b.Stage == Workflow.WalkInFirst && w1b.Repairable == null, $"{why} {w1b.Stage}");
    Check("...and Back is no longer offered", await tickets.BackStepAsync(tid, w1b, T, "Tess Tech") == null);
    Check("...nor obeyed when pressed anyway", (await tickets.ActAsync(tid, w1.Id, "Tess Tech", T, TicketService.GoBackKey, new StageInput { Reason = "oops" })).Length > 0);
    Check("...and the old inspection is still in the story", (await Notes(w1.Id)).Any(n => n.FormTitle == "Inspection findings" || n.Body.Contains("PSU dead.")));

    Head("closed, and in the billing queue");
    var g4 = await New("Done already", TicketCategory.General);
    await Set(g4.Id, x => { x.Stage = Workflow.Closed; x.Lane = (int)WorkLane.Done; x.Status = TicketStatus.Closed; x.ClosedUtc = DateTime.UtcNow; });
    (why, _) = await tickets.EditAsync(tid, g4.Id, "Olga Owner", O, new TicketEdit(Category: (int)TicketCategory.CallOut));
    Check("a closed job's category is not changed", why.Contains("closed"), why);
    (why, _) = await tickets.EditAsync(tid, g4.Id, "Olga Owner", O, new TicketEdit(AppointmentGiven: true, AppointmentUtc: future));
    Check("...nor its appointment", why.Contains("closed"), why);
    (why, _) = await tickets.EditAsync(tid, g4.Id, "Olga Owner", O, new TicketEdit(Subject: "Done already — printer"));
    Check("...but a typo in its subject may still be put right", why == "" && (await Get(g4.Id)).Subject == "Done already — printer", why);
    var g5 = await New("In billing", TicketCategory.General, who: "");
    await Set(g5.Id, x => { x.Stage = "to-be-invoiced"; x.Lane = (int)WorkLane.Office; });
    (why, _) = await tickets.EditAsync(tid, g5.Id, "Bea Office", B, new TicketEdit(Category: (int)TicketCategory.CallOut));
    var g5b = await Get(g5.Id);
    Check("out of the billing queue onto the bench, waiting to be picked up — and said", why == "" && g5b.Lane == (int)WorkLane.Bench
        && (await Notes(g5.Id)).Last().Body.Contains("waiting for somebody to pick it up"), $"{why} lane {g5b.Lane}");

    // =========================================================== the unassigned tile
    Head("the dashboard's Unassigned is the list it opens");
    var b1 = await New("Invoice me", TicketCategory.General, who: "");
    await Set(b1.Id, x => { x.Stage = "to-be-invoiced"; x.Lane = (int)WorkLane.Office; });
    var b2 = await New("Approve me", TicketCategory.General, who: "");
    await Set(b2.Id, x => { x.Lane = (int)WorkLane.Supervisor; });
    var dash = await new DashboardService(factory).LoadAsync(tid, "Olga Owner", new HashSet<string>());
    var pool = await tickets.ListAsync(tid, "unassigned", "", "Olga Owner", O);
    Check("the tile counts exactly what the Unassigned list shows", dash.Unassigned == pool.Count, $"tile {dash.Unassigned}, list {pool.Count}");
    Check("...which leaves out the billing queue and the supervisors' (nobody's name on them either)",
        pool.All(t => t.Id != b1.Id && t.Id != b2.Id) && pool.Any(t => t.Id == g5.Id), string.Join(", ", pool.Select(t => "#" + t.Number)));

    // =========================================================== sorting
    Head("sorting by date — done before the first few hundred are taken");
    var s1 = await New("Sort A", TicketCategory.General); var s2 = await New("Sort B", TicketCategory.General); var s3 = await New("Sort C", TicketCategory.General);
    await Set(s1.Id, x => { x.CreatedUtc = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc); x.UpdatedUtc = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc); x.AppointmentUtc = future.AddDays(2); });
    await Set(s2.Id, x => { x.CreatedUtc = new DateTime(2026, 3, 5, 8, 0, 0, DateTimeKind.Utc); x.UpdatedUtc = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc); x.AppointmentUtc = null; });
    await Set(s3.Id, x => { x.CreatedUtc = new DateTime(2026, 2, 5, 8, 0, 0, DateTimeKind.Utc); x.UpdatedUtc = new DateTime(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc); x.AppointmentUtc = future.AddDays(1); });
    async Task<string> Order(string sort) => string.Join(" ", (await tickets.ListAsync(tid, "open", "Sort ", "Olga Owner", O, sort: sort)).Select(t => t.Subject[^1..]));
    Check("Newest first", await Order(TicketSort.Newest) == "B C A", await Order(TicketSort.Newest));
    Check("Oldest first", await Order(TicketSort.Oldest) == "A C B", await Order(TicketSort.Oldest));
    Check("Last updated", await Order(TicketSort.Updated) == "A C B", await Order(TicketSort.Updated));
    Check("Appointment, soonest first — none last", await Order(TicketSort.Appointment) == "C A B", await Order(TicketSort.Appointment));
    Check("nothing, or nonsense, is the order it always was", await Order("") == "A C B" && await Order("drop table") == "A C B");
    var few = await tickets.ListAsync(tid, "all", "", "Olga Owner", O, take: 1, sort: TicketSort.Oldest);
    Check("the order is applied BEFORE the take: one ticket, oldest first, is the oldest of all", few.Count == 1 && few[0].Id == s1.Id, few.FirstOrDefault()?.Subject ?? "none");

    // =========================================================== the platform
    Head("the platform");
    var p = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "-c", "Debug", "--project", proj, "--no-build", "--", "run" }) p.ArgumentList.Add(a);
    p.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(p.Environment, dataDir);
    p.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
    p.Environment["RSP_Platform__PublicUrl"] = baseUrl;
    p.Environment["RSP_Urls"] = baseUrl;
    p.Environment["ASPNETCORE_URLS"] = baseUrl;
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var built = await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + built); return 1; }
    server = Process.Start(p)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
    var owner = await Portal(port, "owner@editco.test");
    var bea = await Portal(port, "bea@editco.test");
    var tim = await Portal(port, "tim@editco.test");

    Head("the portal's Edit the ticket");
    var e1 = await New("Router", TicketCategory.General);
    var page = await owner.GetStringAsync($"/portal/tickets/{e1.Id}");
    Check("the ticket page has the card and the Edit link", page.Contains("Edit the ticket") && page.Contains("✎ Edit") && page.Contains("handler=Edit"));
    var res = await PostForm(bea, $"/portal/tickets/{e1.Id}?handler=Edit", new()
    { ["id"] = e1.Id.ToString(), ["subject"] = "Router reboots every hour", ["category"] = ((int)TicketCategory.CallOut).ToString(),
      ["appointmentShown"] = "true", ["appointmentDate"] = CompanyClockLocal(future).ToString("yyyy-MM-dd"), ["appointmentTime"] = CompanyClockLocal(future).ToString("HH:mm"),
      ["description"] = "It reboots on the hour." });
    var after = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    var e1b = await Get(e1.Id);
    Check("the office saves subject, type, appointment and description in one go", e1b.Subject == "Router reboots every hour" && e1b.Category == TicketCategory.CallOut
        && e1b.Stage == Workflow.CallOutFirst && e1b.AppointmentUtc == future && e1b.Description == "It reboots on the hour.",
        $"{e1b.Subject} / {e1b.Category} / {e1b.Stage} / {e1b.AppointmentUtc:u} / {e1b.Description}");
    Check("...is told it is saved, on the Details tab", after.Contains("Saved — the change is in the ticket's story.") && after.Contains("Edit the ticket"));
    Check("...and the story shows the change", after.Contains(TicketService.CategoryChangedTitle) && after.Contains("(was General)"));
    res = await PostForm(owner, $"/portal/tickets/{e1.Id}?handler=Edit", new()
    { ["id"] = e1.Id.ToString(), ["subject"] = "", ["description"] = "Kept words" });
    after = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    Check("an emptied subject is REFUSED (not read as 'leave it alone')", after.Contains("needs a subject") && (await Get(e1.Id)).Subject == "Router reboots every hour", Snip(after, "alert-bad"));
    Check("...and what was typed is still in the boxes", after.Contains(">Kept words</textarea>"));
    res = await PostForm(owner, $"/portal/tickets/{e1.Id}?handler=Edit", new()
    { ["id"] = e1.Id.ToString(), ["subject"] = "Router reboots every hour", ["description"] = "" });
    Check("an emptied description is taken away, its words kept in the story", (await Get(e1.Id)).Description == ""
        && (await Notes(e1.Id)).Last().Body.Contains("Description removed. It was:\nIt reboots on the hour."));
    var hidden = await New("Tess's own", TicketCategory.General, who: "Tess Tech");
    var refused = await PostForm(tim, $"/portal/tickets/{hidden.Id}?handler=Edit", new() { ["id"] = hidden.Id.ToString(), ["subject"] = "Mine now" });
    Check("a technician cannot edit a colleague's ticket they cannot see — refused as not found", refused.StatusCode == HttpStatusCode.NotFound && (await Get(hidden.Id)).Subject == "Tess's own", $"{(int)refused.StatusCode}");

    Head("arrival photos may go on any open ticket now (to become a workshop job)");
    var (status, json) = await UploadPhoto(owner, e1.Id);
    Check("a call out at Call out booked takes one", status == 200 && json.Contains("\"id\""), $"{status} {json}");

    Head("the portal's Sort by, remembered");
    var list = WebUtility.HtmlDecode(await owner.GetStringAsync("/portal/tickets?search=Sort&sort=oldest"));
    int ia = list.IndexOf("Sort A"), ib = list.IndexOf("Sort B"), ic = list.IndexOf("Sort C");
    Check("Oldest first is one list in that order", ia > 0 && ia < ic && ic < ib, $"A {ia}, C {ic}, B {ib}");
    // Since §165 every heading sorts: "oldest first" is the # column the right way up, its arrow showing, and Client a heading of its own.
    Check("...the # heading says it is the one in use, oldest first (▲), with Client a column", Regex.IsMatch(list, "aria-sort=\"ascending\"><a class=\"t-sort on\"[^>]*>#") && list.Contains(">Client</a></th>"), Snip(list, "aria-sort"));
    list = WebUtility.HtmlDecode(await owner.GetStringAsync("/portal/tickets?search=Sort"));
    Check("the next visit, with no sort asked, opens the same way (remembered in the browser)", Regex.IsMatch(list, "<option value=\"oldest\" selected"), Snip(list, "Sort by"));
    list = WebUtility.HtmlDecode(await owner.GetStringAsync("/portal/tickets?search=Sort&sort=customer"));
    Check("Customer is the view it always had, gathered by client", list.Contains("<details open class=\"card\"") && !list.Contains("<th>Client</th>"));

    Head("the portal's dashboard tile");
    var home = WebUtility.HtmlDecode(await owner.GetStringAsync("/portal"));
    var tile = Regex.Match(home, "filter=unassigned\"[^>]*>\\s*<div class=\"n\">(\\d+)</div>");
    var shown = WebUtility.HtmlDecode(await owner.GetStringAsync("/portal/tickets?filter=unassigned&sort=newest"));
    int rows = Regex.Matches(shown, "aria-label=\"Open #").Count;
    Check("the number on the tile is the number of tickets the tile opens", tile.Success && int.Parse(tile.Groups[1].Value) == rows, $"tile {(tile.Success ? tile.Groups[1].Value : "?")}, list {rows}");

    Head("the console");
    var con = await Sign(port, "owner@editco.test", Pw);
    con.Ws.Send(new TicketEditMsg { TicketId = e1.Id, Subject = "Router — fixed name", Category = (int)TicketCategory.General });
    var notice = await con.Next<TicketNoticeMsg>(n => n.TicketId == e1.Id, 15000);
    var e1c = await Get(e1.Id);
    Check("TicketEditMsg saves through the same rules", notice?.Message == "Saved." && e1c.Subject == "Router — fixed name" && e1c.Category == TicketCategory.General && e1c.Stage == Workflow.GeneralFirst,
        $"{notice?.Message}{notice?.Error} / {e1c.Subject} / {e1c.Stage}");
    Check("...and the appointment, not sent, is left alone", e1c.AppointmentUtc == future);
    con.Ws.Send(new TicketEditMsg { TicketId = e1.Id, Subject = " " });
    notice = await con.Next<TicketNoticeMsg>(n => n.TicketId == e1.Id && n.Error.Length > 0, 15000);
    Check("...and refuses what the portal refuses, in words", notice?.Error.Contains("needs a subject") == true, notice?.Error ?? "no answer");
    con.Ws.Send(new TicketListRequestMsg { Filter = "open", Search = "Sort", Sort = TicketSort.Appointment });
    var got = await con.Next<TicketListMsg>(m => m.Sort == TicketSort.Appointment, 15000);
    Check("a sorted list request comes back in that order, saying which", got != null && string.Join(" ", got.Tickets.Select(t => t.Subject[^1..])) == "C A B",
        got == null ? "no answer" : string.Join(" ", got.Tickets.Select(t => t.Subject)));
    con.Ws.Send(new TicketListRequestMsg { Filter = "open", Search = "Sort" });
    got = await con.Next<TicketListMsg>(m => m.Sort == "" && m.Tickets.Count == 3, 15000);
    Check("an older console's request (no sort) gets the order it always had", got != null && string.Join(" ", got.Tickets.Select(t => t.Subject[^1..])) == "A C B");
}
finally
{
    try { server?.Kill(entireProcessTree: true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
try { Directory.Delete(dataDir, true); } catch { }
return fail == 0 ? 0 : 1;

// The portal reads a typed appointment in the company's time zone; a new company is Africa/Johannesburg (ZA).
static DateTime CompanyClockLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, CompanyClock.Find("Africa/Johannesburg") ?? TimeZoneInfo.Utc);

static async Task<(int status, string json)> UploadPhoto(HttpClient c, int ticketId)
{
    using var form = new MultipartFormDataContent { { new StringContent(ticketId.ToString()), "ticketId" }, { new StringContent("before"), "kind" } };
    var part = new ByteArrayContent(RemoteSupport.Tools.TestImages.Jpeg());
    part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
    form.Add(part, "files", "arrived.jpg");
    using var req = new HttpRequestMessage(HttpMethod.Post, "/api/portal/photos") { Content = form };
    req.Headers.Add("X-Photo", "1");
    var res = await c.SendAsync(req);
    return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
}
static async Task<HttpResponseMessage> PostForm(HttpClient c, string path, Dictionary<string, string> fields)
{
    // The token from the page the form is on (§80), fetched WITHOUT its handler.
    // Read whatever the status: a ticket they may not see is a 404 since the pentest's F6, and still carries the token.
    var page = await (await c.GetAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""))).Content.ReadAsStringAsync();
    fields["__RequestVerificationToken"] = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
    return await c.PostAsync(path, new FormUrlEncodedContent(fields));
}
static async Task<HttpClient> Portal(int port, string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
    { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "ticket-edit-2026" });
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
    return Regex.Replace(Regex.Replace(html.Substring(i, Math.Min(260, html.Length - i)), "<[^>]+>", " "), "\\s+", " ").Trim();
}
static async Task<TechConsole> Sign(int port, string email, string password)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws));
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
