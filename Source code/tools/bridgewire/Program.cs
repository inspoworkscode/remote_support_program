using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Technician;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// bridgewire - copying files STRAIGHT from one machine to another the technician has open, through
// the server and never through the technician's PC (2026-10-15; docs/NEXT-SESSION-HOST2HOST.md, HANDOVER §144).
//
// A platform of its own on port 8187. Two pretend tray apps each start the REAL host program,
// RemoteSupport.Host.exe built from this checkout, as the agent does. The console is the REAL
// SessionLink — the end-to-end handshake, sealing, and SendSealed for the copy's key — and plays
// the protocol SessionForm.Bridge.cs plays: ask the hub, wait for its yes, hand each machine the key.
//
//   0. BridgeSeal on its own: no plaintext in a frame; altered, replayed, reflected, another
//      copy's and a wrong key all refused
//   1. a copy A -> B lands on B byte for byte (files, a folder, an empty folder, 3 MB to make the
//      window and the acknowledgements work), and B's machine puts it on its clipboard; both
//      technicians' windows told; the ticket names the other machine; the person at the machine is
//      told "another computer"; the files' words are nowhere in the platform's database
//   2. refused: another technician's session, a session gone clear, an older host, over the limit
//   3. a key sent CLEAR by somebody else in the session moves nothing
//   4. B's window closed half-way: A stops, says so, and B's half-written copy is removed
//
// Both hosts run on THIS PC and its clipboard, so it takes the clipboard harnesses' mutex.
// Clint's live server and his database are never touched.
//
//   dotnet run -c Release --project tools/bridgewire/bridgewire.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8187;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine($"bridgewire - files straight from one machine to the other, through the server ({HarnessDb.Name})");
Console.WriteLine();

using var clipboardTurn = new Mutex(false, @"Global\RemoteSupport-clipboard-harness");
bool gotTurn;
try { gotTurn = clipboardTurn.WaitOne(TimeSpan.FromMinutes(3)); } catch (AbandonedMutexException) { gotTurn = true; }
if (!gotTurn) { Console.WriteLine("another clipboard harness has been running for three minutes; not starting a second one."); return 2; }

// ---- 0. the seal on its own ---------------------------------------------------------------
Console.WriteLine("THE SEAL OF ONE COPY");
{
    var key = RandomNumberGenerator.GetBytes(32);
    var id = Guid.NewGuid().ToString("N");
    using var sender = new BridgeSeal(id, key, sending: true);
    using var receiver = new BridgeSeal(id, key, sending: false);
    var sealMarker = "SEALMARKER-" + Guid.NewGuid().ToString("N")[..8];
    var chunk = Bin.FileChunk(id + "#0", 0, Encoding.UTF8.GetBytes(sealMarker));
    var f1 = sender.SealChunk(chunk);
    var f2 = sender.SealMessage(new FileSetOfferMsg { SetId = id, Label = sealMarker });
    Check("the bridge id is readable for routing, and nothing else is", BridgeSeal.IdOf(f1) == id && !Has(f1, sealMarker) && !Has(f2, sealMarker));
    var (_, opened) = receiver.Open(f1);
    var (msg, _) = receiver.Open(f2);
    Check("the other end opens both: the chunk whole, the message as itself",
        opened != null && opened.SequenceEqual(chunk) && msg is FileSetOfferMsg o && o.Label == sealMarker);
    Check("the same frame again is refused (a replay)", receiver.Open(f2) == (null, null));
    var altered = sender.SealMessage(new BridgeAckMsg { Bytes = 5 }); altered[^20] ^= 1;
    Check("an altered frame is refused", receiver.Open(altered) == (null, null));
    var mine = sender.SealMessage(new BridgeAckMsg { Bytes = 6 });
    Check("a frame bounced back at the end that sent it is refused", sender.Open(mine) == (null, null));
    using var other = new BridgeSeal(Guid.NewGuid().ToString("N"), key, sending: false);
    Check("another copy's frame is refused", other.Open(sender.SealMessage(new BridgeAckMsg())) == (null, null));
    using var wrong = new BridgeSeal(id, RandomNumberGenerator.GetBytes(32), sending: false);
    Check("a frame under another key is refused", wrong.Open(sender.SealMessage(new BridgeAckMsg())) == (null, null));
    Check("a plain id only: no path, no spaces", !BridgeSeal.IsId("../x") && !BridgeSeal.IsId("a b") && BridgeSeal.IsId(id));
}

// ---- the platform and the two real hosts ------------------------------------------------------
var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-bridgewire-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
var hostDir = Path.Combine(work, "host");
Console.WriteLine();
Log($"building the platform and the host into {work}");
foreach (var (proj, dir) in new[] { ("RemoteSupport.Platform", serverDir), ("RemoteSupport.Host", hostDir) })
{
    var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", proj, proj + ".csproj")}\" -v q --nologo -p:OutDir=\"{dir}\\\"", repo, 400000);
    if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine($"{proj} would not build"); return 2; }
}
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
var hostExe = Path.Combine(hostDir, "RemoteSupport.Host.exe");

var env = new Dictionary<string, string>
{
    ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false",
    ["RSP_Platform__PublicUrl"] = http, ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[]
{
    "create-tenant \"Bridge Wire\" bridge-wire 5",
    "approve bridge-wire",
    "add-tech bridge-wire tech@local.test localtest \"Local Tech\" --admin",
    "add-tech bridge-wire other@local.test localtest \"Other Tech\"",
})
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
var server = Process.Start(psi)!;
var serverLog = new ConcurrentQueue<string>();
server.OutputDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.ErrorDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
var hosts = new ConcurrentBag<Process>();

using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
bool up = false;
for (int i = 0; i < 60 && !up; i++) { try { up = (await hc.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { await Task.Delay(500); } }
if (!up) { Console.WriteLine(string.Join("\n", serverLog)); Console.WriteLine("the platform did not come up"); Kill(); return 2; }

var cts = new CancellationTokenSource();
var linkStops = new ConcurrentDictionary<SessionLink, CancellationTokenSource>();
var pinFile = Path.Combine(work, "machine-keys.json");
var files = Path.Combine(work, "copied");
var pasteRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "Temp", "RemoteSupport", "paste");
var marker = "BRIDGEMARKER-" + Guid.NewGuid().ToString("N")[..10];
var pastesAtStart = PasteFolders();
try { await WireAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally
{
    cts.Cancel(); Kill();
    // What it copied (600 MB of it) and what landed in this user's paste staging go; the server's data is kept.
    try { Directory.Delete(files, true); } catch { }
    foreach (var d in PasteFolders().Except(pastesAtStart)) try { Directory.Delete(d, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server, hosts and data kept at {work}");
return fail == 0 ? 0 : 1;

void Kill()
{
    foreach (var h in hosts) try { if (!h.HasExited) h.Kill(true); } catch { }
    try { if (!server.HasExited) server.Kill(true); } catch { }
}

async Task WireAsync()
{
    var tok1 = await Login("tech@local.test");
    var tok2 = await Login("other@local.test");
    var tech1 = await Peer.Open($"{ws}/ws/tech?token={tok1}");
    var tech2 = await Peer.Open($"{ws}/ws/tech?token={tok2}");
    if (await tech1.WaitFor<StateMsg>(_ => true) == null || await tech2.WaitFor<StateMsg>(_ => true) == null) throw new Exception("no state from the tech sockets");

    var trayA = await Tray("bridge-a", "BRIDGE-A", realHost: true);
    var trayB = await Tray("bridge-b", "BRIDGE-B", realHost: true);
    var trayC = await Tray("bridge-c", "BRIDGE-C", realHost: false);
    await Task.Delay(800);

    var sA = await Start(tech1, "bridge-a");
    var sB = await Start(tech1, "bridge-b");
    Log($"sessions: A {sA}, B {sB}");
    var (linkA, seenA) = Link(sA, tok1, "bridge-a", "BRIDGE-A");
    var (linkB, seenB) = Link(sB, tok1, "bridge-b", "BRIDGE-B");
    bool keyed = await Until(() => linkA.Keyed && linkB.Keyed, 25000);
    bool can = await Until(() => seenA.Has<SysInfoMsg>(HostCan.Bridge) && seenB.Has<SysInfoMsg>(HostCan.Bridge), 15000);
    Check("both sessions are end-to-end encrypted, and both real hosts say they can copy straight across", keyed && can,
        $"A {linkA.Status}, B {linkB.Status}; can: {can}");
    if (!keyed || !can) return;
    await Task.Delay(1500);   // the hub settles each session as sealed once every viewer has said hello

    // ---- 1. A to B ---------------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("A COPY FROM A, PASTED ON B — STRAIGHT ACROSS");
    Directory.CreateDirectory(files);
    var report = Path.Combine(files, "report.txt");
    File.WriteAllText(report, "the report " + marker + new string('r', 4000));
    var folder = Path.Combine(files, "Logs");
    Directory.CreateDirectory(Path.Combine(folder, "deeper"));
    Directory.CreateDirectory(Path.Combine(folder, "empty"));
    File.WriteAllText(Path.Combine(folder, "deeper", "today.log"), "a log line " + marker);
    var big = new byte[3 * 1024 * 1024 + 123]; RandomNumberGenerator.Fill(big);
    File.WriteAllBytes(Path.Combine(folder, "big.bin"), big);
    var paths = new List<string> { report, folder };
    long bytes = new FileInfo(report).Length + Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    ClearClipboard();
    var pastesBefore = PasteFolders();
    var id1 = Guid.NewGuid().ToString("N");
    var yes = await Ask(linkB, seenB, id1, sA, bytes, 2);
    Check("the server agrees: the ask comes back", yes, yes ? "" : "no answer");
    if (!yes) return;
    var key1 = RandomNumberGenerator.GetBytes(32);
    bool sealedB = linkB.SendSealed(new BridgeKeyMsg { BridgeId = id1, Key = Convert.ToBase64String(key1), Send = false, Other = "BRIDGE-A" });
    bool sealedA = linkA.SendSealed(new BridgeKeyMsg { BridgeId = id1, Key = Convert.ToBase64String(key1), Send = true, Paths = paths, Other = "BRIDGE-B" });
    Check("each machine is handed the key inside its own sealed session", sealedA && sealedB);
    var endB = await seenB.WaitFor<BridgeEndMsg>(e => e.BridgeId == id1, 60000);
    Check("B's window is told the copy landed", endB is { Ok: true }, endB == null ? "nothing in 60 s" : $"ok {endB.Ok}, {endB.Files} file(s), {endB.Bytes:N0} bytes{(endB.Why.Length > 0 ? ", " + endB.Why : "")}");
    var endA = await seenA.WaitFor<BridgeEndMsg>(e => e.BridgeId == id1, 5000);
    Check("...and so is A's", endA is { Ok: true });
    Check("B's machine said how far it had got, sealed, along the way", seenB.All<BridgeProgressMsg>().Any(p => p.BridgeId == id1)
        && linkB.Status == SessionLink.E2eStatus.EncryptedUnchecked, $"{seenB.All<BridgeProgressMsg>().Count(p => p.BridgeId == id1)} progress message(s); B {linkB.Status}");

    var onClip = await ReadClipboardFiles(8000);
    Check("B's machine put them on its clipboard: the two things copied", onClip.Count == 2
        && onClip.Select(Path.GetFileName).OrderBy(n => n).SequenceEqual(new[] { "Logs", "report.txt" }), string.Join(", ", onClip));
    var landed = onClip.Count > 0 ? Path.GetDirectoryName(onClip[0])! : "";
    Check("...in a paste staging folder of B's own, not anywhere A named", landed.StartsWith(pasteRoot, StringComparison.OrdinalIgnoreCase), landed);
    Check("...byte for byte, the folder and the file inside a folder too",
        Same(report, Path.Combine(landed, "report.txt")) && Same(Path.Combine(folder, "big.bin"), Path.Combine(landed, "Logs", "big.bin"))
        && Same(Path.Combine(folder, "deeper", "today.log"), Path.Combine(landed, "Logs", "deeper", "today.log")));
    Check("...and the empty folder arrives as an empty folder", Directory.Exists(Path.Combine(landed, "Logs", "empty")));

    var faA = await seenA.WaitFor<FileActivityMsg>(f => f.BridgeId == id1, 8000);
    var faB = await seenB.WaitFor<FileActivityMsg>(f => f.BridgeId == id1, 8000);
    Check("A's machine reports what left it, naming B to the technician", faA is { FromMachine: true, Error: null } && faA.OtherMachine == "BRIDGE-B",
        faA == null ? "nothing" : faA.Describe("Local Tech"));
    Check("B's machine reports what arrived, naming A", faB is { FromMachine: false, Error: null } && faB.OtherMachine == "BRIDGE-A",
        faB == null ? "nothing" : faB.Describe("Local Tech"));
    var trayLine = await trayA.WaitFor<FileActivityMsg>(f => f.BridgeId == id1, 5000);
    Check("the person at A is told \"another computer\", never which", trayLine != null && trayLine.OtherMachine == ""
        && trayLine.Describe("Local Tech").Contains("straight to another computer"), trayLine?.Describe("Local Tech") ?? "nothing");
    await Task.Delay(1500);
    Check("A's ticket says where they went", Count("SELECT COUNT(*) FROM TicketNotes WHERE Body LIKE '%straight to BRIDGE-B%'") == 1);
    Check("B's ticket says where they came from", Count("SELECT COUNT(*) FROM TicketNotes WHERE Body LIKE '%straight from BRIDGE-A%'") == 1);
    if (!HarnessDb.Postgres)
        Check("the files' words are nowhere in the platform's database", !Has(DatabaseBytes(), marker));
    Check("...nor in anything the server logged", !serverLog.Any(l => l.Contains(marker)));

    // ---- 2. refused --------------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("REFUSED");
    // Another technician with only B open (joined as a second viewer) cannot reach into A.
    var (linkB2, seenB2) = Link(sB, tok2, "bridge-b", "BRIDGE-B");
    await Until(() => linkB2.Keyed, 15000);
    var r1 = await Refusal(linkB2, seenB2, sA);
    Check("somebody without A open themselves cannot copy from it", r1 != null && r1.Contains("both machines open yourself"), r1 ?? "not refused");

    // A viewer that cannot open sealed content (an older console) in A: after its grace, A goes clear.
    var raw = await Peer.Open($"{ws}/ws/session?role=viewer&id={sA}&token={tok2}");
    await Task.Delay(6500);
    var r2 = await Refusal(linkB, seenB, sA);
    Check("a session gone clear cannot be copied from", r2 != null && r2.Contains("not end-to-end encrypted"), r2 ?? "not refused");
    raw.Socket.Abort();
    await Task.Delay(1500);

    var r3 = await Refusal(linkB, seenB, sA, bytes: FileSetLimits.MaxBytes + 1);
    Check("over the size limit", r3 != null && r3.Contains("over"), r3 ?? "not refused");

    // An older host: BRIDGE-C's host is played here, and says nothing about copying straight across. The other
    // technician leaves B first: two machines at once, at most (§143).
    Stop(linkB2);
    await Task.Delay(800);
    var sC = await Start(tech2, "bridge-c");
    var (linkC2, seenC2) = Link(sC, tok2, "bridge-c", "BRIDGE-C");
    var (linkA2, _) = Link(sA, tok2, "bridge-a", "BRIDGE-A");
    await Until(() => linkA2.Keyed, 15000);
    await Task.Delay(1500);
    var r4 = await Refusal(linkC2, seenC2, sA);
    Check("a machine running an older app is never asked", r4 != null && r4.Contains("older app"), r4 ?? "not refused");

    // ---- 3. a key sent clear ---------------------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("A KEY SENT CLEAR");
    var rogue = await Peer.Open($"{ws}/ws/session?role=viewer&id={sA}&token={tok1}");
    var id3 = Guid.NewGuid().ToString("N");
    var key3 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    // Inside its grace the session is still sealed, so the hub agrees to the route — and the host must refuse a key it
    // did not get sealed. Clear content in a sealed session never reaches it.
    rogue.Ws.Send(new BridgeKeyMsg { BridgeId = id3, Key = key3, Send = true, Paths = new() { report }, Other = "BRIDGE-B" });
    bool yes3 = await Ask(linkB, seenB, id3, sA, 100, 1);
    linkB.SendSealed(new BridgeKeyMsg { BridgeId = id3, Key = key3, Send = false, Other = "BRIDGE-A" });
    await Task.Delay(5000);
    Check("a key sent clear by somebody in the session moves nothing", yes3 && !seenB.All<BridgeProgressMsg>().Any(p => p.BridgeId == id3)
        && !seenA.All<FileActivityMsg>().Any(f => f.BridgeId == id3), yes3 ? "" : "(the route was refused, so this proved nothing)");
    linkB.Send(new BridgeEndMsg { BridgeId = id3, Why = "cancelled by the harness" });
    rogue.Socket.Abort();
    await Task.Delay(1500);

    // ---- 4. B's window closed half-way -----------------------------------------------------------
    Console.WriteLine();
    Console.WriteLine("B's WINDOW CLOSED HALF-WAY");
    Stop(linkA2);
    await Task.Delay(1000);
    var huge = Path.Combine(files, "huge.bin");
    using (var fs = new FileStream(huge, FileMode.Create))
    {
        var block = new byte[1 << 20]; RandomNumberGenerator.Fill(block);
        for (int i = 0; i < 600; i++) fs.Write(block);
    }
    var before4 = PasteFolders();
    var id4 = Guid.NewGuid().ToString("N");
    if (!await Ask(linkB, seenB, id4, sA, new FileInfo(huge).Length, 1)) { Check("(the server agreed to the big copy)", false); return; }
    var key4 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    linkB.SendSealed(new BridgeKeyMsg { BridgeId = id4, Key = key4, Send = false, Other = "BRIDGE-A" });
    linkA.SendSealed(new BridgeKeyMsg { BridgeId = id4, Key = key4, Send = true, Paths = new() { huge }, Other = "BRIDGE-B" });
    var started = await seenB.WaitFor<BridgeProgressMsg>(p => p.BridgeId == id4 && p.BytesDone > 0, 20000);
    Check("(the big copy is moving)", started != null, started == null ? "" : $"{FileSetLimits.Human(started.BytesDone)} of {FileSetLimits.Human(started.TotalBytes)}");
    Stop(linkB);
    var endA4 = await seenA.WaitFor<BridgeEndMsg>(e => e.BridgeId == id4, 10000);
    Check("A's window is told it stopped, and why", endA4 is { Ok: false } && endA4.Why.Contains("closed the window"), endA4?.Why ?? "nothing");
    var faA4 = await seenA.WaitFor<FileActivityMsg>(f => f.BridgeId == id4, 15000);
    Check("A's machine reports it did not go — no line saying it was copied", faA4 is { FromMachine: true } && faA4.Error != null, faA4?.Describe("Local Tech") ?? "nothing");
    await Task.Delay(2500);
    var left = PasteFolders().Except(before4).ToList();
    Check("B's half-written copy is removed from its temp", left.Count == 0, left.Count == 0 ? "" : string.Join(", ", left));

    Stop(linkA);
    tech1.Ws.Send(new EndSessionMsg { SessionId = sA });
    await Task.Delay(800);
}

// ------------------------------------------------------------------ plumbing

async Task<string> Login(string email)
{
    var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = "localtest" });
    return JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
}

/// <summary>A pretend tray app. With <paramref name="realHost"/> it starts the REAL host for each session, as the agent
/// does; without, this harness plays an OLDER host itself — sealed nothing, and no word about copying straight across.</summary>
async Task<Peer> Tray(string deviceId, string machine, bool realHost)
{
    var tray = await Peer.Open($"{ws}/ws/device");
    tray.Ws.OnMessage += m =>
    {
        if (m is not StartSessionMsg { HostToken.Length: > 0 } s) return;
        if (realHost)
        {
            var p = Process.Start(new ProcessStartInfo(hostExe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                Arguments = $"--server {ws} --session {s.SessionId} --token {s.HostToken} --consent auto --tech \"Local Tech\""
            });
            if (p != null) hosts.Add(p);
            Log($"the real host for {machine} started (pid {p?.Id})");
        }
        else _ = Task.Run(async () =>
        {
            var old = await Peer.Open($"{ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}");
            old.Ws.Send(new MonitorsMsg { Monitors = new() { new MonitorInfo { Index = 0, Width = 800, Height = 600, Primary = true } } });
            old.Ws.Send(new SysInfoMsg { Info = new() { ["Machine"] = machine } });
        });
    };
    tray.Ws.Send(new HelloMsg
    {
        Role = DeviceRole.Client, DeviceId = deviceId, DeviceKey = deviceId + "-key", MachineName = machine,
        UserName = Environment.UserName, Os = "Windows", Version = "1.0", TenantKey = tenantKey
    });
    return tray;
}

async Task<string> Start(Peer tech, string deviceId)
{

    tech.Ws.Send(new ConnectDeviceMsg { DeviceId = deviceId, Reason = "bridgewire" });
    var ready = await tech.WaitFor<SessionReadyMsg>(s => s.Session.DeviceId == deviceId && s.Session.HostConnected, 25000);
    if (ready == null) throw new Exception($"no session started on {deviceId}");
    return ready.Session.SessionId;
}

(SessionLink, Seen) Link(string sessionId, string token, string deviceId, string machine)
{
    var seen = new Seen();
    var link = new SessionLink(ws, sessionId, token) { DeviceId = deviceId, MachineName = machine, Owner = "", Pins = new MachinePins(pinFile) };
    link.OnMessage += m => seen.Add(m);
    var own = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    linkStops[link] = own;
    _ = link.RunAsync(own.Token);
    return (link, seen);
}

/// <summary>Closes a console's window onto the session: its socket goes, as a closed window's does.</summary>
void Stop(SessionLink link) { if (linkStops.TryGetValue(link, out var c)) c.Cancel(); }

/// <summary>Asks, as the receiving window does; true when the server sends the ask back.</summary>
async Task<bool> Ask(SessionLink link, Seen seen, string id, string fromSession, long size, int items)
{
    link.Send(new BridgeAskMsg { BridgeId = id, FromSessionId = fromSession, Bytes = size, Items = items });
    var answer = await Task.WhenAny(
        seen.WaitFor<BridgeAskMsg>(a => a.BridgeId == id, 8000),
        seen.WaitFor<BridgeEndMsg>(e => e.BridgeId == id, 8000).ContinueWith(_ => (BridgeAskMsg?)null));
    return await answer != null;
}

async Task<string?> Refusal(SessionLink link, Seen seen, string fromSession, long bytes = 100)
{
    var id = Guid.NewGuid().ToString("N");
    link.Send(new BridgeAskMsg { BridgeId = id, FromSessionId = fromSession, Bytes = bytes, Items = 1 });
    var e = await seen.WaitFor<BridgeEndMsg>(x => x.BridgeId == id && x.Refused, 8000);
    if (e == null && seen.All<BridgeAskMsg>().Any(a => a.BridgeId == id)) { link.Send(new BridgeEndMsg { BridgeId = id }); return "AGREED (should have been refused)"; }
    return e?.Why;
}

HashSet<string> PasteFolders() =>
    Directory.Exists(pasteRoot) ? Directory.GetDirectories(pasteRoot).ToHashSet(StringComparer.OrdinalIgnoreCase) : new();

int Count(string sql)
{
    using var c = HarnessDb.Connect($"Data Source={Path.Combine(dataDir, "platform.db")};Mode=ReadOnly;Pooling=False");
    c.Open();
    using var cmd = c.CreateCommand(); cmd.CommandText = HarnessDb.Portable(sql);
    return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
}

byte[] DatabaseBytes()
{
    var all = new List<byte>();
    foreach (var f in Directory.GetFiles(dataDir, "platform.db*"))
        try { using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); var b = new byte[s.Length]; s.ReadExactly(b); all.AddRange(b); } catch { }
    return all.ToArray();
}

static bool Has(byte[] hay, string needle) => hay.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;
static bool Same(string a, string b) => File.Exists(a) && File.Exists(b) && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

static async Task<bool> Until(Func<bool> ok, int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until) { if (ok()) return true; await Task.Delay(50); }
    return ok();
}

/// <summary>The clipboard is read on an STA thread of its own, as Windows requires.</summary>
static T OnSta<T>(Func<T> f)
{
    T result = default!;
    var t = new Thread(() => { try { result = f(); } catch { } });
    t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
    return result;
}

static void ClearClipboard() => OnSta(() => { System.Windows.Forms.Clipboard.Clear(); return true; });

static async Task<List<string>> ReadClipboardFiles(int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        var l = OnSta(() => System.Windows.Forms.Clipboard.ContainsFileDropList()
            ? System.Windows.Forms.Clipboard.GetFileDropList().Cast<string>().ToList() : new List<string>()) ?? new();
        if (l.Count > 0) return l;
        await Task.Delay(200);
    }
    return new();
}

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(psi)!;
    var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit(ms);
    return (p.HasExited ? p.ExitCode : -1, o);
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "src", "RemoteSupport.Platform"))) return d.FullName;
    throw new InvalidOperationException("could not find the repository root");
}

internal sealed class Seen
{
    private readonly List<Msg> _got = new();
    public void Add(Msg m) { lock (_got) _got.Add(m); }
    public List<T> All<T>() where T : Msg { lock (_got) return _got.OfType<T>().ToList(); }
    public bool Has<T>(Func<T, bool> ok) where T : Msg { lock (_got) return _got.OfType<T>().Any(ok); }
    public async Task<T?> WaitFor<T>(Func<T, bool> ok, int ms) where T : Msg
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { lock (_got) { var m = _got.OfType<T>().FirstOrDefault(ok); if (m != null) return m; } await Task.Delay(40); }
        return null;
    }
}

internal sealed class Peer
{
    public WsConnection Ws = null!;
    public ClientWebSocket Socket = null!;
    private readonly Seen _seen = new();

    public Task<T?> WaitFor<T>(Func<T, bool> ok, int ms = 8000) where T : Msg => _seen.WaitFor(ok, ms);
    public static async Task<Peer> Open(string url)
    {
        var p = new Peer { Socket = new ClientWebSocket() };
        await p.Socket.ConnectAsync(new Uri(url), CancellationToken.None);
        p.Ws = new WsConnection(p.Socket);
        p.Ws.OnMessage += m => p._seen.Add(m);
        _ = Task.Run(async () => { try { await p.Ws.RunAsync(); } catch { } });
        return p;
    }
}

