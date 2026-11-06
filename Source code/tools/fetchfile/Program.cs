using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Bring a file OFF a customer's machine, from the command line.
//
// Dragging out of the session window is roadmap 1.3 and deliberately not built, so fetching is
// meant to go through the Files tab or the clipboard. This drives the same wire path those use —
// FilePullMsg out, a FileOfferMsg back, BinKind.FileChunk frames, then FileDoneMsg — directly, so
// a file can be collected without depending on the console UI at all.
//
// It exists because a log was needed off a machine to fix a startup fault and the UI route would
// not do it. A harness that can do the job is also the measurement of whether the job is
// possible, which is worth more than an opinion about the tab.
//
//   list a folder (defaults to the logged-on user's desktop):
//     dotnet run -c Release --project tools/fetchfile/fetchfile.csproj -- DESKTOP-OPHQIDH --list
//     dotnet run -c Release --project tools/fetchfile/fetchfile.csproj -- DESKTOP-OPHQIDH --list "C:\Temp"
//
//   fetch one file (lands in your Downloads unless --out says otherwise):
//     dotnet run -c Release --project tools/fetchfile/fetchfile.csproj -- DESKTOP-OPHQIDH "C:\Users\Lenovo\Desktop\error.png"
//
// %DESKTOP% is resolved BY THE HOST against the logged-on user, because this end cannot know that
// path and must not guess it: the host runs as SYSTEM and its own profile is not theirs.
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var pos = args.Where(a => !a.StartsWith("--")).ToList();
string want = pos.FirstOrDefault() ?? "DESKTOP-OPHQIDH";
bool listing = args.Contains("--list");
string? remote = pos.Count > 1 ? pos[1] : null;
string? ArgVal(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
string outDir = ArgVal("--out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {s}");

async Task<T?> Wait<T>(ConcurrentQueue<Msg> q, int ms, Func<T, bool>? f = null) where T : Msg
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed ({(int)lr.StatusCode}) - is the server running?"); return 2; }
var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

var techWs = new ClientWebSocket();
await techWs.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
var tc = new WsConnection(techWs);
var tq = new ConcurrentQueue<Msg>();
tc.OnMessage += m => tq.Enqueue(m);
_ = tc.RunAsync();

var state = await Wait<StateMsg>(tq, 8000);
var dev = state?.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found"); return 2; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 2; }

// Join a session that is already open rather than starting a second one, so this never closes
// the technician's own window out from under them - the trap hostinfo already learned.
var already = state!.Sessions.FirstOrDefault(x => x.DeviceId == dev.DeviceId);
string sessionId;
bool mine = false;
if (already != null) { sessionId = already.SessionId; Log($"joining the session already open on {want}"); }
else
{
    tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "fetchfile diagnostic" });
    var ready = await Wait<SessionReadyMsg>(tq, 25000, s => s.Session.DeviceId == dev.DeviceId);
    if (ready == null) { Console.WriteLine("no session started"); return 2; }
    sessionId = ready.Session.SessionId; mine = true;
    Log($"session {sessionId}");
}

var raw = new ClientWebSocket();
await raw.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={tok}"), default);
var vc = new WsConnection(raw);
var vq = new ConcurrentQueue<Msg>();

FileStream? outFile = null;
long written = 0, expected = 0;
string savedTo = "";
var doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

// Every handler attached BEFORE the pump starts: the replayed MonitorsMsg lands within
// milliseconds of the socket opening and is gone for good if nothing is listening.
vc.OnMessage += m =>
{
    vq.Enqueue(m);
    if (m is FileOfferMsg o)
    {
        expected = o.Size;
        Directory.CreateDirectory(outDir);
        savedTo = Path.Combine(outDir, SafeName(o.Name));
        try { outFile = File.Create(savedTo); }
        catch (Exception ex) { Console.WriteLine($"could not create {savedTo}: {ex.Message}"); doneTcs.TrySetResult(false); }
        Log($"receiving {o.Name} ({o.Size:N0} bytes) -> {savedTo}");
    }
    else if (m is FileDoneMsg d)
    {
        if (!d.Ok) { Console.WriteLine("the machine could not send it: " + (string.IsNullOrWhiteSpace(d.Error) ? "no reason given" : d.Error)); doneTcs.TrySetResult(false); }
        else doneTcs.TrySetResult(true);
    }
};
vc.OnBinary += b =>
{
    if (b.Length == 0 || b.Span[0] != BinKind.FileChunk) return;
    var (_, off, data) = Bin.ParseFileChunk(b);
    var f = outFile;
    if (f == null) return;
    lock (f) { f.Seek(off, SeekOrigin.Begin); f.Write(data.Span); written += data.Length; }
};
_ = vc.RunAsync();

if (await Wait<MonitorsMsg>(vq, 25000) == null && mine)
{
    Console.WriteLine("the host never attached - end any open session on that machine and try again");
    tc.Send(new EndSessionMsg { SessionId = sessionId });
    return 2;
}

int exit = 0;
if (listing || remote == null)
{
    var path = remote ?? RemotePaths.Desktop;
    Log($"listing {path}");
    vc.Send(new FileListRequestMsg { Path = path });
    var fl = await Wait<FileListMsg>(vq, 20000);
    if (fl == null) { Console.WriteLine("no answer within 20s - the Files path is not working on this machine"); exit = 1; }
    else if (!string.IsNullOrEmpty(fl.Error))
    {
        // PRINTED, not swallowed. The first version of this tool ignored Error and showed an
        // empty listing for %DESKTOP%, which reads as "the folder is empty" when it actually
        // means "that path does not exist" - and sent me looking in the wrong place.
        Console.WriteLine();
        Console.WriteLine($"  the machine could not list {fl.Path}:");
        Console.WriteLine("    " + fl.Error);
        exit = 1;
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("  " + fl.Path);
        foreach (var e in fl.Entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name))
            Console.WriteLine(e.IsDir ? $"    <dir>  {e.Name}" : $"    {e.Size,12:N0}  {e.Name}");
        Console.WriteLine();
        Console.WriteLine("  To collect one of them:");
        Console.WriteLine($"    dotnet run -c Release --project tools/fetchfile/fetchfile.csproj -- {want} \"{fl.Path}\\NAME\"");
    }
}
else
{
    Log($"asking for {remote}");
    vc.Send(new FilePullMsg { Path = remote, FileId = Guid.NewGuid().ToString("N") });
    var finished = await Task.WhenAny(doneTcs.Task, Task.Delay(120000)) == doneTcs.Task && doneTcs.Task.Result;
    // The host sends FileDoneMsg straight after queueing the last chunk, so the final frames can
    // still be in flight when it arrives. filecheck learned this the same way.
    await Task.Delay(500);
    try { outFile?.Flush(); outFile?.Dispose(); } catch { }
    Console.WriteLine();
    if (finished && savedTo.Length > 0 && File.Exists(savedTo))
    {
        var got = new FileInfo(savedTo).Length;
        Console.WriteLine($"  SAVED  {savedTo}");
        Console.WriteLine($"         {got:N0} bytes" + (expected > 0 && got != expected ? $"   WARNING: the machine said {expected:N0}" : ""));
        if (expected > 0 && got != expected) exit = 1;
    }
    else { Console.WriteLine("  FAILED - nothing was saved."); exit = 1; }
}

if (mine) { tc.Send(new EndSessionMsg { SessionId = sessionId }); await Task.Delay(600); }
return exit;

static string SafeName(string n)
{
    foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
    return string.IsNullOrWhiteSpace(n) ? "fetched.bin" : n;
}
