using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// END-TO-END ENCRYPTION THROUGH A REAL HUB (2026-10-07, docs/NEXT-SESSION-E2E.md, HANDOVER §112).
//
// A platform of its own. The console is the REAL SessionLink (handshake, pins, badge status). The host is a
// stand-in made of the same SessionSeal and E2eHandshake the real host uses — and it keeps every frame exactly
// as the server relayed it, before opening. A "server's-eye" viewer says hello (so the session stays sealed)
// but holds no private key: it receives everything the server fans out, as the server sees it.
//
//   1. sealed: the console is given the key, signed, and pinned; markers the host sends (clipboard, picture,
//      file, script output) arrive opened at the console and appear NOWHERE in what the server relayed; markers
//      the console sends (clipboard, a script, a file) arrive opened at the host and appear nowhere either —
//      AND NOWHERE IN THE PLATFORM'S DATABASE; the ticket still says which script ran (its first line, once);
//      a clear-list message sent sealed by a rogue console never reaches the host;
//   2. an older console joins: after its grace the host is told CLEAR, the old console can read, the new one's
//      badge says "not end-to-end encrypted"; it leaves and the host is told to seal again;
//   3. the machine's identity changes: the console is asked, and opens NOTHING until it answers; yes uses and
//      re-pins it; a pinned machine answering UNSIGNED is asked too, and no keeps it shut;
//   4. the free version: a sealed script from a console is dropped by the hub's allow-list by its visible type;
//      a sealed mouse movement passes.
//
//   dotnet run -c Release --project tools/e2ewire/e2ewire.csproj
// ---------------------------------------------------------------------------------------------

internal static class E2eWire
{
    private static int _pass, _fail;
    private static void Check(string what, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private const string Pw = "FreeMachine!2026";
    private static readonly string[] HostMarkers = { "MARKER-HOST-CLIP-7f3a", "MARKER-HOST-VIDEO-19c2", "MARKER-HOST-FILE-55de", "MARKER-HOST-SCRIPT-a1b0" };
    private static readonly string[] TechMarkers = { "MARKER-TECH-CLIP-3e8d", "MARKER-TECH-SCRIPT-0c4f", "MARKER-TECH-FILE-9a7b" };

    // ------------------------------------------------------------------ the stand-in host

    private sealed class FakeHost
    {
        public WsConnection Ws = null!;
        public SessionSeal Seal = null!;
        public string SessionId = "";
        private readonly byte[] _k = RandomNumberGenerator.GetBytes(32);
        private readonly ECDiffieHellman _eph = E2eHandshake.NewEphemeral();
        public ECDsa? Identity;
        private readonly Dictionary<string, E2eKeyMsg> _answers = new();
        private uint _next;
        public volatile bool Sealing;
        public readonly List<byte[]> RawBinary = new();
        public readonly List<string> RawText = new();
        public readonly List<Msg> Opened = new();
        public readonly List<byte[]> OpenedBinary = new();
        public readonly List<E2eModeMsg> Modes = new();
        public readonly List<string> SealedTagsArrived = new();

        public static async Task<FakeHost> Connect(int port, StartSessionMsg start, ECDsa? identity)
        {
            var h = new FakeHost { Identity = identity, SessionId = start.SessionId };
            h.Seal = new SessionSeal(start.SessionId); h.Seal.UseKey(h._k, 0);
            var cws = new ClientWebSocket();
            await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/session?role=host&id={start.SessionId}&token={start.HostToken}"), CancellationToken.None);
            h.Ws = new WsConnection(cws);   // NO Seal on the socket: this stand-in keeps the raw frame, then opens it itself
            h.Ws.OnMessage += h.OnMessage;
            h.Ws.OnBinary += h.OnBinary;
            _ = Task.Run(async () => { try { await h.Ws.RunAsync(); } catch { } });
            await Task.Delay(200);
            h.Ws.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() } });
            h.Ws.Send(new E2eReadyMsg());
            return h;
        }

        private void OnMessage(Msg m)
        {
            lock (RawText) RawText.Add(Json.Serialize(m));
            if (m is E2eModeMsg mode) { Sealing = mode.Sealed; lock (Modes) Modes.Add(mode); return; }
            if (m is E2eHelloMsg hello)
            {
                E2eKeyMsg? a;
                lock (_answers)
                    if (!_answers.TryGetValue(hello.Pub, out a))
                    {
                        a = E2eHandshake.Answer(SessionId, _eph, _k, hello.Pub, ++_next, Identity);
                        if (a == null) return;
                        _answers[hello.Pub] = a;
                    }
                Ws.Send(a);
                return;
            }
            lock (Opened) Opened.Add(m);
        }

        private void OnBinary(ReadOnlyMemory<byte> b)
        {
            var f = b.ToArray();
            lock (RawBinary) RawBinary.Add(f);
            if (f[0] == BinKind.Sealed)
            {
                lock (SealedTagsArrived) SealedTagsArrived.Add(SessionSeal.TagOfFrame(f) ?? "?");
                var m = Seal.OpenMessage(f); if (m != null) lock (Opened) Opened.Add(m);
            }
            else if ((f[0] & BinKind.SealedBit) != 0) { var o = Seal.OpenBinary(f); if (o != null) lock (OpenedBinary) OpenedBinary.Add(o); }
            else lock (OpenedBinary) OpenedBinary.Add(f);
        }

        public void Send(Msg m)
        {
            if (Sealing && !SessionSeal.StaysClear(m)) Ws.SendBinary(Seal.SealMessage(m));
            else Ws.Send(m);
        }

        public void SendBinary(byte[] frame)
        {
            if (Sealing) Ws.SendBinary(Seal.SealBinary(frame));
            else Ws.SendBinary(frame);
        }
    }

    // ------------------------------------------------------------------ a raw viewer: the server's eye, an old console, a rogue

    private sealed class RawViewer
    {
        public WsConnection Ws = null!;
        public readonly List<Msg> Got = new();
        public readonly List<byte[]> RawBinary = new();
        public readonly List<string> RawText = new();
        public SessionSeal? Seal;
        public ECDiffieHellman? Eph;
        public volatile bool Closed;
        public string Pub = "";

        public static async Task<RawViewer> Open(int port, string sessionId, string token)
        {
            var v = new RawViewer();
            var cws = new ClientWebSocket();
            await cws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/session?role=viewer&id={sessionId}&token={token}"), CancellationToken.None);
            v.Ws = new WsConnection(cws);
            v.Ws.OnMessage += m => { lock (v.Got) { v.Got.Add(m); v.RawText.Add(Json.Serialize(m)); } };
            v.Ws.OnBinary += b => { lock (v.RawBinary) v.RawBinary.Add(b.ToArray()); };
            _ = Task.Run(async () => { try { await v.Ws.RunAsync(); } catch { } v.Closed = true; });
            return v;
        }

        /// <summary>Says hello with a key whose private half is thrown away: counted as able, can open nothing.</summary>
        public void HelloWithoutKey()
        {
            using var k = E2eHandshake.NewEphemeral();
            Ws.Send(new E2eHelloMsg { Pub = E2eHandshake.PubOf(k) });
        }

        /// <summary>A console that keeps its key — so it can seal things of its own choosing (a rogue).</summary>
        public async Task<bool> HelloAndKey(string sessionId)
        {
            Eph = E2eHandshake.NewEphemeral(); Pub = E2eHandshake.PubOf(Eph);
            Ws.Send(new E2eHelloMsg { Pub = Pub });
            var until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until)
            {
                E2eKeyMsg? mine; lock (Got) mine = Got.OfType<E2eKeyMsg>().FirstOrDefault(k => k.For == Pub);
                if (mine != null)
                {
                    var a = E2eHandshake.Accept(sessionId, Eph, mine, out _);
                    if (a == null) return false;
                    Seal = new SessionSeal(sessionId); Seal.UseKey(a.Key, a.SenderId);
                    return true;
                }
                await Task.Delay(50);
            }
            return false;
        }
    }

    private static async Task<int> Main()
    {
        var root = FindRepoRoot();
        if (root == null) { Console.WriteLine("Could not find the repository root."); return 1; }
        var dataDir = Path.Combine(Path.GetTempPath(), "rs-e2ewire-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(dataDir);
        var pinFile = Path.Combine(dataDir, "machine-keys.json");
        int port = FreePort();
        var proj = Path.Combine(root, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj");
        var wsBase = $"ws://127.0.0.1:{port}";
        Console.WriteLine($"A platform of its own on port {port}, database in {dataDir}");

        string key, freeKey;
        try
        {
            var created = await RunPlatform(proj, dataDir, port, "create-tenant", "Full Company");
            key = KeyOf(created); var slug = SlugOf(created);
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "boss@example.com", "FullOwner!2026", "Bo Boss", "--owner", "--technician");
            await RunPlatform(proj, dataDir, port, "add-tech", slug, "tech@example.com", "FullTech!2026", "Ty Tech");
            var free = await RunPlatform(proj, dataDir, port, "create-tenant", "Free Person");
            freeKey = KeyOf(free); var freeSlug = SlugOf(free);
            await RunPlatform(proj, dataDir, port, "set-brand", freeSlug, "plan", "free");
            await RunPlatform(proj, dataDir, port, "add-tech", freeSlug, "me@example.com", "FreeOwner!2026", "Fran Free", "--owner", "--technician");
        }
        catch (Exception ex) { Console.WriteLine("Could not set up: " + ex.Message); return 1; }

        var server = StartPlatform(proj, dataDir, port);
        var cts = new CancellationTokenSource();
        try
        {
            if (!await WaitForPort(port, 60000)) { Console.WriteLine("The platform never started listening."); return 1; }
            var bossTok = (await Login(port, "boss@example.com", "FullOwner!2026"))!;
            var techTok = (await Login(port, "tech@example.com", "FullTech!2026"))!;
            var boss = await Peer.Open($"{wsBase}/ws/tech?token={bossTok}");
            await boss.WaitFor<StateMsg>(_ => true);

            var pc = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
            var agent = await Device(port, pc, DeviceRole.Agent, key, "E2E-PC", "HW-E2E");
            var tray = await Device(port, pc, DeviceRole.Client, key, "E2E-PC", "HW-E2E");
            await tray.WaitFor<UnattendedStateMsg>(_ => true);
            tray.Ws.Send(new SetUnattendedMsg { On = true });
            await tray.WaitFor<UnattendedStateMsg>(u => u.On);
            using var identity1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            // ---- 1. sealed -----------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 1. a session sealed end to end ==");
            var (start, host) = await StartSession(boss, agent, port, pc.DeviceId, identity1);
            if (host == null) { Console.WriteLine("No session started."); return 1; }
            var (link, seen) = Link(wsBase, start.SessionId, bossTok, pc.DeviceId, "E2E-PC", pinFile, cts.Token);
            var spy = await RawViewer.Open(port, start.SessionId, techTok);
            spy.HelloWithoutKey();
            Check("the console is given the key, signed by the machine, and remembers it (End-to-end encrypted)",
                await Until(() => link.Status == SessionLink.E2eStatus.Encrypted), link.Status.ToString());
            Check("the hub tells the host to seal", await Until(() => host.Sealing));
            Check("the console's identity file now holds the machine", new MachinePins(pinFile).Check(pc.DeviceId, "E2E-PC", "", E2eHandshake.PubOf(identity1)).Verdict == MachinePins.Verdict.Matches);
            await Task.Delay(300);

            host.Send(new ClipboardMsg { Text = HostMarkers[0] });
            host.SendBinary(Frame(BinKind.Video, HostMarkers[1]));
            host.SendBinary(Bin.FileChunk("f1", 0, Encoding.UTF8.GetBytes(HostMarkers[2])));
            host.Send(new ScriptOutputMsg { RunId = "r", Text = HostMarkers[3] });
            host.Send(new FileActivityMsg { Kind = "pull", FromMachine = true, Label = "report.pdf", FileCount = 1, Bytes = 10 });
            Check("what the host sent arrives OPENED at the console — clipboard, picture, file, script output",
                await Until(() => seen.Msgs.OfType<ClipboardMsg>().Any(c => c.Text == HostMarkers[0])
                    && seen.Bins.Any(b => Has(b, HostMarkers[1])) && seen.Bins.Any(b => Has(b, HostMarkers[2]))
                    && seen.Msgs.OfType<ScriptOutputMsg>().Any(o => o.Text == HostMarkers[3])));
            await Task.Delay(500);
            Check("and NONE of it is readable in what the server relayed (the server's-eye viewer)",
                HostMarkers.All(m => !SpySaw(spy, m)), string.Join(", ", HostMarkers.Where(m => SpySaw(spy, m))));
            Check("(the server's-eye viewer did receive the frames — it just cannot read them)", spy.RawBinary.Count >= 4, $"{spy.RawBinary.Count} frames");
            Check("the disclosure stays clear, for the ticket and the tray app", tray.Got.OfType<FileActivityMsg>().Any() || await Until(() => tray.Got.OfType<FileActivityMsg>().Any(), 3000));

            link.Send(new ClipboardMsg { Text = TechMarkers[0] });
            link.Send(new ScriptAuditMsg { Language = "powershell", FirstLine = "# tidy the temp folder" });
            link.Send(new ScriptRunMsg { RunId = "r1", Language = "powershell", Script = "# tidy the temp folder\nGet-Thing " + TechMarkers[1] });
            link.SendBinary(Bin.FileChunk("f2", 0, Encoding.UTF8.GetBytes(TechMarkers[2])));
            link.Send(new MouseMsg { X = 321, Y = 123, Action = "move" });
            Check("what the console sent arrives OPENED at the host — clipboard, script, file, mouse",
                await Until(() => host.Opened.OfType<ClipboardMsg>().Any(c => c.Text == TechMarkers[0])
                    && host.Opened.OfType<ScriptRunMsg>().Any(s => s.Script.Contains(TechMarkers[1]))
                    && host.OpenedBinary.Any(b => Has(b, TechMarkers[2])) && host.Opened.OfType<MouseMsg>().Any(m => m.X == 321)));
            Check("and none of it was readable in what the server relayed to the host",
                TechMarkers.All(m => !host.RawBinary.Any(b => Has(b, m)) && !host.RawText.Any(t => t.Contains(m))));
            Check("the script audit line is NOT passed to the host", !host.Opened.OfType<ScriptAuditMsg>().Any() && !host.RawText.Any(t => t.Contains("script_audit")));
            await Task.Delay(1500);
            Check("the ticket still says which script ran — its first line, once",
                Count(dataDir, "SELECT COUNT(*) FROM TicketNotes WHERE Body LIKE 'Ran powershell script: # tidy the temp folder%'") == 1,
                Count(dataDir, "SELECT COUNT(*) FROM TicketNotes WHERE Body LIKE 'Ran powershell script%'").ToString());
            var dbBytes = DatabaseBytes(dataDir);
            Check("NO marker, either way, anywhere in the platform's database", HostMarkers.Concat(TechMarkers).All(m => !Has(dbBytes, m)),
                string.Join(", ", HostMarkers.Concat(TechMarkers).Where(m => Has(dbBytes, m))));

            var rogue = await RawViewer.Open(port, start.SessionId, techTok);
            Check("(a rogue console holding the key)", await rogue.HelloAndKey(start.SessionId));
            int chatsBefore = host.SealedTagsArrived.Count(t => t == "chat");
            rogue.Ws.SendBinary(rogue.Seal!.SealMessage(new ChatMsg { Text = "sealed chat" }));
            rogue.Ws.SendBinary(rogue.Seal!.SealMessage(new VoiceMicAnswerMsg()));
            rogue.Ws.SendBinary(rogue.Seal!.SealMessage(new MouseMsg { X = 999 }));
            await Until(() => host.Opened.OfType<MouseMsg>().Any(m => m.X == 999));
            await Task.Delay(400);
            Check("clear-list messages sent SEALED never reach the host (the hub drops them by their visible type)",
                host.SealedTagsArrived.Count(t => t == "chat") == chatsBefore && !host.SealedTagsArrived.Contains("voice_mic_answer"),
                string.Join(",", host.SealedTagsArrived.Distinct()));
            Check("while sealed content from the same console passes", host.Opened.OfType<MouseMsg>().Any(m => m.X == 999));
            await rogue.Ws.CloseAsync();

            // ---- 2. an older console --------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 2. an older console joins ==");
            int modesBefore = host.Modes.Count;
            var old = await RawViewer.Open(port, start.SessionId, techTok);   // says no hello: an older console
            Check("after its grace the host is told: CLEAR", await Until(() => host.Modes.Skip(modesBefore).Any(m => !m.Sealed), 9000));
            host.Send(new ClipboardMsg { Text = "readable-by-the-old-console" });
            Check("the older console can read what is sent now", await Until(() => old.Got.OfType<ClipboardMsg>().Any(c => c.Text == "readable-by-the-old-console")));
            Check("and the new console SAYS so: not end-to-end encrypted", await Until(() => link.Status == SessionLink.E2eStatus.NotEncrypted), link.Status.ToString());
            int modesMid = host.Modes.Count;
            await old.Ws.CloseAsync();
            Check("the older console leaves: the host is told to seal again", await Until(() => host.Modes.Skip(modesMid).Any(m => m.Sealed)));
            await spy.Ws.CloseAsync();
            boss.Ws.Send(new EndSessionMsg { SessionId = start.SessionId });
            cts.Cancel(); cts = new CancellationTokenSource();
            await Task.Delay(1500);

            // ---- 3. the identity changes ------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 3. the machine's identity changes ==");
            using var identity2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var (start2, host2) = await StartSession(boss, agent, port, pc.DeviceId, identity2);
            var (link2, seen2) = Link(wsBase, start2.SessionId, bossTok, pc.DeviceId, "E2E-PC", pinFile, cts.Token);
            Check("the console is ASKED: the identity changed", await Until(() => seen2.Question?.Verdict == MachinePins.Verdict.Changed), seen2.Question?.Verdict.ToString() ?? "not asked");
            Check("and its badge says it is waiting", link2.Status == SessionLink.E2eStatus.Held);
            Check("the host was told to seal", await Until(() => host2!.Sealing));
            host2!.Send(new ClipboardMsg { Text = "before-the-answer" });
            host2.SendBinary(Frame(BinKind.Video, "PICTURE-BEFORE-ANSWER"));
            await Task.Delay(800);
            Check("NOTHING is opened before the technician answers — no clipboard, no picture",
                !seen2.Msgs.OfType<ClipboardMsg>().Any() && !seen2.Bins.Any(b => Has(b, "PICTURE-BEFORE-ANSWER")));
            seen2.Question!.Value.Answer(true);
            host2.Send(new ClipboardMsg { Text = "after-yes" });
            Check("yes (it was reinstalled): the session opens", await Until(() => seen2.Msgs.OfType<ClipboardMsg>().Any(c => c.Text == "after-yes")));
            Check("and the new identity is the one remembered", new MachinePins(pinFile).Check(pc.DeviceId, "E2E-PC", "", E2eHandshake.PubOf(identity2)).Verdict == MachinePins.Verdict.Matches
                && link2.Status == SessionLink.E2eStatus.Encrypted);
            boss.Ws.Send(new EndSessionMsg { SessionId = start2.SessionId });
            cts.Cancel(); cts = new CancellationTokenSource();
            await Task.Delay(1500);

            var (start3, host3) = await StartSession(boss, agent, port, pc.DeviceId, null);   // a host that cannot sign
            var (link3, seen3) = Link(wsBase, start3.SessionId, bossTok, pc.DeviceId, "E2E-PC", pinFile, cts.Token);
            Check("a known machine answering UNSIGNED: asked too", await Until(() => seen3.Question?.Verdict == MachinePins.Verdict.Unsigned), seen3.Question?.Verdict.ToString() ?? "not asked");
            seen3.Question!.Value.Answer(false);
            await Until(() => host3!.Sealing);
            host3!.Send(new ClipboardMsg { Text = "after-no" });
            await Task.Delay(800);
            Check("no: it stays shut — nothing opened, still held", !seen3.Msgs.OfType<ClipboardMsg>().Any() && link3.Status == SessionLink.E2eStatus.Held);
            boss.Ws.Send(new EndSessionMsg { SessionId = start3.SessionId });
            cts.Cancel(); cts = new CancellationTokenSource();
            await Task.Delay(1000);

            // ---- 4. the free version ------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("== 4. the free version's allow-list, on sealed frames ==");
            var home = new DeviceIdentity { DeviceId = Guid.NewGuid().ToString("N"), DeviceKey = Guid.NewGuid().ToString("N") };
            var set = await FreeSetup.SetPasswordAsync(wsBase, freeKey, home, Pw, TimeSpan.FromSeconds(20), hardwareId: "HW-FREE");
            var freeAgent = await Device(port, home, DeviceRole.Agent, freeKey, "HOME-PC", "HW-FREE");
            var meTok = (await Login(port, "me@example.com", "FreeOwner!2026"))!;
            var me = await Peer.Open($"{wsBase}/ws/tech?token={meTok}");
            await me.WaitFor<StateMsg>(_ => true);
            await Task.Delay(500);
            me.Ws.Send(new ConnectDeviceMsg { DeviceId = home.DeviceId, Password = Pw });
            var fstart = await freeAgent.WaitFor<StartSessionMsg>(_ => true);
            Check("(a free session)", set == "" && fstart != null, set + (me.Last<ErrorMsg>()?.Message ?? ""));
            if (fstart != null)
            {
                var fhost = await FakeHost.Connect(port, fstart, identity1);
                var frogue = await RawViewer.Open(port, fstart.SessionId, meTok);
                Check("(the free console holds the key)", await frogue.HelloAndKey(fstart.SessionId));
                await Until(() => fhost.Sealing);
                frogue.Ws.SendBinary(frogue.Seal!.SealMessage(new ScriptRunMsg { Script = "whoami" }));
                frogue.Ws.SendBinary(frogue.Seal!.SealMessage(new MouseMsg { X = 42 }));
                await Until(() => fhost.Opened.OfType<MouseMsg>().Any(m => m.X == 42));
                await Task.Delay(400);
                Check("a sealed SCRIPT on the free version is dropped by the hub, by its visible type", !fhost.SealedTagsArrived.Contains("script_run"),
                    string.Join(",", fhost.SealedTagsArrived));
                Check("a sealed mouse movement passes", fhost.Opened.OfType<MouseMsg>().Any(m => m.X == 42));
            }

            Console.WriteLine();
            Console.WriteLine($"{_pass} of {_pass + _fail} checks passed.");
            return _fail == 0 ? 0 : 1;
        }
        finally
        {
            cts.Cancel();
            try { if (!server.HasExited) server.Kill(true); } catch { }
            try { await Task.Delay(300); Directory.Delete(dataDir, true); } catch { }
        }
    }

    // ------------------------------------------------------------------ the console, as SessionForm drives it

    private sealed class Seen
    {
        public readonly List<Msg> Msgs = new();
        public readonly List<byte[]> Bins = new();
        public (MachinePins.Verdict Verdict, Action<bool> Answer)? Question;
    }

    private static (SessionLink, Seen) Link(string wsBase, string sessionId, string token, string deviceId, string machine, string pinFile, CancellationToken ct)
    {
        var seen = new Seen();
        var link = new SessionLink(wsBase, sessionId, token) { DeviceId = deviceId, MachineName = machine, Owner = "", Pins = new MachinePins(pinFile) };
        link.OnMessage += m => { lock (seen.Msgs) seen.Msgs.Add(m); };
        link.OnBinary += b => { lock (seen.Bins) seen.Bins.Add(b.ToArray()); };
        link.OnIdentityQuestion += (v, _, _, answer) => seen.Question = (v, answer);
        _ = link.RunAsync(ct);
        return (link, seen);
    }

    private static async Task<(StartSessionMsg start, FakeHost? host)> StartSession(Peer console, Peer agent, int port, string deviceId, ECDsa? identity)
    {
        agent.Clear();
        console.Ws.Send(new ConnectDeviceMsg { DeviceId = deviceId, Reason = "checking end-to-end encryption" });
        var start = await agent.WaitFor<StartSessionMsg>(_ => true);
        if (start == null) return (new StartSessionMsg(), null);
        return (start, await FakeHost.Connect(port, start, identity));
    }

    private static byte[] Frame(byte kind, string marker) => new[] { kind }.Concat(new byte[13]).Concat(Encoding.UTF8.GetBytes(marker)).ToArray();

    private static bool Has(byte[] hay, string needle)
    {
        var n = Encoding.UTF8.GetBytes(needle);
        for (int i = 0; i + n.Length <= hay.Length; i++)
            if (hay.AsSpan(i, n.Length).SequenceEqual(n)) return true;
        return false;
    }

    private static bool SpySaw(RawViewer spy, string marker)
    {
        lock (spy.RawBinary) if (spy.RawBinary.Any(b => Has(b, marker))) return true;
        lock (spy.Got) return spy.RawText.Any(t => t.Contains(marker));
    }

    private static async Task<bool> Until(Func<bool> ok, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { try { if (ok()) return true; } catch { } await Task.Delay(50); }
        try { return ok(); } catch { return false; }
    }

    // ------------------------------------------------------------------ the database, read as bytes

    private static byte[] DatabaseBytes(string dataDir)
    {
        var all = new List<byte>();
        foreach (var f in Directory.GetFiles(dataDir, "platform.db*"))
            try { using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); var b = new byte[s.Length]; s.ReadExactly(b); all.AddRange(b); } catch { }
        return all.ToArray();
    }

    private static int Count(string dataDir, string sql)
    {
        using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = HarnessDb.Portable(sql);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ------------------------------------------------------------------ plumbing (as reinstallcheck)

    private sealed class Peer
    {
        public WsConnection Ws = null!;
        public readonly List<Msg> Got = new();
        public T? Last<T>() where T : Msg { lock (Got) return Got.OfType<T>().LastOrDefault(); }
        public void Clear() { lock (Got) Got.Clear(); }
        public async Task<T?> WaitFor<T>(Func<T, bool> ok, int ms = 8000) where T : Msg
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { lock (Got) { var m = Got.OfType<T>().LastOrDefault(ok); if (m != null) return m; } await Task.Delay(40); }
            return null;
        }
        public static async Task<Peer> Open(string url)
        {
            var cws = new ClientWebSocket();
            await cws.ConnectAsync(new Uri(url), CancellationToken.None);
            var p = new Peer { Ws = new WsConnection(cws) };
            p.Ws.OnMessage += m => { lock (p.Got) p.Got.Add(m); };
            _ = Task.Run(async () => { try { await p.Ws.RunAsync(); } catch { } });
            return p;
        }
    }

    private static async Task<Peer> Device(int port, DeviceIdentity id, DeviceRole role, string tenantKey, string machine, string hardware)
    {
        var p = await Peer.Open($"ws://127.0.0.1:{port}/ws/device");
        p.Ws.Send(new HelloMsg
        {
            Role = role, DeviceId = id.DeviceId, DeviceKey = id.DeviceKey, MachineName = machine,
            UserName = "probe", Os = "probe", Version = "1.0", TenantKey = tenantKey, HardwareId = hardware,
            Can = role == DeviceRole.Agent ? new List<string> { DeviceCan.AskConsent } : new List<string>()
        });
        return p;
    }

    private static string KeyOf(string created) => created.Split('\n').First(l => l.StartsWith("Tenant key:"))["Tenant key:".Length..].Trim();
    private static string SlugOf(string created) => Regex.Match(created, @"\(slug ([^,\)]+)").Groups[1].Value.Trim();

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
