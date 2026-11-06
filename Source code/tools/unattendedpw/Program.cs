using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// The UNATTENDED PASSWORD (2026-10-02), against a real hub.
//
// Clint: "set unattended passwords. This is especially handy if someone wanted to connect to their
// own personal machine from the office." His answers: set in the tray app, by whoever is at the
// machine; nobody gets in without it — the owner and supervisors included.
//
// What this proves, on the server, which is the only place it can be enforced:
//   * only the tray app's connection can set it, and the rule (8 characters) is the server's too;
//   * a technician-started session to an unattended machine with a password needs it — missing,
//     wrong and right; the OWNER is refused like anybody else;
//   * a colleague cannot slip into the running session from the Sessions list;
//   * a blank password never counts as a guess (a console reconnecting can never lock a machine);
//     five wrong ones lock the machine, the right one included;
//   * attended machines and the customer's own call are untouched;
//   * removing it works, and a RESTARTED server still has it;
//   * REMEMBERED (2026-10-06): typed right once, a technician is not asked again — from a new
//     sign-in, after a restart, and during somebody else's lockout; a colleague still is; a new
//     password at the machine forgets everybody's; Forget, a supervisor/owner clearing one or all,
//     and removing the technician forget it; a technician may neither see the list nor clear it.
//
// It starts a PLATFORM OF ITS OWN — its own port, its own empty database in the temp folder, its
// own tenant. The live server and Clint's real devices are not involved and are not modified.
//
//   dotnet run -c Release --project tools/unattendedpw/unattendedpw.csproj
// ---------------------------------------------------------------------------------------------

internal static class UnattendedPw
{
    private static int _pass, _fail;
    private const string Right = "Personal-PC-2026";

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

        var dataDir = Path.Combine(Path.GetTempPath(), "rs-unattendedpw-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server and its database are not touched)");

        string tenantKey, slug = "pw-test-co";
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Password Test Co");
            var line = created.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:", StringComparison.Ordinal));
            if (line == null) { Console.WriteLine("Could not create a tenant:\n" + created); return 1; }
            tenantKey = line["Tenant key:".Length..].Trim();
            var m = System.Text.RegularExpressions.Regex.Match(created, @"\(slug ([^,\)]+)");
            if (m.Success) slug = m.Groups[1].Value.Trim();
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "tech@example.com", "PwProbeTech!2026", "Pat Technician");
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "owner@example.com", "PwProbeOwner!2026", "Olive Owner", "--owner", "--technician");
            // Two more, so the guessing in section 4 is done by people who have never typed it right:
            // a remembered password lets its technician through whatever they type (2026-10-06).
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "gail@example.com", "PwProbeGail!2026", "Gail Guesser");
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "barry@example.com", "PwProbeBarry!2026", "Barry Blank");
        }
        catch (Exception ex) { Console.WriteLine("Could not start the platform: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var tokA = await Login(port, "tech@example.com", "PwProbeTech!2026");
            var tokB = await Login(port, "owner@example.com", "PwProbeOwner!2026");
            var tokG = await Login(port, "gail@example.com", "PwProbeGail!2026");
            var tokBarry = await Login(port, "barry@example.com", "PwProbeBarry!2026");
            if (tokA == null || tokB == null || tokG == null || tokBarry == null) { Console.WriteLine("Could not sign the test technicians in."); return 1; }
            await using var techA = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tokA}");
            await using var owner = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tokB}");
            await using var gail = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tokG}");
            await using var barry = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tokBarry}");
            await Task.Delay(600);

            var pc = NewId();

            // ---- 1. setting it -----------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 1. setting it, from the tray app and only from there ==");
            await using (var tray = await Device(port, pc, DeviceRole.Client, tenantKey, "PERSONAL-PC"))
            await using (var agent = await Device(port, pc, DeviceRole.Agent, tenantKey, "PERSONAL-PC", DeviceCan.AskConsent))
            {
                await tray.WaitFor<UnattendedStateMsg>(_ => true);
                tray.Ws.Send(new SetUnattendedMsg { On = true });
                var on = await tray.WaitFor<UnattendedStateMsg>(u => u.On);
                Check("unattended access is on, and no password yet", on is { On: true, HasPassword: false });

                tray.Clear();
                tray.Ws.Send(new SetUnattendedPasswordMsg { Password = "short" });
                var tooShort = await tray.WaitFor<UnattendedStateMsg>(u => u.PasswordProblem.Length > 0);
                Check("a password under 8 characters is refused by the SERVER, with why", tooShort is { HasPassword: false },
                      tooShort?.PasswordProblem ?? "no answer");

                tray.Clear(); techA.Clear();
                agent.Ws.Send(new SetUnattendedPasswordMsg { Password = "AgentSetThis!123" });
                await Task.Delay(1200);
                Check("the AGENT cannot set one — it is the thing the password guards",
                      tray.Last<UnattendedStateMsg>() == null && techA.Last<DeviceUpdateMsg>() is not { Device.UnattendedPassword: true });

                tray.Clear(); techA.Clear();
                tray.Ws.Send(new SetUnattendedPasswordMsg { Password = Right });
                var set = await tray.WaitFor<UnattendedStateMsg>(u => u.HasPassword);
                Check("the tray app sets it, and is told so by the server", set != null);
                var seen = await techA.WaitFor<DeviceUpdateMsg>(d => d.Device.DeviceId == pc.DeviceId && d.Device.UnattendedPassword);
                Check("every console is told the machine HAS one — so Connect can ask", seen != null);
                Check("and nothing that looks like it travels to the console",
                      seen != null && !JsonSerializer.Serialize(seen).Contains(Right));

                // ---- 2. connecting -----------------------------------------------------------
                Console.WriteLine();
                Console.WriteLine("== 2. a technician connecting to it ==");
                techA.Clear(); agent.Clear();
                techA.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "no password typed" });
                var need = await techA.WaitFor<ErrorMsg>(_ => true);
                Check("without the password: refused, and the console told it is the PASSWORD it wants",
                      need is { NeedsPassword: true } && need.DeviceId == pc.DeviceId, need?.Message ?? "no answer");
                await Task.Delay(500);
                Check("and nothing is started on the machine", agent.Last<StartSessionMsg>() == null);

                techA.Clear();
                techA.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "wrong password", Password = "Not-the-password" });
                var wrong = await techA.WaitFor<ErrorMsg>(_ => true);
                Check("a wrong one: refused, saying how many tries are left", wrong is { NeedsPassword: true } && wrong.Message.Contains("4 more tries"),
                      wrong?.Message ?? "no answer");
                Check("still nothing started", agent.Last<StartSessionMsg>() == null);

                techA.Clear(); owner.Clear();
                owner.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "I own the company" });
                var ownerRefused = await owner.WaitFor<ErrorMsg>(_ => true);
                Check("the OWNER is refused without it too — nobody gets round it", ownerRefused is { NeedsPassword: true },
                      ownerRefused?.Message ?? "the owner got in");

                techA.Clear(); agent.Clear();
                techA.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "the right password", Password = Right });
                var start = await agent.WaitFor<StartSessionMsg>(_ => true);
                Check("the right one: the agent is asked to host, UNATTENDED — nobody is asked", start is { AskConsent: false });
                Check("a full company's session shows no \"Being controlled remotely\" bar — its tray app says so", start is { ShowNotice: false });
                var ready = await techA.WaitFor<SessionReadyMsg>(_ => true);
                Check("and the console sees an unattended session", ready is { Session.Unattended: true });
                var savedA = await techA.WaitFor<SavedPasswordsMsg>(s => s.DeviceIds.Contains(pc.DeviceId));
                Check("REMEMBERED: the console is told its password for this machine is saved", savedA != null);
                Check("and the password itself is not in what it is told",
                      savedA != null && !JsonSerializer.Serialize<Msg>(savedA).Contains(Right));
                Check("a technician is not offered other people's saved passwords", savedA is { MayManage: false });

                if (start != null)
                {
                    await using var host = await Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={start.SessionId}&token={start.HostToken}");
                    await using var viewerA = await Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={start.SessionId}&token={tokA}");
                    await Task.Delay(500);
                    Check("the technician who typed it joins their session", viewerA.Last<SessionEndedMsg>() == null);

                    // ---- 3. a colleague, from the Sessions list ------------------------------
                    Console.WriteLine();
                    Console.WriteLine("== 3. a colleague trying to join the running session ==");
                    var sneak = await TryOpen($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={start.SessionId}&token={tokB}");
                    var shut = sneak.Peer == null ? null : await sneak.Peer.WaitFor<SessionEndedMsg>(_ => true, 4000);
                    Check("opening it straight from the Sessions list is REFUSED", shut != null && shut.Reason.Contains("unattended password"),
                          shut?.Reason ?? (sneak.Peer == null ? sneak.Error : "they were let in"));
                    if (sneak.Peer != null) await sneak.Peer.DisposeAsync();

                    owner.Clear();
                    owner.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "joining" });
                    var joinNeed = await owner.WaitFor<ErrorMsg>(_ => true);
                    Check("Connect on the machine asks THEM for the password, even with a session running", joinNeed is { NeedsPassword: true },
                          joinNeed?.Message ?? "they were handed the session");

                    owner.Clear();
                    owner.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "joining", Password = Right });
                    var joinReady = await owner.WaitFor<SessionReadyMsg>(r => r.Session.SessionId == start.SessionId);
                    Check("with it, they are handed the running session", joinReady != null);
                    await using (var viewerB = await Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={start.SessionId}&token={tokB}"))
                    {
                        await Task.Delay(800);
                        Check("and may now join it", viewerB.Last<SessionEndedMsg>() == null,
                              viewerB.Last<SessionEndedMsg>()?.Reason ?? "");
                    }

                    techA.Ws.Send(new EndSessionMsg { SessionId = start.SessionId });
                    await Task.Delay(600);
                }

                // ---- 4. blanks never count; wrong ones do ------------------------------------
                Console.WriteLine();
                Console.WriteLine("== 4. guessing ==");
                // Barry and Gail have never typed it, so nothing remembered lets them through.
                for (int i = 0; i < 10; i++)
                    barry.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "a console reconnecting with no password" });
                await Task.Delay(800);
                gail.Clear(); agent.Clear();
                gail.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "after ten blanks", Password = Right });
                var afterBlanks = await agent.WaitFor<StartSessionMsg>(_ => true);
                Check("ten BLANK attempts lock nothing — a reconnect loop can never shut a machine", afterBlanks != null);
                Check("and no complaint came with it",
                      gail.Last<ErrorMsg>() == null);
                if (afterBlanks != null) { gail.Ws.Send(new EndSessionMsg { SessionId = afterBlanks.SessionId }); await Task.Delay(600); }

                ErrorMsg? lastWrong = null;
                for (int i = 0; i < 5; i++)
                {
                    barry.Clear();
                    barry.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "guessing", Password = "guess-" + i + "-wrong" });
                    lastWrong = await barry.WaitFor<ErrorMsg>(_ => true);
                }
                Check("the fifth wrong guess says the machine is now refused", lastWrong?.Message.Contains("now refused") == true, lastWrong?.Message ?? "");
                barry.Clear(); agent.Clear();
                barry.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "right, but too late", Password = Right });
                var locked = await barry.WaitFor<ErrorMsg>(_ => true);
                await Task.Delay(300);
                Check("and then even the RIGHT password is refused, for 15 minutes",
                      locked?.Message.Contains("refused for") == true && agent.Last<StartSessionMsg>() == null, locked?.Message ?? "it got in");

                // Gail typed it right just now, so it is remembered for her: somebody else guessing
                // must not lock out the people who already know it.
                gail.Clear(); agent.Clear();
                gail.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "remembered, during a lockout" });
                var gailIn = await agent.WaitFor<StartSessionMsg>(_ => true);
                Check("REMEMBERED: a technician who typed it before is let in during somebody else's lockout, nothing typed",
                      gailIn is { AskConsent: false } && gail.Last<ErrorMsg>() == null, gail.Last<ErrorMsg>()?.Message ?? "");
                if (gailIn != null) { gail.Ws.Send(new EndSessionMsg { SessionId = gailIn.SessionId }); await Task.Delay(600); }

                // ---- 5. a new password starts the count again; removing it --------------------
                Console.WriteLine();
                Console.WriteLine("== 5. changing it, removing it ==");
                tray.Clear();
                techA.Clear(); gail.Clear();
                tray.Ws.Send(new SetUnattendedPasswordMsg { Password = Right + "-new" });
                await tray.WaitFor<UnattendedStateMsg>(u => u.HasPassword);
                Check("REMEMBERED: a new password at the machine forgets EVERYBODY's, and their consoles are told",
                      await techA.WaitFor<SavedPasswordsMsg>(s => !s.DeviceIds.Contains(pc.DeviceId)) != null
                      && await gail.WaitFor<SavedPasswordsMsg>(s => !s.DeviceIds.Contains(pc.DeviceId)) != null);
                gail.Clear(); agent.Clear();
                gail.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "remembered the OLD one" });
                var gailOld = await gail.WaitFor<ErrorMsg>(_ => true);
                await Task.Delay(300);
                Check("and with nothing typed she is asked again", gailOld is { NeedsPassword: true } && agent.Last<StartSessionMsg>() == null,
                      gailOld?.Message ?? "it got in");
                techA.Clear(); agent.Clear();
                techA.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "the old password", Password = Right });
                var old = await techA.WaitFor<ErrorMsg>(_ => true);
                Check("a NEW password: the old one no longer works", old is { NeedsPassword: true } && old.Message.Contains("not the"),
                      old?.Message ?? "");
                techA.Clear(); agent.Clear();
                techA.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "the new one", Password = Right + "-new" });
                var withNew = await agent.WaitFor<StartSessionMsg>(_ => true);
                Check("and the new one works — setting it lifted the lockout", withNew is { AskConsent: false });
                if (withNew != null) { techA.Ws.Send(new EndSessionMsg { SessionId = withNew.SessionId }); await Task.Delay(600); }

                tray.Clear(); techA.Clear();
                tray.Ws.Send(new SetUnattendedPasswordMsg { Password = "" });
                var gone = await tray.WaitFor<UnattendedStateMsg>(u => !u.HasPassword);
                Check("removing it, from the tray app", gone != null);
                Check("every console told", await techA.WaitFor<DeviceUpdateMsg>(d => d.Device.DeviceId == pc.DeviceId && !d.Device.UnattendedPassword) != null);
                techA.Clear(); agent.Clear();
                techA.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "no password any more" });
                var free = await agent.WaitFor<StartSessionMsg>(_ => true);
                Check("and connecting needs none again, unattended", free is { AskConsent: false });
                if (free != null) { techA.Ws.Send(new EndSessionMsg { SessionId = free.SessionId }); await Task.Delay(600); }

                // ---- 6. what it must NOT touch -----------------------------------------------
                Console.WriteLine();
                Console.WriteLine("== 6. attended machines and the customer's own call are untouched ==");
                tray.Clear();
                tray.Ws.Send(new SetUnattendedPasswordMsg { Password = Right });
                await tray.WaitFor<UnattendedStateMsg>(u => u.HasPassword);
                tray.Ws.Send(new SetUnattendedMsg { On = false });
                await tray.WaitFor<UnattendedStateMsg>(u => !u.On && u.HasPassword);
                techA.Clear(); agent.Clear();
                techA.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "attended, with a password set" });
                var attended = await agent.WaitFor<StartSessionMsg>(_ => true);
                Check("unattended OFF: no password asked — the person there is asked instead, as ever",
                      attended is { AskConsent: true } && techA.Last<ErrorMsg>() == null);
                if (attended != null) { techA.Ws.Send(new EndSessionMsg { SessionId = attended.SessionId }); await Task.Delay(600); }

                tray.Ws.Send(new SetUnattendedMsg { On = true });
                await tray.WaitFor<UnattendedStateMsg>(u => u.On && u.HasPassword);
                techA.Clear(); agent.Clear(); tray.Clear();
                tray.Ws.Send(new RequestCallMsg { ContactName = "Me", Issue = "my own machine, asking for help" });
                var ring = await techA.WaitFor<RingMsg>(_ => true);
                if (ring == null) Check("the call rang", false);
                else
                {
                    techA.Ws.Send(new AcceptCallMsg { CallId = ring.Call.CallId });
                    var called = await agent.WaitFor<StartSessionMsg>(_ => true);
                    Check("the person's OWN call needs no password — they are there, and they asked", called != null && techA.Last<ErrorMsg>() == null,
                          techA.Last<ErrorMsg>()?.Message ?? "");
                    if (called != null) { techA.Ws.Send(new EndSessionMsg { SessionId = called.SessionId }); await Task.Delay(600); }
                }
            }

            // ---- 7. a restarted server ---------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 7. after the server restarts ==");
            await techA.DisposeAsync(); await owner.DisposeAsync();
            try { server.Kill(true); server.WaitForExit(10000); } catch { }
            await Task.Delay(1500);
            server = StartPlatform(proj, dataDir, port);
            if (!await WaitForPort(port, 60000)) { Check("the platform came back", false); }
            else
            {
                var tok = await Login(port, "tech@example.com", "PwProbeTech!2026");
                await using var tech2 = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tok}");
                var state = await tech2.WaitFor<StateMsg>(_ => true);
                Check("the console's machine list still says it has a password, before the machine is even back",
                      state?.Devices.FirstOrDefault(d => d.DeviceId == pc.DeviceId) is { UnattendedPassword: true });
                await using var agent2 = await Device(port, pc, DeviceRole.Agent, tenantKey, "PERSONAL-PC", DeviceCan.AskConsent);
                await using var tray2 = await Device(port, pc, DeviceRole.Client, tenantKey, "PERSONAL-PC");
                var told = await tray2.WaitFor<UnattendedStateMsg>(u => u.HasPassword);
                Check("the tray app is told it is still set", told != null);
                await Task.Delay(800);
                tech2.Clear(); agent2.Clear();
                tech2.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "after a restart" });
                var stillNeeds = await tech2.WaitFor<ErrorMsg>(_ => true);
                Check("and connecting still needs it", stillNeeds is { NeedsPassword: true } && agent2.Last<StartSessionMsg>() == null,
                      stillNeeds?.Message ?? "it got in");
                tech2.Clear(); agent2.Clear();
                tech2.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = "after a restart", Password = Right });
                var s7 = await agent2.WaitFor<StartSessionMsg>(_ => true);
                Check("with it, it works", s7 is { AskConsent: false });
                if (s7 != null) { tech2.Ws.Send(new EndSessionMsg { SessionId = s7.SessionId }); await Task.Delay(600); }
                Check("and it is remembered for them", await tech2.WaitFor<SavedPasswordsMsg>(s => s.DeviceIds.Contains(pc.DeviceId)) != null);
                await tech2.DisposeAsync(); await agent2.DisposeAsync(); await tray2.DisposeAsync();

                // ---- 8. remembered passwords (2026-10-06) ----------------------------------------
                Console.WriteLine();
                Console.WriteLine("== 8. remembered, per technician, through another restart ==");
                try { server.Kill(true); server.WaitForExit(10000); } catch { }
                await Task.Delay(1500);
                server = StartPlatform(proj, dataDir, port);
                if (!await WaitForPort(port, 60000)) { Check("the platform came back a second time", false); }
                else await Remembered(proj, dataDir, port, slug, tenantKey, pc);
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
    /// Section 8. Pat (tech) typed the password just before this restart; Olive (owner) has not typed
    /// the current one. Clint's answers: kept on the server, so another sign-in is not asked; a
    /// colleague still is; forgotten by Forget, by a supervisor/owner clearing it, by the technician
    /// being removed; the owner sees names, never the password.
    /// </summary>
    private static async Task Remembered(string proj, string dataDir, int port, string slug, string tenantKey, DeviceIdentity pc)
    {
        var tokA = await Login(port, "tech@example.com", "PwProbeTech!2026");
        var tokB = await Login(port, "owner@example.com", "PwProbeOwner!2026");
        var tokG = await Login(port, "gail@example.com", "PwProbeGail!2026");
        await using var pat = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tokA}");
        await using var olive = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tokB}");
        await using var gail = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tokG}");
        await using var agent = await Device(port, pc, DeviceRole.Agent, tenantKey, "PERSONAL-PC", DeviceCan.AskConsent);
        await using var tray = await Device(port, pc, DeviceRole.Client, tenantKey, "PERSONAL-PC");
        await tray.WaitFor<UnattendedStateMsg>(u => u.HasPassword);
        await Task.Delay(800);

        var atSignIn = await pat.WaitFor<SavedPasswordsMsg>(s => s.DeviceIds.Contains(pc.DeviceId));
        Check("after a RESTART and a new sign-in, the console is told the password is still saved", atSignIn != null);

        async Task<StartSessionMsg?> ConnectBlank(Peer who, string why)
        {
            who.Clear(); agent.Clear();
            who.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = why });
            var s = await agent.WaitFor<StartSessionMsg>(_ => true, 3000);
            if (s != null) { who.Ws.Send(new EndSessionMsg { SessionId = s.SessionId }); await Task.Delay(600); }
            return s;
        }
        async Task<StartSessionMsg?> ConnectWith(Peer who, string why)
        {
            who.Clear(); agent.Clear();
            who.Ws.Send(new ConnectDeviceMsg { DeviceId = pc.DeviceId, Reason = why, Password = Right });
            var s = await agent.WaitFor<StartSessionMsg>(_ => true);
            if (s != null) { who.Ws.Send(new EndSessionMsg { SessionId = s.SessionId }); await Task.Delay(600); }
            return s;
        }
        async Task<SavedPasswordWhoMsg?> Who(Peer asker)
        {
            asker.Clear();
            asker.Ws.Send(new SavedPasswordWhoRequestMsg { DeviceId = pc.DeviceId });
            return await asker.WaitFor<SavedPasswordWhoMsg>(_ => true);
        }

        Check("and Connect with NOTHING typed gets in, unattended", await ConnectBlank(pat, "remembered") is { AskConsent: false },
              pat.Last<ErrorMsg>()?.Message ?? "");

        var oliveList = await olive.WaitFor<SavedPasswordsMsg>(_ => true);
        Check("the owner's console is offered other people's saved passwords", oliveList is { MayManage: true });
        Check("but the owner, who never typed THIS password, is not remembered", oliveList != null && !oliveList.DeviceIds.Contains(pc.DeviceId));
        Check("so a colleague with nothing typed is still asked — the owner included",
              await ConnectBlank(olive, "not mine") == null && olive.Last<ErrorMsg>() is { NeedsPassword: true },
              olive.Last<ErrorMsg>()?.Message ?? "they got in");

        // A technician may not look, nor clear anybody else's.
        pat.Clear();
        pat.Ws.Send(new SavedPasswordWhoRequestMsg { DeviceId = pc.DeviceId });
        var patLook = await pat.WaitFor<ErrorMsg>(_ => true);
        Check("a technician asking who has it saved is refused", patLook != null && pat.Last<SavedPasswordWhoMsg>() == null, patLook?.Message ?? "they were told");
        pat.Clear();
        pat.Ws.Send(new ForgetSavedPasswordMsg { DeviceId = pc.DeviceId, All = true });
        var patClear = await pat.WaitFor<ErrorMsg>(_ => true);
        Check("and clearing everybody's is refused", patClear != null, patClear?.Message ?? "no refusal");

        var who1 = await Who(olive);
        Check("the owner sees WHO has it saved — Pat, by name", who1 != null && who1.People.Select(p => p.Name).SequenceEqual(new[] { "Pat Technician" }),
              who1 == null ? "no answer" : string.Join(", ", who1.People.Select(p => p.Name)));
        Check("and never the password", who1 != null && !JsonSerializer.Serialize<Msg>(who1).Contains(Right));
        Check("Pat's refused clear-all changed nothing", who1?.People.Count == 1);

        Check("the owner types it, and is in", await ConnectWith(olive, "typing it") is { AskConsent: false });
        Check("now both are on the list", (await Who(olive))?.People.Count == 2);

        // Forget one's own.
        pat.Clear();
        pat.Ws.Send(new ForgetSavedPasswordMsg { DeviceId = pc.DeviceId });
        Check("Forget saved password: Pat's console is told it is gone",
              await pat.WaitFor<SavedPasswordsMsg>(s => !s.DeviceIds.Contains(pc.DeviceId)) != null);
        Check("and Pat is asked again", await ConnectBlank(pat, "forgotten") == null && pat.Last<ErrorMsg>() is { NeedsPassword: true },
              pat.Last<ErrorMsg>()?.Message ?? "they got in");
        Check("the owner's own is untouched by it", await ConnectBlank(olive, "still mine") is { AskConsent: false });

        // The owner clears somebody else's.
        await ConnectWith(pat, "typing it again");
        var who2 = await Who(olive);
        var patId = who2?.People.FirstOrDefault(p => p.Name == "Pat Technician")?.TechnicianId ?? 0;
        Check("Pat typed it again and is back on the list", patId != 0);
        pat.Clear();
        olive.Ws.Send(new ForgetSavedPasswordMsg { DeviceId = pc.DeviceId, TechnicianId = patId });
        Check("the owner clears Pat's: Pat's console is told",
              await pat.WaitFor<SavedPasswordsMsg>(s => !s.DeviceIds.Contains(pc.DeviceId)) != null);
        var who3 = await olive.WaitFor<SavedPasswordWhoMsg>(_ => true);
        Check("and the owner's list is redrawn without Pat", who3 != null && who3.People.All(p => p.Name != "Pat Technician"));
        Check("Pat is asked again", await ConnectBlank(pat, "cleared") == null && pat.Last<ErrorMsg>() is { NeedsPassword: true });

        // A removed technician forgets everything.
        await ConnectWith(gail, "Gail types it");
        Check("Gail typed it and is on the list", (await Who(olive))?.People.Any(p => p.Name == "Gail Guesser") == true);
        await RunPlatform(proj, dataDir, port, "remove-tech", slug, "gail@example.com");
        Check("Gail REMOVED: she is gone from the list", (await Who(olive))?.People.Any(p => p.Name == "Gail Guesser") == false);

        // Everybody's.
        olive.Clear();
        olive.Ws.Send(new ForgetSavedPasswordMsg { DeviceId = pc.DeviceId, All = true });
        Check("Clear everybody's: the owner's own console is told too",
              await olive.WaitFor<SavedPasswordsMsg>(s => !s.DeviceIds.Contains(pc.DeviceId)) != null);
        Check("and the list is empty", (await olive.WaitFor<SavedPasswordWhoMsg>(w => w.People.Count == 0)) != null);
        Check("and the owner is asked again", await ConnectBlank(olive, "cleared all") == null && olive.Last<ErrorMsg>() is { NeedsPassword: true });
    }

    // ------------------------------------------------------------------ plumbing (as consentwire)

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
