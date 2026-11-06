// =====================================================================================
// basiccheck — THE FREE VERSION AND THE BASIC VERSION FOR REMOTE WORKERS, side by side (2026-10-25, HANDOVER §161–§163).
//
// The morning of 2026-10-25 the free version became the Basic Version for Remote Workers (R50 a month, three computers, §161),
// with more computers at R10 each up to 20 (§162). The evening, Clint: "I think that we should have a free version as well. The
// free version should only allow one connection for unattended access with password as it was previously. The basic version we
// should change to allow attended and unattended, but still only a maximum of 20 machines." And: "will the free then have the
// option to upgrade to basic and full, and does basic have the option to upgrade to full and downgrade to free?" — yes, all of it.
//
//   THE RULES     The Free version: nothing to pay, no card, ONE computer, its own terms (one computer, no fees). The Basic version:
//                 R50 / $5 / £5 / €5, 3 computers and R10 each more up to 20, by card, its terms say attended as well and how to
//                 move down. Upgrades: Free → Basic (R0 → R50) or Full; Basic → Full. The words and emails by plan.
//   THE MIGRATION (SQLite) companies stored "free" before §163: the old free accounts stay Free; one given a trial, extra computers
//                 or a card is Basic.
//   THE PAGES     Owner → Free and Basic makes either (Free active, Basic on a trial), no Give notice any more. The Basic owner's
//                 Billing and its computers. Moving down: refused with two computers installed, done with one — the Free plan, no
//                 trial, its terms kept, the change on Billing. Then the Free owner's Upgrade asks which; to Basic (R0 → R50, a
//                 14-day trial), then to the full version (R50 → R500, the trial carried). With every place taken, the full version
//                 waits and neither one-person plan does; the signup offers all three.
//
//   dotnet run -c Release --project tools/basiccheck/basiccheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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

Console.WriteLine($"basiccheck — the Free version and the Basic Version for Remote Workers ({HarnessDb.Name})");

// ---- the rules ------------------------------------------------------------------------------------
Head("the rules: the Basic version");
Tenant Basic(string region = "ZA") => new() { Region = region, Plan = FreePlan.BasicName, TimeZone = CompanyClock.SouthAfrica };
Tenant Free(string region = "ZA") => new() { Region = region, Plan = FreePlan.Name, TimeZone = CompanyClock.SouthAfrica };
var one = new[] { new Technician { Id = 1, Role = TenantService.FreeRole } };
var zaBill = Pricing.BillFor(Basic(), 1);
Check("the price: R50 a month, one person, nothing extra, marked Basic", zaBill.Total == "R50" && zaBill.Basic && !zaBill.FreeVersion && zaBill.Extra == 0 && zaBill.Included == 1, zaBill.Sum);
Check("...$5 in the US and Canada, £5, €5", Pricing.BillFor(Basic("US"), 1).Total == "$5" && Pricing.BillFor(Basic("CA"), 1).Total == "$5"
    && Pricing.BillFor(Basic("UK"), 1).Total == "£5" && Pricing.BillFor(Basic("EU"), 1).Total == "€5");
Check("...and it is R50 however its people are counted", Pricing.BillFor(Basic(), 4).Total == "R50");
Check("PayFast is asked for 5000 cents", CompanyBilling.OwedCents(Basic(), one) == 5000);
Check("the bill's sum names it, with one person and three computers", zaBill.Sum == "R50 a month — the Basic version, one person and up to 3 computers", zaBill.Sum);
Tenant Paying(int machines, string region = "ZA") { var t = Basic(region); t.BasicMachines = machines; return t; }
var five = Pricing.BillFor(Paying(5), 1);
Check("paying for 5: R50 + 2 × R10 = R70, written out", five.Total == "R70" && five.ExtraMachines == 2 && five.Machines == 5
    && five.Sum == "R50 for the Basic version + 2 extra computers × R10 = R20 → R70 a month — one person, 5 computers", five.Sum);
Check("...$1 / £1 / €1 elsewhere: 5 in the US is $7, 20 in the UK £22", Pricing.BillFor(Paying(5, "US"), 1).Total == "$7" && Pricing.BillFor(Paying(20, "UK"), 1).Total == "£22");
Check("...and PayFast is asked for 7000 cents", CompanyBilling.OwedCents(Paying(5), one) == 7000);
Check("the number is held between the three in the price and 20", FreePlan.MachinesFor(FreePlan.BasicName, 0) == 3 && FreePlan.MachinesFor(FreePlan.BasicName, 2) == 3
    && FreePlan.MachinesFor(FreePlan.BasicName, 7) == 7 && FreePlan.MachinesFor(FreePlan.BasicName, 25) == 20 && Pricing.BillFor(Paying(99), 1).Total == "R220");
var rise5 = TenantService.BasicMachinesRise(Basic(), 5);
Check("going from 3 to 5 is a rise to agree, R50 → R70; going down is none", rise5 is { } rr && rr.Before.Total == "R50" && rr.After.Total == "R70" && rr.Token == "ZA:1:70"
    && TenantService.BasicMachinesRise(Paying(5), 4) == null, rise5?.Token ?? "null");
Check("the refusal at the limit says how to have more — until 20, where it does not",
    FreePlan.TooManyFor(FreePlan.BasicName, 5).Contains("allows 5 computers") && FreePlan.TooManyFor(FreePlan.BasicName, 5).Contains("Add a computer on Billing")
    && !FreePlan.TooManyFor(FreePlan.BasicName, 20).Contains("Add a computer") && FreePlan.TooManyFor(FreePlan.BasicName, 20).Contains("at most"));
Check("attended on the Basic version, not the Free one; both are one-person plans", FreePlan.Attended(FreePlan.BasicName) && !FreePlan.Attended(FreePlan.Name)
    && FreePlan.Is(FreePlan.BasicName) && FreePlan.Is(FreePlan.Name) && !FreePlan.Is("five-user") && !FreePlan.IsFree(FreePlan.BasicName) && !FreePlan.IsBasic(FreePlan.Name));
Check("its names", FreePlan.FullNameOf(FreePlan.BasicName) == "The Basic Version for Remote Workers" && FreePlan.ShortNameOf(FreePlan.BasicName) == "Basic");
var acct = new PayfastService.Account("10000100", "key", "pass", true);
Check("it pays by card", CompanyBilling.Refusal(Basic(), acct) == null, CompanyBilling.Refusal(Basic(), acct) ?? "");
Check("...but, like the full version, only in rand", CompanyBilling.Refusal(Basic("UK"), acct) is { } uk && uk.Contains("rand"));
var terms = Terms.For("ZA", FreePlan.BasicName);
Check("its terms: R50, 3 computers and R10 each up to 20, \"allowed you to reach them\", attended as well, moving down, nothing left unfilled",
    terms.Source.Contains("# The Basic Version for Remote Workers") && terms.Source.Contains("**R50 a month**") && terms.Source.Contains("**3 computers**")
    && terms.Source.Contains("R10 a month for each one more, up to 20 computers in all") && terms.Source.Contains("allowed you to reach them")
    && terms.Source.Contains("**attended**, where the person at the computer is asked to allow each connection") && terms.Source.Contains("move down to the Free version")
    && !terms.Source.Contains("{{") && !terms.Source.Contains("No fees"));
Check("...and they differ from the full version's", terms.Sha256 != Terms.For("ZA").Sha256);
var quote = TenantService.UpgradeQuote(Basic(), one);
Check("upgrading is R50 → R500", quote.Before.Total == "R50" && quote.After.Total == "R500" && quote.Token == "ZA:1:500", $"{quote.Before.Total} → {quote.After.Total}");
var now = DateTime.UtcNow;
var onTrial = Basic(); onTrial.Status = TenantStatus.Trial; onTrial.TrialEndsUtc = now.AddDays(5);
var running = Basic(); running.Status = TenantStatus.Active;
Check("...from the next card payment when it pays by card (no trial for a paying customer)", TenantService.UpgradeTrialEnd(running, true, now) == null);
Check("...the trial it is on, when it is on one", TenantService.UpgradeTrialEnd(onTrial, false, now) == onTrial.TrialEndsUtc);
Check("...else 14 days", TenantService.UpgradeTrialEnd(running, false, now) is { } e && Math.Abs((e - now.AddDays(14)).TotalMinutes) < 1);
var approved = AdminCompanies.Approved(Basic(), "https://x.test");
Check("approved by hand: its 14-day trial of the Basic version", approved.body.Contains("14-day free trial of the Basic version") && !approved.body.Contains("free version"));
Check("where it may go: up to the full version", TenantService.UpgradesFrom(FreePlan.BasicName).SequenceEqual(new[] { "full" }));

Head("the rules: the Free version");
var freeBill = Pricing.BillFor(Free(), 1);
Check("nothing to pay: R0, one person, one computer, said in words", freeBill.Monthly == 0m && freeBill.FreeVersion && !freeBill.Basic && freeBill.Machines == 1
    && freeBill.Sum == "Nothing to pay — the Free version, one person and 1 computer", freeBill.Sum);
Check("PayFast is asked for nothing, and no card is taken", CompanyBilling.OwedCents(Free(), one) == 0 && CompanyBilling.Refusal(Free(), acct) == "The Free version has nothing to pay.");
Check("ONE computer, whatever was paid for", FreePlan.MachinesFor(FreePlan.Name, 0) == 1 && FreePlan.MachinesFor(FreePlan.Name, 9) == 1 && FreePlan.FreeMachines == 1);
Check("the refusal past it: the Free version, one computer, and upgrading", FreePlan.TooManyFor(FreePlan.Name, 1).Contains("The Free version allows 1 computer")
    && FreePlan.TooManyFor(FreePlan.Name, 1).Contains("upgrade") && !FreePlan.TooManyFor(FreePlan.Name, 1).Contains("Billing"));
Check("asked for anything but unattended with a password, it says so; asking for help is neither plan's",
    FreePlan.NoAttended.Contains("unattended only") && FreePlan.NoAskingFor(FreePlan.Name) == FreePlan.NoAttended && FreePlan.NoAskingFor(FreePlan.BasicName).Contains("asked to allow it"));
Check("its names", FreePlan.FullNameOf(FreePlan.Name) == "The Free Version" && FreePlan.ShortNameOf(FreePlan.Name) == "Free" && FreePlan.TheNameOf(FreePlan.Name) == "the Free version");
var freeTerms = Terms.For("ZA", FreePlan.Name);
Check("its terms: the Free version, ONE computer, no fees, upgrading to Basic or full, nothing left unfilled",
    freeTerms.Source.Contains("# The Free Version") && freeTerms.Source.Contains("**1 computer**") && freeTerms.Source.Contains("**No fees.**")
    && freeTerms.Source.Contains("upgrade to the Basic version or the full version") && !freeTerms.Source.Contains("{{")
    && freeTerms.Sha256 != terms.Sha256 && freeTerms.Sha256 != Terms.For("ZA").Sha256);
var toBasic = TenantService.UpgradeQuote(Free(), one, FreePlan.BasicName);
var toFull = TenantService.UpgradeQuote(Free(), one);
Check("upgrading: R0 → R50 to the Basic version, R0 → R500 to the full version", toBasic.Before.Total == "R0" && toBasic.After.Total == "R50" && toBasic.Token == "ZA:1:50"
    && toFull.After.Total == "R500", $"{toBasic.Before.Total} → {toBasic.After.Total} / {toFull.After.Total}");
Check("where it may go: up to Basic or full", TenantService.UpgradesFrom(FreePlan.Name).SequenceEqual(new[] { FreePlan.BasicName, "full" }) && TenantService.UpgradesFrom("five-user").Count == 0);
var approvedFree = AdminCompanies.Approved(Free(), "https://x.test");
Check("approved by hand: ready to use, no trial", approvedFree.body.Contains("the Free version is ready to use") && !approvedFree.body.Contains("trial"));
Check("both portals: Billing and Upgrade allowed, tickets not", FreePortal.Allowed("/portal/billing") && FreePortal.Allowed("/portal/upgrade") && !FreePortal.Allowed("/portal/tickets"));

// ---- the migration -----------------------------------------------------------------------------------
var dir = Path.Combine(Path.GetTempPath(), "basiccheck-" + Guid.NewGuid().ToString("N")[..8]);
var data = Path.Combine(dir, "data"); Directory.CreateDirectory(data);
Head("the migration: what was stored \"free\" this morning");
if (HarnessDb.SqliteUpgradePart("companies stored \"free\" before FreeVersionBack"))
{
    var mdir = Path.Combine(dir, "migration"); Directory.CreateDirectory(mdir);
    var mopts = new DbContextOptionsBuilder<PlatformDb>().UseSqlite($"Data Source={Path.Combine(mdir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(mopts))
    {
        await db.GetService<IMigrator>().MigrateAsync("20261025104153_BasicExtraMachines");
        // Written the way the product wrote them this morning: every one "free".
        foreach (var (id, slug) in new[] { (1, "old-free"), (2, "on-trial"), (3, "more-pcs"), (4, "with-card"), (5, "full-co") })
            await Seed.OldTenant(db, id, slug, slug, "k" + id);
        await db.Database.ExecuteSqlRawAsync("UPDATE Tenants SET Plan = 'free', Status = 0 WHERE Id IN (1,2,3,4)");
        await db.Database.ExecuteSqlRawAsync("UPDATE Tenants SET TrialEndsUtc = '2026-11-08 00:00:00' WHERE Id = 2");
        await db.Database.ExecuteSqlRawAsync("UPDATE Tenants SET BasicMachines = 5 WHERE Id = 3");
        await Insert(db, "PayfastSubscriptions", new() { ["TenantId"] = 4, ["Purpose"] = "signup", ["Status"] = 0, ["Reference"] = "ref-4" });
        await Insert(db, "PayfastSubscriptions", new() { ["TenantId"] = 1, ["Purpose"] = "test", ["Status"] = 0, ["Reference"] = "ref-1" });
        await db.GetService<IMigrator>().MigrateAsync();
    }
    await using (var db = new PlatformDb(mopts))
    {
        var plans = await db.Tenants.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Plan).ToListAsync();
        Check("the old free account stays Free — a test card at PayFast does not count", plans[0] == FreePlan.Name, plans[0]);
        Check("given a trial, extra computers or a real card: Basic", plans[1] == FreePlan.BasicName && plans[2] == FreePlan.BasicName && plans[3] == FreePlan.BasicName, string.Join(",", plans));
        Check("the full version is untouched", plans[4] == "five-user", plans[4]);
    }
}

// ---- the pages -------------------------------------------------------------------------------------
var d = new DirectoryInfo(AppContext.BaseDirectory);
while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
var proj = Path.Combine(d!.FullName, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
var baseUrl = $"http://127.0.0.1:{port}";
const string Pw = "BasicCheck!2026";
const string AdminKey = "basiccheck-admin-key-0123456789abcdef0123456789abcdef";

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
async Task<string> Cli(params string[] args) { var p = Process.Start(Psi(args))!; var o = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); return o; }

var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(data, "platform.db")};Pooling=False").Options;
Process? server = null;
try
{
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build."); return 1; }

    // A free account from before: the Free version again, running, its one person owner and technician.
    await Cli("create-tenant", "Karoo Old", "karoo-old");
    await Cli("set-brand", "karoo-old", "plan", "free");
    await Cli("set-brand", "karoo-old", "status", "Active");
    // A Basic company on its trial, whose owner is the one who goes down and back up again.
    await Cli("create-tenant", "Karoo Basic", "karoo-basic");
    await Cli("set-brand", "karoo-basic", "plan", "basic");
    await Cli("set-brand", "karoo-basic", "status", "Trial");
    await Cli("add-tech", "karoo-basic", "ouma@karoo.test", Pw, "Ouma Karoo", "--owner", "--technician");
    // A full-version company, running: with two places it fills one.
    await Cli("create-tenant", "Full IT", "full-it");
    await Cli("set-brand", "full-it", "status", "Active");
    int old, kb; var trialEnd = DateTime.UtcNow.AddDays(9);
    await using (var db = new PlatformDb(options))
    {
        old = (await db.Tenants.SingleAsync(x => x.Slug == "karoo-old")).Id;
        var t = await db.Tenants.SingleAsync(x => x.Slug == "karoo-basic");
        kb = t.Id; t.TrialEndsUtc = trialEnd; await db.SaveChangesAsync();
    }

    server = Process.Start(Psi("run"))!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    var until = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < until) { try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); break; } catch { await Task.Delay(400); } }

    HttpClient Browser() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    async Task<(HttpStatusCode code, string html, string? to)> Get(HttpClient h, string path)
    {
        var r = await h.GetAsync(path);
        return (r.StatusCode, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()), r.Headers.Location?.ToString());
    }
    string TokenIn(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    async Task<(HttpStatusCode code, string html, string? to)> Post(HttpClient h, string path, string fromHtml, params (string k, string v)[] f)
    {
        var all = f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", TokenIn(fromHtml)));
        var r = await h.PostAsync(path, new FormUrlEncodedContent(all));
        return (r.StatusCode, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()), r.Headers.Location?.ToString());
    }
    string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    string Alert(string html) => Regex.Match(html, "id=\"freeMessage\">([^<]*)").Groups[1].Value.Trim();
    string Said(string html, string id) => Regex.Match(html, $"id=\"{id}\">([^<]*)").Groups[1].Value.Trim();

    var admin = Browser();
    var (_, loginForm, _) = await Get(admin, "/owner/login");
    var keyStep = await Post(admin, "/owner/login", loginForm, ("Key", AdminKey));
    var keySecret = Regex.Match(keyStep.html, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
    await Post(admin, "/owner/login", keyStep.html, ("UseKey", "true"), ("Pending", Field(keyStep.html, "Pending")), ("Code", Totp.Now(keySecret)));

    // ---- Owner → Free and Basic ----------------------------------------------------------------------
    Head("Owner → Free and Basic");
    var (code, page, _) = await Get(admin, "/owner/free");
    Check("its heading, both prices, the choice of version, and no Give notice any more",
        code == HttpStatusCode.OK && page.Contains("<h1>Free and Basic</h1>") && page.Contains("nothing to pay") && page.Contains("R50 a month")
        && page.Contains("id=\"makePlan\"") && !page.Contains("noticeDue") && !page.Contains("handler=Notice"), code.ToString());
    Check("the old free account is listed as Free, the Basic one as Basic",
        Regex.IsMatch(page, $"data-plan=\"{old}\"><span class=\"pill pill-muted\">Free<") && Regex.IsMatch(page, $"data-plan=\"{kb}\"><span class=\"pill pill-muted\">Basic<"));
    var madeBasic = await Post(admin, "/owner/free?handler=Make", page, ("Name", "Lindi Remote"), ("Email", "lindi@remote.test"), ("Plan", "basic"));
    var madeFree = await Post(admin, "/owner/free?handler=Make", madeBasic.html, ("Name", "Thabo Home"), ("Email", "thabo@home.test"), ("Plan", "free"));
    await using (var db = new PlatformDb(options))
    {
        var b = await db.Tenants.AsNoTracking().SingleAsync(x => x.ContactName == "Lindi Remote");
        var f = await db.Tenants.AsNoTracking().SingleAsync(x => x.ContactName == "Thabo Home");
        Check("a Basic account: the Basic plan, a 14-day trial, said", b.Plan == FreePlan.BasicName && b.Status == TenantStatus.Trial
            && b.TrialEndsUtc is { } te && Math.Abs((te - DateTime.UtcNow.AddDays(14)).TotalHours) < 1 && Alert(madeBasic.html).Contains("Basic version, 14-day trial"), $"{b.Plan} {b.Status}");
        Check("a Free account: the Free plan, running, no trial, said", f.Plan == FreePlan.Name && f.Status == TenantStatus.Active && f.TrialEndsUtc == null
            && Alert(madeFree.html).Contains("Free version, active"), $"{f.Plan} {f.Status} {Alert(madeFree.html)}");
    }

    // ---- the Basic owner's portal ------------------------------------------------------------------
    Head("the Basic owner's portal");
    var ouma = Browser();
    var (_, signIn, _) = await Get(ouma, "/login");
    await Post(ouma, "/login", signIn, ("Email", "ouma@karoo.test"), ("Password", Pw));
    var (bcode, billing, _) = await Get(ouma, "/portal/billing");
    Check("Billing: R50 a month, the Basic Version for Remote Workers, its trial's end, no seats, the way down to the Free version",
        bcode == HttpStatusCode.OK && billing.Contains("id=\"monthlyTotal\">R50<") && billing.Contains("id=\"basicPlan\"") && billing.Contains("The Basic Version for Remote Workers")
        && billing.Contains("Your trial ends on") && !billing.Contains("id=\"moreSeats\"") && billing.Contains("id=\"moveDown\"") && billing.Contains("/portal/upgrade?to=free"),
        bcode.ToString());
    var (tcode, _, tto) = await Get(ouma, "/portal/tickets");
    Check("tickets are not the Basic version's", tcode is HttpStatusCode.Redirect or HttpStatusCode.Found && (tto ?? "").Contains("/portal/machines"), $"{tcode} {tto}");
    var asked = await Post(ouma, "/portal/billing?handler=Machines", billing, ("machines", "5"));
    Check("asking for 5 computers: Now R50, After R70, Accept", asked.html.Contains("id=\"machinesQuestion\"") && Field(asked.html, "agreed") == "ZA:1:70");
    var five5 = await Post(ouma, "/portal/billing?handler=Machines", asked.html, ("machines", "5"), ("agreed", Field(asked.html, "agreed")));
    await using (var db = new PlatformDb(options))
        Check("Accept: 5 paid for, R70", (await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == kb)).BasicMachines == 5 && five5.html.Contains("id=\"monthlyTotal\">R70<"));
    var (_, machinesPage, _) = await Get(ouma, "/portal/machines");
    Check("Your computers: reaches 5, attended or unattended", machinesPage.Contains("Your Basic version reaches 5 of your own computers") && machinesPage.Contains("or attended"));

    // ---- moving down -------------------------------------------------------------------------------------
    Head("the Basic version moves down to the Free version");
    await using (var db = new PlatformDb(options))
    {
        db.Devices.Add(new Device { TenantId = kb, DeviceId = "pc-office", MachineName = "OFFICE-PC", HardwareId = "hw-office" });
        db.Devices.Add(new Device { TenantId = kb, DeviceId = "pc-home", MachineName = "HOME-PC", HardwareId = "hw-home" });
        await db.SaveChangesAsync();
    }
    var (dcode, down, _) = await Get(ouma, "/portal/upgrade?to=free");
    Check("the page: Move to the Free version, R70 → nothing, its terms, two installed and the others to remove first",
        dcode == HttpStatusCode.OK && down.Contains("id=\"upgradeHeading\">Move to the Free version<") && down.Contains("Now: <strong>R70 a month</strong>")
        && down.Contains("After: <strong>nothing</strong>") && down.Contains("The Free Version") && down.Contains("you have 2 installed — remove the others first")
        && Field(down, "TermsSha") == Terms.For("ZA", FreePlan.Name).Sha256, dcode.ToString());
    var unticked = await Post(ouma, "/portal/upgrade?handler=Down", down, ("TermsSha", Field(down, "TermsSha")));
    Check("unticked: refused", Said(unticked.html, "upgradeError").Contains("tick the box"), Said(unticked.html, "upgradeError"));
    var two = await Post(ouma, "/portal/upgrade?handler=Down", down, ("Accept", "true"), ("TermsSha", Field(down, "TermsSha")));
    Check("two computers installed: refused, nothing changed", Said(two.html, "upgradeError").Contains("2 computers are installed, and the Free version has 1"), Said(two.html, "upgradeError"));
    await using (var db = new PlatformDb(options))
    {
        Check("...still Basic", (await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == kb)).Plan == FreePlan.BasicName);
        db.Devices.Remove(await db.Devices.SingleAsync(x => x.DeviceId == "pc-home"));
        await db.SaveChangesAsync();
    }
    var wrongTerms = await Post(ouma, "/portal/upgrade?handler=Down", down, ("Accept", "true"), ("TermsSha", Terms.For("ZA", FreePlan.BasicName).Sha256));
    Check("another text's fingerprint: refused", Said(wrongTerms.html, "upgradeError").Contains("terms were updated"), Said(wrongTerms.html, "upgradeError"));
    var moved = await Post(ouma, "/portal/upgrade?handler=Down", down, ("Accept", "true"), ("TermsSha", Field(down, "TermsSha")));
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == kb);
        Check("one computer: done — the Free plan, running, no trial, nothing paid for", t.Plan == FreePlan.Name && t.Status == TenantStatus.Active && t.TrialEndsUtc == null
            && t.BasicMachines == 0 && moved.to == "/portal/billing?moved=1", $"{t.Plan} {t.Status} {t.BasicMachines} → {moved.to}");
        Check("...the Free terms kept word for word", await db.TermsAcceptances.AnyAsync(a => a.TenantId == kb && a.Sha256 == Terms.For("ZA", FreePlan.Name).Sha256));
        Check("...and the change on Billing, R70 → R0", await db.SeatAgreements.AnyAsync(a => a.TenantId == kb && a.What == "Moved down from the Basic version to the Free version"
            && a.MonthlyBefore == 70m && a.Monthly == 0m));
    }
    var (_, freeBilling, _) = await Get(ouma, "/portal/billing?moved=1");
    Check("Billing now: the Free Version, nothing to pay, Upgrade, no card and no computers to buy", freeBilling.Contains("id=\"freePlan\"") && freeBilling.Contains("Nothing to pay")
        && freeBilling.Contains("id=\"freeUpgrade\"") && freeBilling.Contains("You are on the Free version now") && !freeBilling.Contains("id=\"basicMachines\"")
        && !freeBilling.Contains("Add your card"));
    var (_, freeMachines, _) = await Get(ouma, "/portal/machines");
    Check("Your computers: 1 of 1, the Free version", freeMachines.Contains("id=\"machineCount\">1 of 1<") && freeMachines.Contains("The Free Version reaches 1 of your own computers"));

    // ---- and back up --------------------------------------------------------------------------------------
    Head("the Free version moves up: which, then Basic, then full");
    var (_, choose, _) = await Get(ouma, "/portal/upgrade");
    Check("Upgrade asks which: the Basic version (R50) or the full version (R500)", choose.Contains("id=\"upgradeChoice\"") && choose.Contains("id=\"chooseBasic\"")
        && choose.Contains("id=\"chooseFull\"") && choose.Contains("R50 a month") && choose.Contains("R500 a month") && !choose.Contains("id=\"termsText\""));
    var (_, upBasic, _) = await Get(ouma, "/portal/upgrade?to=basic");
    Check("to Basic: Now nothing, After R50, the Basic terms, a 14-day trial", upBasic.Contains("Now: <strong>nothing</strong> — the Free version") && upBasic.Contains("After: <strong>R50 a month</strong>")
        && Field(upBasic, "TermsSha") == Terms.For("ZA", FreePlan.BasicName).Sha256 && Field(upBasic, "Agreed") == "ZA:1:50" && upBasic.Contains("it is free until then"));
    var wentBasic = await Post(ouma, "/portal/upgrade", upBasic, ("To", "basic"), ("Accept", "true"), ("TermsSha", Field(upBasic, "TermsSha")), ("Agreed", Field(upBasic, "Agreed")));
    DateTime basicTrial;
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == kb);
        basicTrial = t.TrialEndsUtc ?? default;
        Check("upgraded to Basic: the Basic plan, a 14-day trial, three computers again", t.Plan == FreePlan.BasicName && t.Status == TenantStatus.Trial
            && Math.Abs((basicTrial - DateTime.UtcNow.AddDays(14)).TotalHours) < 1 && t.BasicMachines == 0 && wentBasic.to == "/portal/billing?upgraded=1", $"{t.Plan} {t.Status} → {wentBasic.to}");
        Check("...the yes on record, R0 → R50", await db.SeatAgreements.AnyAsync(a => a.TenantId == kb && a.What == "Upgraded from the Free version to the Basic version" && a.Monthly == 50m));
    }
    var (_, afterUp, _) = await Get(ouma, "/portal/billing?upgraded=1");
    Check("...and Billing says so", afterUp.Contains("Upgraded to the Basic version. Your 14-day free trial has started"));
    var (_, upFull, _) = await Get(ouma, "/portal/upgrade");
    Check("from Basic, Upgrade goes to the full version: Now R50, After R500, the trial it is on carried",
        upFull.Contains("Upgrade to the full version") && upFull.Contains("Now: <strong>R50 a month</strong>") && upFull.Contains("After: <strong>R500 a month</strong>")
        && upFull.Contains($"Nothing more is charged until {basicTrial.Local(CompanyClock.Find(CompanyClock.SouthAfrica)):d MMMM yyyy}"));
    var wentFull = await Post(ouma, "/portal/upgrade", upFull, ("To", "full"), ("Accept", "true"), ("TermsSha", Field(upFull, "TermsSha")), ("Agreed", Field(upFull, "Agreed")));
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == kb);
        Check("upgraded to the full version: R500, the same trial's end", !FreePlan.Is(t.Plan) && t.TrialEndsUtc is { } te && Math.Abs((te - basicTrial).TotalMinutes) < 1
            && Pricing.BillFor(t, 1).Total == "R500", $"{t.Plan} → {wentFull.to}");
        Check("...the yes on record, from the Basic version", await db.SeatAgreements.AnyAsync(a => a.TenantId == kb && a.What == "Upgraded from the Basic version" && a.Monthly == 500m));
    }
    var (dfCode, _, dfTo) = await Get(ouma, "/portal/upgrade?to=free");
    Check("the full version has no way down here: Upgrade sends it to Billing", dfCode is HttpStatusCode.Redirect or HttpStatusCode.Found && dfTo == "/portal/billing", $"{dfCode} {dfTo}");

    // ---- the places: the full version waits, neither one-person plan does --------------------------------
    Head("five companies at a time: the Free and Basic versions never wait");
    var (_, approvals, _) = await Get(admin, "/owner/approvals");
    var srv = Regex.Match(approvals, "data-places-server=\"(\\d+)\"").Groups[1].Value;
    await Post(admin, "/owner/approvals?handler=Act", approvals, ("server", srv), ("id", "0"), ("act", "places"), ("places", "2"));
    var guest = Browser();
    var (_, fullSignup, _) = await Get(guest, "/signup?region=ZA&plan=full");
    var (_, basicSignup, _) = await Get(guest, "/signup?region=ZA&plan=basic");
    var (_, freeSignup, _) = await Get(guest, "/signup?region=ZA&plan=free");
    Check("every place taken (Full IT and Karoo Basic, now full): the full version shows the waiting list",
        fullSignup.Contains("id=\"placesFull\"") && !fullSignup.Contains("id=\"termsText\""));
    Check("...the Basic version goes straight to its terms — R50 a month, attended as well", basicSignup.Contains("id=\"termsText\"") && !basicSignup.Contains("id=\"placesFull\"")
        && basicSignup.Contains("The Basic Version for Remote Workers") && basicSignup.Contains("R50 a month") && basicSignup.Contains("asked to allow each connection"));
    Check("...and so does the Free version — its own terms, nothing to pay", freeSignup.Contains("id=\"termsText\"") && !freeSignup.Contains("id=\"placesFull\"")
        && freeSignup.Contains("The Free Version") && freeSignup.Contains("nothing to pay") && freeSignup.Contains("No fees"));
    var (_, plans, _) = await Get(guest, "/signup?region=ZA");
    Check("the plan cards: the full version, the Basic version (R50, attended or unattended), the Free version (nothing to pay, one computer)",
        Regex.IsMatch(plans, "id=\"plan-basic\"[\\s\\S]*?The Basic Version for Remote Workers[\\s\\S]*?R50</strong> a month, after a 14-day free trial[\\s\\S]*?3 computers — more at R10 a month each, up to 20[\\s\\S]*?or attended")
        && Regex.IsMatch(plans, "id=\"plan-free\"[\\s\\S]*?The Free Version[\\s\\S]*?Nothing to pay[\\s\\S]*?1 computer, unattended") && plans.Contains("id=\"plan-full\""));
    // A Free signup is approved by hand (nothing to pay — no PayFast), and goes straight to running.
    var (_, freeTermsPage, _) = await Get(guest, "/signup?region=ZA&plan=free");
    var details = await Post(guest, "/signup?handler=Terms", freeTermsPage, ("Region", "ZA"), ("Plan", "free"), ("Accept", "true"), ("TermsSha", Field(freeTermsPage, "TermsSha")));
    var done = await Post(guest, "/signup?handler=Create", details.html, ("Region", "ZA"), ("Plan", "free"), ("Accept", "true"), ("TermsSha", Field(freeTermsPage, "TermsSha")),
        ("ContactName", "Nomsa Free"), ("Email", "nomsa@free.test"), ("Password", Pw));
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(x => x.ContactName == "Nomsa Free");
        Check("a Free signup: the Free plan, waiting for approval, no card asked for", t is { Plan: FreePlan.Name, Status: TenantStatus.Pending } && done.html.Contains("id=\"signupDone\"")
            && done.html.Contains("signup for the Free version is in") && !done.html.Contains("toPayfast"), $"{t?.Plan} {t?.Status}");
        if (t != null)
        {
            var (_, ap, _) = await Get(admin, "/owner/approvals");
            await Post(admin, "/owner/approvals?handler=Act", ap, ("server", srv), ("id", t.Id.ToString()), ("act", "approve"));
            var a = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == t.Id);
            Check("...approved: running, with no trial", a.Status == TenantStatus.Active && a.TrialEndsUtc == null, $"{a.Status}");
        }
    }
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

// A row written by SQL against the table as it stands at this migration, every required column given a blank (as Seed.OldTenant).
static async Task Insert(PlatformDb db, string table, Dictionary<string, object> given)
{
    var conn = db.Database.GetDbConnection();
    bool opened = conn.State != System.Data.ConnectionState.Open;
    if (opened) await conn.OpenAsync();
    var cols = new List<(string name, string type, bool required)>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) cols.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1 && r.IsDBNull(4) && r.GetInt32(5) == 0));
    }
    foreach (var c in cols.Where(c => c.required && !given.ContainsKey(c.name)))
        given[c.name] = c.type.StartsWith("INT") ? 0 : c.name.EndsWith("Utc") ? "2026-10-25 00:00:00" : "";
    using (var ins = conn.CreateCommand())
    {
        ins.CommandText = $"INSERT INTO {table} ({string.Join(",", given.Keys)}) VALUES ({string.Join(",", given.Keys.Select(k => "@" + k))})";
        foreach (var kv in given) { var p = ins.CreateParameter(); p.ParameterName = "@" + kv.Key; p.Value = kv.Value; ins.Parameters.Add(p); }
        await ins.ExecuteNonQueryAsync();
    }
    if (opened) await conn.CloseAsync();
}
