using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Proves clipboard text in BOTH directions, and proves it against the clipboard the USER has
// rather than one the host has to itself.
//
// That second half is the point. The clipboard belongs to a window station, and a process on the
// wrong one reads and writes a private, empty clipboard while every call reports success. A
// round trip through the host alone cannot tell those apart: writing to a private clipboard and
// reading the same private clipboard back passes perfectly and means nothing.
//
// So there are two different checks here:
//   1. round trip -- set text through the host, ask for it back, compare exactly
//   2. INDEPENDENT confirmation -- run clipboardprobe.ps1 as a separate process on the machine
//      and check the length and SHA-256 it reports match what we set. A different process seeing
//      our text is the proof that this is the shared, interactive clipboard.
//
// It puts the customer's own clipboard back when it has finished.
//
//   clipcheck <machine>
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
string want = positional.Length > 0 ? positional[0] : "DESKTOP-OPHQIDH";

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
var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase) && d.Online)
       ?? st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found. Known: {string.Join(", ", st.Devices.Select(d => d.MachineName).Distinct())}"); return 1; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 1; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "clipcheck diagnostic" });
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
var clips = new ConcurrentQueue<ClipboardMsg>();
// The hosts clipboard ledger lines, with when they arrived (--ledger).
var ledgerLines = new ConcurrentQueue<(DateTime at, FileActivityMsg m)>();
var scriptOut = new StringBuilder();
var scriptDone = new TaskCompletionSource();
string scriptRunId = "";
vc.OnMessage += m =>
{
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is ClipboardMsg cb) clips.Enqueue(cb);
    if (m is FileActivityMsg fa && fa.Kind == "clip-text") ledgerLines.Enqueue((DateTime.UtcNow, fa));
    if (m is ScriptOutputMsg so && so.RunId == scriptRunId)
    {
        scriptOut.Append(so.Text);
        if (so.Done) scriptDone.TrySetResult();
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

async Task<ClipboardMsg?> AskForClipboard(int ms = 15000)
{
    while (clips.TryDequeue(out _)) { }
    vc.Send(new ClipboardRequestMsg());
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end)
    {
        if (clips.TryDequeue(out var cb)) return cb;
        await Task.Delay(50);
    }
    return null;
}

// ---- 0. remember what the customer had, so we can put it back ------------------------------
Console.WriteLine("READ — the customer's clipboard as it stands");
var original = await AskForClipboard();
if (original == null)
{
    Record("the host answered a clipboard request", false,
        "no ClipboardMsg within 15s — the customer's machine is probably running a client without clipboard support");
    goto report;
}
Record("the host answered a clipboard request", true,
    string.IsNullOrEmpty(original.Error) ? $"{original.Text.Length:N0} characters on it" : original.Error);
string restore = original.Text ?? "";

// ---- 1. round trip -------------------------------------------------------------------------
// Deliberately awkward text: a non-ASCII character, a tab, both kinds of line ending and a
// trailing space. Clipboard code that mangles any of those is common, and a plain ASCII word
// would sail through all of it.
Console.WriteLine();
Console.WriteLine("ROUND TRIP — set it, then ask for it back");
string marker = $"clipcheck {Guid.NewGuid():N}\r\nsecond line\tafter a tab\nunicode: éü€— trailing ";
vc.Send(new ClipboardMsg { Text = marker });
await Task.Delay(700);   // let the host's clipboard thread do the write

var back = await AskForClipboard();
if (back == null) { Record("the clipboard came back", false, "no ClipboardMsg within 15s"); goto restoreAndReport; }
if (!string.IsNullOrEmpty(back.Error)) { Record("the clipboard came back", false, back.Error); goto restoreAndReport; }
Record("the clipboard came back", true, $"{back.Text.Length:N0} characters");

bool identical = back.Text == marker;
Record("it came back exactly as it went out", identical,
    identical ? "every character, including the tab and the non-ASCII ones"
              : $"sent {marker.Length} characters, got {back.Text.Length} back");
if (!identical)
{
    // Windows normalises a lone \n to \r\n on the clipboard, which is correct behaviour and not
    // a bug — say which it is rather than leaving a bare mismatch.
    string normalised = marker.Replace("\r\n", "\n").Replace("\n", "\r\n");
    if (back.Text == normalised)
        Console.WriteLine("        (the only difference is line endings normalised to CRLF, which Windows does on purpose)");
    else
    {
        int i = 0;
        while (i < Math.Min(back.Text.Length, marker.Length) && back.Text[i] == marker[i]) i++;
        Console.WriteLine($"        first difference at character {i}");
    }
}

// ---- 2. independent confirmation ------------------------------------------------------------
// The check that actually matters. A separate process on the customer's machine reads the
// clipboard and reports its length and hash. If those match, the host wrote to the clipboard
// the rest of the machine can see — not a private one on a service window station.
Console.WriteLine();
Console.WriteLine("INDEPENDENT — a separate process on the machine reads the same clipboard");
string scriptPath = Path.Combine(AppContext.BaseDirectory, "clipboardprobe.ps1");
if (!File.Exists(scriptPath)) scriptPath = Path.Combine("tools", "hostinfo", "clipboardprobe.ps1");
if (!File.Exists(scriptPath)) { Record("clipboardprobe.ps1 found", false, "not next to the executable or in tools/hostinfo"); goto restoreAndReport; }

scriptRunId = Guid.NewGuid().ToString("N");
vc.Send(new ScriptRunMsg { RunId = scriptRunId, Language = "powershell", Script = File.ReadAllText(scriptPath) });
bool ran = await Task.WhenAny(scriptDone.Task, Task.Delay(90000)) == scriptDone.Task;
string probe = scriptOut.ToString();
if (!ran) { Record("the probe ran on the customer machine", false, "no Done within 90s"); goto restoreAndReport; }
Record("the probe ran on the customer machine", true, "");

// The probe prints the SHA-256 of the UTF-16 bytes of the clipboard text, first 16 hex digits.
// Compute the same here for whatever the host reported, so we compare like with like.
string expectHash = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(back.Text)))[..16];
bool hashSeen = probe.Contains(expectHash, StringComparison.OrdinalIgnoreCase);
Record("that separate process sees the same text we set", hashSeen,
    hashSeen ? $"sha256 {expectHash}... matches"
             : $"expected sha256 {expectHash}..., probe reported something else");

bool onWinSta0 = probe.Contains("WinSta0", StringComparison.OrdinalIgnoreCase);
Record("the host is on the interactive window station", onWinSta0,
    onWinSta0 ? "WinSta0 — the clipboard the user sees"
              : "NOT WinSta0 — this would be a private clipboard nobody else can see");

if (!hashSeen || !onWinSta0)
{
    Console.WriteLine();
    Console.WriteLine("--- what the probe reported ---");
    foreach (var line in probe.Split('\n').Where(l =>
                 l.Contains("window station") || l.Contains("desktop") || l.Contains("characters") || l.Contains("owned by")))
        Console.WriteLine("    " + line.TrimEnd());
}

// ---- 3. the mirror: the customer copies, and it arrives here unasked -------------------------
// This is the half that did not exist before stage 3.5.1, and the half that makes copy and paste
// a gesture instead of a button. Everything above proves the host can be ASKED. Nothing above
// proves it ever SPEAKS, and the two are completely different pieces of code.
//
// The direction of the proof is the important part, and it is the opposite of the round trip: a
// SEPARATE PROCESS on the customer's machine puts something on their clipboard, and we assert on
// what arrives here without anybody asking for it. Setting it ourselves and watching it come back
// would prove nothing at all — that is the private-clipboard trap in the header, wearing a hat.

Console.WriteLine();
Console.WriteLine("MIRROR — the customer copies; it should arrive here on its own");

// Runs a fragment on the customer's machine and waits for it to finish. Each run is its own
// RunId because the handler above filters on it.
async Task<string> RunThere(string script, int ms = 60000)
{
    var out2 = new StringBuilder();
    var done = new TaskCompletionSource();
    var id = Guid.NewGuid().ToString("N");
    void H(Msg m)
    {
        if (m is ScriptOutputMsg so && so.RunId == id) { out2.Append(so.Text); if (so.Done) done.TrySetResult(); }
    }
    vc.OnMessage += H;
    try
    {
        vc.Send(new ScriptRunMsg { RunId = id, Language = "powershell", Script = script });
        await Task.WhenAny(done.Task, Task.Delay(ms));
        return out2.ToString();
    }
    finally { vc.OnMessage -= H; }
}

// Waits for a clipboard message to arrive UNASKED. Returns null when none does, which for the
// first check below is the passing answer.
async Task<ClipboardMsg?> WaitForPush(int ms)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end)
    {
        if (clips.TryDequeue(out var cb)) return cb;
        await Task.Delay(50);
    }
    return null;
}

string MarkerText(string tag) => $"clipcheck {tag} {Guid.NewGuid():N} éü€\ttabbed";

// --- 3a. NOBODY IS WATCHING, so nothing may leave -------------------------------------------
// The whole of Clint's decision of 2026-09-18 lives in this check. Mirroring is allowed only
// while a technician is looking at the screen; the password a customer copied this morning must
// not cross merely because a session is open. If this one passes and the rest fail, the feature is
// broken. If this one FAILS, the feature is a leak, and that is much worse than broken.
while (clips.TryDequeue(out _)) { }
var unwatched = MarkerText("unwatched");
await RunThere($"Set-Clipboard -Value {Quote(unwatched)}");
var leaked = await WaitForPush(4000);
// WHAT leaked matters as much as that something did. An error reply and the customer's actual
// clipboard are both wrong here, but they are wrong for completely different reasons and send
// the next hour in completely different directions. Say which.
static string Shape(ClipboardMsg c) =>
    !string.IsNullOrEmpty(c.Error) ? $"an ERROR reply: \"{c.Error}\""
    : c.Files is { Count: > 0 } ? $"a FILE LIST of {c.Files.Count}: {string.Join(", ", c.Files.Select(Path.GetFileName))}"
    : c.Text.Length == 0 ? "an EMPTY ClipboardMsg (no text, no files, no error)"
    : $"THEIR TEXT, {c.Text.Length} chars: \"{(c.Text.Length > 60 ? c.Text[..60] + "…" : c.Text)}\"";
Record("nothing crosses while no technician is watching", leaked == null,
    leaked == null ? "4s after they copied, with no subscription, nothing arrived"
                   : $"a ClipboardMsg arrived with nobody subscribed — it was {Shape(leaked)}");

// --- 3b. now somebody is watching ------------------------------------------------------------
vc.Send(new SubscribeMsg { Monitor = 0, On = true, Codecs = new List<string>() });
await Task.Delay(1200);   // let the host notice, and let the screen traffic settle
while (clips.TryDequeue(out _)) { }

var watched = MarkerText("watched");
await RunThere($"Set-Clipboard -Value {Quote(watched)}");
var pushed = await WaitForPush(8000);
Record("a copy on their machine arrives here without being asked for", pushed != null,
    pushed != null ? "pushed within 8s" : "nothing arrived in 8s — the host is not watching the clipboard");

if (pushed != null)
{
    Record("the pushed text is exactly what they copied", pushed.Text == watched,
        pushed.Text == watched ? $"{watched.Length} characters, tab and non-ASCII included"
                               : $"sent {watched.Length}, got {pushed.Text.Length}");
    // Auto tells the console whether to say anything about it. A push that announces itself like
    // an answer to a request would put a line on the status bar every time anybody copied.
    Record("it is marked as a push rather than an answer", pushed.Auto,
        pushed.Auto ? "Auto = true" : "Auto = false — the console will report every copy on the status bar");
}

// --- 3c. files and folders, not just text ----------------------------------------------------
// Clint, 2026-09-18: all of this must work for text, files AND folders. A tree with an empty
// directory in it, because that is the failure only a folder can have.
while (clips.TryDequeue(out _)) { }
string treeTag = Guid.NewGuid().ToString("N")[..8];
var build = await RunThere($@"
$root = Join-Path $env:TEMP 'clipcheck-{treeTag}'
New-Item -ItemType Directory -Force -Path $root | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $root 'empty') | Out-Null
Set-Content -Path (Join-Path $root 'one.txt')  -Value ('a' * 1000) -NoNewline
Set-Content -Path (Join-Path $root 'two.txt')  -Value ('b' * 2000) -NoNewline
$bytes = (Get-ChildItem -Recurse -File $root | Measure-Object -Property Length -Sum).Sum
Write-Output ""ROOT=$root""
Write-Output ""BYTES=$bytes""
Set-Clipboard -Path $root
");
long theirBytes = 0;
foreach (var line in build.Split('\n'))
    if (line.StartsWith("BYTES=")) long.TryParse(line.Substring(6).Trim(), out theirBytes);

var files = await WaitForPush(8000);
bool gotFiles = files?.Files is { Count: > 0 };
Record("a folder copied on their machine arrives here as a file list", gotFiles,
    gotFiles ? $"{files!.Files!.Count} item(s): {string.Join(", ", files.Files.Select(Path.GetFileName))}"
             : "no file list arrived in 8s");

if (gotFiles)
{
    // The size is not decoration: it is the whole of the rule that decides whether the bytes move
    // on their own or wait to be asked for. A wrong number here is a session stalled by a Ctrl+C.
    bool sized = theirBytes > 0 && files!.Bytes == theirBytes;
    Record("the size of the tree came with it, measured on their machine", sized,
        sized ? $"{files!.Bytes:N0} bytes, and a separate process on that machine agrees"
              : $"host said {files!.Bytes:N0}, the machine itself said {theirBytes:N0}");
    Record("the size is below the threshold, so it would be fetched without asking",
        files.Bytes > 0 && files.Bytes <= FileSetLimits.AutoFetchBytes,
        $"{FileSetLimits.Human(files.Bytes)} against a {FileSetLimits.Human(FileSetLimits.AutoFetchBytes)} threshold");
}
await RunThere($@"Remove-Item -Recurse -Force (Join-Path $env:TEMP 'clipcheck-{treeTag}') -ErrorAction SilentlyContinue");

// --- 3d. no echo -----------------------------------------------------------------------------
// Two ends watching one clipboard is a loop waiting to happen. The host writes what we send it,
// its own watcher sees a change a quarter-second later, and if nothing stops it that change comes
// straight back to us as "the customer copied this" — which we would then mirror onward. The
// guard is inside ClipboardAccess and is invisible from outside except exactly here.
while (clips.TryDequeue(out _)) { }
var echoed = MarkerText("echo");
vc.Send(new ClipboardMsg { Text = echoed });
var bounce = await WaitForPush(4000);
Record("what we send them does not come straight back at us", bounce == null,
    bounce == null ? "4s after setting their clipboard, nothing bounced"
                   : "our own text came back as a push — this is the loop the write guard exists to stop");

// --- 3e. the watcher who LEFT rather than unsubscribed ---------------------------------------
// 3a proves nothing crosses before anybody subscribes. This proves nothing crosses after they
// have gone, which is a different question and the one that was actually broken.
//
// A viewer that closes its socket never sends On=false. Until 2026-09-21 the host went on
// believing that subscription was live for the rest of its life, so the customer's clipboard
// kept crossing to a session nobody was looking at — and a later viewer that had never
// subscribed received it. Found by 3a failing on a host a previous harness had subscribed to.
//
// Done by DROPPING this viewer while subscribed and coming back on a new socket that does not
// subscribe, because that is what a technician closing the console actually does.
Console.WriteLine();
Console.WriteLine("AND NOTHING CROSSES ONCE THE TECHNICIAN HAS GONE");
try { await v.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default); } catch { }
try { v.Abort(); } catch { }
await Task.Delay(2500);   // let the server notice and tell the host

// The replacement socket BECOMES this harness's viewer, so everything after here — including
// putting the customer's clipboard back — keeps working. The local helpers close over `vc`, so
// reassigning it is enough to point them at the new connection.
v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
vc = new WsConnection(v);
vc.OnMessage += m =>
{
    if (m is ClipboardMsg c) clips.Enqueue(c);
    if (m is FileActivityMsg fa && fa.Kind == "clip-text") ledgerLines.Enqueue((DateTime.UtcNow, fa));
};
_ = vc.RunAsync();
await Task.Delay(1500);

while (clips.TryDequeue(out _)) { }
var afterGone = MarkerText("after-they-left");
// Copied on their machine over the NEW socket, which has never subscribed.
await RunThere($"Set-Clipboard -Value {Quote(afterGone)}");
var leakedAfter = await WaitForPush(5000);
Record("nothing crosses once the technician who was watching has gone", leakedAfter == null,
    leakedAfter == null ? "the subscription died with their socket, as it must"
                        : $"it still arrived — {Shape(leakedAfter)}");

// PowerShell single-quoted literal: the only character that needs escaping is the quote itself.
static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

// ---- 4. put the customer's clipboard back ----------------------------------------------------
restoreAndReport:
Console.WriteLine();
Console.WriteLine("RESTORE — putting the customer's own clipboard back");
if (restore.Length == 0)
{
    // Their clipboard was empty. Leaving our test text on it would be worse than leaving a
    // single space, which is the closest we can get to empty through a text-only message.
    vc.Send(new ClipboardMsg { Text = " " });
    await Task.Delay(500);
    Record("customer's clipboard restored", true, "it was empty; left it blank");
}
else
{
    vc.Send(new ClipboardMsg { Text = restore });
    await Task.Delay(700);
    var check = await AskForClipboard(10000);
    bool restored = check != null && check.Text == restore;
    Record("customer's clipboard restored", restored,
        restored ? $"{restore.Length:N0} characters put back" : "could not confirm it went back");
}

// ---- 5. --ledger: the clipboard reaches the ticket with nothing else happening (3.5.3) --------
// The restore above was a movement each way. Nothing moves from here on. The host must report it
// within its minute BY ITSELF: before 2026-09-29 it only noticed the minute when another copy
// came along, so a quiet session never reported its clipboard at all. Lines that arrived before
// this point do not count - an old host flushes on a later movement, and there were plenty.
if (args.Contains("--ledger"))
{
    Console.WriteLine();
    Console.WriteLine("THE CLIPBOARD REPORTS ITSELF - waiting up to 75 s with nothing moving");
    // One movement of our own, onto their machine, so there is certainly something to report -
    // the restore above may have sent nothing that counts. Then their clipboard is left blank again.
    foreach (var l in ledgerLines) Console.WriteLine($"  (earlier line at {l.at:HH:mm:ss}: {l.m.Describe("the technician")})");
    vc.Send(new ClipboardMsg { Text = "clipcheck ledger marker" });
    await Task.Delay(500);
    var quietFrom = DateTime.UtcNow;
    Console.WriteLine($"  one copy onto their machine at {quietFrom:HH:mm:ss}; nothing moves after it");
    var until = quietFrom.AddSeconds(75);
    while (DateTime.UtcNow < until && !ledgerLines.Any(l => l.at >= quietFrom)) await Task.Delay(250);
    var got = ledgerLines.Where(l => l.at >= quietFrom).ToList();
    Record("the host reports its clipboard within the minute, with no further copy to push it out", got.Count > 0,
        got.Count > 0 ? $"after {(got[0].at - quietFrom).TotalSeconds:0} s: " + string.Join(" / ", got.Select(l => l.m.Describe("the technician")))
                      : "nothing in 75 s - the ledger is still waiting for a movement that will not come");
    vc.Send(new ClipboardMsg { Text = restore.Length == 0 ? " " : restore });
}

report:
Console.WriteLine();
Console.WriteLine(new string('-', 70));
int passed = results.Count(r => r.ok);
Console.WriteLine($"{passed} of {results.Count} checks passed");
foreach (var r in results.Where(r => !r.ok)) Console.WriteLine($"  FAILED: {r.step} — {r.detail}");
Console.WriteLine(passed == results.Count
    ? "text and file lists cross both ways, unasked while somebody is watching and never when nobody is,\n"
      + "on the clipboard the user actually has."
    : "clipboard is NOT proven — see the failures above.");

if (!joinedExisting)
{
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(600);
}
return passed == results.Count ? 0 : 1;
