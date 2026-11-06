using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Paste a folder IN, then copy DIFFERENT folders OUT, in the same session.
//
// Clint, 2026-09-29, internal testing: "if i copy folders from this laptop to the test machine it
// works fine, but if i try copy different folders from the test machine in the same session, it
// pastes the original files and folders that i had copied across to the test machine."
//
// Neither usercopy nor userpaste does that sequence: each tests one direction on a clean
// clipboard. The question here is what happens to the customer's NEXT copy once the host has
// written a file list of its own (through the --put-files helper, §39).
//
//   1. the technician pastes folder A in, exactly as the console does;
//   2. the person at the machine, as themselves, copies folder B with the shell's own Copy verb
//      (Ctrl+C), then folder C the same way, then B and C together with Set-Clipboard -Path;
//   3. after each copy a SYSTEM script, a child of the host and so standing where the host
//      stands, lists every format it can see on the clipboard;
//   4. every ClipboardMsg that reaches the technician is printed WITH ITS PATHS — a count is not
//      a reading (§40).
//
//   recopy [machine]            default DESKTOP-OPHQIDH
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

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "recopy diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 1; }
if (ready.Session.HostConnected)
{
    Console.WriteLine($"ending the session already open ({ready.Session.SessionId}) so a fresh host starts");
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(5000);
    while (q.TryDequeue(out _)) { }
    tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "recopy diagnostic" });
    ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
    if (ready == null) { Console.WriteLine("no session after ending the old one"); return 1; }
}
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}");

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);
var vq = new ConcurrentQueue<Msg>();
bool hostSeen = false;
var clips = new ConcurrentQueue<(DateTime At, ClipboardMsg Msg)>();
var outBuf = new StringBuilder();
var scriptDone = new TaskCompletionSource();
string runId = "";
vc.OnMessage += m =>
{
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is ClipboardMsg cb) clips.Enqueue((DateTime.Now, cb));
    if (m is ScriptOutputMsg so && so.RunId == runId) { outBuf.Append(so.Text); if (so.Done) scriptDone.TrySetResult(); }
    vq.Enqueue(m);
};
_ = vc.RunAsync();
async Task<T?> WV<T>(int ms, Func<T, bool>? f = null) where T : Msg
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { foreach (var m in vq) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

Console.Write("waiting for the host to attach");
var deadline = DateTime.UtcNow.AddSeconds(60);
while (DateTime.UtcNow < deadline && !hostSeen) { Console.Write("."); await Task.Delay(500); }
Console.WriteLine(hostSeen ? " attached." : " never attached.");
if (!hostSeen) { tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId }); return 1; }

// The privacy gate: theirs reaches us only while somebody is watching the screen (§26).
vc.Send(new SubscribeMsg { Monitor = 0, On = true, Codecs = new List<string>() });
await Task.Delay(1500);

async Task<string> RunThere(string script, int timeoutMs)
{
    runId = Guid.NewGuid().ToString("N");
    outBuf.Clear();
    scriptDone = new TaskCompletionSource();
    vc.Send(new ScriptRunMsg { RunId = runId, Language = "powershell", Script = script });
    await Task.WhenAny(scriptDone.Task, Task.Delay(timeoutMs));
    return outBuf.ToString();
}

// ---- 1. the technician pastes folder A in ------------------------------------------------------
var stamp = DateTime.Now.ToString("HHmmss");
var local = Path.Combine(Path.GetTempPath(), "recopy-" + stamp);
var folderA = Path.Combine(local, $"recopy-A-{stamp}");
Directory.CreateDirectory(folderA);
File.WriteAllText(Path.Combine(folderA, "a1.txt"), "a1 " + stamp);
File.WriteAllText(Path.Combine(folderA, "a2.txt"), "a2 " + stamp);
var plan = FileSetSender.Plan(new[] { folderA });
var setId = Guid.NewGuid().ToString("N");
Console.WriteLine();
Console.WriteLine($"1. THE TECHNICIAN PASTES FOLDER A IN ({plan.FileCount} file(s))");
vc.Send(new FileSetOfferMsg { SetId = setId, Entries = plan.Entries, TotalBytes = plan.TotalBytes, Label = plan.Label, TargetDir = RemotePaths.PasteStaging });
var acc = await WV<FileSetAcceptMsg>(20000, a => a.SetId == setId);
if (acc == null || !acc.Accept) { Console.WriteLine("   the host did not accept the set: " + (acc?.Error ?? "no answer")); return 1; }
await FileSetSender.SendAsync(setId, plan, b => vc.SendBinary(b), null, CancellationToken.None);
var fin = await WV<FileSetDoneMsg>(60000, d => d.SetId == setId);
if (fin == null || !fin.Ok) { Console.WriteLine("   the host did not finish the set: " + (fin?.Error ?? "no answer")); return 1; }
foreach (var p in fin.SavedPaths) Console.WriteLine("   saved " + p);
vc.Send(new ClipboardMsg { Files = fin.SavedPaths });
await Task.Delay(3000);
bool echoed = false;
while (clips.TryDequeue(out var c))
{
    Console.WriteLine("   (came back straight after the paste: " + Describe(c.Msg) + ")");
    if (c.Msg.Files is { Count: > 0 }) echoed = true;
}
Check("the paste does not bounce straight back as 'the customer copied this'", !echoed);

// ---- 2 and 3. the person copies B, then C, then B and C; SYSTEM looks after each -------------
string userScript = """
$ErrorActionPreference = 'SilentlyContinue'
$out = '@@OUT@@'
$mark = '@@MARK@@'
function L($s) { Add-Content -LiteralPath $out -Value $s -Encoding UTF8 }
function Stage($n) { Set-Content -LiteralPath ($mark + $n) -Value 'x'; Start-Sleep -Seconds 9 }
function Seen { $p = @(Get-Clipboard -Format FileDropList | ForEach-Object { $_.FullName }); if ($p.Count -eq 0) { '(none)' } else { $p -join ' | ' } }
'--- start ---' | Set-Content -LiteralPath $out -Encoding UTF8
L ("running as : {0}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name)
L ("the user sees, before copying anything: {0}" -f (Seen))

$root = Join-Path $env:TEMP ('recopy-' + [guid]::NewGuid().ToString('N').Substring(0,6))
$b = Join-Path $root 'recopy-B-@@STAMP@@'
$c = Join-Path $root 'recopy-C-@@STAMP@@'
New-Item -ItemType Directory $b, $c | Out-Null
'b1' | Set-Content (Join-Path $b 'b1.txt')
'c1' | Set-Content (Join-Path $c 'c1.txt')
'c2' | Set-Content (Join-Path $c 'c2.txt')
L ("B = {0}" -f $b)
L ("C = {0}" -f $c)
$sh = New-Object -ComObject Shell.Application
$parent = $sh.NameSpace($root)

L 'B: the shell Copy verb on folder B (Ctrl+C)'
$parent.ParseName((Split-Path $b -Leaf)).InvokeVerb('Copy')
Start-Sleep -Seconds 1
L ("   the user sees: {0}" -f (Seen))
Stage 1

L 'C: the shell Copy verb on folder C (Ctrl+C)'
$parent.ParseName((Split-Path $c -Leaf)).InvokeVerb('Copy')
Start-Sleep -Seconds 1
L ("   the user sees: {0}" -f (Seen))
Stage 2

L 'BC: Set-Clipboard -Path B, C'
Set-Clipboard -Path @($b, $c)
Start-Sleep -Seconds 1
L ("   the user sees: {0}" -f (Seen))
Stage 3
L '--- done ---'
""";

string sysScript = """
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -Namespace Rc -Name Clip -MemberDefinition @'
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern uint EnumClipboardFormats(uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClipboardFormatNameW(uint f, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint f);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint DragQueryFileW(IntPtr h, uint i, System.Text.StringBuilder s, uint n);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
'@
function Survey($label) {
    $names = @()
    $paths = @()
    if ([Rc.Clip]::OpenClipboard([IntPtr]::Zero)) {
        try {
            $f = 0
            while (($f = [Rc.Clip]::EnumClipboardFormats($f)) -ne 0) {
                $sb = New-Object System.Text.StringBuilder 128
                if ([Rc.Clip]::GetClipboardFormatNameW($f, $sb, 128) -gt 0) { $names += $sb.ToString() } else { $names += "#$f" }
            }
            $h = [Rc.Clip]::GetClipboardData(15)
            if ($h -ne [IntPtr]::Zero) {
                $p = [Rc.Clip]::GlobalLock($h)
                if ($p -ne [IntPtr]::Zero) {
                    $n = [Rc.Clip]::DragQueryFileW($p, 0xFFFFFFFF, $null, 0)
                    for ($i = 0; $i -lt $n; $i++) { $sb = New-Object System.Text.StringBuilder 1024; [void][Rc.Clip]::DragQueryFileW($p, $i, $sb, 1024); $paths += $sb.ToString() }
                    [void][Rc.Clip]::GlobalUnlock($h)
                }
            }
        } finally { [void][Rc.Clip]::CloseClipboard() }
    } else { $names += '(could not open the clipboard)' }
    Write-Output ("SYSTEM after {0}: seq {1}; formats [{2}]; CF_HDROP readable here: {3}" -f $label, [Rc.Clip]::GetClipboardSequenceNumber(), ($names -join ', '), $(if ($paths.Count) { $paths -join ' | ' } else { 'NO' }))
}

Survey 'the paste'
$exp = Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | Select-Object -First 1
if (-not $exp) { Write-Output 'no explorer.exe: nobody is logged on'; exit 1 }
$o = Invoke-CimMethod -InputObject $exp -MethodName GetOwner
$user = "$($o.Domain)\$($o.User)"
$taskName = 'RS-Recopy-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$script = "C:\Users\Public\$taskName.ps1"
$out = "C:\Users\Public\$taskName.out.txt"
$mark = "C:\Users\Public\$taskName.stage"
$src = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('@@B64@@')).Replace('@@OUT@@', $out).Replace('@@MARK@@', $mark)
[IO.File]::WriteAllText($script, $src, (New-Object Text.UTF8Encoding $true))
icacls $script /grant "*S-1-5-32-545:(R)" | Out-Null
icacls 'C:\Users\Public' | Out-Null
try {
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
    $pri = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 3)
    Register-ScheduledTask -TaskName $taskName -Action $act -Principal $pri -Settings $set -Force -ErrorAction Stop | Out-Null
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    Write-Output ("started the copies as {0}" -f $user)
} catch { Write-Output ("could not start the user task: " + $_.Exception.Message) }
$seen = @{}
$t = 0
while ($t -lt 120) {
    Start-Sleep -Milliseconds 500; $t += 0.5
    foreach ($n in 1..3) {
        if (-not $seen[$n] -and (Test-Path ($mark + $n))) { $seen[$n] = $true; Start-Sleep -Seconds 2; Survey (@{1='copy B';2='copy C';3='copy B+C'}[$n]) }
    }
    if ((Test-Path $out) -and ((Get-Content $out -Raw) -match '--- done ---')) { break }
}
if (Test-Path $out) { Write-Output ''; Get-Content $out | ForEach-Object { Write-Output ('  user: ' + $_) } } else { Write-Output 'the user task wrote nothing' }
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item $script, $out -Force -ErrorAction SilentlyContinue
1..3 | ForEach-Object { Remove-Item ($mark + $_) -Force -ErrorAction SilentlyContinue }
""";

userScript = userScript.Replace("@@STAMP@@", stamp);
sysScript = sysScript.Replace("@@B64@@", Convert.ToBase64String(Encoding.UTF8.GetBytes(userScript)));

Console.WriteLine();
Console.WriteLine("2. THE PERSON AT THE MACHINE COPIES B, THEN C, THEN B+C — AS THEMSELVES");
var collected = new List<(DateTime At, ClipboardMsg Msg)>();
var collecting = true;
var collector = Task.Run(async () =>
{
    while (collecting) { if (clips.TryDequeue(out var c)) collected.Add(c); else await Task.Delay(80); }
});
var report = await RunThere(sysScript, 150000);
await Task.Delay(3000);
collecting = false;
await collector;
Console.WriteLine(report);

Console.WriteLine("what reached the technician, in order:");
if (collected.Count == 0) Console.WriteLine("  NOTHING AT ALL");
foreach (var c in collected) Console.WriteLine($"  {c.At:HH:mm:ss}  {Describe(c.Msg)}");
Console.WriteLine();

bool Has(string leaf) => collected.Any(c => c.Msg.Files is { Count: > 0 } f && f.Any(p => Path.GetFileName(p.TrimEnd('\\')) == leaf));
string aLeaf = Path.GetFileName(folderA), bLeaf = $"recopy-B-{stamp}", cLeaf = $"recopy-C-{stamp}";
Check("the copy of folder B reaches the technician", Has(bLeaf));
Check("the copy of folder C reaches the technician", Has(cLeaf));
Check("B and C copied together reach the technician", collected.Any(c => c.Msg.Files is { Count: 2 } f
      && f.Any(p => p.EndsWith(bLeaf)) && f.Any(p => p.EndsWith(cLeaf))));
Check("folder A — what the TECHNICIAN pasted — is never offered back", !Has(aLeaf),
      Has(aLeaf) ? "the customer's later copy arrived as the technician's own paste" : "");

vc.Send(new SubscribeMsg { Monitor = 0, On = false, Codecs = new List<string>() });
tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
await Task.Delay(800);
try { Directory.Delete(local, true); } catch { }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} passed");
return fail == 0 ? 0 : 1;

static string Describe(ClipboardMsg c)
{
    if (!string.IsNullOrEmpty(c.Error)) return "error: " + c.Error;
    if (c.Files is { Count: > 0 }) return $"FILES x{c.Files.Count} ({FileSetLimits.Human(c.Bytes)}): {string.Join(" | ", c.Files)}";
    return $"TEXT {c.Text.Length} chars";
}
