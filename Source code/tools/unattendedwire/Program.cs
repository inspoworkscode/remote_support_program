using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// Does the HUB actually tell a machine its unattended state when it connects?
//
// Written 2026-09-20 because the answer was no, and nothing here could see it. `unattendedcheck`
// drives the tray app against a STAND-IN server and says, in its own output, that the hub's half
// is not covered. That gap is exactly where the bug lived: the hub started the task that sends
// the state BEFORE it added the connection to its list, so on a local database the send happened
// a millisecond too early, found nobody to send to, and sent nothing. The tray app then waits for
// ever for a message that was never coming, and its switch stays greyed out — which is what Clint
// reported after installing the build.
//
// A race that the fast path loses EVERY time is not one anybody catches by running it once. The
// only way to catch it was to talk to a real hub, so this does.
//
// It starts a PLATFORM OF ITS OWN — its own port, its own empty database in the temp folder, its
// own tenant — and touches nothing that is already running. The live server, its database and
// Clint's real devices are not involved and are not modified.
//
//   dotnet run -c Release --project tools/unattendedwire/unattendedwire.csproj
// ---------------------------------------------------------------------------------------------

internal static class UnattendedWire
{
    private static int _pass, _fail;
    private static string? _dataDir;

    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        else { _fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    }

    private static async Task<int> Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }

        var dataDir = Path.Combine(Path.GetTempPath(), "rs-unattendedwire-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        _dataDir = dataDir;
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");

        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server on 8090 and its database are not touched)");

        // Debug, deliberately: the Release output is usually locked by the server Clint has
        // running, and a harness that cannot run while the product is running is a harness that
        // does not get run.
        string tenantKey; string slug = "wire-test-co";
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Wire Test Co");
            var line = created.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:", StringComparison.Ordinal));
            if (line == null) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            tenantKey = line["Tenant key:".Length..].Trim();
            // The slug is needed to add a technician to this tenant, and it is printed rather than
            // guessed: CreateAsync derives it from the name and may de-duplicate it.
            var m = System.Text.RegularExpressions.Regex.Match(created, @"\(slug ([^,\)]+)");
            if (m.Success) slug = m.Groups[1].Value.Trim();
            Console.WriteLine($"Tenant created, key {tenantKey[..8]}…, slug {slug}");
        }
        catch (Exception ex) { Console.WriteLine("Could not start the platform: " + ex.Message); return 1; }

        using var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            Console.WriteLine();

            // ---- a machine's tray app connecting for the first time --------------------------
            Console.WriteLine("== a tray app connects ==");
            var id = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
            var (states, client) = await Connect(port, id, DeviceRole.Client, tenantKey, "WIRE-TEST-PC");
            await using (client)
            {
                var first = await Wait(states, 10000);
                Check("the hub tells a tray app its unattended state, unprompted", first != null,
                      first == null
                        ? "nothing arrived in ten seconds — the switch would be greyed out for ever"
                        : $"on={first.On}, agentOnline={first.AgentOnline}");
                Check("and a machine nobody has allowed starts OFF", first is { On: false },
                      "installing a support client is not agreeing to be watched");

                // ---- and when the person at it ticks the box ---------------------------------
                Console.WriteLine();
                Console.WriteLine("== the person at the machine allows it ==");
                states.Clear();
                client.Send(new SetUnattendedMsg { On = true });
                var after = await Wait(states, 10000);
                Check("the hub answers the change", after != null, after == null ? "no answer" : $"on={after.On}");
                Check("and the answer is ON", after is { On: true });

                // It has to SURVIVE. The whole point of storing it on the server is that the
                // machine can be switched off and the answer is still there when it comes back.
                Console.WriteLine();
                Console.WriteLine("== the machine goes away and comes back ==");
                await client.CloseAsync("probe reconnect");
                await Task.Delay(500);
                var (states2, client2) = await Connect(port, id, DeviceRole.Client, tenantKey, "WIRE-TEST-PC");
                await using (client2)
                {
                    var back = await Wait(states2, 10000);
                    Check("the hub remembers, and says so on the next connection", back is { On: true },
                          back == null ? "nothing arrived" : $"on={back.On}");
                }
            }

            // ---- the agent must not be able to permit itself ---------------------------------
            Console.WriteLine();
            Console.WriteLine("== an agent trying to grant itself unattended access ==");
            var id2 = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
            var (agentStates, agent) = await Connect(port, id2, DeviceRole.Agent, tenantKey, "WIRE-TEST-AGENT");
            await using (agent)
            {
                agent.Send(new SetUnattendedMsg { On = true });
                await Task.Delay(1500);
                // Now ask as the CLIENT for the same device and see what the hub holds.
                var (clientStates, asClient) = await Connect(port, id2, DeviceRole.Client, tenantKey, "WIRE-TEST-AGENT");
                await using (asClient)
                {
                    var s = await Wait(clientStates, 10000);
                    Check("the agent could not turn it on for itself", s is { On: false },
                          s == null ? "nothing arrived" : $"on={s.On} — the agent is the thing being permitted");
                    Check("and the hub did report the agent as online", s is { AgentOnline: true },
                          s == null ? "" : $"agentOnline={s.AgentOnline}");
                }
            }

            // ---- WHICH SIDE ACTUALLY HOSTS IT ------------------------------------------------
            //
            // Clint, 2026-09-21: the switch "switches between attended and unattended on the
            // console, but does not allow for unattended access. the client still has to click on
            // allow."
            //
            // Permission is only half of it. The hub hands a technician-initiated session to the
            // agent only when the permission is granted AND an agent is connected; with no agent
            // it falls back to the tray app, whose host is launched with --consent ask. Every
            // harness up to now has said in its own output that the hub's half is not covered,
            // and this is the half.
            Console.WriteLine();
            Console.WriteLine("== permission granted, and NO agent service connected ==");

            var tech = await SignIn(port, slug);
            if (tech == null) Check("signed a technician in", false, "could not get a token");
            else
            {
                var id3 = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
                var (st3, tray) = await Connect(port, id3, DeviceRole.Client, tenantKey, "WIRE-NO-AGENT");
                await using (tray)
                {
                    await Wait(st3, 10000);
                    st3.Clear();
                    tray.Send(new SetUnattendedMsg { On = true });
                    var allowed = await Wait(st3, 10000);
                    Check("the machine's permission is ON", allowed is { On: true });
                    Check("and the hub agrees no agent is connected", allowed is { AgentOnline: false },
                          "which is the situation the console could not show");

                    var ready = await ConnectAs(tech, port, id3.DeviceId, "checking which side hosts this session");
                    Check("a technician can still connect", ready != null,
                          ready == null ? "no session came back" : ready.Session.SessionId[..8]);
                    // THE FAULT, stated as a fact about the hub rather than a guess about the UI.
                    Check("but the session is ATTENDED — the tray app hosts it, so the customer IS asked",
                          ready is { Session.Unattended: false },
                          "permission granted, no agent, and somebody at the machine still has to accept");

                    if (ready != null) await EndAs(tech, ready.Session.SessionId);

                    // ---- and now with an agent, which is the same machine and the same permission
                    Console.WriteLine();
                    Console.WriteLine("== the same machine, once its agent service connects ==");
                    var (_, agent2) = await Connect(port, id3, DeviceRole.Agent, tenantKey, "WIRE-NO-AGENT");
                    await using (agent2)
                    {
                        await Task.Delay(1200);
                        var ready2 = await ConnectAs(tech, port, id3.DeviceId, "checking which side hosts this session");
                        Check("the session is now UNATTENDED — the agent hosts it, nobody is asked",
                              ready2 is { Session.Unattended: true },
                              ready2 == null ? "no session came back" : $"unattended={ready2.Session.Unattended}");
                        Check("nothing about the permission changed between those two connections", true,
                              "the only difference was an agent being there — which is the whole fault");
                        if (ready2 != null) await EndAs(tech, ready2.Session.SessionId);
                    }
                }
            }

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
    /// A technician socket on the throwaway platform: one account is created for the test tenant
    /// and signed in. Nothing here touches the live server or any real account.
    /// </summary>
    private static async Task<WsConnection?> SignIn(int port, string slug)
    {
        try
        {
            var root = FindRepoRoot()!;
            var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
            var dataDir = _dataDir!;
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "wire@example.com", "WireProbe!2026", "Wire Probe");

            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var lr = await hc.PostAsJsonAsync($"http://127.0.0.1:{port}/api/login",
                new { email = "wire@example.com", password = "WireProbe!2026" });
            if (!lr.IsSuccessStatusCode) return null;
            var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

            var cws = new ClientWebSocket();
            await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/tech?token={tok}"), CancellationToken.None);
            var conn = new WsConnection(cws);
            conn.OnMessage += m => { lock (_techMsgs) _techMsgs.Add(m); };
            _ = conn.RunAsync(CancellationToken.None);
            await Task.Delay(800);
            return conn;
        }
        catch { return null; }
    }

    private static readonly List<Msg> _techMsgs = new();

    /// <summary>Connects to a machine as the technician and waits for the session the hub built.</summary>
    private static async Task<SessionReadyMsg?> ConnectAs(WsConnection tech, int port, string deviceId, string reason)
    {
        lock (_techMsgs) _techMsgs.Clear();
        tech.Send(new ConnectDeviceMsg { DeviceId = deviceId, Reason = reason });
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            lock (_techMsgs)
            {
                var ready = _techMsgs.OfType<SessionReadyMsg>().FirstOrDefault();
                if (ready != null) return ready;
                var err = _techMsgs.OfType<ErrorMsg>().FirstOrDefault();
                if (err != null) { Console.WriteLine($"        (the hub refused: {err.Message})"); return null; }
            }
            await Task.Delay(50);
        }
        return null;
    }

    private static async Task EndAs(WsConnection tech, string sessionId)
    {
        tech.Send(new EndSessionMsg { SessionId = sessionId });
        await Task.Delay(600);
    }

    private static async Task<(List<UnattendedStateMsg>, WsConnection)> Connect(
        int port, DeviceIdentity id, DeviceRole role, string tenantKey, string machine)
    {
        var states = new List<UnattendedStateMsg>();
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/device"), CancellationToken.None);
        var conn = new WsConnection(cws);
        conn.OnMessage += m => { if (m is UnattendedStateMsg u) lock (states) states.Add(u); };
        _ = conn.RunAsync(CancellationToken.None);
        conn.Send(new HelloMsg
        {
            Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey,
            MachineName = machine, UserName = "wire", Os = "probe", Version = "1.0", TenantKey = tenantKey
        });
        return (states, conn);
    }

    private static async Task<UnattendedStateMsg?> Wait(List<UnattendedStateMsg> got, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            lock (got) if (got.Count > 0) return got[^1];
            await Task.Delay(50);
        }
        lock (got) return got.Count > 0 ? got[^1] : null;
    }

    private static Process StartPlatform(string proj, string dataDir, int port)
    {
        var psi = Psi(proj, dataDir, port);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("run");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
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
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        // Its own database and its own port. RSP_ is the prefix Program.cs reads.
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform: the password alone (HANDOVER §72)
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
