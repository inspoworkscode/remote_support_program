using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Runs any PowerShell diagnostic on a remote machine through the product's own script execution,
// so the script is a child of Host.exe and its token IS the host's token.
//
// That last part is the whole reason these harnesses exist. A script Clint runs by hand on the
// customer's machine runs as the customer; the same script run this way runs as SYSTEM on
// whatever desktop and window station the host is on. Those two can give completely different
// answers and only the second one tells you what the product will see. Edge rendering a PDF
// perfectly by hand and silently refusing as SYSTEM cost two sessions (HANDOVER 17).
//
//   runscript <machine> <script.ps1> [--timeout N]
//
// The script name may be a bare file name, in which case it is looked up next to the executable
// and then in tools/hostinfo and tools/ — so `runscript DESKTOP-OPHQIDH clipboardprobe.ps1` and
// `runscript DESKTOP-2N6FVGT agentstate.ps1` both just work.
//
// Read-only by nature: it runs what you hand it and prints what comes back. Nothing else.
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
if (positional.Length < 2)
{
    Console.WriteLine("usage: runscript <machine> <script.ps1> [--timeout seconds]");
    Console.WriteLine("       e.g. runscript DESKTOP-OPHQIDH clipboardprobe.ps1");
    return 2;
}
string want = positional[0];
string scriptArg = positional[1];
int timeoutSec = 120;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i].Equals("--timeout", StringComparison.OrdinalIgnoreCase)) int.TryParse(args[i + 1], out timeoutSec);

// Look next to the executable first, then in the folder the diagnostics live in.
string? scriptPath = null;
foreach (var candidate in new[]
{
    scriptArg,
    Path.Combine(AppContext.BaseDirectory, scriptArg),
    Path.Combine("tools", "hostinfo", scriptArg),
    // tools/ itself, for diagnostics that are not part of hostinfo — agentstate.ps1 is the first.
    Path.Combine("tools", scriptArg)
})
    if (File.Exists(candidate)) { scriptPath = candidate; break; }

if (scriptPath == null) { Console.WriteLine($"cannot find script '{scriptArg}' next to the executable or in tools/hostinfo"); return 2; }
string script = File.ReadAllText(scriptPath);
Console.WriteLine($"script: {Path.GetFullPath(scriptPath)}  ({script.Length:N0} chars)");

// Windows PowerShell 5.1 reads a file with no byte-order mark as ANSI, and the host writes the
// script out without one. Anything above ASCII would arrive mangled on the customer's machine,
// so say so here rather than letting it produce confusing output there.
var nonAscii = script.Where(c => c > 127).Distinct().ToArray();
if (nonAscii.Length > 0)
    Console.WriteLine($"WARNING: the script contains non-ASCII characters ({string.Join(" ", nonAscii.Take(8).Select(c => "U+" + ((int)c).ToString("X4")))}). "
                      + "PowerShell 5.1 reads a BOM-less file as ANSI, so these will arrive mangled.");
Console.WriteLine();

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed: {lr.StatusCode} — is the Platform server running on {http}?"); return 1; }
var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

var tech = new ClientWebSocket();
await tech.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
var tc = new WsConnection(tech);
var q = new ConcurrentQueue<Msg>();
tc.OnMessage += m => q.Enqueue(m);
_ = tc.RunAsync();

async Task<T?> W<T>(int ms, Func<T, bool>? f = null) where T : Msg
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

var st = await W<StateMsg>(8000);
if (st == null) { Console.WriteLine("no state from the server"); return 1; }
var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase) && d.Online)
       ?? st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found. Known: {string.Join(", ", st.Devices.Select(d => d.MachineName).Distinct())}"); return 1; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 1; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "runscript diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 1; }
bool joinedExisting = ready.Session.HostConnected;
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}"
                  + (joinedExisting ? "  (joined an existing one; it will be left open)" : ""));

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);
var output = new StringBuilder();
var done = new TaskCompletionSource();

// Every handler goes on BEFORE RunAsync. The host's one-shot announcements arrive within
// milliseconds and a handler added after the pump has started misses them for good (HANDOVER 10).
bool hostSeen = false;
vc.OnMessage += m =>
{
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is ScriptOutputMsg so)
    {
        output.Append(so.Text);
        if (so.Done) done.TrySetResult();
    }
};
_ = vc.RunAsync();

// A brand-new session has no host for a second or two, and the hub relays with s.Host?.Send(m),
// which is a silent no-op. Anything sent before the host attaches simply vanishes.
if (!joinedExisting)
{
    Console.Write("waiting for the host to attach");
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline && !hostSeen) { Console.Write("."); await Task.Delay(500); }
    Console.WriteLine(hostSeen ? " attached." : " never attached.");
    if (!hostSeen)
    {
        Console.WriteLine("the host never joined the session, so nothing could be run on it.");
        tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
        await Task.Delay(600);
        return 1;
    }
}
Console.WriteLine();

vc.Send(new ScriptRunMsg { RunId = Guid.NewGuid().ToString("N"), Language = "powershell", Script = script });
bool finished = await Task.WhenAny(done.Task, Task.Delay(timeoutSec * 1000)) == done.Task;

Console.WriteLine(output.ToString());
if (!finished) Console.WriteLine($"\n(the script did not report Done within {timeoutSec}s — output above may be partial)");

if (!joinedExisting)
{
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(600);
}
return finished ? 0 : 1;
