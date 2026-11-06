using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// silentoffline - a machine switched off reads offline (2026-10-09).
//
// Clint: "Machines still show online in the console even if they are off." A PC switched off at
// the wall, asleep, or cut from its network never closes its socket, and the server only learned
// it had gone when the operating system gave up on the TCP connection — a quarter of an hour on
// Linux. Now a connection that has pinged and then says nothing for Platform:SilenceSeconds is
// dropped (WsConnection.DropWhenSilent).
//
// The pretend machines here keep their sockets OPEN and simply stop talking, which is exactly what
// a switched-off machine looks like from the server; closing the socket would prove nothing, since
// that path always worked. The limit is 3 s rather than 45 so the harness takes seconds.
//
// Its own platform on port 8151 against a throwaway database; the live server is never touched.
//
//   dotnet run -c Release --project tools/silentoffline/silentoffline.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8151;
const int LimitSeconds = 3;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("silentoffline - a machine that goes quiet reads offline");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-silentoffline-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
Log($"building the platform into {serverDir}");
var b = Run("dotnet", $"build -c Release \"{Path.Combine(repo, "src", "RemoteSupport.Platform", "RemoteSupport.Platform.csproj")}\" -v q --nologo -p:OutDir=\"{serverDir}\\\"", repo, 300000);
if (b.code != 0) { Console.WriteLine(b.output); Console.WriteLine("the platform would not build"); return 2; }
var exe = Path.Combine(serverDir, "RemoteSupport.Platform.exe");
var env = new Dictionary<string, string>
{
    ["RSP_Platform__DataDirectory"] = dataDir, ["RSP_Security__RequireTwoFactor"] = "false", ["RSP_Platform__PublicUrl"] = http,
    ["RSP_Platform__SilenceSeconds"] = LimitSeconds.ToString(),
    ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[] { "create-tenant \"Silent Offline\" silent-offline 5", "approve silent-offline", "add-tech silent-offline tech@local.test localtest \"Local Tech\" --admin", "add-tech silent-offline tech2@local.test localtest \"Second Tech\" --admin" })
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

Process? server = null;
var serverLog = new ConcurrentQueue<string>();
var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
server = Process.Start(psi)!;
server.OutputDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.ErrorDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
bool up = false;
using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
    for (int i = 0; i < 60 && !up; i++) { try { up = (await probe.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { } if (!up) await Task.Delay(500); }
void Kill() { try { if (server is { HasExited: false }) server.Kill(true); server?.WaitForExit(5000); } catch { } }
if (!up) { Console.WriteLine("the platform did not come up"); Kill(); return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { Kill(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    var tech = await Tech.OpenAsync(http, ws, pings: true);

    // Three machines: one that is switched off, one that stays on, and one from before pings counted.
    var off = new FakeMachine(ws, "silent-off", "OFF-AT-THE-WALL", tenantKey);
    var on = new FakeMachine(ws, "silent-on", "STILL-ON", tenantKey);
    var mute = new FakeMachine(ws, "silent-mute", "NEVER-PINGS", tenantKey);
    await off.ConnectAsync(); await on.ConnectAsync(); await mute.ConnectAsync();
    off.StartPinging(); on.StartPinging();
    Check("all three machines read online to the console",
        await tech.WaitDevice(off.Id, d => d.Online, 5000) && await tech.WaitDevice(on.Id, d => d.Online, 5000) && await tech.WaitDevice(mute.Id, d => d.Online, 5000),
        $"{tech.Describe(off.Id)}; {tech.Describe(on.Id)}; {tech.Describe(mute.Id)}");

    Console.WriteLine();
    Console.WriteLine($"== OFF-AT-THE-WALL stops talking, its socket left open (the limit is {LimitSeconds} s) ==");
    var stopped = Stopwatch.StartNew();
    off.GoQuiet();
    Check("for the first moments it still reads online — one missed ping is not an outage",
        await tech.WaitDevice(off.Id, d => d.Online, 300) && !await tech.WaitDevice(off.Id, d => !d.Online, (LimitSeconds - 1) * 1000),
        tech.Describe(off.Id));
    bool wentOffline = await tech.WaitDevice(off.Id, d => !d.Online, (LimitSeconds + 5) * 1000);
    Check("then it reads OFFLINE — the server let go of a socket the machine never closed", wentOffline,
        $"after {stopped.Elapsed.TotalSeconds:0.0} s: {tech.Describe(off.Id)}; socket {(off.SocketOpen ? "open" : "closed")} at our end");
    Check("...soon after the limit, not a quarter of an hour", wentOffline && stopped.Elapsed.TotalSeconds < LimitSeconds + 5,
        $"{stopped.Elapsed.TotalSeconds:0.0} s");
    Check("the server says why in its log", serverLog.Any(l => l.Contains("OFF-AT-THE-WALL") && l.Contains("went silent")),
        serverLog.FirstOrDefault(l => l.Contains("went silent")) ?? "nothing logged");
    var (s0, e0) = await tech.ConnectAsync(off.Id, "silentoffline, a machine that is off");
    Check("connecting to it is refused as offline", s0 == null, s0 != null ? "a session started" : e0);

    Console.WriteLine();
    Console.WriteLine("== the machines that did not go quiet ==");
    Check("STILL-ON, pinging all along, is still online", tech.Latest(on.Id) is { Online: true }, tech.Describe(on.Id));
    Check("NEVER-PINGS, which never promised to, is left alone", tech.Latest(mute.Id) is { Online: true }, tech.Describe(mute.Id));

    Console.WriteLine();
    Console.WriteLine("== OFF-AT-THE-WALL is switched on again ==");
    off.Drop();
    var back = new FakeMachine(ws, "silent-off", "OFF-AT-THE-WALL", tenantKey);
    await back.ConnectAsync(); back.StartPinging();
    Check("it reads online again", await tech.WaitDevice(off.Id, d => d.Online, 5000), tech.Describe(off.Id));
    await Task.Delay((LimitSeconds + 2) * 1000);
    Check("and stays online past the limit while it pings", tech.Latest(off.Id) is { Online: true }, tech.Describe(off.Id));
    var (s1, e1) = await tech.ConnectAsync(off.Id, "silentoffline, back on");
    Check("a technician can connect to it", s1 != null, e1);
    if (s1 != null) tech.End(s1);

    Console.WriteLine();
    Console.WriteLine("== a CONSOLE that goes quiet (its laptop lid shut) is dropped too ==");
    // Somebody else: the same person twice would be one sign-in at a time (HANDOVER 82) signing the first out.
    var quietTech = await Tech.OpenAsync(http, ws, pings: true, email: "tech2@local.test");
    quietTech.GoQuiet();
    Check("the server closes it after the limit", await WaitAsync(() => Task.FromResult(quietTech.Closed), (LimitSeconds + 5) * 1000),
        quietTech.Closed ? "closed" : "still open");
    Check("and the console that kept talking is still connected", !tech.Closed);

    tech.Dispose(); quietTech.Dispose(); on.Drop(); mute.Drop(); back.Drop();
}

static async Task<bool> WaitAsync(Func<Task<bool>> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (await f()) return true; await Task.Delay(100); } return false; }

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

/// <summary>A technician's console connection: the device list as it arrives, and connecting.</summary>
sealed class Tech : IDisposable
{
    private readonly ClientWebSocket _raw = new();
    private WsConnection _c = null!;
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
    public readonly ConcurrentQueue<Msg> Seen = new();
    private readonly CancellationTokenSource _pings = new();
    public volatile bool Closed;

    public static async Task<Tech> OpenAsync(string http, string ws, bool pings, string email = "tech@local.test")
    {
        using var hc = new HttpClient();
        var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = "localtest" });
        var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var t = new Tech();
        await t._raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
        t._c = new WsConnection(t._raw);
        t._c.OnMessage += m =>
        {
            t.Seen.Enqueue(m);
            if (m is StateMsg st) foreach (var d in st.Devices) t._devices[d.DeviceId] = d;
            if (m is DeviceUpdateMsg du) t._devices[du.Device.DeviceId] = du.Device;
        };
        t._c.OnClosed += _ => t.Closed = true;
        _ = t._c.RunAsync();
        // As TechLink does: a ping at once, then every 15 s — here every second, inside the 3 s limit.
        if (pings) _ = Task.Run(async () => { try { while (!t._pings.IsCancellationRequested) { t._c.Send(new PingMsg()); await Task.Delay(1000, t._pings.Token); } } catch { } });
        await Task.Delay(600);
        return t;
    }

    public void GoQuiet() => _pings.Cancel();

    public DeviceInfo? Latest(string id) => _devices.TryGetValue(id, out var d) ? d : null;
    public string Describe(string id) => Latest(id) is { } d ? $"online={d.Online} agent={d.AgentOnline} tray={d.ClientOnline}" : "not in the list";

    public async Task<bool> WaitDevice(string id, Func<DeviceInfo, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (Latest(id) is { } d && f(d)) return true; await Task.Delay(50); } return false; }

    public async Task<(string? session, string error)> ConnectAsync(string deviceId, string reason)
    {
        var mark = Seen.Count;
        _c.Send(new ConnectDeviceMsg { DeviceId = deviceId, Reason = reason });
        var e = DateTime.UtcNow.AddMilliseconds(6000);
        while (DateTime.UtcNow < e)
        {
            foreach (var m in Seen.Skip(mark))
            {
                if (m is SessionReadyMsg r && r.Session.DeviceId == deviceId) return (r.Session.SessionId, "");
                if (m is ErrorMsg er) return (null, er.Message);
            }
            await Task.Delay(50);
        }
        return (null, "no answer in 6 s");
    }

    public void End(string sessionId) => _c.Send(new EndSessionMsg { SessionId = sessionId });

    public void Dispose() { _pings.Cancel(); try { _raw.Abort(); } catch { } }
}

/// <summary>A machine's tray app connection, which can stop talking without closing anything.</summary>
sealed class FakeMachine
{
    private readonly string _ws, _tenantKey;
    public readonly string Id, Name;
    private ClientWebSocket? _raw;
    private WsConnection? _c;
    private readonly CancellationTokenSource _pings = new();

    public FakeMachine(string ws, string id, string name, string tenantKey) { _ws = ws; Id = id; Name = name; _tenantKey = tenantKey; }

    public bool SocketOpen => _raw?.State == WebSocketState.Open;

    public async Task ConnectAsync()
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _c.OnMessage += m => { if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) _ = HostAsync(s); };
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = DeviceRole.Client, DeviceId = Id, DeviceKey = Id + "-key", MachineName = Name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
        });
        await Task.Delay(500);
    }

    /// <summary>As DeviceLink's PingLoop: one at once, then on a timer — every second here.</summary>
    public void StartPinging() => _ = Task.Run(async () =>
    { try { while (!_pings.IsCancellationRequested) { _c!.Send(new PingMsg()); await Task.Delay(1000, _pings.Token); } } catch { } });

    /// <summary>Switched off: nothing more is sent, and nothing is closed.</summary>
    public void GoQuiet() => _pings.Cancel();

    public void Drop() { _pings.Cancel(); try { _raw?.Abort(); } catch { } }

    private async Task HostAsync(StartSessionMsg s)
    {
        var raw = new ClientWebSocket();
        await raw.ConnectAsync(new Uri($"{_ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}"), default);
        var host = new WsConnection(raw);
        host.OnMessage += m => { if (m is SessionEndedMsg) _ = host.CloseAsync("ended"); };
        _ = host.RunAsync();
        host.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 256, Height = 256, Primary = true } } });
    }
}
