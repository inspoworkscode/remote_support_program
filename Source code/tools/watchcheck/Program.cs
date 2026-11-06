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
// watchcheck - Processes and Performance, a look at a machine WITHOUT connecting (2026-10-05).
//
// Clint: right-click a machine, Processes or Performance, "so that the technician can see
// performance before even connecting". His rules: straight through on EVERY online machine,
// attended or unattended, no consent prompt and no unattended password; look only; nothing logged
// and no ticket.
//
// Part 1 measures THIS PC with the real MachineWatcher and checks the numbers against what Windows
// and .NET say independently. Part 2 runs its own platform on port 8121 against a throwaway
// database, with pretend machines and two technicians; the live server is never touched.
//
//   dotnet run -c Release --project tools/watchcheck/watchcheck.csproj
//   ... -- --local      part 1 only
// ---------------------------------------------------------------------------------------------

const int Port = 8121;
string http = $"http://127.0.0.1:{Port}", ws = $"ws://127.0.0.1:{Port}";

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}
void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

Console.WriteLine("watchcheck - Processes and Performance without connecting");

// =============================================================================== part 1
Console.WriteLine();
Console.WriteLine("== part 1: this PC, measured for real ==");
{
    var samples = new ConcurrentQueue<MachineSample>();
    var timed = new MachineWatcher(s => samples.Enqueue(s));
    timed.Set(true);
    await Task.Delay(4600);
    timed.Set(false);
    int after = samples.Count;
    await Task.Delay(1600);

    var list = samples.ToList();
    Check("it sends about once a second while on", list.Count is >= 3 and <= 6, $"{list.Count} readings in 4.6 s");
    Check("and nothing once switched off", samples.Count == after, $"{samples.Count - after} after Off");
    Check("the process list rides on every OTHER reading (it is most of the size)",
        list.Count >= 3 && list[0].Processes != null && list[1].Processes == null && list[2].Processes != null,
        string.Join(",", list.Select(s => s.Processes == null ? "-" : "P")));

    var full = list.FirstOrDefault(s => s.Processes != null);
    if (full == null) { Check("a reading with processes arrived", false); }
    else
    {
        Check("processor between 0 and 100", full.CpuPercent is >= 0 and <= 100, $"{full.CpuPercent:0.0}%");
        Check("logical processors as .NET counts them", full.LogicalProcessors == Environment.ProcessorCount, $"{full.LogicalProcessors}");
        long gcTotalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        Check("memory total agrees with .NET's own figure (within 2%)",
            Math.Abs(full.MemoryTotalMb - gcTotalMb) <= gcTotalMb * 0.02, $"{full.MemoryTotalMb} MB against {gcTotalMb} MB");
        Check("memory in use is between nothing and all of it", full.MemoryUsedMb > 0 && full.MemoryUsedMb < full.MemoryTotalMb, $"{full.MemoryUsedMb} MB");
        Check("disk active time is a percentage, or -1 for 'Windows would not say'",
            full.DiskActivePercent is -1 or (>= 0 and <= 100), $"{full.DiskActivePercent:0.0}");
        Check("network rates are not negative", full.NetReceiveBytesPerSec >= 0 && full.NetSendBytesPerSec >= 0,
            $"in {full.NetReceiveBytesPerSec} B/s, out {full.NetSendBytesPerSec} B/s");
        Check("uptime agrees with Windows' tick count", Math.Abs(full.UptimeSeconds - Environment.TickCount64 / 1000) <= 10, $"{full.UptimeSeconds} s");

        // Private or Public, and the address (Clint, 2026-10-05), against Windows' OWN answer —
        // PowerShell's Get-NetConnectionProfile and Get-NetIPAddress, a different route to the same facts.
        var ps = Run("powershell", "-NoProfile -Command \"Get-NetConnectionProfile | ForEach-Object { $a = (Get-NetIPAddress -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue).IPAddress -join ', '; $_.Name + '|' + $_.InterfaceAlias + '|' + $_.NetworkCategory + '|' + $a }\"", Environment.CurrentDirectory, 30000);
        var windows = ps.output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|')).Where(p => p.Length == 4)
            .Select(p => (Name: p[0], Adapter: p[1], Category: p[2] == "DomainAuthenticated" ? "Domain" : p[2], Address: p[3])).ToList();
        var ours = full.Networks ?? new List<NetworkProfile>();
        if (MachineWatcher.NetworkError != null) Console.WriteLine("        reading the networks failed: " + MachineWatcher.NetworkError);
        Check("it reports the networks Windows says it is connected to", full.Networks != null && ours.Count == windows.Count,
            $"{ours.Count} ours, {windows.Count} Windows': {string.Join("; ", windows.Select(w => $"{w.Name} {w.Category}"))}");
        foreach (var w in windows)
        {
            var hers = ours.FirstOrDefault(n => n.Name == w.Name);
            Check($"\"{w.Name}\": Private/Public as Windows says", hers?.Category == w.Category, $"ours {hers?.Category ?? "missing"}, Windows {w.Category}");
            Check($"\"{w.Name}\": on the adapter Windows names", hers?.Adapter == w.Adapter, $"ours \"{hers?.Adapter}\", Windows \"{w.Adapter}\"");
            Check($"\"{w.Name}\": the IP address Windows gives", hers != null && w.Address.Length > 0 && hers.Address == w.Address, $"ours \"{hers?.Address}\", Windows \"{w.Address}\"");
        }

        var procs = full.Processes!;
        int dotnetCount = Process.GetProcesses().Length;
        Check("about as many processes as .NET lists (within 10%)", Math.Abs(procs.Count - dotnetCount) <= Math.Max(5, dotnetCount / 10), $"{procs.Count} against {dotnetCount}");
        Check("the count it reports is the list's", full.ProcessCount == procs.Count, $"{full.ProcessCount} / {procs.Count}");
        Check("the idle 'process' is left out — it is the processor NOT working", procs.All(p => p.Pid != 0));
        var me = procs.FirstOrDefault(p => p.Pid == Environment.ProcessId);
        Check("this very process is in the list, by its PID", me != null, me?.Name ?? "missing");
        if (me != null)
        {
            Check("...named as Windows names it", me.Name.StartsWith("watchcheck", StringComparison.OrdinalIgnoreCase), me.Name);
            long mineKb = Process.GetCurrentProcess().PrivateMemorySize64 / 1024;
            Check("...with a memory figure in the right range", me.MemoryKb > 1024 && me.MemoryKb <= mineKb * 2, $"{me.MemoryKb} KB (private bytes {mineKb} KB)");
            Check("...owned by this account", string.Equals(me.User, Environment.UserName, StringComparison.OrdinalIgnoreCase), $"\"{me.User}\"");
        }
        // The point of NtQuerySystemInformation: a process this account could never open still shows
        // its memory — Process.GetProcesses would need a handle for its processor time.
        var lsass = procs.FirstOrDefault(p => p.Name.Equals("lsass.exe", StringComparison.OrdinalIgnoreCase));
        Check("a protected SYSTEM process (lsass) is listed, with its memory, without being opened",
            lsass != null && lsass.MemoryKb > 0, lsass == null ? "missing" : $"{lsass.MemoryKb} KB, owner \"{lsass.User}\"");
        var system = procs.FirstOrDefault(p => p.Pid == 4);
        Check("PID 4 is System, owned by SYSTEM", system is { Name: "System", User: "SYSTEM" }, $"{system?.Name} / {system?.User}");
        Check("every process has a name", procs.All(p => p.Name.Length > 0));
        Check("no process is over 100% of the machine", procs.All(p => p.CpuPercent is >= 0 and <= 100));
    }

    // The busy thread's share must show up: one core flat out is 100/N % of the machine.
    var cpuMeter = new MachineWatcher(_ => { });
    cpuMeter.Prime();   // the baseline, with no timer to read in between
    var until = DateTime.UtcNow.AddMilliseconds(1500);
    var busy = new Thread(() => { while (DateTime.UtcNow < until) { } }) { IsBackground = true };
    busy.Start(); busy.Join();
    var reading = cpuMeter.Read(true);
    var mine = reading.Processes?.FirstOrDefault(p => p.Pid == Environment.ProcessId);
    double oneCore = 100.0 / Environment.ProcessorCount;
    Check("a thread spinning for 1.5 s shows as about one core of this process",
        mine != null && mine.CpuPercent >= oneCore * 0.6 && mine.CpuPercent <= oneCore * 2.5,
        $"{mine?.CpuPercent:0.0}% (one core is {oneCore:0.0}%)");
    Check("...and the whole machine is at least that busy", reading.CpuPercent >= oneCore * 0.6, $"{reading.CpuPercent:0.0}%");
}

if (args.Contains("--local"))
{
    Console.WriteLine();
    Console.WriteLine($"{pass} of {pass + fail} checks passed.");
    return fail == 0 ? 0 : 1;
}

// =============================================================================== part 2
Console.WriteLine();
Console.WriteLine("== part 2: the server, on a throwaway platform ==");
var repo = FindRepoRoot();
var work = Path.Combine(Path.GetTempPath(), "rs-watchcheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
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
foreach (var cmd in new[]
{
    "create-tenant \"Watch Check\" watch-check 5", "approve watch-check",
    "add-tech watch-check one@local.test localtest \"Tech One\" --admin",
    "add-tech watch-check two@local.test localtest \"Tech Two\"",
})
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
server = Process.Start(psi)!;
server.OutputDataReceived += (_, _) => { }; server.ErrorDataReceived += (_, _) => { };
server.BeginOutputReadLine(); server.BeginErrorReadLine();
bool up = false;
using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
    for (int i = 0; i < 60 && !up; i++) { try { up = (await probe.GetAsync($"{http}/health")).IsSuccessStatusCode; } catch { } if (!up) await Task.Delay(500); }
if (!up) { Console.WriteLine("the platform did not come up"); server.Kill(true); return 2; }

try { await ChecksAsync(); }
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex); }
finally { try { server.Kill(true); server.WaitForExit(5000); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
Console.WriteLine($"server and database kept at {work}");
return fail == 0 ? 0 : 1;

async Task ChecksAsync()
{
    var one = await Tech.OpenAsync(http, ws, "one@local.test");
    var two = await Tech.OpenAsync(http, ws, "two@local.test");

    // ---- an ATTENDED machine: tray app only, nobody has allowed unattended access ----
    Console.WriteLine();
    Console.WriteLine("== an attended machine: straight through, nobody asked ==");
    const string Att = "watch-attended";
    var attTray = new FakeConn(ws, Att, "k-att", "ATTENDED-PC", tenantKey, DeviceRole.Client, watch: true, marker: 101);
    await attTray.ConnectAsync();
    Check("the console is told this machine can send figures",
        await one.WaitDevice(Att, d => d.Online && d.CanWatch && !d.Unattended, 4000), one.Describe(Att));
    one.Watch(Att, true);
    Check("the tray app is asked to send them — at once, with no question put to anybody",
        await WaitAsync(() => attTray.Running, 3000), $"runs: {string.Join(",", attTray.Runs)}");
    Check("the technician gets readings, labelled with the machine",
        await one.WaitSample(Att, s => s.Sample.ProcessCount == 101, 3000));
    Check("nobody else is sent them — Tech Two did not open the window",
        !two.Seen.OfType<WatchSampleMsg>().Any());
    Check("no session was started", !one.Seen.OfType<SessionReadyMsg>().Any() && attTray.SessionStarts == 0);

    // A second technician opens the same machine: the machine still measures ONCE.
    two.Watch(Att, true);
    await Task.Delay(600);
    Check("a second technician watching does not ask the machine twice", attTray.Runs.Count(r => r) == 1, string.Join(",", attTray.Runs));
    Check("...and gets the readings too", await two.WaitSample(Att, _ => true, 3000));
    one.Watch(Att, false);
    await Task.Delay(600);
    Check("the first closing does not stop it — the other is still looking", attTray.Running, string.Join(",", attTray.Runs));
    two.Watch(Att, false);
    Check("the last closing stops the machine measuring", await WaitAsync(() => !attTray.Running, 3000), string.Join(",", attTray.Runs));

    // ---- unattended allowed, WITH an unattended password: still straight through ----
    Console.WriteLine();
    Console.WriteLine("== unattended with a password: no password needed for a look ==");
    const string Pw = "watch-password";
    var pwTray = new FakeConn(ws, Pw, "k-pw", "PASSWORD-PC", tenantKey, DeviceRole.Client, watch: true, marker: 202);
    var pwAgent = new FakeConn(ws, Pw, "k-pw", "PASSWORD-PC", tenantKey, DeviceRole.Agent, watch: true, marker: 303);
    await pwTray.ConnectAsync(); await pwAgent.ConnectAsync();
    pwTray.Send(new SetUnattendedMsg { On = true });
    pwTray.Send(new SetUnattendedPasswordMsg { Password = "correct-horse-1" });
    Check("(the machine has unattended access and a password)",
        await one.WaitDevice(Pw, d => d.Unattended && d.UnattendedPassword, 4000), one.Describe(Pw));
    var (sess, err) = await one.ConnectAsync(Pw, "watchcheck: connect without the password");
    Check("(and connecting without the password is refused, as §77 says)", sess == null, err);
    one.Watch(Pw, true);
    Check("a look is NOT refused, and asks for no password",
        await one.WaitSample(Pw, _ => true, 3000) && !one.Seen.OfType<WatchEndedMsg>().Any(e => e.DeviceId == Pw));
    Check("the AGENT sends the figures where both are connected (it can name every account)",
        pwAgent.Running && !pwTray.Running, $"agent {pwAgent.Running}, tray {pwTray.Running}");
    Check("...and they are the agent's", await one.WaitSample(Pw, s => s.Sample.ProcessCount == 303, 3000));

    // A stray sender: the tray app sending figures nobody asked it for.
    var mark = one.Seen.Count;
    pwTray.SendSample(999);
    await Task.Delay(500);
    Check("figures from a connection that was not asked are dropped",
        !one.Seen.Skip(mark).OfType<WatchSampleMsg>().Any(s => s.Sample.ProcessCount == 999));
    Check("...and it is told to stop", pwTray.Runs.LastOrDefault() == false && pwTray.Runs.Count > 0, string.Join(",", pwTray.Runs));

    // The agent goes (a restart): the tray app takes over without the technician doing anything.
    pwAgent.Drop();
    Check("when the agent goes, the tray app takes over", await WaitAsync(() => pwTray.Running, 4000), string.Join(",", pwTray.Runs));
    Check("...and the window keeps filling", await one.WaitSample(Pw, s => s.Sample.ProcessCount == 202, 3000));

    // And then the tray app goes too: the window is TOLD, never left frozen.
    pwTray.Drop();
    var ended = await one.WaitFor<WatchEndedMsg>(e => e.DeviceId == Pw, 4000);
    Check("when nothing is left, the technician is told it went offline", ended?.Reason.Contains("went offline") == true, ended?.Reason ?? "nothing said");

    // ---- an older client, and an offline machine ----
    Console.WriteLine();
    Console.WriteLine("== machines that cannot ==");
    const string Old = "watch-old";
    var oldTray = new FakeConn(ws, Old, "k-old", "OLD-CLIENT-PC", tenantKey, DeviceRole.Client, watch: false, marker: 0);
    await oldTray.ConnectAsync();
    Check("an older client reads online but NOT able", await one.WaitDevice(Old, d => d.Online && !d.CanWatch, 4000), one.Describe(Old));
    one.Watch(Old, true);
    var oldWhy = await one.WaitFor<WatchEndedMsg>(e => e.DeviceId == Old, 3000);
    Check("asking it is answered with what to do, not silence", oldWhy?.Reason.Contains("Update the support client") == true, oldWhy?.Reason ?? "nothing said");
    Check("...and the old client is sent nothing it would not understand", oldTray.Runs.Count == 0);
    one.Watch(Pw, true);
    var offWhy = await one.WaitFor<WatchEndedMsg>(e => e.DeviceId == Pw && e.Reason.Contains("offline") && e != ended, 3000);
    Check("an offline machine is answered 'offline'", offWhy != null, offWhy?.Reason ?? "nothing said");
    Check("CanWatch drops when the machine goes", one.Latest(Pw) is { CanWatch: false }, one.Describe(Pw));

    // ---- a console going away takes its windows with it ----
    Console.WriteLine();
    Console.WriteLine("== a console going away, and Exit ==");
    two.Watch(Att, true);
    Check("(Tech Two is watching the attended machine)", await WaitAsync(() => attTray.Running, 3000));
    two.Dispose();
    Check("when that console disappears, the machine stops measuring", await WaitAsync(() => !attTray.Running, 4000), string.Join(",", attTray.Runs));

    one.Watch(Att, true);
    await WaitAsync(() => attTray.Running, 3000);
    await attTray.SayClosingAsync();
    var exitWhy = await one.WaitFor<WatchEndedMsg>(e => e.DeviceId == Att, 4000);
    Check("Exit in the tray app ends a look too, and says why", exitWhy?.Reason.Contains("closed the support app") == true, exitWhy?.Reason ?? "nothing said");
    one.Watch(Att, true);
    var closedWhy = await one.WaitFor<WatchEndedMsg>(e => e.DeviceId == Att && e != exitWhy, 3000);
    Check("...and a closed machine cannot be looked at until it is opened again", closedWhy != null, closedWhy?.Reason ?? "");

    // ---- nothing logged, no ticket ----
    Console.WriteLine();
    Console.WriteLine("== nothing logged ==");
    var o = new DbContextOptionsBuilder<PlatformDb>().UseHarnessDb($"Data Source={Path.Combine(dataDir, "platform.db")};Pooling=False").Options;
    await using var db = new PlatformDb(o);
    int tickets = await db.Tickets.CountAsync();
    int sessions = await db.Sessions.CountAsync();
    Check("no ticket was opened by any of it", tickets == 0, $"{tickets} ticket(s)");
    Check("no session was recorded", sessions == 0, $"{sessions} session row(s)");
    one.Dispose();
}

static async Task<bool> WaitAsync(Func<bool> f, int ms)
{ var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (f()) return true; await Task.Delay(50); } return f(); }

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
    public string Describe(string id) => Latest(id) is { } d
        ? $"online={d.Online} canWatch={d.CanWatch} unattended={d.Unattended} password={d.UnattendedPassword}" : "not in the list";
    public void Watch(string deviceId, bool on) => _c.Send(new WatchMachineMsg { DeviceId = deviceId, On = on });

    public async Task<bool> WaitDevice(string id, Func<DeviceInfo, bool> f, int ms)
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { if (Latest(id) is { } d && f(d)) return true; await Task.Delay(50); } return false; }

    public async Task<T?> WaitFor<T>(Func<T, bool> f, int ms) where T : Msg
    { var e = DateTime.UtcNow.AddMilliseconds(ms); while (DateTime.UtcNow < e) { foreach (var m in Seen) if (m is T t && f(t)) return t; await Task.Delay(50); } return null; }

    public async Task<bool> WaitSample(string deviceId, Func<WatchSampleMsg, bool> f, int ms) =>
        await WaitFor<WatchSampleMsg>(s => s.DeviceId == deviceId && f(s), ms) != null;

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

    public void Dispose() { try { _raw.Abort(); } catch { } }
}

/// <summary>
/// One of a machine's connections, saying (or not) that it can send figures. Asked to run, it sends
/// a pretend reading every 200 ms with its own marker in ProcessCount, so the harness can tell whose
/// figures reached the technician.
/// </summary>
sealed class FakeConn
{
    private readonly string _ws, _id, _key, _name, _tenantKey;
    private readonly DeviceRole _role;
    private readonly bool _watch;
    private readonly int _marker;
    private ClientWebSocket? _raw;
    private WsConnection? _c;
    private CancellationTokenSource? _sending;
    /// <summary>Every WatchRunMsg received, in order.</summary>
    public readonly List<bool> Runs = new();
    public bool Running => _sending != null;
    public int SessionStarts;

    public FakeConn(string ws, string id, string key, string name, string tenantKey, DeviceRole role, bool watch, int marker)
    { _ws = ws; _id = id; _key = key; _name = name; _tenantKey = tenantKey; _role = role; _watch = watch; _marker = marker; }

    public async Task ConnectAsync()
    {
        _raw = new ClientWebSocket();
        await _raw.ConnectAsync(new Uri($"{_ws}/ws/device"), default);
        _c = new WsConnection(_raw);
        _c.OnMessage += m =>
        {
            if (m is StartSessionMsg) SessionStarts++;
            if (m is WatchRunMsg wr)
            {
                lock (Runs) Runs.Add(wr.On);
                if (wr.On && _sending == null) { _sending = new CancellationTokenSource(); _ = SendLoop(_sending.Token); }
                if (!wr.On) { _sending?.Cancel(); _sending = null; }
            }
        };
        _ = _c.RunAsync();
        _c.Send(new HelloMsg
        {
            Role = _role, DeviceId = _id, DeviceKey = _key, MachineName = _name, UserName = "FakeUser",
            Os = "Windows 11 (pretend)", Version = "0.0-test", TenantKey = _tenantKey,
            Spec = new SystemSpec { Cpu = "Pretend CPU", Cores = 1, RamMb = 1024, DiskTotalGb = 10, DiskFreeGb = 5, Os = "Windows 11 (pretend)" },
            Can = _watch ? new List<string> { DeviceCan.Watch } : new List<string>()
        });
        await Task.Delay(500);
    }

    private async Task SendLoop(CancellationToken ct)
    {
        try { while (!ct.IsCancellationRequested) { SendSample(_marker); await Task.Delay(200, ct); } } catch { }
    }

    public void SendSample(int marker) => _c?.Send(new WatchSampleMsg
    {
        DeviceId = "LIES-" + _id,   // the server must fill this in itself
        Sample = new MachineSample { Ts = DateTimeOffset.UtcNow, ProcessCount = marker, CpuPercent = 12, MemoryTotalMb = 1024, MemoryUsedMb = 512 }
    });

    public void Send(Msg m) => _c?.Send(m);
    public void Drop() { _sending?.Cancel(); _sending = null; try { _raw?.Abort(); } catch { } }
    public async Task SayClosingAsync() { _c!.Send(new ClientClosingMsg()); await _c.CloseAsync("closed by the user"); _sending?.Cancel(); _sending = null; }
}
