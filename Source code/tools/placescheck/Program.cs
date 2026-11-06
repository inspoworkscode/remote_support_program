// =====================================================================================
// placescheck — five companies at a time (2026-10-22, HANDOVER §153).
//
// Clint: "My thought process is allow 5 companies in at a time and rather test it that way" — and, asked: count only
// paying companies (not Anywhere IT, pilots or the free version), and the website says honestly when it is full.
//
// A real platform, signups ON, driven over HTTP:
//
//   WHAT COUNTS   Active, Trial and Suspended paying companies, and a signup Pending for fewer than 3 days; never
//                 Anywhere IT (not billed), a pilot, the free version, a Closed company, or a Pending one gone stale.
//   OPEN          with a place left, a full-version signup goes through — and fills the last place.
//   FULL          the plan card says so; choosing the full version shows the waiting list, not the terms; a form posted
//                 straight to Create makes nothing; the free version still signs up; joining the list is kept as Full.
//   OFFERED       Owner → Approvals shows 5 of 5 and offers the entry a place; the link (shown when the mail cannot go)
//                 signs the company up though every place is taken, ONCE — three at once on it make one company; the
//                 same link again is refused; an expired one says it ran out.
//   THE NUMBER    set to 0 on Owner → Approvals, signup is open again.
//
//   dotnet run -c Release --project tools/placescheck/placescheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

Console.WriteLine("placescheck — five paying companies at a time, then the waiting list");
var dir = Path.Combine(Path.GetTempPath(), "placescheck-" + Guid.NewGuid().ToString("N")[..8]);
var data = Path.Combine(dir, "data"); Directory.CreateDirectory(data);
var d = new DirectoryInfo(AppContext.BaseDirectory);
while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
var proj = Path.Combine(d!.FullName, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
var baseUrl = $"http://127.0.0.1:{port}";
const string Pw = "PlacesCheck!2026";
const string AdminKey = "placescheck-admin-key-0123456789abcdef0123456789abcdef";

ProcessStartInfo Psi(params string[] args)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "--no-build", "-c", "Debug", "--project", proj, "--" }.Concat(args)) psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = data;
    HarnessDb.Use(psi.Environment, data);
    psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
    psi.Environment["RSP_Platform__SignupsEnabled"] = "true";
    psi.Environment["RSP_Platform__Region"] = "ZA";
    psi.Environment["RSP_Platform__PublicUrl"] = baseUrl;
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
    psi.Environment["RSP_Urls"] = baseUrl; psi.Environment["ASPNETCORE_URLS"] = baseUrl;
    return psi;
}

var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(data, "platform.db")};Pooling=False").Options;
Process? server = null;
try
{
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build."); return 1; }
    server = Process.Start(Psi("run"))!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    var until = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < until) { try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); break; } catch { await Task.Delay(400); } }

    HttpClient Browser() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    async Task<string> Get(HttpClient h, string path) => WebUtility.HtmlDecode(await (await h.GetAsync(path)).Content.ReadAsStringAsync());
    string TokenIn(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    async Task<string> Post(HttpClient h, string path, string fromHtml, params (string k, string v)[] f)
    {
        var all = f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", TokenIn(fromHtml)));
        return WebUtility.HtmlDecode(await (await h.PostAsync(path, new FormUrlEncodedContent(all))).Content.ReadAsStringAsync());
    }
    async Task<int> Companies() { await using var db = new PlatformDb(options); return await db.Tenants.CountAsync(); }
    var terms = Terms.For("ZA");
    (string k, string v)[] Company(string company, string email, string place = "") => new[]
    {
        ("Region", "ZA"), ("Plan", "full"), ("Place", place), ("TermsSha", terms.Sha256), ("Accept", "true"), ("CompanyName", company), ("ContactName", "Pat " + company),
        ("ContactNumber", "+27 82 555 0101"), ("Email", email), ("Password", Pw), ("Primary", "#1F4E79"), ("Accent", "#2E9BFF")
    };
    /// Signs a company up through the pages, as a person would: the plan, the terms, the form.
    async Task<string> SignUp(string company, string email, string place = "")
    {
        var b = Browser();
        var q = place.Length > 0 ? $"&place={place}" : "";
        var page = await Get(b, $"/signup?region=ZA&plan=full{q}");
        if (!page.Contains("id=\"termsText\"")) return page;
        var details = await Post(b, "/signup?handler=Terms", page, ("Region", "ZA"), ("Plan", "full"), ("Place", place), ("TermsSha", terms.Sha256), ("Accept", "true"));
        return await Post(b, "/signup?handler=Create", details, Company(company, email, place));
    }

    // ---- what counts ----------------------------------------------------------------------------------
    Head("what takes a place");
    await using (var db = new PlatformDb(options))
    {
        Tenant T(string slug, TenantStatus s, Action<Tenant>? also = null)
        {
            var t = new Tenant { Slug = slug, CompanyName = slug, TenantKey = "k-" + slug, Status = s, Region = "ZA" };
            also?.Invoke(t);
            return t;
        }
        db.Tenants.AddRange(
            T("active-a", TenantStatus.Active), T("trial-b", TenantStatus.Trial, t => t.TrialEndsUtc = DateTime.UtcNow.AddDays(10)),
            T("suspended-c", TenantStatus.Suspended),
            T("anywhereit", TenantStatus.Active, t => t.NotBilled = true), T("pilot-x", TenantStatus.Active, t => t.Pilot = true),
            T("free-y", TenantStatus.Active, t => t.Plan = "free"), T("closed-z", TenantStatus.Closed),
            T("stale-pending", TenantStatus.Pending, t => t.CreatedUtc = DateTime.UtcNow.AddDays(-4)));
        await db.SaveChangesAsync();
    }
    await using (var db = new PlatformDb(options))
    {
        var state = await OpenPlaces.StateAsync(db);
        Check("five places on a new server", state.Limit == OpenPlaces.Default && OpenPlaces.Default == 5, state.Limit.ToString());
        Check("Active, Trial and Suspended paying companies take one each — not Anywhere IT, a pilot, the free version, a Closed one or a stale Pending one",
            state.Companies == 3 && !state.Full && state.Left == 2, $"{state.Companies} taken, {state.Left} left");
    }

    // ---- open -----------------------------------------------------------------------------------------
    Head("places left: signing up goes through");
    var plan = await Get(Browser(), "/signup?region=ZA");
    Check("the full version's card says companies are let in a few at a time", plan.Contains("5 companies at a time") && !plan.Contains("id=\"planFull\""));
    var first = await SignUp("Fourth IT", "pat@fourth.test");
    Check("a full-version signup is made", first.Contains("id=\"signupDone\""), Regex.Match(first, "id=\"signupError\">([^<]+)").Groups[1].Value);
    var fifth = await SignUp("Fifth IT", "pat@fifth.test");
    Check("...and another, into the last place", fifth.Contains("id=\"signupDone\""));
    await using (var db = new PlatformDb(options))
    {
        var state = await OpenPlaces.StateAsync(db);
        Check("a signup still waiting (Pending, fresh) holds its place: 5 of 5, full", state.Companies == 5 && state.Full, $"{state.Companies}");
    }

    // ---- full -----------------------------------------------------------------------------------------
    Head("every place taken: the waiting list");
    var fullPlan = await Get(Browser(), "/signup?region=ZA");
    Check("the full version's card says every place is taken", fullPlan.Contains("id=\"planFull\"") && fullPlan.Contains("All places taken"));
    var b1 = Browser();
    var fullPage = await Get(b1, "/signup?region=ZA&plan=full");
    Check("choosing the full version shows the waiting list, not the terms", fullPage.Contains("id=\"placesFull\"") && !fullPage.Contains("id=\"termsText\"")
        && fullPage.Contains("We let in 5 companies at a time") && fullPage.Contains("more are on request"));
    var forged = await Post(b1, "/signup?handler=Create", fullPage, Company("Sixth IT", "pat@sixth.test"));
    Check("a form posted straight to Create makes nothing, and shows the waiting list", await Companies() == 10 && forged.Contains("id=\"placesFull\""), (await Companies()).ToString());
    // The page turns a full signup away before the service is asked, so the SERVICE's own refusal is asked directly here —
    // without this, removing it from SignupAsync left every check above passing.
    var direct = new TenantService(new Factory(options), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), data,
        Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("placescheck"));
    var (madeDirect, directErr) = await direct.SignupAsync(new TenantService.SignupRequest("ZA", null, "Direct IT", "Dee Direct", "+27 82 555 0102",
        "dee@direct.test", Pw, null, null, true, terms.Sha256, "127.0.0.1"));
    Check("...and the service itself refuses it too, when asked directly", madeDirect == null && OpenPlaces.IsFullRefusal(directErr) && await Companies() == 10, directErr);
    var freePage = await Get(Browser(), "/signup?region=ZA&plan=free");
    Check("the free version is not counted and never waits", freePage.Contains("id=\"termsText\""));
    var joined = await Post(b1, "/signup?handler=Wait", fullPage, ("Region", "ZA"), ("Plan", "full"), ("CompanyName", "Sixth IT"), ("ContactName", "Pat Sixth"),
        ("Email", "pat@sixth.test"), ("ContactNumber", ""));
    int entryId;
    await using (var db = new PlatformDb(options))
    {
        var e = await db.WaitingList.SingleAsync();
        entryId = e.Id;
        Check("joining the waiting list keeps them, marked as waiting for a place", e.Full && e.Region == "ZA" && e.Email == "pat@sixth.test" && joined.Contains("as soon as a place opens"));
    }

    // ---- offered --------------------------------------------------------------------------------------
    Head("Owner → Approvals: offering a place");
    var admin = Browser();
    var loginForm = await Get(admin, "/owner/login");
    var keyStep = await Post(admin, "/owner/login", loginForm, ("Key", AdminKey));
    var keySecret = Regex.Match(keyStep, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
    string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    await Post(admin, "/owner/login", keyStep, ("UseKey", "true"), ("Pending", Field(keyStep, "Pending")), ("Code", RemoteSupport.Shared.Totp.Now(keySecret)));
    var approvals = await Get(admin, "/owner/approvals");
    Check("Approvals shows 5 of 5 places taken, and Full", approvals.Contains("id=\"openPlaces\"") && Regex.IsMatch(approvals, "id=\"placesTaken\">5</strong> of 5") && approvals.Contains(">Full<"));
    Check("...and the entry waiting for a place, with Offer a place", approvals.Contains($"data-waiting=\"{entryId}\"") && approvals.Contains("Offer a place"));
    var serverId = Regex.Match(approvals, "data-places-server=\"(\\d+)\"").Groups[1].Value;
    var offered = await Post(admin, "/owner/approvals?handler=Act", approvals, ("server", serverId), ("id", entryId.ToString()), ("act", "offer-place"));
    var link = Regex.Match(offered, @"(http://127\.0\.0\.1:\d+/signup\?region=ZA&plan=full&place=([0-9A-F]+))");
    var placeToken = link.Groups[2].Value;
    Check("a place is offered — the mail cannot go here, so the link is shown once to send by hand", offered.Contains("Place offered") && placeToken.Length >= 32,
        Regex.Match(offered, "alert-ok\">([^<]+)").Groups[1].Value.Trim());
    await using (var db = new PlatformDb(options))
    {
        var e = await db.WaitingList.SingleAsync();
        Check("...kept as a fingerprint, held for seven days", e.PlaceTokenHash == OpenPlaces.Hash(placeToken) && !e.PlaceTokenHash.Contains(placeToken)
            && e.PlaceExpiresUtc is { } x && Math.Abs((x - DateTime.UtcNow.AddDays(OpenPlaces.OfferDays)).TotalMinutes) < 5);
        Check("...and the link never written to the admin log", !await db.AdminLog.AnyAsync(a => a.What.Contains(placeToken)));
        Check("the offer holds a place of its own, so the public cannot take it", (await OpenPlaces.StateAsync(db)) is { Offers: 1, Full: true });
    }
    // Three at once on the one link: one company.
    var racers = await Task.WhenAll(Enumerable.Range(0, 3).Select(n => SignUp($"Sixth IT {n}", $"racer{n}@sixth.test", placeToken)));
    Check("the link signs the company up though every place is taken — three at once on it make ONE company",
        racers.Count(r => r.Contains("id=\"signupDone\"")) == 1 && await Companies() == 11, $"{racers.Count(r => r.Contains("id=\"signupDone\""))} done, {await Companies()} companies");
    await using (var db = new PlatformDb(options))
    {
        var e = await db.WaitingList.SingleAsync();
        Check("...the place is spent, naming the company it made", e.PlaceUsedUtc != null && e.TenantId is int tid && await db.Tenants.AnyAsync(t => t.Id == tid && t.CompanyName.StartsWith("Sixth IT")));
        Check("...and holds no place as an offer any more", (await OpenPlaces.StateAsync(db)) is { Offers: 0 });
    }
    var again = await SignUp("Seventh IT", "pat@seventh.test", placeToken);
    Check("the same link again: the waiting list, saying it has been used", again.Contains("id=\"placesFull\"") && again.Contains("already been used") && await Companies() == 11);
    await using (var db = new PlatformDb(options))
    {
        db.WaitingList.Add(new WaitingListEntry { Region = "ZA", CompanyName = "Late IT", ContactName = "Lee", Email = "lee@late.test", Full = true,
            PlaceTokenHash = OpenPlaces.Hash("ABCDEF0123456789ABCDEF0123456789"), PlaceOfferedUtc = DateTime.UtcNow.AddDays(-8), PlaceExpiresUtc = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
    }
    var late = await Get(Browser(), "/signup?region=ZA&plan=full&place=ABCDEF0123456789ABCDEF0123456789");
    Check("an offer that ran out says so", late.Contains("id=\"placeProblem\"") && late.Contains("has run out"));

    // ---- the number -----------------------------------------------------------------------------------
    Head("the number of places");
    approvals = await Get(admin, "/owner/approvals");
    var set = await Post(admin, "/owner/approvals?handler=Act", approvals, ("server", serverId), ("id", "0"), ("act", "places"), ("places", "0"));
    Check("set to 0 on Owner → Approvals: no limit", set.Contains("No limit on places") && set.Contains("unlimited places taken"));
    var open = await SignUp("Eighth IT", "pat@eighth.test");
    Check("...and signup is open again", open.Contains("id=\"signupDone\"") && await Companies() == 12);
    approvals = await Get(admin, "/owner/approvals");
    await Post(admin, "/owner/approvals?handler=Act", approvals, ("server", serverId), ("id", "0"), ("act", "places"), ("places", "20"));
    await using (var db = new PlatformDb(options))
        Check("set to 20: twenty places, and the change logged", (await OpenPlaces.StateAsync(db)).Limit == 20 && await db.AdminLog.AnyAsync(a => a.What == "Open places no limit → 20"));
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    try { server?.Kill(true); } catch { }
    await Task.Delay(500);
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} passed");
return fail == 0 ? 0 : 1;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
