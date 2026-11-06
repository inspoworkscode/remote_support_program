using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Proves file transfer in BOTH directions, by checksum rather than by eye.
//
// §8 of the handover has listed file transfer as "implemented but not verified" since it was
// written, and drag and drop is a new front end bolted straight onto it. Proving the plumbing
// first means a drag-and-drop bug is a drag-and-drop bug and nothing else.
//
// What it does, in order:
//   1. makes a file of random bytes here and hashes it
//   2. pushes it to the customer machine the way the console does, into the DEFAULT download
//      folder -- which also tests that the host resolves the logged-on user's profile and not
//      SYSTEM's (FileTransfer.DefaultDir, HANDOVER 4)
//   3. lists the folder it landed in, and checks the size the customer reports
//   4. pulls the same file back, reassembles it, and compares the hash with the original
//   5. deletes the file it left behind
//
// A push that "worked" and a pull that returns different bytes is the failure this is for; a
// size match alone would not catch a wrong offset.
//
//   filecheck <machine> [--mb N] [--keep]
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
string want = positional.Length > 0 ? positional[0] : "DESKTOP-OPHQIDH";
bool keep = args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase));
int sizeMb = 3;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i].Equals("--mb", StringComparison.OrdinalIgnoreCase)) int.TryParse(args[i + 1], out sizeMb);
sizeMb = Math.Clamp(sizeMb, 1, 64);

var results = new List<(string step, bool ok, string detail)>();
void Record(string step, bool ok, string detail)
{
    results.Add((step, ok, detail));
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {step}{(detail.Length > 0 ? " — " + detail : "")}");
}

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
var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found. Known: {string.Join(", ", st.Devices.Select(d => d.MachineName))}"); return 1; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 1; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "filecheck diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 1; }
bool joinedExisting = ready.Session.HostConnected;
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}"
                  + (joinedExisting ? "  (joined an existing one; it will be left open)" : ""));
Console.WriteLine();

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);

// Inbound state for the pull half. The host announces the file with a FileOfferMsg, streams
// BinKind.FileChunk frames, then sends FileDoneMsg.
var vq = new ConcurrentQueue<Msg>();
MemoryStream? pullBuf = null;
string pullId = "";
long pullExpected = -1, pullWritten = 0;
var pullLock = new object();

// Every handler goes on BEFORE RunAsync. The host's one-shot announcements arrive in the first
// few milliseconds, and a handler added after the socket is pumping misses them entirely — which
// is how the audio harness came to report "the host never attached" while 601 CursorMsgs from
// that very host went past.
bool hostSeen = false;
vc.OnMessage += m =>
{
    // Any of these can only have come from the host, so any of them proves it is there.
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    vq.Enqueue(m);
};
vc.OnBinary += frame =>
{
    if (frame.Length == 0 || frame.Span[0] != BinKind.FileChunk) return;
    var (id, off, data) = Bin.ParseFileChunk(frame);
    lock (pullLock)
    {
        if (pullBuf == null || id != pullId) return;
        pullBuf.Position = off;
        pullBuf.Write(data.Span);
        pullWritten += data.Length;
    }
};
_ = vc.RunAsync();

// Wait for the host to actually attach before sending it anything. The hub relays a viewer's
// message with `s.Host?.Send(m)` — if the host has not connected yet that is a silent no-op,
// and the transfer simply never starts. A brand-new session always has this gap.
if (!joinedExisting)
{
    Console.Write("waiting for the host to attach");
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline && !hostSeen) { Console.Write("."); await Task.Delay(500); }
    Console.WriteLine(hostSeen ? " attached." : " never attached.");
    if (!hostSeen)
    {
        Console.WriteLine("the host never joined the session, so no file could be transferred.");
        tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
        await Task.Delay(600);
        return 1;
    }
    Console.WriteLine();
}

async Task<T?> WV<T>(int ms, Func<T, bool>? f = null) where T : Msg
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { foreach (var m in vq) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

// ---- 1. a file that cannot accidentally match ----------------------------------------------
// Random bytes, not a repeated pattern: a chunk written at the wrong offset would still hash
// correctly if every chunk held the same thing.
int size = sizeMb * 1024 * 1024;
var payload = RandomNumberGenerator.GetBytes(size);
string localHash = Convert.ToHexString(SHA256.HashData(payload));
string name = $"filecheck-{DateTime.Now:yyyyMMdd-HHmmss}.bin";
Console.WriteLine($"test file: {name}  {size:N0} bytes  sha256 {localHash[..16]}…");
Console.WriteLine();

// ---- 2. push -------------------------------------------------------------------------------
Console.WriteLine("PUSH — technician to customer");
string savedPath = "";   // declared before the first goto: the cleanup block needs it whatever fails
string fileId = Guid.NewGuid().ToString("N");
var pushStart = DateTime.UtcNow;
// TargetDir deliberately left null, so the host picks its own default. That is the path that
// lands in the logged-on user's Downloads rather than SYSTEM's, and it is worth testing.
vc.Send(new FileOfferMsg { FileId = fileId, Name = name, Size = size });

var accept = await WV<FileAcceptMsg>(20000, a => a.FileId == fileId);
var earlyFail = await WV<FileDoneMsg>(100, d => d.FileId == fileId && !d.Ok);
if (earlyFail != null) { Record("host accepted the offer", false, earlyFail.Error ?? "refused"); goto report; }
if (accept == null || !accept.Accept) { Record("host accepted the offer", false, "no FileAcceptMsg within 20s"); goto report; }
Record("host accepted the offer", true, "");

const int chunk = 64 * 1024;
for (long off = 0; off < size; off += chunk)
{
    int n = (int)Math.Min(chunk, size - off);
    vc.SendBinary(Bin.FileChunk(fileId, off, payload.AsSpan((int)off, n)));
    // The host writes each chunk as it arrives; give the send channel room to drain the way
    // the real sender does rather than burying it.
    if ((off / chunk) % 16 == 0) await Task.Delay(1);
}

var pushDone = await WV<FileDoneMsg>(120000, d => d.FileId == fileId);
if (pushDone == null) { Record("customer reported the push finished", false, "no FileDoneMsg within 120s"); goto report; }
if (!pushDone.Ok) { Record("customer reported the push finished", false, pushDone.Error ?? "unknown error"); goto report; }
var pushSecs = (DateTime.UtcNow - pushStart).TotalSeconds;
Record("customer reported the push finished", true,
    $"{size / 1024.0 / 1024.0 / Math.Max(0.001, pushSecs):F1} MB/s");

savedPath = pushDone.SavedPath ?? "";
Record("customer said where it saved it", savedPath.Length > 0, savedPath);
if (savedPath.Length == 0) goto report;

// The point of leaving TargetDir null: SYSTEM's own profile is the wrong answer, and it is a
// silent wrong answer -- the file is there, just nowhere the customer will ever find it.
bool inSystemProfile = savedPath.Contains(@"config\systemprofile", StringComparison.OrdinalIgnoreCase);
Record("it landed in a real user's folder, not SYSTEM's", !inSystemProfile,
    inSystemProfile ? "saved under config\\systemprofile — the customer will never find it" : "");

// ---- 3. does the customer's own directory listing agree? ------------------------------------
Console.WriteLine();
Console.WriteLine("LIST — the customer's view of the folder");
string dir = Path.GetDirectoryName(savedPath) ?? "";
vc.Send(new FileListRequestMsg { Path = dir });
var listing = await WV<FileListMsg>(20000, l => l.Error != null || l.Entries.Count >= 0);
if (listing == null) Record("directory listing came back", false, "no FileListMsg within 20s");
else if (listing.Error != null) Record("directory listing came back", false, listing.Error);
else
{
    Record("directory listing came back", true, $"{listing.Entries.Count} entries in {listing.Path}");
    var entry = listing.Entries.FirstOrDefault(e => !e.IsDir &&
        string.Equals(e.Name, Path.GetFileName(savedPath), StringComparison.OrdinalIgnoreCase));
    if (entry == null) Record("the pushed file appears in the listing", false, "not listed");
    else Record("the pushed file appears in the listing", entry.Size == size,
        entry.Size == size ? $"{entry.Size:N0} bytes" : $"listed as {entry.Size:N0}, pushed {size:N0}");
}

// ---- 4. pull it back -----------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("PULL — customer to technician");
pullId = Guid.NewGuid().ToString("N");
lock (pullLock) { pullBuf = new MemoryStream(size); pullWritten = 0; }
var pullStart = DateTime.UtcNow;
vc.Send(new FilePullMsg { Path = savedPath, FileId = pullId });

var offer = await WV<FileOfferMsg>(30000, o => o.FileId == pullId);
if (offer == null) { Record("customer offered the file back", false, "no FileOfferMsg within 30s"); goto report; }
pullExpected = offer.Size;
Record("customer offered the file back", offer.Size == size,
    offer.Size == size ? $"{offer.Size:N0} bytes" : $"offered {offer.Size:N0}, expected {size:N0}");

var pullDone = await WV<FileDoneMsg>(120000, d => d.FileId == pullId);
if (pullDone == null) { Record("pull finished", false, $"no FileDoneMsg within 120s (got {pullWritten:N0} of {pullExpected:N0} bytes)"); goto report; }
if (!pullDone.Ok) { Record("pull finished", false, pullDone.Error ?? "unknown error"); goto report; }

// The host sends FileDoneMsg straight after queueing the last chunk, so the last frames may
// still be in flight. Wait for the bytes, not for the message.
var settle = DateTime.UtcNow.AddSeconds(30);
while (DateTime.UtcNow < settle)
{
    long got; lock (pullLock) got = pullWritten;
    if (got >= pullExpected) break;
    await Task.Delay(100);
}
var pullSecs = (DateTime.UtcNow - pullStart).TotalSeconds;

byte[] returned;
lock (pullLock) { returned = pullBuf!.ToArray(); }
Record("received every byte", returned.LongLength == size,
    returned.LongLength == size
        ? $"{size / 1024.0 / 1024.0 / Math.Max(0.001, pullSecs):F1} MB/s"
        : $"got {returned.LongLength:N0} of {size:N0}");

// ---- 5. the only answer that matters -------------------------------------------------------
string returnedHash = Convert.ToHexString(SHA256.HashData(returned));
bool identical = returnedHash == localHash;
Record("the bytes that came back are the bytes that went out", identical,
    identical ? $"sha256 {returnedHash[..16]}… matches" : $"sha256 {returnedHash[..16]}… != {localHash[..16]}…");

if (!identical && returned.LongLength == size)
{
    // Say WHERE it diverged. A single wrong offset and a truncated stream look identical in a
    // hash comparison, and they are completely different bugs.
    long first = -1, differing = 0;
    for (long i = 0; i < size; i++)
        if (returned[i] != payload[i]) { if (first < 0) first = i; differing++; }
    Console.WriteLine($"        first difference at byte {first:N0} (chunk {first / chunk}, offset {first % chunk} within it); "
                      + $"{differing:N0} bytes differ in total");
}

// ---- 6. do not leave litter on the customer's machine ---------------------------------------
report:
if (savedPath.Length > 0 && !keep)
{
    Console.WriteLine();
    Console.WriteLine("CLEAN UP");
    var runId = Guid.NewGuid().ToString("N");
    var cleanupDone = new TaskCompletionSource();
    var cleanupOut = new StringBuilder();
    vc.OnMessage += m =>
    {
        if (m is ScriptOutputMsg so && so.RunId == runId)
        {
            cleanupOut.Append(so.Text);
            if (so.Done) cleanupDone.TrySetResult();
        }
    };
    // Single-quoted so nothing in the path is treated as PowerShell syntax; the name is ours
    // and contains no quotes, but the folder is the customer's.
    var escaped = savedPath.Replace("'", "''");
    vc.Send(new ScriptRunMsg
    {
        RunId = runId,
        Language = "powershell",
        Script = $"Remove-Item -LiteralPath '{escaped}' -Force -ErrorAction SilentlyContinue\r\n"
               + $"if (Test-Path -LiteralPath '{escaped}') {{ Write-Output 'STILL THERE' }} else {{ Write-Output 'removed' }}\r\n"
    });
    bool cleaned = await Task.WhenAny(cleanupDone.Task, Task.Delay(30000)) == cleanupDone.Task;
    var text = cleanupOut.ToString();
    Record("test file removed from the customer machine",
        cleaned && text.Contains("removed"), cleaned ? savedPath : "cleanup script did not report back");
}
else if (savedPath.Length > 0)
    Console.WriteLine($"\n(--keep: left {savedPath} on the customer machine)");

// ---- verdict --------------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine(new string('-', 70));
int passed = results.Count(r => r.ok);
Console.WriteLine($"{passed} of {results.Count} checks passed");
foreach (var r in results.Where(r => !r.ok)) Console.WriteLine($"  FAILED: {r.step} — {r.detail}");
Console.WriteLine(passed == results.Count
    ? "file transfer works in both directions, byte for byte."
    : "file transfer is NOT proven — see the failures above.");

if (!joinedExisting)
{
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(600);
}
return passed == results.Count ? 0 : 1;
