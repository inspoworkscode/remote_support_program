// =====================================================================================
// pilotcheck — pilot companies (2026-10-15, HANDOVER §146).
//
// Clint: "I have made a deal with the two pilot I.T companies that they each get 5 user license for free and have to
// pay for every extra user ... they each get a link, that opens a free 5user tenant with the option to create extra
// seats, and then they just fill in their details from there", then "free forever for the 5 free seats". And before it:
// the free owner login "should only be able to setup the tenant and have an overview of all tickets ... They should not
// be able to edit or work on the tickets" — "yes, allow comments".
//
// A real platform, signups OFF (the link is the way in, as on the live server), driven over HTTP:
//
//   THE LINK      made on the command line (`pilot-link`); kept as a fingerprint only; a link we never made, a
//                 cancelled one and an expired one are each refused, and say so.
//   THE SIGNUP    the pilot terms (the pilot section ahead of section 1), ticked, then the company: Active at once —
//                 no trial, no approval, no card — the terms kept word for word, the link spent with it.
//   ONCE          the same link again is refused; two sign-ups racing on one link make ONE company.
//   THE PRICE     five seats R0; a sixth refused until we allow more (2026-10-25: then invoiced); no card.
//   THE OWNER     an owner-only login sees the ticket and comments on it, and is refused every other handler and New
//                 ticket over HTTP; the owner who also works tickets is not.
//   ENDING        (2026-10-22) Owner → Pilots: notice for a day (30 days offered), withdrawn, then a day of today ends it
//                 at once — suspended, its people signed out, closed by the sweep 30 days later; or the standard price,
//                 a trial of 14 days for the card.
//
//   dotnet run -c Release --project tools/pilotcheck/pilotcheck.csproj
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

Console.WriteLine("pilotcheck — a link that signs a pilot company up: five seats free for ever, extra seats paid");
var dir = Path.Combine(Path.GetTempPath(), "pilotcheck-" + Guid.NewGuid().ToString("N")[..8]);
var data = Path.Combine(dir, "data"); Directory.CreateDirectory(data);
var d = new DirectoryInfo(AppContext.BaseDirectory);
while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
var proj = Path.Combine(d!.FullName, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
var baseUrl = $"http://127.0.0.1:{port}";
const string Pw = "PilotCheck!2026";
const string AdminKey = "pilotcheck-admin-key-0123456789abcdef0123456789abcdef";

ProcessStartInfo Psi(params string[] args)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "--no-build", "-c", "Debug", "--project", proj, "--" }.Concat(args)) psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = data;
    HarnessDb.Use(psi.Environment, data);
    psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
    // The pilot sweep every second, so a notice day that has come is acted on while the harness waits.
    psi.Environment["RSP_Platform__PilotSweepSeconds"] = "1";
    // OFF, as on the live server: a pilot link must work while public signup is closed.
    psi.Environment["RSP_Platform__SignupsEnabled"] = "false";
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
    server = Process.Start(Psi("run"))!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    var until = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < until) { try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); break; } catch { await Task.Delay(400); } }

    HttpClient Browser() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
    var http = Browser();
    async Task<(HttpStatusCode code, string html, string? to)> Get(HttpClient h, string path)
    {
        var r = await h.GetAsync(path);
        return (r.StatusCode, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()), r.Headers.Location?.ToString());
    }
    string TokenIn(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    async Task<(HttpStatusCode code, string html)> Post(HttpClient h, string path, string fromHtml, params (string k, string v)[] f)
    {
        var all = f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", TokenIn(fromHtml)));
        var r = await h.PostAsync(path, new FormUrlEncodedContent(all));
        return (r.StatusCode, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
    }
    async Task<int> Tenants() { await using var db = new PlatformDb(options); return await db.Tenants.CountAsync(); }
    string LinkIn(string cliOut) => Regex.Match(cliOut, @"/pilot/([0-9A-F]+)").Groups[1].Value;

    // ---- the link ----------------------------------------------------------------------------------
    Head("the link");
    var made = await Cli("pilot-link", "Kgosi Computers", "thandi@kgosi.test");
    var token = LinkIn(made);
    Check("the command line makes a link and prints it", token.Length >= 32 && made.Contains($"{baseUrl}/pilot/{token}"), made.Trim());
    await using (var db = new PlatformDb(options))
    {
        var i = await db.PilotInvites.SingleAsync();
        Check("...kept as a fingerprint, never the link itself", i.TokenHash == PilotService.Hash(token) && !i.TokenHash.Contains(token) && i.CompanyName == "Kgosi Computers");
        Check($"...working for {PilotService.LinkDays} days", Math.Abs((i.ExpiresUtc - DateTime.UtcNow.AddDays(PilotService.LinkDays)).TotalMinutes) < 5);
    }
    var (signupCode, signupPage, _) = await Get(http, "/signup?region=ZA&plan=full");
    Check("public signup is closed on this server, as on the live one", signupPage.Contains("Invitation only") && !signupPage.Contains("id=\"termsText\""));
    var (_, wrong, _) = await Get(http, "/pilot/0123456789ABCDEF0123456789ABCDEF");
    Check("a link we never made is refused, and says so", wrong.Contains("id=\"pilotRefused\"") && wrong.Contains("not a link we made") && !wrong.Contains("id=\"termsText\""));

    // ---- the signup --------------------------------------------------------------------------------
    Head("signing up from the link");
    var pilotTerms = Terms.For("ZA", plan: null, pilot: true);
    var (code, terms, _) = await Get(http, $"/pilot/{token}");
    Check("the link opens the pilot terms, though signup is closed", code == HttpStatusCode.OK && terms.Contains("id=\"termsText\"") && terms.Contains("Your pilot agreement"));
    Check("...the pilot section first: five seats free while a pilot, ended only on 30 days' notice, extras invoiced (2026-10-25)",
        pilotTerms.Source.IndexOf("Your pilot agreement", StringComparison.Ordinal) < pilotTerms.Source.IndexOf("# 1. What the service is", StringComparison.Ordinal)
        && pilotTerms.Source.Contains("while you are a pilot company") && pilotTerms.Source.Contains("at least 30 days before")
        && pilotTerms.Source.Contains("five seats; more are on request") && pilotTerms.Source.Contains("we invoice them each month") && pilotTerms.Source.Contains("R300 a month") && !pilotTerms.Source.Contains("{{"));
    Check("...and differs from the ordinary terms, so its own fingerprint is what is accepted", pilotTerms.Sha256 != Terms.For("ZA").Sha256);
    var noTick = await Post(http, $"/pilot/{token}?handler=Terms", terms, ("TermsSha", pilotTerms.Sha256), ("CompanyName", "Kgosi Computers"), ("Email", "thandi@kgosi.test"));
    Check("Continue without the box ticked is refused", noTick.html.Contains("tick the box") && !noTick.html.Contains("id=\"createAccount\""));
    var ordinary = await Post(http, $"/pilot/{token}?handler=Terms", terms, ("TermsSha", Terms.For("ZA").Sha256), ("Accept", "true"));
    Check("...and so is the fingerprint of the ORDINARY terms", ordinary.html.Contains("Our terms were updated") && !ordinary.html.Contains("id=\"createAccount\""));
    var details = await Post(http, $"/pilot/{token}?handler=Terms", terms, ("TermsSha", pilotTerms.Sha256), ("Accept", "true"),
        ("CompanyName", "Kgosi Computers"), ("Email", "thandi@kgosi.test"));
    Check("ticked: the company form, with the company and email the link was made for",
        details.html.Contains("id=\"createAccount\"") && details.html.Contains("value=\"Kgosi Computers\"") && details.html.Contains("value=\"thandi@kgosi.test\""));
    (string k, string v)[] Company(string email, string company = "Kgosi Computers") => new[]
    {
        ("TermsSha", pilotTerms.Sha256), ("Accept", "true"), ("CompanyName", company), ("ContactName", "Thandi Kgosi"),
        ("ContactNumber", "+27 82 555 0101"), ("Email", email), ("Password", Pw), ("Primary", "#1F4E79"), ("Accent", "#2E9BFF")
    };
    var done = await Post(http, $"/pilot/{token}?handler=Create", details.html, Company("thandi@kgosi.test"));
    Check("the company is made", done.html.Contains("id=\"pilotDone\"") && done.html.Contains("Welcome, Kgosi Computers!"), done.code.ToString());
    int kgosi;
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.Include(x => x.Technicians).SingleAsync();
        kgosi = t.Id;
        Check("it is a pilot, Active at once — no trial, no approval waiting", t.Pilot && t.Status == TenantStatus.Active && t.TrialEndsUtc == null && t.ApprovedUtc != null,
            $"{t.Status} pilot={t.Pilot} trial={t.TrialEndsUtc}");
        Check("...in South Africa, with five seats in its package", t.Region == "ZA" && t.Seats == 5);
        Check("...its owner the person who signed up", t.Technicians.Single().Email == "thandi@kgosi.test" && t.Technicians.Single().Role == TenantRole.OwnerSupervisor);
        var a = await db.TermsAcceptances.SingleAsync();
        Check("the PILOT terms are kept word for word", a.TenantId == t.Id && a.Text == pilotTerms.Source && a.Sha256 == pilotTerms.Sha256 && a.AcceptedByEmail == "thandi@kgosi.test");
        var i = await db.PilotInvites.SingleAsync();
        Check("the link is spent, and names the company it made", i.UsedUtc != null && i.TenantId == t.Id);
    }
    var (_, againPage, _) = await Get(http, $"/pilot/{token}");
    Check("the same link again is refused", againPage.Contains("id=\"pilotRefused\"") && againPage.Contains("already been used"));
    var replay = await Post(http, $"/pilot/{token}?handler=Create", details.html, Company("sipho@other.test", "Another Company"));
    Check("...and a form posted to it makes nothing", !replay.html.Contains("id=\"pilotDone\"") && await Tenants() == 1);

    // ---- once, even racing ---------------------------------------------------------------------------
    Head("one link, one company");
    var raceToken = LinkIn(await Cli("pilot-link", "Race IT"));
    // Each racer needs its own antiforgery pairing: a fresh browser fetches the page first.
    var racers = Enumerable.Range(0, 4).Select(async n =>
    {
        var b = Browser();
        var (_, p1, _) = await Get(b, $"/pilot/{raceToken}");
        var p2 = await Post(b, $"/pilot/{raceToken}?handler=Terms", p1, ("TermsSha", pilotTerms.Sha256), ("Accept", "true"));
        return await Post(b, $"/pilot/{raceToken}?handler=Create", p2.html, Company($"racer{n}@race.test", $"Race IT {n}"));
    }).ToList();
    var raced = await Task.WhenAll(racers);
    Check("four sign-ups at once on one link make ONE company", raced.Count(r => r.html.Contains("id=\"pilotDone\"")) == 1 && await Tenants() == 2,
        $"{raced.Count(r => r.html.Contains("id=\"pilotDone\""))} done, {await Tenants()} companies");

    // A link made WITHOUT an email (2026-10-16, live): the form posts an EMPTY Email box, which binding makes null — the
    // terms step threw a NullReferenceException and answered 500 on the live server for every tick of the box.
    var bareToken = LinkIn(await Cli("pilot-link", "Bare IT"));
    var bare = Browser();
    var (_, bareTerms, _) = await Get(bare, $"/pilot/{bareToken}");
    Check("a link with no email opens with the email box empty", bareTerms.Contains("name=\"Email\" value=\"\""));
    var bareDetails = await Post(bare, $"/pilot/{bareToken}?handler=Terms", bareTerms, ("TermsSha", pilotTerms.Sha256), ("Accept", "true"),
        ("CompanyName", "Bare IT"), ("Email", ""));
    Check("...and ticking its terms posts that empty box and gets the company form, not a 500",
        bareDetails.code == HttpStatusCode.OK && bareDetails.html.Contains("id=\"createAccount\"") && bareDetails.html.Contains("value=\"Bare IT\""),
        bareDetails.code.ToString());
    var bareBlank = await Post(bare, $"/pilot/{bareToken}?handler=Terms", bareTerms, ("TermsSha", pilotTerms.Sha256), ("Accept", "true"),
        ("CompanyName", ""), ("Email", ""));
    Check("...and an emptied company box too: the company the link was made for is put back",
        bareBlank.code == HttpStatusCode.OK && bareBlank.html.Contains("value=\"Bare IT\""), bareBlank.code.ToString());
    var bareDone = await Post(bare, $"/pilot/{bareToken}?handler=Create", bareDetails.html,
        ("TermsSha", pilotTerms.Sha256), ("Accept", "true"), ("CompanyName", "Bare IT"), ("ContactName", "Bo Bare"), ("ContactNumber", "+27 82 555 0199"),
        ("Email", "bo@bare.test"), ("Password", Pw), ("Primary", ""), ("Accent", ""), ("TicketEmail", ""));
    Check("...and with every optional box left empty, the company is made", bareDone.code == HttpStatusCode.OK && bareDone.html.Contains("id=\"pilotDone\""),
        bareDone.code.ToString());

    var cancelToken = LinkIn(await Cli("pilot-link", "Cancelled IT"));
    var oldToken = LinkIn(await Cli("pilot-link", "Late IT"));
    await using (var db = new PlatformDb(options))
    {
        var c = await db.PilotInvites.SingleAsync(x => x.TokenHash == PilotService.Hash(cancelToken)); c.CancelledUtc = DateTime.UtcNow;
        var o = await db.PilotInvites.SingleAsync(x => x.TokenHash == PilotService.Hash(oldToken)); o.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }
    Check("a cancelled link is refused", (await Get(http, $"/pilot/{cancelToken}")).html.Contains("no longer works"));
    Check("an expired link is refused", (await Get(http, $"/pilot/{oldToken}")).html.Contains("no longer works"));

    // ---- the price -----------------------------------------------------------------------------------
    Head("the price: five seats free, and five is the most (2026-10-22)");
    Tenant Pilot() => new() { Region = "ZA", Seats = 5, Pilot = true };
    Check("five people: R0 a month", Pricing.BillFor(Pilot(), 5).Monthly == 0m && Pricing.BillFor(Pilot(), 1).Monthly == 0m);
    Check("an ordinary company still pays the package", Pricing.BillFor(new Tenant { Region = "ZA", Seats = 5 }, 5).Monthly == 500m);
    Check("the bill says the five are free", Pricing.BillFor(Pilot(), 3).Sum.Contains("free"), Pricing.BillFor(Pilot(), 3).Sum);
    Check("five seats at most, as for everybody", Pricing.MaxSeats == 5);
    Check("...so a sixth person is refused until we allow more", Pricing.LimitProblem(Pilot(), 5, 6) is { } full && full.Contains("may have 5 seats"),
        Pricing.LimitProblem(Pilot(), 5, 6) ?? "");
    Check("no card asked of a pilot", CompanyBilling.Refusal(Pilot(), null) is { } why && why.Contains("no card"), CompanyBilling.Refusal(Pilot(), null) ?? "");

    // ---- the owner who only owns -----------------------------------------------------------------------
    Head("an owner-only login oversees and comments; it does not work tickets");
    await using (var db = new PlatformDb(options))
    {
        var (salt, hash) = Passwords.Create(Pw);
        db.Technicians.Add(new Technician { TenantId = kgosi, Email = "olive@kgosi.test", DisplayName = "Olive Owner", Role = TenantRole.Owner, Salt = salt, Hash = hash });
        db.Tickets.Add(new Ticket { TenantId = kgosi, Number = 1, Subject = "Printer offline", Description = "The office printer", OpenedBy = "Thandi Kgosi",
            Category = TicketCategory.General, Stage = Workflow.GeneralFirst });
        await db.SaveChangesAsync();
    }
    int ticketId, ticketNo; await using (var db = new PlatformDb(options)) { var only = await db.Tickets.SingleAsync(); ticketId = only.Id; ticketNo = only.Number; }
    // The ticket page is addressed by the ticket's number in the company (HANDOVER §159); this browser does not follow redirects.
    async Task<HttpClient> SignIn(string email)
    {
        var b = Browser();
        var (_, page, _) = await Get(b, "/login");
        await Post(b, "/login", page, ("Email", email), ("Password", Pw));
        return b;
    }
    var olive = await SignIn("olive@kgosi.test");
    var (tcode, tpage, _) = await Get(olive, $"/portal/t/{ticketNo}");
    Check("it opens the ticket", tcode == HttpStatusCode.OK && tpage.Contains("Printer offline"), tcode.ToString());
    Check("...with the comment box, and no Edit the ticket, client box or people", tpage.Contains("handler=Comment") && !tpage.Contains("Edit the ticket")
        && !tpage.Contains("handler=SetClient") && !tpage.Contains("handler=Edit\""));
    var commented = await Post(olive, $"/portal/t/{ticketNo}?handler=Comment", tpage, ("id", ticketId.ToString()), ("note", "Please call them back today, @Thandi Kgosi"));
    await using (var db = new PlatformDb(options))
        Check("its comment is posted", (commented.code is HttpStatusCode.Redirect or HttpStatusCode.Found) && await db.TicketNotes.AnyAsync(n => n.TicketId == ticketId && n.Author == "Olive Owner"),
            commented.code.ToString());
    var edit = await Post(olive, $"/portal/t/{ticketNo}?handler=Edit", tpage, ("id", ticketId.ToString()), ("subject", "Changed by the owner"), ("description", "x"));
    await using (var db = new PlatformDb(options))
        Check("an edit is refused before it runs (403), and nothing changes", edit.code == HttpStatusCode.Forbidden && edit.html.Contains("does not work them")
            && (await db.Tickets.SingleAsync()).Subject == "Printer offline", edit.code.ToString());
    var act = await Post(olive, $"/portal/t/{ticketNo}?handler=Act", tpage, ("id", ticketId.ToString()), ("action", "general-done"));
    Check("...and so is a stage button", act.code == HttpStatusCode.Forbidden, act.code.ToString());
    var (ncode, _, nto) = await Get(olive, "/portal/tickets/new");
    Check("New ticket sends it back to the tickets", ncode is HttpStatusCode.Redirect or HttpStatusCode.Found && nto == "/portal/tickets", $"{ncode} {nto}");
    var (_, list, _) = await Get(olive, "/portal/tickets");
    Check("...and the tickets list offers no New ticket", list.Contains("Printer offline") && !list.Contains("href=\"/portal/tickets/new\""));
    var (_, billing, _) = await Get(olive, "/portal/billing");
    Check("it may still see Billing — five seats free", billing.Contains("id=\"monthlyTotal\">R0") && billing.Contains("free"));

    var thandi = await SignIn("thandi@kgosi.test");
    var (_, tp2, _) = await Get(thandi, $"/portal/t/{ticketNo}");
    var edit2 = await Post(thandi, $"/portal/t/{ticketNo}?handler=Edit", tp2, ("id", ticketId.ToString()), ("subject", "Printer offline at reception"), ("description", "The office printer"));
    await using (var db = new PlatformDb(options))
        Check("the owner who also supervises edits it as before", edit2.code == HttpStatusCode.OK && (await db.Tickets.SingleAsync()).Subject == "Printer offline at reception", edit2.code.ToString());

    // ---- ending a pilot (2026-10-22) ----------------------------------------------------------------------
    Head("ending a pilot: a notice day, then suspended and closed — or the standard price");
    var admin = Browser();
    var (_, loginForm, _) = await Get(admin, "/owner/login");
    var keyStep = await Post(admin, "/owner/login", loginForm, ("Key", AdminKey));
    var keySecret = Regex.Match(keyStep.html, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
    string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    await Post(admin, "/owner/login", keyStep.html, ("UseKey", "true"), ("Pending", Field(keyStep.html, "Pending")), ("Code", RemoteSupport.Shared.Totp.Now(keySecret)));
    var (pcode, pilotsPage, _) = await Get(admin, "/owner/pilots");
    Check("Owner → Pilots lists the pilot companies, with how much each has used the service",
        pcode == HttpStatusCode.OK && pilotsPage.Contains("id=\"pilotCompanies\"") && pilotsPage.Contains($"data-pilot-company=\"{kgosi}\"")
        && pilotsPage.Contains("0 machines") && pilotsPage.Contains("End the pilot"), pcode.ToString());
    var zaToday = DateTime.UtcNow.AddHours(2).Date;
    Check($"...offering the terms' {PilotService.NoticeDays} days' notice as the day", pilotsPage.Contains($"value=\"{zaToday.AddDays(30):yyyy-MM-dd}\""));

    var notice = await Post(admin, "/owner/pilots?handler=Notice", pilotsPage, ("id", kgosi.ToString()), ("day", zaToday.AddDays(30).ToString("yyyy-MM-dd")),
        ("after", PilotService.Close), ("message", "We have not seen you use it yet."));
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.SingleAsync(x => x.Id == kgosi);
        Check("notice given: the day is stored, at the start of that day in South Africa, and nothing else changes yet",
            t.Pilot && t.Status == TenantStatus.Active && t.PilotAfter == PilotService.Close
            && t.PilotEndsUtc?.ToString("yyyy-MM-dd HH:mm") == zaToday.AddDays(29).ToString("yyyy-MM-dd") + " 22:00",
            $"{t.PilotEndsUtc:yyyy-MM-dd HH:mm} UTC");
        Check("...the page says so", notice.html.Contains($"pilot ends on {zaToday.AddDays(30):d MMMM yyyy}") && notice.html.Contains("Notice given"));
        Check("...and the admin log has it, by name", await db.AdminLog.AnyAsync(e => e.What.StartsWith("Pilot notice: ends")));
    }
    var (_, thandiBilling, _) = await Get(thandi, "/portal/billing");
    Check("the company's Billing page says when the pilot ends and what follows", thandiBilling.Contains("id=\"pilotEnding\"") && thandiBilling.Contains("is suspended"));
    var withdrawn = await Post(admin, "/owner/pilots?handler=Withdraw", notice.html, ("id", kgosi.ToString()));
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.SingleAsync(x => x.Id == kgosi);
        Check("Withdraw notice: a pilot again, as before", t.Pilot && t.PilotEndsUtc == null && t.PilotAfter == "" && withdrawn.html.Contains("stays a pilot"));
    }
    var wrongWay = await Post(admin, "/owner/pilots?handler=Notice", withdrawn.html, ("id", kgosi.ToString()), ("day", zaToday.ToString("yyyy-MM-dd")), ("after", "delete"));
    await using (var db = new PlatformDb(options))
        Check("an ending that is neither close nor pay is refused", wrongWay.html.Contains("Choose what happens") && (await db.Tenants.SingleAsync(x => x.Id == kgosi)).Pilot);

    // Today: ends at once.
    var now0 = await Post(admin, "/owner/pilots?handler=Notice", withdrawn.html, ("id", kgosi.ToString()), ("day", zaToday.ToString("yyyy-MM-dd")), ("after", PilotService.Close));
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.SingleAsync(x => x.Id == kgosi);
        Check("a notice for today ends the pilot at once: no longer a pilot, Suspended", !t.Pilot && t.Status == TenantStatus.Suspended && t.PilotEndedUtc != null,
            $"pilot={t.Pilot} {t.Status}");
        Check("...the page says it ended", now0.html.Contains("pilot has ended"));
    }
    var (scode, _, sto) = await Get(thandi, "/portal");
    Check("...and its people are signed out", scode is HttpStatusCode.Redirect or HttpStatusCode.Found && (sto ?? "").Contains("/login"), $"{scode} {sto}");
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.SingleAsync(x => x.Id == kgosi);
        t.PilotEndedUtc = DateTime.UtcNow.AddDays(-PilotService.SuspendedDays).AddMinutes(-1);
        await db.SaveChangesAsync();
    }
    TenantStatus closed = TenantStatus.Suspended;
    for (int i = 0; i < 40 && closed != TenantStatus.Closed; i++)
    {
        await Task.Delay(500);
        await using var db = new PlatformDb(options);
        closed = (await db.Tenants.SingleAsync(x => x.Id == kgosi)).Status;
    }
    Check($"{PilotService.SuspendedDays} days suspended: the sweep closes it", closed == TenantStatus.Closed, closed.ToString());

    // The standard price: on a day already past (moved in the database), the sweep moves it to a trial with 14 days for a card.
    int race; await using (var db = new PlatformDb(options)) race = (await db.Tenants.FirstAsync(x => x.Pilot && x.CompanyName.StartsWith("Race IT"))).Id;
    var (_, pilotsPage2, _) = await Get(admin, "/owner/pilots");
    await Post(admin, "/owner/pilots?handler=Notice", pilotsPage2, ("id", race.ToString()), ("day", zaToday.AddDays(40).ToString("yyyy-MM-dd")), ("after", PilotService.Pay));
    await using (var db = new PlatformDb(options))
    {
        var t = await db.Tenants.SingleAsync(x => x.Id == race);
        Check("a notice to move to the standard price is stored", t.Pilot && t.PilotAfter == PilotService.Pay && t.PilotEndsUtc > DateTime.UtcNow.AddDays(39));
        t.PilotEndsUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }
    Tenant? paid = null;
    for (int i = 0; i < 40 && (paid == null || paid.Pilot); i++)
    {
        await Task.Delay(500);
        await using var db = new PlatformDb(options);
        paid = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == race);
    }
    Check("its day come, the sweep ends it: a 14-day trial on the standard price, the service still on",
        paid is { Pilot: false, Status: TenantStatus.Trial, IsServiceEnabled: true } && paid.TrialEndsUtc is { } te && Math.Abs((te - DateTime.UtcNow.AddDays(14)).TotalHours) < 1,
        $"pilot={paid?.Pilot} {paid?.Status} trial ends {paid?.TrialEndsUtc:yyyy-MM-dd}");
    Check("...its price is the package now", paid != null && Pricing.BillFor(paid, 1).Monthly == 500m && CompanyBilling.Refusal(paid, null) is { } cr && !cr.Contains("pilot"),
        paid == null ? "" : CompanyBilling.Refusal(paid, null) ?? "");
    var (_, pilotsPage3, _) = await Get(admin, "/owner/pilots");
    Check("Owner → Pilots shows both as ended", pilotsPage3.Contains($"data-pilot-company=\"{race}\"") && pilotsPage3.Split("Ended ").Length - 1 >= 2);
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
