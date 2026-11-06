using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

int pass = 0, fail = 0;

// ---------------------------------------------------------------------------------------------
// The paste, performed on the customer's machine AS THE CUSTOMER.
//
// Every earlier probe of "Ctrl+V does nothing over there" looked at the customer's clipboard from
// the host's side of the fence: the host reading its own CF_HDROP back, or a PowerShell the host
// spawned — which runs as SYSTEM, like the host (HANDOVER 4). Explorer does not run as SYSTEM.
// It runs as the person at the machine, at Medium integrity, in their own logon session, and it
// is the only process whose opinion of that clipboard decides whether a paste happens.
//
// So this asks Explorer's process family, not the host's. It:
//
//   1. pastes two small files through the product's own transfer, exactly as the console does —
//      a file set to %PASTE% and then a ClipboardMsg carrying the paths the host saved them at;
//   2. runs a script as SYSTEM (the only way a script runs over there) that registers a one-shot
//      scheduled task AS THE LOGGED-ON USER, interactive, and starts it;
//   3. that task reads the clipboard the way Explorer would, checks it can read the staged files,
//      and then invokes the shell's own "Paste" verb into a scratch folder — Ctrl+V without a
//      keyboard — and reports what landed.
//
// It leaves nothing behind but the scratch folder under C:\Users\Public, which it names.
//
//   userpaste [machine]            default DESKTOP-OPHQIDH
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
if (dev == null) { Console.WriteLine($"device {want} not found"); return 1; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 1; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "userpaste diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 1; }
if (ready.Session.HostConnected)
{
    // A session left open by an earlier harness keeps the host it spawned, and that host is
    // whatever exe was installed when it started. This probe exists to test the host that is
    // installed NOW, so it ends the stale session and opens its own.
    Console.WriteLine($"ending the session already open ({ready.Session.SessionId}) so a fresh host starts");
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(5000);
    while (q.TryDequeue(out _)) { }
    tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "userpaste diagnostic" });
    ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
    if (ready == null) { Console.WriteLine("no session after ending the old one"); return 1; }
}
bool joinedExisting = ready.Session.HostConnected;
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}" + (joinedExisting ? "  (joined the one already open)" : ""));

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);
var vq = new ConcurrentQueue<Msg>();
bool hostSeen = false;
var outBuf = new StringBuilder();
var scriptDone = new TaskCompletionSource();
string runId = Guid.NewGuid().ToString("N");
vc.OnMessage += m =>
{
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is ScriptOutputMsg so && so.RunId == runId) { outBuf.Append(so.Text); if (so.Done) scriptDone.TrySetResult(); }
    if (m is ClipboardMsg cb && !string.IsNullOrEmpty(cb.Error)) Console.WriteLine("host: " + cb.Error);
    vq.Enqueue(m);
};
_ = vc.RunAsync();
async Task<T?> WV<T>(int ms, Func<T, bool>? f = null) where T : Msg
{
    var end = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < end) { foreach (var m in vq) if (m is T t && (f == null || f(t))) return t; await Task.Delay(50); }
    return null;
}

if (!joinedExisting)
{
    Console.Write("waiting for the host to attach");
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline && !hostSeen) { Console.Write("."); await Task.Delay(500); }
    Console.WriteLine(hostSeen ? " attached." : " never attached.");
    if (!hostSeen) { tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId }); await Task.Delay(600); return 1; }
}

// ---- 1. the paste, as the console does it --------------------------------------------------
var stamp = DateTime.Now.ToString("HHmmss");
var local = Path.Combine(Path.GetTempPath(), "userpaste-" + stamp);
Directory.CreateDirectory(local);
File.WriteAllText(Path.Combine(local, $"userpaste-{stamp}-a.txt"), "userpaste a " + stamp);
File.WriteAllText(Path.Combine(local, $"userpaste-{stamp}-b.txt"), "userpaste b " + stamp);
var plan = FileSetSender.Plan(new[] { Path.Combine(local, $"userpaste-{stamp}-a.txt"), Path.Combine(local, $"userpaste-{stamp}-b.txt") });
var setId = Guid.NewGuid().ToString("N");
Console.WriteLine();
Console.WriteLine($"1. pasting {plan.FileCount} file(s), {plan.TotalBytes} bytes, to {RemotePaths.PasteStaging}");
vc.Send(new FileSetOfferMsg { SetId = setId, Entries = plan.Entries, TotalBytes = plan.TotalBytes, Label = plan.Label, TargetDir = RemotePaths.PasteStaging });
var acc = await WV<FileSetAcceptMsg>(20000, a => a.SetId == setId);
if (acc == null || !acc.Accept) { Console.WriteLine("   the host did not accept the set: " + (acc?.Error ?? "no answer")); goto End; }
Console.WriteLine($"   accepted into {acc.TargetDir}");
await FileSetSender.SendAsync(setId, plan, b => vc.SendBinary(b), null, CancellationToken.None);
var fin = await WV<FileSetDoneMsg>(60000, d => d.SetId == setId);
if (fin == null || !fin.Ok) { Console.WriteLine("   the host did not finish the set: " + (fin?.Error ?? "no answer")); goto End; }
Console.WriteLine($"   saved {fin.SavedPaths.Count} path(s):");
foreach (var p in fin.SavedPaths) Console.WriteLine("     " + p);
vc.Send(new ClipboardMsg { Files = fin.SavedPaths });
await Task.Delay(1500);

// ---- 2 and 3. look, as the customer ---------------------------------------------------------
Console.WriteLine();
Console.WriteLine("2. asking the logged-on user's own processes what they see, and pasting");
string userScript = """
$ErrorActionPreference = 'SilentlyContinue'
$out = '@@OUT@@'
# Write through to disk on every line, so a hang leaves a partial trace instead of nothing.
function L($s) { Add-Content -LiteralPath $out -Value $s -Encoding UTF8 }
'--- start ---' | Set-Content -LiteralPath $out -Encoding UTF8
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class C {
  [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll")] public static extern bool IsClipboardFormatAvailable(uint f);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool OpenClipboard(IntPtr h);
  [DllImport("user32.dll")] public static extern bool CloseClipboard();
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr GetClipboardData(uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformationW(IntPtr h, int idx, StringBuilder s, int len, out int need);
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)] public static extern uint DragQueryFileW(IntPtr h, uint i, StringBuilder f, uint cch);
  [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr h);
  public static string Sta() { var sb = new StringBuilder(256); int n; GetUserObjectInformationW(GetProcessWindowStation(), 2, sb, 256, out n); return sb.ToString(); }
}
"@
L ("running as      : {0}  pid {1}  session {2}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name, $PID, (Get-Process -Id $PID).SessionId)
L ("integrity       : {0}" -f ((whoami /groups) | Select-String 'Mandatory Level' | ForEach-Object { ($_ -split '\s{2,}')[0] }))
L ("window station  : {0}" -f [C]::Sta())
L ("clipboard seq   : {0}" -f [C]::GetClipboardSequenceNumber())
L ("CF_HDROP avail  : {0}   CF_UNICODETEXT: {1}" -f [C]::IsClipboardFormatAvailable(15), [C]::IsClipboardFormatAvailable(13))
$own = [C]::GetClipboardOwner(); $opid = 0; [void][C]::GetWindowThreadProcessId($own, [ref]$opid)
$op = Get-Process -Id $opid -ErrorAction SilentlyContinue
L ("clipboard owner : hwnd 0x{0:x}  pid {1} ({2})" -f $own.ToInt64(), $opid, $(if ($op) { $op.Name } else { '?' }))
if ([C]::OpenClipboard([IntPtr]::Zero)) {
    $h = [C]::GetClipboardData(15)
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    if ($h -ne [IntPtr]::Zero) {
        $p = [C]::GlobalLock($h)
        $n = [C]::DragQueryFileW($p, 0xFFFFFFFF, $null, 0)
        L ("raw CF_HDROP    : handle ok, {0} path(s)" -f $n)
        for ($i = 0; $i -lt $n; $i++) { $sb = New-Object System.Text.StringBuilder 1024; [void][C]::DragQueryFileW($p, $i, $sb, 1024); L ("   raw path      : " + $sb.ToString()) }
        [void][C]::GlobalUnlock($h)
    } else { L ("raw CF_HDROP    : GetClipboardData returned NULL, win32 {0}" -f $err) }
    [void][C]::CloseClipboard()
} else { L ("raw CF_HDROP    : OpenClipboard failed, win32 {0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
Add-Type -AssemblyName System.Windows.Forms
$files = Get-Clipboard -Format FileDropList
if ($files) {
    L ("OLE file list   : {0} item(s)" -f $files.Count)
    foreach ($f in $files) {
        $exists = Test-Path -LiteralPath $f.FullName
        $readable = $false
        try { $fs = [IO.File]::OpenRead($f.FullName); $fs.Close(); $readable = $true } catch { $readErr = $_.Exception.Message }
        L ("   {0}   exists={1} readable={2} {3}" -f $f.FullName, $exists, $readable, $(if ($readable) { '' } else { $readErr }))
    }
    $dir = Split-Path $files[0].FullName
    L ("staging folder ACL (icacls):")
    icacls $dir 2>&1 | ForEach-Object { L ('   ' + $_) }
} else { L "OLE file list   : NONE  <-- Explorer would have nothing to paste" }
$eff = Get-Clipboard -Format Text
$target = 'C:\Users\Public\rs-pastetest'
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory $target | Out-Null
$sh = New-Object -ComObject Shell.Application
$folder = $sh.NameSpace((Split-Path $target))
$item = $folder.ParseName((Split-Path $target -Leaf))
$verbs = @($item.Verbs() | ForEach-Object { $_.Name })
L ("shell verbs on target folder: " + ($verbs -join ' | '))
$paste = $item.Verbs() | Where-Object { ($_.Name -replace '&','') -ieq 'Paste' } | Select-Object -First 1
if ($paste) { L "invoking the shell's Paste verb into $target"; $paste.DoIt() } else { L "NO PASTE VERB OFFERED on the folder  <-- this is the greyed-out Paste, seen from inside" }
Start-Sleep -Seconds 5
$landed = @(Get-ChildItem $target -Force -ErrorAction SilentlyContinue)
L ("after the paste : {0} item(s) in {1}" -f $landed.Count, $target)
foreach ($x in $landed) { L ("   " + $x.Name + "  " + $x.Length + " bytes") }

# The candidate fix, tried in place: the SAME paths, put on the clipboard again by THIS process —
# the logged-on user, Medium integrity — and the same question asked again.
if ($files) {
    L ''
    L '--- the same list, rewritten by this user process (Set-Clipboard -Path) ---'
    Set-Clipboard -Path @($files | ForEach-Object { $_.FullName })
    Start-Sleep -Milliseconds 500
    L ("clipboard seq   : {0}" -f [C]::GetClipboardSequenceNumber())
    $own2 = [C]::GetClipboardOwner(); $opid2 = 0; [void][C]::GetWindowThreadProcessId($own2, [ref]$opid2)
    L ("clipboard owner : hwnd 0x{0:x}  pid {1}" -f $own2.ToInt64(), $opid2)
    $item2 = $sh.NameSpace((Split-Path $target)).ParseName((Split-Path $target -Leaf))
    $verbs2 = @($item2.Verbs() | ForEach-Object { ($_.Name -replace '&','') })
    L ("Paste offered   : {0}" -f ($verbs2 -contains 'Paste'))
    $paste2 = $item2.Verbs() | Where-Object { ($_.Name -replace '&','') -ieq 'Paste' } | Select-Object -First 1
    if ($paste2) { $paste2.DoIt(); Start-Sleep -Seconds 5 }
    $landed2 = @(Get-ChildItem $target -Force -ErrorAction SilentlyContinue)
    L ("after the paste : {0} item(s) in {1}" -f $landed2.Count, $target)
    foreach ($x in $landed2) { L ("   " + $x.Name + "  " + $x.Length + " bytes") }
}
L '--- done ---'
""";

string sysScript = """
$ErrorActionPreference = 'SilentlyContinue'
Write-Output ("SYSTEM side: running as {0}, session {1}" -f [Security.Principal.WindowsIdentity]::GetCurrent().Name, (Get-Process -Id $PID).SessionId)
Add-Type -AssemblyName System.Windows.Forms
$f = Get-Clipboard -Format FileDropList
Write-Output ("SYSTEM side: OLE file list has {0} item(s)" -f $(if ($f) { $f.Count } else { 0 }))
$exp = Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" | Select-Object -First 1
if (-not $exp) { Write-Output 'no explorer.exe: nobody is logged on, so there is no user to paste as'; exit 1 }
$o = Invoke-CimMethod -InputObject $exp -MethodName GetOwner
$user = "$($o.Domain)\$($o.User)"
Write-Output ("explorer.exe pid {0} runs as {1} in session {2}" -f $exp.ProcessId, $user, $exp.SessionId)
$taskName = 'RS-UserProbe-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$script = "C:\Users\Public\$taskName.ps1"
$out = "C:\Users\Public\$taskName.out.txt"
Remove-Item $out -Force -ErrorAction SilentlyContinue
$userSrc = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('@@B64@@')).Replace('@@OUT@@', $out)
[IO.File]::WriteAllText($script, $userSrc, (New-Object Text.UTF8Encoding $true))
icacls $script /grant "*S-1-5-32-545:(R)" | Out-Null
try {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    $act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
    $pri = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskName $taskName -Action $act -Principal $pri -Settings $set -Force -ErrorAction Stop | Out-Null
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    Write-Output ("registered and started task {0} as {1}" -f $taskName, $user)
} catch { Write-Output ("could not register/start the user task: " + $_.Exception.Message) }
$t = 0
$doneMarker = $false
while ($t -lt 75) {
    Start-Sleep -Seconds 1; $t++
    if (Test-Path $out) { if ((Get-Content $out -Raw) -match '--- done ---') { $doneMarker = $true; break } }
}
if (Test-Path $out) {
    Write-Output ''; Write-Output ('=== as the user ({0}) ===' -f $(if ($doneMarker) { 'completed' } else { "still running after ${t}s — partial" }))
    Get-Content $out | ForEach-Object { Write-Output ('  ' + $_) }
    if (-not $doneMarker) { Write-Output ("  [task state: {0}, last result 0x{1:x}]" -f (Get-ScheduledTask -TaskName $taskName).State, (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult) }
} else { Write-Output ("the user-side task wrote nothing in {0}s. Task state: {1}, last result 0x{2:x}" -f $t, (Get-ScheduledTask -TaskName $taskName).State, (Get-ScheduledTaskInfo -TaskName $taskName).LastTaskResult) }
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item $script -Force -ErrorAction SilentlyContinue
Remove-Item $out -Force -ErrorAction SilentlyContinue
Remove-Item 'C:\Users\Public\rs-pastetest' -Recurse -Force -ErrorAction SilentlyContinue
""";
sysScript = sysScript.Replace("@@B64@@", Convert.ToBase64String(Encoding.UTF8.GetBytes(userScript)));

vc.Send(new ScriptRunMsg { RunId = runId, Language = "powershell", Script = sysScript });
bool ran = await Task.WhenAny(scriptDone.Task, Task.Delay(140000)) == scriptDone.Task;
var report = outBuf.ToString();
Console.WriteLine(report);
if (!ran) Console.WriteLine("(the script did not finish within 140s; the output above is what arrived)");

// The three things that decide whether Ctrl+V works on the customer's machine, asserted on the
// customer's own words. The verbs line is the whole of "greyed out": Explorer lists Paste on the
// folder, or it does not.
void Check(string what, bool ok)
{
    if (ok) pass++; else fail++;
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
}
var verbsLine = report.Split('\n').FirstOrDefault(l => l.Contains("shell verbs on target folder")) ?? "";
// The list the HOST wrote, so cut the report at the "rewritten by this user process" marker and
// look only before it — the second half proves nothing about the host.
int cut = report.IndexOf("rewritten by this user process", StringComparison.Ordinal);
var hostHalf = cut > 0 ? report[..cut] : report;
var landedLine = hostHalf.Split('\n').FirstOrDefault(l => l.Contains("after the paste :")) ?? "";
Check("the check ran as the customer, at Medium integrity, not as SYSTEM", report.Contains("integrity       : Mandatory Label\\Medium"));
Check("the customer's own process reads the pasted file list (OLE)", hostHalf.Contains("OLE file list   : 2 item(s)"));
Check("Explorer OFFERS Paste on a folder for the host-written list (not greyed out)", verbsLine.Contains("Paste"));
Check("invoking Paste lands both files on the customer's machine", landedLine.Contains("2 item(s)"));
Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} passed");

End:
try { Directory.Delete(local, true); } catch { }
if (!joinedExisting) { tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId }); await Task.Delay(600); }
return fail == 0 ? 0 : 1;
