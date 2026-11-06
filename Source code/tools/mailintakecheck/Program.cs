// =====================================================================================
// mailintakecheck - proves tickets by email and the contact form (stage 3.8.4, 2026-10-05).
//
//   "someone sends an email to support@anywhereit.co.za, it automatically creates a ticket using the
//    email address, subject and body ... check the email address against the client database, and
//    automatically link to the client if the email address exists. The same would work from the
//    contact form. The system should then auto reply to the email address given with the ticket
//    reference number and a short message saying a ticket has been opened."
//
// His answers: ANY mail host, with the problems explained; an unknown sender opens with NO client;
// the client known but the person not → SAY so and suggest adding them as a contact first.
//
// Three halves:
//   THE RULES, in-process — what a message says (automatic? spam? who?), quoted replies cut, the
//     ticket number found again, a failure explained in words.
//   WHO IS IT, in-process against a real database — contact, client, ambiguous, the domain
//     suggestion, free mail and the company's own domain never suggesting anybody.
//   THE WIRE, on a platform of its own, with a FAKE IMAP SERVER and a FAKE SMTP SERVER inside this
//     harness: the portal page saved and tested as the owner would, the real minute-timer reading
//     real mail, the real auto-reply caught as the bytes that would have left, forwarding through
//     the platform's own mailbox with its confirmation link, and the contact form posted as a
//     browser posts it. The IMAP server records every command, so "nothing in your mailbox is
//     changed" is checked rather than claimed.
//
//   dotnet run -c Debug --project tools/mailintakecheck/mailintakecheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
var dataDir = Path.Combine(Path.GetTempPath(), "rs-mailintakecheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
var baseUrl = $"http://127.0.0.1:{port}";
var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

const string Pw = "mail-intake-2026";
const string Slug = "mail-test-co", FwdSlug = "fwd-co";
const string Support = "support@mailco.test", SupportPw = "right-pw";
const string Platform = "tickets@platform.test", PlatformPw = "fwd-pw", PlatformFrom = "noreply@platform.test";
const string FwdAddress = "help@fwdco.test";

using var imap = new FakeImap();
using var smtp = new FakeSmtp();
imap.Start(); smtp.Start();
imap.AddUser(Support, SupportPw); imap.AddUser(Platform, PlatformPw);
smtp.AddUser(Support, SupportPw);
Console.WriteLine($"mailintakecheck — tickets by email; a platform on port {port}, IMAP on {imap.Port}, SMTP on {smtp.Port}");

Process? server = null;
try
{
    // =========================================================== the rules
    Head("what a message says");
    var ooo = MailWire.Read(Mime("Jane <jane@acme.test>", Support, "Away", "I am away", h: ("Auto-Submitted", "auto-replied")));
    Check("an out-of-office (Auto-Submitted) is a machine's", ooo.Automatic.Contains("automatic reply"), ooo.Automatic);
    var ooo2 = MailWire.Read(Mime("Jane <jane@acme.test>", Support, "Automatic reply: Printer", "I am away"));
    Check("...so is Outlook's 'Automatic reply:' with no header at all", ooo2.Automatic.Length > 0);
    var bounce = MailWire.Read(Mime("Mail Delivery System <MAILER-DAEMON@mx.test>", Support, "Undelivered Mail", "gone"));
    Check("a bounce is a machine's", bounce.Automatic.Contains("delivery failure"), bounce.Automatic);
    var news = MailWire.Read(Mime("News <news@shop.test>", Support, "Our sale", "buy", h: ("List-Unsubscribe", "<mailto:u@shop.test>")));
    Check("a newsletter is not a person writing", news.Automatic.Contains("newsletter"), news.Automatic);
    var spam = MailWire.Read(Mime("X <x@spam.test>", Support, "Win", "money", h: ("X-Spam-Flag", "YES")));
    Check("the mail server's spam mark is honoured", spam.Spam);
    var person = MailWire.Read(Mime("Jane Smith <Jane@Acme.test>", Support, "Printer", "It jams.", h: ("Delivered-To", Platform)));
    Check("a person writing is nobody's machine", person.Automatic.Length == 0 && !person.Spam);
    Check("their address, lower-cased, and their name", person.FromAddress == "jane@acme.test" && person.FromName == "Jane Smith");
    Check("the delivery headers a forward leaves are read as recipients", person.Recipients.Contains(Platform) && person.Recipients.Contains(Support), string.Join(", ", person.Recipients));
    var html = MailWire.Read(MimeHtml("Jane <jane@acme.test>", Support, "Html", "<p>Hello <b>there</b></p><p>Line&nbsp;two &amp; more</p><script>x()</script>"));
    Check("an HTML-only email becomes readable text", html.Text.Contains("Hello there") && html.Text.Contains("Line two & more") && !html.Text.Contains("<") && !html.Text.Contains("x()"), html.Text.Replace("\n", " | "));
    var supp = MailWire.Read(Mime("A <a@b.test>", Support, "Hi", "x", h: ("X-Auto-Response-Suppress", "All")));
    Check("a message that asks not to be answered automatically is marked so", supp.NoAutoReply && supp.Automatic.Length == 0);

    Head("who is the person");
    var robot = new IncomingMail { FromAddress = "wordpress@mailco-site.test", ReplyToAddress = "jane@acme.test", ReplyToName = "Jane" };
    Check("a website form robot's mail is the customer in its Reply-To", TicketIntake.RequesterOf(robot, Support).address == "jane@acme.test");
    var ownDomainRobot = new IncomingMail { FromAddress = "website@mailco.test", ReplyToAddress = "kim@kim.test" };
    Check("...so is mail from the company's own domain with a Reply-To", TicketIntake.RequesterOf(ownDomainRobot, Support).address == "kim@kim.test");
    var realPerson = new IncomingMail { FromAddress = "jane@acme.test", ReplyToAddress = "accounts@acme.test" };
    Check("a person's own Reply-To does not replace them", TicketIntake.RequesterOf(realPerson, Support).address == "jane@acme.test");
    Check("no-reply addresses are robots", TicketIntake.IsRobot("noreply@x.test") && TicketIntake.IsRobot("do-not-reply@x.test") && !TicketIntake.IsRobot("norman@x.test"));

    Head("a reply without the history under it");
    var gmail = "Thanks, that worked.\n\nOn Mon, 5 Oct 2026 at 10:00, Support <support@mailco.test> wrote:\n> Hello Jane\n> We have opened ticket #4";
    Check("Gmail's 'On … wrote:' and everything under it goes", TicketIntake.StripQuoted(gmail) == "Thanks, that worked.", TicketIntake.StripQuoted(gmail));
    var wrapped = "Still broken.\n\nOn Mon, 5 Oct 2026 at 10:00, Anywhere IT Support Desk\n<support@mailco.test> wrote:\n> old";
    Check("...even when the line wraps", TicketIntake.StripQuoted(wrapped) == "Still broken.", TicketIntake.StripQuoted(wrapped));
    var outlook = "Done, thank you.\n\n________________________________\nFrom: Support <support@mailco.test>\nSent: Monday, 5 October 2026 10:00\nTo: Jane\nSubject: [Ticket #4]";
    Check("Outlook's From/Sent block goes", TicketIntake.StripQuoted(outlook) == "Done, thank you.", TicketIntake.StripQuoted(outlook));
    var allQuoted = "> only quoted\n> nothing new";
    Check("a reply that is nothing but quotes is kept whole rather than lost", TicketIntake.StripQuoted(allQuoted) == allQuoted);

    Head("the ticket an email answers");
    Check("[Ticket #12] in the subject", TicketIntake.TicketNumberIn(new IncomingMail { Subject = "Re: [Ticket #12] Printer" }, 1) == 12);
    Check("our Message-ID in References, when they rewrote the subject",
        TicketIntake.TicketNumberIn(new IncomingMail { Subject = "Re: printer", References = new() { "rsp.7.33.abc123@mailco.test" } }, 7) == 33);
    Check("...but never another company's", TicketIntake.TicketNumberIn(new IncomingMail { Subject = "Re: printer", References = new() { "rsp.8.33.abc123@x.test" } }, 7) == 0);
    Check("the reply's subject carries the number once, however often it is answered",
        TicketIntake.ReplySubject(12, "Re: [Ticket #12] Printer") == "[Ticket #12] Re: Printer", TicketIntake.ReplySubject(12, "Re: [Ticket #12] Printer"));

    Head("a failure, in words a company can act on");
    var gm = new MailServer("imap.gmail.com", 993, "ssl", "support@mailco.test", "x");
    var (gw, glogin) = MailWire.Explain(new AuthenticationException("bad"), gm, false);
    Check("Google refusing a password says App password, and stops checking", gw.Contains("App password") && glogin, gw[..Math.Min(90, gw.Length)]);
    var ms = new MailServer("outlook.office365.com", 993, "ssl", "a@b.test", "x");
    Check("Microsoft refusing says forwarding instead", MailWire.Explain(new AuthenticationException("bad"), ms, false).words.Contains("forwarding"));
    var cp = new MailServer("mail.mailco.test", 993, "ssl", "support@mailco.test", "x");
    var (cw, clogin) = MailWire.Explain(new AuthenticationException("bad"), cp, false);
    Check("any other host refusing says the full address and the mailbox's own password, and stops", cw.Contains("FULL email address") && cw.Contains("block") && clogin);
    Check("a server that does not exist says so", MailWire.Explain(new SocketException((int)SocketError.HostNotFound), cp, false).words.StartsWith("There is no server called mail.mailco.test"));
    var (tw, tlogin) = MailWire.Explain(new TimeoutException(), cp, false);
    Check("a server that does not answer is not a password problem — checking carries on", tw.Contains("did not answer") && !tlogin);

    Head("the contact form's rules");
    var fs = new TicketIntakeSettings { FormPhone = "required" };
    Check("a name is needed", TicketIntake.CheckForm(fs, new("", "a@b.test", "0825551234", "", "", "hi", "")).Contains("name"));
    Check("an email address is needed — the reply goes there", TicketIntake.CheckForm(fs, new("Ann", "", "0825551234", "", "", "hi", "")).Contains("email"));
    Check("a phone, when the company asks for one", TicketIntake.CheckForm(fs, new("Ann", "a@b.test", "", "", "", "hi", "")).Contains("phone"));
    Check("all there is accepted", TicketIntake.CheckForm(fs, new("Ann", "a@b.test", "082 555 1234", "", "", "hi", "")) == "");

    // =========================================================== who is it
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:PublicUrl"] = baseUrl }).Build();
    var dp = DataProtectionProvider.Create("mailintakecheck");
    var tenants = new TenantService(factory, cfg, dataDir, dp);
    var (tenant, _) = await tenants.CreateAsync("Mail Test Co", Slug, 7);
    var (fwdTenant, _) = await tenants.CreateAsync("Forward Co", FwdSlug, 7);
    int tid = tenant!.Id, fid = fwdTenant!.Id;
    await tenants.AddTechnicianAsync(tid, "owner@mailco.test", Pw, "Olga Owner", TenantRole.Owner | TenantRole.Admin);
    await tenants.AddTechnicianAsync(tid, "tech@mailco.test", Pw, "Tess Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(fid, "owner@fwdco.test", Pw, "Fred Forward", TenantRole.Owner | TenantRole.Admin);

    var clients = new ClientService(factory, dp);
    var contacts = new ContactService(factory);
    async Task<int> Client(string name, string email = "") { var (c, e) = await clients.CreateAsync(tid, name, "", "", email, "", ""); if (c == null) throw new Exception(e); return c.Id; }
    async Task Person(int client, string name, string email) { var (c, e) = await contacts.CreateAsync(tid, client, name, "", email, "", ""); if (c == null) throw new Exception(e); }
    var acme = await Client("Acme Ltd", "info@acme.test");
    await Person(acme, "Jane Smith", "jane@acme.test");
    await Client("Beta Corp", "accounts@beta.test");
    await Client("Gamma One", "office@gamma.test");
    await Client("Gamma Two", "office@gamma.test");
    var delta = await Client("Delta Co"); await Person(delta, "Dan", "dan@delta.test");
    var deltaB = await Client("Delta Branch"); await Person(deltaB, "Dora", "dora@delta.test");
    foreach (var n in new[] { "Isp One", "Isp Two", "Isp Three" }) await Person(await Client(n), n + " person", n.Replace(" ", "").ToLower() + "@bigisp.test");
    await Client("Freddie Gmail", "freddie@gmail.com");

    var platformMail = new PlatformMailService(factory, dp, NullLogger<PlatformMailService>.Instance);
    var intake = new TicketIntake(factory, new TicketService(factory), contacts, platformMail, tenants, dp, cfg,
        new MicrosoftSignIn(platformMail, dp, cfg, NullLogger<MicrosoftSignIn>.Instance), NullLogger<TicketIntake>.Instance);

    Head("who is it — Clint's three answers");
    var m1 = await intake.MatchAsync(tid, "Jane@Acme.test", Support);
    Check("a contact's address links the client AND the person", m1.ClientId == acme && m1.ContactName == "Jane Smith" && m1.ContactId != null, m1.Note);
    var m2 = await intake.MatchAsync(tid, "info@acme.test", Support);
    Check("a client's own address links the client", m2.ClientId == acme && m2.ContactId == null, m2.Note);
    var m3 = await intake.MatchAsync(tid, "office@gamma.test", Support);
    Check("an address on two clients links neither, and says so", m3.ClientId == null && m3.Suggested.Count == 2 && m3.Note.Contains("2 clients"), m3.Note);
    var m4 = await intake.MatchAsync(tid, "bob@acme.test", Support);
    Check("answer 3: the client is known by the domain, the person is not — unlinked, with Acme suggested",
        m4.ClientId == null && m4.Suggested.Count == 1 && m4.Suggested[0].id == acme && m4.Note.Contains("acme.test belongs to Acme Ltd") && m4.Note.Contains("Add them as a contact"), m4.Note);
    var m5 = await intake.MatchAsync(tid, "x@delta.test", Support);
    Check("a domain on two clients suggests both, to choose from", m5.ClientId == null && m5.Suggested.Count == 2, m5.Note);
    var m6 = await intake.MatchAsync(tid, "y@bigisp.test", Support);
    Check($"a domain on {TicketIntake.SharedDomainAt} clients is an internet provider's — nobody is suggested", m6.ClientId == null && m6.Suggested.Count == 0, m6.Note);
    var m7 = await intake.MatchAsync(tid, "stranger@gmail.com", Support);
    Check("free mail never suggests a client, though one client uses Gmail", m7.ClientId == null && m7.Suggested.Count == 0 && m7.Note.Contains("not on file"), m7.Note);
    var m8 = await intake.MatchAsync(tid, "newguy@mailco.test", Support);
    Check("the company's own domain never suggests a client", m8.Suggested.Count == 0);
    var m9 = await intake.MatchAsync(tid, "nobody@else.test", Support, "beta corp");
    Check("the form's Company box naming a client suggests it", m9.ClientId == null && m9.Suggested.Count == 1 && m9.Note.Contains("Beta Corp"), m9.Note);
    var m10 = await intake.MatchAsync(tid, "nobody@else.test", Support);
    Check("answer 2: an unknown sender is linked to nothing", m10.ClientId == null && m10.Suggested.Count == 0);

    // =========================================================== the wire
    Head("the platform, the owner's portal page");
    async Task<string> Cli(Dictionary<string, string>? env, params string[] args)
    {
        var p = Psi(); p.ArgumentList.Add("--no-build"); p.ArgumentList.Add("--");
        foreach (var a in args) p.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) p.Environment[k] = v;
        var pr = Process.Start(p)!; var o = await pr.StandardOutput.ReadToEndAsync(); await pr.WaitForExitAsync(); return o;
    }
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
    var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var built = await build.StandardOutput.ReadToEndAsync(); await build.WaitForExitAsync();
    if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + built); return 1; }
    var pm = await Cli(null, "platform-mail", "set", "--host", "127.0.0.1", "--port", smtp.Port.ToString(), "--nossl", "--from", PlatformFrom, "--from-name", "The Remote Support Program");
    Check("the platform's own mailbox is set from the command line", pm.Contains("Saved"), pm.Trim());
    var fm = await Cli(new() { ["RSP_INTAKE_PASSWORD"] = PlatformPw }, "forwarding-mailbox", "set", "--address", Platform, "--host", "127.0.0.1",
        "--port", imap.Port.ToString(), "--security", "ssl", "--user", Platform);
    Check("...and the forwarding mailbox, its password from the environment", fm.Contains("Saved") && fm.Contains("Password updated"), fm.Trim().Split('\n').Last());
    Check("...tested as it is saved, so a wrong one is known at once", fm.Contains("Tested: Signed in to 127.0.0.1"), fm.Trim().Split('\n').Last());
    var show = await Cli(null, "forwarding-mailbox", "show");
    Check("`forwarding-mailbox show` never prints the password", show.Contains("stored") && !show.Contains(PlatformPw));

    var psi = Psi(); psi.ArgumentList.Add("--no-build"); psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

    var owner = await Portal(port, "owner@mailco.test");
    var page = await owner.GetStringAsync("/portal/mailintake");
    Check("the owner's portal has the page", page.Contains("<h1>Tickets by email and form</h1>"));
    Check("...in the menu", page.Contains("href=\"/portal/mailintake\""));
    Check("...offering forwarding to the platform's address", page.Contains(Platform));
    var tech = await Portal(port, "tech@mailco.test");
    var techPage = await tech.GetStringAsync("/portal/mailintake");
    Check("a technician cannot open it (it holds a mailbox password) — by its heading, not a status code (§34)", !techPage.Contains("<h1>Tickets by email and form</h1>"));

    Dictionary<string, string> EmailForm(string password, string sendVia = "own", bool same = true, string smtpPw = "") => new()
    {
        ["emailEnabled"] = "true", ["address"] = Support, ["way"] = "mailbox",
        ["imapHost"] = "127.0.0.1", ["imapPort"] = imap.Port.ToString(), ["imapSecurity"] = "ssl", ["imapUser"] = Support, ["imapPassword"] = password, ["folder"] = "INBOX",
        ["autoReply"] = "true", ["sendVia"] = sendVia, ["smtpSameLogin"] = same ? "true" : "false",
        ["smtpHost"] = "127.0.0.1", ["smtpPort"] = smtp.Port.ToString(), ["smtpSecurity"] = "ssl", ["smtpUser"] = Support, ["smtpPassword"] = smtpPw,
        ["autoReplyText"] = TicketIntake.DefaultReply
    };

    Head("a wrong password: said once, in words, and NOT retried");
    imap.Deliver(Support, Mime("Old Mail <old@else.test>", Support, "Already here 1", "old"));
    imap.Deliver(Support, Mime("Old Mail <old@else.test>", Support, "Already here 2", "old"));
    var wrong = WebUtility.HtmlDecode(await (await PostForm(owner, "/portal/mailintake?handler=Email", EmailForm("WRONG"))).Content.ReadAsStringAsync());
    Check("the test says the server refused the username or password", wrong.Contains("refused the username or password"), Snip(wrong, "refused"));
    Check("...and that checking has stopped", wrong.Contains("Checking has stopped") && wrong.Contains(">stopped<"));
    var tries = imap.Logins(Support);
    await Task.Delay(7000);
    Check("three timer ticks later it has not tried again (cPanel would block our server)", imap.Logins(Support) == tries, $"{tries} then {imap.Logins(Support)} sign-ins");

    Head("the right password: Save and test");
    var right = WebUtility.HtmlDecode(await (await PostForm(owner, "/portal/mailintake?handler=Email", EmailForm(""))).Content.ReadAsStringAsync());
    Check("a blank password box keeps... the WRONG one, so it still fails — nothing is guessed", right.Contains("refused the username or password"));
    right = WebUtility.HtmlDecode(await (await PostForm(owner, "/portal/mailintake?handler=Email", EmailForm(SupportPw))).Content.ReadAsStringAsync());
    Check("the test signs in, and counts the mailbox", right.Contains("The test worked") && right.Contains("holds 2 messages"), Snip(right, "Reading:"));
    Check("...signs in to the sending server too", right.Contains("Sending: signed in to 127.0.0.1"), Snip(right, "Sending:"));
    Check("...and says mail already there stays as it is", right.Contains("only mail arriving from now on"));
    await Task.Delay(5000);
    Check("the two old messages did not become tickets", await Count(options, tid, t => t.Subject.StartsWith("Already here")) == 0);

    Head("Jane, a contact at Acme, writes in");
    var janeMid = "<jane-1@acme.test>";
    imap.Deliver(Support, Mime("Jane Smith <jane@acme.test>", Support, "Printer jams", "The printer in accounts jams on every page.", mid: janeMid));
    var janeT = await WaitTicket(options, tid, "Printer jams");
    Check("a ticket opens by itself", janeT != null);
    if (janeT == null) return Done();
    Check("General, from Email, with what she wrote", janeT.Category == TicketCategory.General && janeT.Source == TicketSource.Email && janeT.Description.Contains("jams on every page"));
    Check("linked to Acme Ltd and to Jane", janeT.ClientId == acme && janeT.ContactId != null && janeT.ContactName == "Jane Smith", $"client {janeT.ClientId}, contact {janeT.ContactName}");
    Check("her address is kept on the ticket for replies", janeT.RequesterEmail == "jane@acme.test");
    var reply = await smtp.WaitFor("jane@acme.test", $"[Ticket #{janeT.Number}]");
    Check("an auto-reply reaches her", reply != null, reply?.Subject ?? "nothing arrived");
    if (reply != null)
    {
        Check("from the company's own address, through its own server and login", reply.FromAddress == Support && reply.AuthUser == Support, $"{reply.FromAddress}, signed in as {reply.AuthUser}");
        Check("in the company's name", reply.FromName == "Mail Test Co");
        Check("subject: [Ticket #N] and her subject", reply.Subject == $"[Ticket #{janeT.Number}] Printer jams", reply.Subject);
        Check("the words say the number and a ticket is open", reply.Body.Contains($"ticket #{janeT.Number}") && reply.Body.Contains("Hello Jane Smith"));
        Check("it says it is automatic (RFC 3834), so an out-of-office does not answer it back", reply.Header("Auto-Submitted") == "auto-replied");
        Check("it answers her message (In-Reply-To), so it threads in her mail", reply.Header("In-Reply-To") == janeMid);
        Check("its Message-ID carries the ticket", Regex.IsMatch(reply.Header("Message-ID"), $@"<rsp\.{tid}\.{janeT.Number}\."), reply.Header("Message-ID"));
    }

    Head("she replies — onto the SAME ticket");
    var before = await Count(options, tid, _ => true);
    var sentBefore = smtp.Count;
    imap.Deliver(Support, Mime("Jane Smith <jane@acme.test>", Support, $"Re: [Ticket #{janeT.Number}] Printer jams",
        $"It is page 2 only, actually.\n\nOn Mon, 5 Oct 2026, Mail Test Co <{Support}> wrote:\n> Hello Jane\n> ticket #{janeT.Number}",
        h: ("References", reply?.Header("Message-ID") ?? "")));
    var note = await WaitNote(options, janeT.Id, "page 2 only");
    Check("her reply is on the ticket, in her name", note != null && note.Author.StartsWith("Jane Smith") && !note.IsSystem, note?.Author ?? "no note");
    Check("...without the history under it", note != null && !note.Body.Contains("Hello Jane"), note?.Body ?? "");
    Check("no second ticket", await Count(options, tid, _ => true) == before);
    await Task.Delay(2500);
    Check("and no auto-reply to a reply", smtp.Count == sentBefore);

    Head("somebody else types her ticket number");
    imap.Deliver(Support, Mime("Eve <eve@else.test>", Support, $"[Ticket #{janeT.Number}] add this", "Please also send me the invoice."));
    var eveT = await WaitTicket(options, tid, $"[Ticket #{janeT.Number}] add this");
    Check("it is NOT added to Jane's ticket — it opens its own", eveT != null && eveT.Id != janeT.Id);
    Check("...whose story says why", eveT != null && (await Notes(options, eveT.Id)).Any(n => n.Body.Contains("came from a different address")));

    Head("Bob at Acme — the client known, the person not (Clint's answer 3)");
    imap.Deliver(Support, Mime("Bob Brown <bob@acme.test>", Support, "New laptop", "Can you set up my new laptop?"));
    var bobT = await WaitTicket(options, tid, "New laptop");
    Check("a ticket opens, NOT linked", bobT != null && bobT.ClientId == null);
    if (bobT != null)
    {
        Check("its story says acme.test belongs to Acme Ltd, and to add him as a contact", (await Notes(options, bobT.Id)).Any(n => n.Body.Contains("acme.test belongs to Acme Ltd") && n.Body.Contains("Add them as a contact")));
        var tp = WebUtility.HtmlDecode(await owner.GetStringAsync($"/portal/tickets/{bobT.Id}"));
        Check("the ticket page says so at the top, with the button", tp.Contains("acme.test belongs to Acme Ltd") && tp.Contains("Add as a contact at Acme Ltd and link this ticket"));
        Check("...with his name filled in from the email", tp.Contains("name=\"contactName\" value=\"Bob Brown\""));
        var added = await PostForm(owner, $"/portal/tickets/{bobT.Id}?handler=AddSender", new() { ["id"] = bobT.Id.ToString(), ["clientId"] = acme.ToString(), ["contactName"] = "Bob Brown" });
        var after = await Ticket(options, bobT.Id);
        Check("one press: he is a contact at Acme, and the ticket is linked to both", after.ClientId == acme && after.ContactId != null && after.ContactName == "Bob Brown");
        await using (var db = new PlatformDb(options))
            Check("...with his email address on the contact", await db.Contacts.AnyAsync(c => c.ClientId == acme && c.Name == "Bob Brown" && c.Email == "bob@acme.test"));
        var tp2 = WebUtility.HtmlDecode(await owner.GetStringAsync($"/portal/tickets/{bobT.Id}"));
        Check("the notice has gone", !tp2.Contains("and link this ticket"));
    }
    imap.Deliver(Support, Mime("Bob Brown <bob@acme.test>", Support, "Second question", "And the printer?"));
    var bob2 = await WaitTicket(options, tid, "Second question");
    Check("his NEXT email links by itself", bob2 != null && bob2.ClientId == acme && bob2.ContactName == "Bob Brown");

    Head("a stranger, and a contact added later");
    imap.Deliver(Support, Mime("Sam Stranger <sam@stranger.test>", Support, "Quote please", "How much for a server?"));
    var samT = await WaitTicket(options, tid, "Quote please");
    Check("answer 2: an unknown sender's ticket opens with no client", samT != null && samT.ClientId == null);
    if (samT != null)
    {
        var sp = WebUtility.HtmlDecode(await owner.GetStringAsync($"/portal/tickets/{samT.Id}"));
        Check("the page says not on file, with no button to add", sp.Contains("sam@stranger.test is not on file") && !sp.Contains("and link this ticket"));
        var stranger = await Client("Stranger Things Ltd"); await Person(stranger, "Sam Stranger", "sam@stranger.test");
        await owner.GetStringAsync($"/portal/tickets/{samT.Id}");
        var linked = await Ticket(options, samT.Id);
        Check("put on file elsewhere, the waiting ticket links itself the next time it is opened", linked.ClientId == stranger && linked.ContactName == "Sam Stranger");
    }

    Head("left alone, every one with the reason on the page");
    var sentNow = smtp.Count;
    imap.Deliver(Support, Mime("Jane Smith <jane@acme.test>", Support, "Out of office", "Back Monday", h: ("Auto-Submitted", "auto-replied")));
    imap.Deliver(Support, Mime("Shop <news@shop.test>", Support, "Spring sale", "50% off", h: ("List-Unsubscribe", "<mailto:x@shop.test>")));
    imap.Deliver(Support, Mime("MAILER-DAEMON <mailer-daemon@mx.test>", Support, "Undelivered", "It bounced"));
    imap.Deliver(Support, Mime("Prize <win@spam.test>", Support, "You won", "Click", h: ("X-Spam-Flag", "YES")));
    imap.Deliver(Support, Mime($"Mail Test Co <{Support}>", Support, "FW: from our own address", "Forwarded by a rule"));
    imap.Deliver(Support, Mime("Shop <noreply@shop.test>", Support, "Your order", "Shipped"));
    await WaitLog(options, tid, "Your order");
    var skipped = await Log(options, tid);
    Check("an out-of-office", skipped.Any(x => x.Subject == "Out of office" && x.Outcome == "skipped" && x.Reason.Contains("automatic reply")));
    Check("a newsletter", skipped.Any(x => x.Subject == "Spring sale" && x.Outcome == "skipped" && x.Reason.Contains("newsletter")));
    Check("a bounce", skipped.Any(x => x.Subject == "Undelivered" && x.Outcome == "skipped" && x.Reason.Contains("bounce")));
    Check("spam", skipped.Any(x => x.Subject == "You won" && x.Outcome == "skipped" && x.Reason.Contains("spam")));
    Check("mail FROM the support address itself (a loop, or an Outlook forward) — and it says how to forward properly",
        skipped.Any(x => x.Subject.StartsWith("FW: from our own") && x.Reason.Contains("Forwarders")));
    Check("a no-reply sender", skipped.Any(x => x.Subject == "Your order" && x.Reason.Contains("no-reply")));
    Check("none of them became a ticket", await Count(options, tid, t => new[] { "Out of office", "Spring sale", "Undelivered", "You won", "Your order" }.Contains(t.Subject) || t.Subject.StartsWith("FW: from our own")) == 0);
    Check("none of them was answered", smtp.Count == sentNow);
    var logPage = WebUtility.HtmlDecode(await owner.GetStringAsync("/portal/mailintake"));
    Check("the page's list shows them, with their reasons", logPage.Contains("Spring sale") && logPage.Contains("left alone") && logPage.Contains("newsletter"));

    Head("a website's own contact form, emailing support@");
    imap.Deliver(Support, Mime("WordPress <wordpress@acme-site.test>", Support, "New contact form submission", "From: Jane\nMessage: the scanner is offline",
        h: ("Reply-To", "Jane Smith <jane@acme.test>")));
    var wpT = await WaitTicket(options, tid, "New contact form submission");
    Check("the customer in its Reply-To is who the ticket is for — linked to Acme", wpT != null && wpT.RequesterEmail == "jane@acme.test" && wpT.ClientId == acme);
    Check("...and the reply goes to her, not the robot", await smtp.WaitFor("jane@acme.test", "New contact form submission") != null);

    Head("the same message twice, HTML only, and a flood");
    var dup = Mime("Dup Dan <dan@delta.test>", Support, "Sent twice", "once", mid: "<same-id@delta.test>");
    imap.Deliver(Support, dup); imap.Deliver(Support, dup);
    await WaitTicket(options, tid, "Sent twice");
    await Task.Delay(4500);
    Check("one Message-ID is one ticket, however often it arrives", await Count(options, tid, t => t.Subject == "Sent twice") == 1);
    imap.Deliver(Support, MimeHtml("Dora <dora@delta.test>", Support, "Html only", "<div>Screen is <b>blank</b></div>"));
    var htmlT = await WaitTicket(options, tid, "Html only");
    Check("an HTML-only email's ticket reads as text", htmlT != null && htmlT.Description.Contains("Screen is blank") && !htmlT.Description.Contains("<b>"));
    for (int i = 1; i <= TicketIntake.PerSenderPerHour + 1; i++) imap.Deliver(Support, Mime("Loop <loop@else.test>", Support, $"Flood {i}", "again"));
    await WaitLog(options, tid, $"Flood {TicketIntake.PerSenderPerHour + 1}");
    Check($"one address opens {TicketIntake.PerSenderPerHour} tickets an hour, and no more", await Count(options, tid, t => t.Subject.StartsWith("Flood ")) == TicketIntake.PerSenderPerHour);
    Check("...the next is left in the mailbox, with the reason", (await Log(options, tid)).Any(x => x.Subject == $"Flood {TicketIntake.PerSenderPerHour + 1}" && x.Reason.Contains("in the last hour")));

    Head("nothing in their mailbox was changed");
    var cmds = imap.Commands(Support);
    var writes = cmds.Where(c => c is "SELECT" or "STORE" or "UID STORE" or "EXPUNGE" or "UID EXPUNGE" or "COPY" or "UID COPY" or "MOVE" or "UID MOVE" or "APPEND" or "DELETE").Distinct().ToList();
    Check("only EXAMINE (read-only), never SELECT, STORE, EXPUNGE, COPY or MOVE", cmds.Contains("EXAMINE") && writes.Count == 0, writes.Count == 0 ? $"{cmds.Count} commands" : string.Join(", ", writes));
    Check("messages were fetched with BODY.PEEK, which never marks them read", imap.PeekOnly);

    Head("the reply from OUR mailbox, and when theirs fails");
    await PostForm(owner, "/portal/mailintake?handler=Email", EmailForm("", sendVia: "platform"));
    imap.Deliver(Support, Mime("Dora <dora@delta.test>", Support, "Via platform", "x"));
    var viaP = await smtp.WaitFor("dora@delta.test", "Via platform");
    Check("from our mailbox, in the company's name, with replies to its support address",
        viaP != null && viaP.FromAddress == PlatformFrom && viaP.FromName == "Mail Test Co" && viaP.ReplyTo.Contains(Support), viaP == null ? "nothing" : $"{viaP.From} / Reply-To {viaP.ReplyTo}");
    await PostForm(owner, "/portal/mailintake?handler=Email", EmailForm("", sendVia: "own", same: false, smtpPw: "WRONG-SMTP"));
    imap.Deliver(Support, Mime("Dan <dan@delta.test>", Support, "Own fails", "x"));
    var fb = await smtp.WaitFor("dan@delta.test", "Own fails");
    Check("their server refusing still answers the customer — from ours", fb != null && fb.FromAddress == PlatformFrom);
    var fbLog = (await Log(options, tid)).FirstOrDefault(x => x.Subject == "Own fails");
    Check("...and the page says why", fbLog != null && fbLog.ReplyResult.StartsWith("sent from our mailbox, because yours said"), fbLog?.ReplyResult ?? "");
    await PostForm(owner, "/portal/mailintake?handler=Email", EmailForm(""));

    Head("the console hears an emailed ticket at once");
    var console = await Sign(port, "tech@mailco.test");
    imap.Deliver(Support, Mime("Kim <kim@kimco.test>", Support, "Console should see this", "x"));
    var changed = await console.Next<TicketChangedMsg>(m => m.Ticket.Subject == "Console should see this", 15000);
    Check("a TicketChangedMsg reaches a signed-in console", changed != null);

    Head("forwarding — another company, through the platform's mailbox");
    var fwd = await Portal(port, "owner@fwdco.test");
    var fwdForm = new Dictionary<string, string>
    {
        ["emailEnabled"] = "true", ["address"] = FwdAddress, ["way"] = "forward", ["autoReply"] = "true", ["sendVia"] = "platform",
        ["imapPort"] = "993", ["smtpPort"] = "465", ["autoReplyText"] = ""
    };
    var fwdSaved = WebUtility.HtmlDecode(await (await PostForm(fwd, "/portal/mailintake?handler=Email", fwdForm)).Content.ReadAsStringAsync());
    Check("saving says a confirmation link has gone to the address", fwdSaved.Contains($"emailed a confirmation link to {FwdAddress}"), Snip(fwdSaved, "confirmation"));
    var conf = await smtp.WaitFor(FwdAddress, "Confirm");
    var link = conf == null ? "" : Regex.Match(conf.Body, @"http://127\.0\.0\.1:\d+/mail-confirm\?t=[0-9A-F]+").Value;
    Check("the link arrives at the address", link.Length > 0, conf?.Subject ?? "nothing arrived");
    imap.Deliver(Platform, Mime("Cara Customer <cara@cust.test>", FwdAddress, "Before confirming", "x", h: ("Delivered-To", Platform)));
    await WaitLog(options, 0, "Before confirming");
    var nobodys = await Log(options, 0);
    Check("until it is used, forwarded mail is sorted to nobody", await Count(options, fid, _ => true) == 0
        && nobodys.Any(x => x.Subject == "Before confirming" && x.Reason.Contains("confirmed address")),
        $"{await Count(options, fid, _ => true)} tickets; nobody's log: {string.Join(" / ", nobodys.Select(x => x.Subject + ": " + x.Reason))}");
    var lc = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(baseUrl) };
    var opened = WebUtility.HtmlDecode(await lc.GetStringAsync(new Uri(link).PathAndQuery));
    await using (var db = new PlatformDb(options))
        Check("OPENING the link (as a mail filter does) confirms nothing", opened.Contains("Confirm your support address") && opened.Contains(FwdAddress)
            && !await db.TicketIntake.AnyAsync(s => s.TenantId == fid && s.AddressConfirmed));
    var done = WebUtility.HtmlDecode(await (await PostForm(lc, new Uri(link).PathAndQuery, new() { ["T"] = new Uri(link).Query[3..] })).Content.ReadAsStringAsync());
    Check("pressing Confirm does", done.Contains("<h1>Confirmed</h1>"), Snip(done, "<h1>"));
    var again = WebUtility.HtmlDecode(await lc.GetStringAsync(new Uri(link).PathAndQuery));
    Check("and the link works once — opened again, it says so", again.Contains("This link cannot be used") && !again.Contains(">Confirm</button>"));
    imap.Deliver(Platform, Mime("Cara Customer <cara@cust.test>", FwdAddress, "Forwarded and confirmed", "My email is down.", h: ("Delivered-To", Platform)));
    var caraT = await WaitTicket(options, fid, "Forwarded and confirmed");
    Check("now forwarded mail becomes the right company's ticket", caraT != null && caraT.RequesterEmail == "cara@cust.test");
    Check("...and NOT the other company's", await Count(options, tid, t => t.Subject == "Forwarded and confirmed") == 0);
    var caraReply = await smtp.WaitFor("cara@cust.test", "Forwarded and confirmed");
    Check("her reply comes from our mailbox in Forward Co's name, answering to its address",
        caraReply != null && caraReply.FromName == "Forward Co" && caraReply.ReplyTo.Contains(FwdAddress));
    var squat = WebUtility.HtmlDecode(await (await PostForm(owner, "/portal/mailintake?handler=Email",
        new(EmailForm("")) { ["address"] = FwdAddress, ["way"] = "forward" })).Content.ReadAsStringAsync());
    Check("another company cannot take a confirmed address", squat.Contains("already taking tickets for another company"));
    imap.Deliver(Platform, Mime("Gmail Team <forwarding-noreply@google.com>", Platform, $"(#55512) Gmail Forwarding Confirmation - Receive Mail from {FwdAddress}",
        $"{FwdAddress} has requested to automatically forward mail to your email address {Platform}.\nConfirmation code: 555123456\n\nhttps://mail-settings.google.com/mail/vf-abc123"));
    await WaitLog(options, fid, "Gmail Forwarding Confirmation");
    var fwdPage = WebUtility.HtmlDecode(await fwd.GetStringAsync("/portal/mailintake"));
    Check("Gmail's forwarding confirmation is shown on the right company's page", fwdPage.Contains("Gmail is asking you to confirm") && fwdPage.Contains("555123456"));

    Head("the contact form");
    var formSaved = WebUtility.HtmlDecode(await (await PostForm(owner, "/portal/mailintake?handler=Form", new()
    {
        ["formEnabled"] = "true", ["formHeading"] = "Ask Mail Test Co", ["formIntro"] = "We answer within the hour.",
        ["formPhone"] = "required", ["formCompany"] = "true", ["formThanks"] = ""
    })).Content.ReadAsStringAsync());
    Check("switched on from the portal, with the line to paste", formSaved.Contains($"/contactform/{Slug}.js") && formSaved.Contains($"/contact/{Slug}"));
    var visitor = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(baseUrl) };
    var embed = await visitor.GetAsync($"/contactform/{Slug}.js");
    var embedJs = await embed.Content.ReadAsStringAsync();
    Check("the line serves the script, with the company written in", embed.IsSuccessStatusCode && embedJs.Contains($"\"{Slug}\"") && !embedJs.Contains("__SLUG__"));
    var formGet = await visitor.GetAsync($"/contact/{Slug}?embed=1");
    var formHtml = WebUtility.HtmlDecode(await formGet.Content.ReadAsStringAsync());
    Check("the form shows the company's heading, and asks for the phone", formHtml.Contains("Ask Mail Test Co") && formHtml.Contains("name=\"Phone\"") && formHtml.Contains("required"));
    Check("it may sit in a frame on their website (no DENY)", !formGet.Headers.Contains("X-Frame-Options"));
    Check("the portal's pages still may not", (await owner.GetAsync("/portal/mailintake")).Headers.Contains("X-Frame-Options"));
    Check("it carries no antiforgery token (a third-party cookie Safari throws away)", !formHtml.Contains("__RequestVerificationToken"));
    var opened2 = Regex.Match(formHtml, "name=\"o\" value=\"([^\"]+)\"").Groups[1].Value;
    Dictionary<string, string> FormPost(string name, string email, string phone, string msg, string company = "", string honey = "") => new()
    {
        ["o"] = opened2, ["Name"] = name, ["Email"] = email, ["Phone"] = phone, ["Company"] = company, ["Subject"] = "", ["Message"] = msg, ["Website"] = honey, ["embed"] = "1"
    };
    var fast = WebUtility.HtmlDecode(await (await visitor.PostAsync($"/contact/{Slug}", new FormUrlEncodedContent(FormPost("Speedy Bot", "bot@bots.test", "0825551234", "too fast")))).Content.ReadAsStringAsync());
    Check("sent within three seconds of opening: thanked, and nothing happens (a robot)", fast.Contains("Thank you") && await Count(options, tid, t => t.Description.Contains("too fast")) == 0);
    await Task.Delay(3200);
    var honey = WebUtility.HtmlDecode(await (await visitor.PostAsync($"/contact/{Slug}", new FormUrlEncodedContent(FormPost("Honey Bot", "bot@bots.test", "0825551234", "honey pot", honey: "http://x")))).Content.ReadAsStringAsync());
    Check("the hidden field filled in: thanked, and nothing happens", honey.Contains("Thank you") && await Count(options, tid, t => t.Description.Contains("honey pot")) == 0);
    var noPhone = WebUtility.HtmlDecode(await (await visitor.PostAsync($"/contact/{Slug}", new FormUrlEncodedContent(FormPost("Jane Smith", "jane@acme.test", "", "need help")))).Content.ReadAsStringAsync());
    Check("a missing required phone is asked for, and what they typed is kept", noPhone.Contains("Please give us a phone number") && noPhone.Contains("need help"));
    var sent = WebUtility.HtmlDecode(await (await visitor.PostAsync($"/contact/{Slug}", new FormUrlEncodedContent(FormPost("Jane Smith", "jane@acme.test", "082 555 1234", "The wifi in the boardroom drops out.")))).Content.ReadAsStringAsync());
    var formNo = Regex.Match(sent, @"opened ticket #(\d+)").Groups[1].Value;
    Check("sent: thanked with the ticket number", formNo.Length > 0, Snip(sent, "Thank"));
    Check("...and told a copy was emailed — because it was", sent.Contains("We have emailed you a copy"));
    var formT = await WaitTicket(options, tid, "The wifi in the boardroom drops out.");
    Check("a General ticket from the Form, linked to Acme and Jane", formT != null && formT.Source == TicketSource.Form && formT.ClientId == acme && formT.ContactName == "Jane Smith");
    Check("...with her phone in it", formT != null && formT.Description.Contains("Phone: 082 555 1234"));
    Check("and the same auto-reply", await smtp.WaitFor("jane@acme.test", $"[Ticket #{formNo}]") != null);
    var named = WebUtility.HtmlDecode(await (await visitor.PostAsync($"/contact/{Slug}", new FormUrlEncodedContent(FormPost("Newbie", "newbie@else.test", "0825550000", "Hello from Beta", company: "Beta Corp")))).Content.ReadAsStringAsync());
    var betaT = await WaitTicket(options, tid, "Hello from Beta");
    Check("an unknown person naming a client in Company: unlinked, with that client suggested",
        betaT != null && betaT.ClientId == null && (await Notes(options, betaT.Id)).Any(n => n.Body.Contains("They said they are from Beta Corp")));
    var preview = WebUtility.HtmlDecode(await (await owner.PostAsync($"/contact/{Slug}?preview=1", new FormUrlEncodedContent(FormPost("Owner Preview", "owner@mailco.test", "0825551111", "from the preview")))).Content.ReadAsStringAsync());
    Check("the portal's preview sends nothing", await Count(options, tid, t => t.Description.Contains("from the preview")) == 0);
    await PostForm(owner, "/portal/mailintake?handler=Form", new() { ["formEnabled"] = "false", ["formHeading"] = "Ask", ["formPhone"] = "optional" });
    var off = WebUtility.HtmlDecode(await visitor.GetStringAsync($"/contact/{Slug}"));
    Check("switched off, the form says so instead of taking messages", off.Contains("not taking messages") && !off.Contains("name=\"Message\""));

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
        if (k == "Reply-To") m.ReplyTo.Add(MailboxAddress.Parse(v));
        else if (k == "References") { if (v.Length > 0) m.References.Add(v.Trim('<', '>')); }
        else m.Headers.Add(k, v);
    }
    m.Body = new TextPart("plain") { Text = text };
    return m;
}
static MimeMessage MimeHtml(string from, string to, string subject, string html)
{
    var m = Mime(from, to, subject, "");
    m.Body = new TextPart("html") { Text = html };
    return m;
}

// ---------------------------------------------------------------- the database, read as it stands

static async Task<Ticket?> WaitTicket(DbContextOptions<PlatformDb> o, int tenantId, string subjectOrText, int ms = 20000)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        await using (var db = new PlatformDb(o))
        {
            var t = await db.Tickets.Where(x => x.TenantId == tenantId && (x.Subject == subjectOrText || x.Description.StartsWith(subjectOrText))).OrderBy(x => x.Id).FirstOrDefaultAsync();
            if (t != null) return t;
        }
        await Task.Delay(300);
    }
    return null;
}
static async Task<Ticket> Ticket(DbContextOptions<PlatformDb> o, int id) { await using var db = new PlatformDb(o); return await db.Tickets.FirstAsync(x => x.Id == id); }
static async Task<int> Count(DbContextOptions<PlatformDb> o, int tenantId, System.Linq.Expressions.Expression<Func<Ticket, bool>> where)
{ await using var db = new PlatformDb(o); return await db.Tickets.Where(x => x.TenantId == tenantId).Where(where).CountAsync(); }
static async Task<List<TicketNote>> Notes(DbContextOptions<PlatformDb> o, int ticketId)
{ await using var db = new PlatformDb(o); return await db.TicketNotes.Where(n => n.TicketId == ticketId).ToListAsync(); }
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
static async Task<List<InboundMail>> Log(DbContextOptions<PlatformDb> o, int tenantId)
{ await using var db = new PlatformDb(o); return await db.InboundMail.Where(x => x.TenantId == tenantId).ToListAsync(); }
static async Task WaitLog(DbContextOptions<PlatformDb> o, int tenantId, string subjectPart, int ms = 20000)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        if ((await Log(o, tenantId)).Any(x => x.Subject.Contains(subjectPart))) return;
        await Task.Delay(300);
    }
}

// ---------------------------------------------------------------- the browser and the console

static async Task<HttpResponseMessage> PostForm(HttpClient c, string path, Dictionary<string, string> fields)
{
    // The page is fetched WITHOUT its handler: a GET naming a handler the page does not have runs no handler at all.
    var page = await c.GetStringAsync(Regex.Replace(path, @"[?&]handler=[^&]*", ""));
    fields["__RequestVerificationToken"] = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
        ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
    return await c.PostAsync(path, new FormUrlEncodedContent(fields));
}
static async Task<HttpClient> Portal(int port, string email)
{
    var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true })
    { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(60) };
    await PostForm(c, "/login", new() { ["Email"] = email, ["Password"] = "mail-intake-2026" });
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
static async Task<TechConsole> Sign(int port, string email)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = "mail-intake-2026" });
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

    public async Task<Caught?> WaitFor(string to, string subjectPart, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_mails) { var m = _mails.LastOrDefault(x => x.To.Contains(to.ToLowerInvariant()) && x.Subject.Contains(subjectPart)); if (m != null) return m; }
            await Task.Delay(150);
        }
        return null;
    }
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
    public string Header(string name) => name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase) ? "<" + _m.MessageId + ">"
        : name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) ? "<" + _m.InReplyTo + ">"
        : _m.Headers[name] ?? "";
}
