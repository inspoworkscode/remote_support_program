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
// A NEW MACHINE'S TWO HALVES SAY HELLO AT ONCE (2026-10-12).
//
// A first install starts the tray app and the agent together, under one device id. Each hello runs
// TenantHub.PersistDeviceAsync, which looks the row up and adds it when there is none — so both found
// none, both added one, and the second save broke the unique index on (TenantId, DeviceId): PostgreSQL
// 23505, seen in the load test of 2026-10-12. The losing hello then skipped everything after its save:
// what IT said about the machine was never stored (the tray app's user name, the agent's network cards),
// and its `finally` opened the wait for the stored record (WhenRecordReadAsync) before the winner had
// put that record into the live one — so a password set next was undone by the winner's older copy.
// The fix (TenantHub.RecordGate): one machine's hellos are stored in turn, and the wait opens after the last.
//
// Against a real hub on a platform of its own, many new machines at once, each machine's agent and tray
// app sending their hellos in the same instant; the tray app ticks unattended on its FIRST state and sets a
// password when told the tick is on, as the free installer does. Then:
//   - the platform logged no failed save of a device ("persist device"), and the race WAS hit (the fix
//     logs each hello that waits for its other half — without either, this run proved nothing);
//   - one row per machine, holding BOTH halves' details: the tray app's user name AND the agent's cards;
//   - the tick and the password stored, the tray app told so, and a console opened afterwards sees every
//     machine unattended with a password.
//
//   dotnet run -c Release --project tools/firsthello/firsthello.csproj [-- --machines 40]
//   (RSP_TEST_DB=postgres for PostgreSQL, where the load test saw it. Testing:PersistDeviceGapMs opens the gap
//   on SQLite, where a local file closes it too fast for the two hellos to meet.)
// ---------------------------------------------------------------------------------------------

internal static class FirstHello
{
    private const string Pw = "Unattended!2026";
    private static int _pass, _fail;
    private static readonly ConcurrentQueue<string> _log = new();

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

    private sealed class Machine
    {
        public int N;
        public DeviceIdentity Id = null!;
        public Peer Agent = null!, Tray = null!;
        public string User => $"sue{N}";
        public string Mac => $"00155D{N:X6}";
        public UnattendedStateMsg? Answer;
    }

    private static async Task<int> Main(string[] args)
    {
        int count = 40;
        var mi = Array.IndexOf(args, "--machines");
        if (mi >= 0 && mi + 1 < args.Length) count = int.Parse(args[mi + 1]);

        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-firsthello-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var wsUrl = $"ws://127.0.0.1:{port}";
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir} ({HarnessDb.Name})");

        string key;
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Hello Company");
            key = created.Split('\n').First(l => l.StartsWith("Tenant key:"))["Tenant key:".Length..].Trim();
            var slug = Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "boss@example.com", "HelloOwner!2026", "Bo Boss", "--owner", "--technician");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var token = await Login(port, "boss@example.com", "HelloOwner!2026");

            Console.WriteLine();
            Console.WriteLine($"== {count} new machines, each machine's agent and tray app saying hello in the same instant ==");
            var machines = Enumerable.Range(1, count).Select(n => new Machine { N = n, Id = NewId() }).ToList();
            // Every socket open first, so nothing but the hellos themselves is left to happen at once.
            foreach (var m in machines)
            {
                m.Agent = await Open($"{wsUrl}/ws/device");
                m.Tray = await Open($"{wsUrl}/ws/device");
            }
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = machines.Select(m => Task.Run(async () =>
            {
                await go.Task;
                // As the real two do: the agent knows the machine's network cards; the tray app is the person.
                m.Agent.Ws.Send(Hello(m, DeviceRole.Agent, key));
                m.Tray.Ws.Send(Hello(m, DeviceRole.Client, key));
                // As the free installer does (FreeSetup): answer the FIRST state it is told with the tick, and the
                // tick's answer with the password. The tick lands in the gap before the stored record is in; the
                // password, once that gap is (said to be) closed — and a second hello still holding an old copy of
                // the record must not undo it.
                if (await m.Tray.WaitFor<UnattendedStateMsg>(_ => true, 20000) == null) return;
                m.Tray.Ws.Send(new SetUnattendedMsg { On = true });
                if (await m.Tray.WaitFor<UnattendedStateMsg>(u => u.On, 20000) == null) return;
                m.Tray.Ws.Send(new SetUnattendedPasswordMsg { Password = Pw, RequestId = "pw" });
                m.Answer = await m.Tray.WaitFor<UnattendedStateMsg>(u => u.RequestId == "pw", 30000);
            })).ToList();
            go.SetResult();
            await Task.WhenAll(runs);
            await Task.Delay(3000);   // every detached save and every send after it

            var lines = _log.ToArray();
            int failedSaves = lines.Count(l => l.Contains("] persist device"));
            int duplicates = lines.Count(l => l.Contains("23505") || l.Contains("UNIQUE constraint failed"));
            int waited = lines.Count(l => l.Contains("waits for its other half"));
            Console.WriteLine($"  (the platform logged {failedSaves} failed device save(s), {duplicates} duplicate-key error line(s), "
                + $"{waited} hello(s) that waited for the other half)");
            Check("the race was hit — two hellos for one new machine at once (or this run proved nothing)", failedSaves + waited > 0,
                "try more machines: -- --machines 100");
            Check("no device save failed", failedSaves == 0,
                $"{failedSaves} failed: " + (lines.FirstOrDefault(l => l.Contains("23505") || l.Contains("UNIQUE constraint")) ?? "").Trim());

            // ---- the rows ---------------------------------------------------------------------------
            int rowsWrong = 0, userLost = 0, cardsLost = 0, notStored = 0;
            var lostWho = new List<string>();
            foreach (var m in machines)
            {
                var id = m.Id.DeviceId;
                if (Count(dataDir, $"SELECT COUNT(*) FROM Devices WHERE DeviceId = '{id}'") != 1) rowsWrong++;
                if (Text(dataDir, $"SELECT UserName FROM Devices WHERE DeviceId = '{id}'") != m.User) { userLost++; lostWho.Add($"{m.User} (tray app)"); }
                if (!Text(dataDir, $"SELECT NetworkCardsJson FROM Devices WHERE DeviceId = '{id}'").Contains(m.Mac)) { cardsLost++; lostWho.Add($"{m.User} (agent)"); }
                if (Count(dataDir, $"SELECT CAST(UnattendedEnabled AS INTEGER) FROM Devices WHERE DeviceId = '{id}'") != 1
                    || Count(dataDir, $"SELECT LENGTH(UnattendedPasswordHash) FROM Devices WHERE DeviceId = '{id}'") == 0) notStored++;
            }
            Check($"one row for each of the {count} machines", rowsWrong == 0, $"{rowsWrong} wrong");
            Check("every row holds what the TRAY APP said (its user name)", userLost == 0, $"{userLost} lost");
            Check("every row holds what the AGENT said (its network cards, for Wake-on-LAN)", cardsLost == 0, $"{cardsLost} lost");
            if (lostWho.Count > 0) Console.WriteLine("        lost: " + string.Join(", ", lostWho.Take(12)) + (lostWho.Count > 12 ? ", …" : ""));
            Check("every tick and password sent on the first state is stored", notStored == 0, $"{notStored} not stored");

            // ---- what is live ---------------------------------------------------------------------------
            int unanswered = machines.Count(m => m.Answer is not { On: true, HasPassword: true });
            Check("every tray app was answered: on, with a password", unanswered == 0, $"{unanswered} not: "
                + string.Join(", ", machines.Where(m => m.Answer is not { On: true, HasPassword: true }).Take(6)
                    .Select(m => m.Answer == null ? "no answer" : $"on={m.Answer.On} password={m.Answer.HasPassword}")));
            int toldLater = machines.Count(m => m.Tray.Last<UnattendedStateMsg>() is not { On: true, HasPassword: true });
            Check("and nothing told it otherwise afterwards (the last state it has is still on, with a password)", toldLater == 0, $"{toldLater} told otherwise");
            await using (var console = await Open($"{wsUrl}/ws/tech?token={token}"))
            {
                var state = await console.WaitFor<StateMsg>(_ => true);
                int notLive = machines.Count(m => state?.Devices.FirstOrDefault(d => d.DeviceId == m.Id.DeviceId) is not { Unattended: true, UnattendedPassword: true });
                Check("a console opened now sees every machine unattended, with a password", state != null && notLive == 0, $"{notLive} not");
            }

            foreach (var m in machines) { await m.Agent.DisposeAsync(); await m.Tray.DisposeAsync(); }
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

    private static HelloMsg Hello(Machine m, DeviceRole role, string tenantKey) => new()
    {
        Role = role, DeviceId = m.Id.DeviceId, DeviceKey = m.Id.DeviceKey, MachineName = $"NEW-PC-{m.N}",
        Os = "probe", Version = "1.0", TenantKey = tenantKey, HardwareId = $"HW-NEW-{m.N}",
        UserName = role == DeviceRole.Client ? m.User : "",
        Cards = role == DeviceRole.Agent ? new List<NetworkCard> { new() { Mac = m.Mac, Wired = true, Networks = { "192.168.1.10/24" } } } : null,
        Can = role == DeviceRole.Agent ? new List<string> { DeviceCan.AskConsent } : new List<string>()
    };

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

    // ------------------------------------------------------------------ plumbing (as reinstallcheck)

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
        // The platform's own log is the one place a failed device save shows (TenantHub logs and carries on).
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
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(proj);
        psi.Environment["RSP_Platform__DataDirectory"] = dataDir;
        HarnessDb.Use(psi.Environment, dataDir);
        psi.Environment["RSP_Security__RequireTwoFactor"] = "false";   // a throwaway platform (HANDOVER §72)
        psi.Environment["RSP_Platform__PublicUrl"] = $"http://127.0.0.1:{port}";
        psi.Environment["RSP_Urls"] = $"http://127.0.0.1:{port}";
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        // Holds the gap between looking the row up and saving it open: on a local SQLite file the two hellos
        // never meet in it otherwise, and on PostgreSQL it makes the meeting certain rather than likely.
        psi.Environment["RSP_Testing__PersistDeviceGapMs"] = "200";
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
