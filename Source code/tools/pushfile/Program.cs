using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Puts files ONTO a customer machine from the command line, through the product's own transfer.
//
// fetchfile brings a file off a machine; filecheck proves the round trip with random bytes. There
// was no way to simply put a named file somewhere named, which is what deploying a fix to a
// machine actually needs - and without it the only way to test a change on real hardware was to
// rebuild the whole client payload and reinstall.
//
//   pushfile <machine> <file> [more files...] [--dir <target folder on that machine>]
//
//   pushfile DESKTOP-OPHQIDH healthcheck\src\Install-HealthCheck.ps1 --dir C:\Temp\hc
//
// Leave --dir off and the host picks its own default, which is the logged-on user's Downloads
// rather than SYSTEM's profile (HANDOVER 4). Give it one and the host writes there - the
// receiving side runs as SYSTEM, so it is the host that refuses a path trying to escape, not
// this tool; see filesetcheck for the tests that cover that.
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";

// One pass, consuming the value that follows a switch. Filtering on "starts with --" alone left
// the value of --dir sitting in the positional list, where it was taken for another file to push
// and the tool reported it could not find the folder it had just been told to write into.
string? targetDir = null;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--dir", StringComparison.OrdinalIgnoreCase)) { if (i + 1 < args.Length) targetDir = args[++i]; continue; }
    if (args[i].StartsWith("--")) continue;
    positional.Add(args[i]);
}

if (positional.Count < 2)
{
    Console.WriteLine("usage: pushfile <machine> <file> [more files...] [--dir <target folder>]");
    return 2;
}
string want = positional[0];
var files = positional.Skip(1).ToArray();

foreach (var f in files)
    if (!File.Exists(f)) { Console.WriteLine($"cannot find {f}"); return 2; }

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed: {lr.StatusCode} - is the Platform server running on {http}?"); return 1; }
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
var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found. Known: {string.Join(", ", st.Devices.Select(d => d.MachineName))}"); return 1; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 1; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "pushfile diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 1; }
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}"
                  + (ready.Session.HostConnected ? "  (joined an existing one; it will be left open)" : ""));
Console.WriteLine();

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);
var vq = new ConcurrentQueue<Msg>();
// Handlers on BEFORE RunAsync: the host's one-shot announcements arrive within milliseconds and
// a handler added after the socket is pumping misses them entirely.
vc.OnMessage += m => vq.Enqueue(m);
_ = vc.RunAsync();

async Task<T?> WV<T>(int ms, Func<T, bool>? f = null) where T : Msg
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { foreach (var m in vq) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

int failed = 0;
foreach (var path in files)
{
    var payload = File.ReadAllBytes(path);
    long size = payload.LongLength;
    string name = Path.GetFileName(path);
    string fileId = Guid.NewGuid().ToString("N");

    Console.Write($"  {name,-34} {size,9:N0} bytes  ");
    vc.Send(new FileOfferMsg { FileId = fileId, Name = name, Size = size, TargetDir = targetDir });

    var accept = await WV<FileAcceptMsg>(20000, a => a.FileId == fileId);
    var earlyFail = await WV<FileDoneMsg>(100, d => d.FileId == fileId && !d.Ok);
    if (earlyFail != null) { Console.WriteLine($"REFUSED - {earlyFail.Error}"); failed++; continue; }
    if (accept == null || !accept.Accept) { Console.WriteLine("no FileAcceptMsg within 20s"); failed++; continue; }

    const int chunk = 64 * 1024;
    bool sentWhole = true;
    for (long off = 0; off < size; off += chunk)
    {
        int n = (int)Math.Min(chunk, size - off);
        // Waits for room instead of losing the chunk -- BUGS #8, and this tool is how it was first
        // seen on real hardware rather than in a harness. A 70 MB push blasted at the socket
        // overran the send queue, a chunk went missing, and the receiving end sat holding an open
        // handle on a full-size file with a hole in it, reporting nothing at all. The result of
        // SendBinary was being thrown away here exactly as it was everywhere else.
        if (!await FileSetSender.SendChunkAsync(b => vc.SendBinary(b), Bin.FileChunk(fileId, off, payload.AsSpan((int)off, n)), CancellationToken.None))
        { sentWhole = false; break; }
        // Give the send channel room to drain, the way the real sender does.
        if ((off / chunk) % 16 == 0) await Task.Delay(1);
    }
    if (!sentWhole) { Console.WriteLine("the link would not carry the whole file"); failed++; continue; }

    var done = await WV<FileDoneMsg>(120000, d => d.FileId == fileId);
    if (done == null) { Console.WriteLine("no FileDoneMsg within 120s"); failed++; continue; }
    if (!done.Ok)     { Console.WriteLine($"FAILED - {done.Error}"); failed++; continue; }
    Console.WriteLine($"-> {done.SavedPath}");
}

Console.WriteLine();
Console.WriteLine(failed == 0 ? $"All {files.Length} file(s) delivered." : $"{failed} of {files.Length} failed.");
return failed == 0 ? 0 : 1;
