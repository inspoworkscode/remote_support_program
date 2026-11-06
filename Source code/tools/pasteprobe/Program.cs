using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// What happened at the CUSTOMER'S end of a paste.
//
// On 2026-09-18 the technician's console did everything right and the paste still did not work.
// Its trace is unambiguous about its own half:
//
//   fileset: copied to DESKTOP-OPHQIDH — paste it anywhere on their machine now
//   activity: the technician sent ...\Temp\RemoteSupport\paste\105722-a34a52 (27,1 KB)
//
// The bytes moved, they landed where they were meant to, and the host was told to point the
// clipboard at them. Everything after that happens on the other machine, and nothing on this one
// can see it. Three rounds of diagnosis were spent reasoning about that gap instead of looking
// into it.
//
// So this looks. It runs one read-only script over a live session and reports the four things
// that decide whether Ctrl+V works over there:
//
//   1. does the customer's clipboard actually hold a file list right now, and what is on it
//   2. what the HOST's own trace says it did — SetFiles succeeding or failing, in its words
//   3. do the staged files still exist, and can the logged-on user read them
//   4. who the host is running as, because the clipboard belongs to a window station
//
// It changes nothing. It is safe to run during a real support call.
//
//   pasteprobe <machine>
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
string want = positional.Length > 0 ? positional[0] : "DESKTOP-OPHQIDH";

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed: {lr.StatusCode} — is the Platform server running on {http}?"); return 1; }
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
var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase) && d.Online)
       ?? st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found. Known: {string.Join("; ", st.Devices.Select(d => $"{d.MachineName} online={d.Online} user={d.UserName} unattended={d.Unattended}"))}"); return 1; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 1; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "pasteprobe diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 1; }
bool joinedExisting = ready.Session.HostConnected;
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}"
                  + (joinedExisting ? "  (joined the one already open; it will be left open)" : ""));

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);

bool hostSeen = false;
var outBuf = new StringBuilder();
var done = new TaskCompletionSource();
string runId = Guid.NewGuid().ToString("N");
vc.OnMessage += m =>
{
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is ScriptOutputMsg so && so.RunId == runId)
    {
        outBuf.Append(so.Text);
        if (so.Done) done.TrySetResult();
    }
};
_ = vc.RunAsync();

if (!joinedExisting)
{
    Console.Write("waiting for the host to attach");
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline && !hostSeen) { Console.Write("."); await Task.Delay(500); }
    Console.WriteLine(hostSeen ? " attached." : " never attached.");
    if (!hostSeen) { tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId }); await Task.Delay(600); return 1; }
}

// Read-only throughout. Nothing here writes a clipboard, moves a file or changes a setting —
// this runs on somebody's working machine and a probe that changes what it is measuring is worse
// than no probe.
const string script = @"
$ErrorActionPreference = 'SilentlyContinue'

Write-Output '=== who the host is, and where its clipboard lives ==='
Get-CimInstance Win32_Process -Filter ""Name = 'RemoteSupport.Host.exe'"" | ForEach-Object {
    $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
    $s = (Get-Process -Id $_.ProcessId).SessionId
    Write-Output (""host pid {0}  as {1}\{2}  session {3}"" -f $_.ProcessId, $o.Domain, $o.User, $s)
}
Write-Output (""this script is running as: {0}"" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name)

Write-Output ''
Write-Output '=== the customer clipboard, right now ==='
Add-Type -AssemblyName System.Windows.Forms
$files = Get-Clipboard -Format FileDropList
if ($files) {
    Write-Output (""FILE LIST PRESENT: {0} item(s)"" -f $files.Count)
    foreach ($f in $files) {
        $exists = Test-Path -LiteralPath $f.FullName
        Write-Output ('  ' + $f.FullName + '   exists=' + $exists)
    }
} else {
    Write-Output 'NO FILE LIST ON THE CLIPBOARD  <-- this is exactly what greys out Paste'
    $t = Get-Clipboard -Format Text
    if ($t) { Write-Output (""(there is text on it instead, {0} characters)"" -f $t.Length) }
    else    { Write-Output '(and no text either)' }
}

Write-Output ''
Write-Output '=== what the host says it did, in its own words ==='
$log = 'C:\ProgramData\RemoteSupport\host-trace.log'
if (Test-Path $log) {
    Get-Content $log -Tail 400 | Select-String 'clipboard:|paths:|fileset' | Select-Object -Last 25 |
        ForEach-Object { Write-Output ('  ' + $_.Line) }
} else { Write-Output 'no host trace at ' + $log }

Write-Output ''
Write-Output '=== what is staged for pasting, and who can read it ==='
$root = Join-Path $env:LOCALAPPDATA 'Temp\RemoteSupport\paste'
Write-Output (""staging root: {0}  exists={1}"" -f $root, (Test-Path $root))
if (Test-Path $root) {
    Get-ChildItem $root -Directory | Sort-Object CreationTime -Descending | Select-Object -First 3 | ForEach-Object {
        Write-Output ('  ' + $_.FullName + '   created ' + $_.CreationTime)
        Get-ChildItem $_.FullName -Recurse -File | Select-Object -First 8 | ForEach-Object {
            $readable = $false
            try { $fs = [IO.File]::OpenRead($_.FullName); $fs.Close(); $readable = $true } catch { }
            Write-Output ('      ' + $_.Name + '   ' + $_.Length + ' bytes   readable=' + $readable)
        }
    }
}
";

Console.WriteLine();
vc.Send(new ScriptRunMsg { RunId = runId, Language = "powershell", Script = script });
bool ran = await Task.WhenAny(done.Task, Task.Delay(90000)) == done.Task;
Console.WriteLine(outBuf.ToString());
if (!ran) Console.WriteLine("(the script did not finish within 90s; the output above is what arrived)");

Console.WriteLine(new string('-', 70));
Console.WriteLine("Read it in this order: is there a file list on their clipboard, and if not, what did");
Console.WriteLine("the host's own trace say when it tried to put one there?");

if (!joinedExisting)
{
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(600);
}
return 0;
