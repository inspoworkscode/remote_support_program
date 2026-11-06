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
// reasonwire - reconnecting inside thirty minutes asks no reason (2026-10-09).
//
// Clint: "Every time you reconnect to a machine with the 30 minute windows it asks for a reason.
// Reconnecting within 30 minutes should automatically be part of the same ticket with no reason
// necessary." The console now connects WITHOUT a reason first; the hub carries on the machine's
// ticket if TicketService.ActiveForDeviceAsync finds one (30 minutes from its last session's end),
// and otherwise answers ErrorMsg.NeedsReason so the console asks as it always has.
//
// This drives the hub over real sockets: a technician, a machine, and a machine with an unattended
// password — whose two questions now arrive in a different order depending on whether there is a
// ticket to carry on. Time is faked by ageing rows in the throwaway database.
//
// Its own platform on port 8152; the live server is never touched.
//
//   dotnet run -c Release --project tools/reasonwire/reasonwire.csproj
// ---------------------------------------------------------------------------------------------

const int Port = 8152;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("reasonwire - a reconnect inside thirty minutes carries on the ticket and asks nothing");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-reasonwire-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
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
foreach (var cmd in new[] { "create-tenant \"Reason Wire\" reason-wire 5", "approve reason-wire",
             "add-tech reason-wire sue@local.test localtest \"Sue Tech\" --admin", "add-tech reason-wire bob@local.test localtest \"Bob Tech\" --admin" })
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

    // ================================================================ a machine with no password
    var pc = new FakeMachine(ws, "reason-pc", "REASON-PC", tenantKey, DeviceRole.Client);
    await pc.ConnectAsync();
    await sue.WaitDevice(pc.Id, d => d.Online, 5000);

    Console.WriteLine("== the first connection to a machine ==");
    var a1 = await sue.ConnectAsync(pc.Id, "");
    Check("with no reason and no ticket to carry on, the hub asks for one — NeedsReason, naming the machine",
        a1.Error is { NeedsReason: true } e1 && e1.DeviceId == pc.Id && a1.Session == null, a1.Describe());
    var a2 = await sue.ConnectAsync(pc.Id, "ab");
    Check("two characters is still not a reason", a2.Error is { NeedsReason: true } && a2.Session == null, a2.Describe());
    var s1 = await sue.ConnectAsync(pc.Id, "Printer will not print");
    Check("with a reason the session starts", s1.Session != null, s1.Describe());
    var t1 = await TicketOfSessionAsync(s1.Session);
    Check("...on a ticket whose subject is the reason", t1 is { Subject: "Printer will not print" }, t1?.Subject ?? "no ticket");
    await sue.EndAsync(s1.Session!, SessionEndedAsync);

    Console.WriteLine();
    Console.WriteLine("== reconnecting straight after ==");
    var s2 = await sue.ConnectAsync(pc.Id, "");
    Check("with NO reason, the session starts — nothing asked", s2.Session != null && s2.Error == null, s2.Describe());
    var t2 = await TicketOfSessionAsync(s2.Session);
    Check("...on the SAME ticket", t2 != null && t2.Id == t1?.Id, $"#{t1?.Number} then #{t2?.Number}");
    Check("...whose subject is still the first reason, not \"Remote session on …\"", t2 is { Subject: "Printer will not print" }, t2?.Subject ?? "");
    Check("and the machine still has one ticket, not two", await TicketCountAsync(pc.Id) == 1, $"{await TicketCountAsync(pc.Id)}");
    await sue.EndAsync(s2.Session!, SessionEndedAsync);

    Console.WriteLine();
    Console.WriteLine("== after a long session ==");
    // Opened two and a half hours ago, its last session ended five minutes ago: the old rule (from the OPENING)
    // called this a new piece of work and asked why all over again.
    await AgeAsync(t1!.Id, openedMinsAgo: 150, lastEndedMinsAgo: 5);
    var s3 = await sue.ConnectAsync(pc.Id, "");
    Check("opened 2½ hours ago, last session ended 5 minutes ago: no reason asked, same ticket",
        s3.Session != null && (await TicketOfSessionAsync(s3.Session))?.Id == t1.Id, s3.Describe());
    if (s3.Session != null) await sue.EndAsync(s3.Session, SessionEndedAsync);

    await AgeAsync(t1.Id, openedMinsAgo: 150, lastEndedMinsAgo: 31);
    var a4 = await sue.ConnectAsync(pc.Id, "");
    Check("last session ended 31 minutes ago: a reason IS asked — it is new work", a4.Error is { NeedsReason: true } && a4.Session == null, a4.Describe());
    var s4 = await sue.ConnectAsync(pc.Id, "Now the scanner");
    var t4 = await TicketOfSessionAsync(s4.Session);
    Check("...and with one it is a NEW ticket", t4 != null && t4.Id != t1.Id && t4.Subject == "Now the scanner", $"#{t4?.Number} {t4?.Subject}");
    if (s4.Session != null) await sue.EndAsync(s4.Session, SessionEndedAsync);

    // ================================================================ a machine with an unattended password
    Console.WriteLine();
    Console.WriteLine("== a machine with an unattended password ==");
    var tray = new FakeMachine(ws, "pw-pc", "PW-PC", tenantKey, DeviceRole.Client);
    var agent = new FakeMachine(ws, "pw-pc", "PW-PC", tenantKey, DeviceRole.Agent);
    await tray.ConnectAsync(); await agent.ConnectAsync();
    tray.Send(new SetUnattendedMsg { On = true });
    await Task.Delay(500);
    tray.Send(new SetUnattendedPasswordMsg { Password = "Corr3ct-Horse" });
    Check("(it reads unattended, with a password)", await sue.WaitDevice(tray.Id, d => d.Unattended && d.UnattendedPassword, 5000), sue.Describe(tray.Id));

    var p1 = await sue.ConnectAsync(tray.Id, "");
    Check("no ticket yet: the REASON is asked first (the console then asks both in one window)",
        p1.Error is { NeedsReason: true, NeedsPassword: false } && p1.Session == null, p1.Describe());
    var p2 = await sue.ConnectAsync(tray.Id, "Backups failing", "Corr3ct-Horse");
    Check("reason and password: in", p2.Session != null, p2.Describe());
    var pt = await TicketOfSessionAsync(p2.Session);
    if (p2.Session != null) await sue.EndAsync(p2.Session, SessionEndedAsync);

    var p3 = await sue.ConnectAsync(tray.Id, "");
    Check("Sue again, inside the window: nothing asked at all — the reason is carried on, her password remembered",
        p3.Session != null && p3.Error == null && (await TicketOfSessionAsync(p3.Session))?.Id == pt?.Id, p3.Describe());
    if (p3.Session != null) await sue.EndAsync(p3.Session, SessionEndedAsync);

    var bob = await Tech.OpenAsync(http, ws, "bob@local.test");
    var p4 = await bob.ConnectAsync(tray.Id, "");
    Check("Bob, who has never typed it: asked the PASSWORD only — not a reason, there is a ticket",
        p4.Error is { NeedsPassword: true, NeedsReason: false } && p4.Session == null, p4.Describe());
    Check("...and told none was given, so his console does not show it as a wrong password",
        p4.Error is { NoPasswordGiven: true }, p4.Describe());
    var p5 = await bob.ConnectAsync(tray.Id, "", "nope-nope");
    Check("a wrong one is refused as wrong — NoPasswordGiven false", p5.Error is { NeedsPassword: true, NoPasswordGiven: false } && p5.Session == null, p5.Describe());
    var p6 = await bob.ConnectAsync(tray.Id, "", "Corr3ct-Horse");
    Check("the right one, still no reason: in, on the same ticket",
        p6.Session != null && (await TicketOfSessionAsync(p6.Session))?.Id == pt?.Id, p6.Describe());
    Check("the password machine has one ticket", await TicketCountAsync(tray.Id) == 1, $"{await TicketCountAsync(tray.Id)}");

    sue.Dispose(); bob.Dispose(); pc.Drop(); tray.Drop(); agent.Drop();
}

PlatformDb Db() => new(new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options);

async Task<Ticket?> TicketOfSessionAsync(string? sessionId)
{
    if (sessionId == null) return null;
    // The hub links the session row to its ticket a moment after the session starts.
    for (int i = 0; i < 40; i++)
    {
        await using var db = Db();
        var tid = await db.Sessions.Where(s => s.SessionId == sessionId).Select(s => s.TicketId).FirstOrDefaultAsync();
        if (tid != null) return await db.Tickets.FirstOrDefaultAsync(t => t.Id == tid);
        await Task.Delay(150);
    }
    return null;
}

async Task<bool> SessionEndedAsync(string sessionId)
{
    await using var db = Db();
    return await db.Sessions.AnyAsync(s => s.SessionId == sessionId && s.EndedUtc != null);
}

async Task<int> TicketCountAsync(string deviceId) { await using var db = Db(); return await db.Tickets.CountAsync(t => t.DeviceId == deviceId); }

/// <summary>Makes a ticket look opened this long ago, and its every session as having ended this long ago.</summary>
async Task AgeAsync(int ticketId, int openedMinsAgo, int lastEndedMinsAgo)
{
    await using var db = Db();
    var t = await db.Tickets.FirstAsync(x => x.Id == ticketId);
    t.CreatedUtc = DateTime.UtcNow.AddMinutes(-openedMinsAgo);
    foreach (var s in await db.Sessions.Where(s => s.TicketId == ticketId).ToListAsync())
    {
        s.StartedUtc = DateTime.UtcNow.AddMinutes(-openedMinsAgo);
        s.EndedUtc = DateTime.UtcNow.AddMinutes(-lastEndedMinsAgo);
    }
    await db.SaveChangesAsync();
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

/// <summary>What one connect came to: a session, or the error the hub sent instead.</summary>
sealed record Answer(string? Session, ErrorMsg? Error)
{
    public string Describe() => Session != null ? "session started"
        : Error == null ? "no answer"
        : $"refused (reason {Error.NeedsReason}, password {Error.NeedsPassword}, none given {Error.NoPasswordGiven}): {Error.Message}";
}

/// <summary>A technician's console connection.</summary>
sealed class Tech : IDisposable
{
    private readonly ClientWebSocket _raw = new();
    private WsConnection _c = null!;
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
    public readonly ConcurrentQueue<Msg> Seen = new();

    public static async Task<Tech> OpenAsync(string http, string ws, string email)
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
        _ = t._c.RunAsync();
        await Task.Delay(600);
        return t;
    }

    public DeviceInfo? Latest(string id) => _devices.TryGetValue(id, out var d) ? d : null;
    public string Describe(string id) => Latest(id) is { } d ? $"online={d.Online} unattended={d.Unattended} password={d.UnattendedPassword}" : "not in the list";

    public async Task<bool> WaitDevice(string id, Func<DeviceInfo, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (Latest(id) is { } d && f(d)) return true; await Task.Delay(50); } return false; }

    /// <summary>Connects as the console does now: whatever reason and password it has, possibly none.</summary>
    public async Task<Answer> ConnectAsync(string deviceId, string reason, string password = "")
    {
        var mark = Seen.Count;
        _c.Send(new ConnectDeviceMsg { DeviceId = deviceId, Reason = reason, Password = password });
        var e = DateTime.UtcNow.AddMilliseconds(6000);
        while (DateTime.UtcNow < e)
        {
            foreach (var m in Seen.Skip(mark))
            {
                if (m is SessionReadyMsg r && r.Session.DeviceId == deviceId) return new Answer(r.Session.SessionId, null);
                if (m is ErrorMsg er) return new Answer(null, er);
            }
            await Task.Delay(50);
        }
        return new Answer(null, null);
    }

    /// <summary>Ends a session and waits until its row says it ended — the time the next one is measured from.</summary>
    public async Task EndAsync(string sessionId, Func<string, Task<bool>> ended)
    {
        _c.Send(new EndSessionMsg { SessionId = sessionId });
        var e = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < e) { if (await ended(sessionId)) return; await Task.Delay(150); }
        throw new Exception($"session {sessionId} never recorded its end");
    }

    public void Dispose() { try { _raw.Abort(); } catch { } }
}

/// <summary>One of a machine's connections, which hosts any session it is handed.</summary>
sealed class FakeMachine
{
    private readonly string _ws, _tenantKey;
    private readonly DeviceRole _role;
    public readonly string Id, Name;
    private ClientWebSocket? _raw;
    private WsConnection? _c;

    public FakeMachine(string ws, string id, string name, string tenantKey, DeviceRole role) { _ws = ws; Id = id; Name = name; _tenantKey = tenantKey; _role = role; }

    public async Task ConnectAsync()
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _c.OnMessage += m => { if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) _ = HostAsync(s); };
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = _role, DeviceId = Id, DeviceKey = Id + "-key", MachineName = Name, UserName = "FakeUser",
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
