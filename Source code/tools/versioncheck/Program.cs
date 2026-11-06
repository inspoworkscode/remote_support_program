using System.Collections.Concurrent;
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
// REAL VERSION NUMBERS (2026-10-16, auto-update stage A, docs/NEXT-SESSION-AUTOUPDATE.md).
//
// Every app said 1.0.0 until today, so no two builds could be told apart and auto-update would have nothing to
// compare. Now build.ps1 (and ship-build.ps1, and Pack-ClientExe.ps1) stamp every app of one build with the moment it
// was built in UTC — tools\BuildVersion.ps1 — and the server keeps what each machine's agent and tray app say, so the
// console, the portal's Devices page and the /owner dashboard can show it. This proves, in turn:
//   - the rules (Shared/AppVersion.cs): order, "the older of the two halves", how a version is shown;
//   - tools\BuildVersion.ps1 makes a version Windows accepts and refuses one it does not, and every build script uses it;
//   - a platform BUILT with a version reports it (the stamp reaches AppVersion.Current in a running app);
//   - the hub keeps the agent's and the tray app's versions apart, in the database and live, a newer hello replaces an
//     older one, the free installer's blank hello clears the tray app's, and they are still there after a restart;
//   - /api/control/status (what the dashboard draws) counts machines by version, newest first;
//   - the portal's Devices page has a Version column, amber for a machine older than the server's apps.
//
//   dotnet run -c Release --project tools/versioncheck/versioncheck.csproj      (RSP_TEST_DB=postgres for PostgreSQL)
// ---------------------------------------------------------------------------------------------

internal static class VersionCheck
{
    /// <summary>What the throwaway platform is built as — a stamped version, as build.ps1 would make.</summary>
    private const string Stamp = "2026.10.16.1200";
    private const string Older = "2026.10.15.900";
    private const string Pw = "VersionOwner!2026";
    private static readonly string ServerKey = new('k', 40);
    private static int _pass, _fail;
    private static readonly ConcurrentQueue<string> _log = new();
    /// <summary>VersionArgs from tools\BuildVersion.ps1 for <see cref="Stamp"/>, read in <see cref="BuildScripts"/>.</summary>
    private static List<string> _stampArgs = new();

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

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

    private static async Task<int> Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }

        Rules();
        await BuildScripts(root);
        await Platform(root);

        Console.WriteLine();
        Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ the rules

    private static void Rules()
    {
        Console.WriteLine("== The rules (AppVersion) ==");
        Check("a later build is newer: 2026.10.16.905 < 2026.10.16.1432 (09:05 before 14:32)",
            AppVersion.Compare("2026.10.16.905", "2026.10.16.1432") < 0);
        Check("a later day beats a later time: 2026.10.16.2359 < 2026.10.17.1",
            AppVersion.Compare("2026.10.16.2359", "2026.10.17.1") < 0);
        Check("every unstamped build (1.0.0.0) is older than any stamped one", AppVersion.Compare("1.0.0.0", Older) < 0);
        Check("a machine that never said is older than anything — never taken for up to date",
            AppVersion.Compare("", "1.0.0.0") < 0 && AppVersion.Compare("1.0.0.0", "") > 0);
        Check("a machine's version is the OLDER of its two halves, blanks ignored",
            AppVersion.Oldest(Stamp, Older) == Older && AppVersion.Oldest("", Stamp) == Stamp && AppVersion.Oldest("", "") == "",
            $"{AppVersion.Oldest(Stamp, Older)} / {AppVersion.Oldest("", Stamp)}");
        Check("shown: the stamp as it is, \"1.0 (old)\" before version numbers, a dash for nothing",
            AppVersion.Label(Stamp) == Stamp && AppVersion.Label("1.0.0.0") == "1.0 (old)" && AppVersion.Label("") == "—",
            $"{AppVersion.Label(Stamp)} | {AppVersion.Label("1.0.0.0")} | {AppVersion.Label("")}");
        Check("a stamp says when it was built: 2026.10.16.905 is 09:05 UTC on 16 October 2026",
            AppVersion.BuiltUtc("2026.10.16.905") == new DateTime(2026, 10, 16, 9, 5, 0, DateTimeKind.Utc) && AppVersion.BuiltUtc("1.0.0.0") == null);
        Check("this harness, built without a stamp, says it is unstamped",
            !AppVersion.IsStamped(AppVersion.Current), AppVersion.Current);
    }

    // ------------------------------------------------------------------ tools\BuildVersion.ps1 and the build scripts

    private static async Task BuildScripts(string root)
    {
        Console.WriteLine();
        Console.WriteLine("== The build scripts ==");
        var bv = Path.Combine(root, "tools", "BuildVersion.ps1");
        var (made, _) = await Ps($". '{bv}'; New-BuildVersion");
        var now = DateTime.UtcNow;
        var parsed = AppVersion.BuiltUtc(made.Trim());
        Check("New-BuildVersion makes four numbers from the moment of the build, in UTC",
            Regex.IsMatch(made.Trim(), @"^\d{4}\.\d{1,2}\.\d{1,2}\.\d{1,4}$") && parsed != null && Math.Abs((now - parsed.Value).TotalMinutes) < 3,
            made.Trim());
        var (chosen, _) = await Ps($". '{bv}'; New-BuildVersion '{Stamp}'");
        Check("a chosen version is taken as it is (-Version, for a build N+1 on purpose)", chosen.Trim() == Stamp, chosen.Trim());
        var (_, bad1) = await Ps($". '{bv}'; New-BuildVersion '1.2.3'");
        var (_, bad2) = await Ps($". '{bv}'; New-BuildVersion '2026.10.16.70000'");
        Check("refused: three numbers, and a part over Windows' 65534", bad1.Contains("four numbers") && bad2.Contains("65534"),
            $"{bad1.Trim()} | {bad2.Trim()}");
        var (args, _) = await Ps($". '{bv}'; (VersionArgs '{Stamp}') -join ' '");
        _stampArgs = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(a => a.StartsWith("-p:")).ToList();
        Check("VersionArgs stamps the assembly AND the file version, with no git hash on the end",
            args.Contains($"-p:AssemblyVersion={Stamp}") && args.Contains($"-p:FileVersion={Stamp}")
            && args.Contains("IncludeSourceRevisionInInformationalVersion=false"), args.Trim());

        string Read(params string[] p) => File.ReadAllText(Path.Combine(new[] { root }.Concat(p).ToArray()));
        var build = Read("build.ps1");
        Check("build.ps1 makes one version and passes it to every publish, the platform and the packed .exe",
            build.Contains("tools\\BuildVersion.ps1") && build.Contains("+ $versionArgs + $extra")
            && Regex.Matches(build, @"-o \$(serverLinux|plat) @versionArgs").Count == 2 && build.Contains("'-Version', $buildVersion"));
        Check("ship-build.ps1 stamps what it publishes", Read("tools", "ship-build.ps1").Contains("@versionArgs @extra"));
        var pack = Read("tools", "Pack-ClientExe.ps1");
        Check("Pack-ClientExe.ps1 stamps the tray app like the host it packs (read off the host when not told)",
            pack.Contains("Get-StagedVersion (Join-Path $PayloadDir 'RemoteSupport.Host.exe')") && pack.Contains("-o $out --nologo -v quiet @versionArgs"));
    }

    // ------------------------------------------------------------------ a real, stamped platform

    private static async Task Platform(string root)
    {
        Console.WriteLine();
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-versioncheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var wsUrl = $"ws://127.0.0.1:{port}";
        Console.WriteLine($"== A platform of its own, BUILT as version {Stamp}, on port {port} ({HarnessDb.Name}) ==");

        string key;
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Version Company");
            key = created.Split('\n').First(l => l.StartsWith("Tenant key:"))["Tenant key:".Length..].Trim();
            var slug = Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "boss@example.com", Pw, "Bo Boss", "--owner", "--technician");
        }
        catch (Exception ex) { Check("set up the platform", false, ex.Message); return; }

        var server = StartPlatform(proj, dataDir, port);
        var ids = new Dictionary<string, DeviceIdentity> { ["A"] = NewId(), ["B"] = NewId(), ["C"] = NewId(), ["E"] = NewId() };
        try
        {
            if (!await WaitForPort(port, 120000)) { Check("the platform starts", false, "never listened"); return; }
            var status = await Status(port);
            Check($"a platform built as {Stamp} says so (the stamp reaches a running app)",
                status?.GetProperty("appVersion").GetString() == Stamp, status?.GetProperty("appVersion").GetString() ?? "no answer");

            var token = await Login(port, "boss@example.com", Pw);
            await using var console = await Open($"{wsUrl}/ws/tech?token={token}");
            await console.WaitFor<StateMsg>(_ => true);

            // A: an agent already updated, its tray app not yet. B: an agent only, up to date. C: from before version
            // numbers. E: an older tray app, then the free installer's blank hello.
            var peers = new List<Peer>
            {
                await HelloAsync(wsUrl, ids["A"], "PC-A", DeviceRole.Agent, Stamp, key),
                await HelloAsync(wsUrl, ids["A"], "PC-A", DeviceRole.Client, Older, key),
                await HelloAsync(wsUrl, ids["B"], "PC-B", DeviceRole.Agent, Stamp, key),
                await HelloAsync(wsUrl, ids["C"], "PC-C", DeviceRole.Agent, "1.0.0.0", key),
                await HelloAsync(wsUrl, ids["C"], "PC-C", DeviceRole.Client, "1.0.0.0", key),
            };
            var eTray = await HelloAsync(wsUrl, ids["E"], "PC-E", DeviceRole.Client, "1.0.0.0", key);
            await Task.Delay(2000);

            Check("the agent's and the tray app's versions are stored APART",
                Text(dataDir, ids["A"], "AgentVersion") == Stamp && Text(dataDir, ids["A"], "ClientVersion") == Older,
                $"agent {Text(dataDir, ids["A"], "AgentVersion")}, tray app {Text(dataDir, ids["A"], "ClientVersion")}");
            Check("an agent-only machine has no tray app version", Text(dataDir, ids["B"], "ClientVersion") == "");
            var seenA = await console.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == ids["A"].DeviceId && u.Device.ClientVersion == Older);
            Check("the console is told both", seenA?.Device.AgentVersion == Stamp, seenA == null ? "nothing" : $"agent {seenA.Device.AgentVersion}");

            await using (var aTray = await HelloAsync(wsUrl, ids["A"], "PC-A", DeviceRole.Client, Stamp, key))
            {
                var now = await console.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == ids["A"].DeviceId && u.Device.ClientVersion == Stamp);
                await Task.Delay(800);
                Check("a newer hello replaces the older version, live and stored", now != null && Text(dataDir, ids["A"], "ClientVersion") == Stamp,
                    Text(dataDir, ids["A"], "ClientVersion"));
            }

            await eTray.DisposeAsync();
            await using (var installer = await HelloAsync(wsUrl, ids["E"], "PC-E", DeviceRole.Client, "", key))
            {
                await Task.Delay(1200);
                Check("the free installer's blank hello clears the tray app's version (a free machine has no tray app)",
                    Text(dataDir, ids["E"], "ClientVersion") == "", Text(dataDir, ids["E"], "ClientVersion"));
            }

            var counts = (await Status(port))?.GetProperty("machineVersions").EnumerateArray()
                .Select(v => (v.GetProperty("version").GetString() ?? "", v.GetProperty("machines").GetInt32())).ToList() ?? new();
            var said = string.Join(", ", counts.Select(c => $"{(c.Item1.Length == 0 ? "none" : c.Item1)} ×{c.Item2}"));
            Check("the dashboard's counts: by each machine's older half, newest first",
                counts.SequenceEqual(new[] { (Stamp, 2), ("1.0.0.0", 1), ("", 1) }), said);

            var page = await PortalPage(port, "/portal/devices");
            Check("the portal's Devices page has a Version column", page.Contains("<th>Version</th>"));
            var cellC = Cell(page, "1.0.0.0");
            Check("a machine from before version numbers reads \"1.0 (old)\", amber — older than the server's apps",
                cellC.Contains("pill-warn") && cellC.Contains("1.0 (old)"), Squash(cellC));
            var cellB = Cell(page, Stamp);
            Check("an up-to-date machine reads its version, plain", cellB.Contains(Stamp) && !cellB.Contains("pill"), Squash(cellB));
            Check("its tooltip names each half", cellC.Contains("Background service 1.0 (old)") && cellC.Contains("Tray app 1.0 (old)")
                && cellC.Contains("Older than this server") && cellC.Contains(Stamp), Squash(cellC));
            // Auto-update stage F (2026-10-16): none of these agents says it updates itself — installed before auto-update.
            Check("a machine installed before auto-update says it needs one update by hand, and the page says how",
                cellC.Contains("update by hand once") && page.Contains("id=\"updateByHand\"") && page.Contains("Update this machine"), Squash(cellC));

            foreach (var p in peers) await p.DisposeAsync();
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
        }

        // ---- a restart: machines that are OFF still show what they run ------------------------------------------
        server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 120000)) { Check("the platform starts again", false); return; }
            var token = await Login(port, "boss@example.com", Pw);
            await using var console = await Open($"{wsUrl}/ws/tech?token={token}");
            var state = await console.WaitFor<StateMsg>(_ => true);
            var c = state?.Devices.FirstOrDefault(d => d.DeviceId == ids["C"].DeviceId);
            Check("after a restart, an offline machine still carries both versions", c is { Online: false, AgentVersion: "1.0.0.0", ClientVersion: "1.0.0.0" },
                c == null ? "not listed" : $"online={c.Online} agent={c.AgentVersion} tray={c.ClientVersion}");
        }
        finally
        {
            try { if (!server.HasExited) server.Kill(true); } catch { }
            try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
        }
    }

    private static async Task<Peer> HelloAsync(string wsUrl, DeviceIdentity id, string name, DeviceRole role, string version, string tenantKey)
    {
        var p = await Open($"{wsUrl}/ws/device");
        p.Ws.Send(new HelloMsg
        {
            Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = name, Os = "probe",
            Version = version, TenantKey = tenantKey, HardwareId = "HW-" + name, UserName = role == DeviceRole.Client ? "sue" : ""
        });
        return p;
    }

    /// <summary>The Version cell of the row whose cell carries <paramref name="version"/>.</summary>
    private static string Cell(string page, string version)
    {
        var m = Regex.Match(page, $"<td class=\"version\" data-version=\"{Regex.Escape(version)}\"[\\s\\S]*?</td>");
        return m.Success ? m.Value : "";
    }

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // ------------------------------------------------------------------ plumbing

    private static async Task<JsonElement?> Status(int port)
    {
        try
        {
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            hc.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ServerKey);
            var r = await hc.GetAsync($"http://127.0.0.1:{port}/api/control/status");
            if (!r.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        catch { return null; }
    }

    private static async Task<string> PortalPage(int port, string path)
    {
        var jar = new CookieContainer();
        using var c = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = true }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var page = await c.GetStringAsync("/login");
        var token = Between(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"", "\"")
                    ?? Between(page, "name=\"__RequestVerificationToken\" value=\"", "\"") ?? "";
        await c.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "boss@example.com", ["Password"] = Pw, ["__RequestVerificationToken"] = token
        }));
        return await c.GetStringAsync(path);
    }

    private static string? Between(string s, string a, string b)
    {
        var i = s.IndexOf(a, StringComparison.Ordinal);
        if (i < 0) return null;
        i += a.Length;
        var j = s.IndexOf(b, i, StringComparison.Ordinal);
        return j < 0 ? null : s[i..j];
    }

    private static async Task<(string Out, string Err)> Ps(string script)
    {
        var psi = new ProcessStartInfo("powershell") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script }) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await o, await e);
    }

    private static string Text(string dataDir, DeviceIdentity id, string column)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable($"SELECT {column} FROM Devices WHERE DeviceId = '{id.DeviceId}'");
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? "" : Convert.ToString(v) ?? "";
    }

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
        p.OutputDataReceived += (_, e) => { if (e.Data != null) _log.Enqueue(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) _log.Enqueue(e.Data); };
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
        // Its own configuration, so the stamped build never lands in the Debug or Release output other harnesses and the
        // local platform use: "Versioned" builds like Debug, into bin\Versioned.
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Versioned");
        // EXACTLY what build.ps1 passes, from tools\BuildVersion.ps1 itself: Version alone is not enough, because
        // Directory.Build.props pins AssemblyVersion and FileVersion too (the first run of this harness found that).
        foreach (var a in _stampArgs) psi.ArgumentList.Add("--property:" + a["-p:".Length..]);
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
        psi.Environment["RSP_Platform__PublicUrl"] = $"http://127.0.0.1:{port}";
        psi.Environment["RSP_Platform__ServerKey"] = ServerKey;        // /api/control/status, what the dashboard reads
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
