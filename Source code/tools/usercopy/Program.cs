using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// The customer copies — really copies, as themselves, the way a person does — and it must reach
// the technician.
//
// WHY THIS EXISTS. On 2026-09-21 pasting INTO the customer's machine started working (§39) and
// Clint reported that copying FROM it does not. `pullcheck` proves the console's receive half is
// fine (8 of 8), and `clipcheck` proves the wire (15 of 15). But clipcheck's "the customer
// copies" is `Set-Clipboard` run by a script the host spawned — and a script over there is a
// child of the host, so it runs as SYSTEM. **Nothing has ever measured a copy made by the person
// at the machine.**
//
// That distinction is exactly the one that turned out to matter for the paste, in the other
// direction, so it is measured here rather than assumed. Explorer's Ctrl+C does not put plain
// bytes on the clipboard: it puts a shell data object that renders the file list on demand, in
// Explorer's own process, at Medium integrity. Whether the SYSTEM host can get that rendered is
// a different question from whether it can read a flat CF_HDROP, and it is the question that
// decides whether copy-from-the-customer works.
//
// So this runs, in one session, with a viewer SUBSCRIBED so the privacy gate is open:
//
//   A. SYSTEM copies text          (what clipcheck does — the control)
//   B. THE USER copies text        (Set-Clipboard, as themselves)
//   C. THE USER copies files       via Set-Clipboard -Path, as themselves
//   D. THE USER copies files       via the SHELL'S OWN Copy verb — this is literally Ctrl+C
//
// and reports, for each, whether a ClipboardMsg reached the technician and what was in it.
//
//   usercopy [machine]            default DESKTOP-OPHQIDH
// ---------------------------------------------------------------------------------------------

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}" + (detail.Length > 0 ? $"  — {detail}" : ""));
}

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
string want = positional.Length > 0 ? positional[0] : "DESKTOP-OPHQIDH";

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed: {lr.StatusCode}"); return 1; }
var tok = JsonDocument.Parse(await lr.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;

var tech = new ClientWebSocket();
await tech.ConnectAsync(new Uri($"{ws}/ws/tech?token={tok}"), default);
var tc = new WsConnection(tech);
var q = new ConcurrentQueue<Msg>();
tc.OnMessage += m => { if (m is ErrorMsg em) Console.WriteLine("server: " + em.Message); q.Enqueue(m); };
_ = tc.RunAsync();

async Task<T?> W<T>(int ms, Func<T, bool>? f = null) where T : Msg
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { foreach (var m in q) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

var st = await W<StateMsg>(8000);
if (st == null) { Console.WriteLine("no state from the server"); return 1; }
var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase) && d.Online);
if (dev == null) { Console.WriteLine($"device {want} not found or offline"); return 1; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "usercopy diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 1; }
if (ready.Session.HostConnected)
{
    Console.WriteLine($"ending the session already open ({ready.Session.SessionId}) so a fresh host starts");
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(5000);
    while (q.TryDequeue(out _)) { }
    tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "usercopy diagnostic" });
    ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
    if (ready == null) { Console.WriteLine("no session after ending the old one"); return 1; }
}
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}");

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);
bool hostSeen = false;
var clips = new ConcurrentQueue<ClipboardMsg>();
var outBuf = new StringBuilder();
var scriptDone = new TaskCompletionSource();
string runId = "";
vc.OnMessage += m =>
{
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is ClipboardMsg cb) clips.Enqueue(cb);
    if (m is ScriptOutputMsg so && so.RunId == runId) { outBuf.Append(so.Text); if (so.Done) scriptDone.TrySetResult(); }
};
_ = vc.RunAsync();

Console.Write("waiting for the host to attach");
var deadline = DateTime.UtcNow.AddSeconds(60);
while (DateTime.UtcNow < deadline && !hostSeen) { Console.Write("."); await Task.Delay(500); }
Console.WriteLine(hostSeen ? " attached." : " never attached.");
if (!hostSeen) { tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId }); return 1; }

// The privacy gate: theirs reaches us only while somebody is watching the screen (§26).
vc.Send(new SubscribeMsg { Monitor = 0, On = true, Codecs = new List<string>() });
await Task.Delay(1500);
Console.WriteLine("subscribed to the screen, so the host is allowed to push their clipboard");

async Task<string> RunThere(string script, int timeoutMs = 120000)
{
    runId = Guid.NewGuid().ToString("N");
    outBuf.Clear();
    scriptDone = new TaskCompletionSource();
    vc.Send(new ScriptRunMsg { RunId = runId, Language = "powershell", Script = script });
    await Task.WhenAny(scriptDone.Task, Task.Delay(timeoutMs));
    return outBuf.ToString();
}

async Task<ClipboardMsg?> WaitForPush(int ms)
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { if (clips.TryDequeue(out var c)) return c; await Task.Delay(100); }
    return null;
}

// ---- A. SYSTEM copies text: the control, and what clipcheck has always tested ----------------
Console.WriteLine();
Console.WriteLine("A. SYSTEM COPIES TEXT (the control — this is what clipcheck proves)");
while (clips.TryDequeue(out _)) { }
var markerA = "usercopy-system-" + Guid.NewGuid().ToString("N")[..8];
await RunThere($"Set-Clipboard -Value '{markerA}'", 30000);
var gotA = await WaitForPush(10000);
Check("a copy made by SYSTEM reaches the technician", gotA != null && gotA.Text.Contains(markerA),
    gotA == null ? "nothing arrived in 10s" : $"arrived: {Describe(gotA)}");

// ---- B, C, D: the person at the machine, as themselves ---------------------------------------
// One task, run as the logged-on user, doing the three copies with a pause between each so the
// pushes can be told apart. The host's watcher polls every 250 ms, so 6 s is generous.
string userScript = """
$ErrorActionPreference = 'SilentlyContinue'
$out = '@@OUT@@'
function L($s) { Add-Content -LiteralPath $out -Value $s -Encoding UTF8 }
'--- start ---' | Set-Content -LiteralPath $out -Encoding UTF8
L ("running as : {0}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name)
L ("integrity  : {0}" -f ((whoami /groups) | Select-String 'Mandatory Level' | ForEach-Object { ($_ -split '\s{2,}')[0] }))

$dir = Join-Path $env:TEMP ('usercopy-' + [guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory $dir | Out-Null
'one' | Set-Content (Join-Path $dir 'one.txt')
'two' | Set-Content (Join-Path $dir 'two.txt')
L ("made       : {0}" -f $dir)

Start-Sleep -Seconds 3
L 'B: user copies TEXT'
Set-Clipboard -Value '@@MARKB@@'
Start-Sleep -Seconds 7

L 'C: user copies FILES with Set-Clipboard -Path'
Set-Clipboard -Path @((Join-Path $dir 'one.txt'), (Join-Path $dir 'two.txt'))
Start-Sleep -Seconds 7

L 'D: user copies FILES with the SHELL Copy verb (this is Ctrl+C)'
$sh = New-Object -ComObject Shell.Application
$folder = $sh.NameSpace($dir)
$folder.ParseName('one.txt').InvokeVerb('Copy')
Start-Sleep -Seconds 7
$n = @(Get-Clipboard -Format FileDropList).Count
L ("   after the shell Copy, the user's own process sees {0} file(s) on the clipboard" -f $n)

L '--- done ---'
""";

string sysScript = """
$ErrorActionPreference = 'SilentlyContinue'
$exp = Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | Select-Object -First 1
if (-not $exp) { Write-Output 'no explorer.exe: nobody is logged on'; exit 1 }
$o = Invoke-CimMethod -InputObject $exp -MethodName GetOwner
$user = "$($o.Domain)\$($o.User)"
Write-Output ("the person at the machine is {0}" -f $user)
$taskName = 'RS-UserCopy-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$script = "C:\Users\Public\$taskName.ps1"
$out = "C:\Users\Public\$taskName.out.txt"
$src = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('@@B64@@')).Replace('@@OUT@@', $out)
[IO.File]::WriteAllText($script, $src, (New-Object Text.UTF8Encoding $true))
icacls $script /grant "*S-1-5-32-545:(R)" | Out-Null
try {
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
    $pri = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 3)
    Register-ScheduledTask -TaskName $taskName -Action $act -Principal $pri -Settings $set -Force -ErrorAction Stop | Out-Null
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    Write-Output ("started the copies as {0}" -f $user)
} catch { Write-Output ("could not start the user task: " + $_.Exception.Message) }
$t = 0
while ($t -lt 90) { Start-Sleep -Seconds 1; $t++; if ((Test-Path $out) -and ((Get-Content $out -Raw) -match '--- done ---')) { break } }
if (Test-Path $out) { Write-Output ''; Get-Content $out | ForEach-Object { Write-Output ('  ' + $_) } }
else { Write-Output 'the user task wrote nothing' }
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item $script,$out -Force -ErrorAction SilentlyContinue
""";

var markerB = "usercopy-user-" + Guid.NewGuid().ToString("N")[..8];
userScript = userScript.Replace("@@MARKB@@", markerB);
sysScript = sysScript.Replace("@@B64@@", Convert.ToBase64String(Encoding.UTF8.GetBytes(userScript)));

Console.WriteLine();
Console.WriteLine("B, C, D. THE PERSON AT THE MACHINE COPIES, AS THEMSELVES");
while (clips.TryDequeue(out _)) { }

var collected = new List<ClipboardMsg>();
var collector = Task.Run(async () =>
{
    var end = DateTime.UtcNow.AddSeconds(100);
    while (DateTime.UtcNow < end) { if (clips.TryDequeue(out var c)) collected.Add(c); else await Task.Delay(80); }
});

var report = await RunThere(sysScript, 150000);
await collector;
Console.WriteLine(report);

Console.WriteLine("what reached the technician while the user was copying:");
if (collected.Count == 0) Console.WriteLine("  NOTHING AT ALL");
foreach (var c in collected) Console.WriteLine("  " + Describe(c));
Console.WriteLine();

bool sawB = collected.Any(c => c.Text.Contains(markerB));
bool sawFiles = collected.Any(c => c.Files is { Count: > 0 });
Check("text copied by the PERSON at the machine reaches the technician", sawB,
    sawB ? "their text arrived" : "their own copy never arrived, though SYSTEM's did");
Check("files copied by the PERSON at the machine reach the technician", sawFiles,
    sawFiles ? string.Join(" | ", collected.Where(c => c.Files is { Count: > 0 }).Select(Describe))
             : "NO file list ever arrived — this is 'copy from the client does not work'");

vc.Send(new SubscribeMsg { Monitor = 0, On = false, Codecs = new List<string>() });
tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
await Task.Delay(800);

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} passed");
return fail == 0 ? 0 : 1;

static string Describe(ClipboardMsg c)
{
    if (!string.IsNullOrEmpty(c.Error)) return "error: " + c.Error;
    if (c.Files is { Count: > 0 }) return $"FILES x{c.Files.Count} ({FileSetLimits.Human(c.Bytes)}): {string.Join(", ", c.Files.Select(Path.GetFileName))}";
    return $"TEXT {c.Text.Length} chars: \"{(c.Text.Length > 40 ? c.Text[..40] + "…" : c.Text)}\"";
}
