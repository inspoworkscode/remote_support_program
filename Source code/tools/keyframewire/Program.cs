using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// keyframewire - a video frame the server drops on its way to a viewer asks the host for a
// complete picture (2026-10-15, HANDOVER §142).
//
// Key frames were every 24 frames, and that quietly repaired a viewer whose picture the server had
// broken by dropping a frame to it (WsConnection drops screen frames for a viewer more than 2 MB
// behind). They are a minute apart now, so the hub asks: KeyFrameRequestMsg, at most once a second
// per session, for VIDEO only — a dropped tile is one stale rectangle and sound just has a gap.
//
// Its own platform on port 8171 against a throwaway database, a pretend customer machine, a viewer
// that reads everything and a viewer that stops reading:
//
//   1. both viewers keeping up: a stream of video asks for nothing;
//   2. the slow viewer far behind on TILES: still nothing;
//   3. the same viewer behind on VIDEO: the host is asked, no more than once a second, and the
//      healthy viewer goes on receiving;
//   4. the rule itself, in-process: a SEALED video frame counts too (the hub cannot see inside it).
//
// Clint's live server and his database are never touched.
//
//   dotnet run -c Release --project tools/keyframewire/keyframewire.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8171;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("keyframewire - a frame dropped on its way to a viewer asks for a complete picture (§142)");
Console.WriteLine();

// =============================================================================================
Console.WriteLine("== the rule ==");
Check("a video frame lost needs a key frame", BinaryRelay.LossNeedsKeyFrame(new byte[] { BinKind.Video, 0 }));
Check("...a SEALED video frame too — the hub sees only its kind", BinaryRelay.LossNeedsKeyFrame(new byte[] { (byte)(BinKind.Video | BinKind.SealedBit), 0 }));
Check("a tile frame lost does not", !BinaryRelay.LossNeedsKeyFrame(new byte[] { BinKind.Tiles, 0 }));
Check("...nor sound", !BinaryRelay.LossNeedsKeyFrame(new byte[] { BinKind.Audio, 0 }) && !BinaryRelay.LossNeedsKeyFrame(new byte[] { (byte)(BinKind.Audio | BinKind.SealedBit) }));
Check("the request is one the server may send in a sealed session", SessionSeal.StaysClear(new KeyFrameRequestMsg()));
Check("...and it travels by name", Json.Serialize<Msg>(new KeyFrameRequestMsg()).Contains("\"key_frame_request\""));

// =============================================================================================
Console.WriteLine();
Console.WriteLine("== the wire ==");
var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-keyframewire-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
Log($"building the platform into {serverDir}");
var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine("the platform would not build"); return 2; }
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");

var env = new Dictionary<string, string>
{
    ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false",
    ["RSP_Platform__PublicUrl"] = http,
    ["ASPNETCORE_URLS"] = http,
    ["ASPNETCORE_ENVIRONMENT"] = "Development",
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[]
{
    "create-tenant \"Key Frame Wire\" key-frame-wire 5",
    "approve key-frame-wire",
    "add-tech key-frame-wire tech@local.test localtest \"Local Tech\" --admin",
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

using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
bool up = false;
for (int i = 0; i < 60 && !up; i++) { try { up = (await hc.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { await Task.Delay(500); } }
if (!up) { Console.WriteLine(string.Join("\n", serverLog)); Console.WriteLine("the platform did not come up"); Kill(); return 2; }

try { await WireAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { Kill(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

void Kill() { try { if (!server.HasExited) server.Kill(true); } catch { } }

async Task WireAsync()
{
    var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = "tech@local.test", password = "localtest" });
    var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    var techRaw = new ClientWebSocket();
    await techRaw.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
    var tc = new WsConnection(techRaw);
    var tq = new ConcurrentQueue<Msg>();
    tc.OnMessage += m => tq.Enqueue(m);
    _ = tc.RunAsync();
    if (await Wait<StateMsg>(tq, 8000) == null) throw new Exception("no state from the tech socket");

    var machine = new FakeMachine(ws, "key-frame-wire-device", "key-frame-wire-key", "KEY-FRAME-PC", tenantKey);
    await machine.ConnectAsync();
    await Task.Delay(700);
    tc.Send(new ConnectDeviceMsg { DeviceId = machine.DeviceId, Reason = "keyframewire diagnostic" });
    var ready = await Wait<SessionReadyMsg>(tq, 15000, s => s.Session.DeviceId == machine.DeviceId);
    if (ready == null) throw new Exception("no session started");
    if (!await machine.WaitForHostAsync(10000)) throw new Exception("the fake host never connected");
    var sessionId = ready.Session.SessionId;
    Log($"session {sessionId}");

    // The viewer that keeps up: reads everything and counts the video.
    long healthyVideo = 0;
    var healthyRaw = new ClientWebSocket();
    await healthyRaw.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={tok}"), default);
    var healthy = new WsConnection(healthyRaw);
    healthy.OnBinary += f => { if (f.Length > 0 && BinKind.Of(f.Span[0]) == BinKind.Video) Interlocked.Increment(ref healthyVideo); };
    _ = healthy.RunAsync();
    await Task.Delay(1500);   // past the encryption grace, so the session settles clear

    var payload = new byte[64 * 1024];
    new Random(7).NextBytes(payload);
    byte[] Video(bool key) => Bin.Video(0, key, 1366, 768, payload);
    byte[] Tiles() { var f = (byte[])payload.Clone(); f[0] = BinKind.Tiles; return f; }

    // ---- 1. both keeping up ----
    machine.KeyRequests.Clear();
    long h0 = Interlocked.Read(ref healthyVideo);
    await machine.StreamAsync(() => Video(false), TimeSpan.FromSeconds(2), maxPerSecond: 60);
    await Task.Delay(800);
    long h1 = Interlocked.Read(ref healthyVideo);
    Check("a viewer keeping up receives the stream", h1 - h0 > 50, $"{h1 - h0} frames");
    Check("...and nothing asks the host for a complete picture", machine.KeyRequests.IsEmpty, $"{machine.KeyRequests.Count} request(s)");

    // ---- 2. a viewer that stops reading, far behind on TILES ----
    var slowRaw = new ClientWebSocket();
    await slowRaw.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={tok}"), default);
    // Never read from slowRaw: the server's queue to it fills, and past 2 MB its frames are dropped.
    await Task.Delay(500);
    machine.KeyRequests.Clear();
    var sent = await machine.StreamAsync(Tiles, TimeSpan.FromSeconds(3), maxPerSecond: 0);
    await Task.Delay(800);
    Check("tile frames dropped for a slow viewer ask for nothing", machine.KeyRequests.IsEmpty,
        $"{sent} tile frames ({sent * payload.Length / 1048576} MB) sent, {machine.KeyRequests.Count} request(s)");

    // ---- 3. the same viewer, behind on VIDEO ----
    machine.KeyRequests.Clear();
    h0 = Interlocked.Read(ref healthyVideo);
    var t0 = DateTime.UtcNow;
    sent = await machine.StreamAsync(() => Video(false), TimeSpan.FromSeconds(3.5), maxPerSecond: 0);
    double secs = (DateTime.UtcNow - t0).TotalSeconds;
    await Task.Delay(800);
    var asked = machine.KeyRequests.ToList();
    h1 = Interlocked.Read(ref healthyVideo);
    Check("video frames dropped for a slow viewer ask the host for a complete picture", asked.Count >= 1,
        $"{sent} video frames in {secs:0.0} s, {asked.Count} request(s)");
    Check("...no more than once a second", asked.Count <= (int)Math.Ceiling(secs + 0.8) + 1 && Gaps(asked).All(g => g >= 950),
        $"gaps {string.Join(", ", Gaps(asked).Select(g => $"{g:0} ms"))}");
    Check("...while the viewer keeping up goes on receiving", h1 - h0 > 20, $"{h1 - h0} frames");
    try { slowRaw.Abort(); } catch { }
}

static IEnumerable<double> Gaps(List<DateTime> t) => t.Zip(t.Skip(1), (a, c) => (c - a).TotalMilliseconds);

async Task<T?> Wait<T>(ConcurrentQueue<Msg> q, int ms, Func<T, bool>? f = null) where T : Msg
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); } return null; }

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

/// <summary>A pretend customer machine: the tray app's device socket and a host socket that streams frames
/// and notes every <see cref="KeyFrameRequestMsg"/> the server sends it.</summary>
sealed class FakeMachine
{
    private readonly string _ws, _key, _name, _tenantKey;
    public string DeviceId { get; }
    private WsConnection? _device, _host;
    private ClientWebSocket? _hostRaw;
    public readonly ConcurrentQueue<DateTime> KeyRequests = new();
    public bool HostOpen => _hostRaw?.State == WebSocketState.Open;

    public FakeMachine(string ws, string deviceId, string key, string name, string tenantKey)
    { _ws = ws; DeviceId = deviceId; _key = key; _name = name; _tenantKey = tenantKey; }

    public async Task ConnectAsync()
    {
        var raw = new ClientWebSocket();
        await raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _device = new WsConnection(raw);
        _device.OnMessage += m => { if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) _ = StartHostAsync(s); };
        _ = _device.RunAsync();
        _device.Send(new HelloMsg
        {
            Role = DeviceRole.Client, DeviceId = DeviceId, DeviceKey = _key, MachineName = _name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
        });
        await Task.Delay(400);
    }

    public async Task<bool> WaitForHostAsync(int ms)
    {
        var e = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < e) { if (HostOpen) return true; await Task.Delay(50); }
        return false;
    }

    /// <summary>Sends frames for <paramref name="length"/>, never letting its OWN queue pass 1 MB — a drop here
    /// would be the host's, which the host already repairs; the drops under test are the server's.
    /// <paramref name="maxPerSecond"/> 0 is as fast as the server takes them.</summary>
    public async Task<int> StreamAsync(Func<byte[]> frame, TimeSpan length, int maxPerSecond)
    {
        int n = 0;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < length)
        {
            if (_host!.PendingBytes > 1024 * 1024) { await Task.Delay(2); continue; }
            if (_host.SendBinary(frame())) n++;
            if (maxPerSecond > 0) await Task.Delay(1000 / maxPerSecond);
            else if (n % 8 == 0) await Task.Yield();
        }
        return n;
    }

    private async Task StartHostAsync(StartSessionMsg s)
    {
        _hostRaw = new ClientWebSocket();
        await _hostRaw.ConnectAsync(new Uri($"{_ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}"), default);
        var host = new WsConnection(_hostRaw);
        _host = host;
        host.OnMessage += m => { if (m is KeyFrameRequestMsg) KeyRequests.Enqueue(DateTime.UtcNow); };
        _ = host.RunAsync();
        host.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 1366, Height = 768, Primary = true } } });
    }
}
