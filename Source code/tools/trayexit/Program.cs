using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// trayexit - Exit in the customer's tray app takes the machine offline (2026-09-29).
//
// Clint: "even if exit is selected on the client tray app, technicians can still connect to the
// machine... It should close the program completely and show as offline on the dashboard and
// console." Then: "Selecting the shortcut should then put the tray app back in session."
//
// The machine that matters is one with unattended access allowed: the AGENT service keeps running
// after the tray app exits and the server went on handing it sessions. So the pretend machine
// here has both connections, and unattended ticked.
//
// Its own platform on port 8097 against a throwaway database; the live server is never touched.
//
//   dotnet run -c Debug --project tools/trayexit/trayexit.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8097;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("trayexit - Exit in the tray app takes the machine offline");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-trayexit-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
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
    ["ASPNETCORE_URLS"] = http, ["ASPNETCORE_ENVIRONMENT"] = "Development",
};
HarnessDb.Use(env, dataDir);
string tenantKey = "";
foreach (var cmd in new[] { "create-tenant \"Tray Exit\" tray-exit 5", "approve tray-exit", "add-tech tray-exit tech@local.test localtest \"Local Tech\" --admin" })
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

Process? server = null;
async Task<bool> StartServer()
{
    // --urls on the command line: appsettings.json's Urls would otherwise win and collide with 8090.
    var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
    server = Process.Start(psi)!;
    server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    for (int i = 0; i < 60; i++) { try { if ((await probe.GetAsync($"{http}/health")).IsSuccessStatusCode) return true; } catch { } await Task.Delay(500); }
    return false;
}
void Kill() { try { if (server is { HasExited: false }) server.Kill(true); server?.WaitForExit(5000); } catch { } }

if (!await StartServer()) { Console.WriteLine("the platform did not come up"); Kill(); return 2; }
try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { Kill(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    const string DevId = "tray-exit-device", DevKey = "tray-exit-key", Name = "TRAY-EXIT-PC";
    var tech = await Tech.OpenAsync(http, ws);

    var tray = new FakeConn(ws, DevId, DevKey, Name, tenantKey, DeviceRole.Client);
    var agent = new FakeConn(ws, DevId, DevKey, Name, tenantKey, DeviceRole.Agent);
    await tray.ConnectAsync(); await agent.ConnectAsync();
    tray.Send(new SetUnattendedMsg { On = true });
    Check("the machine is online, with unattended access allowed",
        await tech.WaitDevice(DevId, d => d.Online && d.Unattended, 6000), tech.Describe(DevId));

    // ---- the fault as Clint saw it: the tray app goes, the agent stays, and anybody gets in ----
    Console.WriteLine();
    Console.WriteLine("== the tray app just disappearing (a crash, a shutdown - or Exit, before 2026-09-29) ==");
    tray.Drop();
    await Task.Delay(800);
    Check("the machine still reads online: the agent is there", await tech.WaitDevice(DevId, d => d.Online, 4000), tech.Describe(DevId));
    var (s1, e1) = await tech.ConnectAsync(DevId, "trayexit, before");
    Check("and a technician still gets in, through the agent - this is what Exit used to do", s1 != null, e1);
    if (s1 != null) { tech.End(s1); await Task.Delay(3800); }

    // ---- Exit, as it is now ----
    Console.WriteLine();
    Console.WriteLine("== Exit ==");
    await tray.ConnectAsync();
    await tech.WaitDevice(DevId, d => d.Online, 4000);
    var (live, _) = await tech.ConnectAsync(DevId, "trayexit, a session to be ended by Exit");
    Check("(a session is running when they choose Exit)", live != null);
    tech.Seen.Clear();
    await tray.SayClosingAsync();
    Check("the machine reads OFFLINE to the console", await tech.WaitDevice(DevId, d => !d.Online && !d.Unattended, 4000), tech.Describe(DevId));
    Check("...while its agent really is still connected (said honestly, for the health check)",
        tech.Latest(DevId)?.AgentOnline == true);
    Check("the running session is ended - the host on the machine is told",
        await WaitAsync(() => Task.FromResult(agent.Ended.ContainsKey(live ?? "")), 5000),
        agent.Ended.TryGetValue(live ?? "", out var why) ? why : "its host heard nothing");
    var (s2, e2) = await tech.ConnectAsync(DevId, "trayexit, after Exit");
    Check("a technician is REFUSED, agent or not", s2 == null, s2 != null ? "a session started" : "");
    Check("...and told why, not just \"offline\"", e2.Contains("closed the support app"), e2);
    Check("it is stored, not only remembered", await ClosedInDb(DevId) != null);

    // ---- a restart must not bring it back: the agent reconnects on its own ----
    Console.WriteLine();
    Console.WriteLine("== the server restarts ==");
    Kill();
    tech.Dispose(); agent.Drop();
    if (!await StartServer()) throw new Exception("the platform did not come back");
    tech = await Tech.OpenAsync(http, ws);
    await agent.ConnectAsync();
    await Task.Delay(1200);
    Check("after a restart, with the agent back, it is STILL offline", tech.Latest(DevId) is { Online: false }, tech.Describe(DevId));
    var (s3, _) = await tech.ConnectAsync(DevId, "trayexit, after restart");
    Check("...and still refused", s3 == null);

    // ---- the agent cannot do it, only the person at the machine ----
    agent.Send(new ClientClosingMsg());
    await Task.Delay(500);

    // ---- the shortcut: the tray app runs again ----
    Console.WriteLine();
    Console.WriteLine("== the tray app is opened again ==");
    await tray.ConnectAsync();
    Check("the machine is online again", await tech.WaitDevice(DevId, d => d.Online && d.Unattended, 5000), tech.Describe(DevId));
    Check("...and it is cleared in the database", await WaitAsync(async () => await ClosedInDb(DevId) == null, 4000));
    var (s4, e4) = await tech.ConnectAsync(DevId, "trayexit, reopened");
    Check("a technician gets in again", s4 != null, e4);
    if (s4 != null) { tech.End(s4); await Task.Delay(3800); }

    Console.WriteLine();
    Console.WriteLine("== only the tray app speaks for the person ==");
    agent.Send(new ClientClosingMsg());
    await Task.Delay(1000);
    Check("the AGENT saying it is ignored - the machine stays online", tech.Latest(DevId) is { Online: true }, tech.Describe(DevId));
}

async Task<DateTime?> ClosedInDb(string id)
{
    var o = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using var db = new PlatformDb(o);
    return await db.Devices.Where(d => d.DeviceId == id).Select(d => d.ClosedByUserUtc).FirstOrDefaultAsync();
}

static async Task<bool> WaitAsync(Func<Task<bool>> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (await f()) return true; await Task.Delay(150); } return false; }

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
    private string _http = "";

    public static async Task<Tech> OpenAsync(string http, string ws)
    {
        using var hc = new HttpClient();
        var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = "tech@local.test", password = "localtest" });
        var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var t = new Tech { _http = http };
        await t._raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
        t._c = new WsConnection(t._raw);
        t._c.OnMessage += m =>
        {
            t.Seen.Enqueue(m);
            if (m is StateMsg st) foreach (var d in st.Devices) t._devices[d.DeviceId] = d;
            if (m is DeviceUpdateMsg du) t._devices[du.Device.DeviceId] = du.Device;
        };
        _ = t._c.RunAsync();
        await Task.Delay(600);
        return t;
    }

    public DeviceInfo? Latest(string id) => _devices.TryGetValue(id, out var d) ? d : null;
    public string Describe(string id) => Latest(id) is { } d
        ? $"online={d.Online} unattended={d.Unattended} agent={d.AgentOnline} tray={d.ClientOnline}" : "not in the list";

    public async Task<bool> WaitDevice(string id, Func<DeviceInfo, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (Latest(id) is { } d && f(d)) return true; await Task.Delay(50); } return false; }

    public async Task<T?> WaitFor<T>(Func<T, bool> f, int ms) where T : Msg
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in Seen) if (m is T t && f(t)) return t; await Task.Delay(50); } return null; }

    /// <summary>Asks to connect; returns the session id, or null and what the server said instead.</summary>
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

    public void Dispose() { try { _raw.Abort(); } catch { } }
}

/// <summary>One of a machine's two connections: the tray app (Client) or the agent service.</summary>
sealed class FakeConn
{
    private readonly string _ws, _id, _key, _name, _tenantKey;
    private readonly DeviceRole _role;
    /// <summary>Sessions this connection hosted that the server ended, with the reason it gave.</summary>
    public readonly ConcurrentDictionary<string, string> Ended = new();
    private ClientWebSocket? _raw;
    private WsConnection? _c;

    public FakeConn(string ws, string id, string key, string name, string tenantKey, DeviceRole role)
    { _ws = ws; _id = id; _key = key; _name = name; _tenantKey = tenantKey; _role = role; }

    public async Task ConnectAsync()
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _c.OnMessage += m => { if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) _ = HostAsync(s); };
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = _role, DeviceId = _id, DeviceKey = _key, MachineName = _name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
        });
        await Task.Delay(500);
    }

    public void Send(Msg m) => _c?.Send(m);

    /// <summary>The socket simply going, with nothing said: a crash, a shutdown, or the old Exit.</summary>
    public void Drop() { try { _raw?.Abort(); } catch { } }

    /// <summary>What the tray app's Exit does now: say so, and close once it has gone.</summary>
    public async Task SayClosingAsync()
    {
        _c!.Send(new ClientClosingMsg());
        await _c.CloseAsync("closed by the user");
    }

    private async Task HostAsync(StartSessionMsg s)
    {
        var raw = new ClientWebSocket();
        await raw.ConnectAsync(new Uri($"{_ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}"), default);
        var host = new WsConnection(raw);
        host.OnMessage += m => { if (m is SessionEndedMsg e) { Ended[s.SessionId] = e.Reason; _ = host.CloseAsync("ended"); } };
        _ = host.RunAsync();
        host.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 256, Height = 256, Primary = true } } });
    }
}
