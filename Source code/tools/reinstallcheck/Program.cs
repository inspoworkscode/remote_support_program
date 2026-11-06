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
// A REINSTALLED MACHINE WAITS FOR THE OFFICE (2026-10-07, roadmap 4.4, HANDOVER §111).
//
// A machine that comes back with a new install is matched to its old row by its hardware fingerprint —
// the board's UUID and Windows' MachineGuid, which any program on it can read. With the company's
// public key, a stranger's PC could present them and take the row over, unattended permission and all.
// Clint's answer: the office confirms it. Until a supervisor or the owner does, unattended access waits,
// whatever the tick says — the impostor holds the tray app, so the tick proves nothing.
//
// Against a real hub on a platform of its own, with PersistDeviceDelayMs so the gap between a hello and
// its stored record being read is OPEN (on a local file it closes in a millisecond and hides the race):
//   1. a reinstall: the row kept, marked unconfirmed, the tick kept, unattended HELD BACK — a technician
//      connecting gets an ATTENDED session (the person is asked), no password asked; the tray app is told
//      AwaitingConfirm; ticking it off and on again changes nothing; a technician may not confirm, the
//      owner may; then unattended works again, the confirmation recorded;
//   2. it survives a server restart (the stored flag is read at start-up);
//   3. a tick and a password sent in the gap before the stored record is read are KEPT (the race found on
//      the way: they were made to a fresh record and overwritten);
//   4. the portal: the pill and Confirm for the owner, not a technician, a technician's post refused in
//      words, the owner's post confirming;
//   5. the free version: the installer told "awaiting confirmation" on a reinstall, the owner's console
//      refused in words, /portal/machines' "It is mine", then connecting works.
//
//   dotnet run -c Release --project tools/reinstallcheck/reinstallcheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class ReinstallCheck
{
    private const string Pw = "Unattended!2026";
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
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-reinstallcheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var baseUrl = $"http://127.0.0.1:{port}";
        var wsUrl = $"ws://127.0.0.1:{port}";
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");

        string key, slug, freeKey, freeSlug;
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Full Company");
            key = KeyOf(created); slug = SlugOf(created);
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "boss@example.com", "FullOwner!2026", "Bo Boss", "--owner", "--technician");
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "tech@example.com", "FullTech!2026", "Ty Tech");
            var free = await RunPlatform(proj, dataDir, port, "create-tenant", "Free Person");
            freeKey = KeyOf(free); freeSlug = SlugOf(free);
            await RunPlatform(proj, dataDir, port, "set-brand", freeSlug, "plan", "free");
            await RunPlatform(proj, dataDir, port, "add-tech", freeSlug, "me@example.com", "FreeOwner!2026", "Fran Free", "--owner", "--technician");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var bossTok = await Login(port, "boss@example.com", "FullOwner!2026");
            var techTok = await Login(port, "tech@example.com", "FullTech!2026");
            var boss = await Open($"{wsUrl}/ws/tech?token={bossTok}");
            var tech = await Open($"{wsUrl}/ws/tech?token={techTok}");
            Check("the owner's console may confirm a reinstall; a technician's may not",
                (await boss.WaitFor<StateMsg>(_ => true)) is { MayConfirmReinstall: true }
                && (await tech.WaitFor<StateMsg>(_ => true)) is { MayConfirmReinstall: false });

            // ---- 1. a reinstall ----------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 1. a reinstall waits for the office ==");
            var first = NewId();
            var agent = await Device(port, first, DeviceRole.Agent, key, "SUE-PC", "HW-SUE");
            var tray = await Device(port, first, DeviceRole.Client, key, "SUE-PC", "HW-SUE");
            await tray.WaitFor<UnattendedStateMsg>(_ => true);
            tray.Ws.Send(new SetUnattendedMsg { On = true });
            await tray.WaitFor<UnattendedStateMsg>(u => u.On);
            tray.Ws.Send(new SetUnattendedPasswordMsg { Password = Pw, RequestId = "p1" });
            await tray.WaitFor<UnattendedStateMsg>(u => u.RequestId == "p1");
            var s0 = await ConnectAndEnd(boss, agent, first.DeviceId, Pw);
            Check("(before: the owner's connection is UNATTENDED — the agent hosts it without asking)", s0 is { AskConsent: false }, s0 == null ? "no session" : "asked");
            await agent.DisposeAsync(); await tray.DisposeAsync();
            await Task.Delay(800);

            var again = NewId();
            boss.Clear();
            agent = await Device(port, again, DeviceRole.Agent, key, "SUE-PC", "HW-SUE");
            tray = await Device(port, again, DeviceRole.Client, key, "SUE-PC", "HW-SUE");
            var seen = await boss.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == again.DeviceId && u.Device.ReinstallUnconfirmed);
            Check("the console sees it marked \"reinstalled, not confirmed\"", seen != null);
            Check("its tick is kept, but it is NOT unattended", seen is { Device.UnattendedEnabled: true, Device.Unattended: false },
                seen == null ? "" : $"enabled {seen.Device.UnattendedEnabled}, unattended {seen.Device.Unattended}");
            Check("one row for the hardware, taken over (not a second machine), flagged in the database",
                Count(dataDir, "SELECT COUNT(*) FROM Devices WHERE HardwareId = 'HW-SUE'") == 1
                && Count(dataDir, $"SELECT ReinstallUnconfirmed FROM Devices WHERE DeviceId = '{again.DeviceId}'") == 1);
            Check("the tray app is told it waits for the IT company",
                await tray.WaitFor<UnattendedStateMsg>(u => u.On && u.AwaitingConfirm) != null);

            var s1 = await ConnectAndEnd(boss, agent, again.DeviceId, "");
            Check("the owner connecting now gets an ATTENDED session — the person is asked — and no password is wanted",
                s1 is { AskConsent: true }, s1 == null ? (boss.Last<ErrorMsg>()?.Message ?? "no session") : "unattended!");

            tray.Ws.Send(new SetUnattendedMsg { On = false });
            await tray.WaitFor<UnattendedStateMsg>(u => !u.On);
            boss.Clear();
            tray.Ws.Send(new SetUnattendedMsg { On = true });
            var reTicked = await boss.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == again.DeviceId && u.Device.UnattendedEnabled);
            Check("the person there ticking it off and on again does NOT let it through — the tray app is no proof",
                reTicked is { Device.Unattended: false, Device.ReinstallUnconfirmed: true });

            tech.Clear();
            tech.Ws.Send(new ConfirmReinstallMsg { DeviceId = again.DeviceId });
            var techNo = await tech.WaitFor<ErrorMsg>(_ => true);
            Check("a technician confirming is refused, in words", techNo != null && techNo.Message.Contains("supervisor or the owner"), techNo?.Message ?? "");
            Check("and it is still unconfirmed", Count(dataDir, $"SELECT ReinstallUnconfirmed FROM Devices WHERE DeviceId = '{again.DeviceId}'") == 1);

            boss.Clear(); tech.Clear(); tray.Clear();
            boss.Ws.Send(new ConfirmReinstallMsg { DeviceId = again.DeviceId });
            var ok = await tech.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == again.DeviceId && !u.Device.ReinstallUnconfirmed);
            Check("the owner confirms: every console sees it unattended again", ok is { Device.Unattended: true });
            Check("the tray app is told it no longer waits", await tray.WaitFor<UnattendedStateMsg>(u => u.On && !u.AwaitingConfirm) != null);
            Check("the confirmation is recorded: who, and cleared",
                Text(dataDir, $"SELECT CAST(ReinstallUnconfirmed AS INTEGER) || '|' || ReinstallConfirmedBy FROM Devices WHERE DeviceId = '{again.DeviceId}'") == "0|Bo Boss"
                && Count(dataDir, $"SELECT COUNT(*) FROM Devices WHERE DeviceId = '{again.DeviceId}' AND ReinstallConfirmedUtc IS NOT NULL") == 1);
            var s2 = await ConnectAndEnd(boss, agent, again.DeviceId, Pw);
            Check("and the owner's connection is unattended again", s2 is { AskConsent: false });

            await agent.DisposeAsync(); await tray.DisposeAsync();
            await Task.Delay(600);
            boss.Clear();
            agent = await Device(port, again, DeviceRole.Agent, key, "SUE-PC", "HW-SUE");
            var plain = await boss.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == again.DeviceId && u.Device.AgentOnline, 5000);
            await Task.Delay(2000);   // past the persist delay
            Check("the same install simply reconnecting is not a reinstall (nothing to confirm)",
                plain != null && Count(dataDir, $"SELECT ReinstallUnconfirmed FROM Devices WHERE DeviceId = '{again.DeviceId}'") == 0
                && Count(dataDir, $"SELECT ReinstallCount FROM Devices WHERE DeviceId = '{again.DeviceId}'") == 1);
            await agent.DisposeAsync();

            // ---- 2. a server restart ------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 2. it survives a server restart ==");
            var bob1 = NewId();
            await using (var a = await Device(port, bob1, DeviceRole.Agent, key, "BOB-PC", "HW-BOB")) await Task.Delay(2200);
            var bob2 = NewId();
            await using (var a = await Device(port, bob2, DeviceRole.Agent, key, "BOB-PC", "HW-BOB")) await Task.Delay(2200);
            Check("(a second machine reinstalled)", Count(dataDir, $"SELECT ReinstallUnconfirmed FROM Devices WHERE DeviceId = '{bob2.DeviceId}'") == 1);
            await boss.DisposeAsync(); await tech.DisposeAsync();
            try { server.Kill(true); } catch { }
            await Task.Delay(1500);
            server = StartPlatform(proj, dataDir, port);
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never came back."); return 1; }
            bossTok = await Login(port, "boss@example.com", "FullOwner!2026");
            techTok = await Login(port, "tech@example.com", "FullTech!2026");
            boss = await Open($"{wsUrl}/ws/tech?token={bossTok}");
            tech = await Open($"{wsUrl}/ws/tech?token={techTok}");
            var after = await boss.WaitFor<StateMsg>(_ => true);
            Check("after a restart the console's list still says BOB-PC waits for confirmation",
                after?.Devices.FirstOrDefault(d => d.DeviceId == bob2.DeviceId) is { ReinstallUnconfirmed: true });

            // ---- 3. the gap before the stored record is read ---------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 3. a tick and a password sent before the record is read are kept ==");
            var fresh = NewId();
            var freshTray = await Device(port, fresh, DeviceRole.Client, key, "NEW-PC", "HW-NEW");
            // As the tray app and the free installer do: on the FIRST state, which is sent from memory as the
            // connection is registered — still inside the 1.5 s the platform takes to read the stored record.
            var firstState = await freshTray.WaitFor<UnattendedStateMsg>(_ => true);
            Check("(the first state arrives before the stored record is read — the gap is open)",
                firstState != null && Count(dataDir, $"SELECT COUNT(*) FROM Devices WHERE DeviceId = '{fresh.DeviceId}'") == 0);
            freshTray.Ws.Send(new SetUnattendedMsg { On = true });
            freshTray.Ws.Send(new SetUnattendedPasswordMsg { Password = Pw, RequestId = "early" });
            var early = await freshTray.WaitFor<UnattendedStateMsg>(u => u.RequestId == "early", 15000);
            await Task.Delay(800);
            Check("the answer says on, with a password", early is { On: true, HasPassword: true });
            Check("and BOTH are stored — not lost to the record read after them",
                Count(dataDir, $"SELECT UnattendedEnabled FROM Devices WHERE DeviceId = '{fresh.DeviceId}'") == 1
                && Count(dataDir, $"SELECT LENGTH(UnattendedPasswordHash) FROM Devices WHERE DeviceId = '{fresh.DeviceId}'") > 0,
                Text(dataDir, $"SELECT CAST(UnattendedEnabled AS INTEGER) || '|' || LENGTH(UnattendedPasswordHash) FROM Devices WHERE DeviceId = '{fresh.DeviceId}'"));
            var later = await freshTray.WaitFor<UnattendedStateMsg>(_ => true);
            Check("and the live record agrees (the last state told is still on, with a password)", later is { On: true, HasPassword: true });
            await freshTray.DisposeAsync();

            // ---- 4. the portal --------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 4. the portal ==");
            var owner = await SignIn(baseUrl, "boss@example.com", "FullOwner!2026");
            var techWeb = await SignIn(baseUrl, "tech@example.com", "FullTech!2026");
            var (_, devices) = await Get(owner!, "/portal/devices");
            Check("Devices: BOB-PC is marked \"reinstalled — not confirmed\"", devices.Contains("reinstall-unconfirmed") && devices.Contains("reinstalled — not confirmed"));
            Check("with Confirm for the owner", devices.Contains("handler=ConfirmReinstall") && devices.Contains($"value=\"{bob2.DeviceId}\""));
            var (_, techDevices) = await Get(techWeb!, "/portal/devices");
            Check("and not for a technician", techDevices.Contains("reinstall-unconfirmed") && !techDevices.Contains("handler=ConfirmReinstall"));
            var techPost = await Post(techWeb!, "/portal/devices?handler=ConfirmReinstall", techDevices, ("deviceId", bob2.DeviceId));
            Check("a technician's post is refused, in words on the page", techPost.html.Contains("id=\"devicesError\"") && techPost.html.Contains("supervisor or the owner"));
            var ownerPost = await Post(owner!, "/portal/devices?handler=ConfirmReinstall", devices, ("deviceId", bob2.DeviceId));
            Check("the owner's post confirms it, and says so", ownerPost.html.Contains("BOB-PC confirmed as the same machine"));
            Check("stored, and every console told", Count(dataDir, $"SELECT ReinstallUnconfirmed FROM Devices WHERE DeviceId = '{bob2.DeviceId}'") == 0
                && await tech.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == bob2.DeviceId && !u.Device.ReinstallUnconfirmed) != null);

            // ---- 5. the free version -------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 5. the free version ==");
            var homeA = NewId();
            bool waits = true;
            var set1 = await FreeSetup.SetPasswordAsync(wsUrl, freeKey, homeA, Pw, TimeSpan.FromSeconds(20), hardwareId: "HW-HOME", awaitingConfirm: w => waits = w);
            Check("a first free install: done, nothing to confirm", set1 == "" && !waits, set1);
            await Task.Delay(500);
            var homeB = NewId();
            waits = false;
            var set2 = await FreeSetup.SetPasswordAsync(wsUrl, freeKey, homeB, Pw, TimeSpan.FromSeconds(20), hardwareId: "HW-HOME", awaitingConfirm: w => waits = w);
            Check("installed again: done, and the installer is told it waits for the owner", set2 == "" && waits, set2);
            await using var homeAgent = await Device(port, homeB, DeviceRole.Agent, freeKey, "HOME-PC", "HW-HOME");
            var meTok = await Login(port, "me@example.com", "FreeOwner!2026");
            await using var me = await Open($"{wsUrl}/ws/tech?token={meTok}");
            await me.WaitFor<StateMsg>(_ => true);
            await Task.Delay(1800);
            me.Clear();
            me.Ws.Send(new ConnectDeviceMsg { DeviceId = homeB.DeviceId, Password = Pw });
            var meNo = await me.WaitFor<ErrorMsg>(_ => true);
            Check("the free owner's console is refused in words until they confirm it", meNo != null && meNo.Message.Contains("reinstalled") && homeAgent.Last<StartSessionMsg>() == null,
                meNo?.Message ?? "no answer");
            var freeWeb = await SignIn(baseUrl, "me@example.com", "FreeOwner!2026");
            var (_, machines) = await Get(freeWeb!, "/portal/machines");
            Check("Your computers says it was installed again, with \"It is mine\"", machines.Contains("installed again — confirm it") && machines.Contains("handler=ConfirmReinstall"));
            var mine = await Post(freeWeb!, "/portal/machines?handler=ConfirmReinstall", machines, ("deviceId", homeB.DeviceId));
            Check("\"It is mine\": back on the page, which says so", mine.at.Contains("confirmed=") && mine.html.Contains("HOME-PC confirmed as the same computer"), mine.at);
            me.Clear();
            me.Ws.Send(new ConnectDeviceMsg { DeviceId = homeB.DeviceId, Password = Pw });
            var freeStart = await homeAgent.WaitFor<StartSessionMsg>(_ => true);
            Check("and now the free owner connects, unattended", freeStart is { AskConsent: false }, me.Last<ErrorMsg>()?.Message ?? "");

            await boss.DisposeAsync(); await tech.DisposeAsync();
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

    /// <summary>Connects as this console, takes the session the agent is handed, and ends it.</summary>
    private static async Task<StartSessionMsg?> ConnectAndEnd(Peer console, Peer agent, string deviceId, string password)
    {
        agent.Clear(); console.Clear();
        console.Ws.Send(new ConnectDeviceMsg { DeviceId = deviceId, Reason = "checking the reinstall", Password = password });
        var start = await agent.WaitFor<StartSessionMsg>(_ => true);
        if (start != null) { console.Ws.Send(new EndSessionMsg { SessionId = start.SessionId }); await Task.Delay(1200); }
        return start;
    }

    private static string KeyOf(string created) => created.Split('\n').First(l => l.StartsWith("Tenant key:"))["Tenant key:".Length..].Trim();
    private static string SlugOf(string created) => Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();

    // ------------------------------------------------------------------ the database, read directly

    private static int Count(string dataDir, string sql) => Convert.ToInt32(Scalar(dataDir, sql) ?? 0);
    private static string Text(string dataDir, string sql) => Convert.ToString(Scalar(dataDir, sql)) ?? "";
    private static object? Scalar(string dataDir, string sql)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable(sql);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
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

    /// <summary>Posts with the token of the Confirm form itself (§80: the form it posts, never another's).</summary>
    private static async Task<(string at, string html)> Post(HttpClient hc, string path, string fromHtml, params (string k, string v)[] fields)
    {
        var forms = Regex.Matches(fromHtml, "<form method=\"post\"[^>]*>(.*?)</form>", RegexOptions.Singleline).Select(m => m.Value).ToList();
        var form = forms.FirstOrDefault(f => f.Contains("ConfirmReinstall")) ?? forms.FirstOrDefault() ?? fromHtml;
        var all = fields.Select(f => new KeyValuePair<string, string>(f.k, f.v)).Append(new("__RequestVerificationToken", Token(form)));
        var r = await hc.PostAsync(path, new FormUrlEncodedContent(all));
        return (r.RequestMessage!.RequestUri!.PathAndQuery, WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()));
    }

    // ------------------------------------------------------------------ plumbing (as removecheck)

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
        // Holds the stored record back, so the gap §3 is about is open as it is on a real database (§50).
        psi.Environment["RSP_Testing__PersistDeviceDelayMs"] = "1500";
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
