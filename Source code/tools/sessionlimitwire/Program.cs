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
// sessionlimitwire - two machines at once, at most (2026-10-15).
//
// Clint: two sessions side by side, copying from one office machine and pasting on the other, and
// no more than two. The console refuses a third before it asks; the hub refuses it too, so an old
// console or a socket driven by hand cannot get round it (TenantHub.SessionLimit.cs). This drives
// the HUB over real sockets:
//
//   - Sue in two sessions: a third machine is refused in SessionLimit's words, naming the two, with
//     no session and no ticket made for it — its person is never asked
//   - going back to a machine already open is never refused: Connect again, a second viewer socket
//   - a colleague is not limited by Sue's sessions, and Sue cannot JOIN his session as a third
//   - a call she accepts as a third is refused and keeps ringing, so a colleague can take it
//   - once she leaves one, a third machine is hers again
//
// Its own platform on port 8154; the live server is never touched.
//
//   dotnet run -c Release --project tools/sessionlimitwire/sessionlimitwire.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8154;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("sessionlimitwire - a technician may be connected to two machines at once, and no more");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-sessionlimitwire-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
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
foreach (var cmd in new[] { "create-tenant \"Limit Wire\" limit-wire 5", "approve limit-wire",
             "add-tech limit-wire sue@local.test localtest \"Sue Tech\" --admin", "add-tech limit-wire bob@local.test localtest \"Bob Tech\" --admin" })
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
server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
bool up = false;
using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
    for (int i = 0; i < 60 && !up; i++) { try { up = (await probe.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { } if (!up) await Task.Delay(500); }
void Kill() { try { if (!server.HasExited) server.Kill(true); server.WaitForExit(5000); } catch { } }
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
    var sue = await Tech.OpenAsync(http, ws, "sue@local.test");
    var bob = await Tech.OpenAsync(http, ws, "bob@local.test");

    var a = new FakeMachine(ws, "office-a", "OFFICE-A", tenantKey);
    var bm = new FakeMachine(ws, "office-b", "OFFICE-B", tenantKey);
    var c = new FakeMachine(ws, "office-c", "OFFICE-C", tenantKey);
    var d = new FakeMachine(ws, "office-d", "OFFICE-D", tenantKey);
    foreach (var m in new[] { a, bm, c, d }) await m.ConnectAsync();
    foreach (var m in new[] { a, bm, c, d }) await sue.WaitDevice(m.Id, x => x.Online, 5000);

    Console.WriteLine("== Sue in two sessions ==");
    var sa = await sue.ConnectAsync(a.Id, "Copy the accounts folder across");
    var va = sa.Session != null ? await sue.ViewAsync(sa.Session) : null;
    Check("first machine: session, and her viewer is in", sa.Session != null && va is { Refused: null }, sa.Describe() + " / " + va?.Describe());
    var sb = await sue.ConnectAsync(bm.Id, "Copy the accounts folder across");
    var vb = sb.Session != null ? await sue.ViewAsync(sb.Session) : null;
    Check("second machine: session, and her viewer is in", sb.Session != null && vb is { Refused: null }, sb.Describe() + " / " + vb?.Describe());

    Console.WriteLine();
    Console.WriteLine("== a third machine ==");
    int ticketsBefore = await TicketCountAsync();
    var sc = await sue.ConnectAsync(c.Id, "One more");
    Check("is refused, in words", sc.Session == null && sc.Error != null, sc.Describe());
    Check("...SessionLimit's words, naming the two machines she is on",
        sc.Error?.Message == SessionLimit.Refusal(new[] { "OFFICE-A", "OFFICE-B" }) || sc.Error?.Message == SessionLimit.Refusal(new[] { "OFFICE-B", "OFFICE-A" }),
        sc.Error?.Message ?? "");
    await Task.Delay(800);
    Check("the third machine was never told to start a session — its person is not asked", c.StartsSeen == 0, $"{c.StartsSeen} StartSessionMsg");
    Check("and no ticket was opened for it", await TicketCountAsync() == ticketsBefore, $"{ticketsBefore} then {await TicketCountAsync()}");

    Console.WriteLine();
    Console.WriteLine("== going back to a machine already open ==");
    var again = await sue.ConnectAsync(a.Id, "");
    Check("Connect on OFFICE-A again answers with its session — not refused", again.Session == sa.Session && again.Error == null, again.Describe());
    var va2 = await sue.ViewAsync(sa.Session!);
    Check("a second viewer socket on OFFICE-A (a reconnect) is let in", va2.Refused == null, va2.Describe());
    va2.Close();
    await Task.Delay(300);

    Console.WriteLine();
    Console.WriteLine("== a colleague ==");
    var bc = await bob.ConnectAsync(c.Id, "Printer");
    var vbc = bc.Session != null ? await bob.ViewAsync(bc.Session) : null;
    Check("Bob is not limited by Sue's two: his session on OFFICE-C starts", bc.Session != null && vbc is { Refused: null }, bc.Describe());
    var join = bc.Session != null ? await sue.ViewAsync(bc.Session) : null;
    Check("Sue JOINING Bob's session as a third is refused at the socket, in the same words",
        join?.Refused != null && join.Refused.StartsWith("You are already connected to 2 machines"), join?.Describe() ?? "no session to join");
    Check("...and she is not listed in it", !(bob.LatestSession(bc.Session)?.Technicians.Contains("Sue Tech") ?? true),
        string.Join(", ", bob.LatestSession(bc.Session)?.Technicians ?? new List<string>()));

    Console.WriteLine();
    Console.WriteLine("== a call as a third ==");
    d.Send(new RequestCallMsg { ContactName = "Dee", Issue = "Outlook keeps asking for a password" });
    var call = await sue.WaitCall(d.Id, 5000);
    Check("(OFFICE-D rings)", call != null, call?.CallId ?? "no call");
    if (call != null)
    {
        var ac = await sue.AcceptAsync(call.CallId, d.Id);
        Check("Sue accepting it is refused, in words", ac.Session == null && ac.Error?.Message.StartsWith("You are already connected to 2 machines") == true, ac.Describe());
        await Task.Delay(400);
        Check("...and it is still ringing — not taken by her", bob.LatestCall(call.CallId)?.Status == CallStatus.Ringing, $"{bob.LatestCall(call.CallId)?.Status}");
        Check("...and OFFICE-D was not told to start a session", d.StartsSeen == 0, $"{d.StartsSeen}");
        var bd = await bob.AcceptAsync(call.CallId, d.Id);
        Check("Bob can take it", bd.Session != null, bd.Describe());
    }

    Console.WriteLine();
    Console.WriteLine("== once she leaves one ==");
    va!.End(sa.Session!);
    await Task.Delay(1500);
    var join2 = await sue.ViewAsync(bc.Session!);
    Check("with OFFICE-A closed, joining Bob's session on OFFICE-C is let in", join2.Refused == null, join2.Describe());
    var sc2 = await sue.ConnectAsync(a.Id, "Back again");
    Check("and now OFFICE-A would be the third: refused again", sc2.Session == null && sc2.Error?.Message.Contains("OFFICE-B") == true && sc2.Error.Message.Contains("OFFICE-C"), sc2.Describe());

    sue.Dispose(); bob.Dispose();
    foreach (var m in new[] { a, bm, c, d }) m.Drop();
}

PlatformDb Db() => new(new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options);

async Task<int> TicketCountAsync() { await using var db = Db(); return await db.Tickets.CountAsync(); }

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

/// <summary>What one connect or accept came to: a session, or the error the hub sent instead.</summary>
sealed record Answer(string? Session, ErrorMsg? Error)
{
    public string Describe() => Session != null ? "session " + Session[..8] : Error == null ? "no answer" : "refused: " + Error.Message;
}

/// <summary>A session window's viewer socket: in, or refused with the hub's words.</summary>
sealed class Viewer
{
    public ClientWebSocket Raw = null!;
    public WsConnection Conn = null!;
    public string? Refused;
    public string Describe() => Refused == null ? "viewer in" : "viewer refused: " + Refused;
    public void Close() { try { Raw.Abort(); } catch { } }
    /// <summary>Leave / End on the session window: said, then the socket goes.</summary>
    public void End(string sessionId) { try { Conn.Send(new EndSessionMsg { SessionId = sessionId }); } catch { } Thread.Sleep(200); Close(); }
}

/// <summary>A technician's console connection.</summary>
sealed class Tech : IDisposable
{
    private readonly ClientWebSocket _raw = new();
    private WsConnection _c = null!;
    private string _ws = "", _token = "";
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, CallInfo> _calls = new();
    public readonly ConcurrentQueue<Msg> Seen = new();

    public static async Task<Tech> OpenAsync(string http, string ws, string email)
    {
        using var hc = new HttpClient();
        var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email, password = "localtest" });
        var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var t = new Tech { _ws = ws, _token = tok };
        await t._raw.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
        t._c = new WsConnection(t._raw);
        t._c.OnMessage += m =>
        {
            t.Seen.Enqueue(m);
            if (m is StateMsg st)
            {
                foreach (var d in st.Devices) t._devices[d.DeviceId] = d;
                foreach (var s in st.Sessions) t._sessions[s.SessionId] = s;
                foreach (var c in st.Calls) t._calls[c.CallId] = c;
            }
            if (m is DeviceUpdateMsg du) t._devices[du.Device.DeviceId] = du.Device;
            if (m is SessionReadyMsg sr) t._sessions[sr.Session.SessionId] = sr.Session;
            if (m is CallUpdateMsg cu) t._calls[cu.Call.CallId] = cu.Call;
            if (m is RingMsg rg) t._calls[rg.Call.CallId] = rg.Call;
        };
        _ = t._c.RunAsync();
        await Task.Delay(600);
        return t;
    }

    public SessionInfo? LatestSession(string? id) => id != null && _sessions.TryGetValue(id, out var s) ? s : null;
    public CallInfo? LatestCall(string id) => _calls.TryGetValue(id, out var c) ? c : null;

    public async Task<bool> WaitDevice(string id, Func<DeviceInfo, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (_devices.TryGetValue(id, out var d) && f(d)) return true; await Task.Delay(50); } return false; }

    public async Task<CallInfo?> WaitCall(string deviceId, int ms)
    {
        var e = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < e)
        {
            var c = _calls.Values.FirstOrDefault(x => x.DeviceId == deviceId && x.Status == CallStatus.Ringing);
            if (c != null) return c;
            await Task.Delay(50);
        }
        return null;
    }

    public Task<Answer> ConnectAsync(string deviceId, string reason) =>
        AnswerAsync(new ConnectDeviceMsg { DeviceId = deviceId, Reason = reason }, deviceId);

    public Task<Answer> AcceptAsync(string callId, string deviceId) => AnswerAsync(new AcceptCallMsg { CallId = callId }, deviceId);

    private async Task<Answer> AnswerAsync(Msg ask, string deviceId)
    {
        var mark = Seen.Count;
        _c.Send(ask);
        var e = DateTime.UtcNow.AddMilliseconds(6000);
        while (DateTime.UtcNow < e)
        {
            foreach (var m in Seen.Skip(mark))
            {
                // Only messages after the ask, and only for the machine asked about: a SessionReadyMsg is also
                // broadcast to every console when anybody's session starts.
                if (m is SessionReadyMsg r && r.Session.DeviceId == deviceId) return new Answer(r.Session.SessionId, null);
                if (m is ErrorMsg er) return new Answer(null, er);
            }
            await Task.Delay(50);
        }
        return new Answer(null, null);
    }

    /// <summary>Opens a session window's socket, as SessionLink does, and waits to see whether it is turned away.</summary>
    public async Task<Viewer> ViewAsync(string sessionId)
    {
        var v = new Viewer { Raw = new ClientWebSocket() };
        await v.Raw.ConnectAsync(new Uri($"{_ws}/ws/session?role=viewer&id={sessionId}&token={_token}"), default);
        v.Conn = new WsConnection(v.Raw);
        v.Conn.OnMessage += m => { if (m is SessionEndedMsg se) v.Refused = se.Reason; };
        _ = v.Conn.RunAsync();
        await Task.Delay(1200);
        return v;
    }

    public void Dispose() { try { _raw.Abort(); } catch { } }
}

/// <summary>A machine's tray app, which hosts any session it is handed and counts how often it was.</summary>
sealed class FakeMachine
{
    private readonly string _ws, _tenantKey;
    public readonly string Id, Name;
    private ClientWebSocket? _raw;
    private WsConnection? _c;
    private int _starts;
    public int StartsSeen => _starts;

    public FakeMachine(string ws, string id, string name, string tenantKey) { _ws = ws; Id = id; Name = name; _tenantKey = tenantKey; }

    public async Task ConnectAsync()
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _c.OnMessage += m => { if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) { Interlocked.Increment(ref _starts); _ = HostAsync(s); } };
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = DeviceRole.Client, DeviceId = Id, DeviceKey = Id + "-key", MachineName = Name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" }
        });
        await Task.Delay(500);
    }

    public void Send(Msg m) => _c?.Send(m);
    public void Drop() { try { _raw?.Abort(); } catch { } }

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
