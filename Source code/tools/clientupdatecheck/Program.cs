// =====================================================================================
// clientupdatecheck - proves updating the client from a ticket (Clint, 2026-10-09).
//
//   "If someone posts a comment, there should be an option to update client. The client then gets
//    the original fault as per the ticket description with the comment whoever has posted, but not
//    the entire string of the ticket. If a client replies, it should come back into the same ticket.
//    A lot of comments would be for internal purposes only, so its important that only the original
//    fault, plus only the comment that one has selected to update the client with goes out. The
//    email would include the name of the person that made the comment."
//
// His answers: a tick box when posting AND "Email to the client" on a comment already posted; the
// ticket's contact by default, changeable or typed; fault + THIS comment only, every time; from the
// Tickets by email address, which signup now asks for; a reply flags the people on the ticket until
// they open it.
//
// On a platform of its own with a FAKE IMAP SERVER and a FAKE SMTP SERVER inside this harness (the
// ones mailintakecheck uses): the email is caught as the bytes that would have left, and the client's
// answer is delivered into the mailbox and read by the real minute-timer.
//
//   dotnet run -c Debug --project tools/clientupdatecheck/clientupdatecheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-clientupdatecheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

const string Pw = "client-update-2026";
const string Slug = "update-co";
const string Support = "support@updateco.test", SupportPw = "right-pw";
const string PlatformFrom = "noreply@platform.test";
const string Fault = "The printer in accounts jams on every page.";
const string Internal = "INTERNAL ONLY: the toner they bought is a cheap copy, which is the real cause.";

using var imap = new FakeImap();
using var smtp = new FakeSmtp();
imap.Start(); smtp.Start();
imap.AddUser(Support, SupportPw);
smtp.AddUser(Support, SupportPw);
Console.WriteLine($"clientupdatecheck — emailing a comment to the client; a platform on port {port}, IMAP on {imap.Port}, SMTP on {smtp.Port}");

Process? server = null;
try
{
    // =========================================================== the email itself, in-process
    Head("what the client is sent — the fault and ONE comment, and nothing else");
    var tenantShape = new Tenant { CompanyName = "Update Co", AccentColor = "#123456" };
    var tShape = new Ticket { Number = 41, Subject = "Printer", Description = Fault };
    var nShape = new TicketNote { Author = "Tess Tech", Body = "A new drum is on order <b>and</b> arrives Thursday." };
    var text = ClientUpdateService.Text(tenantShape, tShape, nShape, "Jane Smith");
    Check("it greets them by name", text.StartsWith("Hello Jane Smith,"));
    Check("it names who wrote the comment, and the ticket", text.Contains("Tess Tech has an update on ticket #41 — Printer"));
    Check("the comment, word for word", text.Contains(nShape.Body));
    Check("the original fault, under its own heading", text.Contains("Your original request:") && text.Contains(Fault));
    Check("an address with no name is 'Hello there'", ClientUpdateService.Text(tenantShape, tShape, nShape, "jane@acme.test").StartsWith("Hello there,"));
    var html = ClientUpdateService.Html(tenantShape, tShape, nShape, "Jane <Smith>", null);
    Check("the HTML encodes what was typed — no markup of theirs or ours gets through", html.Contains("&lt;b&gt;and&lt;/b&gt;") && !html.Contains("<b>and</b>") && html.Contains("Jane &lt;Smith&gt;"));
    Check("...in the company's colour, and named when there is no logo", html.Contains("#123456") && html.Contains(">Update Co<"));
    var bad = ClientUpdateService.Html(new Tenant { CompanyName = "X", AccentColor = "red;}</style><script>" }, tShape, nShape, "", null);
    Check("a colour that is not #rrggbb is never written into the style", !bad.Contains("<script>") && bad.Contains("#2E9BFF"));
    var longT = new Ticket { Number = 1, Subject = "Long", Description = new string('x', ClientUpdateService.FaultMax + 500) };
    Check($"a very long fault is cut at {ClientUpdateService.FaultMax} characters", ClientUpdateService.Fault(longT).Length <= ClientUpdateService.FaultMax + 2);
    Check("no fault written: the section is left out rather than empty",
        !ClientUpdateService.Text(tenantShape, new Ticket { Number = 2, Subject = "S" }, nShape, "").Contains("Your original request"));

    Head("which notes may be emailed");
    Check("a comment may", ClientUpdateService.Sendable(new TicketNote { Body = "hi" }));
    Check("an automatic line may not", !ClientUpdateService.Sendable(new TicketNote { Body = "status → Closed", IsSystem = true }));
    Check("a step's form may not", !ClientUpdateService.Sendable(new TicketNote { Body = "x", FormTitle = "Inspection findings" }));
    Check("a Work done entry (the invoice's) may not", !ClientUpdateService.Sendable(new TicketNote { Body = "x", IsWorkDone = true }));
    Check("the customer's own reply may not", !ClientUpdateService.Sendable(new TicketNote { Body = "x", FromCustomer = true }));
    Check("an empty one may not", !ClientUpdateService.Sendable(new TicketNote { Body = "  " }));

    // =========================================================== the company
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl, ["Platform:Region"] = "ZA" }).Build();
    var dp = DataProtectionProvider.Create("clientupdatecheck");
    var tenants = new TenantService(factory, cfg, dataDir, dp);

    Head("signup asks for the ticket email address");
    var zaSha = Terms.For("ZA").Sha256;
    TenantService.SignupRequest Su(string email, string? ticketEmail) =>
        new("ZA", null, "Signup Co " + email[..3], "Sam Signup", "0215550000", email, Pw, null, null, true, zaSha, "203.0.113.9", null, null, ticketEmail);
    var (s1, e1) = await tenants.SignupAsync(Su("sam@signup.test", "Help@Signup.test"));
    TicketIntakeSettings? kept;
    await using (var db = new PlatformDb(options)) kept = await db.TicketIntake.FirstOrDefaultAsync(x => x.TenantId == (s1 == null ? -1 : s1.Id));
    Check("given: it is kept for Tickets by email, lower-cased", s1 != null && kept?.Address == "help@signup.test", e1);
    Check("...switched OFF — the mailbox and its password are the portal's to ask", kept is { EmailEnabled: false, ImapPasswordEnc: "" });
    var (s2, e2) = await tenants.SignupAsync(Su("sid@signup.test", "not an address"));
    Check("a mistyped one is refused, saying it can be left blank", s2 == null && e2.Contains("ticket email address") && e2.Contains("blank"), e2);
    var (s3, e3) = await tenants.SignupAsync(Su("sue@signup.test", ""));
    await using (var db = new PlatformDb(options)) kept = await db.TicketIntake.FirstOrDefaultAsync(x => x.TenantId == (s3 == null ? -1 : s3.Id));
    Check("left blank: the signup goes through, and nothing is made", s3 != null && kept == null, e3);
    var (s4, e4) = await tenants.SignupAsync(Su("fay@signup.test", "help@signup.test") with { Plan = "free", TermsSha256 = Terms.For("ZA", "free").Sha256 });
    await using (var db = new PlatformDb(options)) kept = await db.TicketIntake.FirstOrDefaultAsync(x => x.TenantId == (s4 == null ? -1 : s4.Id));
    Check("the free version has no tickets, so it keeps none", s4 != null && kept == null, e4);

    var (tenant, _) = await tenants.CreateAsync("Update Co", Slug, 7);
    int tid = tenant!.Id;
    await tenants.AddTechnicianAsync(tid, "owner@updateco.test", Pw, "Olga Owner", TenantRole.Owner | TenantRole.Admin);
    await tenants.AddTechnicianAsync(tid, "tech@updateco.test", Pw, "Tess Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(tid, "office@updateco.test", Pw, "Bea Office", TenantRole.Billing);
    var clients = new ClientService(factory, dp);
    var contacts = new ContactService(factory);
    var (acme, _) = await clients.CreateAsync(tid, "Acme Ltd", "", "", "info@acme.test", "", "");
    var (jane, _) = await contacts.CreateAsync(tid, acme!.Id, "Jane Smith", "", "jane@acme.test", "", "");
    await contacts.CreateAsync(tid, acme.Id, "Ken Accounts", "", "ken@acme.test", "", "");
    var tickets = new TicketService(factory);
    var tA = await tickets.CreateAsync(tid, "Printer", Fault, "Olga Owner", TicketSource.Manual, contactName: "Jane Smith",
        assignedTo: "Tess Tech", category: TicketCategory.General, clientId: acme.Id, extra: x => x.ContactId = jane!.Id);
    await tickets.AddNoteAsync(tid, tA.Id, "Olga Owner", Internal, false);
    // A ticket of the old kind, with no route, so the classic page's form is proved too (§61: both layouts).
    var tB = await tickets.CreateAsync(tid, "Old style", "The scanner will not scan.", "Olga Owner", TicketSource.Manual,
        contactName: "Jane Smith", category: TicketCategory.General, clientId: acme.Id, extra: x => x.ContactId = jane!.Id);
    await using (var db = new PlatformDb(options)) { var b = await db.Tickets.FirstAsync(x => x.Id == tB.Id); b.Stage = ""; b.Lane = 0; await db.SaveChangesAsync(); }

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
        p.Environment["RSP_Testing__IntakeSeconds"] = "2";           // the minute, made two seconds
        p.Environment["RSP_Urls"] = baseUrl;
        p.Environment["ASPNETCORE_URLS"] = baseUrl;
        return p;
    }
    async Task<string> Cli(params string[] args)
    {
        var p = Psi(); p.ArgumentList.Add("--no-build"); p.ArgumentList.Add("--");
        foreach (var a in args) p.ArgumentList.Add(a);
        var pr = Process.Start(p)!; var o = await pr.StandardOutput.ReadToEndAsync(); await pr.WaitForExitAsync(); return o;
    }
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var built = await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + built); return 1; }
    var pm = await Cli("platform-mail", "set", "--host", "127.0.0.1", "--port", smtp.Port.ToString(), "--nossl", "--from", PlatformFrom, "--from-name", "The Remote Support Program");
    Check("the platform's own mailbox is set", pm.Contains("Saved"), pm.Trim());
    var psi = Psi(); psi.ArgumentList.Add("--no-build"); psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    var owner = await Portal(port, "owner@updateco.test");
    var tech = await Portal(port, "tech@updateco.test");
    var office = await Portal(port, "office@updateco.test");
    string TicketUrl(int id) => $"/portal/tickets/{id}";

    Head("before Tickets by email is set up: not offered, and said why");
    var pg = WebUtility.HtmlDecode(await owner.GetStringAsync(TicketUrl(tA.Id)));
    Check("the owner is told to set up Tickets by email, with a link", pg.Contains("set up Tickets by email first") && pg.Contains("href=\"/portal/mailintake\""), Snip(pg, "To email comments"));
    Check("...and there is no tick box", !pg.Contains("Also email this to the client"));
    var tp = WebUtility.HtmlDecode(await tech.GetStringAsync(TicketUrl(tA.Id)));
    Check("a technician is told to ask the owner or a supervisor — no link to a page they cannot open", tp.Contains("ask the owner or a supervisor") && !tp.Contains("href=\"/portal/mailintake\""));
    var early = WebUtility.HtmlDecode(await (await PostForm(tech, TicketUrl(tA.Id) + "?handler=Comment",
        new() { ["id"] = tA.Id.ToString(), ["note"] = "posted before it was set up", ["emailClient"] = "true", ["emailTo"] = "jane@acme.test" })).Content.ReadAsStringAsync());
    Check("a tick posted anyway is refused by the server, and the comment is NOT posted", early.Contains("has not been posted") && (await Notes(options, tA.Id)).All(n => !n.Body.Contains("posted before it was set up")));
    Check("...with the words kept in the box", early.Contains(">posted before it was set up</textarea>"));

    Head("Tickets by email switched on");
    var saved = WebUtility.HtmlDecode(await (await PostForm(owner, "/portal/mailintake?handler=Email", new()
    {
        ["emailEnabled"] = "true", ["address"] = Support, ["way"] = "mailbox",
        ["imapHost"] = "127.0.0.1", ["imapPort"] = imap.Port.ToString(), ["imapSecurity"] = "ssl", ["imapUser"] = Support, ["imapPassword"] = SupportPw, ["folder"] = "INBOX",
        ["autoReply"] = "true", ["sendVia"] = "own", ["smtpSameLogin"] = "true",
        ["smtpHost"] = "127.0.0.1", ["smtpPort"] = smtp.Port.ToString(), ["smtpSecurity"] = "ssl", ["smtpUser"] = Support, ["smtpPassword"] = "",
        ["autoReplyText"] = TicketIntake.DefaultReply
    })).Content.ReadAsStringAsync());
    Check("saved and tested", saved.Contains("The test worked"), Snip(saved, "Reading:"));
    Check("the page now says comments can be emailed from it", saved.Contains("update a client from a ticket"));

    Head("the tick box, as the technician sees it");
    tp = WebUtility.HtmlDecode(await tech.GetStringAsync(TicketUrl(tA.Id)));
    Check("under the comment box: Also email this to the client, unticked", Regex.IsMatch(tp, "<input type=\"checkbox\" name=\"emailClient\" value=\"true\" onchange"));
    Check("Jane, the ticket's contact, is chosen by default", tp.Contains("<option value=\"jane@acme.test\" selected=\"selected\">Jane Smith <jane@acme.test></option>"), Snip(tp, "value=\"jane@acme.test\""));
    Check("the client's other people and its own address are offered too", tp.Contains("value=\"ken@acme.test\"") && tp.Contains("value=\"info@acme.test\""));
    Check("...and Another address…", tp.Contains("value=\"other\""));
    Check("it says what goes, and from where", tp.Contains("this comment, with your name on it, from " + Support) && tp.Contains("nothing else on the ticket"));
    Check("the automatic and internal entries offer nothing, the typed comment does",
        CountOf(tp, "Email to the client…") == 1, $"{CountOf(tp, "Email to the client…")} offered");

    Head("a comment posted with the box ticked");
    const string First = "A new drum is on order and arrives on Thursday.";
    var mailsBefore = smtp.Count;
    var r1 = await PostForm(tech, TicketUrl(tA.Id) + "?handler=Comment",
        new() { ["id"] = tA.Id.ToString(), ["note"] = First, ["emailClient"] = "true", ["emailTo"] = "jane@acme.test" });
    var after1 = WebUtility.HtmlDecode(await r1.Content.ReadAsStringAsync());
    Check("back by a GET that says how it went (a refresh cannot send it twice)", r1.RequestMessage!.RequestUri!.Query.Contains("sent="), r1.RequestMessage.RequestUri.PathAndQuery);
    Check("...'Emailed to jane@acme.test.' at the top", after1.Contains("Emailed to jane@acme.test."));
    var m1 = await smtp.WaitFor("jane@acme.test", $"[Ticket #{tA.Number}]");
    Check("an email reaches Jane", m1 != null);
    if (m1 == null) return Done();
    Check("ONE email — nobody else was sent anything", smtp.Count == mailsBefore + 1 && m1.To.Count == 1, string.Join(", ", m1.To));
    Check("from the support address, through the company's own server and login", m1.FromAddress == Support && m1.AuthUser == Support && m1.FromName == "Update Co");
    Check("subject: [Ticket #N] and the ticket's subject", m1.Subject == $"[Ticket #{tA.Number}] Printer", m1.Subject);
    Check("it names who wrote the comment", m1.Body.Contains("Tess Tech has an update on ticket"));
    Check("the comment", m1.Body.Contains(First));
    Check("the original fault", m1.Body.Contains(Fault));
    Check("NOT the internal comment on the same ticket", !m1.Body.Contains("INTERNAL ONLY") && !m1.Html.Contains("INTERNAL ONLY"));
    Check("an HTML part with the same words, for mail programs that show it", m1.Html.Contains(WebUtility.HtmlEncode(First)) && m1.Html.Contains("Tess Tech"));
    Check("NOT marked automatic — a person wrote it", m1.Header("Auto-Submitted") == "");
    Check("its Message-ID carries the ticket, so the reply finds it", Regex.IsMatch(m1.Header("Message-ID"), $@"<rsp\.{tid}\.{tA.Number}\."), m1.Header("Message-ID"));
    var page1 = WebUtility.HtmlDecode(await tech.GetStringAsync(TicketUrl(tA.Id)));
    Check("the comment says it was emailed, to whom, by whom", page1.Contains("Emailed to jane@acme.test by Tess Tech"));
    Check("...and offers to email it again", page1.Contains("Email it again…"));

    Head("a comment posted WITHOUT the box ticked stays on the ticket");
    var before2 = smtp.Count;
    await PostForm(tech, TicketUrl(tA.Id) + "?handler=Comment", new() { ["id"] = tA.Id.ToString(), ["note"] = "Ordered from Rectron, invoice 5521.", ["emailTo"] = "jane@acme.test" });
    await Task.Delay(1500);
    Check("nothing is sent", smtp.Count == before2);

    Head("Email to the client on a comment already posted — to an address typed in");
    var ordered = (await Notes(options, tA.Id)).First(n => n.Body.StartsWith("Ordered from Rectron"));
    var r2 = await PostForm(owner, TicketUrl(tA.Id) + "?handler=EmailNote",
        new() { ["id"] = tA.Id.ToString(), ["noteId"] = ordered.Id.ToString(), ["emailTo"] = "other", ["emailOther"] = "Boss@Elsewhere.test" });
    var m2 = await smtp.WaitFor("boss@elsewhere.test", $"[Ticket #{tA.Number}]");
    Check("it goes to the address typed", m2 != null);
    if (m2 == null) return Done();
    Check("with THAT comment and its author's name — Tess wrote it, Olga sent it", m2.Body.Contains("Ordered from Rectron") && m2.Body.Contains("Tess Tech has an update"));
    Check("the fault again", m2.Body.Contains(Fault));
    Check("but NOT the comment emailed before — every email is the fault and one comment (Clint's answer)", !m2.Body.Contains(First));
    Check("...nor the internal one", !m2.Body.Contains("INTERNAL ONLY"));
    Check("it threads under the last one (In-Reply-To)", m2.Header("In-Reply-To") == m1.Header("Message-ID"), $"{m2.Header("In-Reply-To")} vs {m1.Header("Message-ID")}");
    var page2 = WebUtility.HtmlDecode(await r2.Content.ReadAsStringAsync());
    Check("the page says so", page2.Contains("Emailed to boss@elsewhere.test."));

    Head("refusals");
    var countNotes = (await Notes(options, tA.Id)).Count;
    var bad1 = WebUtility.HtmlDecode(await (await PostForm(tech, TicketUrl(tA.Id) + "?handler=Comment",
        new() { ["id"] = tA.Id.ToString(), ["note"] = "Keep these words", ["emailClient"] = "true", ["emailTo"] = "other", ["emailOther"] = "not an address" })).Content.ReadAsStringAsync());
    Check("a typed address that is not one: refused, saying so", bad1.Contains("does not look like an email address") && bad1.Contains("has not been posted"));
    Check("...the comment NOT posted", (await Notes(options, tA.Id)).Count == countNotes);
    Check("...its words back in the box, ticked, with the address", bad1.Contains(">Keep these words</textarea>") && bad1.Contains("value=\"not an address\"") && Regex.IsMatch(bad1, "name=\"emailClient\" value=\"true\" checked"));
    var auto = await tickets.AddNoteAsync(tid, tA.Id, "System", "status → In progress", true);
    var bad2 = WebUtility.HtmlDecode(await (await PostForm(owner, TicketUrl(tA.Id) + "?handler=EmailNote",
        new() { ["id"] = tA.Id.ToString(), ["noteId"] = auto!.Id.ToString(), ["emailTo"] = "jane@acme.test" })).Content.ReadAsStringAsync());
    Check("an automatic line posted to Email to the client is refused", bad2.Contains("Only a comment can be emailed"));
    var bad3 = WebUtility.HtmlDecode(await (await PostForm(owner, TicketUrl(tB.Id) + "?handler=EmailNote",
        new() { ["id"] = tB.Id.ToString(), ["noteId"] = ordered.Id.ToString(), ["emailTo"] = "jane@acme.test" })).Content.ReadAsStringAsync());
    Check("another ticket's comment, posted to this ticket's number, is refused", bad3.Contains("not on this ticket"));

    Head("the classic page: the same tick box on Update");
    var cp = WebUtility.HtmlDecode(await office.GetStringAsync(TicketUrl(tB.Id)));
    Check("the old-style page has it too (§61: both layouts)", cp.Contains("Also email this to the client"));
    var r3 = WebUtility.HtmlDecode(await (await PostForm(office, TicketUrl(tB.Id) + "?handler=Update", new()
    { ["id"] = tB.Id.ToString(), ["status"] = "0", ["priority"] = "1", ["note"] = "The scanner part is in.", ["emailClient"] = "true", ["emailTo"] = "jane@acme.test" })).Content.ReadAsStringAsync());
    var m3 = await smtp.WaitFor("jane@acme.test", $"[Ticket #{tB.Number}]");
    Check("the office administrator updates the client from it", m3 != null && m3.Body.Contains("The scanner part is in.") && m3.Body.Contains("Bea Office has an update") && m3.Body.Contains("The scanner will not scan."));
    Check("...and is told", r3.Contains("Emailed to jane@acme.test."), Snip(r3, "Note added"));

    // =========================================================== the console
    Head("the console: the same offer, the same sends");
    var con = await Sign(port, "owner@updateco.test", Pw);
    con.Ws.Send(new TicketOpenMsg { TicketId = tA.Id });
    var det = (await con.Next<TicketDetailMsg>(d => d.Detail.Ticket.Id == tA.Id, 10000))?.Detail;
    Check("the console is told it is set up, from which address, and Jane by default", det?.ClientUpdate is { Ready: true, Default: "jane@acme.test" } && det.ClientUpdate.From == Support);
    Check("...the client's people to choose from", det?.ClientUpdate?.Choices.Any(c => c.Address == "ken@acme.test") == true);
    var firstInfo = det?.Notes.FirstOrDefault(n => n.Body == First);
    Check("the comment carries its marker in the portal's words", firstInfo?.Emailed.Any(e => e.StartsWith("Emailed to jane@acme.test by Tess Tech")) == true);
    Check("a comment may be emailed, an automatic line may not", firstInfo?.CanEmail == true && det!.Notes.Any(n => n.IsSystem && !n.CanEmail));
    con.Ws.Send(new TicketNoteAddMsg { TicketId = tA.Id, Body = "From the console: we will be there at 10.", EmailTo = "ken@acme.test" });
    var m4 = await smtp.WaitFor("ken@acme.test", $"[Ticket #{tA.Number}]");
    Check("a comment posted from the console with an address is emailed", m4 != null && m4.Body.Contains("we will be there at 10") && m4.Body.Contains("Olga Owner has an update"));
    var ok4 = await con.Next<TicketNoticeMsg>(n => n.Message.StartsWith("Emailed to ken@acme.test"), 8000);
    Check("...and the console is told", ok4 != null);
    var n0 = (await Notes(options, tA.Id)).Count;
    con.Ws.Send(new TicketNoteAddMsg { TicketId = tA.Id, Body = "Should not be posted", EmailTo = "nope" });
    var no5 = await con.Next<TicketNoticeMsg>(n => n.Error.Contains("has not been posted"), 8000);
    await Task.Delay(500);
    Check("a bad address from the console: refused, and nothing posted", no5 != null && (await Notes(options, tA.Id)).Count == n0);
    var janeMails = smtp.CountTo("jane@acme.test");
    con.Ws.Send(new TicketEmailNoteMsg { TicketId = tA.Id, NoteId = ordered.Id, To = "jane@acme.test" });
    var m6 = await smtp.WaitFor("jane@acme.test", "[Ticket #", 8000, after: janeMails);
    Check("Email to the client from the console", m6 != null && m6.Body.Contains("Ordered from Rectron"));

    // =========================================================== the reply
    Head("Jane answers — onto the SAME ticket, and the people on it are flagged");
    // Olga sent the last update; Tess is on the ticket. Both must hear of the answer.
    imap.Deliver(Support, Mime("Jane Smith <jane@acme.test>", Support, $"Re: [Ticket #{tA.Number}] Printer",
        "Thursday is fine, thank you.\n\nOn Fri, 9 Oct 2026, Update Co <support@updateco.test> wrote:\n> A new drum is on order", null, ("References", m1.Header("Message-ID"))));
    var replyNote = await WaitNote(options, tA.Id, "Thursday is fine");
    Check("her reply is on the ticket, without the history under it", replyNote != null && !replyNote.Body.Contains("A new drum is on order"));
    Check("...marked as the customer's", replyNote is { FromCustomer: true, IsSystem: false } && replyNote.Author == "Jane Smith (by email)");
    Check("no new ticket was opened", await Count(options, tid, t => t.Subject.Contains("Printer")) == 1);
    Check("Tess, who is on the ticket, is flagged", await OpenFlags(options, tid, "Tess Tech") == 1);
    Check("Olga, who sent the last update, is flagged", await OpenFlags(options, tid, "Olga Owner") == 1);
    Check("Bea, neither, is not", await OpenFlags(options, tid, "Bea Office") == 0);
    var badge = await con.Next<MentionCountMsg>(m => m.Count == 1, 8000);
    Check("Olga's console badge moves at once", badge != null);

    Head("opening the ticket is seeing it");
    var seen = WebUtility.HtmlDecode(await tech.GetStringAsync(TicketUrl(tA.Id)));
    Check("Tess's page says Jane replied", seen.Contains("Jane Smith replied by email"));
    Check("...and her reply is marked as theirs", seen.Contains("Their reply, by email"));
    var replyBlock = Between(seen, "Thursday is fine, thank you.", "tk-item") ?? "";
    Check("...and it offers NO Email to the client on the customer's own words", replyBlock.Length > 0 && !replyBlock.Contains("Email to the client"), replyBlock.Length.ToString());
    Check("having looked, Tess's flag is gone", await OpenFlags(options, tid, "Tess Tech") == 0);
    Check("...Olga's is not — she has not looked", await OpenFlags(options, tid, "Olga Owner") == 1);
    con.Ws.Send(new TicketOpenMsg { TicketId = tA.Id });
    var det2 = (await con.Next<TicketDetailMsg>(d => d.Detail.Ticket.Id == tA.Id && d.Detail.RepliedBy.Count > 0, 10000))?.Detail;
    Check("Olga's console says Jane replied — told apart from 'asked you something'", det2?.RepliedBy.Contains("Jane Smith") == true && det2.AskedBy.Count == 0);
    Check("...her reply marked as the customer's on the console too", det2?.Notes.Any(n => n.FromCustomer && !n.CanEmail) == true);
    await Task.Delay(800);
    Check("opened in the console, Olga's flag is gone", await OpenFlags(options, tid, "Olga Owner") == 0);

    Head("who else may answer on the ticket");
    imap.Deliver(Support, Mime("The Boss <boss@elsewhere.test>", Support, $"Re: [Ticket #{tA.Number}] Printer", "Please invoice us for the drum.", null, ("References", m2.Header("Message-ID"))));
    Check("the address an update was typed to may answer, though not on file", await WaitNote(options, tA.Id, "Please invoice us for the drum") != null);
    imap.Deliver(Support, Mime("Mallory <mallory@else.test>", Support, $"Re: [Ticket #{tA.Number}] Printer", "Send me the invoice instead."));
    await Task.Delay(6000);
    Check("a stranger typing the number does NOT write on it", (await Notes(options, tA.Id)).All(n => !n.Body.Contains("Send me the invoice instead")));

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
    await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "client-update-2026" });
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
    public string Header(string name) => name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase) ? "<" + _m.MessageId + ">"
        : name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) ? "<" + _m.InReplyTo + ">"
        : _m.Headers[name] ?? "";
}
