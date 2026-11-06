using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// THE FREE VERSION'S LIMITS, against a real hub (2026-10-06, docs/NEXT-SESSION-FREE.md, Stage B).
//
// Clint: "a single owner/technician with no ticketing system. It should allow for unattended access
// to a maximum of two computers and have no attended access." With his answers: a third install is
// refused with the reason; removing a machine frees its place, twice a calendar month; the session
// tools are remote control, printing, and Files with copy and paste.
//
// Since 2026-10-25 (HANDOVER §163) there are TWO one-person plans. Sections 1–8 are the BASIC version ("basic"): attended as
// well as unattended — a computer without unattended access and a password is connected to with the person there ASKED, by the
// agent service, never a tray app. Section 9 is the FREE version: one computer — a company from before with two keeps them —
// unattended only, with a password.
//
// What this proves, at the hub — the one place every machine and console passes through:
//   * one person: a second is refused;
//   * three machines since 2026-10-25 (two before): a fourth is refused at hello with words and NotAdmitted, a reinstall of one of the
//     three (same hardware) is not a fourth, and it is still so after a restart;
//   * unattended only, with a password: unattended off is refused (never a consent prompt), no
//     password is refused, a tray app never hosts, a call and an outside-help code are refused;
//   * no reason asked and no ticket written;
//   * in a session, only control, Files, clipboard and printing reach the machine;
//   * from the console, only what the free version has (tickets, Processes, chat refused);
//   * Remove: the machine goes, its agent is told and refused when it comes back, its place is
//     free, and the third removal in a month is refused with the date of the next;
//   * a FULL company beside it is untouched.
//
// It starts a PLATFORM OF ITS OWN — its own port and an empty database in the temp folder.
//
//   dotnet run -c Release --project tools/freecheck/freecheck.csproj
// ---------------------------------------------------------------------------------------------

internal static class FreeCheck
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
        public readonly List<byte[]> Bin = new();
        public volatile bool Closed;
        public T? Last<T>() where T : Msg { lock (Got) return Got.OfType<T>().LastOrDefault(); }
        public void Clear() { lock (Got) { Got.Clear(); Bin.Clear(); } }
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

        var dataDir = Path.Combine(Path.GetTempPath(), "rs-freecheck-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");
        Console.WriteLine("(the live server and its database are not touched)");

        string freeKey, fullKey, oldKey, freeSlug = "", fullSlug = "", oldSlug = "";
        Console.WriteLine();
        Console.WriteLine("== 1. one person ==");
        try
        {
            (freeKey, freeSlug) = await CreateTenant(proj, dataDir, port, "Branch Offices Free");
            (fullKey, fullSlug) = await CreateTenant(proj, dataDir, port, "Full Company");
            await RunPlatform(proj, dataDir, port, "set-brand", freeSlug, "plan", "basic");
            // A company that will be on the Free version with two computers from before (section 9): Basic until the restart.
            (oldKey, oldSlug) = await CreateTenant(proj, dataDir, port, "Old Free");
            await RunPlatform(proj, dataDir, port, "set-brand", oldSlug, "plan", "basic");
            await RunPlatform(proj, dataDir, port, "add-tech", oldSlug, "old@example.com", "OldFree!2026", "Olu Old", "--owner", "--technician");
            var first = await RunPlatform(proj, dataDir, port, "add-tech", freeSlug, "me@example.com", "FreeOwner!2026", "Fran Free", "--owner", "--technician");
            Check("the first person — owner and technician — is added", !first.Contains("free version"), first.Trim());
            var second = await RunPlatform(proj, dataDir, port, "add-tech", freeSlug, "two@example.com", "FreeSecond!2026", "Sid Second");
            Check("a SECOND person is refused, saying why", second.Contains("Basic versions are one person"), second.Trim());
            await RunPlatform(proj, dataDir, port, "add-tech", fullSlug, "boss@example.com", "FullOwner!2026", "Bo Boss", "--owner", "--technician");
            var fullSecond = await RunPlatform(proj, dataDir, port, "add-tech", fullSlug, "tech@example.com", "FullTech!2026", "Ty Tech");
            Check("a FULL company adds a second person as ever", !fullSecond.Contains("free version"), fullSecond.Trim());
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var tok = await Login(port, "me@example.com", "FreeOwner!2026");
            var fullTok = await Login(port, "boss@example.com", "FullOwner!2026");
            if (tok == null || fullTok == null) { Console.WriteLine("Could not sign in."); return 1; }
            await using var me = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tok}");
            await using var boss = await Open($"ws://127.0.0.1:{port}/ws/tech?token={fullTok}");
            var state = await me.WaitFor<StateMsg>(_ => true);
            Check("the console is told the company is on a one-person plan, and that it is the Basic version", state is { Free: true, Basic: true, MaxMachines: 3 });
            Check("and a full company's console is not", (await boss.WaitFor<StateMsg>(_ => true)) is { Free: false });

            // ---- 2. two machines --------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 2. two machines ==");
            var a = NewId("HW-A"); var b = NewId("HW-B"); var c = NewId("HW-C");
            var agentA = await Device(port, a, DeviceRole.Agent, freeKey, "BRANCH-A", DeviceCan.AskConsent);
            // The third the Basic version allows since 2026-10-25: connected, and stored, for the rest of the run.
            var x = NewId("HW-X");
            var agentX = await Device(port, x, DeviceRole.Agent, freeKey, "BRANCH-X", DeviceCan.AskConsent);
            var agentB = await Device(port, b, DeviceRole.Agent, freeKey, "BRANCH-B", DeviceCan.AskConsent);
            await Task.Delay(800);
            Check("the first three machines are let on", agentA.Last<ErrorMsg>() == null && agentB.Last<ErrorMsg>() == null && agentX.Last<ErrorMsg>() == null);
            Check("and the console lists them",
                  await me.WaitFor<DeviceUpdateMsg>(d => d.Device.DeviceId == b.DeviceId) != null);
            me.Clear();
            await using (var agentC = await Device(port, c, DeviceRole.Agent, freeKey, "BRANCH-C", DeviceCan.AskConsent))
            {
                var no = await agentC.WaitFor<ErrorMsg>(_ => true);
                Check("a FOURTH is refused at hello, saying why", no is { NotAdmitted: true } && no.Message.Contains("allows 3 computers"),
                      no?.Message ?? "no refusal");
                await Task.Delay(500);
                Check("and its connection is closed", agentC.Closed);
                Check("and no console ever hears of it", me.Last<DeviceUpdateMsg>() is not { Device.DeviceId: var id } || id != c.DeviceId);
            }
            // A reinstall of A: a new install id on the same hardware is the same machine.
            var a2 = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
            await using (var agentA2 = await Device(port, a2, DeviceRole.Agent, freeKey, "BRANCH-A", DeviceCan.AskConsent, hardware: "HW-A"))
            {
                await Task.Delay(800);
                Check("a REINSTALL of one of the three (same hardware) is not a fourth", agentA2.Last<ErrorMsg>() == null,
                      agentA2.Last<ErrorMsg>()?.Message ?? "");
            }
            // The full company: three, as ever.
            await using (var f1 = await Device(port, NewId("F1"), DeviceRole.Agent, fullKey, "FULL-1"))
            await using (var f2 = await Device(port, NewId("F2"), DeviceRole.Agent, fullKey, "FULL-2"))
            await using (var f3 = await Device(port, NewId("F3"), DeviceRole.Agent, fullKey, "FULL-3"))
            {
                await Task.Delay(800);
                Check("a FULL company takes a third machine", f3.Last<ErrorMsg>() == null);
            }

            // ---- 3. attended, or unattended with a password (the Basic version, §163) ------------
            Console.WriteLine();
            Console.WriteLine("== 3. attended — the person asked, by the agent — or unattended with a password ==");
            await using var setterA = await Device(port, a, DeviceRole.Client, freeKey, "BRANCH-A");   // stands in for the installer
            await setterA.WaitFor<UnattendedStateMsg>(_ => true);
            // §2 reinstalled BRANCH-A (a2) and A's own install has now come back on the same hardware: both are
            // takeovers by hardware alone, so since 2026-10-07 (§111) it waits for the owner to say it is theirs.
            var waiting = await me.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == a.DeviceId && u.Device.ReinstallUnconfirmed);
            Check("(back on the same hardware: it waits for the owner to confirm it — §111)", waiting != null);
            me.Clear();   // or the wait below finds A's update from before the takeover
            me.Ws.Send(new ConfirmReinstallMsg { DeviceId = a.DeviceId });
            var confirmed = await me.WaitFor<DeviceUpdateMsg>(u => u.Device.DeviceId == a.DeviceId && !u.Device.ReinstallUnconfirmed);
            Check("the free owner confirms it from the CONSOLE (its allow-list lets that through)", confirmed != null, me.Last<ErrorMsg>()?.Message ?? "");
            me.Clear(); agentA.Clear();
            me.Ws.Send(new ConnectDeviceMsg { DeviceId = a.DeviceId, Reason = "" });
            var asking = await agentA.WaitFor<StartSessionMsg>(_ => true);
            Check("unattended OFF: ATTENDED — the agent hosts and asks the person, never the tray app, and the notice shows",
                  asking is { AskConsent: true, ShowNotice: true } && setterA.Last<StartSessionMsg>() == null && me.Last<ErrorMsg>() == null,
                  me.Last<ErrorMsg>()?.Message ?? (asking == null ? "nothing started" : $"AskConsent {asking.AskConsent}"));
            if (asking != null) { me.Ws.Send(new EndSessionMsg { SessionId = asking.SessionId }); await Task.Delay(600); }

            setterA.Ws.Send(new SetUnattendedMsg { On = true });
            await setterA.WaitFor<UnattendedStateMsg>(u => u.On);
            me.Clear(); agentA.Clear();
            me.Ws.Send(new ConnectDeviceMsg { DeviceId = a.DeviceId, Reason = "" });
            var noPw = await agentA.WaitFor<StartSessionMsg>(_ => true);
            Check("unattended on but NO password: still ATTENDED — unattended needs both",
                  noPw is { AskConsent: true } && me.Last<ErrorMsg>() == null, me.Last<ErrorMsg>()?.Message ?? (noPw == null ? "nothing started" : $"AskConsent {noPw.AskConsent}"));
            if (noPw != null) { me.Ws.Send(new EndSessionMsg { SessionId = noPw.SessionId }); await Task.Delay(600); }

            setterA.Ws.Send(new SetUnattendedPasswordMsg { Password = Pw });
            await setterA.WaitFor<UnattendedStateMsg>(u => u.HasPassword);
            me.Clear(); agentA.Clear();
            me.Ws.Send(new ConnectDeviceMsg { DeviceId = a.DeviceId, Reason = "", Password = Pw });
            var start = await agentA.WaitFor<StartSessionMsg>(_ => true);
            Check("with the password: in, unattended, and NO REASON asked (there is no ticket for it to describe)",
                  start is { AskConsent: false } && me.Last<ErrorMsg>() == null, me.Last<ErrorMsg>()?.Message ?? "");
            Check("and the host is asked to show \"Being controlled remotely\" (there is no tray app to say so)", start is { ShowNotice: true });

            // ---- 4. in the session -------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 4. in a session: control, Files, clipboard, printing — nothing else ==");
            if (start != null)
            {
                await using var host = await Open($"ws://127.0.0.1:{port}/ws/session?role=host&id={start.SessionId}&token={start.HostToken}");
                await using var viewer = await Open($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={start.SessionId}&token={tok}");
                await Task.Delay(800);
                host.Clear();
                Msg[] allowed =
                {
                    new MouseMsg(), new KeyMsg(), new SubscribeMsg(), new ClipboardMsg { Text = "copied" }, new ClipboardRequestMsg(),
                    new FileListRequestMsg(), new RemotePrintMsg(), new PrintFileMsg(), new SysActionMsg { Action = "lock" }
                };
                Msg[] refused =
                {
                    new ScriptRunMsg { Script = "whoami" }, new AudioListenMsg(), new VoiceTalkMsg { On = true },
                    new PrivacyScreenMsg { On = true }, new RecordingStateMsg { On = true }
                };
                foreach (var m in allowed.Concat(refused)) viewer.Ws.Send(m);
                viewer.Ws.SendBinary(new byte[] { BinKind.FileChunk, 1, (byte)'x', 0, 0, 0, 0, 0, 0, 0, 0, 42 });
                viewer.Ws.SendBinary(new byte[] { BinKind.Voice, 0, 0, 0, 0 });
                await Task.Delay(1500);
                List<string> got; int fileChunks, voice;
                lock (host.Got)
                {
                    got = host.Got.Select(m => m.GetType().Name).ToList();
                    fileChunks = host.Bin.Count(x => x[0] == BinKind.FileChunk);
                    voice = host.Bin.Count(x => x[0] == BinKind.Voice);
                }
                var missing = allowed.Select(m => m.GetType().Name).Where(n => !got.Contains(n)).ToList();
                var leaked = refused.Select(m => m.GetType().Name).Where(n => got.Contains(n)).ToList();
                Check("control, Files, clipboard, printing and the System menu reach the machine", missing.Count == 0,
                      missing.Count == 0 ? "" : "missing: " + string.Join(", ", missing));
                Check("scripts, sound, talking, the privacy screen and recording do NOT", leaked.Count == 0,
                      leaked.Count == 0 ? "" : "leaked: " + string.Join(", ", leaked));
                Check("a file chunk reaches it", fileChunks == 1, $"{fileChunks}");
                Check("a voice frame does not", voice == 0, $"{voice}");
                me.Ws.Send(new EndSessionMsg { SessionId = start.SessionId });
                await Task.Delay(800);
            }
            Check("NO TICKET was written for the session", TicketCount(dataDir, freeSlug) == 0, $"{TicketCount(dataDir, freeSlug)} ticket(s)");

            // ---- 5. a tray app, calls and outside help -----------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 5. nobody at the machine asks, and a tray app never hosts ==");
            me.Clear(); setterA.Clear();
            setterA.Ws.Send(new RequestCallMsg { ContactName = "Somebody", Issue = "help me please" });
            var noCall = await setterA.WaitFor<ErrorMsg>(_ => true);
            await Task.Delay(400);
            Check("a support request from the machine is refused, and nothing rings",
                  noCall != null && me.Last<RingMsg>() == null, noCall?.Message ?? "no refusal");
            setterA.Clear();
            setterA.Ws.Send(new OutsiderCodeRequestMsg { For = "my accountant" });
            var noCode = await setterA.WaitFor<ErrorMsg>(_ => true);
            Check("an outside-help code is refused", noCode != null && setterA.Last<OutsiderCodeMsg>() == null, noCode?.Message ?? "");

            // A machine with only a tray app: never hosted by it, even with permission and a password.
            await agentA.DisposeAsync();
            await Task.Delay(800);
            me.Clear(); setterA.Clear();
            me.Ws.Send(new ConnectDeviceMsg { DeviceId = a.DeviceId, Reason = "", Password = Pw });
            await Task.Delay(1200);
            Check("with the agent gone, the tray app is NOT asked to host", setterA.Last<StartSessionMsg>() == null);
            Check("and the console is told why rather than left waiting", me.Last<ErrorMsg>() != null, me.Last<ErrorMsg>()?.Message ?? "");

            // ---- 6. the console asks only for what the free version has -------------------------
            Console.WriteLine();
            Console.WriteLine("== 6. the console: only what the free version has ==");
            foreach (var (m, what) in new (Msg, string)[]
                     {
                         (new TicketListRequestMsg { Filter = "open" }, "the ticket list"),
                         (new WatchMachineMsg { DeviceId = b.DeviceId, On = true }, "Processes"),
                         (new StaffChatSendMsg { To = "everyone", Text = "hi" }, "office chat"),
                         (new WakeRequestMsg { DeviceId = b.DeviceId }, "Wake up"),
                     })
            {
                me.Clear();
                me.Ws.Send(m);
                var e = await me.WaitFor<ErrorMsg>(_ => true, 3000);
                Check($"{what} is refused, in words", e != null && e.Message.Contains("full version"), e?.Message ?? "no answer");
            }
            boss.Clear();
            boss.Ws.Send(new TicketListRequestMsg { Filter = "open" });
            Check("a full company's console still gets its tickets", await boss.WaitFor<TicketListMsg>(_ => true) != null);

            // ---- 7. Remove ----------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 7. removing a machine frees its place, twice a month ==");
            me.Clear(); agentB.Clear();
            me.Ws.Send(new DeviceRemoveMsg { DeviceId = b.DeviceId });
            var gone = await me.WaitFor<DeviceGoneMsg>(g => g.DeviceId == b.DeviceId);
            Check("Remove: the console is told it is gone", gone != null);
            var told = await agentB.WaitFor<ErrorMsg>(e => e.NotAdmitted);
            await Task.Delay(500);
            Check("its agent is told, and its connection closed", told != null && agentB.Closed, told?.Message ?? "");
            await using (var back = await Device(port, b, DeviceRole.Agent, freeKey, "BRANCH-B", DeviceCan.AskConsent))
            {
                var refusedBack = await back.WaitFor<ErrorMsg>(_ => true);
                Check("the removed install saying hello again is refused — it cannot put itself back",
                      refusedBack is { NotAdmitted: true } && refusedBack.Message.Contains("removed"), refusedBack?.Message ?? "it was let on");
            }
            var agentC2 = await Device(port, c, DeviceRole.Agent, freeKey, "BRANCH-C", DeviceCan.AskConsent);
            await Task.Delay(800);
            Check("its place is free: the fourth machine is let on now", agentC2.Last<ErrorMsg>() == null, agentC2.Last<ErrorMsg>()?.Message ?? "");
            me.Clear();
            me.Ws.Send(new DeviceRemoveMsg { DeviceId = c.DeviceId });
            Check("a second removal this month", await me.WaitFor<DeviceGoneMsg>(g => g.DeviceId == c.DeviceId) != null);
            me.Clear();
            me.Ws.Send(new DeviceRemoveMsg { DeviceId = a.DeviceId });
            var third = await me.WaitFor<ErrorMsg>(_ => true);
            Check("a THIRD removal this month is refused, saying when the next can be",
                  third != null && third.Message.Contains("this month's are used") && third.Message.Contains(" 1 "), third?.Message ?? "it was removed");
            Check("and the machine is still there", me.Last<DeviceGoneMsg>() == null);
            Check("the removals are recorded", RemovalCount(dataDir, freeSlug) == 2, $"{RemovalCount(dataDir, freeSlug)}");
            boss.Clear();
            boss.Ws.Send(new DeviceRemoveMsg { DeviceId = "anything" });
            var fullRemove = await boss.WaitFor<ErrorMsg>(_ => true);
            // Since 2026-10-06 the full version removes too — with a reason, and no monthly limit (tools/removecheck).
            Check("a full company's Remove asks a reason, and has no monthly limit", fullRemove != null && fullRemove.Message.Contains("Choose why"), fullRemove?.Message ?? "");
            await agentC2.DisposeAsync(); await agentB.DisposeAsync();

            // ---- the Free version's two computers from before, made while it was still Basic -----
            var o1 = NewId("HW-O1"); var o2 = NewId("HW-O2");
            await using (var old1 = await Device(port, o1, DeviceRole.Agent, oldKey, "OLD-1", DeviceCan.AskConsent))
            await using (var old2 = await Device(port, o2, DeviceRole.Agent, oldKey, "OLD-2", DeviceCan.AskConsent))
                await Task.Delay(1200);

            // ---- 8. after a restart -------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 8. after the server restarts ==");
            await me.DisposeAsync(); await boss.DisposeAsync(); await setterA.DisposeAsync();
            await RunPlatform(proj, dataDir, port, "set-brand", oldSlug, "plan", "free");
            try { server.Kill(true); server.WaitForExit(10000); } catch { }
            // Paid for four (the Basic version's extra computers, 2026-10-25) — read by the hub when it loads.
            await RunPlatform(proj, dataDir, port, "set-brand", freeSlug, "basic-machines", "4");
            await Task.Delay(1500);
            server = StartPlatform(proj, dataDir, port);
            if (!await WaitForPort(port, 60000)) { Check("the platform came back", false); }
            else
            {
                await using (var back = await Device(port, b, DeviceRole.Agent, freeKey, "BRANCH-B"))
                {
                    var e = await back.WaitFor<ErrorMsg>(_ => true);
                    Check("a removed install is still refused", e is { NotAdmitted: true } && e.Message.Contains("removed"), e?.Message ?? "it was let on");
                }
                // A and X are the machines left; paid for four now, D and E make four, and the next is a fifth.
                var d1 = NewId("HW-D"); var d2 = NewId("HW-E");
                await using var dOne = await Device(port, d1, DeviceRole.Agent, freeKey, "BRANCH-D");
                await Task.Delay(800);
                Check("a third machine is let on", dOne.Last<ErrorMsg>() == null, dOne.Last<ErrorMsg>()?.Message ?? "");
                await using var dTwo = await Device(port, d2, DeviceRole.Agent, freeKey, "BRANCH-E");

                await Task.Delay(800);
                Check("paid for four: a fourth machine is let on after the restart", dTwo.Last<ErrorMsg>() == null, dTwo.Last<ErrorMsg>()?.Message ?? "");
                await using var dThree = await Device(port, NewId("HW-F"), DeviceRole.Agent, freeKey, "BRANCH-F");
                var e2 = await dThree.WaitFor<ErrorMsg>(_ => true);
                Check("and a fifth is refused, counted from what is stored, saying it can be added on Billing", e2 is { NotAdmitted: true } && e2.Message.Contains("allows 4 computers") && e2.Message.Contains("Add a computer on Billing"), e2?.Message ?? "it was let on");

                // A reinstall with nothing left connected under the old id (the way a real one goes):
                // the old id must leave every console's list, or it reads as a third machine — and
                // removing that twin would spend one of the month's removals on nothing.
                var tok2 = await Login(port, "me@example.com", "FreeOwner!2026");
                await using var me2 = await Open($"ws://127.0.0.1:{port}/ws/tech?token={tok2}");
                await me2.WaitFor<StateMsg>(_ => true);
                await dOne.DisposeAsync();
                await Task.Delay(800);
                var d1b = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
                await using var dOneAgain = await Device(port, d1b, DeviceRole.Agent, freeKey, "BRANCH-D", hardware: "HW-D");
                var twin = await me2.WaitFor<DeviceGoneMsg>(g => g.DeviceId == d1.DeviceId);
                Check("a REINSTALL takes the old install id off the console's list — no twin", twin != null && dOneAgain.Last<ErrorMsg>() == null,
                      dOneAgain.Last<ErrorMsg>()?.Message ?? "");

                // ---- 9. the Free version (back 2026-10-25, §163) ----------------------------------
                Console.WriteLine();
                Console.WriteLine("== 9. the Free version: one computer, unattended only, with a password ==");
                var oldTok = await Login(port, "old@example.com", "OldFree!2026");
                await using var olu = await Open($"ws://127.0.0.1:{port}/ws/tech?token={oldTok}");
                var oldState = await olu.WaitFor<StateMsg>(_ => true);
                Check("its console is told: a one-person plan, NOT the Basic version, one computer", oldState is { Free: true, Basic: false, MaxMachines: 1 },
                      oldState == null ? "no state" : $"Basic {oldState.Basic}, {oldState.MaxMachines}");
                await using var oldA = await Device(port, o1, DeviceRole.Agent, oldKey, "OLD-1", DeviceCan.AskConsent);
                await using var oldB = await Device(port, o2, DeviceRole.Agent, oldKey, "OLD-2", DeviceCan.AskConsent);
                await Task.Delay(900);
                Check("the two computers it had before are both let back on — only a NEW one is refused", oldA.Last<ErrorMsg>() == null && oldB.Last<ErrorMsg>() == null,
                      oldA.Last<ErrorMsg>()?.Message ?? oldB.Last<ErrorMsg>()?.Message ?? "");
                await using (var oldC = await Device(port, NewId("HW-O3"), DeviceRole.Agent, oldKey, "OLD-3", DeviceCan.AskConsent))
                {
                    var no = await oldC.WaitFor<ErrorMsg>(_ => true);
                    Check("a new computer is refused: the Free version allows 1, and upgrading is the way to more",
                          no is { NotAdmitted: true } && no.Message.Contains("The Free version allows 1 computer") && no.Message.Contains("upgrade"), no?.Message ?? "it was let on");
                }
                olu.Clear(); oldA.Clear();
                olu.Ws.Send(new ConnectDeviceMsg { DeviceId = o1.DeviceId, Reason = "" });
                var freeOff = await olu.WaitFor<ErrorMsg>(_ => true);
                await Task.Delay(400);
                Check("unattended OFF: refused — the Free version never asks anybody", freeOff != null && freeOff.Message.Contains("unattended only")
                      && oldA.Last<StartSessionMsg>() == null, freeOff?.Message ?? "it started");
                await using var oldSetter = await Device(port, o1, DeviceRole.Client, oldKey, "OLD-1");
                await oldSetter.WaitFor<UnattendedStateMsg>(_ => true);
                oldSetter.Ws.Send(new SetUnattendedMsg { On = true });
                await oldSetter.WaitFor<UnattendedStateMsg>(u => u.On);
                olu.Clear(); oldA.Clear();
                olu.Ws.Send(new ConnectDeviceMsg { DeviceId = o1.DeviceId, Reason = "" });
                var freeNoPw = await olu.WaitFor<ErrorMsg>(_ => true);
                await Task.Delay(400);
                Check("unattended on, NO password: refused, saying the Free version requires one",
                      freeNoPw != null && freeNoPw.Message.Contains("Free version requires") && oldA.Last<StartSessionMsg>() == null, freeNoPw?.Message ?? "it started");
                oldSetter.Ws.Send(new SetUnattendedPasswordMsg { Password = Pw });
                await oldSetter.WaitFor<UnattendedStateMsg>(u => u.HasPassword);
                olu.Clear(); oldA.Clear();
                olu.Ws.Send(new ConnectDeviceMsg { DeviceId = o1.DeviceId, Reason = "", Password = Pw });
                var freeIn = await oldA.WaitFor<StartSessionMsg>(_ => true);
                Check("with the password: in, unattended, with the notice", freeIn is { AskConsent: false, ShowNotice: true } && olu.Last<ErrorMsg>() == null, olu.Last<ErrorMsg>()?.Message ?? "");
                oldSetter.Clear();
                oldSetter.Ws.Send(new RequestCallMsg { ContactName = "Somebody", Issue = "help" });
                var freeCall = await oldSetter.WaitFor<ErrorMsg>(_ => true);
                Check("a support request is refused in the Free version's words", freeCall != null && freeCall.Message.Contains("Free version is unattended only"), freeCall?.Message ?? "");
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

    // ------------------------------------------------------------------ the database, read directly

    private static int TicketCount(string dataDir, string slug) =>
        Scalar(dataDir, "SELECT COUNT(*) FROM Tickets t JOIN Tenants n ON n.Id = t.TenantId WHERE n.Slug = $s", slug);

    private static int RemovalCount(string dataDir, string slug) =>
        Scalar(dataDir, "SELECT COUNT(*) FROM DeviceRemovals r JOIN Tenants n ON n.Id = r.TenantId WHERE n.Slug = $s", slug);

    private static int Scalar(string dataDir, string sql, string slug)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = HarnessDb.Portable(sql);
        cmd.With("@s", slug);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ------------------------------------------------------------------ plumbing (as unattendedpw)

    private static async Task<(string Key, string Slug)> CreateTenant(string proj, string dataDir, int port, string name)
    {
        var created = await RunPlatform(proj, dataDir, port, "create-tenant", name);
        var line = created.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:", StringComparison.Ordinal))
                   ?? throw new InvalidOperationException("could not create " + name + ":\n" + created);
        var m = System.Text.RegularExpressions.Regex.Match(created, @"\(slug ([^,\)]+)");
        return (line["Tenant key:".Length..].Trim(), m.Success ? m.Groups[1].Value.Trim() : "");
    }

    private static readonly Dictionary<string, string> HardwareOf = new();

    private static DeviceIdentity NewId(string hardware)
    {
        var id = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
        HardwareOf[id.DeviceId] = hardware;
        return id;
    }

    private static async Task<Peer> Open(string url)
    {
        var cws = new ClientWebSocket();
        await cws.ConnectAsync(new Uri(url), CancellationToken.None);
        var peer = new Peer { Ws = new WsConnection(cws) };
        peer.Ws.OnMessage += m => { lock (peer.Got) peer.Got.Add(m); };
        peer.Ws.OnBinary += b => { lock (peer.Got) peer.Bin.Add(b.ToArray()); };
        _ = Task.Run(async () => { try { await peer.Ws.RunAsync(CancellationToken.None); } catch { } peer.Closed = true; });
        return peer;
    }

    private static async Task<Peer> Device(int port, DeviceIdentity id, DeviceRole role, string tenantKey, string machine,
        string? can = null, string? hardware = null)
    {
        var peer = await Open($"ws://127.0.0.1:{port}/ws/device");
        peer.Ws.Send(new HelloMsg
        {
            Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
            UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey,
            HardwareId = hardware ?? (HardwareOf.TryGetValue(id.DeviceId, out var hw) ? hw : ""),
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
