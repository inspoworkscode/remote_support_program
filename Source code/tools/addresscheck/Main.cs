// =====================================================================================
// addresscheck - proves the portal names things by per-company numbers, not database ids (HANDOVER 159).
//
//   The outside review's F8: one id sequence for every company, and the portal showed it — /portal/tickets/129 for a
//   company's ticket #2 — so the gap between two of a company's own tickets told it the whole platform's volume.
//   Now: /portal/t/{n}, /portal/c/{n}, /portal/a/{n}, /portal/r/{n} by number within the company, and stored files by a
//   random token (/portal/p|f|i|v/{token}). The old id addresses redirect for the company's own people and are a 404
//   for anybody else, the same 404 as a number that does not exist.
//
// Two companies, the OTHER one seeded first so the first company's ids and numbers differ (or nothing is proved):
//   the backfill (rows written before the migration, numbered 1, 2, 3 per company in id order, every file a token);
//   numbers given on save; the old-address translator; the hand-off's allow-list; then a platform of its own — the
//   new addresses, the redirects, the 404s, every page's source searched for a database id, forms posted by number
//   with a forged id, uploads by number, files by token, and the console's hand-off in both shapes.
//
//   dotnet run -c Debug --project tools/addresscheck/addresscheck.csproj          (add -- --hold to walk it in a browser after)
// =====================================================================================

using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-addresscheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
const string Pw = "address-check-2026";
Console.WriteLine($"addresscheck — per-company numbers in portal addresses, on {HarnessDb.Name}; a platform on port {port}");

Process? server = null;
try
{
    // =========================================================== the backfill
    Head("the backfill: rows from before the migration, numbered per company in id order");
    await BackfillAsync();

    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl, ["Platform:Region"] = "ZA" }).Build();
    var dp = DataProtectionProvider.Create("addresscheck");
    var tenants = new TenantService(factory, cfg, dataDir, dp);
    var photos = new TicketPhotoService(factory, NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    var clients = new ClientService(factory, dp);
    var kb = new KnowledgeBaseService(factory, NullLogger<KnowledgeBaseService>.Instance, dataDir);
    var files = new TicketAttachmentService(factory, NullLogger<TicketAttachmentService>.Instance, dataDir);
    var recs = new RecordingService(factory, NullLogger<RecordingService>.Instance, dataDir);
    var recurring = new RecurringTaskService(factory, tickets);
    const TenantRole O = TenantRole.Owner | TenantRole.Admin;

    // =========================================================== two companies, the other one FIRST
    Head("two companies — Busy Co first, so Acme's ids are not its numbers");
    var (busyT, _) = await tenants.CreateAsync("Busy Co", "busy-co", 5);
    int busy = busyT!.Id;
    await tenants.AddTechnicianAsync(busy, "owner@busy.test", Pw, "Bob Busy", O);
    var busyTickets = new List<Ticket>();
    for (int i = 1; i <= 6; i++) busyTickets.Add(await tickets.CreateAsync(busy, $"BUSY-TICKET-{i}", "Busy's own.", "Bob Busy", TicketSource.Manual, assignedTo: "Bob Busy"));
    var busyClients = new List<Client>();
    for (int i = 1; i <= 5; i++) busyClients.Add((await clients.CreateAsync(busy, $"BUSY-CLIENT-{i}", "", "", "", "", "")).client!);
    var busyArticles = new List<KbArticle>();
    for (int i = 1; i <= 4; i++) busyArticles.Add((await kb.CreateAsync(busy, $"BUSY-ARTICLE-{i}", "Busy's steps.", null, null, null, "Bob Busy", O)).article!);
    for (int i = 1; i <= 4; i++) await photos.AddAsync(busy, TestImages.Jpeg(), $"busy-{i}.jpg", PhotoKind.Before, "Bob Busy", busyTickets[0].Id, "");
    var (busyImg, _) = await kb.AddImageAsync(busy, TestImages.Png(), "Bob Busy");
    var (busyTask, _, _) = await recurring.CreateAsync(busy, "Bob Busy", new RecurringInput { Title = "BUSY-TASK", Kind = "Maintenance", AssignedTo = "Bob Busy", Repeats = Repeats.Monthly, MonthDay = 1 });
    var busyTask2 = (await recurring.CreateAsync(busy, "Bob Busy", new RecurringInput { Title = "BUSY-TASK-2", Kind = "Maintenance", AssignedTo = "Bob Busy", Repeats = Repeats.Monthly, MonthDay = 1 })).task!;

    var (acmeT, _) = await tenants.CreateAsync("Acme IT", "acme-it", 5);
    int acme = acmeT!.Id;
    await tenants.AddTechnicianAsync(acme, "owner@acme.test", Pw, "Olga Owner", O);
    await tenants.AddTechnicianAsync(acme, "tess@acme.test", Pw, "Tess Tech", TenantRole.Technician);
    var (c1, _) = await clients.CreateAsync(acme, "ACME-CLIENT-ONE", "Jane", "011 555 0101", "jane@one.test", "", "");
    var (c2, _) = await clients.CreateAsync(acme, "ACME-CLIENT-TWO", "", "", "", "", "");
    var w1 = await tickets.CreateAsync(acme, "ACME-WORKSHOP-ONE", "It will not boot.", "Olga Owner", TicketSource.Manual,
        assignedTo: "Olga Owner", category: TicketCategory.Workshop, clientId: c1!.Id);
    var g2 = await tickets.CreateAsync(acme, "ACME-GENERAL-TWO", "A printer.", "Olga Owner", TicketSource.Manual, assignedTo: "Olga Owner", clientId: c1.Id);
    var old3 = await tickets.CreateAsync(acme, "ACME-CLASSIC-THREE", "An old ticket with no route.", "Olga Owner", TicketSource.Manual,
        assignedTo: "Olga Owner", clientId: c1.Id, extra: t => t.Stage = "");
    var shot4 = await tickets.CreateAsync(acme, "ACME-REMOTE-FOUR", "Their screen.", "Olga Owner", TicketSource.Call, assignedTo: "Olga Owner",
        category: TicketCategory.RemoteSupport, clientId: c1.Id);
    // The classic page is a ticket with no route; ticket 1's work is recorded as done, so it may be written up (§80).
    await using (var db = new PlatformDb(options))
    {
        var o3 = await db.Tickets.FirstAsync(t => t.Id == old3.Id); o3.Stage = "";
        var o1 = await db.Tickets.FirstAsync(t => t.Id == w1.Id); o1.Outcome = "Repaired";
        await db.SaveChangesAsync();
    }
    var wPhotos = new List<TicketPhoto>();
    for (int i = 1; i <= 3; i++) wPhotos.Add((await photos.AddAsync(acme, TestImages.Jpeg(), $"in-{i}.jpg", PhotoKind.Before, "Olga Owner", w1.Id, "")).photo!);
    var oPhoto = (await photos.AddAsync(acme, TestImages.Jpeg(), "classic.jpg", PhotoKind.Before, "Olga Owner", old3.Id, "")).photo!;
    var shot = (await photos.AddAsync(acme, TestImages.Jpeg(), "screen.jpg", PhotoKind.Screenshot, "Olga Owner", shot4.Id, "")).photo!;
    var (att, attWhy) = await files.AddAsync(acme, g2.Id, TestImages.Png(), "note.png", "Olga Owner");
    await tickets.AddNoteAsync(acme, g2.Id, "Olga Owner", "See the picture.", false, attachments: new[] { att!.Id });
    var (img, _) = await kb.AddImageAsync(acme, TestImages.Png(), "Olga Owner");
    var (a1, _) = await kb.CreateAsync(acme, "ACME-ARTICLE-ONE", $"1. Open it\n{KbText.ImageTag(img!.Id)}\n2. Close it", c1.Id, null, w1.Id, "Olga Owner", O);
    var (a2, _) = await kb.CreateAsync(acme, "ACME-ARTICLE-TWO", "General words.", null, null, null, "Olga Owner", O);
    var (task, _, _) = await recurring.CreateAsync(acme, "Olga Owner", new RecurringInput { Title = "ACME-TASK", Kind = "Maintenance", AssignedTo = "Olga Owner", Repeats = Repeats.Monthly, MonthDay = 1, ClientId = c1.Id });
    SessionRecording rec;
    await using (var db = new PlatformDb(options))
    {
        rec = new SessionRecording { TenantId = acme, TicketId = shot4.Id, SessionId = "s-1", MachineName = "PC-1", FileName = "rec-1.mp4", Bytes = 8, Seconds = 1, RecordedBy = "Olga Owner" };
        db.SessionRecordings.Add(rec);
        await db.SaveChangesAsync();
    }
    Directory.CreateDirectory(Path.GetDirectoryName(recs.FilePath(acme, rec.FileName))!);
    await File.WriteAllBytesAsync(recs.FilePath(acme, rec.FileName), new byte[] { 0, 0, 0, 8, 0x66, 0x74, 0x79, 0x70 });

    Check("Acme's ticket #1 is not database id 1 (the other company was first)", w1.Number == 1 && w1.Id != 1 && g2.Number == 2, $"#{w1.Number} is id {w1.Id}");
    Check("Acme's client #1 is not database id 1", c1.Number == 1 && c1.Id > busyClients.Count && c2!.Number == 2, $"#{c1.Number} is id {c1.Id}");
    Check("Acme's article #1 is not database id 1", a1!.Number == 1 && a1.Id > busyArticles.Count && a2!.Number == 2, $"#{a1.Number} is id {a1.Id}");
    Check("Acme's recurring task #1 is not database id 1", task!.Number == 1 && task.Id > 2 && busyTask!.Number == 1 && busyTask2.Number == 2, $"#{task.Number} is id {task.Id}");
    Check("every stored file has a token of the right shape, all different",
        new[] { wPhotos[0].Token, shot.Token, att.Token, img.Token, rec.Token, busyImg!.Token }.All(FileToken.Looks)
        && new[] { wPhotos[0].Token, wPhotos[1].Token, shot.Token, att.Token, img.Token, rec.Token }.Distinct().Count() == 6, wPhotos[0].Token);

    // =========================================================== numbers given on save
    Head("a new row gets its company's next number, whatever made it");
    var (c3, _) = await clients.CreateAsync(acme, "ACME-CLIENT-THREE", "", "", "", "", "");
    var (b6, _) = await clients.CreateAsync(busy, "BUSY-CLIENT-6", "", "", "", "", "");
    Check("Add a client: Acme's third is #3, Busy's sixth is #6", c3!.Number == 3 && b6!.Number == 6, $"{c3.Number}, {b6!.Number}");
    await using (var db = new PlatformDb(options))
    {
        db.Clients.AddRange(new Client { TenantId = acme, Name = "IMPORT-A" }, new Client { TenantId = busy, Name = "IMPORT-B" }, new Client { TenantId = acme, Name = "IMPORT-C" });
        await db.SaveChangesAsync();
        var got = await db.Clients.Where(c => c.Name.StartsWith("IMPORT-")).OrderBy(c => c.Name).Select(c => c.Number).ToListAsync();
        Check("several in one save (the import's way), two companies at once: 4, 7, 5", string.Join(",", got) == "4,7,5", string.Join(",", got));
    }
    var (a3, _) = await kb.CreateAsync(acme, "ACME-ARTICLE-THREE", "x", null, null, null, "Olga Owner", O);
    Check("a new article is #3", a3!.Number == 3, a3.Number.ToString());
    await using (var db = new PlatformDb(options))
    {
        bool refused = false;
        try { db.Clients.Add(new Client { TenantId = acme, Name = "DUPLICATE", Number = 1 }); await db.SaveChangesAsync(); }
        catch (DbUpdateException) { refused = true; }
        Check("a number given twice in one company is refused by the database itself (unique index)", refused);
    }

    // =========================================================== the translator, in-process
    Head("the old addresses, translated within the company");
    async Task<string?> Old(int tenantId, string path) { await using var db = new PlatformDb(options); return await PortalAddresses.FromOldAsync(db, tenantId, path); }
    Check("Acme's /portal/tickets/{id}?tab=photos → /portal/t/1?tab=photos", await Old(acme, $"/portal/tickets/{w1.Id}?tab=photos") == "/portal/t/1?tab=photos", await Old(acme, $"/portal/tickets/{w1.Id}?tab=photos") ?? "null");
    Check("...a client, an article, its editor, a recurring task", await Old(acme, $"/portal/clients/{c1.Id}") == "/portal/c/1"
        && await Old(acme, $"/portal/kb/{a1.Id}?version=1") == "/portal/a/1?version=1" && await Old(acme, $"/portal/kb/edit/{a1.Id}") == "/portal/a/1/edit"
        && await Old(acme, $"/portal/recurring/{task.Id}") == "/portal/r/1");
    Check("...a photo, a comment's file, an article's picture, a recording — by token", await Old(acme, $"/portal/photo/{shot.Id}") == "/portal/p/" + shot.Token
        && await Old(acme, $"/portal/attachment/{att.Id}") == "/portal/f/" + att.Token && await Old(acme, $"/portal/kb/image/{img.Id}") == "/portal/i/" + img.Token
        && await Old(acme, $"/portal/recordings/{rec.Id}") == "/portal/v/" + rec.Token);
    Check("...?client={id} and ?ticket={id} become ?c= and ?t= (0 stays General)", await Old(acme, $"/portal/kb?client={c1.Id}") == "/portal/kb?c=1"
        && await Old(acme, "/portal/kb?client=0") == "/portal/kb?c=0" && await Old(acme, $"/portal/kb/edit?ticket={w1.Id}") == "/portal/kb/edit?t=1"
        && await Old(acme, $"/portal/tickets?client={c1.Id}&filter=closed") == "/portal/tickets?c=1&filter=closed");
    Check("Busy asking for Acme's ids gets nothing — every kind", await Old(busy, $"/portal/tickets/{w1.Id}") == null && await Old(busy, $"/portal/clients/{c1.Id}") == null
        && await Old(busy, $"/portal/kb/{a1.Id}") == null && await Old(busy, $"/portal/recurring/{task.Id}") == null && await Old(busy, $"/portal/photo/{shot.Id}") == null
        && await Old(busy, $"/portal/attachment/{att.Id}") == null && await Old(busy, $"/portal/kb/image/{img.Id}") == null && await Old(busy, $"/portal/recordings/{rec.Id}") == null
        && await Old(busy, $"/portal/kb?client={c1.Id}") == null);
    Check("an address that was never an old shape comes back unchanged", await Old(acme, "/portal/tickets?filter=asked") == "/portal/tickets?filter=asked" && await Old(acme, "/portal/t/1") == "/portal/t/1");

    Head("the hand-off's allow-list: the new shapes, and the old kept for consoles not yet updated");
    string tok = shot.Token;
    foreach (var p in new[] { "/portal/t/12", "/portal/t/12?open=quote-requested", "/portal/t/12?tab=photos", "/portal/a/4", "/portal/r/2", "/portal/kb?c=3",
                 "/portal/kb/edit?t=5", "/portal/kb/edit?c=5", "/portal/p/" + tok, "/portal/f/" + tok, $"/portal/tickets/{w1.Id}", $"/portal/kb?client={c1.Id}", $"/portal/photo/{shot.Id}", $"/portal/attachment/{att.Id}" })
        Check($"allowed: {p}", PortalHandoffService.SafePath(p) == p, PortalHandoffService.SafePath(p));
    foreach (var p in new[] { "/portal/t/12?evil=1", "/portal/t/x", "/portal/p/../../etc", "/portal/p/" + tok.ToUpperInvariant(), "//evil.example/portal/t/1", "/portal/a/1/edit/../x" })
        Check($"replaced by the queue: {p}", PortalHandoffService.SafePath(p) == PortalHandoffService.DefaultPath, PortalHandoffService.SafePath(p));

    // =========================================================== the platform
    Head("the platform");
    var ps = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "-c", "Debug", "--project", proj, "--no-build", "--", "run" }) ps.ArgumentList.Add(a);
    ps.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(ps.Environment, dataDir);
    ps.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
    ps.Environment["RSP_Platform__PublicUrl"] = baseUrl;
    ps.Environment["RSP_Urls"] = baseUrl;
    ps.Environment["ASPNETCORE_URLS"] = baseUrl;
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var built = await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + built); return 1; }
    server = Process.Start(ps)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
    var (A, Araw) = await Portal(port, "owner@acme.test");
    var (B, Braw) = await Portal(port, "owner@busy.test");

    Head("the new addresses open the company's own");
    var (st, page) = await Get(A, "/portal/t/1");
    Check("Acme's /portal/t/1 is Acme's ticket #1", st == 200 && page.Contains("ACME-WORKSHOP-ONE"), $"{st}");
    (st, page) = await Get(B, "/portal/t/1");
    Check("Busy's /portal/t/1 is Busy's ticket #1, never Acme's", st == 200 && page.Contains("BUSY-TICKET-1") && !page.Contains("ACME-"), $"{st}");
    (st, page) = await Get(A, "/portal/c/1");
    Check("Acme's /portal/c/1 is ACME-CLIENT-ONE", st == 200 && page.Contains("ACME-CLIENT-ONE"), $"{st}");
    (st, page) = await Get(B, "/portal/c/1");
    Check("Busy's /portal/c/1 is BUSY-CLIENT-1", st == 200 && page.Contains("BUSY-CLIENT-1") && !page.Contains("ACME-"), $"{st}");
    (st, page) = await Get(A, "/portal/a/1");
    Check("Acme's /portal/a/1 is ACME-ARTICLE-ONE", st == 200 && page.Contains("ACME-ARTICLE-ONE"), $"{st}");
    (st, page) = await Get(B, "/portal/a/1");
    Check("Busy's /portal/a/1 is BUSY-ARTICLE-1", st == 200 && page.Contains("BUSY-ARTICLE-1") && !page.Contains("ACME-"), $"{st}");
    (st, page) = await Get(A, "/portal/r/1");
    Check("Acme's /portal/r/1 is ACME-TASK", st == 200 && page.Contains("ACME-TASK"), $"{st}");
    (st, page) = await Get(B, "/portal/r/1");
    Check("Busy's /portal/r/1 is BUSY-TASK", st == 200 && page.Contains("BUSY-TASK") && !page.Contains("ACME-"), $"{st}");
    (st, page) = await Get(A, "/portal/a/1/edit");
    Check("Acme's /portal/a/1/edit edits ACME-ARTICLE-ONE", st == 200 && page.Contains("ACME-ARTICLE-ONE") && page.Contains("action=\"/portal/a/1/edit\""), $"{st}");

    Head("the old id addresses: sent on for the company's own, query kept");
    async Task Moved(string what, string path, string to)
    {
        var r = await Araw.GetAsync(path);
        var loc = r.Headers.Location?.OriginalString ?? "";
        Check($"{what}: {path} → {to}", (int)r.StatusCode == 302 && loc == to, $"{(int)r.StatusCode} {loc}");
    }
    await Moved("a ticket", $"/portal/tickets/{w1.Id}?tab=photos", "/portal/t/1?tab=photos");
    await Moved("a client", $"/portal/clients/{c1.Id}", "/portal/c/1");
    await Moved("an article", $"/portal/kb/{a1.Id}?version=1", "/portal/a/1?version=1");
    await Moved("its editor", $"/portal/kb/edit/{a1.Id}", "/portal/a/1/edit");
    await Moved("a recurring task", $"/portal/recurring/{task.Id}", "/portal/r/1");
    await Moved("a new recurring task", "/portal/recurring/new", "/portal/r/new");
    await Moved("one client's articles", $"/portal/kb?client={c1.Id}", "/portal/kb?c=1");
    await Moved("one client's tickets", $"/portal/tickets?client={c1.Id}&filter=closed", "/portal/tickets?c=1&filter=closed");
    await Moved("a write-up", $"/portal/kb/edit?ticket={w1.Id}", "/portal/kb/edit?t=1");
    await Moved("a photo", $"/portal/photo/{shot.Id}", "/portal/p/" + shot.Token);
    await Moved("a comment's file", $"/portal/attachment/{att.Id}", "/portal/f/" + att.Token);
    await Moved("an article's picture", $"/portal/kb/image/{img.Id}", "/portal/i/" + img.Token);
    await Moved("a recording", $"/portal/recordings/{rec.Id}", "/portal/v/" + rec.Token);

    Head("another company's id, or number, is the same 404 as one that does not exist");
    async Task Same404(string what, string theirs, string nobodys)
    {
        var r1 = await Braw.GetAsync(theirs); var r2 = await Braw.GetAsync(nobodys);
        // The page carries its own address (the light/dark switch comes back to it); that, and the form token, aside.
        var s1 = Normal((await r1.Content.ReadAsStringAsync()).Replace(theirs, "HERE"));
        var s2 = Normal((await r2.Content.ReadAsStringAsync()).Replace(nobodys, "HERE"));
        Check($"{what}: Busy opening Acme's is a 404, word for word the missing one's", (int)r1.StatusCode == 404 && (int)r2.StatusCode == 404 && s1 == s2,
            $"{(int)r1.StatusCode} / {(int)r2.StatusCode}, {(s1 == s2 ? "same page" : "DIFFERENT pages")}");
    }
    await Same404("a ticket's old id", $"/portal/tickets/{w1.Id}", "/portal/tickets/987654");
    await Same404("a ticket's number Busy does not have", "/portal/t/40", "/portal/t/987654");
    await Same404("a client's old id", $"/portal/clients/{c1.Id}", "/portal/clients/987654");
    await Same404("an article's old id", $"/portal/kb/{a1.Id}", "/portal/kb/987654");
    await Same404("a recurring task's old id", $"/portal/recurring/{task.Id}", "/portal/recurring/987654");
    await Same404("a photo's old id", $"/portal/photo/{shot.Id}", "/portal/photo/987654");
    await Same404("a photo's token", "/portal/p/" + shot.Token, "/portal/p/" + new string('a', 24));
    await Same404("a comment's file's token", "/portal/f/" + att.Token, "/portal/f/" + new string('a', 24));
    await Same404("an article picture's token", "/portal/i/" + img.Token, "/portal/i/" + new string('a', 24));
    await Same404("a recording's token", "/portal/v/" + rec.Token, "/portal/v/" + new string('a', 24));
    (st, page) = await Get(B, $"/portal/tickets?client={c1.Id}");
    Check("Busy's ?client={Acme's id} goes to the plain list, as a client id nobody has does", st == 200 && !page.Contains("ACME-"), $"{st}");

    Head("files by token, for the company's own");
    foreach (var (what, path) in new[] { ("a photo", "/portal/p/" + shot.Token), ("a comment's file", "/portal/f/" + att.Token),
                 ("an article's picture", "/portal/i/" + img.Token), ("a recording", "/portal/v/" + rec.Token) })
    {
        var r = await A.GetAsync(path);
        Check($"{what} opens: {path[..10]}…", r.IsSuccessStatusCode && (await r.Content.ReadAsByteArrayAsync()).Length > 0, $"{(int)r.StatusCode}");
    }

    Head("no page shows a database id — in an address, a link, a hidden field or a script");
    var aIds = new[] { w1.Id, g2.Id, old3.Id, shot4.Id, c1.Id, c2!.Id, a1.Id, a2!.Id, task.Id }.Select(i => i.ToString()).ToHashSet();
    foreach (var (what, path, must) in new[]
    {
        ("the dashboard", "/portal", "Dashboard"),
        ("the ticket list", "/portal/tickets?filter=all", "ACME-GENERAL-TWO"),
        ("one client's tickets", "/portal/tickets?c=1&filter=all", "ACME-WORKSHOP-ONE"),
        ("a workshop ticket (the story)", "/portal/t/1", "ACME-WORKSHOP-ONE"),
        ("a ticket with a comment's file", "/portal/t/2", "note.png"),
        ("a ticket in the classic page", "/portal/t/3", "ACME-CLASSIC-THREE"),
        ("a ticket with their screen", "/portal/t/4", "ACME-REMOTE-FOUR"),
        ("the client list", "/portal/clients", "ACME-CLIENT-TWO"),
        ("a client page", "/portal/c/1", "ACME-CLIENT-ONE"),
        ("the knowledge base", "/portal/kb", "ACME-ARTICLE-TWO"),
        ("one client's articles", "/portal/kb?c=1", "ACME-ARTICLE-ONE"),
        ("an article with a picture", "/portal/a/1", "ACME-ARTICLE-ONE"),
        ("an article's editor", "/portal/a/1/edit", "ACME-ARTICLE-ONE"),
        ("a ticket written up", "/portal/kb/edit?t=1", "Write up ticket #1"),
        ("the recurring tasks", "/portal/recurring", "ACME-TASK"),
        ("a recurring task", "/portal/r/1", "ACME-TASK"),
        ("the devices", "/portal/devices", "Devices"),
        ("the workload", "/portal/workload", "Who has what"),
    })
    {
        (st, page) = await Get(A, path);
        var leaks = Leaks(page, aIds);
        Check($"{what} ({path}) — {(st == 200 && page.Contains(must) ? "shown" : "NOT SHOWN")}, no id", st == 200 && page.Contains(must) && leaks.Count == 0,
            leaks.Count == 0 ? $"{st}" : string.Join(" | ", leaks.Take(4)));
    }
    (st, page) = await Get(A, "/portal/t/1");
    Check("...and the ticket page names its photos by token", page.Contains("/portal/p/" + wPhotos[0].Token) && page.Contains("/portal/p/" + wPhotos[2].Token));
    Check("...its knowledge base article by number", page.Contains("/portal/a/1\""));
    (st, page) = await Get(A, "/portal/t/2");
    Check("a comment's file by token", page.Contains("/portal/f/" + att.Token));
    (st, page) = await Get(A, "/portal/t/4");
    Check("their screen by token, a recording by token", page.Contains("/portal/p/" + shot.Token) && page.Contains("/portal/v/" + rec.Token));
    (st, page) = await Get(A, "/portal/a/1");
    Check("an article's picture by token", page.Contains("/portal/i/" + img.Token));
    (st, page) = await Get(A, "/portal/tickets?filter=all");
    Check("the ticket list's Tickets tab still lights up on /portal/t/…", (await Get(A, "/portal/t/1")).body.Contains("sections = { t: '/portal/tickets'"));

    Head("forms post by number; whatever was posted as an id is never read");
    int before = (await NotesOf(options, w1.Id)).Count;
    var res = await PostForm(A, "/portal/t/1?handler=Comment", new() { ["id"] = g2.Id.ToString(), ["note"] = "COMMENT-BY-NUMBER" });
    Check("a comment posted at /portal/t/1 with a forged id of #2 lands on #1", (await NotesOf(options, w1.Id)).Any(n => n.Body == "COMMENT-BY-NUMBER")
        && !(await NotesOf(options, g2.Id)).Any(n => n.Body == "COMMENT-BY-NUMBER"), $"{(int)res.StatusCode}");
    Check("...and comes back to /portal/t/1", res.RequestMessage?.RequestUri?.AbsolutePath == "/portal/t/1", res.RequestMessage?.RequestUri?.AbsolutePath ?? "");
    res = await PostForm(B, "/portal/t/1?handler=Comment", new() { ["id"] = w1.Id.ToString(), ["note"] = "FORGED-BY-BUSY" });
    Check("Busy posting at /portal/t/1 with Acme's id lands on BUSY's #1 — Acme's #1 untouched",
        !(await NotesOf(options, w1.Id)).Any(n => n.Body == "FORGED-BY-BUSY") && (await NotesOf(options, busyTickets[0].Id)).Any(n => n.Body == "FORGED-BY-BUSY"));
    res = await PostForm(B, "/portal/t/40?handler=Comment", new() { ["id"] = w1.Id.ToString(), ["note"] = "FORGED-NOWHERE" });
    Check("Busy posting to a number it does not have is a 404 and writes nothing anywhere", (int)res.StatusCode == 404
        && !(await NotesOf(options, w1.Id)).Any(n => n.Body == "FORGED-NOWHERE"), $"{(int)res.StatusCode}");
    res = await PostForm(A, $"/portal/tickets/{w1.Id}?handler=Comment", new() { ["note"] = "POSTED-TO-THE-OLD-ADDRESS" });
    Check("a page left open since before (posting to the old address) is sent on with its post (307) and lands",
        (await NotesOf(options, w1.Id)).Any(n => n.Body == "POSTED-TO-THE-OLD-ADDRESS"), $"{(int)res.StatusCode}");
    res = await PostForm(B, $"/portal/tickets/{w1.Id}?handler=Comment", new() { ["note"] = "BUSY-AT-THE-OLD-ADDRESS" });
    Check("...and Busy posting to Acme's old address is a 404, nothing written", (int)res.StatusCode == 404
        && !(await NotesOf(options, w1.Id)).Any(n => n.Body == "BUSY-AT-THE-OLD-ADDRESS"), $"{(int)res.StatusCode}");
    res = await PostForm(A, "/portal/c/2?handler=Save", new() { ["id"] = c1.Id.ToString(), ["name"] = "ACME-CLIENT-TWO-RENAMED" });
    await using (var db = new PlatformDb(options))
    {
        var names = await db.Clients.Where(c => c.Id == c1.Id || c.Id == c2.Id).OrderBy(c => c.Number).Select(c => c.Name).ToListAsync();
        Check("a client saved at /portal/c/2 with client #1's id posted changes #2", names.SequenceEqual(new[] { "ACME-CLIENT-ONE", "ACME-CLIENT-TWO-RENAMED" }), string.Join(", ", names));
    }
    res = await PostForm(B, "/portal/c/1?handler=Save", new() { ["id"] = c1.Id.ToString(), ["name"] = "HIJACKED" });
    await using (var db = new PlatformDb(options))
        Check("Busy saving at /portal/c/1 with Acme's id changes nothing of Acme's", !await db.Clients.AnyAsync(c => c.TenantId == acme && c.Name == "HIJACKED"));
    res = await PostForm(A, "/portal/kb/edit?t=1", new() { ["title"] = "WRITTEN-UP-BY-NUMBER", ["body"] = "Steps.", ["clientId"] = c1.Id.ToString(), ["version"] = "0", ["fromTicketNumber"] = "1" });
    await using (var db = new PlatformDb(options))
    {
        var wa = await db.KbArticles.FirstOrDefaultAsync(a => a.Title == "WRITTEN-UP-BY-NUMBER");
        Check("a write-up posted at /portal/kb/edit?t=1 is filed from ticket #1 and opens at its number", wa != null && wa.FromTicketId == w1.Id && wa.TenantId == acme
            && res.RequestMessage?.RequestUri?.AbsolutePath == "/portal/a/" + wa.Number, $"{wa?.FromTicketId} {res.RequestMessage?.RequestUri}");
    }

    Head("uploads name the ticket by number");
    var (us, uj) = await UploadPhoto(A, "ticketNumber", "1");
    Check("a photo sent with ticketNumber=1 goes on Acme's #1, and comes back with its token", us == 200 && uj.Contains("\"token\""), $"{us} {uj}");
    await using (var db = new PlatformDb(options))
        Check("...stored on that ticket", await db.TicketPhotos.CountAsync(p => p.TicketId == w1.Id) == 4);
    (us, uj) = await UploadPhoto(B, "ticketNumber", "40");
    Check("a number Busy does not have is a 404", us == 404, $"{us}");

    Head("the console's hand-off: an old-shape link and a new one both land on the number");
    var con = await Sign(port, "owner@acme.test", Pw);
    async Task<string> Lands(TechConsole c, string path)
    {
        c.Clear();
        c.Ws.Send(new PortalLinkRequestMsg { Path = path });
        var link = await c.Next<PortalLinkMsg>(_ => true, 15000);
        if (link == null) return "(no link)";
        using var fresh = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(baseUrl) };
        var r = await fresh.GetAsync(link.Url);
        return r.Headers.Location?.OriginalString ?? $"({(int)r.StatusCode})";
    }
    var landed = await Lands(con, $"/portal/tickets/{w1.Id}?tab=photos");
    Check("an OLD console's /portal/tickets/{id}?tab=photos lands on /portal/t/1?tab=photos — the id never reaches the browser", landed == "/portal/t/1?tab=photos", landed);
    landed = await Lands(con, "/portal/t/1?open=quote-requested");
    Check("a new console's /portal/t/1?open=quote-requested lands there", landed == "/portal/t/1?open=quote-requested", landed);
    landed = await Lands(con, $"/portal/kb?client={c1.Id}");
    Check("an old /portal/kb?client={id} lands on ?c=1", landed == "/portal/kb?c=1", landed);
    landed = await Lands(con, $"/portal/photo/{shot.Id}");
    Check("an old screenshot link lands on its token", landed == "/portal/p/" + shot.Token, landed);
    landed = await Lands(con, $"/portal/attachment/{att.Id}");
    Check("a comment's file (never on the list before) lands on its token", landed == "/portal/f/" + att.Token, landed);
    var busyCon = await Sign(port, "owner@busy.test", Pw);
    landed = await Lands(busyCon, $"/portal/tickets/{w1.Id}");
    Check("Busy's console asking for Acme's ticket id lands on its own queue", landed == PortalHandoffService.DefaultPath, landed);
    con.Clear();
    con.Ws.Send(new TicketOpenMsg { TicketId = shot4.Id });
    var detail = await con.Next<TicketDetailMsg>(m => m.Detail.Ticket.Id == shot4.Id, 15000);
    Check("the console is told the screenshot's token and the ticket's number, to send the new shapes",
        detail != null && detail.Detail.ScreenshotTokens.SequenceEqual(new[] { shot.Token }) && detail.Detail.Ticket.Number == 4,
        detail == null ? "no answer" : string.Join(",", detail.Detail.ScreenshotTokens));

    // --hold: leave the platform up to walk it in a browser (both companies, every page above).
    if (args.Contains("--hold"))
    {
        Console.WriteLine($"\nHOLDING: {baseUrl}/login as owner@acme.test or owner@busy.test / {Pw}. Stop this process to finish.");
        await Task.Delay(Timeout.Infinite);
    }
}
finally
{
    try { server?.Kill(entireProcessTree: true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
try { Directory.Delete(dataDir, true); } catch { }
return fail == 0 ? 0 : 1;

// ---------------------------------------------------------------- the backfill, on a database of its own
async Task BackfillAsync()
{
    var dir = Path.Combine(dataDir, "backfill");
    Directory.CreateDirectory(dir);
    DbContext ctx;
    string before;
    if (HarnessDb.Postgres)
    {
        ctx = new PgPlatformDb(new DbContextOptionsBuilder<PgPlatformDb>().UseNpgsql(HarnessDb.For(dir)).Options);
        before = "20261023060704_InstalledForPerson";
    }
    else
    {
        ctx = new PlatformDb(new DbContextOptionsBuilder<PlatformDb>().UseSqlite($"Data Source={Path.Combine(dir, "platform.db")};Pooling=False").Options);
        before = "20261023060628_InstalledForPerson";
    }
    await using (ctx)
    {
        await ctx.GetService<IMigrator>().MigrateAsync(before);
        var conn = ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        bool pg = HarnessDb.Postgres;
        // Interleaved, as a live server's rows are: company 7, then 9, then 7 …
        var order = new[] { 7, 9, 7, 9, 7 };
        var ticket = await Insert(conn, pg, "Tickets", new() { ["TenantId"] = 7, ["Number"] = 1, ["Subject"] = "old" });
        foreach (var t in order) await Insert(conn, pg, "Clients", new() { ["TenantId"] = t, ["Name"] = "old client " + t });
        foreach (var t in order) await Insert(conn, pg, "KbArticles", new() { ["TenantId"] = t, ["Title"] = "old article " + t });
        foreach (var t in order) await Insert(conn, pg, "RecurringTasks", new() { ["TenantId"] = t, ["Title"] = "old task " + t });
        foreach (var t in order) await Insert(conn, pg, "TicketPhotos", new() { ["TenantId"] = t, ["FileName"] = "x.jpg", ["StageId"] = "s" });
        foreach (var t in order) await Insert(conn, pg, "TicketAttachments", new() { ["TenantId"] = t, ["TicketId"] = (int)ticket, ["FileName"] = "x.bin" });
        foreach (var t in order) await Insert(conn, pg, "KbImages", new() { ["TenantId"] = t, ["FileName"] = "x.png" });
        foreach (var t in order) await Insert(conn, pg, "SessionRecordings", new() { ["TenantId"] = t, ["TicketId"] = (int)ticket, ["FileName"] = "x.mp4" });
        await conn.CloseAsync();
        await ctx.Database.MigrateAsync();

        await conn.OpenAsync();
        foreach (var table in new[] { "Clients", "KbArticles", "RecurringTasks" })
        {
            var got = await Column(conn, $"SELECT \"TenantId\", \"Number\" FROM \"{table}\" ORDER BY \"Id\"");
            Check($"{table}: 7→1, 9→1, 7→2, 9→2, 7→3, in id order", string.Join(" ", got) == "7:1 9:1 7:2 9:2 7:3", string.Join(" ", got));
        }
        foreach (var table in new[] { "TicketPhotos", "TicketAttachments", "KbImages", "SessionRecordings" })
        {
            var got = await Column(conn, $"SELECT \"Token\" FROM \"{table}\" ORDER BY \"Id\"");
            Check($"{table}: every row a token of the right shape, all different", got.Count == 5 && got.All(FileToken.Looks) && got.Distinct().Count() == 5, string.Join(" ", got.Take(2)));
        }
        await conn.CloseAsync();
    }
}

static async Task<long> Insert(DbConnection conn, bool pg, string table, Dictionary<string, object?> given)
{
    var cols = new List<(string name, string type, bool required)>();
    using (var cmd = conn.CreateCommand())
    {
        if (pg)
        {
            cmd.CommandText = "SELECT column_name, data_type, is_nullable, column_default, is_identity FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t";
            var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = table; cmd.Parameters.Add(p);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                cols.Add((r.GetString(0), r.GetString(1), r.GetString(2) == "NO" && r.IsDBNull(3) && r.GetString(4) != "YES"));
        }
        else
        {
            cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                cols.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1 && r.IsDBNull(4) && r.GetInt32(5) == 0));
        }
    }
    var values = new Dictionary<string, object?>(given);
    foreach (var c in cols.Where(c => c.required && !values.ContainsKey(c.name)))
        values[c.name] = pg ? c.type switch
        {
            "integer" or "smallint" => 0, "bigint" => 0L, "boolean" => false, "numeric" => 0m, "double precision" => 0.0,
            "timestamp with time zone" => new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "timestamp without time zone" => new DateTime(2026, 9, 1),
            "date" => new DateOnly(2026, 9, 1), "bytea" => Array.Empty<byte>(), "interval" => TimeSpan.Zero, _ => ""
        } : c.type.StartsWith("INT") ? 0 : c.type == "REAL" ? 0.0 : c.type == "BLOB" ? Array.Empty<byte>()
          : c.name.EndsWith("Utc") || c.name.EndsWith("On") || c.name.EndsWith("Due") ? "2026-09-01 00:00:00" : "";
    using var ins = conn.CreateCommand();
    ins.CommandText = $"INSERT INTO \"{table}\" ({string.Join(",", values.Keys.Select(k => $"\"{k}\""))}) VALUES ({string.Join(",", values.Keys.Select(k => "@" + k))}) RETURNING \"Id\"";
    foreach (var kv in values) { var p = ins.CreateParameter(); p.ParameterName = "@" + kv.Key; p.Value = kv.Value ?? DBNull.Value; ins.Parameters.Add(p); }
    return Convert.ToInt64(await ins.ExecuteScalarAsync());
}

static async Task<List<string>> Column(DbConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    using var r = await cmd.ExecuteReaderAsync();
    var list = new List<string>();
    while (await r.ReadAsync()) list.Add(r.FieldCount == 1 ? Convert.ToString(r.GetValue(0))! : $"{r.GetValue(0)}:{r.GetValue(1)}");
    return list;
}

// ---------------------------------------------------------------- what a page must never show

/// <summary>Every place a page's source names something by database id: an old-shape portal address, an id query, a
/// hidden id field holding one of this company's ticket/client/article ids, an upload naming a ticket by id.</summary>
static List<string> Leaks(string html, HashSet<string> ids)
{
    var found = new List<string>();
    foreach (Match m in Regex.Matches(html, @"/portal/(tickets|clients|kb|kb/edit|kb/image|recurring|photo|attachment|recordings)/\d+")) found.Add(m.Value);
    foreach (Match m in Regex.Matches(html, @"/portal/[a-z/]*\?([^""'\s<>]*&)?(client|ticket)=\d+")) found.Add(m.Value);
    foreach (Match m in Regex.Matches(html, @"name=""id"" value=""(\d+)""")) if (ids.Contains(m.Groups[1].Value)) found.Add(m.Value);
    foreach (Match m in Regex.Matches(html, @"'ticketId'|""ticketId""")) found.Add(m.Value);
    return found;
}

/// <summary>A page with what differs between two requests taken out — the form token, the request's own nonces.</summary>
static string Normal(string html) => Regex.Replace(Regex.Replace(html, @"CfDJ8[A-Za-z0-9_\-]+", "TOKEN"), @"nonce=""[^""]*""", "nonce");

static async Task<List<TicketNote>> NotesOf(DbContextOptions<PlatformDb> o, int ticketId)
{ await using var db = new PlatformDb(o); return await db.TicketNotes.Where(n => n.TicketId == ticketId).ToListAsync(); }

static async Task<(int status, string body)> Get(HttpClient c, string path)
{
    var r = await c.GetAsync(path);
    return ((int)r.StatusCode, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
}

static async Task<(int status, string json)> UploadPhoto(HttpClient c, string field, string value)
{
    using var form = new MultipartFormDataContent { { new StringContent(value), field }, { new StringContent("before"), "kind" } };
    var part = new ByteArrayContent(TestImages.Jpeg());
    part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
    form.Add(part, "files", "arrived.jpg");
    var res = await c.PostAsync("/api/portal/photos", form);
    return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
}
static async Task<HttpResponseMessage> PostForm(HttpClient c, string path, Dictionary<string, string> fields)
{
    // The token from the page the form is on (§80), fetched WITHOUT its handler, whatever its status (§158).
    var page = await (await c.GetAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""))).Content.ReadAsStringAsync();
    fields["__RequestVerificationToken"] = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
    return await c.PostAsync(path, new FormUrlEncodedContent(fields));
}
/// <summary>A signed-in portal browser: one client that follows redirects, one on the same cookies that does not.</summary>
static async Task<(HttpClient follows, HttpClient raw)> Portal(int port, string email)
{
    var jar = new CookieContainer();
    var c = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    var raw = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "address-check-2026" });
    return (c, raw);
}
static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal);
    if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal);
    return j < 0 ? null : s[i..j];
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
    public void Clear() { lock (_got) _got.Clear(); }
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
