using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// reviewcheck — companies' reviews on the website (2026-10-14, HANDOVER §139). Clint: asked a week in, "later" puts it off a
// month, then once more with an opt-out; stars and words, their name, their COMPANY NAME, a link; published only after approval.
//
//   ASKING      the owner of a full-version company only (not a technician, not the free version); Later → gone, then back
//               with "Don't ask again"; that → gone for good. (Here a "week" is 0 days and a "month" ~4 seconds.)
//   WRITING     stars, words, the name, the company name, an https website or none, and the publishing tick — each refused
//               without; only the owner; the platform's admins are emailed.
//   APPROVAL    nothing on the website until published on /owner/reviews; published → on the home page with its stars, words
//               ENCODED (no script runs), and the link rel="ugc nofollow"; rewritten → off until checked again; declined → off.
//
//   dotnet run -c Release --project tools/reviewcheck/reviewcheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class ReviewCheck
{
    private static int _pass, _fail;
    private static string _proj = "", _dataDir = "", _base = "";
    private static readonly string AdminKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private const string Pw = "Harness-Pass!2026x";
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
        _proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        Mail.Start();
        _dataDir = Path.Combine(Path.GetTempPath(), $"rs-reviewcheck-{DateTime.Now:HHmmss}");
        Directory.CreateDirectory(_dataDir);
        int port = FreePort();
        _base = $"http://127.0.0.1:{port}";
        Console.WriteLine($"reviewcheck — reviews ({HarnessDb.Name})");

        var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var buildOut = await build.StandardOutput.ReadToEndAsync();
        await build.WaitForExitAsync();
        if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }

        await Cli("platform-mail", "set", "--host", "127.0.0.1", "--port", Mail.Port.ToString(), "--nossl", "--from", "noreply@rev.test", "--from-name", "The Remote Support Program");
        await Cli("add-admin", "ada@admins.test", "Ada Admin", "full");
        foreach (var (slug, name, owner) in new[] { ("rev-co", "Rev Co", "rita@rev.test"), ("rev-two", "Rev Two", "tess@two.test") })
        {
            await Cli("create-tenant", name, slug, "5");
            await Cli("set-brand", slug, "status", "Active");
            await Cli("add-tech", slug, owner, Pw, name.Split(' ')[0] + " Owner", "--owner", "--admin");
        }
        await Cli("add-tech", "rev-co", "tim@rev.test", Pw, "Tim Tech", "--technician");
        await Cli("create-tenant", "Free Rev", "free-rev", "5");
        await Cli("set-brand", "free-rev", "plan", "free");
        await Cli("set-brand", "free-rev", "status", "Active");
        await Cli("add-tech", "free-rev", "fred@free.test", Pw, "Fred Free", "--owner", "--technician");

        var platform = StartPlatform();
        try
        {
            if (!await WaitForPort(port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }

            // ---- asking -----------------------------------------------------------------------------------------------------
            Head("asking");
            var rita = await SignIn("rita@rev.test");
            var dash = await Html(rita, "/portal");
            Check("the owner is asked: Write a review, Later — and not yet Don't ask again",
                dash.Contains("id=\"reviewAsk\" data-ask=\"1\"") && dash.Contains("href=\"/portal/review\"") && !dash.Contains("ask again"));
            var tim = await SignIn("tim@rev.test");
            Check("a technician is not asked", !(await Html(tim, "/portal")).Contains("id=\"reviewAsk\""));
            var fred = await SignIn("fred@free.test");
            Check("the free version is not asked", !(await Html(fred, "/portal/machines")).Contains("id=\"reviewAsk\""));
            await BannerPost(rita, "Later");
            Check("Later: not asked now", !(await Html(rita, "/portal")).Contains("id=\"reviewAsk\""));
            await Task.Delay(5000);
            dash = await Html(rita, "/portal");
            Check("a \"month\" later: asked again, now with Don't ask again", dash.Contains("data-ask=\"2\"") && WebUtility.HtmlDecode(dash).Contains("Don't ask again"));
            var tess = await SignIn("tess@two.test");
            await BannerPost(tess, "Never");
            Check("Don't ask again before the second ask does nothing", (await Html(tess, "/portal")).Contains("data-ask=\"1\""));
            await BannerPost(tess, "Later"); await Task.Delay(5000);
            await BannerPost(tess, "Never");
            await Task.Delay(5000);
            Check("Don't ask again: never asked again", !(await Html(tess, "/portal")).Contains("id=\"reviewAsk\""));

            // ---- writing ----------------------------------------------------------------------------------------------------
            Head("writing");
            var form = await Html(rita, "/portal/review");
            Check("the form starts with the owner's name and the company's name", Field(form, "ShownName") == "Rev Owner" && Field(form, "BusinessName") == "Rev Co");
            const string words = "<script>alert('x')</script> Since we moved over, our workshop and call-outs are on one board & invoicing takes minutes.";
            async Task<string> Write(int stars, string w, string name, string business, string url, bool consent)
            {
                var f = new List<(string, string)> { ("Words", w), ("ShownName", name), ("BusinessName", business), ("BusinessUrl", url) };
                if (stars > 0) f.Add(("Stars", stars.ToString()));
                if (consent) f.Add(("Consent", "true"));
                return WebUtility.HtmlDecode(await Post(rita, "/portal/review", await Html(rita, "/portal/review"), f.ToArray()));
            }
            Check("no stars: refused", (await Write(0, words, "Rita", "Rev Co", "", true)).Contains("one to five stars"));
            Check("too few words: refused", (await Write(4, "Good.", "Rita", "Rev Co", "", true)).Contains("at least 20"));
            Check("no company name: refused (Clint: they must put their company name)", (await Write(4, words, "Rita", "", "", true)).Contains("your company's name"));
            Check("a plain-http website: refused", (await Write(4, words, "Rita", "Rev Co", "http://rev.example", true)).Contains("https://"));
            Check("no publishing tick: refused", (await Write(4, words, "Rita", "Rev Co", "", false)).Contains("Please tick the box"));
            var timTry = WebUtility.HtmlDecode(await Post(tim, "/portal/review", await Html(tim, "/portal/review"),
                ("Stars", "5"), ("Words", words), ("ShownName", "Tim"), ("BusinessName", "Rev Co"), ("Consent", "true")));
            Check("a technician cannot write it", timTry.Contains("Only the owner"));
            var before = Mail.All.LastOrDefault();
            var ok = await Write(4, words, "Rita Rowe", "Rev Computers", "rev-computers.co.za", true);
            Check("all of it: in, waiting for our check", ok.Contains("Your review is in") && ok.Contains("We check every review"), Snip(ok, "alert"));
            var told = await Mail.WaitFor("ada@admins.test", "A review from Rev Co is waiting", before);
            Check("the platform's Full admins are emailed", told != null && told.Body.Contains("4 of 5 stars"), told?.Subject ?? "nothing");
            Check("and the owner is not asked any more", !(await Html(rita, "/portal")).Contains("id=\"reviewAsk\""));
            using var pub = new HttpClient { BaseAddress = new Uri(_base) };
            Check("NOT on the website before approval", !(await pub.GetStringAsync("/")).Contains("id=\"reviews\""));

            // ---- approval ---------------------------------------------------------------------------------------------------
            Head("approval");
            var admin = NewBrowser();
            var step = await Post(admin, "/owner/login", await Html(admin, "/owner/login"), ("Key", AdminKey));
            var secret = Regex.Match(step, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
            await PostFollow(admin, "/owner/login", step, ("UseKey", "true"), ("Pending", Field(step, "Pending")), ("Code", Totp.Now(secret)));
            var list = await Html(admin, "/owner/reviews");
            var id = Regex.Match(list, "data-review=\"(\\d+)\" data-status=\"Waiting\"").Groups[1].Value;
            Check("/owner/reviews lists it waiting, with who wrote it", id.Length > 0 && list.Contains("rita@rev.test"));
            Check("its words are shown as words — the script is not run", list.Contains("&lt;script&gt;") && !list.Contains("<script>alert"));
            var published = await Post(admin, "/owner/reviews", list, ("id", id), ("act", "publish"));
            Check("Publish", WebUtility.HtmlDecode(published).Contains("Published — it is on the website now"));
            var home = await pub.GetStringAsync("/");
            var card = Section(home, "id=\"reviews\"");
            Check("on the home page: four stars of five, the words, the name, the company",
                WebUtility.HtmlDecode(card).Contains("★★★★☆") && card.Contains("our workshop and call-outs are on one board") && card.Contains("Rita Rowe") && card.Contains("Rev Computers"));
            Check("the words ENCODED on the public page — no script", card.Contains("&lt;script&gt;") && !home.Contains("<script>alert"));
            Check("the website linked https, rel=\"ugc nofollow\"", card.Contains("href=\"https://rev-computers.co.za\" rel=\"ugc nofollow noopener\""));
            var errors = new List<string>();
            var parser = new HtmlParser();
            parser.Error += (_, e) => { if (e is AngleSharp.Html.Dom.Events.HtmlErrorEvent he) errors.Add($"line {he.Position.Line}: {he.Message}"); };
            parser.ParseDocument(home);
            Check("the home page with a review is still valid HTML", errors.Count == 0, string.Join("; ", errors.Take(2)));
            Check("the log: written, published", WebUtility.HtmlDecode(await Html(admin, "/owner/log")).Contains("Published the company's review on the website"));

            Head("changed or declined");
            await Write(5, words + " Five stars now.", "Rita Rowe", "Rev Computers", "", true);
            Check("rewritten: off the website until checked again", !(await pub.GetStringAsync("/")).Contains("id=\"reviews\""));
            list = await Html(admin, "/owner/reviews");
            await Post(admin, "/owner/reviews", list, ("id", id), ("act", "decline"));
            Check("declined: not on the website, and the owner is told", !(await pub.GetStringAsync("/")).Contains("id=\"reviews\"") &&
                WebUtility.HtmlDecode(await Html(rita, "/portal/review")).Contains("We did not publish your review"));
        }
        finally { try { platform.Kill(true); } catch { } }
        Mail.Dispose();
        Console.WriteLine();
        Console.WriteLine($"reviewcheck: {_pass} of {_pass + _fail} passed ({HarnessDb.Name})");
        return _fail == 0 ? 0 : 1;
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private static async Task BannerPost(HttpClient c, string handler)
    {
        var page = await Html(c, "/portal");
        var at = page.IndexOf($"handler={handler}\"", StringComparison.Ordinal);
        var token = at < 0 ? Token(page) : Token(page[at..page.IndexOf("</form>", at, StringComparison.Ordinal)]);
        await c.PostAsync($"/portal/review?handler={handler}", Form(("__RequestVerificationToken", token)));
    }

    private static async Task<HttpClient> SignIn(string email)
    {
        var c = NewBrowser();
        var form = await Html(c, "/login");
        await c.PostAsync("/login", Form(("Email", email), ("Password", Pw), ("__RequestVerificationToken", Token(form))));
        return c;
    }

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
        psi.Environment["RSP_Platform__Region"] = "ZA";
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a harness platform only (§72)
        // A "week" of 0 days and a "month" of about four seconds, so the asking can be watched.
        psi.Environment["RSP_Platform__ReviewAskDays"] = "0";
        psi.Environment["RSP_Platform__ReviewLaterDays"] = "0.00005";
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
        return psi;
    }

    private static HttpClient NewBrowser() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(60) };
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
    private static string Token(string page) => Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
    private static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{name}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);
    private static string Section(string html, string near) { var i = html.IndexOf(near, StringComparison.Ordinal); return i < 0 ? "" : html[i..Math.Min(html.Length, i + 4000)]; }
    private static string Snip(string html, string near) { var i = html.IndexOf(near, StringComparison.OrdinalIgnoreCase); return i < 0 ? "" : Regex.Replace(html.Substring(Math.Max(0, i - 40), Math.Min(220, html.Length - Math.Max(0, i - 40))), "\\s+", " "); }
    private static FormUrlEncodedContent Form(params (string k, string v)[] f) => new(f.Select(x => new KeyValuePair<string, string>(x.k, x.v)));

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

    // ---- a mail server (controlcheck's) ----
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

        public async Task<Message?> WaitFor(string to, string subjectPart, Message? after = null, string? fromBy = null, int seconds = 10)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
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
}
