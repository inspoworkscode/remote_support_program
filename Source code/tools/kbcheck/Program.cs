// =====================================================================================
// kbcheck - the knowledge base (roadmap 3.8.5, 2026-10-05).
//
// Clint: "an option on all user's dashboards to ... access and edit/create in the knowledgebase",
// "once a ticket is completed ... commit to the knowledgebase that then opens it as an editable
// item", "linked to clients", "a General section and clients section". His six answers were "go
// with your recommendations" — each is a section here.
//
// A platform of its own (own port, own database in the temp folder, two companies), driven the
// way people drive it: the portal over HTTP with real cookies and antiforgery tokens, and the
// console over a real /ws/tech socket. The live server on 8090 is not touched.
//
//   the words: everything typed is encoded; steps, headings, commands, pictures, links
//   who may: everybody reads and writes; only a supervisor or the owner deletes
//   a ticket: offered once its work is done, to the people on it and supervisors - never before
//   the write-up: the ticket's words as steps; nothing saved until Save; the ticket is told
//   found without looking: the client's count on its other tickets, its client page, the console
//   every save kept; two people saving the same version; putting a version back
//   pictures: only real pictures, only to this company
//   another company sees nothing, and cannot file an article under its own client
//   the console: the ticket's knowledge line, the count for a machine, the hand-off links
//
//   dotnet run -c Debug --project tools/kbcheck/kbcheck.csproj
// =====================================================================================

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
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
const string Pw = "kb-check-2026";

// ------------------------------------------------------------------ the words, on their own
Console.WriteLine("== the words (KbText) ==");
{
    var h = KbText.Render("<script>alert(1)</script>\n<img src=x onerror=alert(2)>");
    Check("anything typed as HTML is shown, never run", !h.Contains("<script") && !h.Contains("<img src=x") && h.Contains("&lt;script&gt;"), h.Trim());
    h = KbText.Render("1. Open Active Directory\n2. Right-click Users\n3. New → User");
    Check("numbered lines are numbered steps", h.Contains("<ol><li>Open Active Directory</li><li>Right-click Users</li><li>New → User</li></ol>"), h.Trim());
    h = KbText.Render("4. carry on\n5. and on");
    Check("a list that starts at 4 says so", h.Contains("<ol start=\"4\">"), h.Trim());
    h = KbText.Render("1. Run this\n   as an administrator\n2. Then this");
    Check("an indented line under a step belongs to that step", h.Contains("<li>Run this<br>as an administrator</li>"), h.Trim());
    h = KbText.Render("# Before you start\n- Have the user's name\n- And their manager");
    Check("a heading and bullets", h.Contains("<h2>Before you start</h2>") && h.Contains("<ul><li>Have the user&#39;s name</li><li>And their manager</li></ul>"), h.Trim());
    h = KbText.Render("```\nnet user jane.doe /add\n  <b>kept</b>\n```");
    Check("a block of commands keeps its spacing and is not read as markup",
        h.Contains("<pre><code>net user jane.doe /add\n  &lt;b&gt;kept&lt;/b&gt;</code></pre>"), h.Trim());
    h = KbText.Render("Use **Staff** and `gpupdate /force` then");
    Check("bold and inline commands", h.Contains("<strong>Staff</strong>") && h.Contains("<code>gpupdate /force</code>"), h.Trim());
    h = KbText.Render("[image:42]");
    Check("[image:N] on its own line is the picture", h.Contains("<img src=\"/portal/kb/image/42\""), h.Trim());
    h = KbText.Render("See https://learn.microsoft.com/a?b=1&c=2.");
    Check("an address is a link, and the full stop is not part of it",
        h.Contains("<a href=\"https://learn.microsoft.com/a?b=1&amp;c=2\" target=\"_blank\" rel=\"noopener noreferrer\">") && h.Contains("</a>."), h.Trim());
    h = KbText.Render("javascript:alert(1) and https://x.example/\"onmouseover=\"alert(1)");
    Check("only http and https become links, and a quote cannot break out of one",
        !h.Contains("href=\"javascript") && !h.Contains("\"onmouseover") && !h.Contains("onmouseover=\"alert"), h.Trim());
    // Link buttons (Clint, 2026-10-06): "some urls can be extremely long and would look messy".
    const string LongUrl = "https://support.hp.com/za-en/drivers/selfservice/hp-laserjet-pro-m404-m405-printer-series/19202535?sku=W1A53A&x=1";
    h = KbText.Render($"[button: Download the driver | {LongUrl}]");
    Check("[button: words | address] is a button with those words",
        h.Contains("class=\"kb-btn\" href=\"https://support.hp.com/za-en/drivers/selfservice/hp-laserjet-pro-m404-m405-printer-series/19202535?sku=W1A53A&amp;x=1\"")
        && h.Contains(">Download the driver <span"), h.Trim());
    Check("...and the long address is never drawn as words", !h.Contains(">https://"), h.Trim());
    h = KbText.Render("Get it here:\n\nhttps://www.hp.com/za-en/drivers/a/really/long/path?with=a&query=string\n\nThen install it.");
    Check("an address alone on a line is a button named after its site", h.Contains(">Open hp.com <span") && !h.Contains(">https://"), h.Trim());
    h = KbText.Render($"See {LongUrl} for the driver.");
    Check("a long address in a sentence shows as its site and \"/…\", the whole of it behind the link",
        h.Contains(">support.hp.com/…</a> for the driver") && h.Contains("href=\"https://support.hp.com/za-en/drivers/"), h.Trim());
    h = KbText.Render("[button:  | https://acme.example/portal]");
    Check("a button with no words says Open and the site", h.Contains(">Open acme.example <span"), h.Trim());
    h = KbText.Render("1. Download it: [button: Driver | https://a.example/d] then run it");
    Check("a button can sit inside a step", h.Contains("<li>Download it: <a class=\"kb-btn\" href=\"https://a.example/d\"") && h.Contains("</a> then run it</li>"), h.Trim());
    h = KbText.Render("[button: <b>x</b> | https://a.example/\"onmouseover=\"alert(1)]\n[button: Click | javascript:alert(1)]");
    Check("a button's words and address are encoded, and only http and https make one",
        !h.Contains("<b>x") && !h.Contains("onmouseover=\"alert") && !h.Contains("href=\"javascript"), h.Trim());
    h = KbText.Render("\u00010\u0002 and [button: B | https://b.example/]");
    Check("nothing typed can stand in for a button", !h.Contains('\u0001') && h.Split("kb-btn").Length == 2, h.Trim());
    Check("a list's snippet reads a button as its words", KbText.Snippet($"Get [button: the driver | {LongUrl}] now") == "Get the driver now",
        KbText.Snippet($"Get [button: the driver | {LongUrl}] now"));

    Check("a list's snippet is plain words", KbText.Snippet("# Steps\n1. **Open** it\n[image:3]\n```\nnet use\n```") == "Steps 1. Open it net use",
        KbText.Snippet("# Steps\n1. **Open** it\n[image:3]\n```\nnet use\n```"));

    var pwLine = KbText.LooksLikePassword("Log in as admin\nPassword: Summer2026!");
    Check("\"Password: Summer2026!\" is noticed", pwLine.Length > 0, pwLine);
    Check("...and the secret itself is never repeated", !pwLine.Contains("Summer2026"), pwLine);
    Check("\"PIN 4821\" is noticed", KbText.LooksLikePassword("Door PIN 4821").Length > 0);
    Check("\"the wifi key is Acme-Guest-77\" is noticed", KbText.LooksLikePassword("the wifi key is Acme-Guest-77").Length > 0);
    foreach (var fine in new[] { "Reset the password for the user", "Set a password for them in AD", "Password: ********",
                                 "Tick 'User must change password at next logon'", "The password is in the vault" })
        Check($"\"{fine}\" is left alone", KbText.LooksLikePassword(fine).Length == 0, KbText.LooksLikePassword(fine));
}

// ------------------------------------------------------------------ a platform of its own
var root = AppContext.BaseDirectory;
while (root != null && !File.Exists(Path.Combine(root, "CLAUDE.md"))) root = Path.GetDirectoryName(root);
if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
var dataDir = Path.Combine(Path.GetTempPath(), "rs-kbcheck-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dataDir);
var port = FreePort();
Console.WriteLine();
Console.WriteLine($"A platform of its own on port {port}, database in {dataDir} (the live server is not touched)");

Process? server = null;
try
{
    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using (var db = new PlatformDb(options)) await HarnessDb.MigrateAsync(db);
    var factory = new Factory(options);
    var tenants = new TenantService(factory, new ConfigurationBuilder().Build(), dataDir, DataProtectionProvider.Create("kbcheck"));
    var clients = new ClientService(factory, DataProtectionProvider.Create("kbcheck"));
    var tickets = new TicketService(factory);

    var (co, _) = await tenants.CreateAsync("KB Check", "kb-check", 7);
    await tenants.AddTechnicianAsync(co!.Id, "sue@kb.test", Pw, "Sue Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(co.Id, "bob@kb.test", Pw, "Bob Tech", TenantRole.Technician);
    await tenants.AddTechnicianAsync(co.Id, "sam@kb.test", Pw, "Sam Supervisor", TenantRole.Admin);
    await tenants.AddTechnicianAsync(co.Id, "ossie@kb.test", Pw, "Ossie Office", TenantRole.Billing);
    var (acme, _) = await clients.CreateAsync(co.Id, "Acme Holdings", "Jane", "", "", "", "");
    var (zenith, _) = await clients.CreateAsync(co.Id, "Zenith Motors", "", "", "", "", "");
    var (other, _) = await tenants.CreateAsync("Other Co", "kb-other", 7);
    await tenants.AddTechnicianAsync(other!.Id, "ed@other.test", Pw, "Ed Elsewhere", TenantRole.Admin);
    var (globex, _) = await clients.CreateAsync(other.Id, "Globex", "", "", "", "", "");

    // Sue's job for Acme, not done yet; Sue's second Acme job; Bob's job, done.
    var newUser = await tickets.CreateAsync(co.Id, "Create a new user for Jane", "Jane starts on Monday and needs a login and an email address.",
        "Ossie Office", TicketSource.Manual, assignedTo: "Sue Tech", category: TicketCategory.General, clientId: acme!.Id);
    var printer = await tickets.CreateAsync(co.Id, "Printer offline", "", "Ossie Office", TicketSource.Manual,
        assignedTo: "Sue Tech", category: TicketCategory.General, clientId: acme.Id);
    var bobs = await tickets.CreateAsync(co.Id, "Bob's job", "", "Ossie Office", TicketSource.Manual, assignedTo: "Bob Tech", category: TicketCategory.General);
    // The portal names an article by its number in the company (HANDOVER §159); the checks below that read the database want its id.
    async Task<int> IdOfArticle(int no) { await using var db = new PlatformDb(options); return await db.KbArticles.Where(a => a.TenantId == co.Id && a.Number == no).Select(a => a.Id).FirstOrDefaultAsync(); }
    async Task<string> ImageToken(long id) { await using var db = new PlatformDb(options); return await db.KbImages.Where(i => i.Id == id).Select(i => i.Token).FirstOrDefaultAsync() ?? ""; }
    await using (var db = new PlatformDb(options))
    {
        db.Devices.Add(new Device { TenantId = co.Id, DeviceId = "dev-acme-1", MachineName = "ACME-RECEPTION", ClientId = acme.Id });
        db.Devices.Add(new Device { TenantId = co.Id, DeviceId = "dev-stray", MachineName = "STRAY" });
        await db.SaveChangesAsync();
    }

    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "run", "-c", "Debug", "--project", Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj"), "--", "run" })
        psi.ArgumentList.Add(a);
    psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
    HarnessDb.Use(psi.Environment, dataDir);
    psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
    psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
    psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    if (!await WaitForPort(port, 120000)) { Console.WriteLine("The platform never started listening."); return 1; }

    var sue = await Portal(port, "sue@kb.test");
    var bob = await Portal(port, "bob@kb.test");
    var sam = await Portal(port, "sam@kb.test");
    var ossie = await Portal(port, "ossie@kb.test");
    var ed = await Portal(port, "ed@other.test");

    // ------------------------------------------------------------------ everybody's
    Console.WriteLine();
    Console.WriteLine("== on everybody's menu (recommendation: everybody reads and writes) ==");
    foreach (var (who, c) in new[] { ("a technician", sue), ("an office administrator", ossie), ("a supervisor", sam) })
    {
        var page = await Get(c, "/portal/kb");
        // §34: a refused page arrives as the sign-in form with a 200 — so the page's own heading is asserted.
        Check($"{who} opens the knowledge base", page.Contains("<h1>Knowledge base"), Heading(page));
        Check($"...and it is on {who}'s menu", page.Contains("<a href=\"/portal/kb\">Knowledge base</a>"));
    }
    var dash = await Get(sam, "/portal");
    Check("the dashboard has it, with its count", dash.Contains("📘 Knowledge base") && dash.Contains("href=\"/portal/kb\""));
    var empty = await Get(sue, "/portal/kb");
    Check("an empty knowledge base says how to start one", empty.Contains("Write the first article") && empty.Contains("Add to knowledge base"));

    // ------------------------------------------------------------------ a ticket, before its work is done
    Console.WriteLine();
    Console.WriteLine("== a ticket offers it once its work is done (recommendation 1) ==");
    var t1 = await Get(sue, $"/portal/tickets/{newUser.Id}");
    Check("Sue's open job does not offer it yet", TicketTitle(t1).Contains("Create a new user") && !t1.Contains("Add to knowledge base"), TicketTitle(t1));
    var early = await Get(sue, $"/portal/kb/edit?ticket={newUser.Id}");
    Check("...and the write-up link says why rather than starting one", early.Contains("can be written up once its work is recorded as done"));

    // Sue finishes it — "To be invoiced", with what she did, one thing to a line, and the time.
    var why = await tickets.ActAsync(co.Id, newUser.Id, "Sue Tech", TenantRole.Technician, "general-invoice", new StageInput
    {
        Details = "Opened Active Directory Users and Computers\n- Created jane.doe in Acme > Users\n2. Added her to the Staff group",
        Minutes = 25, Amount = 350
    });
    Check("Sue records the work as done", why == "", why);

    t1 = await Get(sue, $"/portal/tickets/{newUser.Id}");
    Check("now it offers Add to knowledge base — to Sue, who is on it", t1.Contains($"href=\"/portal/kb/edit?t={newUser.Number}\""));
    var t1sam = await Get(sam, $"/portal/tickets/{newUser.Id}");
    Check("...and to a supervisor", t1sam.Contains($"href=\"/portal/kb/edit?t={newUser.Number}\""));
    var t1ossie = await Get(ossie, $"/portal/tickets/{newUser.Id}");
    Check("...but not to the office administrator, who was not on it", TicketTitle(t1ossie).Contains("Create a new user") && !t1ossie.Contains("Add to knowledge base"), TicketTitle(t1ossie));
    var ossieDraft = await Get(ossie, $"/portal/kb/edit?ticket={newUser.Id}");
    Check("...who is told so if she tries the link", ossieDraft.Contains("Only the people on #") && ossieDraft.Contains("value=\"\""));
    var bobDraft = await Get(bob, $"/portal/kb/edit?ticket={newUser.Id}");
    Check("a technician who cannot see the ticket gets nothing of it", bobDraft.Contains("That ticket was not found") && !bobDraft.Contains("Jane starts"));
    var bobsT = await Get(bob, $"/portal/tickets/{bobs.Id}");
    Check("Bob's own job, not done, does not offer it", !bobsT.Contains("Add to knowledge base"));

    // ------------------------------------------------------------------ the write-up
    Console.WriteLine();
    Console.WriteLine("== the write-up opens as an editable article ==");
    var draft = await Get(sue, $"/portal/kb/edit?ticket={newUser.Id}");
    var body = Textarea(draft);
    Check("it is titled from the ticket", draft.Contains("value=\"Create a new user for Jane\""));
    Check("...filed under the ticket's client", draft.Contains($"name=\"clientId\" value=\"{acme.Id}\"") && draft.Contains("value=\"Acme Holdings\""));
    Check("...with what was needed", body.Contains("# What was needed\nJane starts on Monday and needs a login and an email address."), body);
    Check("...and what was done as numbered steps, whatever marks they had",
        body.Contains("1. Opened Active Directory Users and Computers\n2. Created jane.doe in Acme > Users\n3. Added her to the Staff group"), body);
    Check("...without the time and the amount, which are the invoice's", !body.Contains("Time spent") && !body.Contains("350"), body);
    Check("it says which ticket it came from", draft.Contains($"From ticket <a href=\"/portal/t/{newUser.Number}\">#{newUser.Number}</a>"));
    Check("opening it saved nothing", (await Count(options, co.Id)) == 0);

    var steps = "# Before you start\nHave the person's full name and their manager.\n\n# Step by step\n1. Open Active Directory Users and Computers\n2. Go to Acme > Users and choose New > User\n3. Tick **User must change password at next logon**\n4. Add them to the Staff group\n\n```\ngpupdate /force\n```";
    var saved = await PostForm(sue, $"/portal/kb/edit?ticket={newUser.Id}", ("title", "Adding a new user"), ("body", steps), ("clientId", acme.Id.ToString()), ("version", "0"), ("fromTicketNumber", newUser.Number.ToString()));
    var articleNo = NoFrom(saved.url); var articleId = await IdOfArticle(articleNo);
    Check("Save writes it and opens it", articleId > 0 && saved.page.Contains("<h1 style=\"margin-bottom:4px\">Adding a new user</h1>") && saved.page.Contains("Saved."), saved.url);
    Check("...drawn as headings, steps and a block of commands",
        saved.page.Contains("<h2>Step by step</h2>") && saved.page.Contains("<li>Open Active Directory Users and Computers</li>") && saved.page.Contains("<pre><code>gpupdate /force</code></pre>"));
    Check("...for Acme, written by Sue, from the ticket",
        saved.page.Contains("For Acme Holdings") && saved.page.Contains("written by Sue Tech") && saved.page.Contains($"href=\"/portal/t/{newUser.Number}\">#{newUser.Number}</a>"));
    Check("a sentence ABOUT a password raises no warning", !saved.page.Contains("id=\"kbPassword\""));
    t1 = await Get(sue, $"/portal/tickets/{newUser.Id}");
    Check("the ticket shows what was written from it", t1.Contains($"href=\"/portal/a/{articleNo}\">Adding a new user</a>"));
    Check("...and offers another, not the same again", t1.Contains("Add another to the knowledge base"));
    Check("...and its story says so", t1.Contains("Written up in the knowledge base: Adding a new user"));

    // ------------------------------------------------------------------ found without looking
    Console.WriteLine();
    Console.WriteLine("== found without having to look for it (recommendation 2) ==");
    var tp = await Get(sue, $"/portal/tickets/{printer.Id}");
    Check("Acme's OTHER ticket says the client has an article", tp.Contains($"href=\"/portal/kb?c={acme.Number}\">📘 Knowledge base for Acme Holdings (1)</a>"));
    Check("...without offering a write-up of a job that is not done", !tp.Contains("Add to knowledge base") && !tp.Contains("Add another to the knowledge base"));
    var cp = await Get(ossie, $"/portal/clients/{acme.Id}");
    Check("the client's own page says so too", cp.Contains("📘 Knowledge base for Acme Holdings (1)"));
    var zp = await Get(ossie, $"/portal/clients/{zenith!.Id}");
    Check("...and a client with none says none", zp.Contains("📘 Knowledge base for Zenith Motors (0)"));

    // ------------------------------------------------------------------ General and Clients
    Console.WriteLine();
    Console.WriteLine("== a General section and a Clients section ==");
    var gen = await PostForm(bob, "/portal/kb/edit", ("title", "Clearing the print spooler"), ("body", "1. Stop the Print Spooler service\n2. Empty C:\\Windows\\System32\\spool\\PRINTERS\n3. Start it again"), ("clientId", "0"), ("version", "0"), ("fromTicketNumber", "0"));
    var genId = await IdOfArticle(NoFrom(gen.url));
    Check("Bob writes a General article from scratch", genId > 0 && gen.page.Contains(">General</a>"), gen.url);
    var list = await Get(sue, "/portal/kb");
    var gPart = Between(list, "<h2 class=\"kb-client\">General", "<h2 class=\"kb-client\" style=\"margin-top:34px\">Clients") ?? "";
    var cPart = Between(list, "<h2 class=\"kb-client\" style=\"margin-top:34px\">Clients", "</section>") ?? "";
    Check("General lists the General article and not the client's", gPart.Contains("Clearing the print spooler") && !gPart.Contains("Adding a new user"));
    Check("Clients lists it under Acme", cPart.Contains("Acme Holdings") && cPart.Contains("Adding a new user") && !cPart.Contains("Clearing the print spooler"));
    var acmeOnly = await Get(sue, $"/portal/kb?client={acme.Id}");
    Check("one client's list is that client's only", acmeOnly.Contains("Adding a new user") && !acmeOnly.Contains("Clearing the print spooler") && acmeOnly.Contains("<strong>Acme Holdings</strong>") && acmeOnly.Contains("class=\"kb-crumbs\""));
    var genOnly = await Get(sue, "/portal/kb?client=0");
    Check("General only is General only", genOnly.Contains("Clearing the print spooler") && !genOnly.Contains("Adding a new user"));
    Check("searching \"staff group\" finds the steps", (await Get(sue, "/portal/kb?search=staff+group")).Contains("Adding a new user"));
    Check("searching the client's name finds its articles", (await Get(sue, "/portal/kb?search=acme")).Contains("Adding a new user"));
    var none = await Get(sue, "/portal/kb?search=vpn");
    Check("a search with no match says so", none.Contains("Nothing matches") && !none.Contains("Adding a new user"));
    var newFor = await Get(sue, $"/portal/kb/edit?client={acme.Id}");
    Check("New article on a client's list starts filed under that client", newFor.Contains($"name=\"clientId\" value=\"{acme.Id}\"") && newFor.Contains("value=\"Acme Holdings\""));

    // ------------------------------------------------------------------ every save kept
    Console.WriteLine();
    Console.WriteLine("== everybody edits, every save is kept (recommendation 5) ==");
    var bobEdit = await PostForm(bob, $"/portal/kb/edit/{articleId}", ("title", "Adding a new user"), ("body", steps + "\n5. Send Jane her first-logon details by SMS"), ("clientId", acme.Id.ToString()), ("version", "1"), ("fromTicketNumber", "0"));
    Check("Bob, who never saw the ticket, edits the article", bobEdit.page.Contains("Saved.") && bobEdit.page.Contains("Send Jane her first-logon details"));
    var clash = await PostForm(sue, $"/portal/kb/edit/{articleId}", ("title", "Adding a new user"), ("body", steps + "\n5. Sue's own last step"), ("clientId", acme.Id.ToString()), ("version", "1"), ("fromTicketNumber", "0"));
    Check("Sue saving the version she opened, after Bob's, is refused — not overwritten",
        clash.page.Contains("Bob Tech saved a change to this article") && clash.url.EndsWith("/edit"), clash.url);
    Check("...and what she typed is still in front of her", Textarea(clash.page).Contains("5. Sue's own last step"));
    var art = await Get(sue, $"/portal/kb/{articleId}");
    Check("the article is Bob's version", art.Contains("Send Jane her first-logon details") && !art.Contains("Sue's own last step"));
    Check("the history has both saves, the first from the ticket", art.Contains("History — 2 versions") && art.Contains($"Written up from #{newUser.Number}"));
    var v1 = await Get(ossie, $"/portal/kb/{articleId}?version=1");
    Check("version 1 can be read", v1.Contains("You are looking at <strong>version 1</strong> of 2") && !v1.Contains("Send Jane her first-logon details"));
    var restored = await PostFormHandler(ossie, $"/portal/kb/{articleId}?version=1", "Restore", ("version", "1"));
    Check("the office administrator puts version 1 back", restored.page.Contains("Version 1 is back") && !restored.page.Contains("Send Jane her first-logon details"), restored.url);
    Check("...as a third version, so putting back can itself be undone", restored.page.Contains("History — 3 versions") && restored.page.Contains("Put back version 1"));

    // ------------------------------------------------------------------ never a password
    Console.WriteLine();
    Console.WriteLine("== never a password (recommendation 6) ==");
    var pwArt = await PostForm(sam, "/portal/kb/edit", ("title", "Reception PC"), ("body", "Log in as the local admin.\nPassword: Summer2026!"), ("clientId", acme.Id.ToString()), ("version", "0"), ("fromTicketNumber", "0"));
    Check("an article that gives a password is saved", pwArt.page.Contains("Saved."));
    Check("...and warns everybody who opens it, with the secret hidden",
        pwArt.page.Contains("id=\"kbPassword\"") && pwArt.page.Contains("Password: ••••••") && !(Between(pwArt.page, "id=\"kbPassword\"", "</div>") ?? "Summer2026").Contains("Summer2026"));
    var editPage = await Get(sam, $"/portal/a/{NoFrom(pwArt.url)}/edit");
    Check("the editor says never a password", editPage.Contains("<strong>Never a password</strong>"));

    // ------------------------------------------------------------------ the editor's two helpers
    Console.WriteLine();
    Console.WriteLine("== Preview and pictures ==");
    var pv = await Raw(sue, "/api/portal/kb/preview", new StringContent("1. one\nPIN 1234"), kb: false);
    Check("Preview without the page's own header is refused", pv.status == 400, $"HTTP {pv.status}");
    pv = await Raw(sue, "/api/portal/kb/preview", new StringContent("1. one\nPIN 1234"), kb: true);
    var pvHtml = pv.status == 200 ? JsonDocument.Parse(pv.body).RootElement.GetProperty("html").GetString() ?? "" : "";
    var pvPw = pv.status == 200 ? JsonDocument.Parse(pv.body).RootElement.GetProperty("password").GetString() ?? "" : "";
    Check("Preview draws with the page's own renderer, and notices the PIN", pvHtml.Contains("<ol><li>one</li></ol>") && pvPw.Contains("PIN ••••••"), pvHtml + " / " + pvPw);
    var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
    var up = await Upload(sue, png, "shot.png", kb: false);
    Check("a picture without the page's own header is refused", up.status == 400, $"HTTP {up.status}");
    up = await Upload(sue, png, "shot.png", kb: true);
    var imgId = up.status == 200 ? JsonDocument.Parse(up.body).RootElement.GetProperty("id").GetInt64() : 0;
    Check("a pasted screenshot goes up and comes back as its tag", imgId > 0 && up.body.Contains($"[image:{imgId}]"), up.body);
    var fake = await Upload(sue, System.Text.Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "evil.png", kb: true);
    Check("a file NAMED .png that is not a picture is refused", fake.status == 400 && fake.body.Contains("not a picture"), fake.body);
    var got = await sue.GetAsync($"/portal/kb/image/{imgId}");
    Check("the picture is served to the company", got.StatusCode == HttpStatusCode.OK && got.Content.Headers.ContentType?.MediaType == "image/png", $"HTTP {(int)got.StatusCode}");
    var edGot = await ed.GetAsync($"/portal/kb/image/{imgId}");
    Check("...and never to another company", edGot.StatusCode == HttpStatusCode.NotFound, $"HTTP {(int)edGot.StatusCode}");
    var withPic = await PostForm(sue, "/portal/kb/edit", ("title", "Mapping the S: drive"), ("body", $"1. Open This PC\n\n[image:{imgId}]\n\n2. Map network drive"), ("clientId", "0"), ("version", "0"), ("fromTicketNumber", "0"));
    Check("an article shows its picture", withPic.page.Contains($"<img src=\"/portal/i/{await ImageToken(imgId)}\""));

    // ------------------------------------------------------------------ the dangerous words
    var xss = await PostForm(bob, "/portal/kb/edit", ("title", "<script>alert('t')</script>"), ("body", "<script>alert('b')</script>\n<iframe src=//evil>"), ("clientId", "0"), ("version", "0"), ("fromTicketNumber", "0"));
    Check("a title and words written as HTML are shown, never run", xss.raw.Contains("&lt;script&gt;alert(") && !xss.raw.Contains("<script>alert('") && !xss.raw.Contains("<iframe src=//evil>"));
    var blank = await PostForm(bob, "/portal/kb/edit", ("title", ""), ("body", "Some words worth keeping"), ("clientId", "0"), ("version", "0"), ("fromTicketNumber", "0"));
    Check("no title is refused, and the words are kept", blank.page.Contains("Give it a title") && Textarea(blank.page).Contains("Some words worth keeping"));

    // ------------------------------------------------------------------ another company
    Console.WriteLine();
    Console.WriteLine("== another company sees none of it ==");
    var edList = await Get(ed, "/portal/kb");
    Check("their knowledge base is their own", edList.Contains("<h1>Knowledge base") && !edList.Contains("Adding a new user") && !edList.Contains("Clearing the print spooler"));
    // A 404 since the pentest's F6 (2026-10-23), still saying so.
    var edOurs = await ed.GetAsync($"/portal/kb/{articleId}");
    Check("one of ours by number is \"not there\" — a 404", (int)edOurs.StatusCode == 404
        && WebUtility.HtmlDecode(await edOurs.Content.ReadAsStringAsync()).Contains("That article is not in the knowledge base"), $"HTTP {(int)edOurs.StatusCode}");
    var edSave = await PostForged(ed, $"/portal/kb/edit/{articleId}", null, ("title", "Taken"), ("body", "x"), ("clientId", "0"), ("version", "3"), ("fromTicketNumber", "0"));
    Check("...and cannot be saved over", !edSave.page.Contains("Saved.") && (await Get(sue, $"/portal/kb/{articleId}")).Contains("Adding a new user"));
    var edFiled = await PostForm(ed, "/portal/kb/edit", ("title", "Under somebody else's client"), ("body", "x"), ("clientId", acme.Id.ToString()), ("version", "0"), ("fromTicketNumber", "0"));
    Check("they cannot file an article under OUR client", edFiled.page.Contains("That client was not found"));
    var ourGlobex = await PostForm(sue, "/portal/kb/edit", ("title", "Under their client"), ("body", "x"), ("clientId", globex!.Id.ToString()), ("version", "0"), ("fromTicketNumber", "0"));
    Check("...nor we under theirs", ourGlobex.page.Contains("That client was not found"));

    // ------------------------------------------------------------------ the console
    Console.WriteLine();
    Console.WriteLine("== the console ==");
    var sueC = await Sign(port, "sue@kb.test", Pw);
    var samC = await Sign(port, "sam@kb.test", Pw);
    sueC.Ws.Send(new TicketOpenMsg { TicketId = newUser.Id });
    var d = (await sueC.Next<TicketDetailMsg>())?.Detail;
    Check("the ticket window is told Acme's count, what was written up, and to offer another",
        d?.Knowledge is { ClientCount: 2, CanAdd: true } k1 && k1.ClientName == "Acme Holdings" && k1.FromTicket.Any(a => a.Id == articleId && a.Title == "Adding a new user"),
        JsonSerializer.Serialize(d?.Knowledge));
    sueC.Ws.Send(new TicketOpenMsg { TicketId = printer.Id });
    d = (await sueC.Next<TicketDetailMsg>())?.Detail;
    Check("...and on Acme's unfinished job: the count, and no write-up", d?.Knowledge is { ClientCount: 2, CanAdd: false } && d.Knowledge.FromTicket.Count == 0, JsonSerializer.Serialize(d?.Knowledge));
    samC.Ws.Send(new TicketOpenMsg { TicketId = bobs.Id });
    d = (await samC.Next<TicketDetailMsg>())?.Detail;
    Check("a job with no client and no work done says nothing to offer", d?.Knowledge is { ClientCount: 0, CanAdd: false }, JsonSerializer.Serialize(d?.Knowledge));

    sueC.Ws.Send(new KnowledgeCountRequestMsg { DeviceId = "dev-acme-1" });
    var kc = await sueC.Next<KnowledgeCountMsg>();
    Check("connecting to Acme's machine: the session window is told \"Acme Holdings, 2\"",
        kc is { DeviceId: "dev-acme-1", ClientName: "Acme Holdings", Count: 2 } && kc.ClientId == acme.Id, JsonSerializer.Serialize(kc));
    sueC.Ws.Send(new KnowledgeCountRequestMsg { DeviceId = "dev-stray" });
    kc = await sueC.Next<KnowledgeCountMsg>();
    Check("...and a machine that belongs to nobody: no client", kc is { ClientId: 0, Count: 0 }, JsonSerializer.Serialize(kc));

    foreach (var (path, lands) in new[]
    {
        ("/portal/kb", "/portal/kb"), ($"/portal/kb?client={acme.Id}", $"/portal/kb?c={acme.Number}"),
        ($"/portal/kb/{articleId}", $"/portal/a/{articleNo}"), ($"/portal/kb/edit?ticket={newUser.Id}", $"/portal/kb/edit?t={newUser.Number}"),
        // An old console asks by id; the hand-off sends the browser to the number (HANDOVER §159). A new one asks by number.
        ($"/portal/kb?c={acme.Number}", $"/portal/kb?c={acme.Number}"), ($"/portal/a/{articleNo}", $"/portal/a/{articleNo}"), ($"/portal/kb/edit?t={newUser.Number}", $"/portal/kb/edit?t={newUser.Number}"),
        ("/portal/kb?client=1&next=//evil.example", "/portal/tickets"), ("/portal/kb/edit?ticket=1x", "/portal/tickets"),
    })
    {
        sueC.Ws.Send(new PortalLinkRequestMsg { Path = path });
        var link = await sueC.Next<PortalLinkMsg>();
        var browser = Browser(port);
        var r = link == null ? null : await browser.GetAsync(link.Url);
        var at = r?.RequestMessage?.RequestUri?.PathAndQuery ?? "";
        Check($"the console's link for {path} lands on {lands}", at == lands, at);
    }

    // ------------------------------------------------------------------ folders
    Console.WriteLine();
    Console.WriteLine("== folders and subfolders, in General and for each client (2026-10-06) ==");
    {
        async Task<int> FolderId(string name, int tenant) { await using var db = new PlatformDb(options); return await db.KbFolders.Where(f => f.Name == name && f.TenantId == tenant).Select(f => f.Id).FirstOrDefaultAsync(); }
        async Task<int?> InFolder(int articleId) { await using var db = new PlatformDb(options); return await db.KbArticles.Where(a => a.Id == articleId).Select(a => a.FolderId).FirstAsync(); }

        var made = await PostFormHandler(sue, "/portal/kb?client=0", "NewFolder", ("name", "  Printers  "));
        var printers = await FolderId("Printers", co.Id);
        Check("a technician makes a folder in General, its name tidied", printers > 0 && made.url.Contains("done=made") && made.page.Contains("Folder made."), made.url);
        Check("...and General shows it as a folder", made.page.Contains($"href=\"/portal/kb?folder={printers}\"") && made.page.Contains(">Printers</span>"));
        var sub = await PostFormHandler(sue, $"/portal/kb?folder={printers}", "NewFolder", ("name", "HP"));
        var hp = await FolderId("HP", co.Id);
        Check("a subfolder inside it", hp > 0 && sub.url == $"/portal/kb?folder={printers}&done=made", sub.url);
        var again = await PostFormHandler(bob, $"/portal/kb?folder={printers}", "NewFolder", ("name", "hp"));
        Check("the same name twice in one place is refused, whatever its case", again.page.Contains("There is already a folder called hp there."));
        // The box itself, by its label: "handler=NewFolder" is also in the Light/Dark switch's return address.
        var newBox = FormOf(again.page, "aria-label=\"The new folder's name\"");
        Check("...and what was typed stays in the box", newBox.Contains("value=\"hp\""), newBox);
        var inHp = await Get(sue, $"/portal/kb?folder={hp}");
        Check("inside a folder, the way back reads Knowledge base › General › Printers › HP",
            (Between(inHp, "class=\"kb-crumbs\"", "</nav>") ?? "") is var crumbs
            && crumbs.Contains(">Knowledge base</a>") && crumbs.Contains(">General</a>") && crumbs.Contains($"folder={printers}\">Printers</a>") && crumbs.Contains("<strong>HP</strong>"),
            crumbs);
        Check("an empty folder says so, and offers New article here", inHp.Contains("This folder is empty") && inHp.Contains($"href=\"/portal/kb/edit?folder={hp}\""));

        await PostFormHandler(sam, $"/portal/kb?client={acme.Id}", "NewFolder", ("name", "Network"));
        var network = await FolderId("Network", co.Id);
        Check("a client's section has folders of its own", network > 0 && (await Get(sue, $"/portal/kb?client={acme.Id}")).Contains($"folder={network}\""));
        Check("...not offered in General's", !(await Get(sue, "/portal/kb?client=0")).Contains($"folder={network}\""));
        var api = await sue.GetStringAsync($"/api/portal/kb/folders?client={acme.Id}");
        Check("the editor's Folder box asks for a client's folders and gets only theirs", api.Contains("Network") && !api.Contains("Printers"), api);

        int parent = 0, deep = 0;
        string deepWhy = "";
        for (int i = 1; i <= 7; i++)
        {
            var r = await PostFormHandler(sue, parent == 0 ? "/portal/kb?client=0" : $"/portal/kb?folder={parent}", "NewFolder", ("name", $"Level {i}"));
            var id = await FolderId($"Level {i}", co.Id);
            if (id == 0) { deepWhy = Between(r.page, "alert-bad\">", "</div>") ?? ""; break; }
            parent = id; deep = i;
        }
        Check($"folders go {KnowledgeBaseService.MaxFolderDepth} deep and no deeper", deep == KnowledgeBaseService.MaxFolderDepth && deepWhy.Contains("deep at most"), $"{deep} deep: {deepWhy}");

        // An article in a folder, from the folder's own New article here.
        var edHere = await Get(sue, $"/portal/kb/edit?folder={hp}");
        Check("New article here starts in the folder", edHere.Contains($"<option value=\"{hp}\" selected=\"selected\">"), Between(edHere, "id=\"kbFolder\"", "</select>") ?? "");
        var driver = await PostFormTo(sue, $"/portal/kb/edit?folder={hp}", "/portal/kb/edit", "id=\"kbForm\"",
            new[] { ("title", "Installing the M404 driver"), ("body", "1. Download it\n2. Run it"), ("clientId", "0"), ("folderId", hp.ToString()), ("version", "0"), ("fromTicketNumber", "0") });
        var driverId = await IdOfArticle(NoFrom(driver.url));
        Check("it is saved in the folder", driverId > 0 && await InFolder(driverId) == hp, driver.url);
        Check("...and its page says where it lives", (Between(driver.page, "class=\"kb-crumbs\"", "</nav>") ?? "").Contains($"folder={hp}\">HP</a>"));
        var pPage = await Get(sue, $"/portal/kb?folder={printers}");
        Check("the folder above shows the subfolder holding 1 article, not the article itself",
            pPage.Contains("<span class=\"kb-t\">HP</span><span class=\"kb-m\">1 article</span>") && !pPage.Contains("Installing the M404 driver"));
        Check("General's top shows Printers, not the article", (await Get(sue, "/portal/kb?client=0")) is var gTop && gTop.Contains(">Printers</span>") && !gTop.Contains("Installing the M404 driver"));
        var found = await Get(sue, $"/portal/kb?folder={printers}&search=m404");
        Check("a search inside Printers finds it, and says which folder it is in", found.Contains("Installing the M404 driver") && found.Contains("📁 Printers › HP"));
        Check("a search in Acme's Network folder does not", !(await Get(sue, $"/portal/kb?folder={network}&search=m404")).Contains("Installing the M404 driver"));
        var wrong = await PostForm(sue, "/portal/kb/edit", ("title", "Acme's switch"), ("body", "1. x"), ("clientId", acme.Id.ToString()), ("folderId", hp.ToString()), ("version", "0"), ("fromTicketNumber", "0"));
        Check("an Acme article cannot go in a General folder", wrong.page.Contains("is not in this client's section") && Textarea(wrong.page).Contains("1. x"));

        var top = await PostFormHandler(bob, $"/portal/kb/{driverId}", "Move", ("folderId", "0"));
        Check("Move on the article's page takes it to the top", top.page.Contains("Moved.") && await InFolder(driverId) == null);
        Check("...as a version of its own", top.page.Contains("Moved out of its folder"));
        await PostFormHandler(bob, $"/portal/kb/{driverId}", "Move", ("folderId", hp.ToString()));
        Check("...and back", await InFolder(driverId) == hp);
        var cross = await PostForged(bob, $"/portal/kb/{driverId}", "Move", ("folderId", network.ToString()));
        Check("never into another section's folder", cross.page.Contains("one client's") && await InFolder(driverId) == hp, Between(cross.page, "alert-bad\">", "</div>") ?? "");
        var putBack = await PostFormHandler(ossie, $"/portal/kb/{driverId}?version=2", "Restore", ("version", "2"));
        Check("putting a version back puts it back where it was then", putBack.page.Contains("Version 2 is back") && await InFolder(driverId) == null);
        await PostFormHandler(bob, $"/portal/kb/{driverId}", "Move", ("folderId", hp.ToString()));

        var renamed = await PostFormHandler(sue, $"/portal/kb?folder={hp}", "EditFolder", ("name", "HP LaserJet"), ("parentId", "0"));
        Check("a folder is renamed and moved to the top of its section in one go", renamed.url.Contains("done=changed")
            && (await Get(sue, "/portal/kb?client=0")).Contains(">HP LaserJet</span>"), renamed.url);
        await PostFormHandler(sue, $"/portal/kb?folder={hp}", "EditFolder", ("name", "HP"), ("parentId", printers.ToString()));
        var loop = await PostFormHandler(sue, $"/portal/kb?folder={printers}", "EditFolder", ("name", "Printers"), ("parentId", hp.ToString()));
        Check("a folder cannot go inside a folder that is in it", loop.page.Contains("cannot go inside itself"));
        var across = await PostForged(sue, $"/portal/kb?folder={printers}", "EditFolder", ("name", "Printers"), ("parentId", network.ToString()));
        Check("...nor into another section", across.page.Contains("same section"));

        var sueRemove = await PostForged(sue, $"/portal/kb?folder={printers}", "RemoveFolder");
        Check("a technician cannot remove a folder with something in it", sueRemove.page.Contains("Only a supervisor or the owner can remove a folder"));
        Check("...and is not offered the button", !(await Get(sue, $"/portal/kb?folder={printers}")).Contains("Remove folder</button>"));
        Check("an empty one, they can", (await PostFormHandler(sue, $"/portal/kb?folder={network}", "RemoveFolder")).url == $"/portal/kb?c={acme.Number}&done=removed"
            && await FolderId("Network", co.Id) == 0);
        var samRemove = await PostFormHandler(sam, $"/portal/kb?folder={printers}", "RemoveFolder");
        Check("a supervisor removes a full one; nothing in it is lost — it moves up",
            samRemove.url == "/portal/kb?c=0&done=removed" && samRemove.page.Contains(">HP</span>") && await InFolder(driverId) == hp, samRemove.url);

        var edSees = await Get(ed, $"/portal/kb?folder={hp}");
        Check("another company opening our folder sees none of it", !edSees.Contains(">HP<") && !edSees.Contains("Installing the M404 driver"));
        Check("...nor in its Folder box", !(await ed.GetStringAsync("/api/portal/kb/folders?client=0")).Contains("HP"));
        var edMakes = await PostForged(ed, $"/portal/kb?folder={hp}", "NewFolder", ("name", "Sneaky"));
        await using (var db = new PlatformDb(options))
            Check("...and cannot make a folder inside it", !await db.KbFolders.AnyAsync(f => f.Name == "Sneaky" && f.ParentId == hp));
    }

    // ------------------------------------------------------------------ deleting
    Console.WriteLine();
    Console.WriteLine("== only a supervisor or the owner deletes ==");
    var bobDel = await PostForged(bob, $"/portal/kb/{genId}", "Delete");
    Check("a technician's delete is refused, with the reason", bobDel.page.Contains("Only a supervisor or the owner can delete"));
    Check("...and the button is not drawn for him", !bobDel.page.Contains(">Delete</button>"));
    var ossieDel = await PostForged(ossie, $"/portal/kb/{genId}", "Delete");
    Check("so is the office administrator's", ossieDel.page.Contains("Only a supervisor or the owner can delete"));
    var samDel = await PostFormHandler(sam, $"/portal/kb/{articleId}", "Delete");
    Check("a supervisor deletes, and lands back where it was", samDel.url.EndsWith($"/portal/kb?c={acme.Number}&deleted=true") && samDel.page.Contains("Deleted. It is gone"), samDel.url);
    var goneNow = await sue.GetAsync($"/portal/kb/{articleId}");
    Check("...and it is gone for everybody — a 404", (int)goneNow.StatusCode == 404
        && WebUtility.HtmlDecode(await goneNow.Content.ReadAsStringAsync()).Contains("That article is not in the knowledge base"), $"HTTP {(int)goneNow.StatusCode}");
    t1 = await Get(sue, $"/portal/tickets/{newUser.Id}");
    Check("the ticket no longer points at it, and offers a fresh write-up", !t1.Contains($"/portal/a/{articleNo}\"") && t1.Contains(">Add to knowledge base</a>"));
    var stale = await PostForged(sue, $"/portal/kb/edit/{articleId}", null, ("title", "Adding a new user"), ("body", "x"), ("clientId", "0"), ("version", "3"), ("fromTicketNumber", "0"));
    Check("an editor left open on a deleted article keeps what was typed", stale.page.Contains("not in the knowledge base") && stale.page.Contains("What you typed"));

    Console.WriteLine();
    Console.WriteLine($"{pass} of {pass + fail} checks passed.");
    return fail == 0 ? 0 : 1;
}
finally
{
    try { if (server is { HasExited: false }) server.Kill(true); } catch { }
    try { await Task.Delay(400); Directory.Delete(dataDir, true); } catch { }
}

// ====================================================================================== helpers

static async Task<int> Count(DbContextOptions<PlatformDb> o, int tenantId)
{
    await using var db = new PlatformDb(o);
    return await db.KbArticles.CountAsync(a => a.TenantId == tenantId);
}

static HttpClient Browser(int port) =>
    new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

/// <summary>Signed in through the portal's own form, antiforgery token and all.</summary>
static async Task<HttpClient> Portal(int port, string email)
{
    var c = Browser(port);
    var page = await c.GetStringAsync("/login");
    await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
    { ["Email"] = email, ["Password"] = "kb-check-2026", ["__RequestVerificationToken"] = Token(page) }));
    return c;
}

/// <summary>A form posted the way the page posts it: its token read from the page first.</summary>
static async Task<(string url, string page, string raw)> PostForm(HttpClient c, string url, params (string k, string v)[] fields) =>
    await PostFormTo(c, url, url, "id=\"kbForm\"", fields);

/// <summary>A request made by hand where the page draws no such form: a token taken from the Sign out form, as anybody could.</summary>
static Task<(string url, string page, string raw)> PostForged(HttpClient c, string url, string? handler, params (string k, string v)[] fields) =>
    PostFormTo(c, url, handler == null ? url : url + (url.Contains('?') ? "&" : "?") + "handler=" + handler, "action=\"/logout\"", fields);

static Task<(string url, string page, string raw)> PostFormHandler(HttpClient c, string url, string handler, params (string k, string v)[] fields) =>
    PostFormTo(c, url, url + (url.Contains('?') ? "&" : "?") + "handler=" + handler, "handler=" + handler, fields);

static async Task<(string url, string page, string raw)> PostFormTo(HttpClient c, string pageUrl, string postUrl, string formMarker, (string k, string v)[] fields)
{
    var page = await c.GetStringAsync(pageUrl);
    // The token from THE FORM BEING POSTED, as a browser sends it — never one borrowed from elsewhere
    // on the page. Borrowing the Sign out form's token once hid a Save that no browser could make.
    var all = fields.Select(f => new KeyValuePair<string, string>(f.k, f.v)).Append(new("__RequestVerificationToken", Token(FormOf(page, formMarker))));
    var r = await c.PostAsync(postUrl, new FormUrlEncodedContent(all));
    var raw = await r.Content.ReadAsStringAsync();
    // Decoded, because Razor writes 📘, • and — as character references; raw kept for the injection checks.
    return (r.RequestMessage?.RequestUri?.PathAndQuery ?? "", WebUtility.HtmlDecode(raw), raw);
}

static async Task<(int status, string body)> Raw(HttpClient c, string url, HttpContent content, bool kb)
{
    var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
    if (kb) req.Headers.Add("X-Kb", "1");
    var r = await c.SendAsync(req);
    return ((int)r.StatusCode, await r.Content.ReadAsStringAsync());
}

static Task<(int status, string body)> Upload(HttpClient c, byte[] bytes, string name, bool kb)
{
    var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
    return Raw(c, "/api/portal/kb/images", form, kb);
}

/// <summary>An article's number in the company, from the address Save went to: /portal/a/{number} (HANDOVER §159).</summary>
static int NoFrom(string url)
{
    var m = System.Text.RegularExpressions.Regex.Match(url, @"^/portal/a/(\d+)");
    return m.Success ? int.Parse(m.Groups[1].Value) : 0;
}

static string Textarea(string page) => WebUtility.HtmlDecode(Between(page, "name=\"body\" rows=\"20\" required>", "</textarea>") ?? "").Replace("\r", "");
static string Heading(string page) => Between(page, "<h1", "</h1>") ?? "(no heading)";
/// <summary>The one form on a page that holds this marker, from its opening tag to its end — or "" if none does.</summary>
static string FormOf(string page, string marker)
{
    var at = page.IndexOf(marker, StringComparison.Ordinal);
    if (at < 0) return "";
    var start = page.LastIndexOf("<form", at, StringComparison.Ordinal);
    var end = page.IndexOf("</form>", at, StringComparison.Ordinal);
    return start < 0 || end < 0 ? "" : page[start..end];
}
// A ticket with a route reads as a story, titled by its tk-title line rather than an h1 (2026-09-27).
static string TicketTitle(string page) => Between(page, "<p class=\"tk-title\">", "</p>") ?? Heading(page);
static async Task<string> Get(HttpClient c, string url) => WebUtility.HtmlDecode(await c.GetStringAsync(url));

static string Token(string page) =>
    Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
    ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";

static string? Between(string s, string a, string b)
{
    var i = s.IndexOf(a, StringComparison.Ordinal);
    if (i < 0) return null;
    i += a.Length;
    var j = s.IndexOf(b, i, StringComparison.Ordinal);
    return j < 0 ? null : s[i..j];
}

static async Task<TechConsole> Sign(int port, string email, string pw)
{
    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password = pw });
    lr.EnsureSuccessStatusCode();
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var cws = new ClientWebSocket();
    await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={Uri.EscapeDataString(tok)}"), CancellationToken.None);
    var c = new TechConsole(new WsConnection(cws));
    _ = c.Ws.RunAsync(CancellationToken.None);
    await Task.Delay(500);
    c.Clear();
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

/// <summary>One signed-in console and everything it has been sent, in order.</summary>
file sealed class TechConsole
{
    public WsConnection Ws { get; }
    private readonly List<Msg> _got = new();
    public TechConsole(WsConnection ws) { Ws = ws; ws.OnMessage += m => { lock (_got) _got.Add(m); }; }
    public void Clear() { lock (_got) _got.Clear(); }

    public async Task<T?> Next<T>(int ms = 8000) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (_got)
            {
                var m = _got.OfType<T>().FirstOrDefault();
                if (m != null) { _got.Remove(m); return m; }
            }
            await Task.Delay(40);
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
