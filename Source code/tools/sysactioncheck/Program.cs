using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Does a system action actually reach the machine, and what does it say back?
//
// Roadmap 1.5 asked for this and it was not built at the time: the buttons were shipped proven
// only by a screenshot of the toolbar, which says nothing about whether pressing one does
// anything. Ctrl+Alt+Del then "did nothing" twice, and both times the interesting evidence was a
// message nobody was reading.
//
// It sends the action on a REAL viewer socket, exactly as the session window does, and waits for
// the SysActionResultMsg that comes back. That answer distinguishes the cases that otherwise all
// look identical from the technician's chair:
//
//   * a reply saying Ok       - it was carried out, and the fault (if any) is elsewhere
//   * a reply saying not Ok   - it was refused, and the reply says why
//   * NO REPLY AT ALL         - nothing on the machine is listening for it. That is what an old
//                               agent looks like, and an old agent is the likeliest thing after a
//                               ship, because replacing the binary is not the same as restarting
//                               the service that is running it.
//
// Ctrl+Alt+Del is the default because it is the one that has been wrong. It is also harmless and
// reversible: it puts the Windows security screen up on that machine, and Escape dismisses it.
//
//   dotnet run -c Release --project tools/sysactioncheck/sysactioncheck.csproj -- DESKTOP-OPHQIDH [lock]
//   ... -- DESKTOP-2N6FVGT --observe     Ctrl+Alt+Del, and WATCH whether the security screen came up
//                                         (added 2026-09-29: the reply alone says "sent" either way)
//
// "reboot" is deliberately NOT accepted. Restarting somebody's machine is not a thing a
// diagnostic should do because it was run with the wrong argument.
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToList();
string want = positional.FirstOrDefault() ?? "DESKTOP-OPHQIDH";
string action = positional.Count > 1 ? positional[1] : "ctrl_alt_del";
if (action is not ("ctrl_alt_del" or "lock"))
{
    Console.WriteLine($"'{action}' is not something this will send. Use ctrl_alt_del or lock.");
    Console.WriteLine("reboot is excluded on purpose - a diagnostic should not restart somebody's machine.");
    return 2;
}

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
Log($"{dev.MachineName} online - unattended agent present: {dev.Unattended}");
if (!dev.Unattended && action == "ctrl_alt_del")
    Log("NOTE: no agent is reported for this machine, and only a service can send Ctrl+Alt+Del.");

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "sysactioncheck diagnostic" });
var ready = await Wait<SessionReadyMsg>(tq, 25000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session started"); return 2; }
if (ready.Session.HostConnected)
{
    // A session left open keeps the host that was installed when it started — the wrong one to
    // test after swapping in a new build. End it and open our own, as userpaste does.
    Log($"ending the session already open ({ready.Session.SessionId}) so a fresh host starts");
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(5000);
    while (tq.TryDequeue(out _)) { }
    tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "sysactioncheck diagnostic" });
    ready = await Wait<SessionReadyMsg>(tq, 25000, s => s.Session.DeviceId == dev.DeviceId);
    if (ready == null) { Console.WriteLine("no session after ending the old one"); return 2; }
}
var sessionId = ready.Session.SessionId;
Log($"session {sessionId}");

var raw = new ClientWebSocket();
await raw.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={sessionId}&token={tok}"), default);
var vc = new WsConnection(raw);
var vq = new ConcurrentQueue<Msg>();
// Attached before the pump, as always: the replayed MonitorsMsg lands within milliseconds.
vc.OnMessage += m => vq.Enqueue(m);
_ = vc.RunAsync();

// Wait for evidence of the host. A new session has no host for a second or two and the hub
// relays with s.Host?.Send(m), which is a silent no-op - so an action sent too early vanishes
// and looks exactly like one that was ignored.
var mons = await Wait<MonitorsMsg>(vq, 25000);
if (mons == null) { Console.WriteLine("the host never attached - end any open session on that machine and try again"); tc.Send(new EndSessionMsg { SessionId = sessionId }); return 2; }
Log("host is up");

// --observe: the reply is not the result. §21 found the agent answering "sent Ctrl+Alt+Del" for a
// call that did nothing, so this LOOKS: a script under the host (SYSTEM, in the customer's session)
// watches the input desktop while the action is sent. Ctrl+Alt+Del that worked switches it to
// "Winlogon"; one that did not leaves it on "Default". Escape is then pressed through the host,
// the same way a technician would dismiss it, so nobody has to walk over to the machine.
bool observe = args.Contains("--observe") && action == "ctrl_alt_del";
string runId = Guid.NewGuid().ToString("N");
var watchOut = new System.Text.StringBuilder();
var watchDone = new TaskCompletionSource();
string codecBefore = "";
string CodecNow() => vq.OfType<SysInfoMsg>().LastOrDefault()?.Info.GetValueOrDefault("Video codec") ?? "";
if (observe)
{
    // Watch the screen the way a console does, offering H.264, so the capture path and the
    // encoder are really running across the switch — the crash was in the encoder, and a check
    // that never started it would pass on a host that still crashes.
    vc.Send(new SubscribeMsg { Monitor = 0, On = true, Codecs = new List<string> { VideoCodec.H264, VideoCodec.JpegTiles } });
    for (int i = 0; i < 20 && !CodecNow().StartsWith("H.264"); i++) await Task.Delay(250);
    codecBefore = CodecNow();
    Log("picture before: " + (codecBefore.Length > 0 ? codecBefore : "(no codec reported)"));
    vc.OnMessage += m => { if (m is ScriptOutputMsg so && so.RunId == runId) { lock (watchOut) watchOut.Append(so.Text); if (so.Done) watchDone.TrySetResult(); } };
    const string watcher = """
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -Namespace Sw -Name D -MemberDefinition @'
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint f, bool i, uint a);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformationW(IntPtr h, int n, System.Text.StringBuilder s, int l, out int need);
'@
function Name { $h = [Sw.D]::OpenInputDesktop(0, $false, 0x00020000); if ($h -eq [IntPtr]::Zero) { return '(cannot open: ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ')' }; try { $sb = New-Object System.Text.StringBuilder 256; $n = 0; [void][Sw.D]::GetUserObjectInformationW($h, 2, $sb, 256, [ref]$n); $sb.ToString() } finally { [void][Sw.D]::CloseDesktop($h) } }
$last = ''
$sw = [Diagnostics.Stopwatch]::StartNew()
# READY only once Default has held for a full second: a machine already showing the security
# screen (or a UAC prompt) would otherwise hand the check a Winlogon it did not cause.
$steadySince = -1; $ready = $false
while ($sw.ElapsedMilliseconds -lt 30000) {
    $n = Name
    if ($n -ne $last) { Write-Output ("{0,6} ms  input desktop: {1}" -f $sw.ElapsedMilliseconds, $n); $last = $n; $steadySince = $sw.ElapsedMilliseconds }
    if (-not $ready -and $n -eq 'Default' -and $sw.ElapsedMilliseconds - $steadySince -ge 1000) { Write-Output ("{0,6} ms  READY" -f $sw.ElapsedMilliseconds); $ready = $true; $readyAt = $sw.ElapsedMilliseconds }
    if ($ready -and $sw.ElapsedMilliseconds - $readyAt -gt 15000) { break }
    Start-Sleep -Milliseconds 150
}
Write-Output ("  final input desktop: " + (Name))
""";
    vc.Send(new ScriptRunMsg { RunId = runId, Language = "powershell", Script = watcher });
    bool watcherReady = false;
    for (int i = 0; i < 100 && !watcherReady; i++) { await Task.Delay(150); lock (watchOut) watcherReady = watchOut.ToString().Contains("READY"); }
    if (!watcherReady) { Log("the watcher never saw a settled Default desktop - is a security screen or UAC prompt already up there?"); }
}
// Only what happens AFTER the watcher said READY counts.
bool SwitchedAfterReady()
{
    lock (watchOut)
    {
        var s = watchOut.ToString();
        int at = s.IndexOf("READY", StringComparison.Ordinal);
        return at >= 0 && s.IndexOf("Winlogon", at, StringComparison.Ordinal) >= 0;
    }
}

Console.WriteLine();
Log($"sending {action}");
vc.Send(new SysActionMsg { Action = action });

var result = await Wait<SysActionResultMsg>(vq, 15000, r => r.Action == action);
bool sawWinlogon = false;
if (observe)
{
    // Give the switch time to happen, then dismiss it if it did.
    for (int i = 0; i < 24 && !sawWinlogon; i++) { await Task.Delay(250); sawWinlogon = SwitchedAfterReady(); }
    if (sawWinlogon)
    {
        await Task.Delay(1500);
        Log("the security screen is up; pressing Escape through the host");
        vc.Send(new KeyMsg { Vk = 0x1B, Down = true });
        await Task.Delay(80);
        vc.Send(new KeyMsg { Vk = 0x1B, Down = false });
    }
    await Task.WhenAny(watchDone.Task, Task.Delay(25000));
    Console.WriteLine();
    Console.WriteLine("what the input desktop did, seen from the host:");
    lock (watchOut) Console.WriteLine(watchOut.ToString().TrimEnd());
}
Console.WriteLine();
int exit;
if (result == null)
{
    Console.WriteLine($"  FAIL  nothing answered the {action} within 15 seconds.");
    Console.WriteLine("        Nothing on that machine is listening for it. After a ship the usual cause is");
    Console.WriteLine("        an agent service still running the previous binary - replacing the exe does not");
    Console.WriteLine("        restart the service. Check with tools/hostinfo/deployedprobe.ps1.");
    exit = 1;
}
else if (result.Ok)
{
    Console.WriteLine($"  PASS  {want} carried it out - {result.Detail}");
    if (action == "ctrl_alt_del") Console.WriteLine("        The Windows security screen should be up on that machine. Press Escape there.");
    exit = 0;
}
else
{
    Console.WriteLine($"  FAIL  {want} refused it - {result.Detail}");
    exit = 1;
}
if (observe)
{
    if (sawWinlogon) Console.WriteLine("  PASS  after it was sent, the input desktop switched to Winlogon: the security screen really came up");
    else { Console.WriteLine("  FAIL  after it was sent, the input desktop never switched to Winlogon: whatever the reply said, Ctrl+Alt+Del did not happen"); exit = 1; }

    // And the session has to still BE there. On DESKTOP-2N6FVGT, 2026-09-29, the security screen
    // came up every time and the host crashed two to four seconds later, every time — which from
    // the technician's chair is "Ctrl+Alt+Del does not work". A watcher that never printed its
    // last line died with the host that was carrying it.
    await Task.Delay(4000);
    bool hostGone = vq.Any(m => m is SessionEndedMsg || (m is ParticipantsMsg p && !p.HostConnected));
    bool watcherFinished; lock (watchOut) watcherFinished = watchOut.ToString().Contains("final input desktop");
    if (!hostGone && watcherFinished) Console.WriteLine("  PASS  the host survived the trip to the secure desktop and back");
    else
    {
        Console.WriteLine("  FAIL  the host did NOT survive: " + (hostGone ? "the session ended under us" : "the watcher it was running never finished")
                          + ". Run tools/hostinfo/hostcrash.ps1 there for the crash record.");
        exit = 1;
    }

    // A host that survives by giving up H.264 for the rest of the session is not the fix. Only
    // asked where the session started on H.264; a machine with no encoder has nothing to lose.
    if (codecBefore.StartsWith("H.264") && !hostGone)
    {
        for (int i = 0; i < 24 && !CodecNow().StartsWith("H.264"); i++) await Task.Delay(250);
        var after = CodecNow();
        bool kept = after.StartsWith("H.264");
        Console.WriteLine($"  {(kept ? "PASS" : "FAIL")}  the picture is H.264 again afterwards" + (kept ? "" : $" — it is now: {after}"));
        if (!kept) exit = 1;
    }
}

tc.Send(new EndSessionMsg { SessionId = sessionId });
await Task.Delay(600);
return exit;
