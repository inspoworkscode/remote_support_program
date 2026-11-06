using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// UPGRADING FROM THE FREE VERSION, IN PLACE (2026-10-06, docs/NEXT-SESSION-FREE.md Stage G).
//
// Clint's answer: "Yes, in place" — the same account, the same two computers, the same saved passwords;
// a normal company once the owner accepts the full terms and price. So a free company with two
// computers, a remembered password and its console OPEN upgrades through the real page, and then:
//   * nothing it had has gone — the computers, the remembered password, the person;
//   * the hub's rules are the full version's — a third computer is let on, tickets answer;
//   * the open console is sent its new state at once (no sign-in needed);
//   * the portal is the full one;
//   * what was agreed is on record: the FULL terms word for word, the price from nothing to the package,
//     and a 14-day trial — it was already approved, so nobody approves it again.
// And before that, the page refuses: no tick, other terms, a price that changed.
//
//   dotnet run -c Release --project tools/upgradecheck/upgradecheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class UpgradeCheck
{
    private static int _pass, _fail;
    private const string Pw = "Branch-Office-2026";
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private sealed class Peer : IAsyncDisposable
    {
        public WsConnection Ws = null!;
        public readonly List<Msg> Got = new();
        public T? Last<T>() where T : Msg { lock (Got) return Got.OfType<T>().LastOrDefault(); }
        public void Clear() { lock (Got) Got.Clear(); }
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

    private static async Task<int> Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-upgradecheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var baseUrl = $"http://127.0.0.1:{port}";
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");

        string key, slug;
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Branch Offices");
            key = created.Split('\n').First(l => l.StartsWith("Tenant key:"))["Tenant key:".Length..].Trim();
            slug = Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();
            await RunPlatform(proj, dataDir, port, "set-brand", slug, "plan", "basic");
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "me@example.com", "FreeOwner!2026", "Fran Free", "--owner", "--technician");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }

            // ---- a free company, as it is used ------------------------------------------------
            var a = NewId(); var b = NewId();
            await using var agentA = await Device(port, a, DeviceRole.Agent, key, "BRANCH-A", "HW-A");
            await using var setter = await Device(port, a, DeviceRole.Client, key, "BRANCH-A", "HW-A");   // as the installer does
            await setter.WaitFor<UnattendedStateMsg>(_ => true);
            setter.Ws.Send(new SetUnattendedMsg { On = true });
            await setter.WaitFor<UnattendedStateMsg>(u => u.On);
            setter.Ws.Send(new SetUnattendedPasswordMsg { Password = Pw });
            await setter.WaitFor<UnattendedStateMsg>(u => u.HasPassword);
            await using var agentB = await Device(port, b, DeviceRole.Agent, key, "BRANCH-B", "HW-B");
            var tok = await Login(port, "me@example.com", "FreeOwner!2026");
            await using var console = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tok}");
            Check("(a free company's console)", (await console.WaitFor<StateMsg>(_ => true)) is { Free: true });
            console.Ws.Send(new ConnectDeviceMsg { DeviceId = a.DeviceId, Password = Pw });
            var s1 = await agentA.WaitFor<StartSessionMsg>(_ => true);
            if (s1 != null) { console.Ws.Send(new EndSessionMsg { SessionId = s1.SessionId }); await Task.Delay(800); }
            Check("(its password for BRANCH-A is remembered)", await console.WaitFor<SavedPasswordsMsg>(m => m.DeviceIds.Contains(a.DeviceId)) != null);

            // ---- 1. the page ----------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 1. the Upgrade page ==");
            var web = await SignIn(baseUrl, "me@example.com", "FreeOwner!2026");
            if (web == null) { Check("signed in to the portal", false); return 1; }
            var (_, home) = await Get(web, "/portal/machines");
            Check("the free menu offers Upgrade", home.Contains("id=\"navUpgrade\""));
            var (_, page) = await Get(web, "/portal/upgrade");
            Check("it shows the price: from R50 (the Basic version, since 2026-10-25) to R500 a month, free until a date",
                page.Contains("id=\"upgradeHeading\"") && page.Contains("Now: <strong>R50 a month</strong>") && page.Contains("R500 a month") && page.Contains("Nothing more is charged until"));
            Check("and the FULL version's terms — not the free section", page.Contains("id=\"termsText\"") && !page.Contains("Your own computers only"));
            var sha = Regex.Match(page, "name=\"TermsSha\" value=\"([^\"]+)\"").Groups[1].Value;
            var token = Regex.Match(page, "name=\"Agreed\" value=\"([^\"]+)\"").Groups[1].Value;
            Check("(the page carries what is to be agreed)", sha.Length == 64 && token.StartsWith("ZA:1:500"), token);

            var noTick = await Post(web, "/portal/upgrade", page, ("TermsSha", sha), ("Agreed", token));
            Check("no tick: refused, and still free", noTick.html.Contains("id=\"upgradeError\"") && Plan(dataDir, slug) == "basic");
            var otherTerms = await Post(web, "/portal/upgrade", page, ("TermsSha", "00"), ("Agreed", token), ("Accept", "true"));
            Check("other terms: refused", otherTerms.html.Contains("Our terms were updated") && Plan(dataDir, slug) == "basic");
            var otherPrice = await Post(web, "/portal/upgrade", page, ("TermsSha", sha), ("Agreed", "ZA:1:1"), ("Accept", "true"));
            Check("a price that is not the one shown: refused", otherPrice.html.Contains("price has changed") && Plan(dataDir, slug) == "basic");

            // ---- 2. upgrading ------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 2. upgrading ==");
            console.Clear();
            var done = await Post(web, "/portal/upgrade", page, ("TermsSha", sha), ("Agreed", token), ("Accept", "true"));
            Check("ticked, the price named: on Billing, saying so", done.at.StartsWith("/portal/billing") && done.html.Contains("Upgraded to the full version"), done.at);
            Check("the company is on the full version, in a 14-day trial — no approval again",
                Plan(dataDir, slug) == "five-user" && Text(dataDir, $"SELECT Status FROM Tenants WHERE Slug = '{slug}'") == "2"
                && Text(dataDir, $"SELECT {(HarnessDb.Postgres ? "CAST(FLOOR(EXTRACT(EPOCH FROM (TrialEndsUtc - now())) / 86400 + 0.5) AS INTEGER)" : "CAST(julianday(TrialEndsUtc) - julianday('now') + 0.5 AS INTEGER)")} FROM Tenants WHERE Slug = '{slug}'") == "14",
                Text(dataDir, $"SELECT Plan || ' ' || Status || ' ' || TrialEndsUtc FROM Tenants WHERE Slug = '{slug}'"));
            Check("the FULL terms are kept word for word",
                Text(dataDir, $"SELECT t.Sha256 FROM TermsAcceptances t JOIN Tenants n ON n.Id = t.TenantId WHERE n.Slug = '{slug}' ORDER BY t.Id DESC LIMIT 1") == sha);
            Check("the price agreed is on record: R50 to R500, by the owner",
                Text(dataDir, $"SELECT CAST(MonthlyBefore AS INTEGER) || '>' || CAST(Monthly AS INTEGER) || '|' || What || '|' || AgreedBy FROM SeatAgreements") == "50>500|Upgraded from the Basic version|Fran Free",
                Text(dataDir, "SELECT CAST(MonthlyBefore AS INTEGER) || '>' || CAST(Monthly AS INTEGER) || '|' || What || '|' || AgreedBy FROM SeatAgreements"));

            // ---- 3. nothing it had has gone; the rules are the full version's --------------------------
            Console.WriteLine();
            Console.WriteLine("== 3. the same account, now full ==");
            var newState = await console.WaitFor<StateMsg>(_ => true);
            Check("the OPEN console is told at once — the full console, no sign-in", newState is { Free: false, MayRemoveDevices: true });
            Check("with both computers", newState?.Devices.Select(d => d.MachineName).OrderBy(x => x).SequenceEqual(new[] { "BRANCH-A", "BRANCH-B" }) == true);
            Check("and its remembered password", await console.WaitFor<SavedPasswordsMsg>(m => m.DeviceIds.Contains(a.DeviceId)) != null);
            agentA.Clear();
            console.Ws.Send(new ConnectDeviceMsg { DeviceId = a.DeviceId, Reason = "after the upgrade" });
            var s2 = await agentA.WaitFor<StartSessionMsg>(_ => true);
            Check("connecting needs no password typed — still remembered", s2 is { AskConsent: false }, console.Last<ErrorMsg>()?.Message ?? "");
            if (s2 != null) { console.Ws.Send(new EndSessionMsg { SessionId = s2.SessionId }); await Task.Delay(800); }
            await using (var c = await Device(port, NewId(), DeviceRole.Agent, key, "BRANCH-C", "HW-C"))
            {
                await Task.Delay(1000);
                Check("a THIRD computer is let on now", c.Last<ErrorMsg>() == null, c.Last<ErrorMsg>()?.Message ?? "");
            }
            console.Clear();
            console.Ws.Send(new TicketListRequestMsg { Filter = "all" });
            Check("tickets answer — the session just held has one", (await console.WaitFor<TicketListMsg>(_ => true))?.Tickets.Count >= 1);

            var (_, portalHome) = await Get(web, "/portal");
            Check("the portal is the full one", portalHome.Contains("href=\"/portal/tickets\"") && !portalHome.Contains("id=\"navUpgrade\""));
            var (upAt, _) = await Get(web, "/portal/upgrade");
            Check("and Upgrade, opened again, goes to Billing", upAt.StartsWith("/portal/billing"), upAt);

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

    // ------------------------------------------------------------------ the database, read directly

    private static string Plan(string dataDir, string slug) => Text(dataDir, $"SELECT Plan FROM Tenants WHERE Slug = '{slug}'");
    private static string Text(string dataDir, string sql)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable(sql);
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    // ------------------------------------------------------------------ the portal, as a browser

    private static async Task<HttpClient?> SignIn(string baseUrl, string email, string password)
    {
        var hc = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var login = await hc.GetStringAsync("/login");
        await hc.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
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

    /// <summary>Posts the page's own form — its own token (§80) — following the redirect as a browser would.</summary>
    private static async Task<(string at, string html)> Post(HttpClient hc, string path, string fromHtml, params (string k, string v)[] fields)
    {
        var form = Regex.Match(fromHtml, "<form method=\"post\"[^>]*>(.*?)</form>", RegexOptions.Singleline).Groups[1].Value;
        var all = fields.Select(f => new KeyValuePair<string, string>(f.k, f.v)).Append(new("__RequestVerificationToken", Token(form)));
        var r = await hc.PostAsync(path, new FormUrlEncodedContent(all));
        return (r.RequestMessage!.RequestUri!.PathAndQuery, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
    }

    // ------------------------------------------------------------------ plumbing (as freecheck)

    private static DeviceIdentity NewId() => new() { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };

    private static async Task<Peer> Open(string url)
    {
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri(url), CancellationToken.None);
        var peer = new Peer { Ws = new WsConnection(cws) };
        peer.Ws.OnMessage += m => { lock (peer.Got) peer.Got.Add(m); };
        _ = Task.Run(async () => { try { await peer.Ws.RunAsync(CancellationToken.None); } catch { } });
        return peer;
    }

    private static async Task<Peer> Device(int port, DeviceIdentity id, DeviceRole role, string tenantKey, string machine, string hardware)
    {
        var peer = await Open($"ws://127.0.0.1:{port}/ws/device");
        peer.Ws.Send(new HelloMsg
        {
            Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
            UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey, HardwareId = hardware,
            Can = role == DeviceRole.Agent ? new List<string> { DeviceCan.AskConsent } : new List<string>()
        });
        return peer;
    }

    private static async Task<string?> Login(int port, string email, string password)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email, password });
        if (!lr.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
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
