// =====================================================================================
// jobtypepage - proves the Job types PAGE (HANDOVER 167, stage B of docs/NEXT-SESSION-JOBTYPES.md):
//
//   Clint: "would it be possible to show them the stages of the ones they do want so that they can deselect stages
//   they dont want without breaking the flow."
//
// A platform of its own (SQLite, or PostgreSQL with RSP_TEST_DB=postgres), signed in to as each role:
//   - the owner is sent to the page ONCE from the dashboard, with Keep everything; a supervisor is not sent;
//   - a technician and the office read it and cannot change it (refused, nothing stored);
//   - the line under each card, asked as boxes are ticked, and the judge's answer with it;
//   - Save: what is stored (a switched-off category's steps left out), who and when, the boxes as saved;
//   - and what follows on the portal: New ticket without the switched-off category and with photos optional, a
//     workshop job booked in with none, its page offering Repairable at once, Edit without the category, and a
//     free company refused the page.
//
//   dotnet run -c Debug --project tools/jobtypepage/jobtypepage.csproj
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-jobtypepage-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
const string Pw = "job-types-page-2026";
Console.WriteLine($"jobtypepage — the Job types page, on a platform of its own on port {port} ({HarnessDb.Name})");

Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl, ["Platform:Region"] = "ZA" }).Build();
    var tenants = new TenantService(factory, cfg, dataDir, DataProtectionProvider.Create("jobtypepage"));
    var (tenant, _) = await tenants.CreateAsync("Page Co", "page-co", 7);
    int tid = tenant!.Id;
    await tenants.AddTechnicianAsync(tid, "olga@pageco.test", Pw, "Olga Owner", TenantRole.Owner | TenantRole.Admin);
    await tenants.AddTechnicianAsync(tid, "sam@pageco.test", Pw, "Sam Supervisor", TenantRole.Admin | TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "tess@pageco.test", Pw, "Tess Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "bea@pageco.test", Pw, "Bea Office", TenantRole.Billing);
    var (free, _) = await tenants.CreateAsync("Free Person", "free-person", 1);
    await using (var db = new PlatformDb(options))
    {
        var f = await db.Tenants.FirstAsync(x => x.Id == free!.Id);
        f.Plan = RemoteSupport.Shared.FreePlan.Name;
        await db.SaveChangesAsync();
    }
    await tenants.AddTechnicianAsync(free!.Id, "free@person.test", Pw, "Fran Free", TenantRole.Owner | TenantRole.Technician);
    var photos = new TicketPhotoService(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<TicketPhotoService>.Instance, dataDir);
    var tickets = new TicketService(factory, photos);
    var general = await tickets.CreateAsync(tid, "Printer", "It jams.", "Olga Owner", TicketSource.Manual, assignedTo: "Tess Tech");
    async Task<Tenant> Company() { await using var db = new PlatformDb(options); return await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == tid); }

    // =========================================================== the platform
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

    // =========================================================== the first visit
    Head("the owner is shown Job types once; nobody else is sent there");
    var (olga, landed) = await Portal(port, "olga@pageco.test");
    Check("the owner's sign-in lands on Job types, first time", landed.Contains("/portal/jobtypes") && landed.Contains("first=1"), landed);
    var page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/jobtypes?first=1"));
    Check("...headed Job types, saying everything is on, with Keep everything", page.Contains("<h1>Job types</h1>")
        && page.Contains("Everything is switched on. Untick what your company doesn't use") && page.Contains("Keep everything"),
        $"h1 {page.Contains("<h1>Job types</h1>")}, line {page.Contains("Everything is switched on")}, keep {page.Contains("Keep everything")}: {Snip(page, "Everything is")}");
    Check("...every card, Workshop's tick, the locks with their reasons", JobTypes.Cards.All(c => page.Contains($"<h3>{c.Title}</h3>"))
        && page.Contains("value=\"cat-workshop\"") && page.Contains("Every job needs a way to end without a bill.") && page.Contains("🔒 Always on"));
    Check("...and the line a workshop job takes", page.Contains("Booked in → Inspected → Repairable → To be invoiced → Invoiced — awaiting payment → Awaiting collection → Closed"));
    // 2026-10-28, Clint: "When I click on the dashboard tab, it jumps to job types" — the owner who looks and clicks away,
    // answering nothing, must get the dashboard the next time (the old rule sent them back until Save or Keep everything).
    var away = await olga.GetAsync("/portal");
    Check("an owner who clicks away without answering gets the dashboard next time", away.RequestMessage!.RequestUri!.AbsolutePath == "/portal",
        away.RequestMessage.RequestUri.ToString());
    away = await olga.GetAsync("/portal");
    Check("...and every time after", away.RequestMessage!.RequestUri!.AbsolutePath == "/portal" && (await Company()).JobTypesChosenUtc == null,
        away.RequestMessage.RequestUri.ToString());
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/jobtypes?first=1"));
    Check("...while the page itself still offers Keep everything until they answer", page.Contains("Keep everything"));
    var (sam, samLanded) = await Portal(port, "sam@pageco.test");
    Check("a supervisor's sign-in goes to the dashboard, as before", samLanded.EndsWith("/portal"), samLanded);
    var res = await PostForm(olga, "/portal/jobtypes?handler=Keep", new());
    var kept = await Company();
    Check("Keep everything: nothing off, and it counts as chosen", kept.JobTypesOff == "" && kept.JobTypesChosenUtc != null && kept.JobTypesChosenBy == "Olga Owner",
        $"[{kept.JobTypesOff}] {kept.JobTypesChosenBy}");
    var dash = await olga.GetAsync("/portal");
    Check("...so the dashboard is the dashboard from now on", dash.RequestMessage!.RequestUri!.AbsolutePath == "/portal", dash.RequestMessage.RequestUri.ToString());
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/jobtypes?first=1"));
    Check("...and the first-time line is not shown again", !page.Contains("Keep everything") && page.Contains("Last changed by Olga Owner"));

    // =========================================================== who may change it
    Head("a technician and the office read it; they cannot change it");
    var (tess, _) = await Portal(port, "tess@pageco.test");
    page = WebUtility.HtmlDecode(await tess.GetStringAsync("/portal/jobtypes"));
    Check("a technician sees the page, told only a supervisor or the owner can change it, with no Save",
        page.Contains("<h1>Job types</h1>") && page.Contains("Only a supervisor or the owner can change this") && !page.Contains("id=\"jtSave\""));
    Check("...every box drawn disabled", Regex.Matches(page, "<input type=\"checkbox\" name=\"on\"[^>]*>").All(m => m.Value.Contains("disabled")));
    res = await PostForm(tess, "/portal/jobtypes", new() { ["on"] = "cat-workshop" });
    page = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    Check("a technician's post anyway is refused, and nothing is stored", page.Contains("supervisor's or the owner's to change") && (await Company()).JobTypesOff == "");
    var (bea, _) = await Portal(port, "bea@pageco.test");
    page = WebUtility.HtmlDecode(await bea.GetStringAsync("/portal/jobtypes"));
    Check("the office reads it too, and has it in the menu", page.Contains("Only a supervisor or the owner can change this") && page.Contains("href=\"/portal/jobtypes\""));

    // =========================================================== the line, as boxes are ticked
    Head("the line under each card, asked as boxes are ticked");
    var json = JsonDocument.Parse(await olga.GetStringAsync("/portal/jobtypes?handler=Path&off=" + Uri.EscapeDataString("inspection,track-payment,cat-walkin")));
    var lines = json.RootElement.GetProperty("lines");
    Check("Workshop without inspection or tracking payment", lines.GetProperty("Workshop").GetString() == "Booked in → Repairable → To be invoiced → Awaiting collection → Closed",
        lines.GetProperty("Workshop").GetString());
    Check("Walk in switched off says so", lines.GetProperty("Walk in").GetString()!.StartsWith("Switched off"));
    Check("the judge's answer comes with it: nothing wrong", json.RootElement.GetProperty("problem").GetString() == "");
    json = JsonDocument.Parse(await tess.GetStringAsync("/portal/jobtypes?handler=Path&off=quotes"));
    Check("anybody who can read the page may ask it (it changes nothing)", json.RootElement.GetProperty("lines").GetProperty("Quotes").GetString() == "No quotes.");
    Check("...and asking it changed nothing", (await Company()).JobTypesOff == "");

    // =========================================================== saving
    Head("Save — and the console told at once (§168)");
    var console = await Sign(port, "sam@pageco.test", Pw);
    var first = await console.Next<StateMsg>(_ => true, 15000);
    Check("a console signing in is told every category is on and a booking needs 3 photos",
        first != null && first.CategoriesOff.Count == 0 && first.BookingPhotos == 3, first == null ? "no state" : $"[{string.Join(",", first.CategoriesOff)}] {first.BookingPhotos}");
    var ticked = JobTypes.Switches.Select(s => s.Key).Except(new[] { JobTypes.Inspection, JobTypes.PhotosIn, JobTypes.WalkIn, JobTypes.WalkInToWorkshop }).ToList();
    res = await PostForm(sam, "/portal/jobtypes", new(), ticked.Select(k => new KeyValuePair<string, string>("on", k)));
    page = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    var told = await console.Next<StateMsg>(s => s.CategoriesOff.Count > 0, 15000);
    Check("the save reaches the open console: Walk in off, no photos at booking — without signing out",
        told != null && told.CategoriesOff.SequenceEqual(new[] { (int)TicketCategory.WalkIn }) && told.BookingPhotos == 0,
        told == null ? "no new state" : $"[{string.Join(",", told.CategoriesOff)}] {told.BookingPhotos}");
    var later = await Sign(port, "tess@pageco.test", Pw);
    var tessState = await later.Next<StateMsg>(_ => true, 15000);
    Check("...and a console signing in afterwards is told the same", tessState != null
        && tessState.CategoriesOff.SequenceEqual(new[] { (int)TicketCategory.WalkIn }) && tessState.BookingPhotos == 0);
    var co = await Company();
    Check("a supervisor saves; the walk in's own step is left out with it", co.JobTypesOff == $"{JobTypes.PhotosIn},{JobTypes.Inspection},{JobTypes.WalkIn}", co.JobTypesOff);
    Check("...told so, with who", page.Contains("Saved. New tickets follow this from now on") && page.Contains("Last changed by Sam Supervisor"));
    Check("...the boxes drawn as saved", !Box(page, JobTypes.Inspection).Contains("checked") && Box(page, JobTypes.Delivery).Contains("checked")
        && !Box(page, JobTypes.WalkIn).Contains("checked") && Box(page, JobTypes.PaidCounter).Contains("disabled"), Box(page, JobTypes.Inspection));
    Check("...and the line drawn from what is stored", page.Contains("Booked in → Repairable → To be invoiced"));

    // =========================================================== what follows on the portal
    Head("New ticket, the ticket page and Edit follow it");
    page = WebUtility.HtmlDecode(await olga.GetStringAsync("/portal/tickets/new"));
    Check("New ticket offers no Walk in", !page.Contains($"data-cat=\"{(int)TicketCategory.WalkIn}\"") && page.Contains($"data-cat=\"{(int)TicketCategory.Workshop}\""));
    Check("...and says photos are optional", page.Contains("Photos of the machine as it arrived (optional)") && page.Contains("doesn't ask for photos when a machine is booked in"));
    res = await PostForm(olga, "/portal/tickets/new", new()
    { ["Category"] = ((int)TicketCategory.Workshop).ToString(), ["ClientName"] = "Harbour Café", ["Subject"] = "Laptop will not charge",
      ["Description"] = "Dead on arrival.", ["People"] = "Tess Tech", ["Priority"] = "1" }, stageField: "StageId");
    Ticket? w;
    await using (var db = new PlatformDb(options)) w = await db.Tickets.OrderByDescending(x => x.Id).FirstOrDefaultAsync(x => x.TenantId == tid && x.Category == TicketCategory.Workshop);
    Check("a workshop machine is booked in with no photos", w != null && w.Subject == "Laptop will not charge" && w.RouteOff == co.JobTypesOff,
        w == null ? Snip(WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()), "alert") : w.RouteOff);
    res = await PostForm(olga, "/portal/tickets/new", new()
    { ["Category"] = ((int)TicketCategory.WalkIn).ToString(), ["ClientName"] = "Harbour Café", ["Subject"] = "Mouse", ["People"] = "Tess Tech" }, stageField: "StageId");
    page = WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync());
    Check("a walk in posted anyway is refused, in words", page.Contains("switched Walk in off"), Snip(page, "alert"));
    if (w != null)
    {
        page = WebUtility.HtmlDecode(await olga.GetStringAsync($"/portal/t/{w.Number}"));
        Check("the workshop job's page offers Repairable at once, and no Inspection findings",
            page.Contains("value=\"repairable\"") && !page.Contains("value=\"inspection\""), Snip(page, "Repairable"));
    }
    page = WebUtility.HtmlDecode(await olga.GetStringAsync($"/portal/t/{general.Number}"));
    Check("Edit the ticket offers no Walk in", page.Contains("teCat(") && !page.Contains($"teCat({(int)TicketCategory.WalkIn})") && page.Contains($"teCat({(int)TicketCategory.Workshop})"));

    Head("the free version has no Job types");
    var (fran, _) = await Portal(port, "free@person.test");
    var fr = await fran.GetAsync("/portal/jobtypes");
    var frPage = await fr.Content.ReadAsStringAsync();
    Check("a free company is refused the page", !frPage.Contains("<h1>Job types</h1>"), $"{(int)fr.StatusCode} {fr.RequestMessage!.RequestUri}");
}
finally
{
    try { server?.Kill(entireProcessTree: true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"jobtypepage ({HarnessDb.Name}): {pass} of {pass + fail} checks passed.");
try { Directory.Delete(dataDir, true); } catch { }
return fail == 0 ? 0 : 1;

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

// The tick box for one switch, as drawn.
static string Box(string page, string key) => Regex.Match(page, $"<input type=\"checkbox\" name=\"on\" value=\"{Regex.Escape(key)}\"[^>]*>").Value;

static async Task<HttpResponseMessage> PostForm(HttpClient c, string path, Dictionary<string, string> fields,
    IEnumerable<KeyValuePair<string, string>>? more = null, string? stageField = null)
{
    // The token from the page the form is on (§80), fetched WITHOUT its handler.
    var page = await (await c.GetAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""))).Content.ReadAsStringAsync();
    fields["__RequestVerificationToken"] = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
    if (stageField != null) fields[stageField] = Between(page, $"name=\"{stageField}\" value=\"", "\"") ?? "";
    return await c.PostAsync(path, new FormUrlEncodedContent(fields.Concat(more ?? Enumerable.Empty<KeyValuePair<string, string>>())));
}
static async Task<(HttpClient, string)> Portal(int port, string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
    { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    var r = await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "job-types-page-2026" });
    return (c, r.RequestMessage!.RequestUri!.ToString());
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
    return Regex.Replace(Regex.Replace(html.Substring(i, Math.Min(300, html.Length - i)), "<[^>]+>", " "), "\\s+", " ").Trim();
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
    /// <summary>The first message of this kind matching, waiting for it; each is taken once, so a later ask sees later ones.</summary>
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
