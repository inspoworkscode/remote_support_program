using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Proves files and folders over a REAL session, in both directions, by checksum.
//
// tools/filesetcheck already proves the tree engine on one machine: empty folders, zero-byte
// files, duplicate names, hostile paths. What it cannot prove is the part that only exists on
// the wire — the offer, the accept, chunks arriving out of a socket, and a host on the far end
// rebuilding the tree as SYSTEM into the logged-on user's folder. That is what this does.
//
// Four things, in order:
//   1. PUSH a tree to the customer, and wait for their machine to confirm where it landed
//   2. VERIFY INDEPENDENTLY — a separate PowerShell process on that machine hashes every file
//      under the folder and reports back. A transfer cannot mark its own homework: this is a
//      different process, reading the real disk, computing SHA-256 for itself.
//   3. PULL the same tree back and compare every hash with the originals here
//   4. CLIPBOARD FILE LIST — put those paths on the customer's clipboard as CF_HDROP and read
//      them back, which is what makes paste work rather than merely copying
//
// It removes what it left behind.
//
//   treecheck <machine> [--keep]
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
string want = positional.Length > 0 ? positional[0] : "DESKTOP-OPHQIDH";
bool keep = args.Any(a => a.Equals("--keep", StringComparison.OrdinalIgnoreCase));

var results = new List<(string step, bool ok, string detail)>();
void Record(string step, bool ok, string detail = "")
{
    results.Add((step, ok, detail));
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {step}{(detail.Length > 0 ? " — " + detail : "")}");
}

// ---- a tree with every awkward case in it ----------------------------------------------------
var local = Path.Combine(Path.GetTempPath(), "treecheck-" + Guid.NewGuid().ToString("N")[..8]);
var source = Path.Combine(local, "source");
var pulled = Path.Combine(local, "pulled");
Directory.CreateDirectory(source);
Directory.CreateDirectory(pulled);

var rnd = new Random(20260913);
// Keyed by the path relative to the root of the transfer, with '/' separators — the same shape
// the protocol uses, so comparing against either end needs no translation.
var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
string MakeFile(string abs, string rel, int size)
{
    Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
    var bytes = new byte[size];
    rnd.NextBytes(bytes);
    File.WriteAllBytes(abs, bytes);
    var h = Convert.ToHexString(SHA256.HashData(bytes));
    expected[rel] = h;
    return h;
}

var treeName = "treecheck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
var tree = Path.Combine(source, treeName);
MakeFile(Path.Combine(tree, "notes.txt"), $"{treeName}/notes.txt", 2_000);
// Comfortably more than one 64 KB chunk, so offsets and reassembly are actually exercised.
MakeFile(Path.Combine(tree, "big.bin"), $"{treeName}/big.bin", 900_000);
MakeFile(Path.Combine(tree, "sub", "deep", "inner.dat"), $"{treeName}/sub/deep/inner.dat", 40_000);
MakeFile(Path.Combine(tree, "sub", "zero.txt"), $"{treeName}/sub/zero.txt", 0);
MakeFile(Path.Combine(tree, "café ümläut.txt"), $"{treeName}/café ümläut.txt", 600);
Directory.CreateDirectory(Path.Combine(tree, "an empty folder"));

Console.WriteLine($"test tree: {treeName}");
Console.WriteLine($"  {expected.Count} files, one empty folder, one zero-byte file, one non-ASCII name");
Console.WriteLine();

// ---- connect ---------------------------------------------------------------------------------
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

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "treecheck diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 1; }
bool joinedExisting = ready.Session.HostConnected;
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}"
                  + (joinedExisting ? "  (joined an existing one; it will be left open)" : ""));

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);

// Every handler goes on BEFORE RunAsync — the host's one-shot announcements arrive within
// milliseconds and a late handler misses them for good (HANDOVER 10).
bool hostSeen = false;
var accepts = new ConcurrentQueue<FileSetAcceptMsg>();
var dones = new ConcurrentQueue<FileSetDoneMsg>();
var offers = new ConcurrentQueue<FileSetOfferMsg>();
var clips = new ConcurrentQueue<ClipboardMsg>();
var scriptOut = new StringBuilder();
var scriptDone = new TaskCompletionSource();
string scriptRunId = "";
FileSetReceiver? rx = null;
var rxComplete = new TaskCompletionSource();
var rxLock = new object();

vc.OnMessage += m =>
{
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is FileSetAcceptMsg a) accepts.Enqueue(a);
    if (m is FileSetDoneMsg d) dones.Enqueue(d);
    if (m is FileSetOfferMsg o) offers.Enqueue(o);
    if (m is ClipboardMsg c) clips.Enqueue(c);
    if (m is ScriptOutputMsg so && so.RunId == scriptRunId)
    {
        scriptOut.Append(so.Text);
        if (so.Done) scriptDone.TrySetResult();
    }
};
vc.OnBinary += frame =>
{
    if (frame.Length == 0 || frame.Span[0] != BinKind.FileChunk) return;
    var (id, off, data) = Bin.ParseFileChunk(frame);
    int hash = id.IndexOf('#');
    if (hash <= 0) return;
    if (!int.TryParse(id[(hash + 1)..], out var index)) return;
    lock (rxLock)
    {
        if (rx == null) return;
        if (rx.Chunk(index, off, data)) rxComplete.TrySetResult();
    }
};
_ = vc.RunAsync();

if (!joinedExisting)
{
    Console.Write("waiting for the host to attach");
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline && !hostSeen) { Console.Write("."); await Task.Delay(500); }
    Console.WriteLine(hostSeen ? " attached." : " never attached.");
    if (!hostSeen)
    {
        Console.WriteLine("the host never joined the session, so nothing could be tested.");
        tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
        await Task.Delay(600);
        return 1;
    }
}
Console.WriteLine();

async Task<T?> Wait<T>(ConcurrentQueue<T> queue, int ms, Func<T, bool>? f = null)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end)
    {
        if (queue.TryDequeue(out var item) && (f == null || f(item))) return item;
        await Task.Delay(50);
    }
    return default;
}

/// Runs a script on the customer machine and returns its output.
async Task<string> RunRemote(string script, int timeoutMs = 120000)
{
    scriptRunId = Guid.NewGuid().ToString("N");
    scriptOut.Clear();
    var tcs = new TaskCompletionSource();
    scriptDone = tcs;
    vc.Send(new ScriptRunMsg { RunId = scriptRunId, Language = "powershell", Script = script });
    await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
    return scriptOut.ToString();
}

string remoteDir = "";
var pushSetId = Guid.NewGuid().ToString("N");

// ---- 1. push ---------------------------------------------------------------------------------
Console.WriteLine("PUSH — a tree from here to the customer");
var plan = FileSetSender.Plan(new[] { tree });
Record("the tree was planned here", plan.FileCount == expected.Count && plan.Skipped.Count == 0,
    $"{plan.FileCount} files, {FileSetLimits.Human(plan.TotalBytes)}");

// TargetDir left null deliberately, so the host picks its own default. That is the path that has
// to resolve the logged-on user's Downloads and not SYSTEM's profile.
vc.Send(new FileSetOfferMsg { SetId = pushSetId, Entries = plan.Entries, TotalBytes = plan.TotalBytes, Label = plan.Label });
var accept = await Wait(accepts, 30000, a => a.SetId == pushSetId);
if (accept == null) { Record("the customer's machine accepted the offer", false, "no FileSetAcceptMsg within 30s — the machine may be running a client without whole-tree support"); goto report; }
if (!accept.Accept) { Record("the customer's machine accepted the offer", false, accept.Error ?? "refused"); goto report; }
Record("the customer's machine accepted the offer", true, accept.TargetDir ?? "");

var pushStart = DateTime.UtcNow;
await FileSetSender.SendAsync(pushSetId, plan, b => vc.SendBinary(b), null, CancellationToken.None);

var pushDone = await Wait(dones, 180000, d => d.SetId == pushSetId);
if (pushDone == null) { Record("the customer's machine reported it finished", false, "no FileSetDoneMsg within 180s"); goto report; }
var pushSecs = (DateTime.UtcNow - pushStart).TotalSeconds;
Record("the customer's machine reported it finished", pushDone.Ok,
    pushDone.Ok ? $"{plan.TotalBytes / 1024.0 / 1024.0 / Math.Max(0.001, pushSecs):F1} MB/s"
                : (pushDone.Error ?? "") + " " + string.Join("; ", pushDone.Skipped.Take(3)));

remoteDir = pushDone.SavedDir ?? "";
Record("it said where it put the tree", remoteDir.Length > 0, remoteDir);
if (remoteDir.Length == 0) goto report;

// SYSTEM's own profile is the wrong answer and a silent one: the files are there, just nowhere
// the customer will ever find them.
bool inSystemProfile = remoteDir.Contains(@"config\systemprofile", StringComparison.OrdinalIgnoreCase);
Record("it landed in a real user's folder, not SYSTEM's", !inSystemProfile,
    inSystemProfile ? "under config\\systemprofile — the customer will never find it" : "");
Record("it named the top-level item for the clipboard", pushDone.SavedPaths.Count > 0,
    string.Join(", ", pushDone.SavedPaths.Take(3)));

// ---- 2. independent verification --------------------------------------------------------------
// A separate process on that machine, reading the real disk and hashing for itself. A transfer
// reporting its own success proves only that it believes itself.
Console.WriteLine();
Console.WriteLine("INDEPENDENT — a separate process on the machine hashes what actually landed");
var landed = pushDone.SavedPaths.FirstOrDefault() ?? Path.Combine(remoteDir, treeName);
// File names come back BASE64-ENCODED, not as text.
//
// A name is exactly the thing a code page mangles, and this harness exists to tell "the file did
// not arrive" apart from "the file arrived and I cannot read its name" — a distinction it got
// wrong on its first live run, reporting a missing "café ümläut.txt" while the very next check
// confirmed the same file was present. Base64 of the UTF-16 bytes travels through any code page
// unharmed, so the comparison no longer depends on the customer's console encoding or on how
// recent their client is.
var hashScript = $@"
$root = '{landed.Replace("'", "''")}'
if (-not (Test-Path -LiteralPath $root)) {{ Write-Output 'MISSING-ROOT'; exit }}
Get-ChildItem -LiteralPath $root -Recurse -Force | ForEach-Object {{
    $rel = $_.FullName.Substring($root.Length).TrimStart('\')
    $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($rel))
    if ($_.PSIsContainer) {{ Write-Output ('D|' + $b64) }}
    else {{
        $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        Write-Output ('F|' + $b64 + '|' + $_.Length + '|' + $h)
    }}
}}
";
var report = await RunRemote(hashScript);
if (report.Contains("MISSING-ROOT")) { Record("the folder exists on the customer machine", false, landed); goto cleanup; }
Record("the folder exists on the customer machine", true, landed);

var remoteFiles = new Dictionary<string, (long size, string hash)>(StringComparer.OrdinalIgnoreCase);
var remoteDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
string Decode(string b64)
{
    try { return Encoding.Unicode.GetString(Convert.FromBase64String(b64)).Replace('\\', '/'); }
    catch { return "(unreadable name)"; }
}
foreach (var line in report.Split('\n'))
{
    var t = line.Trim();
    if (t.StartsWith("D|")) remoteDirs.Add(Decode(t[2..]));
    else if (t.StartsWith("F|"))
    {
        var parts = t[2..].Split('|');
        if (parts.Length >= 3 && long.TryParse(parts[^2], out var size))
            remoteFiles[Decode(parts[0])] = (size, parts[^1]);
    }
}
Record("the probe listed files", remoteFiles.Count > 0, $"{remoteFiles.Count} file(s), {remoteDirs.Count} folder(s)");

var missing = new List<string>();
var differs = new List<string>();
foreach (var (rel, hash) in expected)
{
    // The expected keys carry the tree name as their first segment; the remote listing is
    // relative to the tree folder itself.
    var inner = rel[(rel.IndexOf('/') + 1)..];
    if (!remoteFiles.TryGetValue(inner, out var got)) { missing.Add(inner); continue; }
    if (!got.hash.Equals(hash, StringComparison.OrdinalIgnoreCase)) differs.Add(inner);
}
Record("every file arrived, byte for byte", missing.Count == 0 && differs.Count == 0,
    missing.Count == 0 && differs.Count == 0
        ? $"{expected.Count} files hashed on their machine and matched"
        : $"missing: {string.Join(", ", missing)}  different: {string.Join(", ", differs)}");
Record("the empty folder survived the trip", remoteDirs.Contains("an empty folder"),
    remoteDirs.Contains("an empty folder") ? "" : "it was not created on the far side");
Record("the zero-byte file survived the trip", remoteFiles.ContainsKey("sub/zero.txt"),
    "no chunk is ever sent for one, so it has to be created up front");
Record("the non-ASCII name survived the trip", remoteFiles.Keys.Any(k => k.Contains("caf")),
    remoteFiles.Keys.FirstOrDefault(k => k.Contains("caf")) ?? "not found");

// ---- 3. pull it back ---------------------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("PULL — the same tree back from the customer");
var pullSetId = Guid.NewGuid().ToString("N");
vc.Send(new FileSetPullMsg { SetId = pullSetId, Paths = new List<string> { landed } });

var offer = await Wait(offers, 60000, o => o.SetId == pullSetId);
if (offer == null) { Record("the customer offered the tree back", false, "no FileSetOfferMsg within 60s"); goto cleanup; }
Record("the customer offered the tree back", offer.Entries.Count > 0,
    $"{offer.Entries.Count} entries, {FileSetLimits.Human(offer.TotalBytes)}");

lock (rxLock)
{
    rx = new FileSetReceiver(offer);
    if (!rx.Begin(pulled, out var why)) { rx = null; Record("we could receive it", false, why); }
}
if (rx == null) goto cleanup;
Record("we could receive it", true, pulled);
vc.Send(new FileSetAcceptMsg { SetId = pullSetId, Accept = true, TargetDir = pulled });

bool finished = await Task.WhenAny(rxComplete.Task, Task.Delay(180000)) == rxComplete.Task;
FileSetDoneMsg pullDone;
lock (rxLock) pullDone = rx!.Done();
Record("every byte came back", finished,
    finished ? $"{pullDone.SavedPaths.Count} top-level item(s)" : "the transfer did not complete within 180s");

if (finished)
{
    var badPull = new List<string>();
    foreach (var (rel, hash) in expected)
    {
        var inner = rel[(rel.IndexOf('/') + 1)..].Replace('/', Path.DirectorySeparatorChar);
        var abs = Path.Combine(pulled, Path.GetFileName(landed), inner);
        if (!File.Exists(abs)) { badPull.Add(inner + " (missing)"); continue; }
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(abs)));
        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) badPull.Add(inner + " (different)");
    }
    Record("the round trip is byte for byte identical to the original", badPull.Count == 0,
        badPull.Count == 0 ? $"{expected.Count} files" : string.Join("; ", badPull.Take(5)));
    Record("the empty folder came back too",
        Directory.Exists(Path.Combine(pulled, Path.GetFileName(landed), "an empty folder")));
}

// ---- 4. the clipboard file list -----------------------------------------------------------------
// The half that turns a copy into a paste: the customer's clipboard pointed at what landed.
Console.WriteLine();
Console.WriteLine("CLIPBOARD — a file list on the customer's machine (CF_HDROP)");
while (clips.TryDequeue(out _)) { }
vc.Send(new ClipboardMsg { Files = new List<string> { landed } });
await Task.Delay(1200);
vc.Send(new ClipboardRequestMsg());
var clipBack = await Wait(clips, 20000);
if (clipBack == null) Record("the customer's clipboard holds the file list", false, "no ClipboardMsg within 20s");
else if (!string.IsNullOrEmpty(clipBack.Error)) Record("the customer's clipboard holds the file list", false, clipBack.Error);
else
{
    bool hasFiles = clipBack.Files is { Count: > 0 };
    bool right = hasFiles && clipBack.Files!.Any(f => f.Equals(landed, StringComparison.OrdinalIgnoreCase));
    Record("the customer's clipboard holds the file list", right,
        hasFiles ? string.Join(", ", clipBack.Files!.Take(3)) : "the clipboard came back with text, not files");
}

// ---- 5. clean up ---------------------------------------------------------------------------------
cleanup:
if (remoteDir.Length > 0 && !keep)
{
    Console.WriteLine();
    Console.WriteLine("CLEAN UP");
    var target = (pushDone?.SavedPaths.FirstOrDefault() ?? Path.Combine(remoteDir, treeName)).Replace("'", "''");
    var outText = await RunRemote($@"
Remove-Item -LiteralPath '{target}' -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath '{target}') {{ Write-Output 'STILL THERE' }} else {{ Write-Output 'removed' }}
", 60000);
    Record("test tree removed from the customer machine", outText.Contains("removed"), target);
}
else if (remoteDir.Length > 0)
    Console.WriteLine($"\n(--keep: left {remoteDir} on the customer machine)");

report:
Console.WriteLine();
Console.WriteLine(new string('-', 70));
int passed = results.Count(r => r.ok);
Console.WriteLine($"{passed} of {results.Count} checks passed");
foreach (var r in results.Where(r => !r.ok)) Console.WriteLine($"  FAILED: {r.step} — {r.detail}");
Console.WriteLine(passed == results.Count
    ? "files and folders work over a real session, both ways, byte for byte."
    : "files and folders are NOT proven — see the failures above.");

try { Directory.Delete(local, true); } catch { }
if (!joinedExisting)
{
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(600);
}
return passed == results.Count ? 0 : 1;
