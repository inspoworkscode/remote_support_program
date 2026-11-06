// =====================================================================================
// ticketsortcheck - proves the ticket list sorted by every column, and the due date (2026-10-26, HANDOVER 165):
//
//   Clint, showing SuperOps' ticket list: "Every column (Status, Subject, Company, Tech, Priority, Last Updated, Due
//   Date) can be sorted by simply clicking on Last Updated, for example. Can we do something similar?" — then: "Lets go
//   with your suggestions and add a due date now as well".
//
// The rules in-process — TicketSort's keys, every column both ways on the server with blanks last, the four old keys as
// they were, the due date (read, refused in the past, changed and said in the story, overdue only while it is the
// bench's), a recurring task's ticket due on its day, "Customer replied" — then a platform of its own: the portal's
// headings, the remembered order, Group by customer (and an old "customer" cookie), the Due and Priority columns, the
// Customer replied pill, New ticket and Edit with a due date; and the console's sorted list, its due date and flag, and
// New ticket and Edit sending a due day over a real socket.
//
//   dotnet run -c Debug --project tools/ticketsortcheck/ticketsortcheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-ticketsortcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
const string Pw = "ticket-sort-2026";
Console.WriteLine($"ticketsortcheck — sorting by every column, and the due date; a platform on port {port}");

Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl, ["Platform:Region"] = "ZA" }).Build();
    var dp = DataProtectionProvider.Create("ticketsortcheck");
    var tenants = new TenantService(factory, cfg, dataDir, dp);
    var (tenant, _) = await tenants.CreateAsync("Sort Co", "sort-co", 7);
    int tid = tenant!.Id;
    const TenantRole O = TenantRole.Owner | TenantRole.Admin, T = TenantRole.Technician;
    await tenants.AddTechnicianAsync(tid, "olga@sort.test", Pw, "Olga Owner", O);
    await tenants.AddTechnicianAsync(tid, "ryan@sort.test", Pw, "Ryan Tech", T);
    await tenants.AddTechnicianAsync(tid, "tim@sort.test", Pw, "Tim Tech", T);
    var clients = new ClientService(factory, dp);
    var (acme, _) = await clients.CreateAsync(tid, "Acme Ltd", "", "", "", "", "");
    var (zulu, _) = await clients.CreateAsync(tid, "Zulu Farms", "", "", "", "", "");
    var tickets = new TicketService(factory);
    async Task<Ticket> Get(int id) { await using var db = new PlatformDb(options); return await db.Tickets.FirstAsync(x => x.Id == id); }
    async Task Set(int id, Action<Ticket> change) { await using var db = new PlatformDb(options); var t = await db.Tickets.FirstAsync(x => x.Id == id); change(t); await db.SaveChangesAsync(); }
    async Task<TicketNote> LastNote(int id) { await using var db = new PlatformDb(options); return await db.TicketNotes.Where(n => n.TicketId == id).OrderByDescending(n => n.Id).FirstAsync(); }
    var today = DateTime.UtcNow.Date;
    DateTime Day(int plus) => DateTime.SpecifyKind(today.AddDays(plus), DateTimeKind.Utc);

    // =========================================================== TicketSort
    Head("the keys");
    Check("a column on its own goes its first way: the newest update, the highest number, the most urgent first; the rest A–Z",
        TicketSort.Of("updated") == "updated:desc" && TicketSort.Of("number") == "number:desc" && TicketSort.Of("priority") == "priority:desc"
        && TicketSort.Of("subject") == "subject:asc" && TicketSort.Of("due") == "due:asc");
    Check("nothing, or nonsense, is last updated newest first", TicketSort.Of(null) == "updated:desc" && TicketSort.Of("drop table") == "updated:desc");
    Check("clicking the column in use turns it round; another column starts its own way",
        TicketSort.Toggle("updated:desc", "updated") == "updated:asc" && TicketSort.Toggle("updated:asc", "updated") == "updated:desc"
        && TicketSort.Toggle("updated:desc", "client") == "client:asc" && TicketSort.Toggle("client:asc", "priority") == "priority:desc");
    Check("the arrow: ▼ down (Z–A, newest, most urgent), ▲ up, nothing on the others",
        TicketSort.Arrow("due:asc", "due") == "▲" && TicketSort.Arrow("due:desc", "due") == "▼" && TicketSort.Arrow("due:desc", "client") == "");
    Check("the four keys from before are kept, and say which column they were",
        TicketSort.Of("newest") == "newest" && TicketSort.Parse("newest") == ("number", true) && TicketSort.Parse("oldest") == ("number", false)
        && TicketSort.Parse("appointment") == ("due", false));

    // =========================================================== every column, both ways
    Head("every column, both ways, on the server");
    var t1 = await tickets.CreateAsync(tid, "Alpha printer", "", "Olga Owner", TicketSource.Manual, assignedTo: "Tim Tech", clientId: acme!.Id);
    var t2 = await tickets.CreateAsync(tid, "bravo router", "", "Olga Owner", TicketSource.Manual, assignedTo: "Ryan Tech", clientId: zulu!.Id, priority: TicketPriority.Urgent);
    var t3 = await tickets.CreateAsync(tid, "Charlie laptop", "", "Olga Owner", TicketSource.Manual, priority: TicketPriority.Low);
    var t4 = await tickets.CreateAsync(tid, "delta call out", "", "Olga Owner", TicketSource.Manual, assignedTo: "Ryan Tech", clientId: acme.Id,
        priority: TicketPriority.High, category: TicketCategory.CallOut);
    var now = DateTime.UtcNow;
    await Set(t1.Id, x => { x.DueDate = Day(5); x.UpdatedUtc = now.AddHours(-3); });
    await Set(t2.Id, x => { x.DueDate = Day(1); x.UpdatedUtc = now.AddHours(-1); });
    await Set(t3.Id, x => { x.UpdatedUtc = now.AddHours(-2); x.Status = TicketStatus.InProgress; });
    await Set(t4.Id, x => { x.AppointmentUtc = now.AddDays(3); x.UpdatedUtc = now.AddMinutes(-10); });
    var num = new Dictionary<int, string> { [t1.Id] = "1", [t2.Id] = "2", [t3.Id] = "3", [t4.Id] = "4" };
    async Task<string> Order(string key) =>
        string.Join(" ", (await tickets.ListAsync(tid, "all", "", "Olga Owner", O, sort: key)).Select(t => num.TryGetValue(t.Id, out var n) ? n : "?"));
    async Task Both(string col, string up, string down)
    {
        var a = await Order(col + ":asc"); var d = await Order(col + ":desc");
        Check($"{col}: up {up}, down {down}", a == up && d == down, $"up {a}, down {d}");
    }
    await Both("number", "1 2 3 4", "4 3 2 1");
    await Both("subject", "1 2 3 4", "4 3 2 1");                // A, b, C, d: capitals do not decide
    await Both("type", "4 2 3 1", "2 3 1 4");                   // by the name: Call out before General
    await Both("client", "4 1 2 3", "2 4 1 3");                 // none last both ways; ties newest update first
    await Both("assigned", "4 2 1 3", "1 4 2 3");               // nobody last both ways
    await Both("status", "4 2 1 3", "3 2 1 4");
    await Both("priority", "3 1 4 2", "2 4 1 3");
    await Both("updated", "1 3 2 4", "4 2 3 1");
    await Both("due", "2 4 1 3", "1 4 2 3");                    // the due date, else the appointment; neither last both ways
    Check("the four old keys sort as they did: Newest first, Appointment", await Order("newest") == "4 3 2 1" && await Order("appointment") == "4 2 3 1",
        $"{await Order("newest")} / {await Order("appointment")}");
    var first = await tickets.ListAsync(tid, "all", "", "Olga Owner", O, take: 1, sort: "due:desc");
    Check("sorted BEFORE the first few are taken", first.Count == 1 && first[0].Id == t1.Id);

    // =========================================================== the due date
    Head("the due date");
    Check("a day from a date box is read as the day, a blank is none, nonsense is refused",
        TicketDue.TryRead("2026-11-04", out var read) && read == new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc)
        && TicketDue.TryRead("", out var blank) && blank == null && !TicketDue.TryRead("next Tuesday", out _));
    Check("never a day already gone; today and later are fine",
        TicketDue.Problem(Day(-2)).Contains("already gone") && TicketDue.Problem(Day(2)) == "" && TicketDue.Problem(null) == "");
    var (why, said) = await tickets.EditAsync(tid, t3.Id, "Tim Tech", T, new TicketEdit(DueGiven: true, DueDate: Day(2)));
    Check("Edit sets it, and the story says it and what it was", why == "" && (await Get(t3.Id)).DueDate == Day(2)
        && (await LastNote(t3.Id)).Body.Contains($"Due: {TicketDue.Say(Day(2))} (was not set)"), $"{why} / {(await LastNote(t3.Id)).Body}");
    (why, said) = await tickets.EditAsync(tid, t3.Id, "Tim Tech", T, new TicketEdit(DueGiven: true, DueDate: Day(2).AddHours(15)));
    Check("the same day again is not a change", why == "" && said.Count == 0);
    (why, _) = await tickets.EditAsync(tid, t3.Id, "Tim Tech", T, new TicketEdit(Subject: "Charlie laptop (keyboard)", DueGiven: true, DueDate: Day(-3)));
    Check("a day already gone is refused — and NOTHING of that edit is applied", why.Contains("already gone")
        && (await Get(t3.Id)).DueDate == Day(2) && (await Get(t3.Id)).Subject == "Charlie laptop", why);
    (why, _) = await tickets.EditAsync(tid, t3.Id, "Tim Tech", T, new TicketEdit(Subject: "Charlie laptop (keyboard)"));
    Check("an edit that does not send the due date leaves it alone", why == "" && (await Get(t3.Id)).DueDate == Day(2));
    (why, _) = await tickets.EditAsync(tid, t3.Id, "Tim Tech", T, new TicketEdit(DueGiven: true, DueDate: null));
    Check("it may be taken away, said with what it was", why == "" && (await Get(t3.Id)).DueDate == null
        && (await LastNote(t3.Id)).Body.Contains($"Due date removed (was {TicketDue.Say(Day(2))})"), (await LastNote(t3.Id)).Body);
    var done = await tickets.CreateAsync(tid, "Done job", "", "Olga Owner", TicketSource.Manual);
    await Set(done.Id, x => x.Status = TicketStatus.Closed);
    (why, _) = await tickets.EditAsync(tid, done.Id, "Olga Owner", O, new TicketEdit(DueGiven: true, DueDate: Day(4)));
    Check("a closed job's due date stays as it is", why.Contains("closed"), why);

    Head("overdue");
    var late = await tickets.CreateAsync(tid, "Late job", "", "Olga Owner", TicketSource.Manual, assignedTo: "Tim Tech");
    await Set(late.Id, x => x.DueDate = Day(-2));
    var lt = await Get(late.Id);
    Check("past its day, open, on the bench: overdue — in the summary too", TicketDue.IsOverdue(lt) && TicketService.ToSummary(lt).Overdue && TicketService.ToSummary(lt).DueDate == Day(-2));
    lt.Lane = (int)WorkLane.Office;
    Check("gone to be invoiced: the people on it have done their part", !TicketDue.IsOverdue(lt));
    lt.Lane = (int)WorkLane.Bench; lt.Status = TicketStatus.Closed;
    Check("closed: not overdue", !TicketDue.IsOverdue(lt));
    Check("due today: not yet", !TicketDue.IsOverdue(new Ticket { DueDate = Day(0), Lane = 0, Status = TicketStatus.Open }, today));
    var facts = TicketService.WorkflowFacts(await Get(late.Id));
    Check("the ticket's facts say Due, and overdue", facts.Any(f => f.Label == "Due" && f.Value == TicketDue.Say(Day(-2)) + " — overdue"),
        string.Join(" | ", facts.Select(f => f.Label + ": " + f.Value)));

    Head("a recurring task's ticket is due on its day");
    var rec = new RecurringTaskService(factory, tickets);
    var noon = DateTime.SpecifyKind(today.AddHours(10), DateTimeKind.Utc);
    var (task, rerr, opened) = await rec.CreateAsync(tid, "Olga Owner",
        new RecurringInput { Title = "Check the backups", Kind = "Maintenance", AssignedTo = "Tim Tech", Repeats = Repeats.Daily }, noon);
    var ro = opened == null ? null : await Get(opened.Id);
    Check("its due date is its day (and its schedule's day is kept apart)", ro != null && ro.DueDate != null && ro.DueDate == ro.DueOn, $"{rerr} {ro?.DueDate:u} / {ro?.DueOn:u}");

    Head("Customer replied");
    async Task<bool> Replied(int id) { await using var db = new PlatformDb(options); return (await TicketService.CustomerRepliedAsync(db, tid, new[] { id })).Count > 0; }
    Check("no reply: not flagged", !await Replied(t2.Id));
    await tickets.AddCustomerReplyAsync(tid, t2.Id, "Zak Zulu", "It is still slow.", "Email");
    Check("the customer's emailed reply is the newest entry: flagged", await Replied(t2.Id));
    await tickets.AddNoteAsync(tid, t2.Id, "Ryan Tech", "Ryan Tech connected to ZULU-SRV.", true, automatic: true);
    Check("a session's automatic line is not an answer", await Replied(t2.Id));
    await tickets.AddNoteAsync(tid, t2.Id, "Ryan Tech", "Looking at it now.", false);
    Check("somebody's comment is: no longer flagged", !await Replied(t2.Id));
    await tickets.AddCustomerReplyAsync(tid, t1.Id, "Anna Acme", "Any news?", "Email");   // for the portal and console below

    // =========================================================== the platform
    Head("the platform");
    var pinfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in new[] { "run", "-c", "Debug", "--project", proj, "--no-build", "--", "run" }) pinfo.ArgumentList.Add(arg);
    pinfo.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(pinfo.Environment, dataDir);
    pinfo.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
    pinfo.Environment["RSP_Platform__PublicUrl"] = baseUrl;
    pinfo.Environment["RSP_Urls"] = baseUrl;
    pinfo.Environment["ASPNETCORE_URLS"] = baseUrl;
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var built = await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + built); return 1; }
    server = Process.Start(pinfo)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    Head("the portal's list");
    var (olga, jar) = await Portal(port, "olga@sort.test");
    var byNum = new Dictionary<int, string> { [t1.Number] = "1", [t2.Number] = "2", [t3.Number] = "3", [t4.Number] = "4" };
    string Rows(string html) => string.Join(" ", Regex.Matches(html, "<td class=\"t-num\"><strong>#(\\d+)</strong>")
        .Select(m => int.Parse(m.Groups[1].Value)).Where(byNum.ContainsKey).Select(n => byNum[n]));
    var page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/tickets?filter=all&sort=priority"));
    Check("?sort=priority: the most urgent first", Rows(page) == "2 4 1 3", Rows(page));
    Check("...the Priority heading says so (▼, aria-sort) and turns round when clicked",
        Regex.IsMatch(page, "<th aria-sort=\"descending\"><a class=\"t-sort on\" href=\"[^\"]*sort=priority%3Aasc[^\"]*\"[^>]*>Priority <span aria-hidden=\"true\">▼</span>"),
        Snip(page, "aria-sort"));
    Check("...every column is a heading that sorts", TicketSort.Columns.All(c => page.Contains($">{c.Heading}</a></th>") || page.Contains($">{c.Heading} <span")));
    Check("...each heading keeps the page's filter", Regex.IsMatch(page, "href=\"/portal/tickets\\?filter=all&sort=client%3Aasc\""), Snip(page, "sort=client"));
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/tickets?filter=all"));
    Check("the next visit, with no sort asked, opens the same way (remembered in the browser)", Rows(page) == "2 4 1 3", Rows(page));
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/tickets?filter=all&sort=due:asc"));
    Check("Due: soonest first, the appointment standing in for a call out, the ones with neither last", Rows(page).StartsWith("2 4 1") && Rows(page).EndsWith("3"), Rows(page));
    Check("...the due day is shown, the call out's appointment with 📅 and its time", page.Contains(Day(1).ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture)) && page.Contains("📅 "));
    Check("...an overdue job is red and says Overdue", Regex.IsMatch(page, "<span class=\"t-late\">[^<]+</span> <span class=\"pill pill-bad\">Overdue</span>"));
    Check("Priority has its words and its dot; Last updated says how long ago", page.Contains("class=\"t-dot\"") && page.Contains(">Urgent</td>") && Regex.IsMatch(page, "class=\"t-upd\" title=\"[^\"]+\">\\d+ (min|h) ago"));
    Check("Customer replied: on the ticket whose newest entry is the customer's, and only there",
        Regex.Matches(page, "Customer replied").Count == 1 && Regex.IsMatch(page, $"#{t1.Number}</strong>(?:(?!</tr>).)*Customer replied", RegexOptions.Singleline));
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/tickets?filter=all&group=1"));
    Check("Group by customer: gathered under each customer, the Machine column for Client", page.Contains("<details open class=\"card\"") && page.Contains("<th>Machine</th>")
        && page.Contains("id=\"tGroupTick\" checked"));
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/tickets?filter=all"));
    Check("...remembered", page.Contains("<details open class=\"card\""));
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/tickets?filter=all&group=0"));
    Check("...and unticked, one list again", !page.Contains("<details open class=\"card\""));
    jar.Add(new Uri(baseUrl), new Cookie("rsp_ticket_sort", "customer", "/portal"));
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/tickets?filter=all"));
    Check("a browser that chose \"Customer\" in the old Sort box still sees it grouped", page.Contains("<details open class=\"card\""));
    Check("the Sort box is gone (one control beats two) — only the phone's stand-in remains",
        !page.Contains("<option value=\"customer\"") && page.Contains("class=\"t-phone-sort\""));

    Head("the portal's New ticket and Edit");
    var res = await PostForm(olga, "/portal/tickets/new", new() { ["Category"] = "4", ["Subject"] = "Due from the portal", ["Priority"] = "1", ["DueDate"] = Day(3).ToString("yyyy-MM-dd") });
    Ticket? fromPortal; await using (var db = new PlatformDb(options)) fromPortal = await db.Tickets.FirstOrDefaultAsync(t => t.TenantId == tid && t.Subject == "Due from the portal");
    Check("New ticket takes a due date", fromPortal?.DueDate == Day(3), $"{(int)res.StatusCode} {fromPortal?.DueDate:u}");
    res = await PostForm(olga, "/portal/tickets/new", new() { ["Category"] = "4", ["Subject"] = "Due yesterday", ["Priority"] = "1", ["DueDate"] = Day(-3).ToString("yyyy-MM-dd") });
    var body = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    bool madeLate; await using (var db = new PlatformDb(options)) madeLate = await db.Tickets.AnyAsync(t => t.TenantId == tid && t.Subject == "Due yesterday");
    Check("...and refuses a day already gone, making nothing", !madeLate && body.Contains("already gone"), Snip(body, "alert"));
    var edit = await PostForm(olga, $"/portal/t/{t4.Number}?handler=Edit", new() { ["subject"] = "delta call out", ["description"] = "", ["dueShown"] = "true", ["dueDate"] = Day(6).ToString("yyyy-MM-dd") });
    var after = WebUtility.HtmlDecode(await edit.Content.ReadAsStringAsync());
    Check("Edit the ticket sets it, and says so", (await Get(t4.Id)).DueDate == Day(6) && after.Contains("Saved — the change is in the ticket's story."), Snip(after, "alert"));
    Check("...the edit form shows it", after.Contains($"value=\"{Day(6):yyyy-MM-dd}\"") && after.Contains("id=\"teDue\""));
    Check("...and the ticket's facts say Due", after.Contains(">Due<") && after.Contains(TicketDue.Say(Day(6))));

    Head("the console");
    var con = await Sign(port, "olga@sort.test", Pw);
    con.Ws.Send(new TicketListRequestMsg { Filter = "all", Search = "", Sort = "priority:desc" });
    var got = await con.Next<TicketListMsg>(m => m.Sort == "priority:desc", 15000);
    var order = got == null ? "" : string.Join(" ", got.Tickets.Where(t => num.ContainsKey(t.Id)).Select(t => num[t.Id]));
    Check("a sort by a column comes back in that order, saying which", order == "2 4 1 3", order);
    var s1 = got?.Tickets.FirstOrDefault(t => t.Id == t1.Id); var sl = got?.Tickets.FirstOrDefault(t => t.Id == late.Id);
    Check("...each ticket says Customer replied, its due day and overdue", s1 != null && s1.CustomerReplied && s1.DueDate == Day(5)
        && sl != null && sl.Overdue && got!.Tickets.Count(t => t.CustomerReplied) == 1);
    con.Ws.Send(new TicketCreateMsg { Subject = "Due from the console", Category = (int)TicketCategory.General, AssignToMe = true, DueDate = Day(4) });
    Ticket? fromConsole = null;
    for (int i = 0; i < 50 && fromConsole == null; i++)
    {
        await Task.Delay(200);
        await using var db = new PlatformDb(options);
        fromConsole = await db.Tickets.FirstOrDefaultAsync(t => t.TenantId == tid && t.Subject == "Due from the console");
    }
    Check("New ticket from the console keeps the DAY it was sent", fromConsole?.DueDate == Day(4), $"{fromConsole?.DueDate:u}");
    con.Ws.Send(new TicketCreateMsg { Subject = "Console, too late", Category = (int)TicketCategory.General, AssignToMe = true, DueDate = Day(-3) });
    var refused = await con.Next<ErrorMsg>(m => m.Message.Contains("already gone"), 15000);
    Check("...and refuses a day already gone, in words", refused != null);
    con.Ws.Send(new TicketEditMsg { TicketId = fromConsole?.Id ?? 0, DueGiven = true, DueDate = null });
    var notice = await con.Next<TicketNoticeMsg>(n => n.TicketId == fromConsole?.Id, 15000);
    Check("Edit from the console takes it away", notice?.Message == "Saved." && (await Get(fromConsole!.Id)).DueDate == null, $"{notice?.Message}{notice?.Error}");
    con.Ws.Send(new TicketEditMsg { TicketId = fromConsole.Id, Subject = "Due from the console (renamed)" });
    notice = await con.Next<TicketNoticeMsg>(n => n.TicketId == fromConsole.Id, 15000);
    Check("...and an older console's edit (no due date) leaves it as it is", notice?.Message == "Saved." && (await Get(fromConsole.Id)).DueDate == null);
    con.Ws.Send(new TicketListRequestMsg { Filter = "all", Search = "", Sort = TicketSort.Newest });
    var old = await con.Next<TicketListMsg>(m => m.Sort == TicketSort.Newest, 15000);
    Check("an older console's Sort box key still sorts as it did", old != null && old.Tickets.Select(t => t.Number).SequenceEqual(old.Tickets.Select(t => t.Number).OrderByDescending(n => n)));
}
finally
{
    try { server?.Kill(entireProcessTree: true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
try { Directory.Delete(dataDir, true); } catch { }
return fail == 0 ? 0 : 1;

static async Task<HttpResponseMessage> PostForm(HttpClient c, string path, Dictionary<string, string> fields)
{
    var page = await (await c.GetAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""))).Content.ReadAsStringAsync();
    fields["__RequestVerificationToken"] = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
    return await c.PostAsync(path, new FormUrlEncodedContent(fields));
}
static async Task<(HttpClient, CookieContainer)> Portal(int port, string email)
{
    var jar = new CookieContainer();
    var c = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true })
    { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "ticket-sort-2026" });
    return (c, jar);
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
    /// <summary>The first message of this kind that matches and has not been taken before.</summary>
    public async Task<T?> Next<T>(Func<T, bool> where, int ms) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var m = _got.OfType<T>().FirstOrDefault(where);
                if (m != null) { _got.Remove(m); return m; }
            }
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
