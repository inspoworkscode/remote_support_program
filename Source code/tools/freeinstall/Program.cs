using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// SETTING UP A FREE COMPUTER (2026-10-06, docs/NEXT-SESSION-FREE.md Stage D), against a real hub.
//
// Clint's answers: the unattended password is chosen at install, unattended access is switched on
// then, no tray app stays behind, and changing the password is installing again. The downloaded copy
// does three things as the person at the machine: asks whether this computer may be added (before
// installing anything), installs the agent service, and connects ONCE as this machine's client to set
// the permission and the password — only a client connection may (§31, §77).
//
// This drives the REAL FreeSetup code (Shared) — the installer between its steps needs an
// administrator and a real service, which is check 469 on hardware, not this.
//
//   * brand.json says a free company is free, and a full one is not;
//   * may-add: yes on an empty account, for this computer when it is one of the two, and always on a
//     full company; no — with the hub's words — for a third; FreeSetup.MayAddAsync asks with THIS PC's
//     fingerprint;
//   * the password: set with unattended switched on, answered by the server for THIS request (a
//     machine is told its state twice as it connects), and a technician then connects with it;
//   * a reinstall choosing a new password: the old stops working, the new works, and the answer was
//     not the old "has a password";
//   * a password the server's rule refuses is refused before anything is sent;
//   * a third computer setting a password is refused with the hub's words.
//
// Since 2026-10-25 (HANDOVER §163) the company here is on the BASIC version (three computers), whose installer may leave the
// password blank: FreeSetup.SetAttendedOnlyAsync switches unattended access off and a technician is then let in only with the
// person asked. A second company on the FREE version: one computer, and its brand.json says it is not Basic.
//
//   dotnet run -c Release --project tools/freeinstall/freeinstall.csproj
// ---------------------------------------------------------------------------------------------

internal static class FreeInstall
{
    private static int _pass, _fail;
    private const string Pw = "Branch-Office-2026", Pw2 = "Second-Password-77";

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
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-freeinstall-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");

        string freeKey, freeSlug, fullSlug, freeOnlyKey, freeOnlySlug;
        try
        {
            (freeKey, freeSlug) = await CreateTenant(proj, dataDir, port, "Branch Offices Free");
            (_, fullSlug) = await CreateTenant(proj, dataDir, port, "Full Company");
            await RunPlatform(proj, dataDir, port, "set-brand", freeSlug, "plan", "basic");
            (freeOnlyKey, freeOnlySlug) = await CreateTenant(proj, dataDir, port, "One Computer Free");
            await RunPlatform(proj, dataDir, port, "set-brand", freeOnlySlug, "plan", "free");
            await RunPlatform(proj, dataDir, port, "add-tech", freeSlug, "me@example.com", "FreeOwner!2026", "Fran Free", "--owner", "--technician");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        var http = $"http://127.0.0.1:{port}";
        var wsUrl = $"ws://127.0.0.1:{port}";
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var me = MachineFingerprint.Get();

            // ---- 1. the branding -----------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 1. a downloaded copy learns the company is free ==");
            var freeBrand = await hc.GetFromJsonAsync<Branding>($"{http}/download/{freeSlug}/brand.json", Json.Options);
            var fullBrand = await hc.GetFromJsonAsync<Branding>($"{http}/download/{fullSlug}/brand.json", Json.Options);
            Check("brand.json says the Basic company is a one-person plan, and Basic", freeBrand is { Free: true, Basic: true });
            var freeOnlyBrand = await hc.GetFromJsonAsync<Branding>($"{http}/download/{freeOnlySlug}/brand.json", Json.Options);
            Check("and the Free company's says one-person, NOT Basic (its installer needs a password)", freeOnlyBrand is { Free: true, Basic: false });
            Check("and a full company's says it is not", fullBrand is { Free: false });

            // ---- 2. may this computer be added? ----------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 2. asked BEFORE installing: may this computer be added? ==");
            async Task<(bool ok, string message)> MayAdd(string slug, string hw)
            {
                var a = await hc.GetFromJsonAsync<JsonElement>($"{http}/download/{slug}/may-add?hw={Uri.EscapeDataString(hw)}");
                return (a.GetProperty("ok").GetBoolean(), a.GetProperty("message").GetString() ?? "");
            }
            Check("an empty account: yes", (await MayAdd(freeSlug, "HW-NEW")).ok);
            Check("FreeSetup.MayAddAsync, with THIS PC's fingerprint: yes", await FreeSetup.MayAddAsync(http, freeSlug) == null);

            // This PC is the first machine (its agent), a branch office the second.
            var pcId = NewId();
            await using var agent = await Device(port, pcId, DeviceRole.Agent, freeKey, Environment.MachineName, me, DeviceCan.AskConsent);
            await using var branch = await Device(port, NewId(), DeviceRole.Agent, freeKey, "BRANCH-B", "HW-B");
            // The third the Basic version allows since 2026-10-25.
            await using var branchX = await Device(port, NewId(), DeviceRole.Agent, freeKey, "BRANCH-X", "HW-X");
            await Task.Delay(1000);
            var third = await MayAdd(freeSlug, "HW-C");
            Check("with three on the account, a fourth: NO, in the hub's words", !third.ok && third.message.Contains("allows 3 computers"), third.message);
            Check("but THIS computer, one of the two: yes (a reinstall to change the password)", await FreeSetup.MayAddAsync(http, freeSlug) == null);
            Check("a full company is always yes", (await MayAdd(fullSlug, "HW-ANY")).ok);

            // ---- 3. the password, as this machine's client ------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 3. the password, set by the downloaded copy as this machine's client ==");
            Check("a password the server's rule refuses is refused before anything is sent",
                (await FreeSetup.SetPasswordAsync(wsUrl, freeKey, pcId, "short", TimeSpan.FromSeconds(10))).Contains("at least"));
            var set = await FreeSetup.SetPasswordAsync(wsUrl, freeKey, pcId, Pw, TimeSpan.FromSeconds(20));
            Check("set: unattended switched on and the password kept, the server says so", set == "", set);

            var tok = await Login(port, "me@example.com", "FreeOwner!2026");
            await using var console = await Open($"{wsUrl}/ws/tech?token={tok}");
            var state = await console.WaitFor<StateMsg>(_ => true);
            var dev = state?.Devices.FirstOrDefault(d => d.DeviceId == pcId.DeviceId);
            Check("the console sees it unattended, with a password", dev is { UnattendedEnabled: true, UnattendedPassword: true },
                dev == null ? "not listed" : $"unattended {dev.UnattendedEnabled}, password {dev.UnattendedPassword}");
            agent.Clear();
            console.Ws.Send(new ConnectDeviceMsg { DeviceId = pcId.DeviceId, Password = Pw });
            var start = await agent.WaitFor<StartSessionMsg>(_ => true);
            Check("and connects with it, unattended", start is { AskConsent: false }, console.Last<ErrorMsg>()?.Message ?? "");
            if (start != null) { console.Ws.Send(new EndSessionMsg { SessionId = start.SessionId }); await Task.Delay(600); }

            // ---- 4. installing again to change it ----------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 4. installing again with a new password ==");
            var changed = await FreeSetup.SetPasswordAsync(wsUrl, freeKey, pcId, Pw2, TimeSpan.FromSeconds(20));
            Check("the new one is set — answered for THIS request, not by the old \"has a password\"", changed == "", changed);
            await console.WaitFor<SavedPasswordsMsg>(s => !s.DeviceIds.Contains(pcId.DeviceId));
            console.Clear(); agent.Clear();
            console.Ws.Send(new ConnectDeviceMsg { DeviceId = pcId.DeviceId, Password = Pw });
            var old = await console.WaitFor<ErrorMsg>(_ => true);
            Check("the OLD password no longer works — and the console's remembered one went with it", old is { NeedsPassword: true }, old?.Message ?? "it got in");
            console.Clear(); agent.Clear();
            console.Ws.Send(new ConnectDeviceMsg { DeviceId = pcId.DeviceId, Password = Pw2 });
            var withNew = await agent.WaitFor<StartSessionMsg>(_ => true);
            Check("the new one does", withNew != null, console.Last<ErrorMsg>()?.Message ?? "");
            if (withNew != null) { console.Ws.Send(new EndSessionMsg { SessionId = withNew.SessionId }); await Task.Delay(600); }

            // ---- 5. a third computer ----------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 5. a third computer setting a password ==");
            // A computer whose fingerprint is not one of the two — what an install racing another for the
            // last place, past the may-add question, would meet.
            var thirdPw = await FreeSetup.SetPasswordAsync(wsUrl, freeKey, NewId(), Pw, TimeSpan.FromSeconds(10), hardwareId: "HW-THIRD");
            Check("a FOURTH computer is refused, in the hub's words", thirdPw.Contains("allows 3 computers"), thirdPw);
            // A new identity on THIS computer (uninstalled, then installed again) is the same machine.
            var again = await FreeSetup.SetPasswordAsync(wsUrl, freeKey, NewId(), Pw, TimeSpan.FromSeconds(10));
            Check("a new identity on one of the two computers is a reinstall, not a third", again == "", again);

            // ---- 6. the Basic version with the password left blank (§163) ------------------------------
            Console.WriteLine();
            Console.WriteLine("== 6. the Basic version, password left blank: attended ==");
            var blank = await FreeSetup.SetAttendedOnlyAsync(wsUrl, freeKey, pcId, TimeSpan.FromSeconds(20));
            Check("set up with no password: the server says unattended is off", blank == "", blank);
            var offNow = await console.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == pcId.DeviceId && !u.Device.UnattendedEnabled);
            Check("the console sees it attended", offNow != null);
            console.Clear(); agent.Clear();
            console.Ws.Send(new ConnectDeviceMsg { DeviceId = pcId.DeviceId });
            var attended = await agent.WaitFor<StartSessionMsg>(_ => true);
            Check("and connecting with no password goes in ATTENDED — the agent asks the person — not unattended",
                attended is { AskConsent: true, ShowNotice: true } && console.Last<ErrorMsg>() == null, console.Last<ErrorMsg>()?.Message ?? "");
            if (attended != null) { console.Ws.Send(new EndSessionMsg { SessionId = attended.SessionId }); await Task.Delay(600); }
            Check("setting it up with no password again changes nothing and says done", await FreeSetup.SetAttendedOnlyAsync(wsUrl, freeKey, pcId, TimeSpan.FromSeconds(20)) == "");
            var backOn = await FreeSetup.SetPasswordAsync(wsUrl, freeKey, pcId, Pw, TimeSpan.FromSeconds(20));
            Check("and a password chosen later makes it unattended again", backOn == "", backOn);

            // ---- 7. the Free version: one computer ----------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 7. the Free version: one computer ==");
            Check("an empty Free account: yes", (await MayAdd(freeOnlySlug, "HW-F1")).ok);
            await using var fOne = await Device(port, NewId(), DeviceRole.Agent, freeOnlyKey, "FREE-1", "HW-F1");
            await Task.Delay(800);
            var fTwo = await MayAdd(freeOnlySlug, "HW-F2");
            Check("with one on it, a second: NO — the Free version allows 1 computer", !fTwo.ok && fTwo.message.Contains("The Free version allows 1 computer"), fTwo.message);
            Check("but the one it has: yes (a reinstall)", (await MayAdd(freeOnlySlug, "HW-F1")).ok);

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

    // ------------------------------------------------------------------ plumbing (as freecheck)

    private static async Task<(string Key, string Slug)> CreateTenant(string proj, string dataDir, int port, string name)
    {
        var created = await RunPlatform(proj, dataDir, port, "create-tenant", name);
        var line = created.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:", StringComparison.Ordinal))
                   ?? throw new InvalidOperationException("could not create " + name + ":\n" + created);
        var m = System.Text.RegularExpressions.Regex.Match(created, @"\(slug ([^,\)]+)");
        return (line["Tenant key:".Length..].Trim(), m.Success ? m.Groups[1].Value.Trim() : "");
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

    private static async Task<Peer> Device(int port, DeviceIdentity id, DeviceRole role, string tenantKey, string machine, string hardware, string? can = null)
    {
        var peer = await Open($"ws://127.0.0.1:{port}/ws/device");
        peer.Ws.Send(new HelloMsg
        {
            Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
            UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey, HardwareId = hardware,
            Can = can == null ? new List<string>() : new List<string> { can }
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
