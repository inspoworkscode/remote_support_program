// =====================================================================================
// signupcheck — country first, the terms accepted, then the company (2026-10-05, HANDOVER §84).
//
// Clint: "when signing up, the system must first ask what country they are from with the 5 options
// we currently have. this will determine pricing and also add that tenant to the relevant server …
// it must also have a country relevant disclaimer … which the client must acknowledge before they
// can set up their tenant." His answers: a waiting list for a region with no server yet; Claude
// drafts the terms, his lawyer reviews — and since 2026-10-06 none says DRAFT ("assume they are correct as is").
//
//   THE REGIONS   five, each with its own price; a region with a server elsewhere is sent there;
//                 one with none gets a waiting list (the same email twice is one entry).
//   THE TERMS     each region's text whole (no placeholder left), its own law, versioned, none a draft.
//   FREE          (2026-10-06) full or free asked after the region; the free terms (free section ahead of
//                 section 1), no company name or phone needed, no colours, one owner-technician, Active on approval.
//   THE GATE      no account without the box ticked, with the very text shown (fingerprint), in a
//                 region this server holds — a doctored form gains nothing; the details form posts
//                 to its own handler (it once looped back to the terms step for ever).
//   THE RECORD    the company in its region, and the terms it accepted kept WORD FOR WORD with who,
//                 when and from where — shown on its Billing page once approved.
//   EUROPE        in-process with a server configured for Europe: a European country is required.
//
//   dotnet run -c Release --project tools/signupcheck/signupcheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// <summary>One region's card on the first step: from its id to the end of its link.</summary>
string Card(string html, string code) { int i = html.IndexOf($"id=\"region-{code}\"", StringComparison.Ordinal); return i < 0 ? "" : html[i..html.IndexOf("</a>", i, StringComparison.Ordinal)]; }

Console.WriteLine("signupcheck — country first, the terms accepted, then the company");
var dir = Path.Combine(Path.GetTempPath(), "signupcheck-" + Guid.NewGuid().ToString("N")[..8]);
var data = Path.Combine(dir, "data"); Directory.CreateDirectory(data);
var d = new DirectoryInfo(AppContext.BaseDirectory);
while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
var proj = Path.Combine(d!.FullName, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
var baseUrl = $"http://127.0.0.1:{port}";
const string Pw = "SignupCheck!2026", UsServer = "https://us.example.test";

ProcessStartInfo Psi(params string[] args)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "--no-build", "-c", "Debug", "--project", proj, "--" }.Concat(args)) psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = data;
    HarnessDb.Use(psi.Environment, data);
    psi.Environment["RSP_Platform__AdminKey"] = "signupcheck-admin-key-0123456789abcdef0123456789abcdef";
    psi.Environment["RSP_Platform__SignupsEnabled"] = "true";
    psi.Environment["RSP_Platform__Region"] = "ZA";
    psi.Environment["RSP_Platform__RegionServers__US"] = UsServer;
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
    psi.Environment["RSP_Urls"] = baseUrl; psi.Environment["ASPNETCORE_URLS"] = baseUrl;
    return psi;
}
async Task<string> Cli(params string[] args) { var p = Process.Start(Psi(args))!; var o = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); return o; }

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

    var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    async Task<(string path, string html)> Get(string path) { var r = await http.GetAsync(path); return (r.RequestMessage!.RequestUri!.PathAndQuery, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync())); }
    async Task<string> Post(string handler, string fromHtml, params (string k, string v)[] f)
    {
        var token = Regex.Match(fromHtml, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        var all = f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", token));
        var r = await http.PostAsync($"/signup?handler={handler}", new FormUrlEncodedContent(all));
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }
    async Task<int> Tenants() { await using var db = new PlatformDb(options); return await db.Tenants.CountAsync(); }

    // ---- the regions ---------------------------------------------------------------------------
    Head("step one: where is your company?");
    var (_, first) = await Get("/signup");
    Check("five regions, each a link", Pricing.Regions.All(r => first.Contains($"id=\"region-{r.Code}\"")));
    Check("each with its own price: R500, $49, $49, £49, €49",
        first.Contains("R500") && first.Contains("$49") && first.Contains("£49") && first.Contains("€49") && first.Contains("Any mix of roles") && !first.Contains("for each extra seat"));
    Check("the regions with no server say so; the one with a server elsewhere does not",
        Regex.Matches(first, "Coming soon").Count == 3 && !Card(first, "US").Contains("Coming soon") && Card(first, "UK").Contains("Coming soon"));
    Check("an unknown region is the first step again", (await Get("/signup?region=XX")).html.Contains("id=\"region-ZA\""));

    var (_, us) = await Get("/signup?region=US");
    Check("the United States goes to its own server", us.Contains($"{UsServer}/signup?region=US") && us.Contains("sign up on our United States server"));
    Check("...and no terms or form are shown here", !us.Contains("id=\"termsText\"") && !us.Contains("id=\"createAccount\""));

    var (_, uk) = await Get("/signup?region=UK");
    Check("the United Kingdom, with no server yet, is a waiting list", uk.Contains("We are not open in the United Kingdom yet") && uk.Contains("id=\"joinWaiting\""));
    var waited = await Post("Wait", uk, ("Region", "UK"), ("CompanyName", "Thames IT"), ("ContactName", "Amy Hall"), ("Email", "Amy@Thames.test"), ("ContactNumber", ""));
    Check("joining it says when they will hear", waited.Contains("we will email Amy@Thames.test as soon as we open in the United Kingdom"));
    await Post("Wait", uk, ("Region", "UK"), ("CompanyName", "Thames IT Ltd"), ("ContactName", "Amy Hall"), ("Email", "amy@thames.test"), ("ContactNumber", "020 7946 0000"));
    await using (var db = new PlatformDb(options))
    {
        var w = await db.WaitingList.ToListAsync();
        Check("the same email twice is ONE entry, kept up to date", w.Count == 1 && w[0].CompanyName == "Thames IT Ltd" && w[0].Region == "UK" && w[0].Country == "United Kingdom",
            string.Join("; ", w.Select(x => $"{x.Region} {x.CompanyName} {x.Email}")));
    }
    Check("...and nothing was made for a region this server does not hold", await Tenants() == 0);

    // ---- the terms -----------------------------------------------------------------------------
    Head("step two: the terms");
    var (_, zaPlan) = await Get("/signup?region=ZA");
    Check("South Africa first asks: the full version or the free one (2026-10-06)",
        zaPlan.Contains("id=\"plan-full\"") && zaPlan.Contains("id=\"plan-free\"") && !zaPlan.Contains("id=\"termsText\""));
    var (_, za) = await Get("/signup?region=ZA&plan=full");
    var zaTerms = Terms.For("ZA");
    Check("South Africa shows its terms — final since 2026-10-06, no DRAFT notice — with the version", za.Contains("id=\"termsText\"") && !za.Contains("id=\"termsDraft\"") && za.Contains($"id=\"termsVersion\">Version {Terms.Version}"));
    Check("...under POPIA, as operator and responsible party", za.Contains("Protection of Personal Information Act 4 of 2013") && za.Contains("you are the responsible party and we are your operator"));
    Check("...saying we are not responsible for loss of data, in bold", za.Contains("<strong>We are not responsible for any loss of, damage to, or corruption of data</strong>"));
    Check("...with the South African price, and no VAT", za.Contains("R500 a month") && za.Contains("No VAT is charged"));
    Check("...and no form for the company yet", !za.Contains("id=\"createAccount\""));
    foreach (var (code, law) in new[] { ("UK", "UK General Data Protection Regulation"), ("EU", "Article 28 of the GDPR"), ("US", "California Consumer Privacy Act"), ("CA", "PIPEDA") })
    {
        var t = Terms.For(code);
        Check($"{Pricing.For(code).Name}: its own law, its own money, governed by, nothing left unfilled",
            t.Source.Contains(law) && t.Source.Contains(Pricing.Money(Pricing.For(code), Pricing.For(code).Package)) && t.Source.Contains("governed by") && !t.Source.Contains("{{"));
    }
    var (_, ukPage) = await Get("/terms/UK");
    Check("anybody can read a region's terms at /terms — and no region says DRAFT any more (Clint, 2026-10-06)", ukPage.Contains("Information Commissioner") && !ukPage.Contains("DRAFT"));

    var noTick = await Post("Terms", za, ("Region", "ZA"), ("Plan", "full"), ("TermsSha", zaTerms.Sha256));
    Check("Continue without the box ticked is refused, with the terms again", noTick.Contains("tick the box to accept them") && noTick.Contains("id=\"termsText\"") && !noTick.Contains("id=\"createAccount\""));
    var stale = await Post("Terms", za, ("Region", "ZA"), ("Plan", "full"), ("TermsSha", "0000"), ("Accept", "true"));
    Check("ticked, but for terms that have since changed: read them again", stale.Contains("Our terms were updated") && !stale.Contains("id=\"createAccount\""));
    var details = await Post("Terms", za, ("Region", "ZA"), ("Plan", "full"), ("TermsSha", zaTerms.Sha256), ("Accept", "true"));
    Check("ticked: the form for the company", details.Contains("id=\"createAccount\"") && details.Contains("You accepted our") && details.Contains("action=\"/Signup?handler=Create\""));

    // ---- the gate ------------------------------------------------------------------------------
    Head("step three: nothing is made without the terms");
    (string k, string v)[] Company(string region, string sha, string accept, string email = "thandi@kgosi.test") => new[]
    {
        ("Region", region), ("Plan", "full"), ("TermsSha", sha), ("Accept", accept), ("CompanyName", "Kgosi Computers"), ("ContactName", "Thandi Kgosi"),
        ("ContactNumber", "+27 82 555 0101"), ("Email", email), ("Password", Pw), ("Primary", "#1F4E79"), ("Accent", "#2E9BFF")
    };
    var unticked = await Post("Create", details, Company("ZA", zaTerms.Sha256, "false"));
    Check("a form posted without the acceptance makes nothing, and shows the terms", await Tenants() == 0 && unticked.Contains("id=\"termsText\""));
    var oldTerms = await Post("Create", details, Company("ZA", "DEADBEEF", "true"));
    Check("...nor with the fingerprint of other terms", await Tenants() == 0 && oldTerms.Contains("Our terms were updated"));
    var wrongPlace = await Post("Create", details, Company("UK", Terms.For("UK").Sha256, "true"));
    Check("...nor for a region this server does not hold", await Tenants() == 0 && !wrongPlace.Contains("id=\"signupDone\""));
    var done = await Post("Create", details, Company("ZA", zaTerms.Sha256, "true"));
    Check("with all three, the company is made", done.Contains("id=\"signupDone\"") && done.Contains("Thanks, Kgosi Computers!"));
    var again = await Post("Create", details, Company("ZA", zaTerms.Sha256, "true"));
    Check("the same email again is refused, and nothing half-made", again.Contains("already registered") && await Tenants() == 1);

    // ---- the record ----------------------------------------------------------------------------
    Head("what was kept");
    int tenantId;
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.Include(x => x.Technicians).SingleAsync();
        tenantId = t.Id;
        Check("the company is in South Africa, billed in rand, awaiting approval",
            t.Region == "ZA" && t.Country == "South Africa" && t.Status == TenantStatus.Pending && t.Seats == 5, $"{t.Region} {t.Country} {t.Status} {t.Seats}");
        Check("its owner is the person who signed up", t.Technicians.Single().Email == "thandi@kgosi.test" && t.Technicians.Single().IsOwner);
        var a = await db.TermsAcceptances.SingleAsync();
        Check("the terms accepted are kept WORD FOR WORD, with their fingerprint", a.TenantId == t.Id && a.Text == zaTerms.Source && a.Sha256 == zaTerms.Sha256);
        Check("...the version, and that it was final, not a draft", a.Version == Terms.Version && !a.Draft && a.Region == "ZA");
        Check("...who accepted, and from where", a.AcceptedByName == "Thandi Kgosi" && a.AcceptedByEmail == "thandi@kgosi.test" && a.FromAddress.Length > 0, a.FromAddress);
    }
    var approved = await Cli("approve", "kgosi-computers");
    Check("the platform owner approves it", approved.Contains("Approved"), approved.Trim());
    var owner = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(baseUrl) };
    var login = await owner.GetStringAsync("/login");
    await owner.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["Email"] = "thandi@kgosi.test", ["Password"] = Pw,
        ["__RequestVerificationToken"] = Regex.Match(login, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value
    }));
    var billing = WebUtility.HtmlDecode(await owner.GetStringAsync("/portal/billing"));
    Check("once approved, the owner's Billing page shows the terms they accepted",
        billing.Contains("id=\"termsAccepted\"") && billing.Contains("by Thandi Kgosi") && !billing.Contains("(draft)") && billing.Contains("Protection of Personal Information Act"));

    // ---- the free version (2026-10-06, NEXT-SESSION-FREE.md Stage C) --------------------------------
    Head("the free version: its own terms, fewer questions, one person");
    var (_, freeTermsPage) = await Get("/signup?region=ZA&plan=basic");
    var freeTerms = Terms.For("ZA", "basic");
    Check("the Basic version (once free, 2026-10-25) shows ITS terms: its section ahead of section 1, R50, three computers, not a draft",
        freeTermsPage.Contains("id=\"termsText\"") && freeTermsPage.Contains("Your own computers.") && freeTermsPage.Contains("R10 a month for each one more, up to 20 computers in all") && freeTermsPage.Contains("R50 a month")
        && !freeTermsPage.Contains("id=\"termsDraft\"") && freeTerms.Source.IndexOf("# The Basic Version for Remote Workers", StringComparison.Ordinal) < freeTerms.Source.IndexOf("# 1. What the service is", StringComparison.Ordinal));
    Check("...and they are not the full version's", freeTerms.Sha256 != zaTerms.Sha256);
    var crossed = await Post("Terms", freeTermsPage, ("Region", "ZA"), ("Plan", "basic"), ("TermsSha", zaTerms.Sha256), ("Accept", "true"));
    Check("ticking the FULL terms does not get you the free form", crossed.Contains("Our terms were updated") && !crossed.Contains("id=\"createAccount\""));
    var freeDetails = await Post("Terms", freeTermsPage, ("Region", "ZA"), ("Plan", "basic"), ("TermsSha", freeTerms.Sha256), ("Accept", "true"));
    Check("the free form: company name optional, no colours asked",
        freeDetails.Contains("id=\"createAccount\"") && freeDetails.Contains("id=\"freeCompany\"") && !freeDetails.Contains("name=\"Primary\"") && !freeDetails.Contains("name=\"Accent\""));
    int before = await Tenants();
    var freeDone = await Post("Create", freeDetails, ("Region", "ZA"), ("Plan", "basic"), ("TermsSha", freeTerms.Sha256), ("Accept", "true"),
        ("CompanyName", ""), ("ContactName", "Lindiwe Dube"), ("ContactNumber", ""), ("Email", "lindiwe@branches.test"), ("Password", Pw),
        ("Primary", "#FF0000"), ("Accent", "#00FF00"));
    Check("made with no company name and no phone", freeDone.Contains("id=\"signupDone\"") && freeDone.Contains("Basic version") && await Tenants() == before + 1,
        Regex.Match(freeDone, "id=\"signupError\">([^<]*)").Groups[1].Value);
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.Include(x => x.Technicians).SingleAsync(x => x.Slug == "lindiwe-dube");
        Check("it is on the free plan, named after the person", RemoteSupport.Shared.FreePlan.Is(t.Plan) && t.CompanyName == "Lindiwe Dube" && t.Status == TenantStatus.Pending,
            $"{t.Plan} {t.CompanyName} {t.Status}");
        Check("our colours, whatever a doctored form sent", t.PrimaryColor == new Tenant().PrimaryColor && t.AccentColor == new Tenant().AccentColor, $"{t.PrimaryColor} {t.AccentColor}");
        var p = t.Technicians.Single();
        Check("its one person is the owner AND the technician", p.Role == (TenantRole.Owner | TenantRole.Technician), p.Role.ToString());
        var a = await db.TermsAcceptances.SingleAsync(x => x.TenantId == t.Id);
        Check("the FREE terms are kept word for word", a.Text == freeTerms.Source && a.Sha256 == freeTerms.Sha256 && !a.Draft);
    }
    var freeApproved = await Cli("approve", "lindiwe-dube");
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.SingleAsync(x => x.Slug == "lindiwe-dube");
        Check("approved by hand (no card payment here), a Basic company starts its 14-day TRIAL — R50 a month after it, no longer free", t.Status == TenantStatus.Trial && t.TrialEndsUtc is { } te && Math.Abs((te - DateTime.UtcNow.AddDays(14)).TotalHours) < 1,
            $"{t.Status} {t.TrialEndsUtc} {freeApproved.Trim()}");
    }
    var (_, freePublic) = await Get("/terms/ZA?plan=basic");
    Check("the Basic terms can be read at /terms/ZA?plan=basic", freePublic.Contains("Your own computers.") && freePublic.Contains("id=\"freeTerms\""));

    // ---- Europe, in-process: a server that holds the EU ------------------------------------------
    Head("Europe: a country is required");
    var euDir = Path.Combine(dir, "eu"); Directory.CreateDirectory(euDir);
    var euOptions = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(euDir, "eu.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(euOptions)) await HarnessDb.MigrateAsync(db);
    var euCfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:Region"] = "EU" }).Build();
    var euTenants = new TenantService(new Factory(euOptions), euCfg, euDir, DataProtectionProvider.Create("signupcheck"));
    var euSha = Terms.For("EU").Sha256;
    TenantService.SignupRequest Eu(string? country, string region = "EU") =>
        new(region, country, "Berlin Bits", "Jonas Weber", "+49 30 000000", $"jonas{country}@bits.test", Pw, null, null, true, euSha, "203.0.113.9", "Europe/Amsterdam");   // Europe chooses its zone (§85)
    var (noCountry, e1) = await euTenants.SignupAsync(Eu(""));
    Check("Europe with no country is refused", noCountry == null && e1.Contains("choose your country"), e1);
    var (notEurope, e2) = await euTenants.SignupAsync(Eu("Kenya"));
    Check("...and with a country outside Europe", notEurope == null, e2);
    var (berlin, e3) = await euTenants.SignupAsync(Eu("Germany"));
    Check("Germany is accepted, billed in euros", berlin?.Region == "EU" && berlin?.Country == "Germany", e3);
    var (zaHere, e4) = await euTenants.SignupAsync(Eu(null, "ZA") with { TermsSha256 = Terms.For("ZA").Sha256, Email = "za@bits.test" });
    Check("a server for Europe will not take a South African company", zaHere == null && e4.Contains("not open in South Africa"), e4);
}
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    try { await Task.Delay(300); Directory.Delete(dir, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

file sealed class Factory : IDbContextFactory<PlatformDb>
{
    private readonly DbContextOptions<PlatformDb> _o;
    public Factory(DbContextOptions<PlatformDb> o) => _o = o;
    public PlatformDb CreateDbContext() => new(_o);
}
