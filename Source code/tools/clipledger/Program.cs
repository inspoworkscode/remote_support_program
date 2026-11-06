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
// clipledger - roadmap 3.5.3: everything that moves between the two clipboards is on the ticket.
// "Automatic must not mean invisible."
//
// Two halves, both on THIS machine alone:
//
//   the ledger   ClipLedger, in-process, with a one-second quiet period instead of a minute. A
//                single copy must be reported WITHOUT waiting for a second one — before
//                2026-09-29 it was not, and a session with one burst of copies never reported it.
//   the wire     its own platform on port 8098 against a throwaway database, and a pretend
//                customer machine. A clipboard line sent mid-session lands on the session's
//                ticket and in the customer's tray; and the host's LAST line, sent when it is told
//                the session has ended, lands too, BEFORE "Session ended". Before 2026-09-29 the
//                server forgot the session's ticket the moment it ended and closed the host
//                straight after, so that line went nowhere.
//
// Clint's live server on 8090 and his database are never touched.
//
//   dotnet run -c Debug --project tools/clipledger/clipledger.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8098;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("clipledger - every clipboard movement is on the ticket (3.5.3)");
Console.WriteLine();

// =============================================================================================
Console.WriteLine("== the ledger ==");
{
    var got = new ConcurrentQueue<(bool from, int times, long chars, string? err)>();
    using var ledger = new ClipLedger(TimeSpan.FromSeconds(1), (f, t, c, e) => got.Enqueue((f, t, c, e)));

    ledger.Note(true, 12);
    await Task.Delay(300);
    Check("nothing is reported before the quiet period is up", got.IsEmpty);
    await Task.Delay(1400);
    Check("ONE copy is reported on its own, with nothing after it to push it out",
        got.Count == 1 && got.TryPeek(out var one) && one is (true, 1, 12, null), string.Join("; ", got));
    got.Clear();

    ledger.Note(true, 5); ledger.Note(true, 7); ledger.Note(false, 30);
    await Task.Delay(1500);
    var lines = got.ToList();
    Check("a burst is one line per direction", lines.Count == 2, string.Join("; ", lines));
    Check("...the times and the characters add up", lines.Contains((true, 2, 12, null)) && lines.Contains((false, 1, 30, null)));
    Check("...and the copy OFF the machine is reported before the one onto it", lines.Count == 2 && lines[0].from);
    got.Clear();

    ledger.Note(false, 9);
    ledger.Flush();
    Check("Flush reports what is held at once", got.Count == 1 && got.TryPeek(out var f) && f is (false, 1, 9, null));
    await Task.Delay(1400);
    Check("...and the timer then has nothing left to say", got.Count == 1);
    got.Clear();

    ledger.Flush();
    Check("nothing moved, nothing reported", got.IsEmpty);

    ledger.Note(false, 4, "the clipboard is in use by another program");
    Check("a refusal is its own line, at once", got.Count == 1 && got.TryPeek(out var r) && r.err != null && r.times == 1);
    got.Clear();

    // A steady trickle must not postpone the line for ever: the period starts at the first
    // movement it covers.
    for (int i = 0; i < 6; i++) { ledger.Note(true, 1); await Task.Delay(300); }
    Check("a steady trickle still produces a line inside the period", got.Any(), $"{got.Count} line(s) after 1.8 s");
    ledger.Flush();
    Check("...and nothing is lost between the lines", got.Where(x => x.from).Sum(x => x.times) == 6, $"{got.Where(x => x.from).Sum(x => x.times)} of 6");
}

// =============================================================================================
Console.WriteLine();
Console.WriteLine("== the wire ==");
var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-clipledger-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var dataDir = Path.Combine(work, "data");
Directory.CreateDirectory(dataDir);
var serverDir = Path.Combine(work, "server");
// Built to a folder of its own: the live server holds the project's own bin (see reconnectlocal).
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
    "create-tenant \"Clip Ledger\" clip-ledger 5",
    "approve clip-ledger",
    "add-tech clip-ledger tech@local.test localtest \"Local Tech\" --admin",
})
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

// --urls on the command line: appsettings.json's Urls would otherwise win and collide with 8090.
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

    var machine = new FakeMachine(ws, "clip-ledger-device", "clip-ledger-key", "CLIP-LEDGER-PC", tenantKey);
    await machine.ConnectAsync();
    await Task.Delay(700);

    tc.Send(new ConnectDeviceMsg { DeviceId = machine.DeviceId, Reason = "clipledger diagnostic" });
    var ready = await Wait<SessionReadyMsg>(tq, 15000, s => s.Session.DeviceId == machine.DeviceId);
    if (ready == null) throw new Exception("no session started");
    if (!await machine.WaitForHostAsync(10000)) throw new Exception("the fake host never connected");
    var sessionId = ready.Session.SessionId;
    Log($"session {sessionId}");

    var options = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    async Task<List<string>> Notes()
    {
        await using var db = new PlatformDb(options);
        return await db.TicketNotes.OrderBy(n => n.Id).Select(n => n.Body).ToListAsync();
    }
    async Task<bool> NoteArrives(Func<string, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if ((await Notes()).Any(f)) return true; await Task.Delay(150); } return false; }

    // ---- a line in the middle of a session ----
    machine.SendActivity(new FileActivityMsg { Kind = "clip-text", FromMachine = true, Label = "clipboard text", Bytes = 29, Times = 2 });
    Check("a clipboard line mid-session lands on the session's ticket",
        await NoteArrives(n => n.Contains("29 characters") && n.Contains("CLIP-LEDGER-PC"), 5000));
    Check("...counting characters, never quoting them", (await Notes()).Any(n => n.Contains("29 characters, 2 times")));
    Check("...and the customer's tray app is told too", await WaitFor(() => machine.TrayActivity.Any(a => a.Bytes == 29), 3000));

    // ---- the host's LAST line, sent when it is told the session is over ----
    machine.FinalOnEnd = new FileActivityMsg { Kind = "clip-text", FromMachine = false, Label = "clipboard text", Bytes = 41, Times = 1 };
    tc.Send(new EndSessionMsg { SessionId = sessionId });
    Check("the host is told the session has ended", await WaitFor(() => machine.HostSawEnded, 5000));
    Check("the host's last clipboard line, sent on being told, lands on the ticket",
        await NoteArrives(n => n.Contains("41 characters"), 6000), string.Join(" | ", (await Notes()).TakeLast(3)));
    Check("...and \"Session ended\" is still written", await NoteArrives(n => n.StartsWith("Session ended"), 6000));
    var all = await Notes();
    int last = all.FindIndex(n => n.Contains("41 characters")), ended = all.FindIndex(n => n.StartsWith("Session ended"));
    Check("...AFTER the last clipboard line, so the ticket reads in order", last >= 0 && ended > last, $"clipboard at {last}, ended at {ended}");
    Check("the host's socket is closed in the end", await WaitFor(() => !machine.HostOpen, 6000));

    // ---- a host that says nothing on the way out is not waited for ----
    tq.Clear();
    tc.Send(new ConnectDeviceMsg { DeviceId = machine.DeviceId, Reason = "clipledger, second" });
    var r2 = await Wait<SessionReadyMsg>(tq, 15000, s => s.Session.DeviceId == machine.DeviceId && s.Session.SessionId != sessionId);
    if (r2 == null || !await machine.WaitForHostAsync(10000)) throw new Exception("no second session");
    machine.FinalOnEnd = null; machine.CloseOnEnd = false;   // an old host: ignores SessionEndedMsg
    var t0 = DateTime.UtcNow;
    tc.Send(new EndSessionMsg { SessionId = r2.Session.SessionId });
    Check("an old host that ignores the end is closed by the server anyway", await WaitFor(() => !machine.HostOpen, 8000),
        $"{(DateTime.UtcNow - t0).TotalSeconds:0.0} s");
}

async Task<bool> WaitFor(Func<bool> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (f()) return true; await Task.Delay(50); } return false; }

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

/// <summary>
/// A pretend customer machine: the tray app's device socket, and the host socket Host.exe holds.
/// On SessionEndedMsg it does what the real host now does — sends its last clipboard line, then
/// closes its own socket — unless told to behave like an older host that ignores the message.
/// </summary>
sealed class FakeMachine
{
    private readonly string _ws, _key, _name, _tenantKey;
    public string DeviceId { get; }
    private WsConnection? _device, _host;
    private ClientWebSocket? _hostRaw;
    public readonly ConcurrentBag<FileActivityMsg> TrayActivity = new();
    public FileActivityMsg? FinalOnEnd;
    public bool CloseOnEnd = true;
    public bool HostSawEnded;
    /// <summary>How long a reply takes to come back, as over the internet rather than loopback.</summary>
    public int ReplyDelayMs = 150;
    public bool HostOpen => _hostRaw?.State == WebSocketState.Open;

    public FakeMachine(string ws, string deviceId, string key, string name, string tenantKey)
    { _ws = ws; DeviceId = deviceId; _key = key; _name = name; _tenantKey = tenantKey; }

    public async Task ConnectAsync()
    {
        var raw = new ClientWebSocket();
        await raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _device = new WsConnection(raw);
        _device.OnMessage += m =>
        {
            if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) _ = StartHostAsync(s);
            if (m is FileActivityMsg fa) TrayActivity.Add(fa);
        };
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

    public void SendActivity(FileActivityMsg m) => _host?.Send(m);

    private async Task StartHostAsync(StartSessionMsg s)
    {
        HostSawEnded = false;
        _hostRaw = new ClientWebSocket();
        await _hostRaw.ConnectAsync(new Uri($"{_ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}"), default);
        var host = new WsConnection(_hostRaw);
        _host = host;
        host.OnMessage += m =>
        {
            if (m is not SessionEndedMsg) return;
            HostSawEnded = true;
            _ = Task.Run(async () =>
            {
                // A real customer machine is a network away. On loopback the reply beats the
                // server to the end of EndSession and a lost line looks delivered - the first run
                // of this harness passed against the very fault it was written for.
                await Task.Delay(ReplyDelayMs);
                if (FinalOnEnd != null) host.Send(FinalOnEnd);
                if (CloseOnEnd) _ = host.CloseAsync("ended");
            });
        };
        _ = host.RunAsync();
        host.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 256, Height = 256, Primary = true } } });
    }
}
