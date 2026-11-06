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
// REMOVING A MACHINE, on the full version, and UNINSTALLING from the tray app (2026-10-06).
//
// Clint: "The full system also needs a remove option for devices, both via console and portal with a
// reason why. this is important for when machines get upgraded, replaced or a person has been using
// their own private device." His answers: a supervisor or the owner; a reason from a list with a note
// (required for Other); the history kept; an ONLINE machine uninstalls itself. And: "there should also
// be an uninstall option of the client app tray for someone who no longer wants to use the product."
//
// Against a real hub on a platform of its own:
//   * a technician may not remove; a supervisor/owner must give a reason, and a note for Other;
//   * removed: every console drops it, its AGENT is told to uninstall, its tray app only refused;
//     its ticket stays; the reason, note and who are recorded; the install is refused when it comes back;
//   * no monthly limit on the full version; an offline machine removed is refused on return, never told
//     to uninstall;
//   * the tray app's Uninstall: the machine leaves the list as "Uninstalled at the machine", by the person
//     signed in there; an AGENT saying the same is ignored;
//   * the portal: Remove beside each machine for the owner and not a technician, the page refusing a
//     technician, a reason required, the removal named on Devices and listed under Removed machines;
//   * ClientUninstall.Command copies the script and its branding out, and says there is none when not.
//
//   dotnet run -c Release --project tools/removecheck/removecheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class RemoveCheck
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private sealed class Peer : IAsyncDisposable
    {
        public WsConnection Ws = null!;
        public readonly List<Msg> Got = new();
        public volatile bool Closed;
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
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-removecheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var baseUrl = $"http://127.0.0.1:{port}";
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");

        // ---- 0. the uninstall command, on its own ----------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("== 0. the uninstaller is copied out with its branding ==");
        var fakeInstall = Path.Combine(dataDir, "install"); Directory.CreateDirectory(Path.Combine(fakeInstall, "branding"));
        var none = ClientUninstall.Command(fakeInstall, dataDir);
        Check("an install with no Uninstall-Client.ps1 (an old one): says so", none.script == null);
        File.WriteAllText(Path.Combine(fakeInstall, "Uninstall-Client.ps1"), "# probe");
        File.WriteAllText(Path.Combine(fakeInstall, "branding", "brand.json"), "{\"companyName\":\"Probe IT\"}");
        var (script, args) = ClientUninstall.Command(fakeInstall + "\\", dataDir);
        Check("with one: copied out of the folder it will delete, its branding beside it",
            script != null && !script.StartsWith(fakeInstall) && File.Exists(script) && File.Exists(Path.Combine(Path.GetDirectoryName(script)!, "branding", "brand.json")));
        Check("and told which folder to remove, without a trailing slash", args.Contains($"-InstallDir \"{fakeInstall}\"") && args.Contains("-ExecutionPolicy Bypass"), args);

        string key, slug;
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Full Company");
            key = created.Split('\n').First(l => l.StartsWith("Tenant key:"))["Tenant key:".Length..].Trim();
            slug = Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "boss@example.com", "FullOwner!2026", "Bo Boss", "--owner", "--technician");
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "tech@example.com", "FullTech!2026", "Ty Tech");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var bossTok = await Login(port, "boss@example.com", "FullOwner!2026");
            var techTok = await Login(port, "tech@example.com", "FullTech!2026");
            await using var boss = await Open($"ws://127.0.0.1:{port}/ws/tech?token={bossTok}");
            await using var tech = await Open($"ws://127.0.0.1:{port}/ws/tech?token={techTok}");
            Check("the owner's console may remove; a technician's may not", (await boss.WaitFor<StateMsg>(_ => true)) is { MayRemoveDevices: true }
                && (await tech.WaitFor<StateMsg>(_ => true)) is { MayRemoveDevices: false });

            // ---- 1. the console ---------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 1. Remove from the console ==");
            var pc = NewId();
            var agent = await Device(port, pc, DeviceRole.Agent, key, "OLD-LAPTOP", "HW-OLD");
            var tray = await Device(port, pc, DeviceRole.Client, key, "OLD-LAPTOP", "HW-OLD");
            await tray.WaitFor<UnattendedStateMsg>(_ => true);
            tray.Ws.Send(new SetUnattendedMsg { On = true });
            await tray.WaitFor<UnattendedStateMsg>(u => u.On);
            // A session, so the machine has a ticket — history that must outlive it.
            boss.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "moving the files over" });
            var start = await agent.WaitFor<StartSessionMsg>(_ => true);
            if (start != null) { boss.Ws.Send(new EndSessionMsg { SessionId = start.SessionId }); await Task.Delay(1500); }
            int ticketsBefore = Count(dataDir, $"SELECT COUNT(*) FROM Tickets WHERE DeviceId = '{pc.DeviceId}'");
            Check("(the machine has a ticket from a session)", ticketsBefore > 0, $"{ticketsBefore}");

            tech.Clear();
            tech.Ws.Send(new DeviceRemoveMsg { DeviceId = pc.DeviceId, Reason = "Replaced" });
            var techNo = await tech.WaitFor<ErrorMsg>(_ => true);
            Check("a technician is refused, in words", techNo != null && techNo.Message.Contains("supervisor or the owner"), techNo?.Message ?? "");
            foreach (var (reason, note, want) in new[] { ("", "", "Choose why"), ("Other", "", "Say why in the note"), ("Because", "", "Choose why") })
            {
                boss.Clear();
                boss.Ws.Send(new DeviceRemoveMsg { DeviceId = pc.DeviceId, Reason = reason, Note = note });
                var e = await boss.WaitFor<ErrorMsg>(_ => true);
                Check($"the owner with reason \"{reason}\"{(note.Length > 0 ? "" : ", no note")}: refused — {want}", e != null && e.Message.Contains(want), e?.Message ?? "");
            }
            Check("and nothing was removed by any of that", boss.Last<DeviceGoneMsg>() == null && !agent.Closed);

            boss.Clear(); tech.Clear();
            boss.Ws.Send(new DeviceRemoveMsg { DeviceId = pc.DeviceId, Reason = "Replaced", Note = "New Dell for Sue" });
            Check("Replaced, with a note: every console drops the row",
                await boss.WaitFor<DeviceGoneMsg>(g => g.DeviceId == pc.DeviceId) != null && await tech.WaitFor<DeviceGoneMsg>(g => g.DeviceId == pc.DeviceId) != null);
            var toAgent = await agent.WaitFor<ErrorMsg>(e => e.NotAdmitted);
            var toTray = await tray.WaitFor<ErrorMsg>(e => e.NotAdmitted);
            Check("its AGENT is told to uninstall itself — it is online", toAgent is { Uninstall: true });
            Check("its tray app is refused, not told to run a second uninstaller", toTray is { Uninstall: false });
            Check("its ticket is kept", Count(dataDir, $"SELECT COUNT(*) FROM Tickets WHERE DeviceId = '{pc.DeviceId}'") == ticketsBefore);
            Check("the removal is recorded: reason, note, who",
                Text(dataDir, $"SELECT Reason || '|' || Note || '|' || RemovedBy FROM DeviceRemovals WHERE DeviceId = '{pc.DeviceId}'") == "Replaced|New Dell for Sue|Bo Boss");
            await using (var back = await Device(port, pc, DeviceRole.Agent, key, "OLD-LAPTOP", "HW-OLD"))
            {
                var e = await back.WaitFor<ErrorMsg>(_ => true);
                Check("the removed install saying hello again is refused (full version too) — and not told to uninstall again",
                    e is { NotAdmitted: true, Uninstall: false } && e.Message.Contains("removed"), e?.Message ?? "let on");
            }

            // No monthly limit, and an offline machine.
            var ids = new[] { NewId(), NewId(), NewId() };
            for (int i = 0; i < ids.Length; i++) { var p = await Device(port, ids[i], DeviceRole.Agent, key, $"SPARE-{i}", $"HW-S{i}"); await Task.Delay(500); await p.DisposeAsync(); }
            await Task.Delay(800);
            int gone = 0;
            foreach (var id in ids)
            {
                boss.Clear();
                boss.Ws.Send(new DeviceRemoveMsg { DeviceId = id.DeviceId, Reason = "Upgraded" });
                if (await boss.WaitFor<DeviceGoneMsg>(g => g.DeviceId == id.DeviceId) != null) gone++;
            }
            Check("three more in the same month — no limit on the full version — offline, each removed", gone == 3, $"{gone} of 3");
            await using (var back = await Device(port, ids[0], DeviceRole.Agent, key, "SPARE-0", "HW-S0"))
            {
                var e = await back.WaitFor<ErrorMsg>(_ => true);
                Check("an offline machine removed is refused when it comes back, and stays installed (never told to uninstall)",
                    e is { NotAdmitted: true, Uninstall: false }, e?.Message ?? "let on");
            }

            // ---- 2. Uninstall, from the tray app ------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 2. Uninstall, from the tray app ==");
            var home = NewId();
            await using var homeAgent = await Device(port, home, DeviceRole.Agent, key, "HOME-PC", "HW-HOME");
            await using var homeTray = await Device(port, home, DeviceRole.Client, key, "HOME-PC", "HW-HOME");
            await homeTray.WaitFor<UnattendedStateMsg>(_ => true);
            boss.Clear();
            homeAgent.Ws.Send(new ClientUninstallingMsg());
            await Task.Delay(1200);
            Check("an AGENT saying it was uninstalled is ignored — only the tray app speaks for the person", boss.Last<DeviceGoneMsg>() == null);
            homeTray.Ws.Send(new ClientUninstallingMsg());
            Check("the tray app's Uninstall: every console drops it", await boss.WaitFor<DeviceGoneMsg>(g => g.DeviceId == home.DeviceId) != null);
            Check("recorded as \"Uninstalled at the machine\", by the person signed in there",
                Text(dataDir, $"SELECT Reason || '|' || RemovedBy FROM DeviceRemovals WHERE DeviceId = '{home.DeviceId}'") == "Uninstalled at the machine|probe at HOME-PC",
                Text(dataDir, $"SELECT Reason || '|' || RemovedBy FROM DeviceRemovals WHERE DeviceId = '{home.DeviceId}'"));
            var homeTold = await homeAgent.WaitFor<ErrorMsg>(e => e.NotAdmitted);
            Check("its agent is not told to run a second uninstaller — one is already running", homeTold is { Uninstall: false });

            // ---- 3. the portal ----------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 3. the portal ==");
            var web = NewId();
            await using var webAgent = await Device(port, web, DeviceRole.Agent, key, "PRIVATE-PC", "HW-PRIV");
            await Task.Delay(1000);
            var owner = await SignIn(baseUrl, "boss@example.com", "FullOwner!2026");
            var techWeb = await SignIn(baseUrl, "tech@example.com", "FullTech!2026");
            var (_, devices) = await Get(owner!, "/portal/devices");
            Check("Devices: Remove beside the machine, for the owner", devices.Contains($"/portal/removedevice?id={web.DeviceId}"));
            var (_, techDevices) = await Get(techWeb!, "/portal/devices");
            Check("and not for a technician", !techDevices.Contains("/portal/removedevice"));
            var (techAt, techPage) = await Get(techWeb!, $"/portal/removedevice?id={web.DeviceId}");
            Check("the Remove page refuses a technician", !techPage.Contains("id=\"removeHeading\""), techAt);
            Check("Devices lists the removed machines, with who and why", devices.Contains("id=\"removedMachines\"") && devices.Contains("New Dell for Sue") && devices.Contains("Uninstalled at the machine"));

            var (_, form) = await Get(owner!, $"/portal/removedevice?id={web.DeviceId}");
            Check("the Remove page says it is online and will uninstall itself", form.Contains("id=\"removeHeading\"") && form.Contains("uninstalls itself"));
            var noReason = await Post(owner!, $"/portal/removedevice?id={web.DeviceId}", form, ("Id", web.DeviceId), ("Reason", ""), ("Note", ""));
            Check("without a reason: refused, and nothing removed", noReason.html.Contains("id=\"removeError\"") && webAgent.Last<ErrorMsg>() == null);
            var done = await Post(owner!, $"/portal/removedevice?id={web.DeviceId}", form, ("Id", web.DeviceId), ("Reason", "Private device"), ("Note", "Sue's own laptop"));
            Check("Private device: back on Devices, which names it", done.at.StartsWith("/portal/devices") && done.html.Contains("Removed PRIVATE-PC."), done.at);
            Check("and its agent is told to uninstall", (await webAgent.WaitFor<ErrorMsg>(e => e.NotAdmitted)) is { Uninstall: true });
            Check("the reason and the note are on the Removed machines list", done.html.Contains("Private device") && done.html.Contains("Sue&#x27;s own laptop") || done.html.Contains("Sue's own laptop"));

            await agent.DisposeAsync(); await tray.DisposeAsync();
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

    private static int Count(string dataDir, string sql) => Convert.ToInt32(Scalar(dataDir, sql));
    private static string Text(string dataDir, string sql) => Convert.ToString(Scalar(dataDir, sql)) ?? "";
    private static object? Scalar(string dataDir, string sql)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable(sql);
        return cmd.ExecuteScalar();
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

    /// <summary>Posts the page's own form (its own token — §80), following the redirect as a browser would.</summary>
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
        _ = Task.Run(async () => { try { await peer.Ws.RunAsync(CancellationToken.None); } catch { } peer.Closed = true; });
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
