using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Asks the remote machine, through a real session, what is actually hosting it and at what
// integrity level (docs/HANDOVER.md 6d).
//
// "Unattended control works, attended control does not" comes down to which process runs
// Host.exe and with what token: SendInput from a medium-integrity process is refused against
// higher-integrity windows (UIPI), and the agent and client paths can land at different levels.
//
// The script runs as a child of Host.exe, so its own token IS the host's token. Read-only.
// Run it once during an unattended session and once during an attended one, and compare.
//
//   hostinfo <machine>
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
string want = args.Length > 0 ? args[0] : "DESKTOP-L2B85US";
string scriptPath = Path.Combine(AppContext.BaseDirectory, "hostinfo.ps1");
if (!File.Exists(scriptPath)) scriptPath = Path.Combine("tools", "hostinfo", "hostinfo.ps1");
string script = File.ReadAllText(scriptPath);

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
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
if (st == null) { Console.WriteLine("no state from the server"); return; }

Console.WriteLine("=== devices the server knows about ===");
foreach (var d in st.Devices)
    Console.WriteLine($"  {d.MachineName,-20} id={d.DeviceId}  online={d.Online}  user={d.UserName}");
Console.WriteLine();

var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found"); return; }

// If a session is already running on this device the server hands that one back instead of
// starting a new one — which is the point: run this while a failing attended session is live
// and the script executes inside THAT session's host.
tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "hostinfo diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return; }

// Report the facts rather than a verdict. A brand-new session has no host attached yet, so
// HostConnected=true here means we joined one that was already running — but the reliable
// answer is further down: the host's own command line carries the session id it was started
// for, and its parent process says who launched it.
// A session this tool just started has no host attached yet, so HostConnected=true here means
// we joined one that was already running.
bool joinedExisting = ready.Session.HostConnected;
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}");
Console.WriteLine($"  startedBy={ready.Session.StartedBy}  hostConnected={ready.Session.HostConnected}  " +
                  $"technicians=[{string.Join(", ", ready.Session.Technicians)}]");
Console.WriteLine(joinedExisting
    ? "  -> joined a session that was already running; it will be left open"
    : "  -> started a new session; it will be ended when this finishes");
Console.WriteLine();

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);
var output = new StringBuilder();
var done = new TaskCompletionSource();
int cursorCount = 0;
string lastCursor = "(none)";
vc.OnMessage += m =>
{
    if (m is CursorMsg cm) { Interlocked.Increment(ref cursorCount); lastCursor = $"{cm.X},{cm.Y} (mon {cm.Monitor})"; }
    if (m is ScriptOutputMsg so)
    {
        output.Append(so.Text);
        if (so.Done) done.TrySetResult();
    }
};
_ = vc.RunAsync();

// ---- is the host still talking, and does it still accept input? -----------------------------
// The host announces the real cursor position every 80 ms. If none arrive, the problem is the
// host->viewer direction. If they arrive but a MouseMsg from here does not move the cursor, the
// problem is the viewer->host direction or the injection itself. This viewer is independent of
// the technician console, so it splits "the console is broken" from "the session is broken".
Console.WriteLine("watching the host's cursor announcements for 4s...");
await Task.Delay(4000);
int before = cursorCount; string beforePos = lastCursor;
Console.WriteLine($"  CursorMsg received: {before}   last position: {beforePos}");

if (before == 0)
    Console.WriteLine("  -> the host is not announcing the cursor at all: host->viewer is the broken direction");
else
{
    Console.WriteLine("sending MouseMsg moves from this second viewer...");
    foreach (var (x, y) in new[] { (200, 200), (1500, 900), (960, 540) })
    {
        vc.Send(new MouseMsg { Monitor = 0, X = x, Y = y, Action = "move" });
        await Task.Delay(700);
    }
    await Task.Delay(1200);
    Console.WriteLine($"  after sending: last position: {lastCursor}");
    Console.WriteLine(lastCursor == beforePos
        ? "  -> the remote cursor did NOT move: input is not reaching the desktop (viewer->host, or injection)"
        : "  -> the remote cursor MOVED: this session accepts input fine, so the fault is in the console");
}
Console.WriteLine();

Console.WriteLine("running the diagnostic on the remote machine...\n");
vc.Send(new ScriptRunMsg { RunId = Guid.NewGuid().ToString("N"), Language = "powershell", Script = script });
await Task.WhenAny(done.Task, Task.Delay(60000));

Console.WriteLine(output.ToString());

// Only tear down a session this tool started. When it joins a live one — which is the whole
// point of running it during a failing session — ending it would close the technician's window
// out from under them.
if (!joinedExisting)
{
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(600);
}
else Console.WriteLine("(joined an existing session — leaving it open)");
