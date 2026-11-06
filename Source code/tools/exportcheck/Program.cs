// =====================================================================================
// exportcheck — a company takes its data with it (roadmap 4.9, 2026-10-22, HANDOVER §154).
//
// Clint: "the ability for companies to export their clients, tickets and knowledgebase." His answers: the owner and
// supervisors; a company that cannot sign in is exported BY US and its owners emailed a link; a zip of Excel files and
// the files themselves, clients.xlsx in Import clients' own columns; recordings on a tick, off by default.
//
// A real platform of its own (and a mail server of its own), two companies seeded with every kind of record and a file
// on disk for each, then driven over HTTP as a person would:
//
//   WHO          a technician and an office administrator are refused the page and the start; an owner-only login and a
//                supervisor are let in; another company's owner cannot download this company's export by its number.
//   THE ZIP      made in the background, the supervisor emailed; every file in it is the very bytes on the server; a file
//                gone from the disk is said, not skipped silently; device passwords appear NOWHERE; the other company
//                appears nowhere; a cell too long for Excel is cut and says so, and story.txt has it whole.
//   ROUND TRIP   clients.xlsx's first sheet read by the real Import clients into an EMPTY platform database (made by
//                HarnessDb.MigrateAsync) gives the same clients, contact people, phones, emails, addresses and notes;
//                read into the company it came from, it changes nothing. The tickets, story, quote lines, files and
//                knowledge base read back equal to the rows they came from; every article's picture resolves inside
//                the zip, and what was typed is encoded.
//   ONE AT A TIME asking while one is being made makes no second one; recordings come only when ticked; a newer export
//                replaces the one before.
//   BY US        a suspended company: Owner → All companies → Export their data; the owners get a link; opening the link
//                downloads nothing (mail filters open links); the button does; a wrong token and an expired export are
//                refused; the admin log has it by name.
//   RESTART      an export cut short by a restart is made again from the start.
//
//   dotnet run -c Release --project tools/exportcheck/exportcheck.csproj        (RSP_TEST_DB=postgres for PostgreSQL)
// =====================================================================================

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
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

Console.WriteLine($"exportcheck — a company's clients, tickets and knowledge base in one zip ({HarnessDb.Name})");
var dir = Path.Combine(Path.GetTempPath(), "exportcheck-" + Guid.NewGuid().ToString("N")[..8]);
var data = Path.Combine(dir, "data"); Directory.CreateDirectory(data);
var d0 = new DirectoryInfo(AppContext.BaseDirectory);
while (d0 != null && !File.Exists(Path.Combine(d0.FullName, "RemoteSupport.sln"))) d0 = d0.Parent;
var proj = Path.Combine(d0!.FullName, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
var baseUrl = $"http://127.0.0.1:{port}";
const string Pw = "ExportCheck!2026";
const string AdminKey = "exportcheck-admin-key-0123456789abcdef0123456789abcdef";
const string Secret1 = "ENC-DEVICE-PASSWORD-7f3a9", Secret2 = "ENC-SECOND-MACHINE-PW-41c2";
using var mail = new FakeSmtp(); mail.Start();

ProcessStartInfo Psi(params string[] args)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "--no-build", "-c", "Debug", "--project", proj, "--" }.Concat(args)) psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = data;
    HarnessDb.Use(psi.Environment, data);
    psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
    psi.Environment["RSP_Platform__PublicUrl"] = baseUrl;
    psi.Environment["RSP_Platform__ExportSweepSeconds"] = "2";
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
    psi.Environment["RSP_Urls"] = baseUrl; psi.Environment["ASPNETCORE_URLS"] = baseUrl;
    return psi;
}
async Task<string> Cli(params string[] args) { var p = Process.Start(Psi(args))!; var o = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); return o; }
async Task<Process> StartServer()
{
    var s = Process.Start(Psi("run"))!;
    s.OutputDataReceived += (_, _) => { }; s.ErrorDataReceived += (_, _) => { };
    s.BeginOutputReadLine(); s.BeginErrorReadLine();
    var until = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < until) { try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); break; } catch { await Task.Delay(400); } }
    return s;
}

var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(data, "platform.db")};Pooling=False").Options;
Process? server = null;
try
{
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build."); return 1; }
    var mailSet = await Cli("platform-mail", "set", "--host", "127.0.0.1", "--port", mail.Port.ToString(), "--nossl",
        "--from", "noreply@export.test", "--from-name", "The Remote Support Program");
    if (!mailSet.Contains("Saved")) { Console.WriteLine("Could not set the platform mailbox:\n" + mailSet); return 1; }
    server = await StartServer();

    HttpClient Browser() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
    async Task<(HttpStatusCode code, string html, string? to)> Get(HttpClient h, string path)
    {
        var r = await h.GetAsync(path);
        return (r.StatusCode, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()), r.Headers.Location?.ToString());
    }
    string TokenIn(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    async Task<HttpResponseMessage> Post(HttpClient h, string path, string fromHtml, params (string k, string v)[] f)
    {
        var all = f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", TokenIn(fromHtml)));
        return await h.PostAsync(path, new FormUrlEncodedContent(all));
    }
    async Task<HttpClient> SignIn(string email)
    {
        var b = Browser();
        var (_, page, _) = await Get(b, "/login");
        await Post(b, "/login", page, ("Email", email), ("Password", Pw));
        return b;
    }

    // ================================================================== the two companies, every kind of record
    Head("seeding two companies with every kind of record and a file for each");
    byte[] Bytes(int n) { var b = new byte[n]; RandomNumberGenerator.Fill(b); return b; }
    var onDisk = new Dictionary<string, byte[]>();   // the server's own file → its bytes, to compare with the zip
    void Put(string sub, int tenant, string name, byte[] bytes)
    {
        var p = Path.Combine(data, sub, tenant.ToString(), name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
        onDisk[$"{sub}/{tenant}/{name}"] = bytes;
    }
    var longWords = string.Concat(Enumerable.Range(1, 1500).Select(i => $"Event log line {i}: the service stopped and started again.\n")) + "THE VERY LAST LINE OF THE LOG";
    int coast, other, acme, gamma, t1, t2, img;
    long noteComment, noteStep, line1, att1;
    await using (var db = new PlatformDb(options))
    {
        Tenant T(string slug, string name) => new() { Slug = slug, CompanyName = name, TenantKey = "k-" + slug, Status = TenantStatus.Active, Region = "ZA" };
        var a = T("coastline", "Coastline IT"); var b = T("othercompany", "Other Company Ltd");
        db.Tenants.AddRange(a, b); await db.SaveChangesAsync();
        coast = a.Id; other = b.Id;
        var (salt, hash) = Passwords.Create(Pw);
        Technician P(int t, string email, string name, TenantRole r) => new() { TenantId = t, Email = email, DisplayName = name, Role = r, Salt = salt, Hash = hash };
        db.Technicians.AddRange(
            P(coast, "olive@coast.test", "Olive Owner", TenantRole.Owner),
            P(coast, "sam@coast.test", "Sam Supervisor", TenantRole.Admin),
            P(coast, "tom@coast.test", "Tom Technician", TenantRole.Technician),
            P(coast, "bea@coast.test", "Bea Office", TenantRole.Billing),
            P(other, "otto@other.test", "Otto Other", TenantRole.Owner | TenantRole.Admin));

        // Clients: the client's own person on file; a switchboard number that is nobody's; none at all; archived; awkward names.
        var cAcme = new Client { TenantId = coast, Name = "Acme Trading", ContactName = "Jane Smith", Phone = "0821234567", Email = "jane@acme.test",
            Address = "1 Main Road, Durban", Notes = "Gate code 1234\nAsk for Jane at reception", HealthCheck = true };
        var cBeta = new Client { TenantId = coast, Name = "Beta Bakery", Phone = "0310000000", Email = "orders@beta.test", Address = "4 Flour Lane, Pinetown" };
        var cGamma = new Client { TenantId = coast, Name = "Gamma Garage", ContactName = "Pete Mokoena", Phone = "0315550000", Email = "",
            Address = "9 Spanner St", Notes = "Switchboard first" };
        var cCafe = new Client { TenantId = coast, Name = "Café Ünïcode & Sons <b>", Address = "Rue \"Quoted\" 5" };
        var cOld = new Client { TenantId = coast, Name = "Old Archived Client", Archived = true, Phone = "0110000000" };
        var cOther = new Client { TenantId = other, Name = "OTHERCOMPANY-ONLY Client", Phone = "0119999999" };
        db.Clients.AddRange(cAcme, cBeta, cGamma, cCafe, cOld, cOther); await db.SaveChangesAsync();
        acme = cAcme.Id; gamma = cGamma.Id;
        db.Contacts.AddRange(
            new Contact { TenantId = coast, ClientId = cAcme.Id, Name = "Jane Smith", Phone = "0821234567", Email = "jane@acme.test", Role = "Owner" },
            new Contact { TenantId = coast, ClientId = cAcme.Id, Name = "John Doe", Phone = "0831112222", Email = "john@acme.test", Role = "Accounts", Notes = "Mornings only" },
            new Contact { TenantId = coast, ClientId = cAcme.Id, Name = "Gone Gary", Phone = "0840000000", Archived = true },
            new Contact { TenantId = coast, ClientId = cGamma.Id, Name = "Pete Mokoena", Phone = "0825550001", Email = "pete@gamma.test" },
            new Contact { TenantId = coast, ClientId = cCafe.Id, Name = "Zoë O'Neill", Phone = "0729876543", Email = "zoe@cafe.test" },
            new Contact { TenantId = coast, ClientId = cOld.Id, Name = "Archived Client Person", Phone = "0110000001" },
            new Contact { TenantId = other, ClientId = cOther.Id, Name = "OTHERCOMPANY-ONLY Person" });

        // Ticket #1: a workshop job with everything on it. #2: a general ticket, part of #1.
        var tk1 = new Ticket { TenantId = coast, Number = 1, Subject = "Laptop will not boot", Description = "Black screen after the Windows logo.\nCustomer needs it by Friday.",
            Category = TicketCategory.Workshop, ClientId = cAcme.Id, ContactName = "Jane Smith", AssignedTo = "Tom Technician, Sam Supervisor", WorkedBy = "Tom Technician",
            OpenedBy = "Bea Office", AssetMake = "HP", AssetModel = "ProBook 450", AssetSerial = "SN-HP-0001", Accessories = "Charger",
            DevicePasswordProtected = true, DevicePasswordEnc = Secret1, QuoteAmount = 1250.50m, QuoteReference = "Q-77", QuoteSupplierNames = "Rectron",
            InvoiceAmount = 1450m, InvoiceNumber = "INV-1001", TimeSpentMinutes = 95, Repairable = true, Outcome = "solved" };
        var tk2 = new Ticket { TenantId = coast, Number = 2, Subject = "Printer offline", Description = "Reception printer", Category = TicketCategory.General,
            Stage = Workflow.GeneralFirst, ClientId = cBeta.Id, OpenedBy = "Sam Supervisor" };
        var tkOther = new Ticket { TenantId = other, Number = 1, Subject = "OTHERCOMPANY-ONLY ticket", Description = "never in Coastline's export" };
        db.Tickets.AddRange(tk1, tk2, tkOther); await db.SaveChangesAsync();
        t1 = tk1.Id; t2 = tk2.Id;
        tk2.ParentTicketId = tk1.Id;
        var nComment = new TicketNote { TenantId = coast, TicketId = t1, Author = "Tom Technician", Body = "Opened it up — the SSD is loose.\nPhotos attached." };
        var nStep = new TicketNote { TenantId = coast, TicketId = t1, Author = "Tom Technician", Body = "Reseated the SSD; boots fine.", FormTitle = "Inspection findings",
            Stage = Workflow.GeneralFirst, MovedTo = Workflow.GeneralFirst, UndoneUtc = DateTime.UtcNow.AddMinutes(-5), UndoneBy = "Sam Supervisor" };
        db.TicketNotes.AddRange(nComment, nStep,
            new TicketNote { TenantId = coast, TicketId = t1, Author = "", Body = "Session ended", IsSystem = true },
            new TicketNote { TenantId = coast, TicketId = t1, Author = "Jane Smith", Body = "Thanks, collecting at 4.", FromCustomer = true },
            new TicketNote { TenantId = coast, TicketId = t1, Author = "Tom Technician", Body = longWords, IsWorkDone = true },
            new TicketNote { TenantId = coast, TicketId = t2, Author = "Sam Supervisor", Body = "Asked them to restart it =SUM(A1) +27 0821234567" });
        db.TicketMachines.Add(new TicketMachine { TenantId = coast, TicketId = t1, Purpose = "the new laptop — data goes onto this one", Make = "Dell",
            Model = "Latitude 5440", Serial = "SN-DELL-NEW", PasswordProtected = true, PasswordEnc = Secret2, AddedBy = "Bea Office" });
        var line = new QuoteLine { TenantId = coast, TicketId = t1, ItemName = "SSD 512GB NVMe", PartNumber = "SSD-512", SerialNumber = "S512-998", Quantity = 2, AddedBy = "Bea Office" };
        db.QuoteLines.Add(line);
        await db.SaveChangesAsync();
        noteComment = nComment.Id; noteStep = nStep.Id; line1 = line.Id;

        for (int i = 1; i <= 3; i++) { Put("ticketphotos", coast, $"before{i}.jpg", Bytes(3000 + i)); db.TicketPhotos.Add(new TicketPhoto { TenantId = coast, TicketId = t1, Kind = PhotoKind.Before, FileName = $"before{i}.jpg", OriginalName = $"IMG_{i}.jpg", Bytes = 3000 + i, UploadedBy = "Bea Office" }); }
        for (int i = 1; i <= 2; i++) { Put("ticketphotos", coast, $"part{i}.jpg", Bytes(2000 + i)); db.TicketPhotos.Add(new TicketPhoto { TenantId = coast, TicketId = t1, Kind = PhotoKind.Part, FileName = $"part{i}.jpg", QuoteLineId = line1, Bytes = 2000 + i, UploadedBy = "Bea Office" }); }
        Put("ticketphotos", coast, "staged.jpg", Bytes(100));
        db.TicketPhotos.Add(new TicketPhoto { TenantId = coast, TicketId = null, Kind = PhotoKind.Before, FileName = "staged.jpg", StageId = "x", StagedBy = "Tom Technician" });
        Put("ticketfiles", coast, "att1.bin", Bytes(5000));
        var att = new TicketAttachment { TenantId = coast, TicketId = t1, NoteId = noteComment, FileName = "att1.bin", OriginalName = "Supplier quote.pdf", ContentType = "application/pdf", Bytes = 5000, UploadedBy = "Tom Technician" };
        db.TicketAttachments.AddRange(att,
            new TicketAttachment { TenantId = coast, TicketId = t1, NoteId = noteComment, FileName = "gone.bin", OriginalName = "lost.txt", ContentType = "text/plain", Bytes = 10, UploadedBy = "Tom Technician" },
            new TicketAttachment { TenantId = coast, TicketId = t1, NoteId = null, FileName = "loose.bin", OriginalName = "never posted.txt", Bytes = 1 });
        Put("recordings", coast, "rec1.mp4", Bytes(7000));
        db.SessionRecordings.Add(new SessionRecording { TenantId = coast, TicketId = t1, SessionId = "s1", MachineName = "DESKTOP-ACME1", FileName = "rec1.mp4", Bytes = 7000, Seconds = 75, RecordedBy = "Tom Technician" });

        // The knowledge base: nested folders, a client's section, a picture, words that must be encoded, a deleted article.
        Put("knowledge", coast, "img1.png", Bytes(4000));
        var image = new KbImage { TenantId = coast, FileName = "img1.png", ContentType = "image/png", Bytes = 4000, UploadedBy = "Tom Technician" };
        db.KbImages.Add(image);
        var fPrinters = new KbFolder { TenantId = coast, Name = "Printers" };
        var fNet = new KbFolder { TenantId = coast, ClientId = cAcme.Id, Name = "Network: VPN/Wi-Fi" };
        db.KbFolders.AddRange(fPrinters, fNet); await db.SaveChangesAsync();
        var fHp = new KbFolder { TenantId = coast, Name = "HP", ParentId = fPrinters.Id };
        db.KbFolders.Add(fHp); await db.SaveChangesAsync();
        att1 = att.Id; img = (int)image.Id;
        db.KbArticles.AddRange(
            new KbArticle { TenantId = coast, FolderId = fHp.Id, Title = "Install the HP driver", Body = $"# Steps\n1. Download it\n{KbText.ImageTag(image.Id)}\n**Done** <script>alert('x')</script>",
                FromTicketId = t1, FromTicketNumber = 1, CreatedBy = "Tom Technician", UpdatedBy = "Sam Supervisor", Version = 3 },
            new KbArticle { TenantId = coast, Title = "Wi-Fi password policy", Body = "Change it every 90 days.", CreatedBy = "Sam Supervisor", UpdatedBy = "Sam Supervisor" },
            new KbArticle { TenantId = coast, ClientId = cAcme.Id, FolderId = fNet.Id, Title = "Acme VPN: set-up?", Body = "Use the FortiClient.", CreatedBy = "Tom Technician", UpdatedBy = "Tom Technician" },
            new KbArticle { TenantId = coast, Title = "DELETED-ARTICLE", Body = "gone", DeletedUtc = DateTime.UtcNow },
            new KbArticle { TenantId = other, Title = "OTHERCOMPANY-ONLY article", Body = "x" });
        await db.SaveChangesAsync();
        await HarnessDb.ResetSequencesAsync(db);
    }
    Check("two companies seeded", true, $"Coastline IT {coast}, Other Company {other}");

    // ================================================================== who
    Head("who may: the owner and supervisors only");
    var tom = await SignIn("tom@coast.test");
    var (tc, tp, _) = await Get(tom, "/portal/export");
    Check("a technician is refused the page", tc != HttpStatusCode.OK || !tp.Contains("<h1>Export your data</h1>"), tc.ToString());
    var (_, tomTickets, _) = await Get(tom, "/portal/tickets");
    var tomPost = await Post(tom, "/portal/export?handler=Start", tomTickets);
    var bea = await SignIn("bea@coast.test");
    var (bc, bp, _) = await Get(bea, "/portal/export");
    Check("an office administrator is refused the page", bc != HttpStatusCode.OK || !bp.Contains("<h1>Export your data</h1>"), bc.ToString());
    var (_, beaTickets, _) = await Get(bea, "/portal/tickets");
    await Post(bea, "/portal/export?handler=Start", beaTickets);
    await using (var db = new PlatformDb(options))
        Check("...and neither of them could start one", !await db.CompanyExports.AnyAsync(), tomPost.StatusCode.ToString());
    Check("the rule itself: owner and supervisor yes, technician and office administrator no",
        Permissions.CanExportCompany(TenantRole.Owner) && Permissions.CanExportCompany(TenantRole.Admin)
        && !Permissions.CanExportCompany(TenantRole.Technician) && !Permissions.CanExportCompany(TenantRole.Billing)
        && !Permissions.CanExportCompany(TenantRole.Technician | TenantRole.Billing));
    var olive = await SignIn("olive@coast.test");
    var (oc, op, _) = await Get(olive, "/portal/export");
    Check("an owner-only login is let in (it manages the company)", oc == HttpStatusCode.OK && op.Contains("<h1>Export your data</h1>"), oc.ToString());
    var sam = await SignIn("sam@coast.test");
    var (sc, sp, _) = await Get(sam, "/portal/export");
    Check("a supervisor is let in, with the tick box and the recordings' size", sc == HttpStatusCode.OK && sp.Contains("Include session recordings") && sp.Contains("1 recording"), sc.ToString());
    var (_, portalHome, _) = await Get(sam, "/portal/clients");
    Check("the portal's menu offers Export to a supervisor", portalHome.Contains("href=\"/portal/export\""));
    Check("...and not to a technician", !(await Get(tom, "/portal/tickets")).html.Contains("href=\"/portal/export\""));

    // ================================================================== one at a time
    Head("one at a time");
    await using (var db = new PlatformDb(options))
    {
        db.CompanyExports.Add(new CompanyExport { TenantId = coast, State = ExportState.Making, RequestedBy = "somebody", Progress = "Tickets: 1 of 2", FileName = "held.zip" });
        await db.SaveChangesAsync();
    }
    var again = await Post(sam, "/portal/export?handler=Start", sp);
    await using (var db = new PlatformDb(options))
    {
        Check("asking while one is being made makes no second one", await db.CompanyExports.CountAsync() == 1, again.StatusCode.ToString());
        Check("...and the page says it is being made, with where it has got to", (await Get(sam, "/portal/export")).html.Contains("Tickets: 1 of 2"));
        db.CompanyExports.RemoveRange(db.CompanyExports); await db.SaveChangesAsync();
    }

    // ================================================================== made, and downloaded
    Head("made in the background, then downloaded");
    var (_, sp2, _) = await Get(sam, "/portal/export");
    var started = await Post(sam, "/portal/export?handler=Start", sp2);
    Check("Make the export answers with a redirect (a refresh never asks twice)", started.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, started.StatusCode.ToString());
    async Task<CompanyExport?> WaitDone(int tenant, long after = 0)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            await using var db = new PlatformDb(options);
            var e = await db.CompanyExports.Where(x => x.TenantId == tenant && x.Id > after).OrderByDescending(x => x.Id).FirstOrDefaultAsync();
            if (e is { State: ExportState.Ready or ExportState.Failed }) return e;
            await Task.Delay(300);
        }
        return null;
    }
    var job = await WaitDone(coast);
    Check("it is made", job?.State == ExportState.Ready, $"{job?.State} {job?.Error} {job?.Summary}");
    if (job?.State != ExportState.Ready) throw new Exception("no export to read");
    Check("...with what is in it said, and the missing file owned up to",
        job.Summary.StartsWith("5 clients, 2 tickets, 3 articles,") && job.Summary.Contains("(1 missing on the server)"), job.Summary);
    Check("...kept for 7 days", job.ExpiresUtc is { } exp && Math.Abs((exp - DateTime.UtcNow).TotalDays - CompanyExportService.KeepDays) < 0.01);
    var ready = await mail.WaitFor("sam@coast.test", "your data export is ready");
    Check("the supervisor who asked is emailed when it is ready", ready != null && ready.Body.Contains($"{baseUrl}/portal/export"), ready?.Subject ?? "no mail");

    var (_, listed, _) = await Get(sam, "/portal/export");
    Check("the page lists it Ready, with a Download button", listed.Contains($"data-export=\"{job.Id}\" data-state=\"Ready\"") && listed.Contains($"handler=Download&id={job.Id}"));
    var dl = await sam.GetAsync($"/portal/export?handler=Download&id={job.Id}");
    var zipBytes = await dl.Content.ReadAsByteArrayAsync();
    Check("Download gives the zip", dl.Content.Headers.ContentType?.MediaType == "application/zip" && zipBytes.Length == job.Bytes
        && (dl.Content.Headers.ContentDisposition?.FileNameStar ?? dl.Content.Headers.ContentDisposition?.FileName ?? "").Contains("Coastline IT - export"),
        $"{dl.Content.Headers.ContentType} {zipBytes.Length} bytes");
    var otto = await SignIn("otto@other.test");
    var cross = await otto.GetAsync($"/portal/export?handler=Download&id={job.Id}");
    Check("another company's owner cannot download it by its number", cross.Content.Headers.ContentType?.MediaType != "application/zip", cross.StatusCode.ToString());
    var tomDl = await tom.GetAsync($"/portal/export?handler=Download&id={job.Id}");
    Check("...nor can the company's own technician", tomDl.Content.Headers.ContentType?.MediaType != "application/zip", tomDl.StatusCode.ToString());

    // ================================================================== what is in it
    Head("the zip: every file, nothing secret, nobody else's");
    using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
    byte[] Entry(string path) { var e = zip.GetEntry(path); if (e == null) return Array.Empty<byte>(); using var s = e.Open(); using var m = new MemoryStream(); s.CopyTo(m); return m.ToArray(); }
    string TextOf(string path) => Encoding.UTF8.GetString(Entry(path)).TrimStart('﻿').Replace("\r\n", "\n");
    var names = zip.Entries.Select(e => e.FullName).ToList();
    foreach (var want in new[] { "README.txt", "clients.xlsx", "tickets.xlsx", "ticket-story.xlsx", "quote-lines.xlsx", "ticket-files.xlsx", "knowledge-base.xlsx", "tickets/1/story.txt", "tickets/2/story.txt" })
        Check($"it has {want}", names.Contains(want));
    // Everything readable in it, the workbooks' insides included, as one text to search.
    var everything = new StringBuilder();
    foreach (var e in zip.Entries)
    {
        var b = Entry(e.FullName);
        everything.Append(Encoding.UTF8.GetString(b));
        if (e.FullName.EndsWith(".xlsx"))
            using (var inner = new ZipArchive(new MemoryStream(b), ZipArchiveMode.Read))
                foreach (var ie in inner.Entries) { using var s = ie.Open(); everything.Append(new StreamReader(s).ReadToEnd()); }
    }
    var all = everything.ToString();
    Check("no device password, of the ticket or of its second machine, anywhere", !all.Contains(Secret1) && !all.Contains(Secret2));
    Check("nothing of the other company anywhere", !all.Contains("OTHERCOMPANY-ONLY"));
    Check("no deleted article, no staged photo, no file never posted", !all.Contains("DELETED-ARTICLE") && !names.Any(n => n.Contains("never posted"))
        && names.Count(n => n.StartsWith("tickets/1/photos/")) == 5);

    // ---- files, byte for byte
    var files = Spreadsheet.ReadXlsx(Entry("ticket-files.xlsx"), 0).Rows;
    string Cell(Spreadsheet.Cell[] r, int i) => i < r.Length ? r[i].Text : "";
    int col(Spreadsheet.Sheet s, string h) => Array.FindIndex(s.Rows[0], c => c.Text == h);
    var fsheet = Spreadsheet.ReadXlsx(Entry("ticket-files.xlsx"), 0);
    int fWhat = col(fsheet, "What"), fPath = col(fsheet, "File in this export"), fOrig = col(fsheet, "Original name");
    bool Same(string zipPath, string disk) => onDisk.TryGetValue(disk, out var want) && Entry(zipPath).AsSpan().SequenceEqual(want);
    var photoRows = files.Skip(1).Where(r => Cell(r, fWhat).StartsWith("Photo")).ToList();
    Check("every photo is listed: three as it arrived, two of a part", photoRows.Count(r => Cell(r, fWhat) == "Photo as it arrived") == 3 && photoRows.Count(r => Cell(r, fWhat) == "Photo of a part") == 2);
    Check("...and each is the very bytes on the server",
        Enumerable.Range(1, 3).All(i => Same($"tickets/1/photos/arrived {i}.jpg", $"ticketphotos/{coast}/before{i}.jpg"))
        && Enumerable.Range(1, 2).All(i => Same($"tickets/1/photos/part {i}.jpg", $"ticketphotos/{coast}/part{i}.jpg")));
    Check("a file on a comment keeps its own name and bytes", Same("tickets/1/files/Supplier quote.pdf", $"ticketfiles/{coast}/att1.bin"));
    var lost = files.FirstOrDefault(r => Cell(r, fOrig) == "lost.txt");
    Check("a file gone from the server's disk is listed as missing, not skipped", lost != null && Cell(lost, fPath) == "missing on the server");
    var rec = files.FirstOrDefault(r => Cell(r, fWhat) == "Session recording");
    Check("a recording is listed but NOT copied without the tick", rec != null && Cell(rec, fPath).StartsWith("not included") && !names.Any(n => n.EndsWith(".mp4")));

    // ---- clients: the first sheet is Import clients' own
    Head("clients.xlsx: Import clients' own columns, and back in again");
    var csheet = Spreadsheet.ReadXlsx(Entry("clients.xlsx"), 0);
    Check("the first sheet's headings are exactly Import clients'", csheet.Rows[0].Select(c => c.Text).SequenceEqual(ClientImport.TemplateHeaders),
        string.Join(", ", csheet.Rows[0].Select(c => c.Text)));
    Check("...no archived client or contact on it", !csheet.Rows.Any(r => r.Any(c => c.Text.Contains("Old Archived") || c.Text == "Gone Gary")));
    Check("...and every cell is text, so a phone keeps its 0", csheet.Rows.Skip(1).SelectMany(r => r).All(c => !c.IsNumber)
        && csheet.Rows.Any(r => r.Any(c => c.Text == "0821234567")));
    var everyone = Spreadsheet.ReadXlsx(Entry("clients.xlsx"), 1);
    Check("the second sheet has everything, archived ones too, with roles and contact notes",
        everyone.Rows.Any(r => r.Any(c => c.Text == "Old Archived Client")) && everyone.Rows.Any(r => r.Any(c => c.Text == "Gone Gary"))
        && everyone.Rows.Any(r => r.Any(c => c.Text == "Mornings only")) && everyone.Rows.Any(r => r.Any(c => c.Text == "Accounts")));

    // Into an EMPTY platform database of its own, by the real import.
    var rtDir = Path.Combine(dir, "roundtrip"); Directory.CreateDirectory(rtDir);
    var rtOptions = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(rtDir, "platform.db")};Pooling=False").Options;
    int fresh;
    await using (var db = new PlatformDb(rtOptions))
    {
        await HarnessDb.MigrateAsync(db);
        var t = new Tenant { Slug = "fresh", CompanyName = "Fresh Start IT", TenantKey = "k-fresh", Status = TenantStatus.Active, Region = "ZA" };
        db.Tenants.Add(t); await db.SaveChangesAsync(); fresh = t.Id;
    }
    var parsed = ClientImport.Parse(csheet, southAfrica: true);
    var rtImport = new ClientImportService(new Factory(rtOptions));
    var (plan, why) = await rtImport.ImportAsync(fresh, TenantRole.Owner, parsed.Rows, new HashSet<string>());
    Check("Import clients reads it with no errors and no columns ignored", plan != null && plan.Errors == 0 && parsed.ColumnsIgnored.Count == 0 && parsed.Error.Length == 0,
        $"{why}{parsed.Error} ignored: {string.Join(", ", parsed.ColumnsIgnored)}");
    string N(string s) => s.Replace("\r\n", "\n").Trim();
    Dictionary<string, (string contact, string phone, string email, string address, string notes, string people)> Clients(DbContextOptions<PlatformDb> o, int tenant)
    {
        using var db = new PlatformDb(o);
        return db.Clients.Where(c => c.TenantId == tenant && !c.Archived).ToList()
            .ToDictionary(c => c.Name, c => (c.ContactName, c.Phone, c.Email, N(c.Address), N(c.Notes),
                string.Join(" | ", db.Contacts.Where(p => p.ClientId == c.Id && !p.Archived).AsEnumerable().Select(p => $"{p.Name}/{p.Phone}/{p.Email}").OrderBy(x => x))));
    }
    var before = Clients(options, coast); var after = Clients(rtOptions, fresh);
    Check("...and makes the same clients, none more", before.Keys.OrderBy(k => k).SequenceEqual(after.Keys.OrderBy(k => k)), string.Join(", ", after.Keys));
    var differs = before.Where(b => !after.TryGetValue(b.Key, out var a) || a.address != b.Value.address || a.notes != b.Value.notes || a.people != b.Value.people)
        .Select(b => $"{b.Key}: {b.Value} => {after.GetValueOrDefault(b.Key)}").ToList();
    Check("...each with its address, notes and contact people — names, phones, emails — exactly", differs.Count == 0, string.Join(" ;; ", differs));
    // The client's OWN contact person, phone and email (the fields from before contact people existed): Import fills a
    // blank from any row, so a blank cannot travel — but every one that is set comes back as it was.
    var ownDiffers = before.Where(b => after.TryGetValue(b.Key, out var a)
            && (b.Value.contact.Length > 0 && a.contact != b.Value.contact || b.Value.phone.Length > 0 && a.phone != b.Value.phone || b.Value.email.Length > 0 && a.email != b.Value.email))
        .Select(b => $"{b.Key}: {b.Value} => {after[b.Key]}").ToList();
    Check("...and the client's own contact person, phone and email, wherever they are set", ownDiffers.Count == 0, string.Join(" ;; ", ownDiffers));
    Check("...the switchboard number stays the client's and Pete keeps his own", after["Gamma Garage"].phone == "0315550000" && after["Gamma Garage"].people == "Pete Mokoena/0825550001/pete@gamma.test");
    var home = new ClientImportService(new Factory(options));
    var homePlan = await home.PlanAsync(coast, parsed.Rows, new HashSet<string>());
    Check("read back into the company it came from: no client and no contact person added",
        homePlan.New == 0 && homePlan.ContactsAdded == 0 && homePlan.Errors == 0, $"{homePlan.New} new, {homePlan.ContactsAdded} contacts");
    var fills = homePlan.Clients.SelectMany(c => c.Fills.Select(f => $"{c.Name}: {f}")).ToList();
    Check("...and nothing set is touched — at most a client's blank phone or email filled from its first person, as Import fills blanks",
        homePlan.Clients.SelectMany(c => c.Fills).All(f => f is "phone" or "email" or "contact person"), string.Join(", ", fills));

    // ---- tickets, the story, quote lines
    Head("the tickets, their story and quote lines read back");
    var tsheet = Spreadsheet.ReadXlsx(Entry("tickets.xlsx"), 0);
    string At(Spreadsheet.Sheet s, int row, string h) => Cell(s.Rows[row], col(s, h));
    Check("one row per ticket, in number order", tsheet.Rows.Count == 3 && At(tsheet, 1, "Ticket") == "#1" && At(tsheet, 2, "Ticket") == "#2");
    Check("...its words as written, line breaks and all", At(tsheet, 1, "Subject") == "Laptop will not boot"
        && At(tsheet, 1, "Description").Replace("\r\n", "\n") == "Black screen after the Windows logo.\nCustomer needs it by Friday.");
    Check("...category, client, people, machine, money and time", At(tsheet, 1, "Category") == "Workshop" && At(tsheet, 1, "Client") == "Acme Trading"
        && At(tsheet, 1, "Assigned to") == "Tom Technician, Sam Supervisor" && At(tsheet, 1, "Serial number") == "SN-HP-0001"
        && At(tsheet, 1, "Quote amount") == "1250.50" && At(tsheet, 1, "Invoice number") == "INV-1001" && At(tsheet, 1, "Time spent (minutes)") == "95"
        && At(tsheet, 1, "Password protected") == "Yes" && At(tsheet, 1, "Story") == "tickets/1/story.txt");
    Check("...the stage in words, and a ticket's parent", At(tsheet, 2, "Stage") == Workflow.Label(Workflow.GeneralFirst) && At(tsheet, 2, "Part of ticket") == "#1");
    var others = Spreadsheet.ReadXlsx(Entry("tickets.xlsx"), 1);
    Check("the second machine on the job is on its own sheet", others.Rows.Count == 2 && Cell(others.Rows[1], 2) == "Dell" && Cell(others.Rows[1], 4) == "SN-DELL-NEW");

    var story = Spreadsheet.ReadXlsx(Entry("ticket-story.xlsx"), 0);
    List<TicketNote> notes; await using (var db = new PlatformDb(options)) notes = await db.TicketNotes.Where(n => n.TenantId == coast).OrderBy(n => n.Id).ToListAsync();
    Check("one story row per entry, in order", story.Rows.Count == notes.Count + 1);
    var wordsCol = col(story, "Words");
    var shortOnes = notes.Select((n, i) => (n, row: story.Rows[i + 1])).Where(x => x.n.Body.Length < 30000).ToList();
    Check("...each one's words exactly (a formula-looking line stays words)", shortOnes.All(x => Cell(x.row, wordsCol).Replace("\r\n", "\n") == x.n.Body),
        string.Join(" | ", shortOnes.Where(x => Cell(x.row, wordsCol).Replace("\r\n", "\n") != x.n.Body).Select(x => x.n.Body)));
    Check("...and what kind of entry each is", story.Rows.Skip(1).Select(r => Cell(r, col(story, "Kind"))).SequenceEqual(new[] { "Comment", "Step", "Automatic", "From the customer", "Work done", "Comment" }),
        string.Join(", ", story.Rows.Skip(1).Select(r => Cell(r, col(story, "Kind")))));
    var stepRow = story.Rows[2];
    Check("...a step's form, where it moved the job, and that it was gone back over", Cell(stepRow, col(story, "Step")) == "Inspection findings"
        && Cell(stepRow, col(story, "Moved to")) == Workflow.Label(Workflow.GeneralFirst) && Cell(stepRow, col(story, "Gone back over")).EndsWith("by Sam Supervisor"));
    Check("...a comment's files named where they are in the zip", Cell(story.Rows[1], col(story, "Files")).Contains("tickets/1/files/Supplier quote.pdf"));
    var longCell = Cell(story.Rows[5], wordsCol);
    Check("a cell too long for Excel is cut, and says where the whole is", longCell.Length <= Spreadsheet.Workbook.MaxCell && longCell.EndsWith("the whole text is in that ticket's story.txt]"),
        $"{longCell.Length} characters");
    var story1 = TextOf("tickets/1/story.txt");
    Check("...and story.txt has it whole, to the last line", story1.Contains("THE VERY LAST LINE OF THE LOG") && story1.Contains("    Event log line 1500:"));
    Check("story.txt reads as a story: the ticket, then each entry with its author", story1.StartsWith("Ticket #1: Laptop will not boot")
        && story1.Contains("Tom Technician — Inspection findings →") && story1.Contains("File: tickets/1/files/Supplier quote.pdf")
        && story1.Contains("Also on this job: Dell Latitude 5440, serial SN-DELL-NEW"));

    var qsheet = Spreadsheet.ReadXlsx(Entry("quote-lines.xlsx"), 0);
    Check("the quote line: item, part number, serial, quantity", qsheet.Rows.Count == 2 && At(qsheet, 1, "Item") == "SSD 512GB NVMe" && At(qsheet, 1, "Part number") == "SSD-512"
        && At(qsheet, 1, "Serial number") == "S512-998" && At(qsheet, 1, "Quantity") == "2");
    Check("...and its photos, named where they are", At(qsheet, 1, "Photos").Replace("\r\n", "\n") == "tickets/1/photos/part 1.jpg\ntickets/1/photos/part 2.jpg");

    // ---- the knowledge base
    Head("the knowledge base: folders, articles, pictures");
    var ksheet = Spreadsheet.ReadXlsx(Entry("knowledge-base.xlsx"), 0);
    Check("three articles (the deleted one left out)", ksheet.Rows.Count == 4);
    var hpRow = ksheet.Rows.First(r => r.Any(c => c.Text == "Install the HP driver"));
    var hpPath = Cell(hpRow, col(ksheet, "File in this export"));
    Check("an article sits in its folders, as in the portal", hpPath == "knowledge-base/General/Printers/HP/Install the HP driver.html"
        && Cell(hpRow, col(ksheet, "Folder")) == "Printers › HP" && Cell(hpRow, col(ksheet, "Written up from ticket")) == "#1", hpPath);
    var acmeRow = ksheet.Rows.First(r => r.Any(c => c.Text == "Acme VPN: set-up?"));
    Check("a client's article is under Clients, its folder's name made safe for a disk",
        Cell(acmeRow, col(ksheet, "File in this export")) == "knowledge-base/Clients/Acme Trading/Network VPN Wi-Fi/Acme VPN set-up.html",
        Cell(acmeRow, col(ksheet, "File in this export")));
    var html = TextOf(hpPath);
    var src = Regex.Match(html, "<img src=\"([^\"]+)\"").Groups[1].Value;
    var resolved = Path.GetFullPath(Path.Combine("/root", Path.GetDirectoryName(hpPath)!, Uri.UnescapeDataString(src))).Replace('\\', '/');
    resolved = resolved[(resolved.IndexOf("/root/", StringComparison.Ordinal) + 6)..];
    Check("its picture points inside the zip, at the very bytes on the server", Same(resolved, $"knowledge/{coast}/img1.png"), $"{src} → {resolved}");
    Check("what was typed is encoded — no script runs", html.Contains("&lt;script&gt;") && !html.Contains("<script>"));
    Check("...and the .txt beside it is the article as written, marks and all",
        TextOf(Path.ChangeExtension(hpPath, ".txt")) == $"# Steps\n1. Download it\n{KbText.ImageTag(img)}\n**Done** <script>alert('x')</script>");
    var folders = Spreadsheet.ReadXlsx(Entry("knowledge-base.xlsx"), 1);
    Check("the folders are listed with their paths", folders.Rows.Any(r => r.Any(c => c.Text == "Printers › HP")) && folders.Rows.Any(r => r.Any(c => c.Text == "Network: VPN/Wi-Fi")));
    var readme = TextOf("README.txt");
    Check("README says what each file is, and that recordings are not in this one", readme.Contains("clients.xlsx") && readme.Contains("NOT included in this export")
        && readme.StartsWith("Coastline IT — data export"));

    // ================================================================== recordings, and a newer one replacing
    Head("with recordings, and a newer export replacing the older");
    var (_, sp3, _) = await Get(sam, "/portal/export");
    await Post(sam, "/portal/export?handler=Start", sp3, ("recordings", "true"));
    var job2 = await WaitDone(coast, job.Id);
    Check("made again, recordings ticked", job2?.State == ExportState.Ready && job2.IncludeRecordings, $"{job2?.State} {job2?.Error}");
    var zip2 = await (await sam.GetAsync($"/portal/export?handler=Download&id={job2!.Id}")).Content.ReadAsByteArrayAsync();
    using (var z2 = new ZipArchive(new MemoryStream(zip2), ZipArchiveMode.Read))
    {
        var mp4 = z2.Entries.FirstOrDefault(e => e.FullName.StartsWith("tickets/1/recordings/") && e.FullName.EndsWith(".mp4"));
        byte[] got = Array.Empty<byte>(); if (mp4 != null) { using var s = mp4.Open(); using var m = new MemoryStream(); s.CopyTo(m); got = m.ToArray(); }
        Check("the recording is in it, byte for byte", mp4 != null && got.AsSpan().SequenceEqual(onDisk[$"recordings/{coast}/rec1.mp4"]), mp4?.FullName ?? "none");
    }
    await using (var db = new PlatformDb(options))
    {
        var first = await db.CompanyExports.SingleAsync(e => e.Id == job.Id);
        Check("the older export is replaced: marked, and its file deleted", first.State == ExportState.Expired && !File.Exists(Path.Combine(data, "exports", coast.ToString(), first.FileName)));
    }
    var oldDl = await sam.GetAsync($"/portal/export?handler=Download&id={job.Id}");
    Check("...so it no longer downloads", oldDl.Content.Headers.ContentType?.MediaType != "application/zip");

    // ================================================================== made by us, for a company that cannot sign in
    Head("a suspended company: we make it, its owners get a link");
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.SingleAsync(x => x.Id == coast); t.Status = TenantStatus.Suspended; await db.SaveChangesAsync();
    }
    var shutOut = await SignIn("olive@coast.test");
    Check("its owner cannot sign in to make one", (await Get(shutOut, "/portal/export")).code != HttpStatusCode.OK);
    var admin = Browser();
    var (_, loginForm, _) = await Get(admin, "/owner/login");
    var keyStep = await (await Post(admin, "/owner/login", loginForm, ("Key", AdminKey))).Content.ReadAsStringAsync();
    keyStep = WebUtility.HtmlDecode(keyStep);
    var keySecret = Regex.Match(keyStep, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
    string Field(string h, string name) => Regex.Match(h, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    await Post(admin, "/owner/login", keyStep, ("UseKey", "true"), ("Pending", Field(keyStep, "Pending")), ("Code", RemoteSupport.Shared.Totp.Now(keySecret)));
    var (tcode, tenantsPage, _) = await Get(admin, "/owner/tenants");
    Check("Owner → All companies offers Export their data", tcode == HttpStatusCode.OK && tenantsPage.Contains($"data-export-form=\"{coast}\""), tcode.ToString());
    var asked = WebUtility.HtmlDecode(await (await Post(admin, "/owner/tenants?handler=Act", tenantsPage, ("server", "0"), ("id", coast.ToString()), ("act", "export"))).Content.ReadAsStringAsync());
    Check("...and says who will be emailed", asked.Contains("Export of Coastline IT started") && asked.Contains("olive@coast.test"), Regex.Match(asked, "alert-(ok|bad)\">([^<]*)").Groups[2].Value);
    var job3 = await WaitDone(coast, job2.Id);
    Check("it is made", job3?.State == ExportState.Ready && job3.ByUs, $"{job3?.State} {job3?.Error}");
    var linkMail = await mail.WaitFor("olive@coast.test", "your data export is ready");
    var link = linkMail == null ? "" : Regex.Match(linkMail.Body, @"http://127\.0\.0\.1:\d+/export/[A-Za-z0-9_-]+").Value;
    Check("the owner is emailed a link to it", link.Length > 0, linkMail?.Subject ?? "no mail");
    await using (var db = new PlatformDb(options))
    {
        var j = await db.CompanyExports.SingleAsync(e => e.Id == job3!.Id);
        Check("...kept on the server as its fingerprint only", j.LinkHash.Length == 64 && !all.Contains(link) && j.LinkHash == CompanyExportService.Hash(link[(link.LastIndexOf('/') + 1)..]));
    }
    var visitor = Browser();
    var opened = await visitor.GetAsync(link);
    var openedHtml = WebUtility.HtmlDecode(await opened.Content.ReadAsStringAsync());
    Check("opening the link downloads nothing — a page and a button (mail filters open links)",
        opened.Content.Headers.ContentType?.MediaType == "text/html" && openedHtml.Contains("Coastline IT") && openedHtml.Contains("id=\"exportDownload\""));
    await using (var db = new PlatformDb(options))
        Check("...and counts no download", (await db.CompanyExports.SingleAsync(e => e.Id == job3!.Id)).Downloads == 0);
    var got3 = await Post(visitor, new Uri(link).AbsolutePath, openedHtml);
    var zip3 = await got3.Content.ReadAsByteArrayAsync();
    Check("the button downloads the zip, with no sign-in", got3.Content.Headers.ContentType?.MediaType == "application/zip" && zip3.Length == job3!.Bytes, got3.StatusCode.ToString());
    using (var z3 = new ZipArchive(new MemoryStream(zip3), ZipArchiveMode.Read))
        Check("...and it is the company's: its README says one of us asked", new StreamReader(z3.GetEntry("README.txt")!.Open()).ReadToEnd().Contains("of The Remote Support Program"));
    var wrong = WebUtility.HtmlDecode(await (await visitor.GetAsync("/export/not-a-real-token-at-all")).Content.ReadAsStringAsync());
    Check("a wrong token is refused", wrong.Contains("id=\"exportGone\""));
    await using (var db = new PlatformDb(options))
    {
        Check("the admin log has it by name: started, and the link emailed", await db.AdminLog.AnyAsync(e => e.What.StartsWith("Export started"))
            && await db.AdminLog.AnyAsync(e => e.What.StartsWith("Export ready") && e.What.Contains("link emailed to olive@coast.test")));
        var j = await db.CompanyExports.SingleAsync(e => e.Id == job3!.Id); j.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
    }
    var expiredPage = WebUtility.HtmlDecode(await (await visitor.GetAsync(link)).Content.ReadAsStringAsync());
    Check("past its days, the link says it no longer works", expiredPage.Contains("id=\"exportGone\""));
    var gone = false;
    for (var until = DateTime.UtcNow.AddSeconds(20); DateTime.UtcNow < until && !gone; await Task.Delay(300))
        await using (var db = new PlatformDb(options)) gone = (await db.CompanyExports.SingleAsync(e => e.Id == job3!.Id)).State == ExportState.Expired;
    Check("...and the sweep deletes its file", gone && !File.Exists(Path.Combine(data, "exports", coast.ToString(), job3!.FileName)));

    // ================================================================== a restart part way
    Head("an export cut short by a restart is made again");
    long cut;
    await using (var db = new PlatformDb(options))
    {
        var e = new CompanyExport { TenantId = other, State = ExportState.Making, RequestedBy = "Otto Other", Progress = "Tickets: 0 of 1", FileName = "20261022000000-cutshort.zip", StartedUtc = DateTime.UtcNow };
        db.CompanyExports.Add(e); await db.SaveChangesAsync(); cut = e.Id;
    }
    var partFile = Path.Combine(data, "exports", other.ToString(), "20261022000000-cutshort.zip.part");
    Directory.CreateDirectory(Path.GetDirectoryName(partFile)!); File.WriteAllBytes(partFile, Bytes(50));
    try { server.Kill(true); await server.WaitForExitAsync(); } catch { }
    server = await StartServer();
    var made = await WaitDone(other, cut - 1);
    Check("after the restart it is made from the start", made?.Id == cut && made.State == ExportState.Ready, $"{made?.State} {made?.Error}");
    Check("...its half-made file gone", !File.Exists(partFile));
    var ottoDl = await (await SignIn("otto@other.test")).GetAsync($"/portal/export?handler=Download&id={cut}");
    var ottoZip = await ottoDl.Content.ReadAsByteArrayAsync();
    using (var z4 = new ZipArchive(new MemoryStream(ottoZip), ZipArchiveMode.Read))
    {
        var t4 = Spreadsheet.ReadXlsx(new BinaryReader(z4.GetEntry("tickets.xlsx")!.Open()).ReadBytes(1 << 20), 0);
        Check("...and it is the other company's own, and only its own", t4.Rows.Count == 2 && Cell(t4.Rows[1], 1) == "OTHERCOMPANY-ONLY ticket");
    }
}
catch (Exception ex) { fail++; Console.WriteLine($"  FAIL  the harness stopped: {ex}"); }
finally
{
    try { server?.Kill(true); } catch { }
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

/// <summary>The import service's factory, over the harness's own options.</summary>
sealed class Factory(DbContextOptions<PlatformDb> o) : IDbContextFactory<PlatformDb>
{
    public PlatformDb CreateDbContext() => new(o);
}

/// <summary>Just enough SMTP for the platform to deliver to: every message kept, with its envelope recipients.</summary>
sealed class FakeSmtp : IDisposable
{
    private TcpListener? _l;
    private readonly List<Message> _mails = new();
    public int Port { get; private set; }

    public void Start()
    {
        _l = new TcpListener(IPAddress.Loopback, 0);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (true) { TcpClient c; try { c = await _l.AcceptTcpClientAsync(); } catch { return; } _ = Handle(c); } });
    }

    private async Task Handle(TcpClient c)
    {
        using (c)
        {
            var s = c.GetStream();
            var r = new StreamReader(s, Encoding.UTF8);
            var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            await w.WriteLineAsync("220 exportcheck ESMTP");
            var to = new List<string>();
            while (await r.ReadLineAsync() is { } line)
            {
                var cmd = line.ToUpperInvariant();
                if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await w.WriteLineAsync("250 exportcheck");
                else if (cmd.StartsWith("RCPT TO:")) { to.Add(line[8..].Trim().Trim('<', '>').ToLowerInvariant()); await w.WriteLineAsync("250 OK"); }
                else if (cmd == "DATA")
                {
                    await w.WriteLineAsync("354 go ahead");
                    var sb = new StringBuilder();
                    while (await r.ReadLineAsync() is { } d && d != ".") sb.Append(d.StartsWith("..") ? d[1..] : d).Append("\r\n");
                    lock (_mails) _mails.Add(new Message(to.ToList(), sb.ToString()));
                    to.Clear();
                    await w.WriteLineAsync("250 queued");
                }
                else if (cmd == "QUIT") { await w.WriteLineAsync("221 bye"); break; }
                else await w.WriteLineAsync("250 OK");
            }
        }
    }

    public async Task<Message?> WaitFor(string to, string subjectPart)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            Message? m; lock (_mails) m = _mails.LastOrDefault(x => x.To.Contains(to) && x.Subject.Contains(subjectPart));
            if (m != null) return m;
            await Task.Delay(100);
        }
        return null;
    }

    public void Dispose() { try { _l?.Stop(); } catch { } }
}

/// <summary>One message, read as a mail program would: unfolded headers, decoded body.</summary>
sealed class Message
{
    public List<string> To { get; }
    public string Subject = "", Body = "";
    public Message(List<string> to, string raw)
    {
        To = to;
        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = Regex.Replace(split < 0 ? raw : raw[..split], "\r\n[ \t]+", " ");
        var body = split < 0 ? "" : raw[(split + 4)..];
        string H(string n) => Regex.Match(head, $"^{n}:\\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        Subject = Words(H("Subject"));
        var cte = H("Content-Transfer-Encoding").ToLowerInvariant();
        Body = cte == "base64" ? Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Replace(body, "\\s", "")))
             : cte == "quoted-printable" ? QuotedPrintable(body) : body;
        Body = Body.Replace("\r\n", "\n");
    }

    private static string QuotedPrintable(string s)
    {
        s = s.Replace("=\r\n", "");
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string Words(string v) => Regex.Replace(v, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
        m.Groups[2].Value.ToUpperInvariant() == "B"
            ? Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[3].Value))
            : QuotedPrintable(m.Groups[3].Value.Replace('_', ' ')));
}
