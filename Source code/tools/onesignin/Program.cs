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
// onesignin — one console and one browser at a time (2026-10-05, HANDOVER §82).
//
// Clint: "The system must also not allow more than one simultaneous sign in. If they log into a
// second machine it must automatically log them out of the first machine and request a code again
// incase it someone trying to hack the system." His answer to "does a console plus a portal count
// as two?": one console and one browser per person; the console's Tickets hand-off is not a new
// sign-in.
//
//   CONSOLES   the same PC signing in again closes the old console (and says "this computer");
//              another PC is asked the code even with a trusted token, and the old console is told
//              "another computer" and closed; that voids every trust, so the first PC is asked too;
//              a console that EXITS (/api/logout) pushes nobody off next time.
//   BROWSERS   the same browser again is fine; another browser is asked the code with the reason;
//              the old browser lands on the sign-in page saying why, once; consoles are untouched.
//   HAND-OFF   the Tickets button's browser does not push the real browser off, and dies with the
//              console that made it.
//
// A platform of its own with two-factor ON, an empty database in the temp folder. Device ids are
// sent as a console sends them; browsers carry their own rsp_device cookie.
//
//   dotnet run -c Release --project tools/onesignin/onesignin.csproj
// ---------------------------------------------------------------------------------------------

internal static class OneSignIn
{
    private static int _pass, _fail;
    private const string Pw = "OneAtATime!2026", Tech = "one@once.test";
    private const string PcA = "harnesspca0000000001", PcB = "harnesspcb0000000002", PcC = "harnesspcc0000000003", PcD = "harnesspcd0000000004";
    private static string _slug = "once-test-co", _base = "", _proj = "", _dataDir = "";
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
        Console.WriteLine("onesignin — one console and one browser at a time");

        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        _dataDir = Path.Combine(Path.GetTempPath(), "rs-onesignin-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_dataDir);
        _port = FreePort();
        _proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        _base = $"http://127.0.0.1:{_port}";
        Console.WriteLine($"A platform of its own on port {_port}, database in {_dataDir}, two-factor REQUIRED");

        try
        {
            var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{_proj}\" -c Debug -v q -nologo") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var buildOut = await build.StandardOutput.ReadToEndAsync();
            await build.WaitForExitAsync();
            if (build.ExitCode != 0) { Console.WriteLine("The platform did not build:\n" + buildOut); return 1; }
            var created = await Cli("create-tenant", "Once Test Co", _slug, "10");
            if (!created.Contains("Tenant key:")) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            var added = await Cli("add-tech", _slug, Tech, Pw, "Una Once", "--technician");
            if (!added.Contains("Added")) { Console.WriteLine("Could not add a person:\n" + added); return 1; }
        }
        catch (Exception ex) { Console.WriteLine("Could not run the platform's command line: " + ex.Message); return 1; }

        using var server = StartPlatform();
        try
        {
            if (!await WaitForPort(_port, 90000)) { Console.WriteLine("The platform never started listening."); return 1; }
            using var http = new HttpClient { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };

            // ---- set up, on PC A ------------------------------------------------------------
            Head("setting up on PC A");
            var a0 = await Api(http, "/api/login", new { email = Tech, password = Pw, device = PcA });
            var secret = a0.Str("secret");
            var a1 = await Api(http, "/api/login/enrol", new { pending = a0.Str("pending"), code = Totp.Now(secret) });
            var tokenA1 = a1.Str("token"); var trustA = a1.Str("trust"); var rc = a1.Arr("recoveryCodes");
            Check("set up and signed in on PC A, with a trust token and recovery codes", tokenA1.Length > 0 && trustA.Length > 0 && rc.Count == 10,
                  $"HTTP {(int)a1.status}");
            var consoleA1 = await OpenConsole(tokenA1);
            Check("PC A's console is connected", consoleA1.state.Task.Wait(TimeSpan.FromSeconds(10)), consoleA1.status);

            // ---- the same PC again ----------------------------------------------------------
            Head("consoles: the same PC signing in again");
            var a2 = await Api(http, "/api/login", new { username = Tech, password = Pw, trust = trustA, device = PcA });
            var tokenA2 = a2.Str("token");
            Check("PC A again, trusted: the password alone — the same PC is not somewhere else", tokenA2.Length > 0 && a2.Str("step") == "",
                  $"HTTP {(int)a2.status}, step '{a2.Str("step")}'");
            bool a1Told = consoleA1.signedOut.Task.Wait(TimeSpan.FromSeconds(5));
            Check("the first console is still closed — one console at a time", a1Told, a1Told ? consoleA1.signedOut.Task.Result : "nothing within 5 s");
            Check("...and told it was THIS computer, not another", a1Told && consoleA1.signedOut.Task.Result.Contains("on this computer"));
            Check("its token opens nothing now", (await OpenConsole(tokenA1)).status == "401");
            var consoleA2 = await OpenConsole(tokenA2);
            Check("the new console on PC A is connected", consoleA2.state.Task.Wait(TimeSpan.FromSeconds(10)), consoleA2.status);

            // ---- another PC -----------------------------------------------------------------
            Head("consoles: another PC");
            var b0 = await Api(http, "/api/login", new { username = Tech, password = Pw, trust = trustA, device = PcB });
            Check("PC B, even holding a good trust token, is asked for the code", b0.Str("step") == "code" && b0.Str("token") == "",
                  $"step '{b0.Str("step")}'");
            Check("...and told why, as a notice and not as a wrong code", b0.Str("notice").Contains("another computer") && b0.Str("wrong") == "",
                  b0.Str("notice"));
            Check("asking the code signs nobody out: PC A is still connected", !consoleA2.signedOut.Task.Wait(TimeSpan.FromSeconds(1)));
            var sw = Stopwatch.StartNew();
            var b1 = await Api(http, "/api/login/code", new { pending = b0.Str("pending"), code = rc[0] });
            var tokenB1 = b1.Str("token"); var trustB = b1.Str("trust");
            Check("the code signs PC B in", tokenB1.Length > 0, $"HTTP {(int)b1.status}");
            bool a2Told = consoleA2.signedOut.Task.Wait(TimeSpan.FromSeconds(5));
            Check("PC A's console is told and closed", a2Told, a2Told ? $"{sw.ElapsedMilliseconds} ms: \"{consoleA2.signedOut.Task.Result}\"" : "nothing within 5 s");
            Check("...that it was another computer, and what to do if it was not them",
                  a2Told && consoleA2.signedOut.Task.Result.Contains("another computer") && consoleA2.signedOut.Task.Result.Contains("change your password"));
            Check("PC A's token opens nothing now", (await OpenConsole(tokenA2)).status == "401");

            // ---- exiting, and the trust voided ----------------------------------------------
            Head("consoles: Exit gives the sign-in up; the first PC's trust is gone");
            var lo = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/logout") { Headers = { { "Authorization", "Bearer " + tokenB1 } } });
            Check("PC B's console exits (/api/logout)", lo.StatusCode == HttpStatusCode.OK, $"HTTP {(int)lo.StatusCode}");
            Check("PC B's token opens nothing now", (await OpenConsole(tokenB1)).status == "401");
            var a3 = await Api(http, "/api/login", new { username = Tech, password = Pw, trust = trustA, device = PcA });
            Check("PC A with its old trust token is asked the code — being pushed off voided it", a3.Str("step") == "code" && a3.Str("token") == "",
                  $"step '{a3.Str("step")}'");
            Check("...with no notice, because nobody is signed in to push off", a3.Str("notice") == "", a3.Str("notice"));
            var c1 = await Api(http, "/api/login", new { username = Tech, password = Pw, trust = trustB, device = PcC });
            var tokenC1 = c1.Str("token");
            Check("PC C with PC B's NEW trust token: the password alone — B exited, so nobody is pushed off", tokenC1.Length > 0 && c1.Str("step") == "",
                  $"step '{c1.Str("step")}'");
            var consoleC1 = await OpenConsole(tokenC1);
            Check("PC C's console is connected", consoleC1.state.Task.Wait(TimeSpan.FromSeconds(10)), consoleC1.status);
            var junk = await http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/logout") { Headers = { { "Authorization", "Bearer not-a-token" } } });
            Check("a logout with a token nobody issued is answered the same and does nothing",
                  junk.StatusCode == HttpStatusCode.OK && !consoleC1.signedOut.Task.Wait(TimeSpan.FromMilliseconds(500)));

            // ---- browsers ---------------------------------------------------------------------
            Head("browsers: one at a time, and the console left alone");
            var (browser1, landed1) = await PortalSignIn(NewBrowser(), Tech, Pw, rc[1]);
            Check("browser 1 signs in with the code", landed1 == "/portal/tickets", $"landed on {landed1}");
            Check("PC C's console is untouched by a BROWSER sign-in", !consoleC1.signedOut.Task.Wait(TimeSpan.FromSeconds(1)));
            await PortalSignOut(browser1);
            var again1 = await PortalPasswordOnly(browser1, Tech, Pw);
            Check("browser 1 signs out and back in: the password alone — the same browser", again1.landed == "/portal/tickets", $"landed on {again1.landed}");

            var browser2 = NewBrowser();
            var step2 = await PortalPasswordOnly(browser2, Tech, Pw);
            Check("browser 2 is asked the code", step2.landed == "/login" && step2.html.Contains("name=\"Mode\" value=\"code\""), $"landed on {step2.landed}");
            Check("...and told it will sign the other browser out", step2.html.Contains("signed in on another browser"));
            Check("browser 1 is still signed in while browser 2 is only being asked", (await Land(browser1, "/portal/tickets")).path == "/portal/tickets");
            var done2 = await PortalCode(browser2, step2.html, rc[2]);
            Check("browser 2 signs in with the code", done2 == "/portal/tickets", $"landed on {done2}");

            var out1 = await Land(browser1, "/portal/tickets");
            Check("browser 1's next page is the sign-in page", out1.path == "/login", $"landed on {out1.path}");
            Check("...saying it was signed in on another computer", out1.html.Contains("signed in on another computer"));
            var twice = await Land(browser1, "/login");
            Check("...once: the next visit to the sign-in page does not say it again", !twice.html.Contains("signed in on another computer"));
            var back1 = await PortalPasswordOnly(browser1, Tech, Pw);
            Check("browser 1, though trusted a minute ago, is asked the code", back1.landed == "/login" && back1.html.Contains("name=\"Mode\" value=\"code\""),
                  $"landed on {back1.landed}");
            Check("PC C's console is still untouched — one console AND one browser", !consoleC1.signedOut.Task.Wait(TimeSpan.FromSeconds(1)));
            var c2 = await Api(http, "/api/login", new { username = Tech, password = Pw, trust = trustB, device = PcC });
            Check("but the CONSOLE's trust went too: PC C signing in again is asked the code", c2.Str("step") == "code", $"step '{c2.Str("step")}'");

            // ---- the console's Tickets hand-off -------------------------------------------------
            Head("the Tickets button's browser");
            consoleC1.conn!.Send(new PortalLinkRequestMsg { Path = "/portal/tickets" });
            var link = consoleC1.portalLink.Task.Wait(TimeSpan.FromSeconds(5)) ? consoleC1.portalLink.Task.Result : "";
            Check("PC C's console is handed a portal link", link.StartsWith("/portal/enter?t="), link);
            var browser3 = NewBrowser();
            var entered = await Land(browser3, link);
            Check("the link opens the portal signed in", entered.path == "/portal/tickets", $"landed on {entered.path}");
            Check("...and is NOT a new browser sign-in: browser 2 stays in", (await Land(browser2, "/portal/tickets")).path == "/portal/tickets");
            Check("the hand-off browser keeps working", (await Land(browser3, "/portal/tickets")).path == "/portal/tickets");

            var d0 = await Api(http, "/api/login", new { username = Tech, password = Pw, device = PcD });
            var d1 = await Api(http, "/api/login/code", new { pending = d0.Str("pending"), code = rc[3] });
            Check("PC D signs in to the console", d1.Str("token").Length > 0, $"HTTP {(int)d1.status}");
            Check("PC C's console is told and closed", consoleC1.signedOut.Task.Wait(TimeSpan.FromSeconds(5)));
            var after3 = await Land(browser3, "/portal/tickets");
            Check("the hand-off browser dies with the console that made it", after3.path == "/login", $"landed on {after3.path}");
            Check("browser 2 — the real browser sign-in — is still in", (await Land(browser2, "/portal/tickets")).path == "/portal/tickets");

            Console.WriteLine();
            Console.WriteLine($"{_pass} passed, {_fail} failed");
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

    private static async Task<Answer> Api(HttpClient http, string path, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        var r = await http.SendAsync(req);
        var text = await r.Content.ReadAsStringAsync();
        JsonElement doc;
        try { doc = JsonDocument.Parse(text).RootElement; } catch { doc = default; }
        return new Answer(r.StatusCode, doc);
    }

    private sealed class ConsoleSocket
    {
        public string status = "";
        public WsConnection? conn;
        public readonly TaskCompletionSource<bool> state = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> signedOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> portalLink = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            s.conn = conn;
            conn.OnMessage += m =>
            {
                if (m is StateMsg) s.state.TrySetResult(true);
                if (m is SignedOutMsg so) s.signedOut.TrySetResult(so.Reason);
                if (m is PortalLinkMsg pl) s.portalLink.TrySetResult(pl.Url.Length > 0 ? pl.Url : "error: " + pl.Error);
            };
            _ = conn.RunAsync(CancellationToken.None);
        }
        catch { s.status = ((int)cws.HttpStatusCode).ToString(); }
        return s;
    }

    private static HttpClient NewBrowser() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<(string path, string html)> Land(HttpClient c, string path)
    {
        var r = await c.GetAsync(path);
        return (r.RequestMessage?.RequestUri?.AbsolutePath ?? "?", await r.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpClient client, string landed)> PortalSignIn(HttpClient c, string email, string password, string code)
    {
        var step = await PortalPasswordOnly(c, email, password);
        return (c, await PortalCode(c, step.html, code));
    }

    private static async Task<string> PortalCode(HttpClient c, string step, string code)
    {
        var mode = Regex.Match(step, "name=\"Mode\" value=\"([^\"]*)\"").Groups[1].Value;
        var pending = Regex.Match(step, "name=\"Pending\" value=\"([^\"]*)\"").Groups[1].Value;
        var r = await c.PostAsync("/login", Form(("Mode", mode), ("Pending", pending), ("Code", code), ("__RequestVerificationToken", Token(step))));
        return r.RequestMessage?.RequestUri?.AbsolutePath ?? "?";
    }

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
    private static ProcessStartInfo Psi()
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_proj);
        psi.Environment["RSP_Platform__DataDirectory"] = _dataDir;
        HarnessDb.Use(psi.Environment, _dataDir);
        psi.Environment["RSP_Platform__AdminKey"] = "onesignin-admin-key-0123456789abcdef0123456789abcdef";
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
