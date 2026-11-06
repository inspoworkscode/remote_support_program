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
// invitecheck — people choose their own passwords (2026-10-01, HANDOVER §73).
//
// Clint: "when setting up users, will it send each one a link for their download and request them
// to change their password?" It did not. This proves that it does, end to end, against a REAL
// platform with two-factor ON — and with a REAL mail server: a tiny SMTP server inside this harness
// that the platform is pointed at, so every invitation and reset is caught as the bytes that would
// have left, and read the way a person reads it (From, Reply-To, Subject, the link).
//
//   INVITING     Add on the People page mails a link — nobody types a password; the mail is in the
//                company's name, replies go to its support address, the owner gets no copy, and a
//                seat brings the console link. Invited people cannot sign in until they finish.
//   THE LINK     opening it twice spends nothing (mail filters open links); a bad password is
//                refused without spending it; the right one goes straight to the QR code, then the
//                recovery codes, then the console download; used once, never again.
//   RESEND       a new invitation and the old link stops working.
//   FORGOT       the same answer for any address; a real one gets a link; the new password still
//                needs the code; the reset signs a live console out; three an hour per address.
//   SEND RESET   from the People page, naming who asked.
//   MY ACCOUNT   current password AND a code; this browser stays in, the console is signed out.
//   NO MAILBOX   the supervisor is shown the link to pass on, once.
//   COMMAND LINE `invite` mails a link at the public address.
//
// A platform of its own on its own port, with an empty database in the temp folder.
//
//   dotnet run -c Release --project tools/invitecheck/invitecheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class InviteCheck
{
    private static int _pass, _fail;
    private const string Slug = "invite-test-co", Company = "Invite Test Co";
    private const string Boss = "boss@inv.test", Tess = "tess@inv.test", Olive = "olive@inv.test", Fran = "fran@inv.test", CliPerson = "cli@inv.test";
    private const string BossPw = "Bootstrap!2026", TessPw = "TessChose!2026", TessPw2 = "TessReset!2026", TessPw3 = "TessChanged!2026";
    private const string Support = "support@inv.test", CopyTo = "copy@inv.test";
    private static string _base = "", _proj = "", _dataDir = "";
    private static int _port;
    private static readonly FakeSmtp Mail = new();

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
        _dataDir = Path.Combine(Path.GetTempPath(), "rs-invitecheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_dataDir);
        _port = FreePort();
        _proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        _base = $"http://127.0.0.1:{_port}";
        Mail.Start();
        Console.WriteLine("invitecheck — people choose their own passwords");
        Console.WriteLine($"A platform of its own on port {_port}, two-factor REQUIRED; a mail server of its own on port {Mail.Port}");

        try
        {
            var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var buildOut = await build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();
            if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }
            if (!(await Cli("create-tenant", Company, Slug, "10")).Contains("Tenant key:")) { Console.WriteLine("Could not create a tenant."); return 1; }
            await Cli("set-brand", Slug, "email", Support);
            // The company's first owner is the one person still made with a password, on the command line.
            if (!(await Cli("add-tech", Slug, Boss, BossPw, "Bea Boss", "--owner", "--admin")).Contains("Added")) { Console.WriteLine("Could not add the owner."); return 1; }
            var mailSet = await Cli("platform-mail", "set", "--host", "127.0.0.1", "--port", Mail.Port.ToString(), "--nossl",
                "--from", "noreply@inv.test", "--from-name", "The Remote Support Program", "--copy-to", CopyTo);
            if (!mailSet.Contains("Saved")) { Console.WriteLine("Could not set the platform mailbox:\n" + mailSet); return 1; }
        }
        catch (Exception ex) { Console.WriteLine("Could not run the platform's command line: " + ex.Message); return 1; }

        using var server = StartPlatform();
        try
        {
            if (!await WaitForPort(_port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
            using var api = new HttpClient { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };

            var bossCodes = await EnrolByApi(api, Boss, BossPw);
            var boss = await PortalSignIn(Boss, BossPw, bossCodes[0]);
            // In the portal: the dashboard, or — the first time for an owner — Job types (HANDOVER §167).
            if (boss.landed is not ("/portal" or "/portal/jobtypes")) { Console.WriteLine($"The owner could not sign in to the portal (landed on {boss.landed})."); return 1; }

            // ---- inviting ------------------------------------------------------------------
            Head("inviting: Add on the People page");
            var people = await Html(boss.client, "/portal/technicians");
            Check("the Add form asks for no password", !Regex.IsMatch(Section(people, "Add a person"), "name=\"password\""), "");
            var added = await PortalPost(boss.client, "/portal/technicians", "Add", ("name", "Tess Tech"), ("email", Tess), ("role", "technician"));
            Check("Add says an invitation has gone", added.Contains("An invitation has gone to " + Tess), Snip(added, "invitation"));
            var invite = await Mail.WaitFor(Tess, "set up your account");
            Check("an invitation reaches the person", invite != null, invite?.Subject ?? "nothing arrived");
            if (invite == null) return Done();
            Check("in the company's name", invite.FromName == Company, $"From: {invite.From}");
            Check("replies go to the company's support address", invite.ReplyTo.Contains(Support), $"Reply-To: {invite.ReplyTo}");
            Check("and only to them — the owner's copy address is NOT on a password link", invite.To.Count == 1 && !invite.To.Contains(CopyTo), string.Join(", ", invite.To));
            var link = invite.SetupLink;
            Check("it carries a one-use setup link", link.Length > 0, link.Length > 0 ? link[..Math.Min(70, link.Length)] + "…" : "no link");
            Check("and, with a seat, the console download link", invite.Body.Contains($"{_base}/tech/{Slug}"));
            people = await Html(boss.client, "/portal/technicians");
            Check("the People page shows them as Invited — not set up", people.Contains("Invited — not set up"));
            var noPw = await Api(api, "/api/login", new { email = Tess, password = "anything-at-all" });
            Check("they cannot sign in before setting up", noPw.Str("token") == "" && noPw.Str("error") == "Invalid email or password.", noPw.Str("error"));

            // ---- the link -----------------------------------------------------------------
            Head("the link");
            var tess = NewBrowser();
            var open1 = await Html(tess, Rel(link));
            var open2 = await Html(tess, Rel(link));
            Check("opening it shows who it is for", open1.Contains("Set up your account") && open1.Contains("Tess Tech") && open1.Contains(Company));
            Check("opening it twice spends nothing (mail filters open links first)", open2.Contains("Set up your account") && open2.Contains("name=\"Password\""));
            var mismatch = await Post(tess, Rel(link), open2, ("T", Token(link)), ("Password", TessPw), ("Again", TessPw + "x"));
            Check("two different passwords are refused", mismatch.Contains("not the same"));
            var shortPw = await Post(tess, Rel(link), mismatch, ("T", Token(link)), ("Password", "short"), ("Again", "short"));
            Check("a short one is refused", shortPw.Contains("at least 8 characters"));
            Check("and neither spent the link", (await Html(tess, Rel(link))).Contains("name=\"Password\""));
            var chosen = await Post(tess, Rel(link), shortPw, ("T", Token(link)), ("Password", TessPw), ("Again", TessPw));
            var secret = Regex.Match(chosen, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
            Check("the right one goes straight on to the authenticator QR code", chosen.Contains("data:image/png;base64,") && secret.Length == 32, $"key {secret.Length} chars");
            var saved = await Post(tess, Rel(link), chosen, ("Mode", Field(chosen, "Mode")), ("Pending", Field(chosen, "Pending")), ("Code", Totp.Now(secret)));
            var tessCodes = WebUtility.HtmlDecode(Regex.Match(saved, "<pre id=\"rcodes\"[^>]*>([^<]+)</pre>").Groups[1].Value).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Check("then the ten recovery codes", tessCodes.Count == 10, $"{tessCodes.Count}");
            Check("and on to the console download", saved.Contains($"href=\"/tech/{Slug}\""));
            var download = await Html(tess, $"/tech/{Slug}");
            // This platform has no console payload to hand out, so the page says so rather than showing
            // the button — what matters is that it knows her: signed in, not asked to sign in again.
            Check("signed in, the download page knows her — no second sign-in",
                !download.Contains("Sign in to download") && (download.Contains("Download the technician console") || download.Contains("isn't available to download yet")),
                download.Contains("isn't available") ? "signed in (this test platform carries no console to download)" : "");
            var again = await Html(NewBrowser(), Rel(link));
            Check("the link works once, never again", again.Contains("already been used"), Snip(again, "link"));
            var withPw = await Api(api, "/api/login", new { email = Tess, password = TessPw });
            Check("they sign in with the password THEY chose — and the code", withPw.Str("step") == "code", $"step '{withPw.Str("step")}'");
            people = await Html(boss.client, "/portal/technicians");
            Check("the People page now shows two-factor On", Row(people, Tess).Contains(">On<"));

            // ---- resend -----------------------------------------------------------------------
            Head("resending an invitation");
            await PortalPost(boss.client, "/portal/technicians", "Add", ("name", "Olive Office"), ("email", Olive), ("role", "office"));
            var oliveFirst = await Mail.WaitFor(Olive, "set up your account");
            Check("an office administrator's invitation has no console link", oliveFirst != null && !oliveFirst.Body.Contains("/tech/"), "");
            var resent = await PortalPost(boss.client, "/portal/technicians", "Invite", ("email", Olive));
            Check("Resend invitation says the earlier link no longer works", resent.Contains("earlier link no longer works"));
            var oliveSecond = await Mail.WaitFor(Olive, "set up your account", after: oliveFirst);
            Check("a second invitation arrives, with a different link", oliveSecond != null && oliveSecond.SetupLink != oliveFirst?.SetupLink);
            Check("the first link is dead", (await Html(NewBrowser(), Rel(oliveFirst!.SetupLink))).Contains("not valid"));
            Check("the second works", (await Html(NewBrowser(), Rel(oliveSecond!.SetupLink))).Contains("Set up your account"));

            // ---- forgot ---------------------------------------------------------------------------
            Head("Forgot password");
            var t1 = await Api(api, "/api/login", new { email = Tess, password = TessPw, code = tessCodes[0] });
            var console = await OpenConsole(t1.Str("token"));
            Check("(Tess has a console open)", console.state.Task.Wait(TimeSpan.FromSeconds(10)), console.status);
            var before = Mail.Count;
            var nobody = await Forgot("nobody@inv.test");
            var real = await Forgot(Tess);
            Check("the page says the same for an address with no account as for a real one",
                Answer(nobody) == Answer(real).Replace(Tess, "nobody@inv.test") && real.Contains("If"), Answer(real));
            var reset = await Mail.WaitFor(Tess, "reset your password");
            Check("no mail for the address with no account; one for the real one", Mail.Count == before + 1 && reset != null, $"{Mail.Count - before} mail(s)");
            var resetPage = await Html(NewBrowser(), Rel(reset!.SetupLink));
            Check("the reset link asks for a new password", resetPage.Contains("Choose a new password"));
            var rb = NewBrowser();
            var rp = await Html(rb, Rel(reset.SetupLink));
            var codeStep = await Post(rb, Rel(reset.SetupLink), rp, ("T", Token(reset.SetupLink)), ("Password", TessPw2), ("Again", TessPw2));
            Check("the new password still needs the code (not a QR code — she has one)", Field(codeStep, "Mode") == "code" && !codeStep.Contains("data:image/png"));
            bool told = console.signedOut.Task.Wait(TimeSpan.FromSeconds(5));
            Check("choosing it signs her open console out", told, told ? console.signedOut.Task.Result : "nothing within 5 s");
            var landed = await PostFollow(rb, Rel(reset.SetupLink), codeStep, ("Mode", "code"), ("Pending", Field(codeStep, "Pending")), ("Code", tessCodes[1]));
            Check("with the code she is in, on the console download", landed == $"/tech/{Slug}", $"landed on {landed}");
            var oldPw = await Api(api, "/api/login", new { email = Tess, password = TessPw });
            var newPw = await Api(api, "/api/login", new { email = Tess, password = TessPw2 });
            Check("the old password is refused and the new one works", oldPw.Str("step") == "" && newPw.Str("step") == "code");
            for (int i = 0; i < 4; i++) await Forgot(Tess);
            var forgotMails = Mail.All.Count(m => m.To.Contains(Tess) && m.Subject.Contains("reset your password") && m.Body.Contains("Somebody asked to reset the password for your Invite Test Co account.\n"));
            Check("Forgot password mails one address three times an hour, no more", forgotMails == 3, $"{forgotMails} after 5 asks");

            // ---- send reset link ------------------------------------------------------------------
            Head("Send reset link on the People page");
            var sentReset = await PortalPost(boss.client, "/portal/technicians", "SendReset", ("email", Tess));
            Check("the page says a link has gone", sentReset.Contains("A link to choose a new password has gone"));
            var bossReset = await Mail.WaitFor(Tess, "reset your password", fromBy: "Bea Boss");
            Check("the mail names who asked", bossReset != null, bossReset == null ? "no mail naming Bea Boss" : "");

            // ---- my account ---------------------------------------------------------------------
            Head("My account: changing your own password");
            var mine = await PortalSignIn(Tess, TessPw2, tessCodes[2]);
            Check("Tess signs in to the portal", mine.landed == "/portal/tickets", $"landed on {mine.landed}");
            var t2 = await Api(api, "/api/login", new { email = Tess, password = TessPw2, code = tessCodes[3] });
            var console2 = await OpenConsole(t2.Str("token"));
            Check("(and a console)", console2.state.Task.Wait(TimeSpan.FromSeconds(10)), console2.status);
            var acct = await Html(mine.client, "/portal/account");
            Check("My account is in the menu and has the form", acct.Contains("href=\"/portal/account\"") && acct.Contains("Change my password"));
            var wrongCurrent = await Post(mine.client, "/portal/account", acct, ("current", "not-it"), ("password", TessPw3), ("again", TessPw3), ("code", tessCodes[4]));
            Check("a wrong current password is refused", wrongCurrent.Contains("current password is not right"));
            var wrongCode = await Post(mine.client, "/portal/account", wrongCurrent, ("current", TessPw2), ("password", TessPw3), ("again", TessPw3), ("code", "000000"));
            Check("so is a wrong code", wrongCode.Contains("code is not right"));
            var changed = await Post(mine.client, "/portal/account", wrongCode, ("current", TessPw2), ("password", TessPw3), ("again", TessPw3), ("code", tessCodes[4]));
            Check("the right ones change it", changed.Contains("Password changed"));
            var stillIn = await mine.client.GetAsync("/portal/tickets");
            Check("this browser stays signed in", stillIn.RequestMessage?.RequestUri?.AbsolutePath == "/portal/tickets", stillIn.RequestMessage?.RequestUri?.AbsolutePath ?? "");
            bool told2 = console2.signedOut.Task.Wait(TimeSpan.FromSeconds(5));
            Check("her console is signed out", told2, told2 ? console2.signedOut.Task.Result : "nothing within 5 s");
            var p2 = await Api(api, "/api/login", new { email = Tess, password = TessPw2 });
            var p3 = await Api(api, "/api/login", new { email = Tess, password = TessPw3 });
            Check("the old password is refused, the new one works", p2.Str("step") == "" && p3.Str("step") == "code");

            // ---- no mailbox -----------------------------------------------------------------------
            Head("when the mail cannot go");
            await Cli("platform-mail", "set", "--port", FreePort().ToString());
            var fallback = await PortalPost(boss.client, "/portal/technicians", "Add", ("name", "Fran Fallback"), ("email", Fran), ("role", "technician"));
            var handOn = Regex.Match(fallback, "<code id=\"handon\"[^>]*>([^<]+)</code>").Groups[1].Value;
            Check("the supervisor is told it did not go, and shown the link to pass on", fallback.Contains("could not be sent") && handOn.Contains("/account/setup?t="), Snip(fallback, "could not"));
            Check("that link works like the mailed one", (await Html(NewBrowser(), Rel(WebUtility.HtmlDecode(handOn)))).Contains("Set up your account"));
            await Cli("platform-mail", "set", "--port", Mail.Port.ToString());

            // ---- command line -------------------------------------------------------------------
            Head("the command line");
            var cli = await Cli("invite", Slug, CliPerson, "Cli Person", "--technician");
            Check("`invite` adds and mails", cli.Contains("invitation is on its way"), cli.Trim());
            var cliMail = await Mail.WaitFor(CliPerson, "set up your account");
            Check("with a link at the public address", cliMail != null && cliMail.SetupLink.StartsWith(_base + "/account/setup?t="), cliMail?.SetupLink ?? "no mail");

            return Done();
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            Mail.Dispose();
            try { await Task.Delay(300); Directory.Delete(_dataDir, true); } catch { }
        }
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // ---- the mail server --------------------------------------------------------------------

    /// <summary>
    /// Just enough SMTP for the platform's SmtpClient to deliver to: no AUTH, no STARTTLS, every
    /// message kept as it arrived, with its envelope recipients (which is where a Bcc shows).
    /// </summary>
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

        public string SetupLink => Regex.Match(Body, @"http://127\.0\.0\.1:\d+/account/setup\?t=[0-9A-F]+").Value;

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

    private static string Rel(string link) => link.Length == 0 ? "/account/setup" : new Uri(link).PathAndQuery;
    private static string Token(string linkOrPage) =>
        linkOrPage.Contains("/account/setup?t=") && !linkOrPage.Contains("<")
            ? linkOrPage[(linkOrPage.IndexOf("?t=", StringComparison.Ordinal) + 3)..]
            : Regex.Match(linkOrPage, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    private static string Field(string html, string name) => Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value;
    private static string Section(string html, string heading) { var i = html.IndexOf(heading, StringComparison.Ordinal); return i < 0 ? "" : html[i..Math.Min(html.Length, i + 3000)]; }
    private static string Row(string html, string email) { var i = html.IndexOf("<td>" + email + "</td>", StringComparison.Ordinal); return i < 0 ? "" : html[i..Math.Min(html.Length, i + 4000)]; }
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
