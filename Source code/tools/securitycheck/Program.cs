using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// securitycheck — signing in safely, before go-live (2026-10-01, HANDOVER §72).
//
// Everything is tested against a REAL platform with two-factor sign-in ON, as it will be live:
//
//   THE TRANSPORT RULE   apps refuse plain http/ws to anything but a private address.
//   THE ARITHMETIC       authenticator codes match RFC 6238's own published test values.
//   SETTING UP           password → QR code → first code → ten recovery codes.
//   CODES                each code works once; the next one works; recovery codes work once each.
//   SIGNING OUT          from the People page (at once) and from the command line (a separate
//                        process, so found by the sweep) — a live console is told and closed, its
//                        token dies, and a portal cookie stops working on the next page.
//   GUESSING             five wrong tries lock the account; thirty from one address lock the
//                        ADDRESS — tested through X-Forwarded-For, which proves the server sees
//                        the caller behind Caddy, and not Caddy.
//   HEADERS              no framing (except website chat), nosniff, HSTS over https.
//   THE PLATFORM OWNER   admin key → QR → code; reset-owner-2fa signs the owner out.
//
// A platform of its own, its own port, an empty database in the temp folder. The live server on
// 8090 and its database are not involved.
//
//   dotnet run -c Release --project tools/securitycheck/securitycheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class SecurityCheck
{
    private static int _pass, _fail;
    private const string Pw = "Security!2026", NewPw = "Changed!2026";
    private const string Tech = "tech@sec.test", Admin = "boss@sec.test", Gone = "gone@sec.test", Locked = "lock@sec.test", Known = "known@sec.test";
    private const string AdminKey = "securitycheck-admin-key-0123456789abcdef0123456789abcdef";
    private static string _slug = "sec-test-co", _base = "", _proj = "", _dataDir = "";
    private static int _port;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("securitycheck — signing in safely");

        // ---- 1. the transport rule (no platform needed) -------------------------------------
        Head("the transport rule: never unencrypted across the internet");
        foreach (var (url, allowed) in new[]
        {
            ("https://theremotesupportprogram.com", true), ("wss://theremotesupportprogram.com", true),
            ("http://theremotesupportprogram.com", false), ("ws://theremotesupportprogram.com", false),
            ("http://8.8.8.8:8090", false), ("http://172.32.0.1", false),
            ("ws://192.168.1.115:8090", true), ("http://10.0.0.5", true), ("http://172.20.1.1", true),
            ("http://127.0.0.1:8090", true), ("ws://localhost:8090", true), ("http://officeserver:8090", true),
            ("http://[::1]:8090", true), ("ftp://example.com", false)
        })
            Check($"{url} is {(allowed ? "allowed" : "refused")}", (SecureTransport.Refusal(url) == null) == allowed,
                  SecureTransport.Refusal(url) ?? "allowed");

        // ---- 2. the arithmetic -------------------------------------------------------------
        Head("authenticator codes, against RFC 6238's published values");
        // RFC 6238 Appendix B: the ASCII secret "12345678901234567890", SHA-1. The RFC prints eight
        // digits; an app shows the last six.
        var rfc = Base32.Encode(System.Text.Encoding.ASCII.GetBytes("12345678901234567890"));
        foreach (var (unix, eight) in new[] { (59L, "94287082"), (1111111109L, "07081804"), (1234567890L, "89005924"), (2000000000L, "69279037") })
            Check($"T={unix}", Totp.Code(rfc, unix / 30) == eight[2..], $"got {Totp.Code(rfc, unix / 30)}, RFC {eight[2..]}");
        Check("a code from one step either side is accepted, two away is not",
              Totp.Match(rfc, Totp.Code(rfc, Totp.StepAt(DateTime.UtcNow) + 1), DateTime.UtcNow) != null
              && Totp.Match(rfc, Totp.Code(rfc, Totp.StepAt(DateTime.UtcNow) + 2), DateTime.UtcNow) == null);

        // ---- the platform --------------------------------------------------------------------
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        _dataDir = Path.Combine(Path.GetTempPath(), "rs-securitycheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_dataDir);
        _port = FreePort();
        _proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        _base = $"http://127.0.0.1:{_port}";
        Console.WriteLine();
        Console.WriteLine($"A platform of its own on port {_port}, database in {_dataDir}, two-factor REQUIRED");

        try
        {
            // Built once, here: the command line is run WHILE the server is up (set-password,
            // reset-owner-2fa), and a `dotnet run` that builds would fight the running copy for its files.
            var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var buildOut = await build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();
            if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }

            var created = await Cli("create-tenant", "Sec Test Co", _slug, "10");
            if (!created.Contains("Tenant key:")) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            foreach (var (email, flag) in new[] { (Tech, "--technician"), (Admin, "--admin"), (Gone, "--technician"), (Locked, "--technician"), (Known, "--technician") })
            {
                var added = await Cli("add-tech", _slug, email, Pw, email.Split('@')[0], flag);
                if (!added.Contains("Added")) { Console.WriteLine("Could not add a person:\n" + added); return 1; }
            }
        }
        catch (Exception ex) { Console.WriteLine("Could not run the platform's command line: " + ex.Message); return 1; }

        using var server = StartPlatform();
        try
        {
            if (!await WaitForPort(_port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
            using var http = new HttpClient { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };

            // ---- 3. setting up ---------------------------------------------------------------
            Head("setting up: password, QR code, first code, recovery codes");
            var first = await Api(http, "/api/login", new { email = Tech, password = Pw });
            Check("the password alone does not sign in", first.status == HttpStatusCode.Unauthorized && first.Str("token") == "", $"HTTP {(int)first.status}");
            Check("it asks to set up an authenticator", first.Str("step") == "enrol", $"step '{first.Str("step")}'");
            var png = Convert.FromBase64String(first.Str("qrPng"));
            Check("with a QR code that is a real PNG", png.Length > 200 && png[0] == 0x89 && png[1] == (byte)'P' && png[2] == (byte)'N', $"{png.Length} bytes");
            var secret = first.Str("secret");
            Check("and the key to type by hand", secret.Length == 32 && first.Str("secretGrouped").Contains(' '), first.Str("secretGrouped"));
            Check("an OLD console is told to update, in words", first.Str("error").Contains("too old"), first.Str("error"));

            var wrong = Totp.Code(secret, Totp.StepAt(DateTime.UtcNow)) == "000000" ? "111111" : "000000";
            var bad = await Api(http, "/api/login/enrol", new { pending = first.Str("pending"), code = wrong });
            Check("a wrong first code does not finish it", bad.Str("token") == "" && bad.Str("step") == "enrol" && bad.Str("wrong").Length > 0, bad.Str("wrong"));

            long setStep = Totp.StepAt(DateTime.UtcNow);
            var set = await Api(http, "/api/login/enrol", new { pending = first.Str("pending"), code = Totp.Code(secret, setStep) });
            var codes = set.Arr("recoveryCodes");
            Check("the right code finishes it and signs in", set.status == HttpStatusCode.OK && set.Str("token").Length > 0, $"HTTP {(int)set.status}");
            Check("with ten recovery codes, shown this once", codes.Count == 10 && codes.All(c => Regex.IsMatch(c, "^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$")), string.Join(" ", codes.Take(2)) + " …");

            var again = await Api(http, "/api/login", new { email = Tech, password = Pw });
            Check("from now on the password leads to the code step", again.Str("step") == "code" && again.Str("token") == "", $"step '{again.Str("step")}'");

            // ---- 4. codes ----------------------------------------------------------------------
            Head("each code works once");
            var reused = await Api(http, "/api/login/code", new { pending = again.Str("pending"), code = Totp.Code(secret, setStep) });
            Check("the code just used is refused the second time", reused.Str("token") == "", reused.Str("wrong"));
            var next = await Api(http, "/api/login/code", new { pending = again.Str("pending"), code = Totp.Code(secret, setStep + 1) });
            var token = next.Str("token");
            Check("the next code is accepted", token.Length > 0, $"HTTP {(int)next.status}");
            var rc0 = await Api(http, "/api/login", new { email = Tech, password = Pw, code = codes[0] });
            Check("a recovery code signs in", rc0.Str("token").Length > 0, $"HTTP {(int)rc0.status}");
            var rc0b = await Api(http, "/api/login", new { email = Tech, password = Pw, code = codes[0] });
            Check("the same recovery code twice is refused", rc0b.Str("token") == "", rc0b.Str("error"));
            var rc1 = await Api(http, "/api/login", new { email = Tech, password = Pw, code = codes[1].Replace("-", "").ToLowerInvariant() });
            Check("a recovery code typed in lower case without dashes still works", rc1.Str("token").Length > 0, $"HTTP {(int)rc1.status}");

            // ---- 5. a token ---------------------------------------------------------------------
            Head("a console token");
            // The LATEST console sign-in: each one closes the one before (one at a time, §82).
            var console1 = await OpenConsole(rc1.Str("token"));
            Check("opens the console socket", console1.state.Task.Wait(TimeSpan.FromSeconds(10)), console1.status);
            var bogus = await OpenConsole("not-a-token");
            Check("a token nobody issued is turned away with 401", bogus.status == "401", bogus.status);

            // ---- 5b. the code once a week (2026-10-02) ------------------------------------------
            // Clint: "sign in requires two factor authentication on every sign in. can we change that
            // to require authentication every 7 days?" This platform trusts for 43 s, not 7 days, so
            // running out can be watched (section 11b).
            Head("the code once a week, not at every sign-in");
            var trust = next.Str("trust");
            var trustIssued = Stopwatch.StartNew();
            Check("a sign-in with a code hands the console a trust token", trust.Length > 40, $"{trust.Length} chars");
            var trusted = await Api(http, "/api/login", new { email = Tech, password = Pw, trust });
            Check("with it, the password alone signs in — no code asked", trusted.Str("token").Length > 0 && trusted.Str("step") == "",
                  $"HTTP {(int)trusted.status}, step '{trusted.Str("step")}'");
            Check("...and that does NOT renew it, so the code is asked again when it runs out", trusted.Str("trust") == "");
            var wrongWithTrust = await Api(http, "/api/login", new { email = Tech, password = "not-the-password", trust });
            Check("the password is still needed: a wrong one is refused", wrongWithTrust.Str("token") == "" && wrongWithTrust.Str("step") == "", wrongWithTrust.Str("error"));
            var forged = await Api(http, "/api/login", new { email = Tech, password = Pw, trust = trust[..^6] + (trust[^6..] == "AAAAAA" ? "BBBBBB" : "AAAAAA") });
            Check("a token altered at all is ignored — the code is asked", forged.Str("step") == "code" && forged.Str("token") == "", $"step '{forged.Str("step")}'");
            var notTheirs = await Api(http, "/api/login", new { email = Admin, password = Pw, trust });
            Check("one person's token does nothing for another's account", notTheirs.Str("token") == "" && notTheirs.Str("step") != "", $"step '{notTheirs.Str("step")}'");

            var browser = await PortalSignIn(Tech, Pw, codes[5]);
            Check("in a browser, a sign-in with a code leaves the trust cookie", browser.landed == "/portal/tickets" && HasCookie(browser.client, "rsp_trust"), $"landed on {browser.landed}");
            await PortalSignOut(browser.client);
            var back = await PortalPasswordOnly(browser.client, Tech, Pw);
            Check("signed out and back in on that browser: the password alone", back.landed == "/portal/tickets", $"landed on {back.landed}");
            var other = await PortalPasswordOnly(NewBrowser(), Tech, Pw);
            Check("on any other browser the code is still asked", other.landed == "/login" && other.html.Contains("name=\"Mode\" value=\"code\""), $"landed on {other.landed}");

            // The trusted sign-in above is now their one console (§82) — watch IT be signed out.
            token = trusted.Str("token");
            console1 = await OpenConsole(token);
            Check("the console signed in last is the one connected", console1.state.Task.Wait(TimeSpan.FromSeconds(10)), console1.status);

            // ---- 6. signing out from the People page -------------------------------------------
            Head("signing somebody out from the People page — at once");
            var adminSet = await Enrol(http, Admin, Pw);
            var boss = await PortalSignIn(Admin, Pw, adminSet.codes[0]);
            Check("a supervisor signs in to the portal with password and code", boss.landed == "/portal", $"landed on {boss.landed}");
            var sw = Stopwatch.StartNew();
            var signOut = await PortalPost(boss.client, "/portal/technicians", "SignOut", ("email", Tech));
            Check("Sign out on the People page says so", signOut.Contains("Signed out of every console"), "");
            bool told = console1.signedOut.Task.Wait(TimeSpan.FromSeconds(5));
            Check("their running console is told and closed", told, told ? $"{sw.ElapsedMilliseconds} ms: \"{console1.signedOut.Task.Result}\"" : "nothing within 5 s");
            var afterOut = await OpenConsole(token);
            Check("and its token no longer opens anything", afterOut.status == "401", afterOut.status);
            var trustAfterOut = await Api(http, "/api/login", new { email = Tech, password = Pw, trust });
            Check("and the week's trust is gone with it — the code is asked", trustAfterOut.Str("step") == "code",
                  $"step '{trustAfterOut.Str("step")}', {trustIssued.Elapsed.TotalSeconds:F0} s after it was issued (it lasts 43)");

            // ---- 7. the command line: another process -----------------------------------------
            Head("a password changed from the command line — another process, found by the sweep");
            var techPortal = await PortalSignIn(Tech, Pw, codes[2]);
            Check("the technician is signed in to the portal", techPortal.landed == "/portal/tickets", $"landed on {techPortal.landed}");
            var t3 = await Api(http, "/api/login", new { email = Tech, password = Pw, code = codes[3] });
            var console3 = await OpenConsole(t3.Str("token"));
            Check("and to a console", console3.state.Task.Wait(TimeSpan.FromSeconds(10)), console3.status);
            sw.Restart();
            var changed = await Cli("set-password", _slug, Tech, NewPw);
            Check("set-password on the command line", changed.Contains("Password updated"), changed.Trim());
            bool told3 = console3.signedOut.Task.Wait(TimeSpan.FromSeconds(25));
            Check("the console is signed out within the sweep (15 s)", told3, told3 ? $"{sw.ElapsedMilliseconds} ms after the command started" : "nothing within 25 s");
            var page = await techPortal.client.GetAsync("/portal/tickets");
            var pageHtml = await page.Content.ReadAsStringAsync();
            Check("the portal cookie stops working on the next page", page.RequestMessage?.RequestUri?.AbsolutePath == "/login" && pageHtml.Contains("Sign in to your portal"),
                  $"landed on {page.RequestMessage?.RequestUri?.AbsolutePath}");
            var oldPw = await Api(http, "/api/login", new { email = Tech, password = Pw });
            var newPw = await Api(http, "/api/login", new { email = Tech, password = NewPw });
            Check("the old password is refused and the new one asks for the code", oldPw.Str("step") == "" && newPw.Str("step") == "code",
                  $"old: '{oldPw.Str("error")}', new: step '{newPw.Str("step")}'");

            // ---- 8. removing somebody -----------------------------------------------------------
            Head("removing somebody");
            var goneSet = await Enrol(http, Gone, Pw);
            var consoleGone = await OpenConsole(goneSet.token);
            Check("they are signed in to a console", consoleGone.state.Task.Wait(TimeSpan.FromSeconds(10)), consoleGone.status);
            sw.Restart();
            var removed = await PortalPost(boss.client, "/portal/technicians", "Remove", ("email", Gone));
            Check("Remove on the People page", removed.Contains("Removed."), "");
            bool toldGone = consoleGone.signedOut.Task.Wait(TimeSpan.FromSeconds(5));
            Check("their console is closed at once — not at the next restart", toldGone, toldGone ? $"{sw.ElapsedMilliseconds} ms: \"{consoleGone.signedOut.Task.Result}\"" : "nothing within 5 s");

            // ---- 9. lost phone ------------------------------------------------------------------
            Head("a lost phone: Reset two-factor");
            var reset = await PortalPost(boss.client, "/portal/technicians", "ResetTwoFactor", ("email", Tech));
            Check("Reset two-factor on the People page", reset.Contains("Two-factor sign-in reset"), "");
            var fresh = await Api(http, "/api/login", new { email = Tech, password = NewPw });
            Check("their next sign-in sets up an authenticator again", fresh.Str("step") == "enrol", $"step '{fresh.Str("step")}'");
            var oldRecovery = await Api(http, "/api/login", new { email = Tech, password = NewPw, code = codes[4] });
            Check("and the old recovery codes are gone with it", oldRecovery.Str("token") == "", oldRecovery.Str("step"));

            // ---- 10. headers --------------------------------------------------------------------
            Head("headers");
            var login = await http.GetAsync("/login");
            Check("pages cannot be framed by another site", H(login, "X-Frame-Options") == "DENY" && H(login, "Content-Security-Policy").Contains("frame-ancestors 'none'"));
            Check("nosniff and a referrer policy", H(login, "X-Content-Type-Options") == "nosniff" && H(login, "Referrer-Policy").Length > 0);
            Check("no HSTS over plain http", H(login, "Strict-Transport-Security") == "");
            using (var req = new HttpRequestMessage(HttpMethod.Get, "/login"))
            {
                req.Headers.Add("X-Forwarded-Proto", "https");
                var viaCaddy = await http.SendAsync(req);
                Check("behind Caddy (X-Forwarded-Proto: https) HSTS is sent", H(viaCaddy, "Strict-Transport-Security").Contains("max-age"), H(viaCaddy, "Strict-Transport-Security"));
            }
            var frame = await http.GetAsync($"/webchat/{_slug}/frame");
            Check("website chat's frame CAN be framed — that is its job", H(frame, "X-Frame-Options") == "" && (int)frame.StatusCode < 500, $"HTTP {(int)frame.StatusCode}");
            // The pentest's F4, F9, F10 (2026-10-23).
            var ro = H(login, "Content-Security-Policy-Report-Only");
            Check("the full policy is sent report-only, with no inline script and somewhere to report",
                ro.Contains("script-src 'self';") && ro.Contains("object-src 'none'") && ro.Contains("frame-ancestors 'none'") && ro.Contains("report-uri /api/csp-report"), ro);
            Check("...and without frame-ancestors on website chat's frame", H(frame, "Content-Security-Policy-Report-Only") is { Length: > 0 } fro && !fro.Contains("frame-ancestors"));
            var report = await http.PostAsync("/api/csp-report", new StringContent("{\"csp-report\":{\"violated-directive\":\"script-src\"}}", System.Text.Encoding.UTF8, "application/csp-report"));
            Check("a report is taken without a token", (int)report.StatusCode == 204, $"HTTP {(int)report.StatusCode}");
            var noTicket = await boss.client.GetAsync("/portal/tickets/999999");
            Check("a ticket that is not there is a 404 (F6), still saying so", (int)noTicket.StatusCode == 404 && (await noTicket.Content.ReadAsStringAsync()).Contains("Ticket not found"),
                  $"HTTP {(int)noTicket.StatusCode}");
            var health = await http.GetStringAsync("/health");
            Check("/health says it is up and nothing more", health == "{\"ok\":true}", health);
            var sec = await http.GetAsync("/.well-known/security.txt");
            var secText = await sec.Content.ReadAsStringAsync();
            Check("/.well-known/security.txt names a contact and an expiry", sec.IsSuccessStatusCode && secText.Contains("Contact: mailto:") && secText.Contains("Expires: "), secText.Replace("\n", " | "));

            // ---- 11. guessing -------------------------------------------------------------------
            Head("guessing");
            for (int i = 0; i < 5; i++) await Api(http, "/api/login", new { email = Locked, password = "wrong-" + i });
            var lockedOut = await Api(http, "/api/login", new { email = Locked, password = Pw });
            Check("after five wrong passwords even the right one is refused", lockedOut.Str("step") == "" && lockedOut.Str("error").StartsWith("Too many"), lockedOut.Str("error"));
            const string Far = "203.0.113.77";
            for (int i = 0; i < 30; i++) await Api(http, "/api/login", new { email = $"nobody{i}@sec.test", password = "x" }, forwardedFor: Far);
            var fromFar = await Api(http, "/api/login", new { email = Admin, password = Pw }, forwardedFor: Far);
            Check("thirty failures from one address lock that ADDRESS", fromFar.Str("error").StartsWith("Too many"), fromFar.Str("error"));
            var fromHere = await Api(http, "/api/login", new { email = Admin, password = Pw });
            Check("while the same account from elsewhere is not — the server sees the real caller behind the proxy", fromHere.Str("step") == "code",
                  $"step '{fromHere.Str("step")}' {fromHere.Str("error")}");

            // The pentest's F1 (2026-10-23): five wrong passwords every fifteen minutes from anywhere kept a known
            // address's owner out for good. A machine they typed their code on this week is now counted on its own.
            var knownSet = await Enrol(http, Known, Pw);
            var knownTrust = (await Api(http, "/api/login", new { email = Known, password = Pw, code = knownSet.codes[0] })).Str("trust");
            const string Stranger = "198.51.100.9";
            for (int i = 0; i < 5; i++) await Api(http, "/api/login", new { email = Known, password = "wrong-" + i }, forwardedFor: Stranger);
            var strangerNow = await Api(http, "/api/login", new { email = Known, password = Pw }, forwardedFor: Stranger);
            Check("a stranger's five wrong passwords lock the account to anybody without its trust", strangerNow.Str("error").StartsWith("Too many"), strangerNow.Str("error"));
            var knownNow = await Api(http, "/api/login", new { email = Known, password = Pw, trust = knownTrust });
            Check("but NOT on the machine its owner typed the code on this week", knownNow.Str("token").Length > 0,
                  $"step '{knownNow.Str("step")}' {knownNow.Str("error")}");
            for (int i = 0; i < 5; i++) await Api(http, "/api/login", new { email = Known, password = "wrong-" + i, trust = knownTrust });
            var knownLocked = await Api(http, "/api/login", new { email = Known, password = Pw, trust = knownTrust });
            Check("and that machine's own five wrong passwords still lock it", knownLocked.Str("error").StartsWith("Too many"), knownLocked.Str("error"));

            // ---- 11b. the trust runs out ------------------------------------------------------------
            Head("the trust runs out (43 s here, 7 days for real)");
            var adminCode = await Api(http, "/api/login", new { email = Admin, password = Pw, code = adminSet.codes[1] });
            var adminTrust = adminCode.Str("trust");
            var stillGood = await Api(http, "/api/login", new { email = Admin, password = Pw, trust = adminTrust });
            Check("fresh, it lets the password alone in", stillGood.Str("token").Length > 0, $"step '{stillGood.Str("step")}'");
            await Task.Delay(45000);
            var expired = await Api(http, "/api/login", new { email = Admin, password = Pw, trust = adminTrust });
            Check("once it has run out, the code is asked again", expired.Str("step") == "code" && expired.Str("token") == "", $"step '{expired.Str("step")}'");

            // ---- 12. the platform owner -----------------------------------------------------------
            Head("the platform owner");
            var owner = NewBrowser();
            var keyPage = await owner.GetStringAsync("/owner/login");
            var wrongKey = await (await owner.PostAsync("/owner/login", Form(("Key", "not-the-key"), ("__RequestVerificationToken", Token(keyPage))))).Content.ReadAsStringAsync();
            Check("a wrong admin key is refused", wrongKey.Contains("Invalid key"));
            var step2 = await (await owner.PostAsync("/owner/login", Form(("Key", AdminKey), ("__RequestVerificationToken", Token(wrongKey))))).Content.ReadAsStringAsync();
            var ownerSecret = Regex.Match(step2, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
            Check("the right key alone does not sign in — the first time, it shows a QR code", step2.Contains("data:image/png;base64,") && ownerSecret.Length == 32, $"key {ownerSecret.Length} chars");
            // 2026-10-15: /api/admin/status and /api/admin/tenants took the key alone in an X-Admin-Key header,
            // with no code, and listed every company. Nothing used them; they are gone. The key must open nothing by itself.
            using (var keyOnly = new HttpClient { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) })
            {
                keyOnly.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
                foreach (var path in new[] { "/api/admin/status", "/api/admin/tenants" })
                {
                    var r = await keyOnly.GetAsync(path);
                    var body = await r.Content.ReadAsStringAsync();
                    Check($"the key alone opens nothing at {path}", r.StatusCode != HttpStatusCode.OK && !body.Contains("\"tenants\"") && !body.Contains("\"slug\""),
                        $"HTTP {(int)r.StatusCode}");
                }
            }
            var pending = Regex.Match(step2, "name=\"Pending\" value=\"([^\"]+)\"").Groups[1].Value;
            var done = await owner.PostAsync("/owner/login", Form(("Pending", pending), ("Code", Totp.Now(ownerSecret)), ("__RequestVerificationToken", Token(step2))));
            Check("key and code sign the owner in", done.RequestMessage?.RequestUri?.AbsolutePath == "/owner", $"landed on {done.RequestMessage?.RequestUri?.AbsolutePath}");
            await Cli("reset-owner-2fa");
            await Task.Delay(16000);   // the owner stamp is cached for 15 s
            var later = await owner.GetAsync("/owner");
            Check("reset-owner-2fa signs every owner browser out", later.RequestMessage?.RequestUri?.AbsolutePath == "/owner/login", $"landed on {later.RequestMessage?.RequestUri?.AbsolutePath}");

            Console.WriteLine();
            Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            try { await Task.Delay(300); Directory.Delete(_dataDir, true); } catch { }
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private sealed record Answer(HttpStatusCode status, JsonElement doc)
    {
        public string Str(string n) => doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        public List<string> Arr(string n) => doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
    }

    /// <summary>
    /// Every console sign-in here comes from ONE PC, as a real console says with its own id
    /// (one sign-in at a time, 2026-10-05, §82): without it, each sign-in would be "somewhere else"
    /// and ask the code over a good trust token. onesignin is where other PCs are tested.
    /// </summary>
    private const string HarnessPc = "securitycheckpc00000001";

    private static async Task<Answer> Api(HttpClient http, string path, object body, string? forwardedFor = null)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body))!.AsObject();
        if (path == "/api/login" && !json.ContainsKey("device")) json["device"] = HarnessPc;
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(json) };
        if (forwardedFor != null) req.Headers.Add("X-Forwarded-For", forwardedFor);
        var r = await http.SendAsync(req);
        var text = await r.Content.ReadAsStringAsync();
        JsonElement doc;
        try { doc = JsonDocument.Parse(text).RootElement; } catch { doc = default; }
        return new Answer(r.StatusCode, doc);
    }

    /// <summary>Sets up an authenticator for somebody through the console's API, returning their token and recovery codes.</summary>
    private static async Task<(string token, List<string> codes)> Enrol(HttpClient http, string email, string password)
    {
        var a = await Api(http, "/api/login", new { email, password });
        var s = a.Str("secret");
        var b = await Api(http, "/api/login/enrol", new { pending = a.Str("pending"), code = s.Length > 0 ? Totp.Now(s) : "" });
        return (b.Str("token"), b.Arr("recoveryCodes"));
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
            conn.OnMessage += m =>
            {
                if (m is StateMsg) s.state.TrySetResult(true);
                if (m is SignedOutMsg so) s.signedOut.TrySetResult(so.Reason);
            };
            _ = conn.RunAsync(CancellationToken.None);
        }
        catch { s.status = ((int)cws.HttpStatusCode).ToString(); }
        return s;
    }

    private static HttpClient NewBrowser()
    {
        var jar = new CookieContainer();
        var c = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
        _jars[c] = jar;
        return c;
    }

    /// <summary>The portal's sign-in form, both steps, as a browser does it. Returns where it landed.</summary>
    private static async Task<(HttpClient client, string landed)> PortalSignIn(string email, string password, string code)
    {
        var c = NewBrowser();
        var form = await c.GetStringAsync("/login");
        var step = await (await c.PostAsync("/login", Form(("Email", email), ("Password", password), ("__RequestVerificationToken", Token(form))))).Content.ReadAsStringAsync();
        var mode = Regex.Match(step, "name=\"Mode\" value=\"([^\"]*)\"").Groups[1].Value;
        var pending = Regex.Match(step, "name=\"Pending\" value=\"([^\"]*)\"").Groups[1].Value;
        var r = await c.PostAsync("/login", Form(("Mode", mode), ("Pending", pending), ("Code", code), ("__RequestVerificationToken", Token(step))));
        return (c, r.RequestMessage?.RequestUri?.AbsolutePath ?? "?");
    }

    /// <summary>The sign-in form's first step only, on a browser that may already hold a trust cookie.</summary>
    private static async Task<(string landed, string html)> PortalPasswordOnly(HttpClient c, string email, string password)
    {
        var form = await c.GetStringAsync("/login");
        var r = await c.PostAsync("/login", Form(("Email", email), ("Password", password), ("__RequestVerificationToken", Token(form))));
        return (r.RequestMessage?.RequestUri?.AbsolutePath ?? "?", await r.Content.ReadAsStringAsync());
    }

    private static async Task PortalSignOut(HttpClient c)
    {
        var page = await c.GetStringAsync("/portal/tickets");
        await c.PostAsync("/logout", Form(("__RequestVerificationToken", Token(page))));
    }

    private static readonly Dictionary<HttpClient, CookieContainer> _jars = new();
    private static bool HasCookie(HttpClient c, string name) =>
        _jars.TryGetValue(c, out var jar) && jar.GetCookies(new Uri(_base)).Any(k => k.Name == name && k.Value.Length > 0);

    private static async Task<string> PortalPost(HttpClient c, string page, string handler, params (string k, string v)[] fields)
    {
        var html = await c.GetStringAsync(page);
        var all = fields.Append(("__RequestVerificationToken", Token(html))).ToArray();
        var r = await c.PostAsync($"{page}?handler={handler}", Form(all));
        return await r.Content.ReadAsStringAsync();
    }

    private static string H(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : r.Content.Headers.TryGetValues(name, out var cv) ? string.Join(",", cv) : "";

    private static FormUrlEncodedContent Form(params (string k, string v)[] f) => new(f.Select(x => new KeyValuePair<string, string>(x.k, x.v)));

    private static string Token(string page) =>
        Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

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

    // Debug, as the other harnesses: the Release output is usually locked by the server Clint has running.
    // Two-factor is NOT turned off here — that is the point of this harness.
    private static ProcessStartInfo Psi()
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_proj);
        psi.Environment["RSP_Platform__DataDirectory"] = _dataDir;
        HarnessDb.Use(psi.Environment, _dataDir);
        psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
        psi.Environment["RSP_Security__TwoFactorTrustDays"] = "0.0005";   // 43 s, so running out can be watched (11b)
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
