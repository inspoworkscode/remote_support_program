using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// admincheck — platform admins (2026-10-13, docs/NEXT-SESSION-CONTROL.md stage A, HANDOVER §132).
//
// Clint: "an option for me to add admins that help me manage everything like activating and
// suspending clients". Against a REAL platform with two-factor ON and a mail server of its own:
//
//   FIRST WAY IN   no admins yet: /owner/login offers the admin key, which signs in as an Owner.
//   ADDING         the key adds the first Owner; a set-up link is MAILED; nothing is typed for them.
//   SETTING UP     opening the link spends nothing; password then QR then code, saved together;
//                  the link works once.
//   SIGNING IN     email + password + code; then a TRUSTED browser skips the code for a week, a
//                  stranger's does not; five wrong passwords lock the account.
//   LEVELS         a Viewer changes nothing (refused by the server, not just a missing button); a
//                  Manager runs companies but not admins; a Full admin adds Viewers and Managers
//                  but not Full; nobody removes an Owner or themselves; a change of level or a
//                  removal signs that admin out at once.
//   SUSPENDING     signs the company's console out, closes its machines, ends its portal cookie,
//                  emails its owners; making it active again emails them that too.
//   EMAILING       a Manager writes to a company's owners; replies come to the Manager.
//   THE LOG        every one of those, by name.
//   COMMAND LINE   add-admin, list-admins, remove-admin (which may remove an Owner).
//
//   dotnet run -c Release --project tools/admincheck/admincheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class AdminCheck
{
    private static int _pass, _fail;
    private const string Slug = "admin-test-co", Company = "Admin Test Co";
    private const string Boss = "boss@adm.test", BossPw = "Bootstrap!2026";
    private const string Clint = "clint@adm.test", Mandy = "mandy@adm.test", Vic = "vic@adm.test", Fred = "fred@adm.test", Gina = "gina@adm.test";
    private const string Pw = "AdminChose!2026";
    private static readonly string AdminKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    private static string _base = "", _proj = "", _dataDir = "", _tenantKey = "";
    private static int _port;
    private static readonly FakeSmtp Mail = new();
    /// <summary>Each admin's authenticator secret, as their phone would hold it.</summary>
    private static readonly Dictionary<string, string> Phones = new();

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }
    private static void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        _dataDir = Path.Combine(Path.GetTempPath(), "rs-admincheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_dataDir);
        _port = FreePort();
        _proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        _base = $"http://127.0.0.1:{_port}";
        Mail.Start();
        Console.WriteLine("admincheck — platform admins");
        Console.WriteLine($"A platform of its own on port {_port}, two-factor REQUIRED; a mail server of its own on port {Mail.Port}");

        try
        {
            var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var buildOut = await build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();
            if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }
            var made = await Cli("create-tenant", Company, Slug, "10");
            _tenantKey = Regex.Match(made, @"Tenant key:\s*(\S+)").Groups[1].Value;
            if (_tenantKey.Length == 0) { Console.WriteLine("Could not create a tenant:\n" + made); return 1; }
            await Cli("set-brand", Slug, "status", "Active");
            if (!(await Cli("add-tech", Slug, Boss, BossPw, "Bea Boss", "--owner", "--admin")).Contains("Added")) { Console.WriteLine("Could not add the company's owner."); return 1; }
            var mailSet = await Cli("platform-mail", "set", "--host", "127.0.0.1", "--port", Mail.Port.ToString(), "--nossl",
                "--from", "noreply@adm.test", "--from-name", "The Remote Support Program", "--copy-to", "copy@adm.test");
            if (!mailSet.Contains("Saved")) { Console.WriteLine("Could not set the platform mailbox:\n" + mailSet); return 1; }
        }
        catch (Exception ex) { Console.WriteLine("Could not run the platform's command line: " + ex.Message); return 1; }

        using var server = StartPlatform();
        try
        {
            if (!await WaitForPort(_port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
            using var api = new HttpClient { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };

            // ---- the first way in ---------------------------------------------------------------
            Head("first way in: the admin key");
            var key = NewBrowser();
            var form = await Html(key, "/owner/login");
            Check("with no admins, the sign-in page offers the admin key", form.Contains("No admins have been added yet") && form.Contains("name=\"Key\""));
            var step = await Post(key, "/owner/login", form, ("Key", AdminKey));
            var keySecret = Regex.Match(step, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
            Check("the key's first sign-in sets up its authenticator", keySecret.Length == 32);
            var landed = await PostFollow(key, "/owner/login", step, ("UseKey", "true"), ("Pending", Field(step, "Pending")), ("Code", Totp.Now(keySecret)));
            Check("and lands on the dashboard", landed == "/owner", landed);

            // ---- adding the first Owner ------------------------------------------------------------
            Head("adding: the key adds the first Owner");
            var admins = await Html(key, "/owner/admins");
            Check("the Admins page offers every level to the key (an Owner)", Regex.IsMatch(Section(admins, "Add an admin"), "value=\"4\"[^>]*>Owner"));
            var added = await PortalPost(key, "/owner/admins", "Add", ("name", "Clint Owner"), ("email", Clint), ("level", "4"));
            Check("Add says a link has gone", added.Contains("a set-up link has gone to " + Clint), Snip(added, "alert"));
            var invite = await Mail.WaitFor(Clint, "set up your admin account");
            Check("the set-up link reaches them by email", invite != null);
            if (invite == null) return Done();
            Check("and only them — no copy of a password link", invite.To.Count == 1, string.Join(", ", invite.To));
            var link = SetupLink(invite);
            Check("it carries a one-use link to /owner/setup", link.Length > 0, link.Length > 0 ? link[..Math.Min(60, link.Length)] + "…" : "none");
            admins = await Html(key, "/owner/admins");
            Check("the list shows them as Not set up yet", Row(admins, Clint).Contains("Not set up yet"));
            var cantYet = NewBrowser();
            var early = WebUtility.HtmlDecode(await Post(cantYet, "/owner/login", await Html(cantYet, "/owner/login"), ("Email", Clint), ("Password", Pw)));
            Check("they cannot sign in before setting up", early.Contains("do not match an admin account that is set up"));

            // ---- setting up -------------------------------------------------------------------------
            Head("setting up from the link");
            var clint = NewBrowser();
            var open1 = await Html(clint, Rel(link));
            var open2 = await Html(clint, Rel(link));
            Check("the link says who it is for and what they may do", open1.Contains("Clint Owner") && open1.Contains("Owner</strong>"));
            Check("opening it twice spends nothing (mail filters open links first)", open2.Contains("name=\"Password\""));
            var mismatch = await Post(clint, Rel(link), open2, ("T", LinkToken(link)), ("Password", Pw), ("Again", Pw + "x"));
            Check("two different passwords are refused", mismatch.Contains("not the same"));
            var chosen = await Post(clint, Rel(link), mismatch, ("T", LinkToken(link)), ("Password", Pw), ("Again", Pw));
            var secret = Regex.Match(chosen, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
            Check("the password goes on to the authenticator's QR code", chosen.Contains("data:image/png;base64,") && secret.Length == 32);
            var wrong = await Post(clint, Rel(link), chosen, ("T", LinkToken(link)), ("Pending", Field(chosen, "Pending")), ("Code", WrongCode(secret)));
            Check("a wrong code is refused, and the QR stays", wrong.Contains("That code is not right") && wrong.Contains("data:image/png;base64,"));
            var nb = NewBrowser();
            var nothingYet = WebUtility.HtmlDecode(await Post(nb, "/owner/login", await Html(nb, "/owner/login"), ("Email", Clint), ("Password", Pw)));
            Check("nothing is saved before the right code — the password alone does not sign in", nothingYet.Contains("do not match"));
            landed = await PostFollow(clint, Rel(link), wrong, ("T", LinkToken(link)), ("Pending", Field(wrong, "Pending")), ("Code", Totp.Now(secret)));
            Phones[Clint] = secret;
            Check("the right code saves it all and signs them in", landed == "/owner", landed);
            var home = await Html(clint, "/owner/admins");
            Check("signed in as themselves, an Owner", home.Contains("You are <strong>Clint Owner</strong>, Owner"));
            Check("the link works once", (await Html(NewBrowser(), Rel(link))).Contains("not valid"));

            // ---- signing in ---------------------------------------------------------------------------
            Head("signing in: password, code, and a trusted browser");
            await NextCodeStep();   // the code used to set up is spent
            var (fresh, freshLanded, trust) = await AdminSignIn(Clint, Pw, Phones[Clint]);
            Check("email, password, then the code", freshLanded == "/owner", freshLanded);
            Check("and the browser is trusted for a week afterwards", trust.Length > 0);
            var again = TrustedBrowser(trust);
            var noCode = await PostFollow(again, "/owner/login", await Html(again, "/owner/login"), ("Email", Clint), ("Password", Pw));
            Check("a trusted browser is NOT asked for the code again", noCode == "/owner", noCode);
            var stranger = NewBrowser();
            var asked = await Post(stranger, "/owner/login", await Html(stranger, "/owner/login"), ("Email", Clint), ("Password", Pw));
            Check("another browser IS asked for it", asked.Contains("six-digit code") && Field(asked, "Pending").Length > 0);
            var badPw = WebUtility.HtmlDecode(await Post(stranger, "/owner/login", await Html(stranger, "/owner/login"), ("Email", Clint), ("Password", "not-the-password")));
            var nobody = WebUtility.HtmlDecode(await Post(stranger, "/owner/login", await Html(stranger, "/owner/login"), ("Email", "nobody@adm.test"), ("Password", "not-the-password")));
            Check("a wrong password says the same as an unknown email", badPw.Contains("do not match an admin account") && nobody.Contains("do not match an admin account"));

            // ---- the other levels ------------------------------------------------------------------
            Head("levels: Manager, Viewer, Full");
            var mandy = await AddAndSetUp(clint, "Mandy Manager", Mandy, 2);
            var vic = await AddAndSetUp(clint, "Vic Viewer", Vic, 1);
            var fred = await AddAndSetUp(clint, "Fred Full", Fred, 3);
            Check("an Owner adds a Manager, a Viewer and a Full admin, each set up from their own link", mandy != null && vic != null && fred != null);
            if (mandy == null || vic == null || fred == null) return Done();
            var tenantsPage = await Html(clint, "/owner/tenants");
            var tenantId = Regex.Match(tenantsPage, "name=\"id\" value=\"(\\d+)\"").Groups[1].Value;
            var serverId = Regex.Match(tenantsPage, "name=\"server\" value=\"(\\d+)\"").Groups[1].Value;

            // Viewer
            var vTenants = await Html(vic, "/owner/tenants");
            Check("a Viewer sees the companies", vTenants.Contains(Company));
            Check("and is told they change nothing — no Apply", vTenants.Contains("You are a Viewer") && !vTenants.Contains(">Apply</button>"));
            var vSuspend = await PostWithToken(vic, "/owner/tenants?handler=Act", vTenants, ("server", serverId), ("id", tenantId), ("act", "status"), ("status", "Suspended"));
            Check("a Viewer's suspend is REFUSED by the server", vSuspend.Contains("Your admin level is Viewer"), Snip(vSuspend, "alert-bad"));
            Check("and the company is still Active", (await Html(clint, "/owner/tenants")).Contains(">Active</span>"));
            var vAdd = await PostWithToken(vic, "/owner/admins?handler=Add", await Html(vic, "/owner/admins"), ("name", "Sneaky"), ("email", "sneaky@adm.test"), ("level", "4"));
            Check("a Viewer cannot add an admin", vAdd.Contains("Only a Full admin or an Owner"), Snip(vAdd, "alert-bad"));
            var vMail = await PostWithToken(vic, "/owner/mail?handler=Test", await Html(vic, "/owner/mail"), ("testTo", "x@adm.test"));
            Check("nor touch the platform's mail", vMail.Contains("Only a Full admin or an Owner can change the platform"));

            // Manager
            var mAdmins = await Html(mandy, "/owner/admins");
            Check("a Manager is offered no Add form", !mAdmins.Contains("id=\"addAdmin\""));
            var mAdd = await PostWithToken(mandy, "/owner/admins?handler=Add", mAdmins, ("name", "Sneaky"), ("email", "sneaky@adm.test"), ("level", "1"));
            Check("and is refused if they post one anyway", mAdd.Contains("Only a Full admin or an Owner"));

            // Full
            var fAdmins = await Html(fred, "/owner/admins");
            var fAddForm = Section(fAdmins, "Add an admin");
            Check("a Full admin is offered Viewer and Manager only", fAddForm.Contains(">Viewer") && fAddForm.Contains(">Manager") && !Regex.IsMatch(fAddForm, ">(Full|Owner) —"));
            var fFull = await PostWithToken(fred, "/owner/admins?handler=Add", fAdmins, ("name", "Too High"), ("email", "high@adm.test"), ("level", "3"));
            Check("and refused a Full admin", fFull.Contains("Only an Owner can make somebody a Full admin"));
            var fRemove = await PostWithToken(fred, "/owner/admins?handler=Remove", fAdmins, ("id", await AdminIdAsync(fred, Clint)));
            Check("nobody but the command line removes an Owner", fRemove.Contains("An Owner cannot be changed or removed here"));
            var gina = await AddAndSetUp(fred, "Gina Viewer", Gina, 1);
            Check("a Full admin adds a Viewer", gina != null);

            // Owner limits and live sign-out
            var cAdmins = await Html(clint, "/owner/admins");
            var self = await PostWithToken(clint, "/owner/admins?handler=Remove", cAdmins, ("id", await AdminIdAsync(clint, Clint)));
            Check("nobody removes themselves", self.Contains("You cannot change or remove yourself"));
            Check("Fred is in before his level changes", (await Landed(fred, "/owner/admins")) == "/owner/admins");
            var lower = await PostWithToken(clint, "/owner/admins?handler=Level", cAdmins, ("id", await AdminIdAsync(clint, Fred)), ("level", "2"));
            Check("an Owner makes the Full admin a Manager", lower.Contains("Level changed"));
            Check("which signs Fred out at once (his cookie carried the old level)", (await Landed(fred, "/owner/admins")) == "/owner/login");
            Check("Gina is in before she is removed", gina != null && (await Landed(gina, "/owner/tenants")) == "/owner/tenants");
            var gone = await PostWithToken(clint, "/owner/admins?handler=Remove", await Html(clint, "/owner/admins"), ("id", await AdminIdAsync(clint, Gina)));
            Check("an Owner removes a Viewer", gone.Contains("Removed"));
            Check("and her open browser is out at once", gina != null && (await Landed(gina, "/owner/tenants")) == "/owner/login");

            // ---- suspending ------------------------------------------------------------------------
            Head("suspending: everything of theirs leaves now, and their owners are told");
            var bossCodes = await EnrolByApi(api, Boss, BossPw);
            var bossPortal = await PortalSignIn(Boss, BossPw, bossCodes[0]);
            // The dashboard, or — the first time for an owner — Job types (HANDOVER §167): either is their portal.
            Check("the company's owner is in their portal", bossPortal.landed is "/portal" or "/portal/jobtypes", bossPortal.landed);
            var tok = await Api(api, "/api/login", new { email = Boss, password = BossPw, code = bossCodes[1] });
            var console = await OpenConsole(tok.Str("token"));
            var gotState = await Task.WhenAny(console.state.Task, Task.Delay(8000)) == console.state.Task;
            var machine = await FakeMachine.ConnectAsync($"ws://127.0.0.1:{_port}", _tenantKey);
            Check("their console and one of their machines are connected", gotState && machine.Accepted, $"console {gotState}, machine {machine.Accepted} {machine.Error}");
            var before = Mail.All.LastOrDefault();
            var mTenants = await Html(mandy, "/owner/tenants");
            var suspended = await PostWithToken(mandy, "/owner/tenants?handler=Act", mTenants, ("server", serverId), ("id", tenantId), ("act", "status"), ("status", "Suspended"));
            Check("a Manager suspends the company", suspended.Contains($"{Company} is now Suspended"), Snip(suspended, "alert-ok"));
            Check("and is told what that did", suspended.Contains("1 console signed out") && suspended.Contains("machine connection") && suspended.Contains("owners were emailed"), Snip(suspended, "alert-ok"));
            var signedOut = await Task.WhenAny(console.signedOut.Task, Task.Delay(5000)) == console.signedOut.Task ? console.signedOut.Task.Result : "";
            Check("the console is told and closed NOW", signedOut.Contains("suspended"), signedOut);
            var closed = await Task.WhenAny(machine.Closed.Task, Task.Delay(5000)) == machine.Closed.Task;
            Check("the machine is disconnected NOW", closed, machine.Error);
            Check("their open portal page is signed out", (await Landed(bossPortal.client, "/portal")) == "/login");
            var back = await FakeMachine.ConnectAsync($"ws://127.0.0.1:{_port}", _tenantKey);
            Check("and the machine is refused when it comes back", !back.Accepted, back.Error);
            var notice = await Mail.WaitFor(Boss, "suspended", before);
            Check("their owner is emailed that they are suspended", notice != null && notice.Body.Contains("Nothing has been deleted"));
            var restored = await PostWithToken(mandy, "/owner/tenants?handler=Act", await Html(mandy, "/owner/tenants"), ("server", serverId), ("id", tenantId), ("act", "status"), ("status", "Active"));
            Check("made Active again", restored.Contains($"{Company} is now Active"));
            Check("and the owner is emailed that too", await Mail.WaitFor(Boss, "active again", notice) != null);
            var returns = await FakeMachine.ConnectAsync($"ws://127.0.0.1:{_port}", _tenantKey);
            Check("their machine is let back in with nothing done at it", returns.Accepted, returns.Error);

            // ---- emailing --------------------------------------------------------------------------
            Head("emailing a company's owners");
            var page = await Html(mandy, $"/owner/email?server={serverId}&id={tenantId}");
            Check("the page says who it goes to", WebUtility.HtmlDecode(page).Contains("Bea Boss <" + Boss + ">"));
            var sent = await PostWithToken(mandy, $"/owner/email?server={serverId}&id={tenantId}", page, ("Server", serverId), ("Id", tenantId), ("Subject", "Your invoice"), ("Body", "Hello Bea, a question about your invoice."));
            Check("a Manager sends it", sent.Contains("Sent to " + Boss), Snip(sent, "alert"));
            var letter = await Mail.WaitFor(Boss, "Your invoice");
            Check("it arrives, signed with the Manager's name", letter != null && letter.Body.Contains("Mandy Manager"));
            Check("and replies go to the Manager", letter != null && letter.ReplyTo.Contains(Mandy), letter?.ReplyTo ?? "");

            // ---- the log ----------------------------------------------------------------------------
            Head("the log");
            var log = WebUtility.HtmlDecode(await Html(vic, "/owner/log"));
            Check("a Viewer can read the log", log.Contains("What admins did"));
            Check("it has the first Owner added by the key", log.Contains("Added Clint Owner (" + Clint + ") as Owner") && log.Contains("Platform owner (admin key)"));
            Check("the suspension, by name", Regex.IsMatch(log, "<td>Mandy Manager</td><td>Status Active → Suspended</td><td>" + Company));
            Check("the emails that went with it", log.Contains("Emailed: " + Company + ": your account has been suspended"));
            Check("the level change and the removal", log.Contains("Changed Fred Full from Full to Manager") && log.Contains("Removed Gina Viewer"));
            Check("the email to the owners", log.Contains("Emailed the owners: \"Your invoice\""));
            Check("nothing a Viewer was refused", !log.Contains("Sneaky"));

            // ---- the lock --------------------------------------------------------------------------
            Head("five wrong passwords lock the account");
            var guesser = NewBrowser();
            for (int i = 0; i < 5; i++) await Post(guesser, "/owner/login", await Html(guesser, "/owner/login"), ("Email", Vic), ("Password", "guess-" + i));
            var locked = WebUtility.HtmlDecode(await Post(guesser, "/owner/login", await Html(guesser, "/owner/login"), ("Email", Vic), ("Password", Pw)));
            Check("then even the right password is refused for a while", !locked.Contains("six-digit code") && Regex.IsMatch(locked, "(?i)too many|try again"), Snip(locked, "alert"));

            // ---- the key still works -----------------------------------------------------------------
            Head("the admin key is still a way in");
            var key2 = NewBrowser();
            var kForm = await Html(key2, "/owner/login?usekey=true");
            Check("with admins on file, the email form is first and the key is a link away", kForm.Contains("name=\"Key\"") && (await Html(NewBrowser(), "/owner/login")).Contains("Use the admin key instead"));
            var kStep = await Post(key2, "/owner/login", kForm, ("UseKey", "true"), ("Key", AdminKey));
            await NextCodeStep();
            var kLanded = await PostFollow(key2, "/owner/login", kStep, ("UseKey", "true"), ("Pending", Field(kStep, "Pending")), ("Code", Totp.Now(keySecret)));
            Check("and the key + its code still signs in", kLanded == "/owner", kLanded);

            // ---- the command line --------------------------------------------------------------------
            Head("the command line");
            var list = await Cli("list-admins");
            Check("list-admins lists every admin with their level", list.Contains(Clint) && list.Contains("Owner") && list.Contains(Fred) && list.Contains("Manager"));
            var cliAdd = await Cli("add-admin", "cli@adm.test", "Cli Person", "viewer");
            Check("add-admin adds one and mails the link", cliAdd.Contains("A set-up link has been emailed") && await Mail.WaitFor("cli@adm.test", "set up your admin account") != null, cliAdd.Trim());
            Check("Clint is in before the command line removes him", (await Landed(clint, "/owner/admins")) == "/owner/admins");
            var cliRemove = await Cli("remove-admin", Clint);
            Check("remove-admin may remove an Owner", cliRemove.Contains("Removed"));
            await Task.Delay(16000);   // the running server's stamp cache is 15 s
            Check("and the running server signs that Owner out", (await Landed(clint, "/owner/admins")) == "/owner/login");
        }
        catch (Exception ex) { Check("ran to the end", false, ex.ToString()); }
        finally { try { server.Kill(entireProcessTree: true); } catch { } Mail.Dispose(); }
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine($"admincheck: {_pass} of {_pass + _fail} passed");
        return _fail == 0 ? 0 : 1;
    }

    // ---- admins -------------------------------------------------------------------------------

    private static async Task<HttpClient?> AddAndSetUp(HttpClient by, string name, string email, int level)
    {
        var before = Mail.All.LastOrDefault();
        var r = await PortalPost(by, "/owner/admins", "Add", ("name", name), ("email", email), ("level", level.ToString()));
        if (!r.Contains("a set-up link has gone")) { Console.WriteLine("        add failed: " + Snip(r, "alert")); return null; }
        var m = await Mail.WaitFor(email, "set up your admin account", before);
        if (m == null) return null;
        var link = SetupLink(m);
        var b = NewBrowser();
        var open = await Html(b, Rel(link));
        var chosen = await Post(b, Rel(link), open, ("T", LinkToken(link)), ("Password", Pw), ("Again", Pw));
        var secret = Regex.Match(chosen, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
        var landed = await PostFollow(b, Rel(link), chosen, ("T", LinkToken(link)), ("Pending", Field(chosen, "Pending")), ("Code", Totp.Now(secret)));
        Phones[email] = secret;
        return landed == "/owner" ? b : null;
    }

    private static async Task<(HttpClient client, string landed, string trust)> AdminSignIn(string email, string password, string secret)
    {
        var jar = new CookieContainer();
        var c = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
        var step = await Post(c, "/owner/login", await Html(c, "/owner/login"), ("Email", email), ("Password", password));
        var landed = await PostFollow(c, "/owner/login", step, ("Pending", Field(step, "Pending")), ("Code", Totp.Now(secret)));
        var trust = jar.GetCookies(new Uri(_base + "/owner"))["rsp_owner_trust"]?.Value ?? "";
        return (c, landed, trust);
    }

    private static HttpClient TrustedBrowser(string trust)
    {
        var jar = new CookieContainer();
        jar.Add(new Uri(_base + "/owner"), new Cookie("rsp_owner_trust", trust, "/owner"));
        return new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>An admin's id, from their row on the Admins page (every row says it, form or not).</summary>
    private static async Task<string> AdminIdAsync(HttpClient asker, string email) =>
        Regex.Match(Row(await Html(asker, "/owner/admins"), email), "data-id=\"(\\d+)\"").Groups[1].Value;

    private static string Row(string html, string email)
    {
        var i = html.IndexOf($"data-admin=\"{email}\"", StringComparison.Ordinal);
        if (i < 0) return "";
        var end = html.IndexOf("</tr>", i, StringComparison.Ordinal);
        return html[i..(end < 0 ? html.Length : end)];
    }

    private static string SetupLink(Message m) => Regex.Match(m.Body, @"http://127\.0\.0\.1:\d+/owner/setup\?t=[0-9A-F]+").Value;
    private static string LinkToken(string link) => link[(link.IndexOf("?t=", StringComparison.Ordinal) + 3)..];
    private static string WrongCode(string secret) { var c = Totp.Now(secret); return c == "000000" ? "111111" : "000000"; }

    /// <summary>Waits into the next 30-second code step, so a code is not the one just used.</summary>
    private static Task NextCodeStep() => Task.Delay(30500 - (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 30000));

    /// <summary>Where a GET ends up after redirects — the page itself if still signed in, the sign-in page if not.</summary>
    private static async Task<string> Landed(HttpClient c, string path) => (await c.GetAsync(path)).RequestMessage?.RequestUri?.AbsolutePath ?? "?";

    private static async Task<string> PostWithToken(HttpClient c, string path, string pageWithToken, params (string k, string v)[] f)
    {
        var r = await c.PostAsync(path, Form(f.Append(("__RequestVerificationToken", Token(pageWithToken))).ToArray()));
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }

    private static string Rel(string link) => link.Length == 0 ? "/owner/setup" : new Uri(link).PathAndQuery;

    // ---- a pretend machine of the company's ---------------------------------------------------

    private sealed class FakeMachine
    {
        public bool Accepted;
        public string Error = "";
        public readonly TaskCompletionSource<bool> Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<FakeMachine> ConnectAsync(string ws, string tenantKey)
        {
            var f = new FakeMachine();
            var raw = new ClientWebSocket();
            await raw.ConnectAsync(new Uri($"{ws}/ws/device"), default);
            var c = new WsConnection(raw);
            var stated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            c.OnMessage += m =>
            {
                if (m is ErrorMsg e) { f.Error = e.Message; stated.TrySetResult(false); }
                else stated.TrySetResult(true);
            };
            c.OnClosed += _ => { f.Closed.TrySetResult(true); stated.TrySetResult(false); };
            _ = c.RunAsync();
            c.Send(new HelloMsg
            {
                Role = DeviceRole.Client, DeviceId = "admincheck-pc", DeviceKey = "admincheck-key", MachineName = "ADMINCHECK-PC", UserName = "FakeUser",
                Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = tenantKey,
                Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
            });
            f.Accepted = await Task.WhenAny(stated.Task, Task.Delay(5000)) == stated.Task && stated.Task.Result;
            return f;
        }
    }

    // ---- from invitecheck: a mail server, a browser, the company's sign-ins -----------------------
    private sealed class FakeSmtp : IDisposable
    {
        private TcpListener? _l;
        private readonly List<Message> _mails = new();
        public int Port { get; private set; }
        public int Count { get { lock (_mails) return _mails.Count; } }
        public List<Message> All { get { lock (_mails) return _mails.ToList(); } }

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
                await w.WriteLineAsync("220 invitecheck ESMTP");
                var to = new List<string>();
                while (await r.ReadLineAsync() is { } line)
                {
                    var cmd = line.ToUpperInvariant();
                    if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await w.WriteLineAsync("250 invitecheck");
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

        public async Task<Message?> WaitFor(string to, string subjectPart, Message? after = null, string? fromBy = null)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                List<Message> all; lock (_mails) all = _mails.ToList();
                var start = after == null ? 0 : all.IndexOf(after) + 1;
                var m = all.Skip(start).LastOrDefault(x => x.To.Contains(to) && x.Subject.Contains(subjectPart) && (fromBy == null || x.Body.Contains(fromBy)));
                if (m != null) return m;
                await Task.Delay(100);
            }
            return null;
        }

        public void Dispose() { try { _l?.Stop(); } catch { } }
    }

    /// <summary>One message, read as a mail program would: unfolded headers, decoded body.</summary>
    private sealed class Message
    {
        public List<string> To { get; }
        public string From = "", FromName = "", ReplyTo = "", Subject = "", Body = "";
        public Message(List<string> to, string raw)
        {
            To = to;
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = Regex.Replace(split < 0 ? raw : raw[..split], "\r\n[ \t]+", " ");
            var body = split < 0 ? "" : raw[(split + 4)..];
            string H(string n) => Regex.Match(head, $"^{n}:\\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            From = Words(H("From")); ReplyTo = Words(H("Reply-To")); Subject = Words(H("Subject"));
            FromName = Regex.Match(From, "^\"?([^\"<]+?)\"?\\s*<").Groups[1].Value.Trim();
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

        /// <summary>RFC 2047 encoded words (=?utf-8?B?…?= and =?utf-8?Q?…?=) back to text.</summary>
        private static string Words(string v) => Regex.Replace(v, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
            m.Groups[2].Value.ToUpperInvariant() == "B"
                ? Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[3].Value))
                : QuotedPrintable(m.Groups[3].Value.Replace('_', ' ')));
    }

    // ---- the browser ----------------------------------------------------------------------

    private static HttpClient NewBrowser() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<string> Html(HttpClient c, string path) => await (await c.GetAsync(path)).Content.ReadAsStringAsync();

    private static async Task<string> Post(HttpClient c, string path, string pageWithToken, params (string k, string v)[] f)
    {
        var r = await c.PostAsync(path, Form(f.Append(("__RequestVerificationToken", Token(pageWithToken))).ToArray()));
        return await r.Content.ReadAsStringAsync();
    }

    private static async Task<string> PostFollow(HttpClient c, string path, string pageWithToken, params (string k, string v)[] f)
    {
        var r = await c.PostAsync(path, Form(f.Append(("__RequestVerificationToken", Token(pageWithToken))).ToArray()));
        return r.RequestMessage?.RequestUri?.AbsolutePath ?? "?";
    }

    private static async Task<string> PortalPost(HttpClient c, string page, string handler, params (string k, string v)[] fields)
    {
        var html = await Html(c, page);
        var r = await c.PostAsync($"{page}?handler={handler}", Form(fields.Append(("__RequestVerificationToken", Token(html))).ToArray()));
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpClient client, string landed)> PortalSignIn(string email, string password, string code)
    {
        var c = NewBrowser();
        var form = await Html(c, "/login");
        var step = await Post(c, "/login", form, ("Email", email), ("Password", password));
        var landed = await PostFollow(c, "/login", step, ("Mode", Field(step, "Mode")), ("Pending", Field(step, "Pending")), ("Code", code));
        return (c, landed);
    }

    private static async Task<string> Forgot(string email)
    {
        var c = NewBrowser();
        var page = await Html(c, "/forgot");
        return WebUtility.HtmlDecode(await Post(c, "/forgot", page, ("Email", email)));
    }

    private static string Answer(string html) => Regex.Match(html, "<div class=\"alert alert-ok\">(.*?)</div>", RegexOptions.Singleline).Groups[1].Value;

    private static async Task<List<string>> EnrolByApi(HttpClient api, string email, string password)
    {
        var a = await Api(api, "/api/login", new { email, password });
        var b = await Api(api, "/api/login/enrol", new { pending = a.Str("pending"), code = Totp.Now(a.Str("secret")) });
        return b.Arr("recoveryCodes");
    }

    private sealed record ApiAnswer(HttpStatusCode status, JsonElement doc)
    {
        public string Str(string n) => doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        public List<string> Arr(string n) => doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
    }

    private static async Task<ApiAnswer> Api(HttpClient http, string path, object body)
    {
        var r = await http.PostAsJsonAsync(path, body);
        JsonElement doc; try { doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement; } catch { doc = default; }
        return new ApiAnswer(r.StatusCode, doc);
    }

    private sealed class ConsoleSocket
    {
        public string status = "";
        public readonly TaskCompletionSource<bool> state = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> signedOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<ConsoleSocket> OpenConsole(string token)
    {
        var s = new ConsoleSocket();
        var cws = new ClientWebSocket();
        cws.Options.CollectHttpResponseDetails = true;
        try
        {
            await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws/tech?token={token}"), CancellationToken.None);
            s.status = "open";
            var conn = new WsConnection(cws);
            conn.OnMessage += m => { if (m is StateMsg) s.state.TrySetResult(true); if (m is SignedOutMsg so) s.signedOut.TrySetResult(so.Reason); };
            _ = conn.RunAsync(CancellationToken.None);
        }
        catch { s.status = ((int)cws.HttpStatusCode).ToString(); }
        return s;
    }

    // ---- small things ----------------------------------------------------------------------

    private static string Token(string linkOrPage) =>
        linkOrPage.Contains("/account/setup?t=") && !linkOrPage.Contains("<")
            ? linkOrPage[(linkOrPage.IndexOf("?t=", StringComparison.Ordinal) + 3)..]
            : Regex.Match(linkOrPage, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    private static string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    private static string Section(string html, string heading) { var i = html.IndexOf(heading, StringComparison.Ordinal); return i < 0 ? "" : html[i..Math.Min(html.Length, i + 3000)]; }
    private static string Snip(string html, string near) { var i = html.IndexOf(near, StringComparison.OrdinalIgnoreCase); return i < 0 ? "" : Regex.Replace(html.Substring(Math.Max(0, i - 60), Math.Min(200, html.Length - Math.Max(0, i - 60))), "\\s+", " "); }
    private static FormUrlEncodedContent Form(params (string k, string v)[] f) => new(f.Select(x => new KeyValuePair<string, string>(x.k, x.v)));

    private static Process StartPlatform()
    {
        var psi = Psi();
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> Cli(params string[] args)
    {
        var psi = Psi();
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    // Two-factor is NOT turned off: this is how a live server behaves.
    private static ProcessStartInfo Psi()
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_proj);
        psi.Environment["RSP_Platform__DataDirectory"] = _dataDir;
        HarnessDb.Use(psi.Environment, _dataDir);
        psi.Environment["RSP_Platform__PublicUrl"] = _base;
        psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
        return psi;
    }

    private static async Task<bool> WaitForPort(int port, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            try { using var c = new TcpClient(); await c.ConnectAsync("127.0.0.1", port); return true; }
            catch { await Task.Delay(400); }
        }
        return false;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start(); int p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }
}
