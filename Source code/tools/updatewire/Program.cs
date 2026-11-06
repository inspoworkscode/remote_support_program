using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// AUTO-UPDATE, THE SERVER'S SIDE (2026-10-16, stage C, Services/UpdateService.cs).
//
// Against a real platform BUILT as a release (stamped like build.ps1 stamps it), with a payload of its own, three
// companies — our own (not billed: ring 0), a pilot (ring 1), another (ring 2) — and fake agents, tray apps and consoles:
//   - the two files are served as they are, with range requests (a broken download carries on);
//   - the release starts at ring 0: only OUR company's agents that said they update themselves are offered it, a few
//     at a time; never an agent that did not say so, never a tray app, never one already on it, never another ring;
//   - a console is told its company's console release in its state, and /update/console.json says the same;
//   - only a Full admin or an Owner moves it on (through the control API, as the dashboard does on any server);
//   - Pause stops new offers and Resume starts them again;
//   - a rollback pauses it BY ITSELF; a tray app's report is ignored;
//   - a machine that rolled back is never offered that version again — even after a restart;
//   - the /owner/updates page draws the release, its rings and what machines said.
//
//   dotnet run -c Release --project tools/updatewire/updatewire.csproj      (RSP_TEST_DB=postgres for PostgreSQL)
// ---------------------------------------------------------------------------------------------

internal static class UpdateWire
{
    private const string Stamp = "2026.10.16.1300";
    private const string Pw = "UpdateOwner!2026";
    private static readonly string ServerKey = new('s', 40);
    private static readonly string AdminKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private static int _pass, _fail, _port;
    private static string _base = "", _ws = "", _proj = "", _dataDir = "", _payload = "";
    private static List<string> _stampArgs = new();
    private static readonly ConcurrentQueue<string> _log = new();

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }
    private static void Head(string s) { Console.WriteLine(); Console.WriteLine($"== {s} =="); }

    private sealed class Peer : IAsyncDisposable
    {
        public string Name = "";
        public DeviceIdentity Id = null!;
        public WsConnection Ws = null!;
        public readonly List<(Msg m, DateTime at)> Got = new();
        public List<(UpdateOfferMsg m, DateTime at)> Offers { get { lock (Got) return Got.Where(g => g.m is UpdateOfferMsg).Select(g => ((UpdateOfferMsg)g.m, g.at)).ToList(); } }
        public async Task<T?> WaitFor<T>(Func<T, bool> ok, int ms = 8000) where T : Msg
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                lock (Got) { var m = Got.Select(g => g.m).OfType<T>().LastOrDefault(ok); if (m != null) return m; }
                await Task.Delay(50);
            }
            return null;
        }
        public async ValueTask DisposeAsync() { try { await Ws.CloseAsync("probe done"); } catch { } }
    }

    private static async Task<int> Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        _proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var (args, _) = await Ps($". '{Path.Combine(root, "tools", "BuildVersion.ps1")}'; (VersionArgs '{Stamp}') -join ' '");
        _stampArgs = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(a => a.StartsWith("-p:")).ToList();
        if (_stampArgs.Count < 3) { Console.WriteLine("tools\\BuildVersion.ps1 gave no VersionArgs."); return 1; }

        _dataDir = Path.Combine(Path.GetTempPath(), "rs-updatewire-" + DateTime.Now.ToString("HHmmss"));
        _payload = Path.Combine(_dataDir, "payloads");
        Directory.CreateDirectory(Path.Combine(_payload, "client"));
        Directory.CreateDirectory(Path.Combine(_payload, "technician"));
        var clientBytes = RandomNumberGenerator.GetBytes(300_000);
        var consoleBytes = RandomNumberGenerator.GetBytes(120_000);
        File.WriteAllBytes(Path.Combine(_payload, "client", "RemoteSupport.Client.exe"), clientBytes);
        File.WriteAllText(Path.Combine(_payload, "client", "client-exe.packed"), "packed for updatewire");
        File.WriteAllBytes(Path.Combine(_payload, "technician", "RemoteSupport.Technician.exe"), consoleBytes);
        File.WriteAllText(Path.Combine(_payload, "technician", "console-exe.ready"), "ready for updatewire");
        var clientSha = Convert.ToHexString(SHA256.HashData(clientBytes)).ToLowerInvariant();

        _port = FreePort(); _base = $"http://127.0.0.1:{_port}"; _ws = $"ws://127.0.0.1:{_port}";
        Console.WriteLine($"A platform of its own, BUILT as {Stamp}, on port {_port} ({HarnessDb.Name})");

        var tenants = new Dictionary<string, (string key, string slug)>();
        try
        {
            foreach (var (name, tag) in new[] { ("Own Company", "A"), ("Pilot Company", "B"), ("Other Company", "C") })
            {
                var created = await RunPlatform("create-tenant", name);
                var key = created.Split('\n').First(l => l.StartsWith("Tenant key:"))["Tenant key:".Length..].Trim();
                var slug = Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();
                await RunPlatform("add-tech", slug, $"boss{tag.ToLowerInvariant()}@example.com", Pw, $"Boss {tag}", "--owner", "--technician");
                tenants[tag] = (key, slug);
            }
            Exec($"UPDATE Tenants SET NotBilled = TRUE WHERE Slug = '{tenants["A"].slug}'");
            Exec($"UPDATE Tenants SET Pilot = TRUE WHERE Slug = '{tenants["B"].slug}'");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform();
        var peers = new List<Peer>();
        try
        {
            if (!await WaitForPort(_port, 180000)) { Console.WriteLine("The platform never started listening."); return 1; }

            Head("The files");
            using (var hc = new HttpClient())
            {
                var got = await hc.GetByteArrayAsync($"{_base}/update/client.exe");
                Check("the customer app is served as it is", got.AsSpan().SequenceEqual(clientBytes), $"{got.Length} bytes");
                var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/update/client.exe");
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
                var part = await hc.SendAsync(req);
                var partBytes = await part.Content.ReadAsByteArrayAsync();
                Check("a broken download carries on where it stopped (a range request)", part.StatusCode == HttpStatusCode.PartialContent
                    && partBytes.AsSpan().SequenceEqual(clientBytes.AsSpan(1000, 1000)), $"{(int)part.StatusCode}, {partBytes.Length} bytes");
                var con = await hc.GetByteArrayAsync($"{_base}/update/console.exe");
                Check("the console is served as it is", con.AsSpan().SequenceEqual(consoleBytes));
            }
            var st = await Status();
            var u = st?.GetProperty("update");
            Check($"the release is {Stamp}, at ring 0 (our own company), not paused",
                u is { } x && x.GetProperty("version").GetString() == Stamp && x.GetProperty("ring").GetInt32() == 0 && !x.GetProperty("paused").GetBoolean(),
                u?.ToString() ?? "no update in the report");
            Check("its hash is the file's", u?.GetProperty("client").GetProperty("sha256").GetString() == clientSha);

            Head("Ring 0: our own company's machines only");
            Peer Agent(string tag, string name, string version = "1.0.0.0", bool selfUpdates = true)
                => Hello(tenants[tag].key, name, DeviceRole.Agent, version, selfUpdates);
            var a = new List<Peer>();
            for (int i = 1; i <= 5; i++) a.Add(Agent("A", $"OWN-{i}"));
            var aOld = Agent("A", "OWN-OLD", selfUpdates: false);
            var aTray = Hello(tenants["A"].key, "OWN-TRAY", DeviceRole.Client, "1.0.0.0", selfUpdates: true);
            var aCurrent = Agent("A", "OWN-CURRENT", Stamp);
            var b1 = Agent("B", "PILOT-1");
            var c1 = Agent("C", "OTHER-1");
            peers.AddRange(a); peers.AddRange(new[] { aOld, aTray, aCurrent, b1, c1 });

            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline && a.Any(p => p.Offers.Count == 0)) await Task.Delay(200);
            Check("every one of our own company's agents that updates itself is offered it", a.All(p => p.Offers.Count > 0),
                $"{a.Count(p => p.Offers.Count > 0)} of {a.Count}");
            var offer = a[0].Offers.FirstOrDefault().m;
            Check("the offer names the release, its address on THIS server and its hash",
                offer?.Release is { } r && r.Version == Stamp && r.Path == "/update/client.exe" && r.Sha256 == clientSha && r.Size == clientBytes.Length,
                offer == null ? "none" : $"{offer.Release.Version} {offer.Release.Path} {offer.Release.Size}");
            var times = a.Select(p => p.Offers.First().at).OrderBy(t => t).ToList();
            Check("offered a few at a time, not all at once (2 a sweep here)", times.Count == 5 && (times[2] - times[0]).TotalSeconds >= 1.0 && (times[4] - times[0]).TotalSeconds >= 2.5,
                string.Join(", ", times.Select(t => $"+{(t - times[0]).TotalSeconds:0.0}s")));
            Check("never an agent that did not say it updates itself (installed before auto-update)", aOld.Offers.Count == 0);
            Check("never a tray app, whatever it says", aTray.Offers.Count == 0);
            Check("never a machine already on it", aCurrent.Offers.Count == 0);
            Check("not yet the pilots, nor anybody else", b1.Offers.Count == 0 && c1.Offers.Count == 0);

            var conA = await ConsoleOf(tenants["A"].slug, "bossa@example.com");
            var conC = await ConsoleOf(tenants["C"].slug, "bossc@example.com");
            peers.Add(conA); peers.Add(conC);
            var sA = await conA.WaitFor<StateMsg>(_ => true);
            var sC = await conC.WaitFor<StateMsg>(_ => true);
            Check("our own company's console is told the console release in its state",
                sA?.ConsoleUpdate is { } cu && cu.Version == Stamp && cu.Path == "/update/console.exe" && cu.Size == consoleBytes.Length,
                sA?.ConsoleUpdate == null ? "nothing" : sA.ConsoleUpdate.Version);
            Check("another company's console is not, yet", sC != null && sC.ConsoleUpdate == null);
            using (var hc = new HttpClient())
            {
                var ja = await hc.GetAsync($"{_base}/update/console.json?slug={tenants["A"].slug}");
                var jc = await hc.GetAsync($"{_base}/update/console.json?slug={tenants["C"].slug}");
                Check("/update/console.json, asked before anybody signs in, says the same (200 / 204)",
                    ja.StatusCode == HttpStatusCode.OK && (await ja.Content.ReadAsStringAsync()).Contains(Stamp) && jc.StatusCode == HttpStatusCode.NoContent,
                    $"{(int)ja.StatusCode} / {(int)jc.StatusCode}");
            }

            Head("Moving it on");
            var (manager, _) = await Act("update-ring", ("ring", "1"), level: 2);
            Check("a Manager cannot move it on — only a Full admin or an Owner", manager.Contains("Only a Full admin"), manager);
            var (full, said) = await Act("update-ring", ("ring", "1"));
            Check("a Full admin moves it on to the pilots", full.Length == 0, full.Length > 0 ? full : said);
            Check("the pilot's agent is offered it", (await b1.WaitFor<UpdateOfferMsg>(_ => true, 10000)) != null);
            await Task.Delay(2500);
            Check("and still nobody else", c1.Offers.Count == 0);

            Head("Pause and resume");
            var (paused, _) = await Act("update-pause");
            Check("paused", paused.Length == 0 && (await Status())?.GetProperty("update").GetProperty("paused").GetBoolean() == true, paused);
            var a6 = Agent("A", "OWN-6");
            peers.Add(a6);
            await Task.Delay(5000);
            Check("while paused, a machine that comes along is not offered it", a6.Offers.Count == 0);
            var (resumed, _) = await Act("update-resume");
            Check("resumed — and it is offered it", resumed.Length == 0 && (await a6.WaitFor<UpdateOfferMsg>(_ => true, 10000)) != null, resumed);

            Head("What machines report");
            foreach (var step in new[] { UpdateSteps.Downloading, UpdateSteps.Ready, UpdateSteps.Installing })
                a[0].Ws.Send(new UpdateReportMsg { Version = Stamp, Step = step });
            aTray.Ws.Send(new UpdateReportMsg { Version = Stamp, Step = UpdateSteps.RolledBack, Detail = "a tray app has no say" });
            await Task.Delay(1500);
            Check("an agent's steps are kept as its machine's history", Count($"SELECT COUNT(*) FROM DeviceUpdateEvents WHERE DeviceId = '{a[0].Id.DeviceId}' AND Step <> 'offered'") == 3);
            Check("a tray app's report is ignored — and pauses nothing", Count($"SELECT COUNT(*) FROM DeviceUpdateEvents WHERE DeviceId = '{aTray.Id.DeviceId}'") == 0
                && (await Status())?.GetProperty("update").GetProperty("paused").GetBoolean() == false);
            a[1].Ws.Send(new UpdateReportMsg { Version = Stamp, Step = UpdateSteps.RolledBack, Detail = "the new agent did not connect within 5 minutes" });
            await Task.Delay(1500);
            var auto = (await Status())?.GetProperty("update");
            Check("a rollback pauses the release BY ITSELF", auto?.GetProperty("paused").GetBoolean() == true
                && (auto?.GetProperty("pausedWhy").GetString() ?? "").Contains("automatically"), auto?.GetProperty("pausedWhy").GetString() ?? "");
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            foreach (var p in peers) await p.DisposeAsync();
            peers.Clear();
        }

        Head("After a restart");
        server = StartPlatform();
        try
        {
            if (!await WaitForPort(_port, 180000)) { Check("the platform starts again", false); return 1; }
            var (resumed, _) = await Act("update-resume");
            Check("resumed", resumed.Length == 0, resumed);
            var rolledBack = ReHello(tenants["A"].key, "OWN-2");
            var fresh = Hello(tenants["A"].key, "OWN-7", DeviceRole.Agent, "1.0.0.0", true);
            peers.Add(rolledBack); peers.Add(fresh);
            Check("a machine never offered it is", (await fresh.WaitFor<UpdateOfferMsg>(_ => true, 12000)) != null);
            await Task.Delay(4000);
            Check("the machine that rolled back is NEVER offered that version again", rolledBack.Offers.Count == 0);

            var (everybody, _) = await Act("update-ring", ("ring", "2"));
            var c2 = Hello(tenants["C"].key, "OTHER-2", DeviceRole.Agent, "1.0.0.0", true);
            peers.Add(c2);
            Check("offered to everybody: another company's agent is offered it", everybody.Length == 0 && (await c2.WaitFor<UpdateOfferMsg>(_ => true, 12000)) != null, everybody);

            Head("The page");
            var page = await OwnerPage("/owner/updates");
            Check("/owner/updates draws the release and how far it has gone",
                page.Contains($"data-release=\"{Stamp}\"") && page.Contains("data-ring=\"2\"") && page.Contains("Release " + Stamp), Snip(page, "data-release"));
            Check("...each ring's machines", Regex.Matches(page, "<tr data-ring=\"[012]\"").Count == 3);
            Check("...and what machines said, the rollback among it", page.Contains("data-step=\"rolled-back\"") && page.Contains("Rolled back"));
            Check("...with the buttons a Full admin needs", page.Contains("id=\"pause\""));
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            foreach (var p in peers) await p.DisposeAsync();
            try { await Task.Delay(300); Directory.Delete(_dataDir, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ peers

    private static readonly Dictionary<string, DeviceIdentity> _ids = new();

    private static Peer Hello(string tenantKey, string name, DeviceRole role, string version, bool selfUpdates)
    {
        if (!_ids.TryGetValue(name, out var id)) _ids[name] = id = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
        var p = Open($"{_ws}/ws/device").GetAwaiter().GetResult();
        p.Name = name; p.Id = id;
        p.Ws.Send(new HelloMsg
        {
            Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = name, Os = "probe", Version = version,
            TenantKey = tenantKey, HardwareId = "HW-" + name, UserName = role == DeviceRole.Client ? "sue" : "",
            Can = selfUpdates ? new List<string> { DeviceCan.AskConsent, DeviceCan.Update } : new List<string> { DeviceCan.AskConsent }
        });
        return p;
    }

    private static Peer ReHello(string tenantKey, string name) => Hello(tenantKey, name, DeviceRole.Agent, "1.0.0.0", true);

    private static async Task<Peer> ConsoleOf(string slug, string email)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var lr = await hc.PostAsJsonAsync($"{_base}/api/login", new { email, password = Pw });
        var token = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        return await Open($"{_ws}/ws/tech?token={token}");
    }

    private static async Task<Peer> Open(string url)
    {
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri(url), CancellationToken.None);
        var peer = new Peer { Ws = new WsConnection(cws) };
        peer.Ws.OnMessage += m => { lock (peer.Got) peer.Got.Add((m, DateTime.UtcNow)); };
        _ = Task.Run(async () => { try { await peer.Ws.RunAsync(CancellationToken.None); } catch { } });
        return peer;
    }

    // ------------------------------------------------------------------ the control API, as the dashboard uses it

    private static async Task<JsonElement?> Status()
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        hc.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ServerKey);
        var r = await hc.GetAsync($"{_base}/api/control/status");
        return r.IsSuccessStatusCode ? JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone() : null;
    }

    /// <summary>An action as another server's dashboard asks it. <paramref name="level"/> 3 is Full, 2 Manager.</summary>
    private static async Task<(string error, string said)> Act(string action, params (string k, string v)[] args) => await Act(action, 3, args);
    private static async Task<(string error, string said)> Act(string action, (string k, string v) arg, int level) => await Act(action, level, new[] { arg });
    private static async Task<(string error, string said)> Act(string action, int level, (string k, string v)[] args)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        hc.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ServerKey);
        var r = await hc.PostAsJsonAsync($"{_base}/api/control/act", new
        {
            action, id = 0, args = args.ToDictionary(a => a.k, a => (string?)a.v), by = "Harness Admin", level, from = "updatewire"
        });
        if (!r.IsSuccessStatusCode) return ($"HTTP {(int)r.StatusCode}", "");
        var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return (j.GetProperty("error").GetString() ?? "", j.GetProperty("said").GetString() ?? "");
    }

    // ------------------------------------------------------------------ the page, signed in with the admin key

    private static async Task<string> OwnerPage(string path)
    {
        var c = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(_base), Timeout = TimeSpan.FromSeconds(30) };
        var form = await (await c.GetAsync("/owner/login")).Content.ReadAsStringAsync();
        var step = await (await c.PostAsync("/owner/login", Form(form, ("Key", AdminKey)))).Content.ReadAsStringAsync();
        var secret = Regex.Match(step, "user-select:all\">([A-Z2-7 ]+)<").Groups[1].Value.Replace(" ", "");
        var pending = Regex.Match(step, "name=\"Pending\" value=\"([^\"]*)\"").Groups[1].Value;
        await c.PostAsync("/owner/login", Form(step, ("UseKey", "true"), ("Pending", pending), ("Code", Totp.Now(secret))));
        return await (await c.GetAsync(path)).Content.ReadAsStringAsync();
    }

    private static FormUrlEncodedContent Form(string pageWithToken, params (string k, string v)[] f)
    {
        var token = Regex.Match(pageWithToken, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        if (token.Length == 0) token = Regex.Match(pageWithToken, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        return new FormUrlEncodedContent(f.Select(x => new KeyValuePair<string, string>(x.k, x.v)).Append(new("__RequestVerificationToken", token)));
    }

    private static string Snip(string html, string near)
    {
        var i = html.IndexOf(near, StringComparison.Ordinal);
        return i < 0 ? (html.Length > 200 ? html[..200] : html) : html.Substring(Math.Max(0, i - 40), Math.Min(200, html.Length - Math.Max(0, i - 40)));
    }

    // ------------------------------------------------------------------ the database, the platform

    private static int Count(string sql) => Convert.ToInt32(Scalar(sql) ?? 0);
    private static object? Scalar(string sql)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(_dataDir, "platform.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable(sql);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }
    private static void Exec(string sql)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(_dataDir, "platform.db")};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable(sql);
        cmd.ExecuteNonQuery();
    }

    private static Process StartPlatform()
    {
        var psi = Psi();
        psi.ArgumentList.Add("--"); psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) _log.Enqueue(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) _log.Enqueue(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    private static async Task<string> RunPlatform(params string[] args)
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
        psi.ArgumentList.Add("run");
        // Built as a release, in a configuration of its own (bin\Versioned), exactly as tools/versioncheck does.
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Versioned");
        foreach (var a in _stampArgs) psi.ArgumentList.Add("--property:" + a["-p:".Length..]);
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(_proj);
        psi.Environment["RSP_Platform__DataDirectory"] = _dataDir;
        HarnessDb.Use(psi.Environment, _dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";
        psi.Environment["RSP_Platform__PublicUrl"] = _base;
        psi.Environment["RSP_Platform__ServerKey"] = ServerKey;
        psi.Environment["RSP_Platform__AdminKey"] = AdminKey;
        psi.Environment["RSP_Platform__ClientPayloadDir"] = Path.Combine(_payload, "client");
        psi.Environment["RSP_Platform__TechnicianPayloadDir"] = Path.Combine(_payload, "technician");
        // A sweep every 2 s, 60 a minute: 2 offers a sweep — slow enough to see them spread out, quick enough to wait for.
        psi.Environment["RSP_Updates__SweepSeconds"] = "2";
        psi.Environment["RSP_Updates__PerMinute"] = "60";
        psi.Environment["RSP_Urls"] = _base;
        psi.Environment["ASPNETCORE_URLS"] = _base;
        return psi;
    }

    private static async Task<(string Out, string Err)> Ps(string script)
    {
        var psi = new ProcessStartInfo("powershell") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await o, await e);
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
