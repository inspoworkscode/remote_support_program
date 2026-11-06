using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// An ATTENDED session: who hosts it, what the technician sees while the customer decides, and
// what happens when they do not answer.
//
// Written 2026-10-02 for internal testing's report:
//   1. On an attended machine a UAC prompt is black, Task Manager will not open, DISM says
//      "elevation required" — the session was hosted by the tray app, as the person, because the
//      hub never offered the agent's SYSTEM host without unattended permission.
//   4. The technician is not shown that the customer is being asked.
//   5. When the question times out, a blank session window opens and, two minutes later, says
//      "Could not get back to the session after 124 seconds".
//
// Part 1 talks to a real hub with fake devices. Part 2 runs the REAL host (src/RemoteSupport.Host,
// Release) with --consent ask: its question appears on THIS screen for a few seconds and is
// either left to time out or withdrawn — nothing needs clicking.
//
// It starts a PLATFORM OF ITS OWN — its own port, its own empty database in the temp folder, its
// own tenant. The live server and Clint's real devices are not involved and are not modified.
//
//   dotnet run -c Release --project tools/consentwire/consentwire.csproj
//   dotnet run -c Release --project tools/consentwire/consentwire.csproj -- --no-host   (part 1 only)
// ---------------------------------------------------------------------------------------------

internal static class ConsentWire
{
    private static int _pass, _fail;
    private static string? _dataDir;

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

    private static async Task<int> Main(string[] args)
    {
        bool runHost = !args.Contains("--no-host");
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }

        var dataDir = Path.Combine(Path.GetTempPath(), "rs-consentwire-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        _dataDir = dataDir;
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server and its database are not touched)");

        string tenantKey, slug = "consent-test-co";
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Consent Test Co");
            var line = created.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:", StringComparison.Ordinal));
            if (line == null) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            tenantKey = line["Tenant key:".Length..].Trim();
            var m = System.Text.RegularExpressions.Regex.Match(created, @"\(slug ([^,\)]+)");
            if (m.Success) slug = m.Groups[1].Value.Trim();
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "consent@example.com", "ConsentProbe!2026", "Consent Probe");
        }
        catch (Exception ex) { Console.WriteLine("Could not start the platform: " + ex.Message); return 1; }

        using var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var token = await Login(port);
            if (token == null) { Console.WriteLine("Could not sign the test technician in."); return 1; }
            await using var tech = await Open($"ws://127.0.0.1:{port}/ws/tech?token={token}");
            await Task.Delay(600);

            // ---- 1. attended, with an agent that can ask --------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 1. an attended machine whose agent can ask ==");
            var pc = NewId();
            await using (var tray = await Device(port, pc, DeviceRole.Client, tenantKey, "CONSENT-PC"))
            await using (var agent = await Device(port, pc, DeviceRole.Agent, tenantKey, "CONSENT-PC", DeviceCan.AskConsent))
            {
                await Task.Delay(800);
                tech.Clear(); tray.Clear(); agent.Clear();
                tech.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "checking who hosts an attended session" });
                var start = await agent.WaitFor<StartSessionMsg>(_ => true);
                Check("the AGENT is asked to host it — the SYSTEM host, which can see a UAC prompt", start != null,
                      start == null ? "the agent got nothing; on the old hub the tray app hosted every attended session" : "");
                Check("and is told to ASK first", start is { AskConsent: true }, "no permission was granted, so somebody must say yes");
                await Task.Delay(400);
                Check("the tray app is NOT told a technician is connected yet", tray.Last<StartSessionMsg>() == null,
                      "\"can now see your screen\" before anybody said yes would be untrue");
                var r0 = await tech.WaitFor<SessionReadyMsg>(_ => true);
                Check("the console hears of the session at once", r0 != null);
                Check("as not under way, not unattended, and not run by the app",
                      r0 is { Session: { HostConnected: false, Unattended: false, HostedByApp: false } },
                      r0 == null ? "" : $"connected={r0.Session.HostConnected} unattended={r0.Session.Unattended} byApp={r0.Session.HostedByApp}");

                if (start != null)
                {
                    tech.Clear();
                    await using var host = await Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={start.SessionId}&token={start.HostToken}&consent=asking&secs=42");
                    var asking = await tech.WaitFor<SessionReadyMsg>(r => r.Session.AwaitingConsent);
                    Check("the console is told the question is on their screen (item 4)", asking != null,
                          "before this it heard nothing until they had answered");
                    Check("with how long it waits", asking is { Session.ConsentSeconds: 42 }, asking == null ? "" : $"{asking.Session.ConsentSeconds} s");
                    Check("and the session is still not \"connected\" — an older console waits as it always did",
                          asking is { Session.HostConnected: false });

                    // A viewer that joins early (an older console) is passed NOTHING to the host.
                    await using var viewer = await Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={start.SessionId}&token={token}");
                    await Task.Delay(400);
                    host.Clear();
                    viewer.Ws.Send(new QualityMsg { Jpeg = 33, Fps = 7 });
                    viewer.Ws.Send(new KeyMsg { Vk = 0x41, Down = true });
                    await Task.Delay(800);
                    Check("nothing a technician sends reaches a host that has not been allowed in",
                          host.Last<QualityMsg>() == null && host.Last<KeyMsg>() == null);

                    tech.Clear();
                    host.Ws.Send(new ConsentGrantedMsg { SessionId = start.SessionId });
                    var allowed = await tech.WaitFor<SessionReadyMsg>(r => r.Session.HostConnected && !r.Session.AwaitingConsent);
                    Check("when they say yes the console is told it is under way", allowed != null);
                    var told = await tray.WaitFor<StartSessionMsg>(_ => true);
                    Check("and only NOW is the tray app told a technician is connected", told is { HostToken: "" },
                          told == null ? "the tray app was never told" : "");
                    host.Clear();
                    viewer.Ws.Send(new QualityMsg { Jpeg = 34, Fps = 8 });
                    var q = await host.WaitFor<QualityMsg>(x => x.Jpeg == 34, 3000);
                    Check("and the technician's messages reach the host from then on", q != null);
                    tech.Ws.Send(new EndSessionMsg { SessionId = start.SessionId });
                    await Task.Delay(500);
                }
            }

            // ---- 2. an OLDER agent that never said it can ask ---------------------------------
            Console.WriteLine();
            Console.WriteLine("== 2. the same, with an older agent that never said it can ask ==");
            var pc2 = NewId();
            await using (var tray = await Device(port, pc2, DeviceRole.Client, tenantKey, "OLD-AGENT-PC"))
            await using (var agent = await Device(port, pc2, DeviceRole.Agent, tenantKey, "OLD-AGENT-PC"))
            {
                await Task.Delay(800);
                tech.Clear(); tray.Clear(); agent.Clear();
                tech.Ws.Send(new ConnectDeviceMsg { DeviceId = pc2.DeviceId, Reason = "an older agent" });
                var viaTray = await tray.WaitFor<StartSessionMsg>(s => s.HostToken.Length > 0);
                await Task.Delay(400);
                Check("it is NEVER handed an attended session — it would start one without asking",
                      agent.Last<StartSessionMsg>() == null);
                Check("the tray app hosts, as before", viaTray != null);
                var r = await tech.WaitFor<SessionReadyMsg>(_ => true);
                Check("and the console is told the app runs it, so it can say what that costs", r is { Session.HostedByApp: true });
                if (viaTray != null)
                {
                    tech.Ws.Send(new EndSessionMsg { SessionId = viaTray.SessionId });
                    await Task.Delay(500);
                    // Any session, however it ended: a window dialling in afterwards must be TOLD.
                    var late = await TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={viaTray.SessionId}&token={token}");
                    var ended = late.Peer == null ? null : await late.Peer.WaitFor<SessionEndedMsg>(_ => true, 4000);
                    Check("a console dialling into it after it ended is told so, with why (item 5)", ended?.Reason.StartsWith("Ended by") == true,
                          late.Peer == null ? $"refused: {late.Error}" : ended?.Reason ?? "nothing");
                    if (late.Peer != null) await late.Peer.DisposeAsync();
                }
            }

            // ---- 3. unattended permission granted: nothing changes ----------------------------
            Console.WriteLine();
            Console.WriteLine("== 3. unattended access allowed: unchanged ==");
            var pc3 = NewId();
            await using (var tray = await Device(port, pc3, DeviceRole.Client, tenantKey, "UNATTENDED-PC"))
            await using (var agent = await Device(port, pc3, DeviceRole.Agent, tenantKey, "UNATTENDED-PC", DeviceCan.AskConsent))
            {
                await Task.Delay(800);
                tray.Ws.Send(new SetUnattendedMsg { On = true });
                await tray.WaitFor<UnattendedStateMsg>(u => u.On);
                tech.Clear(); tray.Clear(); agent.Clear();
                tech.Ws.Send(new ConnectDeviceMsg { DeviceId = pc3.DeviceId, Reason = "unattended" });
                var start = await agent.WaitFor<StartSessionMsg>(_ => true);
                Check("the agent hosts it WITHOUT asking", start is { AskConsent: false });
                Check("the tray app is told at once, as before", await tray.WaitFor<StartSessionMsg>(s => s.HostToken == "") != null);
                Check("and the console sees it as unattended", (await tech.WaitFor<SessionReadyMsg>(_ => true)) is { Session.Unattended: true });
                if (start != null) { tech.Ws.Send(new EndSessionMsg { SessionId = start.SessionId }); await Task.Delay(500); }
            }

            // ---- 4. nobody answers ------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 4. nobody answers (item 5) ==");
            var pc4 = NewId();
            await using (var tray = await Device(port, pc4, DeviceRole.Client, tenantKey, "NOBODY-PC"))
            await using (var agent = await Device(port, pc4, DeviceRole.Agent, tenantKey, "NOBODY-PC", DeviceCan.AskConsent))
            {
                await Task.Delay(800);
                tech.Clear(); agent.Clear();
                tech.Ws.Send(new ConnectDeviceMsg { DeviceId = pc4.DeviceId, Reason = "nobody answers" });
                var start = await agent.WaitFor<StartSessionMsg>(_ => true);
                if (start == null) Check("the session started", false);
                else
                {
                    var why = "Nobody at NOBODY-PC answered within 5 seconds, so the connection was not allowed.";
                    await using (var host = await Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={start.SessionId}&token={start.HostToken}&consent=asking&secs=5"))
                    {
                        await tech.WaitFor<SessionReadyMsg>(r => r.Session.AwaitingConsent);
                        tech.Clear();
                        host.Ws.Send(new DeclineSessionMsg { SessionId = start.SessionId, Reason = why });
                        var over = await tech.WaitFor<SessionReadyMsg>(r => r.Session.Ended);
                        Check("the console is told the session is OVER, not merely \"no host yet\"", over != null,
                              "both used to be no host and no technicians");
                        Check("and why, in words", over?.Session.EndReason == why, over?.Session.EndReason ?? "");
                        Check("and it was never shown as under way, so no session window opens",
                              !tech.Got.OfType<SessionReadyMsg>().Any(r => r.Session.HostConnected),
                              "the old hub said \"host connected\" the moment the decline arrived");
                    }
                    // The window that would have opened, dialling in after the end.
                    var late = await TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={start.SessionId}&token={token}");
                    Check("a console dialling in afterwards is ANSWERED, not refused", late.Peer != null,
                          late.Peer == null ? $"{late.Error} — a reconnect loop reads that as \"not yet\" and dials for two minutes" : "");
                    if (late.Peer != null)
                    {
                        var ended = await late.Peer.WaitFor<SessionEndedMsg>(_ => true, 4000);
                        Check("with \"that session has ended\" and the real reason", ended?.Reason == why, ended?.Reason ?? "nothing");
                        await late.Peer.DisposeAsync();
                    }
                }
            }

            // ---- 5. the technician gives up waiting -------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 5. the technician cancels while they are being asked ==");
            var pc5 = NewId();
            await using (var tray = await Device(port, pc5, DeviceRole.Client, tenantKey, "CANCEL-PC"))
            await using (var agent = await Device(port, pc5, DeviceRole.Agent, tenantKey, "CANCEL-PC", DeviceCan.AskConsent))
            {
                await Task.Delay(800);
                tech.Clear(); agent.Clear();
                tech.Ws.Send(new ConnectDeviceMsg { DeviceId = pc5.DeviceId, Reason = "cancelled" });
                var start = await agent.WaitFor<StartSessionMsg>(_ => true);
                if (start != null)
                {
                    await using var host = await Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={start.SessionId}&token={start.HostToken}&consent=asking&secs=60");
                    await tech.WaitFor<SessionReadyMsg>(r => r.Session.AwaitingConsent);
                    tech.Ws.Send(new EndSessionMsg { SessionId = start.SessionId });   // the waiting window's Cancel
                    var told = await host.WaitFor<SessionEndedMsg>(_ => true, 4000);
                    Check("the host is told, which takes the question off their screen", told != null);
                    Check("and the console is told it is over", await tech.WaitFor<SessionReadyMsg>(r => r.Session.Ended) != null);
                }
                else Check("the session started", false);
            }

            // ---- 6. the REAL host ---------------------------------------------------------------
            if (runHost) await RealHost(root, port, tenantKey, tech);
            else { Console.WriteLine(); Console.WriteLine("(the real host was not run: --no-host)"); }

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

    /// <summary>
    /// The real RemoteSupport.Host.exe with --consent ask, against this hub. Its question is shown
    /// on this screen; it is left to time out (five seconds, set in a brand.json of its own) and
    /// then withdrawn by a cancel. Nobody has to click anything.
    /// </summary>
    private static async Task RealHost(string root, int port, string tenantKey, Peer tech)
    {
        Console.WriteLine();
        Console.WriteLine("== 6. the real host, asking (a question appears on this screen for a few seconds) ==");
        var exe = Path.Combine(root, "src", "RemoteSupport.Host", "bin", "Release", "net10.0-windows", "RemoteSupport.Host.exe");
        if (!File.Exists(exe)) { Check("the host is built", false, exe); return; }
        var work = Path.Combine(_dataDir!, "host");
        Directory.CreateDirectory(Path.Combine(work, "branding"));
        File.WriteAllText(Path.Combine(work, "branding", "brand.json"),
            "{ \"companyName\": \"Consent Test Co\", \"productName\": \"Consent probe\", \"consentTimeoutSeconds\": 5 }");

        var pc = NewId();
        await using var tray = await Device(port, pc, DeviceRole.Client, tenantKey, "REAL-HOST-PC");
        await using var agent = await Device(port, pc, DeviceRole.Agent, tenantKey, "REAL-HOST-PC", DeviceCan.AskConsent);
        await Task.Delay(800);

        // (a) left to time out
        tech.Clear(); agent.Clear();
        tech.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "the real host, timing out" });
        var start = await agent.WaitFor<StartSessionMsg>(_ => true);
        if (start == null) { Check("the session started", false); return; }
        using (var p = LaunchHost(exe, work, port, start))
        {
            var asking = await tech.WaitFor<SessionReadyMsg>(r => r.Session.AwaitingConsent, 15000);
            Check("the real host connects FIRST and says it is asking", asking != null);
            Check("with the prompt's own timeout", asking is { Session.ConsentSeconds: 5 }, asking == null ? "" : $"{asking.Session.ConsentSeconds} s");
            var over = await tech.WaitFor<SessionReadyMsg>(r => r.Session.Ended, 20000);
            Check("left alone, it declines by itself and says nobody answered", over?.Session.EndReason.Contains("answered within 5 seconds") == true,
                  over?.Session.EndReason ?? "no end arrived");
            Check("the console never saw it as under way", !tech.Got.OfType<SessionReadyMsg>().Any(r => r.Session.HostConnected));
            Check("and the host process went away", p.WaitForExit(8000));
        }

        // (b) withdrawn by the technician
        tech.Clear(); agent.Clear();
        tech.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "the real host, cancelled" });
        start = await agent.WaitFor<StartSessionMsg>(_ => true);
        if (start == null) { Check("the second session started", false); return; }
        using (var p = LaunchHost(exe, work, port, start))
        {
            var asking = await tech.WaitFor<SessionReadyMsg>(r => r.Session.AwaitingConsent, 15000);
            Check("asking again", asking != null);
            var t0 = DateTime.UtcNow;
            tech.Ws.Send(new EndSessionMsg { SessionId = start.SessionId });
            bool gone = p.WaitForExit(6000);
            Check("cancelled by the technician, the question is withdrawn and the host leaves at once", gone,
                  gone ? $"{(DateTime.UtcNow - t0).TotalSeconds:0.0} s" : "the prompt stayed up");
            var over = await tech.WaitFor<SessionReadyMsg>(r => r.Session.Ended, 4000);
            Check("as ended by the technician, not as declined by the customer",
                  over?.Session.EndReason.StartsWith("Ended by") == true, over?.Session.EndReason ?? "");
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
    }

    private static Process LaunchHost(string exe, string work, int port, StartSessionMsg s)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = work };
        foreach (var a in new[] { "--server", $"ws://127.0.0.1:{port}", "--session", s.SessionId, "--token", s.HostToken, "--consent", "ask", "--tech", "Consent Probe" })
            psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    // ------------------------------------------------------------------ plumbing

    private static DeviceIdentity NewId() => new() { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };

    private static async Task<Peer> Open(string url)
    {
        var r = await TryOpen(url);
        return r.Peer ?? throw new InvalidOperationException($"could not open {url}: {r.Error}");
    }

    private static async Task<(Peer? Peer, string Error)> TryOpen(string url)
    {
        try
        {
            var cws = new ClientWebSocket();
            await cws.ConnectAsync(new Uri(url), CancellationToken.None);
            var peer = new Peer { Ws = new WsConnection(cws) };
            peer.Ws.OnMessage += m => { lock (peer.Got) peer.Got.Add(m); };
            _ = peer.Ws.RunAsync(CancellationToken.None);
            return (peer, "");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    private static async Task<Peer> Device(int port, DeviceIdentity id, DeviceRole role, string tenantKey, string machine, params string[] can)
    {
        var peer = await Open($"ws://127.0.0.1:{port}/ws/device");
        peer.Ws.Send(new HelloMsg
        {
            Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
            UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey, Can = can.ToList()
        });
        return peer;
    }

    private static async Task<string?> Login(int port)
    {
        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login", new { email = "consent@example.com", password = "ConsentProbe!2026" });
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
        // Debug, deliberately: the Release output is usually locked by a running server.
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
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
        var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start(); int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
        return d?.FullName;
    }
}
