using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// Does a session survive a dropped socket? Answered on THIS machine alone — no customer machine,
// no live server, nothing installed anywhere.
//
// It runs its own copy of the platform on port 8099 against a throwaway database in the temp
// folder, and pretends to be a customer machine: a device connection that accepts a session, a
// host socket that announces a monitor and sends tile frames, and viewer sockets that are killed
// the way Wi-Fi kills one. Clint's live server on 8090 and his real database are never touched.
//
// Why this exists as well as tools/reconnectcheck: the thing being tested is the SERVER's
// session lifetime, and everything a real machine adds — SYSTEM, desktops, capture, UIPI — is
// irrelevant to it. Same argument as tools/audiolocal and tools/filesetcheck: whatever is
// genuinely hard here is hard here rather than on the wire, and this is where to look first.
// reconnectcheck then proves it over real hardware, which this deliberately cannot.
//
//   dotnet run -c Release --project tools/reconnectlocal/reconnectlocal.csproj -- [--server <dir>] [--grace]
//
// --server is the folder holding RemoteSupport.Platform.exe (it is built here if not given).
// --grace also waits out the 90-second hold, which takes about a hundred seconds.
// ---------------------------------------------------------------------------------------------

const int Port = 8099;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";
bool testGrace = args.Contains("--grace");
string? serverDir = null;
for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--server") serverDir = args[i + 1];

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-reconnectlocal-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(work);
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);

if (serverDir == null)
{
    // Built to a folder of our own on purpose: Clint runs the live server out of the project's
    // own bin, and it holds RemoteSupport.Shared.dll while it runs — building there fails
    // fifteen seconds in with an error about a file copy rather than about the server.
    serverDir = Path.Combine(work, "server");
    Log($"building the platform into {serverDir} (the live server's bin is left alone)");
    var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
    if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine("the platform would not build"); return 2; }
}
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
if (!File.Exists(exe)) { Console.WriteLine($"no RemoteSupport.Platform.exe in {serverDir}"); return 2; }

var env = new Dictionary<string, string>
{
    ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false",
    ["RSP_Platform__PublicUrl"] = http,
    ["ASPNETCORE_URLS"] = http,
    ["ASPNETCORE_ENVIRONMENT"] = "Development",
};
HarnessDb.Use(env, dataDir);

// ---- a throwaway tenant and technician, in a throwaway database --------------------------
Log("creating a throwaway tenant in " + dataDir);
string tenantKey = "";
// The console's token: its session windows use the same one, as the real console's do. Signing in again
// for each viewer would close the console that started the session (one at a time, HANDOVER §82).
string consoleToken = "";
foreach (var cmd in new[]
{
    "create-tenant \"Reconnect Local\" reconnect-local 5",
    "approve reconnect-local",
    "add-tech reconnect-local tech@local.test localtest \"Local Tech\" --admin",
})
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    // The tenant key is what authenticates a device to a company. A device that presents none is
    // refused at /ws/device before the hub ever sees it — the installer bakes it in, so a test
    // machine has to as well.
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key from create-tenant"); return 2; }

// ---- start it ---------------------------------------------------------------------------
Log($"starting the platform on {http}");
// --urls on the command line, not ASPNETCORE_URLS. appsettings.json carries "Urls": 0.0.0.0:8090
// and the JSON provider is added AFTER the environment one, so the env var loses and this rig
// would try to bind the port Clint's live server is already on. Command-line args are added last
// and win. Getting this wrong is not harmless: it fails by colliding with the live server.
var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
var server = Process.Start(psi)!;
var serverLog = new ConcurrentQueue<string>();
server.OutputDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.ErrorDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.BeginOutputReadLine(); server.BeginErrorReadLine();

using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
bool up = false;
for (int i = 0; i < 60 && !up; i++)
{
    try { up = (await hc.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { await Task.Delay(500); }
}
if (!up) { Console.WriteLine(string.Join("\n", serverLog)); Console.WriteLine("the platform did not come up"); Kill(); return 2; }
Log("platform is up");

try
{
    var exit = await RunChecksAsync();
    Console.WriteLine();
    Console.WriteLine($"{pass} of {pass + fail} checks passed.");
    if (!testGrace) Console.WriteLine("(--grace also proves the held session ends on its own; it takes about 100 seconds.)");
    Console.WriteLine($"server log and database kept at {work}");
    return exit;
}
finally { Kill(); }

void Kill() { try { if (!server.HasExited) server.Kill(true); } catch { } }

// =============================================================================================
async Task<int> RunChecksAsync()
{
    var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = "tech@local.test", password = "localtest" });
    if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed ({(int)lr.StatusCode})"); return 2; }
    var tok = consoleToken = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

    var techWs = new ClientWebSocket();
    await techWs.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
    var tc = new WsConnection(techWs);
    var tq = new ConcurrentQueue<Msg>();
    tc.OnMessage += m => tq.Enqueue(m);
    _ = tc.RunAsync();
    if (await Wait<StateMsg>(tq, 8000) == null) { Console.WriteLine("no state from the tech socket"); return 2; }

    // ---- the pretend customer machine ----------------------------------------------------
    var machine = new FakeMachine(ws, "recon-local-device", "recon-local-key", "FAKE-RECONNECT-PC", tenantKey);
    await machine.ConnectAsync();
    Log("fake machine connected as a device");

    var st = await Wait<StateMsg>(tq, 3000);
    // Its arrival is announced separately; give the hub a beat and ask again.
    await Task.Delay(700);
    var devId = machine.DeviceId;

    Console.WriteLine();
    Log("starting a session on the fake machine");
    tc.Send(new ConnectDeviceMsg { DeviceId = devId, Reason = "reconnectlocal diagnostic" });
    var ready = await Wait<SessionReadyMsg>(tq, 15000, s => s.Session.DeviceId == devId);
    if (ready == null) { Console.WriteLine("no session started"); return 2; }
    var sessionId = ready.Session.SessionId;
    Log($"session {sessionId}");
    if (!await machine.WaitForHostAsync(10000)) { Console.WriteLine("the fake host never connected"); return 2; }

    // The customer's tray app state. It is a disclosure feature, so it gets checked rather than
    // assumed: the person at the machine is the only one who cannot see what is happening to it.
    var v1 = await OpenViewer(sessionId);
    if (v1 == null) { Console.WriteLine("could not open a viewer"); return 2; }
    var mons = await Wait<MonitorsMsg>(v1.Q, 8000);
    Check("the monitor list reached the first viewer", mons != null);
    v1.Conn.Send(new SubscribeMsg { Monitor = 0, On = true });
    await Task.Delay(1500);
    long before = v1.Tiles;
    Check("tiles were arriving before the socket was killed", before > 0, $"{v1.Frames} frames / {before} tiles");

    // =====================================================================================
    Console.WriteLine();
    Log("CHECK 1: killing the viewer socket with Abort() — no close frame, no EndSessionMsg");
    machine.States.Clear();
    v1.Raw.Abort();
    await Task.Delay(1500);

    Check("the customer's tray app was told the session is being HELD", machine.States.Any(s => s.Active && s.Held),
          machine.States.Count == 0 ? "nothing was sent to the device at all" : Describe(machine.States));
    Check("the HOST was not closed when the viewer dropped", machine.HostOpen,
          machine.HostOpen ? "still connected" : "the server closed it — the session was destroyed by the drop");

    // Cleared BEFORE the reconnect, not after: the "technician is back" notification is sent the
    // instant the viewer socket is accepted, so clearing afterwards throws away the very thing
    // being looked for. The first version of this harness did exactly that and reported a fault
    // the server did not have — which is worse than no harness, because it sends the next person
    // to the wrong file (§18 of the handover, the clock-drift number that was the harness lying).
    machine.States.Clear();
    var v2 = await OpenViewer(sessionId);
    Check("a viewer could rejoin the same session id after the drop", v2 != null && !v2.Refused,
          v2 == null ? "the socket was refused outright" : v2.Refused ? v2.RefusedWhy : "accepted back into the session it left");
    if (v2 == null || v2.Refused) return 1;

    var mons2 = await Wait<MonitorsMsg>(v2.Q, 8000);
    Check("the monitor list was replayed to the reconnected viewer", mons2 != null,
          mons2 != null ? "replayed" : "nothing — a reconnected viewer with no MonitorsMsg is a permanently black screen");

    v2.Conn.Send(new SubscribeMsg { Monitor = 0, On = true });
    await Task.Delay(1500);
    Check("tiles RESUMED after the reconnect", v2.Tiles > 0, $"{v2.Frames} frames / {v2.Tiles} tiles");
    Check("the customer's tray app was told the technician is back", machine.States.Any(s => s.Active && !s.Held),
          machine.States.Count == 0 ? "nothing was sent" : Describe(machine.States));

    // =====================================================================================
    Console.WriteLine();
    Log("CHECK 2: the customer's own socket blinking mid-session (BUGS #2)");
    machine.States.Clear();
    await machine.DropAndReconnectDeviceAsync();
    var resync = await WaitFor(() => machine.States.Count > 0, 8000);
    Check("a device that reconnected mid-session was told the session state", resync,
          resync ? Describe(machine.States) : "nothing was sent — this is the shape of BUGS #2: DeviceLink reconnects and nothing re-sends state");
    Check("...and it was told the session is ACTIVE, not that nothing is happening",
          machine.States.Any(s => s.Active && s.SessionId == sessionId), Describe(machine.States));

    // =====================================================================================
    Console.WriteLine();
    Log("CHECK 3: Leave / End must STILL end the session at once");
    machine.States.Clear();
    v2.Conn.Send(new EndSessionMsg { SessionId = sessionId });
    await Task.Delay(2000);
    Check("the host was closed by a deliberate end", !machine.HostOpen);
    var v3 = await OpenViewer(sessionId, 4000);
    bool gone = v3 == null;
    if (v3 != null) { await Task.Delay(1000); gone = v3.Refused; try { v3.Raw.Abort(); } catch { } }
    Check("the session was gone immediately after Leave / End", gone,
          gone ? "refused straight away" : "a viewer was let back in — a deliberate end is being held like a dropped socket, "
               + "which leaves a host running on the customer's machine after the technician has gone");
    Check("the customer's tray app was told the session ended", machine.Ended > 0 || machine.States.Any(s => !s.Active));

    // =====================================================================================
    // The REPORTED symptom of BUGS #2, not just the mechanism. The bug is a tray app left saying
    // "support in progress" after a session ended, and the narrow gap that causes it is this: the
    // device's socket is DOWN at the moment EndSession fires, so it misses the SessionEndedMsg —
    // and nothing ever told it again. A device that happened to be connected when the session
    // ended was always told correctly, which is exactly why the bug was intermittent and was
    // never reproduced on purpose.
    //
    // It runs AFTER check 3 because a machine can only hold one session at a time: starting a
    // second one just hands back the first.
    Console.WriteLine();
    Log("CHECK 2b: reproducing BUGS #2 — the tray app is offline at the exact moment the session ends");
    tc.Send(new ConnectDeviceMsg { DeviceId = devId, Reason = "reconnectlocal diagnostic" });
    var rb = await Wait<SessionReadyMsg>(tq, 15000, s => s.Session.DeviceId == devId && s.Session.SessionId != sessionId);
    if (rb == null) Check("a session could be started for the BUGS #2 reproduction", false);
    else
    {
        await machine.WaitForHostAsync(10000);
        var vb = await OpenViewer(rb.Session.SessionId);
        await Wait<MonitorsMsg>(vb!.Q, 8000);

        machine.KillDeviceSocket();                                              // the tray app's socket blinks...
        await Task.Delay(600);
        vb.Conn.Send(new EndSessionMsg { SessionId = rb.Session.SessionId });     // ...the session ends while it is down
        await Task.Delay(1500);
        machine.States.Clear(); machine.ResetEnded();
        await machine.ConnectAsync();                                            // ...and it comes back still believing support is in progress
        var told = await WaitFor(() => machine.States.Any(s => !s.Active) || machine.Ended > 0, 8000);
        Check("a tray app that was offline when the session ended is told so when it comes back", told,
              told ? "it was sent active=false as soon as it reconnected" : "it was told nothing — it would still be showing \"support in progress\"");
        try { vb.Raw.Abort(); } catch { }
        await Task.Delay(500);
    }

    // =====================================================================================
    // A refused machine must be told WHY, and must stop hammering.
    //
    // This is the fault that cost an afternoon: a tenant whose trial lapses turns away every one
    // of its machines at once, and nothing anywhere said so. The server logged nothing, and
    // DeviceLink had no ErrorMsg case at all, so the reason arrived and was dropped. From the
    // customer's chair it was indistinguishable from the network being down; from the server's it
    // was indistinguishable from nobody trying.
    //
    // Suspending the tenant is the same branch a lapsed trial takes — both make IsServiceEnabled
    // false — and it is the one a CLI command can reach.
    Console.WriteLine();
    Log("CHECK 5: a machine the server refuses is told why");
    var susp = Run(exe, "set-brand reconnect-local status Suspended", serverDir, 60000, env);
    if (susp.code != 0) { Console.WriteLine(susp.output); Check("the tenant could be suspended for the refusal test", false); }
    else
    {
        machine.Errors.Clear();
        await machine.DropAndReconnectDeviceAsync();
        var told = await WaitFor(() => machine.Errors.Count > 0, 10000);
        Check("a refused machine is told why, in words", told,
              told ? "\"" + machine.Errors.First() + "\"" : "it was turned away in silence — the customer could only report \"it stopped working\"");
        Check("...and the reason names the company rather than saying \"not active\"",
              machine.Errors.Any(e => e.Contains("Reconnect Local", StringComparison.OrdinalIgnoreCase)),
              machine.Errors.Count > 0 ? machine.Errors.First() : "nothing said");

        // Put it back, or every later check in this run fails for the wrong reason.
        var back = Run(exe, "set-brand reconnect-local status Active", serverDir, 60000, env);
        Check("the tenant could be reactivated", back.code == 0);
        await machine.DropAndReconnectDeviceAsync();
        // DeviceLink backs off to 30 s after a refusal, so this reconnect is the manual one the
        // harness just did rather than a retry; what matters is that the server accepts it again.
        var backOn = await WaitFor(() => machine.HelloAccepted, 12000);
        Check("a machine is accepted again once the tenant is active", backOn,
              backOn ? "it reconnected" : "still refused after reactivating");

        // The other half of the fix, and the half nobody would notice was missing until they
        // needed it: the SERVER has to say so too. A refusal the customer can read but that
        // leaves no trace on the server is still undiagnosable from the only place anyone
        // troubleshoots from.
        var logged = serverLog.FirstOrDefault(l => l.Contains("REFUSED", StringComparison.Ordinal));
        Check("the server logged the refusal", logged != null,
              logged != null ? logged.Trim() : "nothing in the server's output mentions it — it is invisible from the one place anyone looks");
        Check("...and the log line says which machine and why",
              logged != null && logged.Contains("FAKE-RECONNECT-PC") && logged.Contains("Suspended"),
              logged ?? "");
    }

    // =====================================================================================
    if (testGrace)
    {
        Console.WriteLine();
        Log("CHECK 4: a held session must end ON ITS OWN when nobody comes back");
        machine.ResetEnded();
        tc.Send(new ConnectDeviceMsg { DeviceId = devId, Reason = "reconnectlocal diagnostic" });
        // Not the earlier sessions: SessionReadyMsg for those is still sitting in the queue, and
        // matching one of them would test nothing while looking like a pass.
        var seen = new[] { sessionId, rb?.Session.SessionId ?? "" };
        var r2 = await Wait<SessionReadyMsg>(tq, 15000, s => s.Session.DeviceId == devId && !seen.Contains(s.Session.SessionId));
        if (r2 == null) Check("a second session could be started", false);
        else
        {
            await machine.WaitForHostAsync(10000);
            var g = await OpenViewer(r2.Session.SessionId);
            await Wait<MonitorsMsg>(g!.Q, 8000);
            Log("aborting it and waiting; the hold is 90 s");
            g.Raw.Abort();

            // Probed through the HOST socket, not by opening a viewer.
            //
            // This is the trap this test sprang on itself, and it is worth knowing about: a
            // viewer that connects to check the session is alive REJOINS it, which correctly
            // cancels the hold — and aborting that probe then starts a FRESH ninety seconds. The
            // first version checked at 30 s with a viewer and then declared a leak at 100 s,
            // because its own probe had moved the deadline to 120 s. The session was behaving
            // exactly as designed.
            //
            // The host socket answers the same question and touches nothing: EndSession closes
            // it, so "is the host still connected" IS "does the session still exist".
            await Task.Delay(30000);
            Check("30 s after the drop it was still being held", machine.HostOpen,
                  machine.HostOpen ? "the host is still connected, so the session is still there" : "the host was already closed");

            await Task.Delay(70000);
            Check("100 s after the drop the hold had expired on its own", !machine.HostOpen,
                  !machine.HostOpen ? "the host was closed when the hold ran out" : "the host is still connected — a held session that never ends leaks a host on the customer's machine");
            // And the session is really gone from the registry, not merely hostless. Safe to ask
            // with a viewer now: there is nothing left for it to accidentally revive.
            var late = await OpenViewer(r2.Session.SessionId, 4000);
            bool ended = late == null;
            if (late != null) { await Task.Delay(1000); ended = late.Refused; try { late.Raw.Abort(); } catch { } }
            Check("...and a viewer can no longer rejoin it", ended, ended ? "refused" : "it let a viewer back into a session that should be over");
            Check("the customer's tray app was told it ended", machine.Ended > 0);
        }
    }

    try { v1.Raw.Abort(); } catch { }
    return fail == 0 ? 0 : 1;
}

string Describe(IEnumerable<SessionStateMsg> states) =>
    string.Join(", ", states.Select(s => $"active={s.Active} held={s.Held}"));

async Task<bool> WaitFor(Func<bool> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (f()) return true; await Task.Delay(50); } return false; }

async Task<T?> Wait<T>(ConcurrentQueue<Msg> q, int ms, Func<T, bool>? f = null) where T : Msg
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); } return null; }

async Task<Viewer?> OpenViewer(string sessionId, int timeoutMs = 8000)
{
    var tok = consoleToken;
    var v = new Viewer { Raw = new ClientWebSocket() };
    try { await v.Raw.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={tok}"), new CancellationTokenSource(timeoutMs).Token); }
    catch { return null; }
    v.Conn = new WsConnection(v.Raw);
    // Attached before RunAsync. The replayed MonitorsMsg lands within milliseconds and is gone
    // for good if nothing is listening — see §10 of the handover.
    v.Conn.OnMessage += m =>
    {
        v.Q.Enqueue(m);
        if (m is SessionEndedMsg se) { v.Refused = true; v.RefusedWhy = se.Reason; }
        if (m is ErrorMsg e) { v.Refused = true; v.RefusedWhy = e.Message; }
    };
    v.Conn.OnBinary += b => { var s = b.Span; if (s.Length >= 9 && s[0] == BinKind.Tiles) { v.Frames++; v.Tiles += BitConverter.ToInt32(s.Slice(5, 4)); } };
    _ = v.Conn.RunAsync();
    return v;
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

sealed class Viewer
{
    public ClientWebSocket Raw = null!;
    public WsConnection Conn = null!;
    public long Tiles, Frames;
    public bool Refused;
    public string RefusedWhy = "";
    public readonly ConcurrentQueue<Msg> Q = new();
}

/// <summary>
/// A pretend customer machine: the device connection the tray app holds, and the host socket the
/// spawned Host.exe holds. It sends one tile per frame of solid colour — the content is
/// irrelevant, the question is whether frames arrive at all, and a real JPEG would only add a
/// dependency on System.Drawing to a test about session lifetime.
/// </summary>
sealed class FakeMachine
{
    private readonly string _ws;
    public string DeviceId { get; }
    private readonly string _key, _name, _tenantKey;
    private WsConnection? _device;
    private ClientWebSocket? _deviceRaw;
    private WsConnection? _host;
    private ClientWebSocket? _hostRaw;
    private CancellationTokenSource _hostCts = new();
    private readonly TaskCompletionSource _hostUp = new();

    public readonly ConcurrentBag<SessionStateMsg> States = new();
    /// <summary>Refusals the server sent, in its own words.</summary>
    public readonly ConcurrentBag<string> Errors = new();
    /// <summary>Set once the server has actually taken this device — proved by it sending
    /// anything at all that is not a refusal. A refused socket is closed immediately.</summary>
    public bool HelloAccepted;
    public int Ended;
    public void ResetEnded() => Ended = 0;
    public bool HostOpen => _hostRaw?.State == WebSocketState.Open;

    public FakeMachine(string ws, string deviceId, string key, string name, string tenantKey)
    { _ws = ws; DeviceId = deviceId; _key = key; _name = name; _tenantKey = tenantKey; }

    public async Task ConnectAsync()
    {
        HelloAccepted = false;
        _deviceRaw = new ClientWebSocket();
        await _deviceRaw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _device = new WsConnection(_deviceRaw);
        _device.OnMessage += m =>
        {
            switch (m)
            {
                case StartSessionMsg s when !string.IsNullOrEmpty(s.HostToken): HelloAccepted = true; _ = StartHostAsync(s); break;
                case SessionStateMsg ss: HelloAccepted = true; States.Add(ss); break;
                case SessionEndedMsg: HelloAccepted = true; Interlocked.Increment(ref Ended); break;
                case ErrorMsg e: Errors.Add(e.Message); break;
            }
        };
        _ = _device.RunAsync();
        _device.Send(new HelloMsg
        {
            Role = DeviceRole.Client, DeviceId = DeviceId, DeviceKey = _key,
            MachineName = _name, UserName = "FakeUser", Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
        });
        await Task.Delay(400);
    }

    /// <summary>Kill the DEVICE socket and bring it back — the customer's tray app blinking, which
    /// is the case BUGS #2 turns on.</summary>
    public async Task DropAndReconnectDeviceAsync()
    {
        KillDeviceSocket();
        await Task.Delay(800);
        await ConnectAsync();
    }

    /// <summary>Kill the device socket and leave it down, so something can happen while the tray
    /// app is not there to hear about it.</summary>
    public void KillDeviceSocket() { try { _deviceRaw?.Abort(); } catch { } }

    public async Task<bool> WaitForHostAsync(int ms)
    {
        var e = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < e) { if (HostOpen) return true; await Task.Delay(50); }
        return false;
    }

    private async Task StartHostAsync(StartSessionMsg s)
    {
        try
        {
            _hostCts = new CancellationTokenSource();
            _hostRaw = new ClientWebSocket();
            await _hostRaw.ConnectAsync(new Uri($"{_ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}"), default);
            _host = new WsConnection(_hostRaw);
            bool sending = false;
            _host.OnMessage += m =>
            {
                // On=true is what sets _forceFull on the real host; here it is simply what turns
                // the stream on, so a viewer that never subscribes correctly sees no tiles.
                if (m is SubscribeMsg sub && sub.On && !sending) { sending = true; _ = SendTilesAsync(_hostCts.Token); }
                // What the real host does since 2026-09-29 (3.5.3): told the session is over, it
                // says its last and closes itself. The server only closes a host that does not,
                // after HostLastWords — tools/clipledger checks that older host separately.
                if (m is SessionEndedMsg) _ = _host?.CloseAsync("ended");
            };
            _ = _host.RunAsync(_hostCts.Token);
            _host.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 256, Height = 256, Primary = true } } });
            _host.Send(new SysInfoMsg { Info = new Dictionary<string, string> { ["Machine"] = _name } });
            _hostUp.TrySetResult();
        }
        catch (Exception ex) { Console.WriteLine("fake host failed: " + ex.Message); }
    }

    private async Task SendTilesAsync(CancellationToken ct)
    {
        // [kind][monitor:i32][count:i32] then per tile [x][y][w][h][len][bytes] — BinKind.Tiles,
        // the same framing ScreenView reads. The payload is not a real JPEG; nothing in this test
        // decodes it, and the count in the header is what is measured.
        while (!ct.IsCancellationRequested && HostOpen)
        {
            var body = new byte[9 + 20 + 4];
            body[0] = BinKind.Tiles;
            BitConverter.GetBytes(0).CopyTo(body, 1);
            BitConverter.GetBytes(1).CopyTo(body, 5);
            int o = 9;
            foreach (var v in new[] { 0, 0, 128, 128, 4 }) { BitConverter.GetBytes(v).CopyTo(body, o); o += 4; }
            try { _host?.SendBinary(body); } catch { }
            try { await Task.Delay(120, ct); } catch { return; }
        }
    }
}
