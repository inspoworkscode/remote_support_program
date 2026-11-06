using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// THE FREE VERSION'S PORTAL (2026-10-06, docs/NEXT-SESSION-FREE.md Stage F), through real pages.
//
// Clint's answer: a small portal — My account, Downloads, its machines, Upgrade — and no tickets,
// clients, people or knowledge base. The rule is ONE middleware (FreePortal), so this asks it about
// the pages a free company must not have, and about the ones it must; and walks the one page that is
// the free version's own, Computers, including Remove through its real form.
//
// §34: a refused page arrives as a 200 somewhere else, so every check reads the PAGE's own heading,
// never a status code — except the API, which answers 403.
//
//   dotnet run -c Release --project tools/freeportal/freeportal.csproj
// ---------------------------------------------------------------------------------------------

internal static class FreePortalCheck
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private static async Task<int> Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-freeportal-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var baseUrl = $"http://127.0.0.1:{port}";
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");

        string freeKey, freeSlug, fullSlug;
        try
        {
            (freeKey, freeSlug) = await CreateTenant(proj, dataDir, port, "Branch Offices Free");
            (_, fullSlug) = await CreateTenant(proj, dataDir, port, "Full Company");
            await RunPlatform(proj, dataDir, port, "set-brand", freeSlug, "plan", "basic");
            await RunPlatform(proj, dataDir, port, "add-tech", freeSlug, "me@example.com", "FreeOwner!2026", "Fran Free", "--owner", "--technician");
            await RunPlatform(proj, dataDir, port, "add-tech", fullSlug, "boss@example.com", "FullOwner!2026", "Bo Boss", "--owner", "--technician");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var free = await SignIn(baseUrl, "me@example.com", "FreeOwner!2026");
            var full = await SignIn(baseUrl, "boss@example.com", "FullOwner!2026");
            if (free == null || full == null) { Console.WriteLine("Could not sign in to the portal."); return 1; }

            // ---- 1. where a free company lands, and what it is refused ------------------------
            Console.WriteLine();
            Console.WriteLine("== 1. the pages a free company has, and the ones it does not ==");
            var (landed, home) = await Get(free, "/portal");
            Check("the dashboard address lands on Your computers", landed.StartsWith("/portal/machines") && home.Contains("id=\"machinesHeading\""), landed);
            foreach (var page in new[] { "/portal/tickets", "/portal/clients", "/portal/technicians", "/portal/branding", "/portal/kb",
                                         "/portal/devices", "/portal/sessions", "/portal/items", "/portal/outsiders" })
            {
                var (at, html) = await Get(free, page);
                Check($"{page} is refused: Your computers, saying it is the full version's",
                    at.StartsWith("/portal/machines") && html.Contains("id=\"machinesError\"") && html.Contains("part of the full version"), at);
            }
            var api = await free.GetAsync("/api/portal/clients?q=a");
            Check("a portal API answers 403", api.StatusCode == HttpStatusCode.Forbidden, ((int)api.StatusCode).ToString());
            var (_, account) = await Get(free, "/portal/account");
            Check("My account is there", account.Contains("<h1>My account</h1>"));
            var (_, downloads) = await Get(free, "/portal/downloads");
            Check("Downloads: the console and the installer for its computers, nothing for customers or technicians",
                downloads.Contains("id=\"freeDownloads\"") && downloads.Contains($"/download/{freeSlug}") && !downloads.Contains("For your customers") && !downloads.Contains("For your technicians"));
            var nav = Regex.Match(home, "<nav id=\"topnav\"[^>]*>(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value;
            var links = Regex.Matches(nav, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            Check("the menu is Computers, Downloads, My account, Billing (it pays R50 since 2026-10-25), Upgrade and Sign out (missing until §163)",
                links.SequenceEqual(new[] { "/portal/machines", "/portal/downloads", "/portal/account", "/portal/billing", "/portal/upgrade", "#" })
                && nav.Contains("action=\"/logout\"") && nav.Contains(">Sign out</a>"), string.Join(" ", links));
            Check("and no office chat on the page — it is one person", !home.Contains("/chat.js"));

            // ---- 2. Computers, with Remove ---------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 2. Your computers, and Remove ==");
            var a = NewId(); var b = NewId();
            var agentA = await Device(port, a, freeKey, "BRANCH-A", "HW-A");
            var agentB = await Device(port, b, freeKey, "BRANCH-B", "HW-B");
            // Three since 2026-10-25 (the Basic version).
            var dd = NewId();
            var agentD = await Device(port, dd, freeKey, "BRANCH-D", "HW-D");
            await Task.Delay(1200);
            var (_, two) = await Get(free, "/portal/machines");
            Check("all three computers are listed, online, counted against the three", two.Contains("BRANCH-A") && two.Contains("BRANCH-B") && two.Contains("BRANCH-D") && two.Contains("3 of 3"),
                Regex.Match(two, "id=\"machineCount\">([^<]*)").Groups[1].Value);
            Check("with two removals left this month", Regex.Match(two, "id=\"removalsLeft\">([^<]*)").Groups[1].Value == "2");
            Check("and no way to add a fourth", two.Contains("which is what the Basic version allows") && !two.Contains("id=\"addLink\""));

            var removed = await Post(free, "/portal/machines?handler=Remove", two, ("deviceId", a.DeviceId));
            Check("Remove, through the page's own form: removed, and said", removed.Contains("id=\"machinesMessage\"") && !removed.Contains($"data-device=\"{a.DeviceId}\"") && removed.Contains("Removed BRANCH-A."),
                Regex.Match(removed, "id=\"machines(Message|Error)\">([^<]*)").Groups[2].Value);
            Check("one removal left, and room to add one", Regex.Match(removed, "id=\"removalsLeft\">([^<]*)").Groups[1].Value == "1" && removed.Contains("id=\"addLink\""));
            var told = await agentA.WaitFor<ErrorMsg>(e => e.NotAdmitted);
            Check("its agent is told it was removed", told != null, told?.Message ?? "");
            var second = await Post(free, "/portal/machines?handler=Remove", removed, ("deviceId", b.DeviceId));
            Check("the second removal: none left, and the page says when the next can be", second.Contains("id=\"noRemovals\"") && !second.Contains($"data-device=\"{b.DeviceId}\""));
            var c = NewId();
            await using var agentC = await Device(port, c, freeKey, "BRANCH-C", "HW-C");
            await Task.Delay(1000);
            var (_, withC) = await Get(free, "/portal/machines");
            var third = await Post(free, "/portal/machines?handler=Remove", withC, ("deviceId", c.DeviceId));
            Check("a third removal is refused, and the computer stays", third.Contains("id=\"machinesError\"") && third.Contains("this month's are used") && third.Contains("BRANCH-C"),
                Regex.Match(third, "id=\"machinesError\">([^<]*)").Groups[1].Value);
            Check("with no Remove button offered while none are left", !third.Contains(">Remove</button>"));
            // Post, then redirect: the address bar never holds the form's post, so the light/dark switch's way back is a page.
            Check("after Remove the address is the page, not the form's post", !third.Contains("value=\"/portal/machines?handler"));
            // Nothing typed into an address shows up as words on the page.
            var (_, crafted) = await Get(free, "/portal/machines?refused=Call+0800+to+verify&removed=Call+0800");
            var shown = string.Join(" ", Regex.Matches(crafted, "class=\"alert[^\"]*\"[^>]*>([^<]*)").Select(m => m.Groups[1].Value));
            Check("a crafted link cannot put words on the page — the messages are the page's own", !shown.Contains("0800"), shown);

            // ---- 3. the public page, and a full company beside it ----------------------------------
            Console.WriteLine();
            Console.WriteLine("== 3. the public download page, and a full company ==");
            using var anon = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var getFree = WebUtility.HtmlDecode(await anon.GetStringAsync($"/get/{freeSlug}"));
            var getFull = WebUtility.HtmlDecode(await anon.GetStringAsync($"/get/{fullSlug}"));
            Check("/get for a free company: add a computer, one download", getFree.Contains("id=\"freeGet\"") && getFree.Contains("Add a computer to"));
            Check("/get for a full company is its customers' page as ever", !getFull.Contains("id=\"freeGet\""));
            var (fullAt, _) = await Get(full, "/portal/machines");
            Check("a full company's Computers goes to its Devices page", fullAt.StartsWith("/portal/devices"), fullAt);
            var (ticketsAt, tickets) = await Get(full, "/portal/tickets");
            Check("and its tickets are where they always were", ticketsAt.StartsWith("/portal/tickets") && Regex.IsMatch(tickets, "<h1>\\s*Tickets"), ticketsAt);
            var (_, fullHome) = await Get(full, "/portal");
            Check("with its full menu", fullHome.Contains("href=\"/portal/tickets\"") && fullHome.Contains("href=\"/portal/technicians\""));

            await agentA.DisposeAsync(); await agentB.DisposeAsync();
            Console.WriteLine();
            Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ the portal, as a browser

    private static async Task<HttpClient?> SignIn(string baseUrl, string email, string password)
    {
        var hc = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var login = await hc.GetStringAsync("/login");
        var r = await hc.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email, ["Password"] = password, ["__RequestVerificationToken"] = Token(login)
        }));
        var (at, _) = await Get(hc, "/portal/account");
        return at.StartsWith("/portal/account") ? hc : null;
    }

    private static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

    private static async Task<(string at, string html)> Get(HttpClient hc, string path)
    {
        var r = await hc.GetAsync(path);
        return (r.RequestMessage!.RequestUri!.PathAndQuery, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
    }

    /// <summary>Posts the form found in <paramref name="fromHtml"/> — its own token, as a browser would (§80).</summary>
    private static async Task<string> Post(HttpClient hc, string path, string fromHtml, params (string k, string v)[] fields)
    {
        var form = Regex.Match(fromHtml, "<form[^>]*handler=Remove[^>]*>(.*?)</form>", RegexOptions.Singleline).Groups[1].Value;
        var all = fields.Select(f => new KeyValuePair<string, string>(f.k, f.v)).Append(new("__RequestVerificationToken", Token(form.Length > 0 ? form : fromHtml)));
        var r = await hc.PostAsync(path, new FormUrlEncodedContent(all));
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ plumbing (as freecheck)

    private sealed class Peer : IAsyncDisposable
    {
        public WsConnection Ws = null!;
        public readonly List<Msg> Got = new();
        public async Task<T?> WaitFor<T>(Func<T, bool> ok, int ms = 8000) where T : Msg
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                lock (Got) { var m = Got.OfType<T>().LastOrDefault(ok); if (m != null) return m; }
                await Task.Delay(40);
            }
            return null;
        }
        public async ValueTask DisposeAsync() { try { await Ws.CloseAsync("probe done"); } catch { } }
    }

    private static DeviceIdentity NewId() => new() { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };

    private static async Task<Peer> Device(int port, DeviceIdentity id, string tenantKey, string machine, string hardware)
    {
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/device"), CancellationToken.None);
        var peer = new Peer { Ws = new WsConnection(cws) };
        peer.Ws.OnMessage += m => { lock (peer.Got) peer.Got.Add(m); };
        _ = Task.Run(async () => { try { await peer.Ws.RunAsync(CancellationToken.None); } catch { } });
        peer.Ws.Send(new HelloMsg
        {
            Role = DeviceRole.Agent, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
            UserName = "probe", Os = "Windows 11 Pro", Version = "1.0", TenantKey = tenantKey, HardwareId = hardware
        });
        return peer;
    }

    private static async Task<(string Key, string Slug)> CreateTenant(string proj, string dataDir, int port, string name)
    {
        var created = await RunPlatform(proj, dataDir, port, "create-tenant", name);
        var line = created.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:", StringComparison.Ordinal))
                   ?? throw new InvalidOperationException("could not create " + name + ":\n" + created);
        var m = Regex.Match(created, @"\(slug ([^,\)]+)");
        return (line["Tenant key:".Length..].Trim(), m.Success ? m.Groups[1].Value.Trim() : "");
    }

    private static Process StartPlatform(string proj, string dataDir, int port)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> RunPlatform(string proj, string dataDir, int port, params string[] args)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stdout;
    }

    private static ProcessStartInfo Psi(string proj, string dataDir, int port)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
        psi.Environment["RSP_Platform__PublicUrl"] = $"http://127.0.0.1:{port}";
        psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
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
