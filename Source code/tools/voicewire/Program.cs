using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Host;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;
using RemoteSupport.Tools;

// ---------------------------------------------------------------------------------------------
// voicewire - talking to the customer (3.3, 2026-09-30), over a REAL hub.
//
// Clint's rules: the technician's voice needs no permission; the customer's MICROPHONE opens only
// when THEY allow it, every session; never in an unattended session. Recordings stay picture only.
//
// The route is the thing under test, because it is new in both directions: the technician's Talk
// goes viewer -> hub -> host, the host's state goes host -> hub -> the TRAY APP (on another socket
// entirely), and the person's answer goes tray app -> hub -> host. The hub is also where the two
// rules that protect the customer live — only the tray app may answer, and never in an unattended
// session — so it is tested for real, against a platform of its own (port 8111, a throwaway
// database; the live server is never touched).
//
// The pretend machine answers with the HOST'S OWN VoiceCall — the real echo-cancelled microphone
// and the real player, on THIS laptop — behind a hand-written host socket. So "the microphone
// opened and its frames reached the technician" is the real code saying so, not a stub.
//
//   dotnet run -c Release --project tools/voicewire/voicewire.csproj
//
// It opens this laptop's microphone for a few seconds and plays SILENCE (and a quiet test signal
// fed as the technician's voice) through the communications device.
// ---------------------------------------------------------------------------------------------

const int Port = 8111;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("voicewire - talking to the customer, over a real hub");
Console.WriteLine();

var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-voicewire-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
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
foreach (var cmd in new[] { "create-tenant \"Voice Wire\" voice-wire 5", "approve voice-wire", "add-tech voice-wire tech@local.test localtest \"Local Tech\" --admin" })
{
    var r = Run(exe, cmd, serverDir, 60000, env);
    if (r.code != 0) { Console.WriteLine(r.output); Console.WriteLine("CLI step failed: " + cmd); return 2; }
    var line = r.output.Split('\n').FirstOrDefault(l => l.StartsWith("Tenant key:"));
    if (line != null) tenantKey = line["Tenant key:".Length..].Trim();
}
if (tenantKey.Length == 0) { Console.WriteLine("could not read the tenant key"); return 2; }

Process? server = null;
var psi = new ProcessStartInfo(exe, $"run --urls {http}") { WorkingDirectory = serverDir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
var serverLog = new ConcurrentQueue<string>();
server = Process.Start(psi)!;
server.OutputDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.ErrorDataReceived += (_, e) => { if (e.Data != null) serverLog.Enqueue(e.Data); };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
bool up = false;
using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
    for (int i = 0; i < 60 && !up; i++) { try { up = (await probe.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { } if (!up) await Task.Delay(500); }
if (!up) { Console.WriteLine("the platform did not come up"); Kill(); return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { Kill(); VoiceHost.EndAll(); }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

void Kill() { try { if (server is { HasExited: false }) server.Kill(true); server?.WaitForExit(5000); } catch { } }

async Task ChecksAsync()
{
    var tech = await Tech.OpenAsync(http, ws);

    // ================================================================== an attended call
    Console.WriteLine("== an attended session: the customer asked for help ==");
    const string DevA = "voice-wire-a", KeyA = "voice-wire-a-key", NameA = "VOICE-A-PC";
    var tray = new FakeConn(ws, DevA, KeyA, NameA, tenantKey, DeviceRole.Client);
    await tray.ConnectAsync();
    await tech.WaitDevice(DevA, d => d.Online, 5000);
    tray.Send(new RequestCallMsg { ContactName = "Pat Customer", Issue = "voicewire: my sound is broken" });
    var ring = await tech.WaitFor<RingMsg>(r => r.Call.DeviceId == DevA, 5000);
    Check("the customer's request rings", ring != null);
    if (ring == null) return;
    var ready = await tech.AcceptAsync(ring.Call.CallId, DevA);
    Check("the technician answers and a session starts", ready != null);
    if (ready == null) return;
    var sid = ready.SessionId;
    var host = await VoiceHost.WaitFor(sid, 5000);
    Check("the tray app hosts it (an attended session)", host != null && host.Role == DeviceRole.Client && !ready.Unattended);
    if (host == null) return;
    var viewer = await Viewer.OpenAsync(ws, sid, tech.Token);
    Check("the technician's session window is connected and the host is there", await viewer.WaitHost(5000));

    // ---- Talk ----
    Console.WriteLine();
    Console.WriteLine("== Talk ==");
    viewer.Send(new VoiceTalkMsg { On = true, Technician = "Somebody Else" });
    var talk = await WaitValue(() => host.Talks.LastOrDefault(), 4000);
    Check("the host is told a technician is talking", talk is { On: true });
    Check("...in the name the HUB knows, not the one the console claimed", talk?.Technician == "Local Tech", talk?.Technician ?? "nothing");
    var askTray = await tray.WaitVoice(v => v.Call && v.Mic == VoiceMic.Asking, 5000);
    Check("the person at the machine is ASKED about their microphone, in their tray app", askTray != null, Describe(tray.Voice.LastOrDefault()));
    Check("...and told who is talking", askTray?.Technicians == "Local Tech", askTray?.Technicians ?? "");
    Check("the technician's window is told the customer is being asked", await viewer.WaitVoice(v => v.Mic == VoiceMic.Asking, 3000) != null);
    Check("the host's microphone is NOT open before they answer", !host.MicOpen);
    Check("the ticket says so", await Note(DevA, "started talking to the customer"), "a line about Local Tech talking");

    // ---- the technician's voice reaches the customer's speakers ----
    Console.WriteLine();
    Console.WriteLine("== the technician's voice ==");
    int playedBefore = host.Call.PacketsPlayed;
    await viewer.SendToneAsync(TimeSpan.FromSeconds(1.2));
    await Task.Delay(500);
    Check("the technician's voice frames reached the host", host.Call.FramesIn >= 50, $"{host.Call.FramesIn} of 60");
    Check("...and were PLAYED on the customer's speakers", host.Call.PacketsPlayed - playedBefore >= 40, $"{host.Call.PacketsPlayed - playedBefore} played");

    // ---- only the tray app may answer ----
    Console.WriteLine();
    Console.WriteLine("== who may answer ==");
    var agentA = new FakeConn(ws, DevA, KeyA, NameA, tenantKey, DeviceRole.Agent);
    await agentA.ConnectAsync();
    agentA.Send(new VoiceMicAnswerMsg { Allow = true });
    await Task.Delay(1200);
    Check("the AGENT saying yes is refused — nobody clicked anything there", host.Answers.Count == 0 && !host.MicOpen, $"{host.Answers.Count} answers reached the host");
    viewer.Send(new VoiceMicAnswerMsg { Allow = true });
    await Task.Delay(1000);
    Check("a TECHNICIAN saying yes on the customer's behalf does nothing", !host.MicOpen, host.MicOpen ? "the microphone opened" : "");

    // ---- the customer allows it ----
    Console.WriteLine();
    Console.WriteLine("== the customer says yes ==");
    tray.Send(new VoiceMicAnswerMsg { Allow = true });
    var on = await viewer.WaitVoice(v => v.Mic is VoiceMic.On or VoiceMic.Unavailable, 6000);
    Check("the technician is told their microphone is on", on?.Mic == VoiceMic.On, Describe(on));
    Check("the person at the machine is told too", await tray.WaitVoice(v => v.Mic == VoiceMic.On, 3000) != null);
    int f0 = viewer.VoiceFrames;
    await Task.Delay(1000);
    int perSecond = viewer.VoiceFrames - f0;
    Check("the customer's voice reaches the technician, 50 frames a second", perSecond >= 40 && perSecond <= 60, $"{perSecond} in 1 s");
    Check("...as 16 kHz mono 16-bit, the voice format", viewer.LastFormat == (VoiceWire.Rate, 1, AudioFormat.Pcm16), viewer.LastFormat.ToString());
    Check("the ticket records that the customer allowed it", await Note(DevA, "allowed their microphone"));

    // ---- a tray app that blinks is asked again ----
    Console.WriteLine();
    Console.WriteLine("== the tray app reconnects mid-call ==");
    tray.Drop();
    await Task.Delay(500);
    tray.Voice.Clear();
    await tray.ConnectAsync();
    Check("it is told the call is still on, and its microphone with it", await tray.WaitVoice(v => v.Call && v.Mic == VoiceMic.On, 4000) != null, Describe(tray.Voice.LastOrDefault()));

    // ---- they change their mind ----
    Console.WriteLine();
    Console.WriteLine("== the customer turns it off ==");
    tray.Send(new VoiceMicAnswerMsg { Allow = false });
    Check("the technician is told", await viewer.WaitVoice(v => v.Mic == VoiceMic.Declined, 4000) != null);
    await Task.Delay(300);
    int f1 = viewer.VoiceFrames;
    await Task.Delay(1000);
    Check("and the customer's voice STOPS", viewer.VoiceFrames - f1 <= 1, $"{viewer.VoiceFrames - f1} frames after they said stop");
    Check("the host's microphone is closed", !host.MicOpen);
    Check("the ticket records it", await Note(DevA, "turned their microphone off"));

    // ---- the technician vanishes without saying stop ----
    Console.WriteLine();
    Console.WriteLine("== the technician's window disappears mid-call ==");
    tray.Send(new VoiceMicAnswerMsg { Allow = true });
    Check("(their microphone is on again)", await viewer.WaitVoice(v => v.Mic == VoiceMic.On, 5000) != null);
    viewer.Drop();
    var ended = await tray.WaitVoice(v => !v.Call, 6000);
    Check("the call ends at the customer's end — their microphone does not stay open for nobody", ended != null && !host.MicOpen,
        host.MicOpen ? "the microphone is STILL open" : Describe(tray.Voice.LastOrDefault()));

    // ================================================================== unattended
    Console.WriteLine();
    Console.WriteLine("== an UNATTENDED session ==");
    const string DevU = "voice-wire-u", KeyU = "voice-wire-u-key", NameU = "VOICE-U-PC";
    var trayU = new FakeConn(ws, DevU, KeyU, NameU, tenantKey, DeviceRole.Client);
    var agentU = new FakeConn(ws, DevU, KeyU, NameU, tenantKey, DeviceRole.Agent);
    await trayU.ConnectAsync(); await agentU.ConnectAsync();
    trayU.Send(new SetUnattendedMsg { On = true });
    await tech.WaitDevice(DevU, d => d.Unattended, 5000);
    var (sidU, errU) = await tech.ConnectAsync(DevU, "voicewire, unattended");
    Check("a technician connects without anybody accepting", sidU != null, errU);
    if (sidU == null) return;
    var hostU = await VoiceHost.WaitFor(sidU, 5000);
    Check("the agent hosts it", hostU?.Role == DeviceRole.Agent);
    if (hostU == null) return;
    var viewerU = await Viewer.OpenAsync(ws, sidU, tech.Token);
    await viewerU.WaitHost(5000);
    viewerU.Send(new VoiceTalkMsg { On = true });
    var un = await trayU.WaitVoice(v => v.Call, 5000);
    Check("the person's tray app is TOLD a technician is talking", un != null);
    Check("...but NOT asked: \"unattended\", not \"asking\"", un?.Mic == VoiceMic.Unattended, Describe(un));
    Check("the technician is told why", (await viewerU.WaitVoice(v => v.Mic == VoiceMic.Unattended, 3000))?.Detail.Contains("unattended") == true);
    trayU.Send(new VoiceMicAnswerMsg { Allow = true });
    await Task.Delay(1500);
    Check("a yes from the tray app is REFUSED in an unattended session", hostU.Answers.Count == 0 && !hostU.MicOpen,
        $"{hostU.Answers.Count} answers reached the host, microphone {(hostU.MicOpen ? "OPEN" : "closed")}");
    Check("...so nothing of the room reaches the technician", viewerU.VoiceFrames == 0, $"{viewerU.VoiceFrames} frames");
    Check("the server says so in its log", serverLog.Any(l => l.Contains("microphone answer") && l.Contains("unattended")));

    viewerU.Send(new VoiceTalkMsg { On = false });
    Check("Talk off ends the call at their end", await trayU.WaitVoice(v => !v.Call, 4000) != null);
    Check("the ticket says the technician stopped", await Note(DevU, "stopped talking"));

    // ---- recording stays picture only: there is nothing in this route that records ----
    tech.End(sidU);
    tech.Dispose();
}

async Task<bool> Note(string deviceId, string contains)
{
    var o = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    // By the MACHINE's tickets: SessionLog has no row until the session ends, which is how the
    // first run read three lines that were there as missing.
    var until = DateTime.UtcNow.AddSeconds(7);
    while (DateTime.UtcNow < until)
    {
        await using (var db = new PlatformDb(o))
        {
            var tickets = await db.Tickets.Where(t => t.DeviceId == deviceId).Select(t => t.Id).ToListAsync();
            if (await db.TicketNotes.AnyAsync(n => tickets.Contains(n.TicketId) && n.Body.Contains(contains))) return true;
        }
        await Task.Delay(200);
    }
    // Say what IS there: a count is not a reading (§40).
    await using (var db = new PlatformDb(o))
    {
        var mine = await db.Tickets.Where(t => t.DeviceId == deviceId).Select(t => t.Id).ToListAsync();
        Console.WriteLine($"        {deviceId}: tickets {string.Join(", ", mine)}");
        foreach (var n in await db.TicketNotes.OrderBy(n => n.Id).Select(n => new { n.TicketId, n.Body }).ToListAsync())
            Console.WriteLine($"        ticket {n.TicketId}: {n.Body}");
    }
    return false;
}

static string Describe(VoiceStateMsg? v) => v == null ? "nothing arrived" : $"call={v.Call} mic={v.Mic} who={v.Technicians} {v.Detail}";

static async Task<T?> WaitValue<T>(Func<T?> f, int ms) where T : class
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { var v = f(); if (v != null) return v; await Task.Delay(50); } return null; }

static (int code, string output) Run(string file, string argLine, string cwd, int ms, Dictionary<string, string>? env = null)
{
    var p0 = new ProcessStartInfo(file, argLine) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (env != null) foreach (var kv in env) p0.Environment[kv.Key] = kv.Value;
    using var p = Process.Start(p0)!;
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

/// <summary>A technician's console connection.</summary>
sealed class Tech : IDisposable
{
    private readonly ClientWebSocket _raw = new();
    private WsConnection _c = null!;
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new();
    public readonly ConcurrentQueue<Msg> Seen = new();
    public string Token = "";

    public static async Task<Tech> OpenAsync(string http, string ws)
    {
        using var hc = new HttpClient();
        var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = "tech@local.test", password = "localtest" });
        var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
        var t = new Tech { Token = tok };
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

    public async Task<bool> WaitDevice(string id, Func<DeviceInfo, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (_devices.TryGetValue(id, out var d) && f(d)) return true; await Task.Delay(50); } return false; }

    public async Task<T?> WaitFor<T>(Func<T, bool> f, int ms) where T : Msg
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in Seen) if (m is T t && f(t)) return t; await Task.Delay(50); } return null; }

    public async Task<SessionInfo?> AcceptAsync(string callId, string deviceId)
    {
        _c.Send(new AcceptCallMsg { CallId = callId });
        return (await WaitFor<SessionReadyMsg>(r => r.Session.DeviceId == deviceId, 6000))?.Session;
    }

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

/// <summary>A technician's session window: the viewer socket.</summary>
sealed class Viewer
{
    private ClientWebSocket _raw = null!;
    private WsConnection _c = null!;
    public readonly ConcurrentQueue<VoiceStateMsg> Voice = new();
    public int VoiceFrames;
    public (int rate, int channels, byte format) LastFormat;
    private volatile bool _hostSeen;

    public static async Task<Viewer> OpenAsync(string ws, string sessionId, string token)
    {
        var v = new Viewer { _raw = new ClientWebSocket() };
        await v._raw.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={token}"), default);
        v._c = new WsConnection(v._raw);
        v._c.OnMessage += m =>
        {
            if (m is MonitorsMsg || m is ParticipantsMsg { HostConnected: true }) v._hostSeen = true;
            if (m is VoiceStateMsg s) v.Voice.Enqueue(s);
        };
        v._c.OnBinary += f =>
        {
            if (f.Length > Bin.AudioHeader && f.Span[0] == BinKind.Voice)
            {
                var (_, rate, ch, fmt, _) = Bin.ParseAudio(f);
                v.LastFormat = (rate, ch, fmt);
                Interlocked.Increment(ref v.VoiceFrames);
            }
        };
        _ = v._c.RunAsync();
        return v;
    }

    public async Task<bool> WaitHost(int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (_hostSeen) return true; await Task.Delay(50); } return false; }

    public async Task<VoiceStateMsg?> WaitVoice(Func<VoiceStateMsg, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { var hit = Voice.LastOrDefault(); if (hit != null && f(hit)) return hit; await Task.Delay(50); } return null; }

    public void Send(Msg m) => _c.Send(m);

    /// <summary>A quiet 440 Hz test signal as the technician's voice, one 20 ms frame every 20 ms.</summary>
    public async Task SendToneAsync(TimeSpan length)
    {
        uint seq = 0;
        var start = DateTime.UtcNow;
        long sent = 0;
        while (DateTime.UtcNow - start < length)
        {
            long due = (long)((DateTime.UtcNow - start).TotalMilliseconds / VoiceWire.FrameMs);
            while (sent <= due)
            {
                var pcm = new byte[VoiceWire.FrameSamples * 2];
                for (int i = 0; i < VoiceWire.FrameSamples; i++)
                {
                    short s = (short)(1500 * Math.Sin(2 * Math.PI * 440 * (sent * VoiceWire.FrameSamples + i) / VoiceWire.Rate));
                    pcm[i * 2] = (byte)(s & 0xFF); pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                _c.SendBinary(Bin.Voice(seq++, pcm));
                sent++;
            }
            await Task.Delay(5);
        }
    }

    public void Drop() { try { _raw.Abort(); } catch { } }
}

/// <summary>One of a machine's two connections: the tray app (Client) or the agent service.
/// Whichever is handed a session hosts it with a <see cref="VoiceHost"/>.</summary>
sealed class FakeConn
{
    private readonly string _ws, _id, _key, _name, _tenantKey;
    private readonly DeviceRole _role;
    private ClientWebSocket? _raw;
    private WsConnection? _c;
    public readonly ConcurrentQueue<VoiceStateMsg> Voice = new();

    public FakeConn(string ws, string id, string key, string name, string tenantKey, DeviceRole role)
    { _ws = ws; _id = id; _key = key; _name = name; _tenantKey = tenantKey; _role = role; }

    public async Task ConnectAsync()
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _c.OnMessage += m =>
        {
            if (m is StartSessionMsg s && !string.IsNullOrEmpty(s.HostToken)) _ = VoiceHost.StartAsync(_ws, s, _role);
            if (m is VoiceStateMsg v) Voice.Enqueue(v);
        };
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
    public void Drop() { try { _raw?.Abort(); } catch { } }

    public async Task<VoiceStateMsg?> WaitVoice(Func<VoiceStateMsg, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { var hit = Voice.LastOrDefault(); if (hit != null && f(hit)) return hit; await Task.Delay(50); } return null; }
}

/// <summary>
/// The pretend machine's host: a hand-written host socket in front of the HOST'S OWN VoiceCall, so
/// the microphone, the player and every rule in VoiceCall are the real ones.
/// </summary>
sealed class VoiceHost
{
    private static readonly ConcurrentDictionary<string, VoiceHost> _all = new();
    public DeviceRole Role;
    public VoiceCall Call = null!;
    public readonly ConcurrentQueue<VoiceTalkMsg> Talks = new();
    public readonly ConcurrentQueue<VoiceMicAnswerMsg> Answers = new();
    private VoiceStateMsg? _last;
    public bool MicOpen => _last is { Mic: VoiceMic.On };

    public static async Task StartAsync(string ws, StartSessionMsg s, DeviceRole role)
    {
        var raw = new ClientWebSocket();
        await raw.ConnectAsync(new Uri($"{ws}/ws/session?role=host&id={s.SessionId}&token={s.HostToken}"), default);
        var c = new WsConnection(raw);
        var h = new VoiceHost { Role = role };
        h.Call = new VoiceCall(m => { if (m is VoiceStateMsg v) h._last = v; c.Send(m); }, f => c.SendBinary(f));
        c.OnMessage += m =>
        {
            switch (m)
            {
                case VoiceTalkMsg t: h.Talks.Enqueue(t); h.Call.Talk(t); break;
                case VoiceMicAnswerMsg a: h.Answers.Enqueue(a); h.Call.MicAnswer(a.Allow); break;
                case ParticipantsMsg p: h.Call.Participants(p.Technicians); break;
                case SessionEndedMsg: h.Call.End(); _ = c.CloseAsync("ended"); break;
            }
        };
        c.OnBinary += f => { if (f.Length > 0 && f.Span[0] == BinKind.Voice) h.Call.Frame(f); };
        _ = c.RunAsync();
        c.Send(new MonitorsMsg { Monitors = new List<MonitorInfo> { new() { Index = 0, Width = 256, Height = 256, Primary = true } } });
        _all[s.SessionId] = h;
    }

    public static async Task<VoiceHost?> WaitFor(string sessionId, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (_all.TryGetValue(sessionId, out var h)) return h; await Task.Delay(50); } return null; }

    public static void EndAll() { foreach (var h in _all.Values) try { h.Call.End(); } catch { } }
}
