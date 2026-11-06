using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Does a session survive the technician's socket dropping?
//
// The whole point of this harness is the ONE thing no other tool does: it kills a viewer socket
// the way a dropped Wi-Fi packet kills one — ClientWebSocket.Abort(), no close handshake, no
// EndSessionMsg — and then proves the session was still there afterwards. A clean CloseAsync
// would not test anything, because the server sees a tidy goodbye either way.
//
// It measures rather than looking. Tiles are counted over a fixed window before the kill and
// again after the reconnect, and a pass needs BOTH to be non-zero: a session that survives on
// the server but never sends another tile is a session that has not recovered, and it would
// look identical to a working one from a screenshot of a frozen picture.
//
// The three checks, and what each would catch:
//
//   1. DROP AND COME BACK. Abort the socket, dial back into the SAME session id, and require
//      tiles to resume. Catches the old behaviour exactly: the server used to end the session
//      the instant the last viewer's socket closed, so the second connect answered "Session
//      not found".
//   2. LEAVE / END IS STILL IMMEDIATE. Send EndSessionMsg, then dial back in and require to be
//      REFUSED. If a deliberate end were held open for the grace period too, every closed window
//      would leave a host running on a customer's machine for a minute and a half — a disclosure
//      problem, not an untidy one. This check is the one that fails if the grace period is
//      applied too broadly.
//   3. THE GRACE PERIOD ACTUALLY EXPIRES (--grace, and it costs ~100 s). Abort, wait past the
//      window, and require the session to be GONE. Without this, "it survived" could just mean
//      the session now leaks forever, which is worse than the bug being fixed.
//
// Ship trap, from ship-build.ps1 and §18 of the handover: a harness that joins an EXISTING
// session talks to the Host.exe that was running when that session started — straight after a
// ship, the OLD binary. End any open session on the machine before running this.
//
//   dotnet run -c Release --project tools/reconnectcheck/reconnectcheck.csproj -- DESKTOP-OPHQIDH [--grace]
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
string want = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "DESKTOP-OPHQIDH";
bool testGrace = args.Contains("--grace");
const int SettleMs = 6000;      // long enough for an idle desktop to send its forced full frame

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

async Task<T?> Wait<T>(ConcurrentQueue<Msg> q, int ms, Func<T, bool>? f = null) where T : Msg
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

// ---- log in and find the machine --------------------------------------------------------
using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed ({(int)lr.StatusCode}) — is the server running?"); return 2; }
var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

var techWs = new ClientWebSocket();
await techWs.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
var tc = new WsConnection(techWs);
var tq = new ConcurrentQueue<Msg>();
tc.OnMessage += m => tq.Enqueue(m);
_ = tc.RunAsync();

var state = await Wait<StateMsg>(tq, 8000);
var dev = state?.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found on this server"); return 2; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 2; }
Log($"{dev.MachineName} ({dev.DeviceId}) online, unattended={dev.Unattended}");

// ---- a viewer socket, wired the way the console wires one -------------------------------
// Every handler is attached BEFORE RunAsync. The server replays its cached MonitorsMsg within
// milliseconds of the socket opening and it is the ONLY thing that triggers a first subscribe;
// a handler attached after the pump has started misses it for good, and the result is a black
// screen that looks exactly like a reconnect that failed. (§10 of the handover, an hour lost.)
async Task<Viewer?> OpenViewerAsync(string sessionId, int timeoutMs = 8000)
{
    var v = new Viewer { Raw = new ClientWebSocket() };
    try { await v.Raw.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={tok}"), new CancellationTokenSource(timeoutMs).Token); }
    catch (Exception ex) { Log($"viewer connect refused at the HTTP level: {ex.GetType().Name}"); return null; }

    v.Conn = new WsConnection(v.Raw);
    v.Conn.OnMessage += m =>
    {
        v.Q.Enqueue(m);
        if (m is MonitorsMsg mm) v.Monitors = mm;
        // A session that no longer exists answers with a session end rather than relaying
        // anything. That is the difference between "not yet" and "never again", and a retry
        // loop that cannot tell them apart hammers the server about something that is over.
        if (m is SessionEndedMsg se) { v.Refused = true; v.RefusedWhy = se.Reason; }
        if (m is ErrorMsg e) { v.Refused = true; v.RefusedWhy = e.Message; }
    };
    v.Conn.OnBinary += b =>
    {
        var s = b.Span;
        if (s.Length < 9 || s[0] != BinKind.Tiles) return;
        v.Frames++;
        v.Tiles += BitConverter.ToInt32(s.Slice(5, 4));
    };
    _ = v.Conn.RunAsync();
    return v;
}

/// <summary>Subscribe the way SwitchMonitor does. On=true sets _forceFull on the host, which is
/// the only way to demand a complete frame from an idle desktop — without it a machine where
/// nothing is moving sends nothing at all and "no tiles" means nothing.</summary>
void Subscribe(Viewer v, int monitor) => v.Conn.Send(new SubscribeMsg { Monitor = monitor, On = true });

// ---- start a session --------------------------------------------------------------------
Console.WriteLine();
Log("starting a session");
tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "reconnectcheck diagnostic" });
var ready = await Wait<SessionReadyMsg>(tq, 25000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session started"); return 2; }
var sessionId = ready.Session.SessionId;
Log($"session {sessionId} (unattended={ready.Session.Unattended})");

var v1 = await OpenViewerAsync(sessionId);
if (v1 == null) { Console.WriteLine("could not open the first viewer socket"); return 2; }

// Wait for evidence of the host before asking it for anything: a new session has no host for a
// second or two, and the hub relays a viewer's message with s.Host?.Send(m), a silent no-op.
var mons = await Wait<MonitorsMsg>(v1.Q, 25000);
if (mons == null || mons.Monitors.Count == 0) { Console.WriteLine("the host never announced its monitors — is a stale session still open on that machine?"); return 2; }
int monitor = mons.Monitors[0].Index;
Log($"host is up, {mons.Monitors.Count} monitor(s); watching monitor {monitor}");

Subscribe(v1, monitor);
await Task.Delay(SettleMs);
long tilesBefore = v1.Tiles, framesBefore = v1.Frames;
Log($"before the drop: {framesBefore} frames, {tilesBefore} tiles");
Check("tiles were arriving before the socket was killed", tilesBefore > 0, $"{framesBefore} frames / {tilesBefore} tiles in {SettleMs / 1000}s");
if (tilesBefore == 0)
{
    Console.WriteLine();
    Console.WriteLine("Nothing arrived even before the drop, so there is nothing to prove recovery against.");
    Console.WriteLine("Most likely a stale session on that machine, hosted by an older Host.exe. End it and run again.");
    return 2;
}

// =========================================================================================
// CHECK 1 — the socket dies the way Wi-Fi kills one, and the session survives it
// =========================================================================================
Console.WriteLine();
Log("CHECK 1: killing the viewer socket with Abort() — no close frame, no EndSessionMsg");
v1.Raw.Abort();                      // this is the whole point: an abort, not a CloseAsync
await Task.Delay(1500);

// Does the SERVER still have it? Asked by dialling back in, which is the question that actually
// matters — a session that exists but will not accept its technician back is no use.
var v2 = await OpenViewerAsync(sessionId);
Check("a viewer socket could be opened to the same session id after the drop", v2 != null);
if (v2 == null) { Console.WriteLine("\nThe session was destroyed by the dropped socket — this is the old behaviour."); goto done; }

await Task.Delay(800);
Check("the server did NOT refuse the reconnecting viewer", !v2.Refused, v2.Refused ? v2.RefusedWhy : "accepted back into the session it left");
if (v2.Refused) goto done;

// The replay is what makes joining late work, and a reconnect depends on it.
var mons2 = await Wait<MonitorsMsg>(v2.Q, 8000);
Check("the monitor list was replayed to the reconnected viewer", mons2 != null,
      mons2 != null ? $"{mons2.Monitors.Count} monitor(s)" : "nothing arrived — a reconnected viewer with no MonitorsMsg is a permanently black screen");

// And the measurement the brief asks for: frames counted after, not "it looked fine".
Subscribe(v2, monitor);
await Task.Delay(SettleMs);
Log($"after the reconnect: {v2.Frames} frames, {v2.Tiles} tiles");
Check("tiles RESUMED after the reconnect", v2.Tiles > 0, $"{v2.Frames} frames / {v2.Tiles} tiles in {SettleMs / 1000}s");

// The host is the other half: a session kept alive on the server with a dead host would pass
// everything above and be useless. Tiles arriving IS the host being alive, so this asks it
// something it has to answer itself.
v2.Conn.Send(new AudioDevicesRequestMsg());
var devices = await Wait<AudioDevicesMsg>(v2.Q, 8000);
Check("the HOST is still the same live process and still answering", devices != null,
      devices != null ? $"answered with {devices.Devices.Count} audio endpoint(s)" : "no answer — the host may have been closed with the session");

// =========================================================================================
// CHECK 2 — a deliberate end must STILL end it at once
// =========================================================================================
Console.WriteLine();
Log("CHECK 2: sending EndSessionMsg — a deliberate end must not get the grace period");
v2.Conn.Send(new EndSessionMsg { SessionId = sessionId });
await Task.Delay(2500);

var v3 = await OpenViewerAsync(sessionId, 5000);
if (v3 == null) Check("the session was gone immediately after Leave / End", true, "the socket was refused outright");
else
{
    await Task.Delay(1200);
    Check("the session was gone immediately after Leave / End", v3.Refused,
          v3.Refused ? v3.RefusedWhy : "a viewer was let back in — a deliberate end is being held open like a dropped socket, "
                                     + "which leaves a host running on the customer's machine after the technician has gone");
    try { v3.Raw.Abort(); } catch { }
}

// =========================================================================================
// CHECK 3 — the grace period expires, so a held session does not leak forever (--grace)
// =========================================================================================
if (testGrace)
{
    Console.WriteLine();
    Log("CHECK 3: starting a second session, dropping it, and waiting out the grace period");
    tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "reconnectcheck diagnostic" });
    var ready2 = await Wait<SessionReadyMsg>(tq, 25000, s => s.Session.DeviceId == dev.DeviceId && s.Session.SessionId != sessionId);
    if (ready2 == null) Check("a second session could be started for the grace test", false, "none started");
    else
    {
        var id2 = ready2.Session.SessionId;
        var g1 = await OpenViewerAsync(id2);
        if (g1 == null) Check("a viewer joined the second session", false);
        else
        {
            await Wait<MonitorsMsg>(g1.Q, 25000);
            Log($"session {id2} up; aborting its viewer and waiting 100 s");
            // Only messages that arrive AFTER the abort count. StartSession broadcasts its own
            // SessionReadyMsg before the host has connected — host absent, technicians empty —
            // which is indistinguishable from the one EndSession sends, and would read as "it had
            // already ended" every single time. ConcurrentQueue enumerates FIFO in a snapshot, so
            // a count taken here is a sound watermark.
            int mark = tq.Count;
            g1.Raw.Abort();

            // Watched PASSIVELY, on the technician dashboard socket we are already holding.
            //
            // This is the trap this check sprang on itself the first time it ran against real
            // hardware, and it is worth knowing about: a viewer that connects to ask whether the
            // session is still alive REJOINS it, which correctly cancels the hold — and aborting
            // that probe then starts a FRESH ninety seconds. The check duly reported a leak at
            // 100 s, having moved the deadline to 120 s itself. The session was behaving exactly
            // as designed. (tools/reconnectlocal hit the identical thing and asks the host socket
            // instead; over the wire that socket is on the customer's machine and out of reach,
            // so the dashboard is the passive observer available here.)
            //
            // The hub broadcasts a SessionReadyMsg on both transitions: BeginGrace sends one with
            // Held = true, and EndSession sends one with the technicians cleared and the host
            // gone. Reading those costs nothing and touches nothing.
            bool Ended() => tq.Skip(mark).OfType<SessionReadyMsg>().Any(m => m.Session.SessionId == id2
                                                                            && !m.Session.HostConnected
                                                                            && m.Session.Technicians.Count == 0);
            bool Held() => tq.Skip(mark).OfType<SessionReadyMsg>().Any(m => m.Session.SessionId == id2 && m.Session.Held);

            await Task.Delay(30000);
            Check("30 s after the drop the session was still being held", !Ended(),
                  Ended() ? "it had already ended" : Held() ? "the server announced it as held, and has not ended it" : "not ended yet");

            await Task.Delay(70000);
            bool gone = Ended();
            Check("100 s after the drop the session had ended on its own", gone,
                  gone ? "the grace period expired and cleaned up" : "the session is still alive — a held session that never ends leaks a host on the customer's machine");
            // Only now is it safe to ask with a viewer: there is nothing left to accidentally revive.
            var late = await OpenViewerAsync(id2, 5000);
            bool refused = late == null;
            if (late != null) { await Task.Delay(1200); refused = late.Refused; try { late.Raw.Abort(); } catch { } }
            Check("...and a viewer can no longer rejoin it", refused,
                  refused ? "refused" : "it let a viewer back into a session that should be over");
            if (!gone) tc.Send(new EndSessionMsg { SessionId = id2 });
        }
    }
}

done:
try { v1.Raw.Abort(); } catch { }
// Never leave a session running behind us. A harness that leaks one sends the next person to
// the wrong file when their own run reports "the host never announced its monitors".
tc.Send(new EndSessionMsg { SessionId = sessionId });
await Task.Delay(600);

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
if (!testGrace) Console.WriteLine("(--grace also proves the held session ends on its own; it takes about 100 seconds.)");
return fail == 0 ? 0 : 1;

/// <summary>One viewer socket and what arrived on it. Kept as a type so a dropped socket and
/// the one that replaces it can be counted separately — the whole measurement is tiles before
/// against tiles after.</summary>
sealed class Viewer
{
    public ClientWebSocket Raw = null!;
    public WsConnection Conn = null!;
    public long Tiles, Frames;
    public MonitorsMsg? Monitors;
    public bool Refused;
    public string RefusedWhy = "";
    public readonly ConcurrentQueue<Msg> Q = new();
}
