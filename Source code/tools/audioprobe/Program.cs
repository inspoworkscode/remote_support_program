using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Answers, by measurement, the question that decides the shape of the audio feature:
//
//   can a process running as SYSTEM in the interactive session see the logged-on user's
//   playback device, read its mute and volume, see which application has it open, and actually
//   make a sound come out of it?
//
// It runs tools/hostinfo/audioprobe.ps1 on the remote machine through the product's own script
// execution, so the script is a child of Host.exe and its token IS the host's token. That is the
// only honest way to ask: Edge refused to render a PDF as SYSTEM and said nothing at all about
// it (docs/HANDOVER.md 17), so a user-level API working here proves nothing about there.
//
//   audioprobe <machine> [--tone]
//   audioprobe <machine> --listen [seconds] [--unmute]
//
// --tone plays a 2-second 1 kHz tone on the customer's speakers. Read-only without it.
//
// --listen runs the OTHER half of the question instead: audiolisten.ps1, which opens WASAPI
// LOOPBACK capture with the host's own token. Rendering and capturing are different permissions
// and different code paths, and the whole audio feature rests on the second one. It plays a known
// 1 kHz tone while it captures and runs a Goertzel filter over the captured samples, so the
// answer is a pass or a fail rather than "I think I heard something".
//
// --unmute additionally measures whether a MUTED endpoint still feeds loopback. It briefly
// unmutes the customer's speakers and puts the mute back; without it the probe changes nothing.
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
bool tone = args.Any(a => a.Equals("--tone", StringComparison.OrdinalIgnoreCase));
bool listen = args.Any(a => a.Equals("--listen", StringComparison.OrdinalIgnoreCase));
bool unmute = args.Any(a => a.Equals("--unmute", StringComparison.OrdinalIgnoreCase));
string want = positional.Length > 0 ? positional[0] : "DESKTOP-OPHQIDH";

// --listen takes an optional seconds count; anything numeric on the command line is it, so the
// machine name is whichever positional argument is not a number.
int listenSeconds = 3;
foreach (var a in positional)
    if (int.TryParse(a, out var n) && n > 0 && n <= 60) { listenSeconds = n; want = positional.FirstOrDefault(x => x != a) ?? "DESKTOP-OPHQIDH"; }

if (unmute && !listen) { Console.WriteLine("--unmute only means anything with --listen."); return; }

string scriptName = listen ? "audiolisten.ps1" : "audioprobe.ps1";
string scriptPath = Path.Combine(AppContext.BaseDirectory, scriptName);
if (!File.Exists(scriptPath)) scriptPath = Path.Combine("tools", "hostinfo", scriptName);
if (!File.Exists(scriptPath)) { Console.WriteLine($"cannot find {scriptName} (looked in {scriptPath})"); return; }
string script = File.ReadAllText(scriptPath);

// The host runs the script with -File and no arguments, so a parameter cannot be passed in.
// Each script carries single marked lines for exactly this, and we refuse to run silently
// mis-flagged: if a marker is gone, say so rather than quietly probing with the wrong settings.
string Flip(string s, string off, string on, string marker)
{
    if (!s.Contains(off))
        throw new InvalidOperationException($"{scriptName} no longer carries the {marker} line; cannot set it.");
    return s.Replace(off, on);
}

try
{
    if (tone && !listen)
        script = Flip(script, "$PlayTone = $false   # AUDIOPROBE_TONE_FLAG",
                              "$PlayTone = $true   # AUDIOPROBE_TONE_FLAG", "AUDIOPROBE_TONE_FLAG");
    if (listen)
    {
        if (unmute)
            script = Flip(script, "$Unmute = $false   # AUDIOLISTEN_UNMUTE_FLAG",
                                  "$Unmute = $true   # AUDIOLISTEN_UNMUTE_FLAG", "AUDIOLISTEN_UNMUTE_FLAG");
        if (listenSeconds != 3)
            script = Flip(script, "$Seconds = 3   # AUDIOLISTEN_SECONDS",
                                  $"$Seconds = {listenSeconds}   # AUDIOLISTEN_SECONDS", "AUDIOLISTEN_SECONDS");
    }
}
catch (InvalidOperationException ex) { Console.WriteLine(ex.Message); return; }

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed: {lr.StatusCode} — is the Platform server running on {http}?"); return; }
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
if (st == null) { Console.WriteLine("no state from the server"); return; }

Console.WriteLine("=== devices the server knows about ===");
foreach (var d in st.Devices)
    Console.WriteLine($"  {d.MachineName,-20} id={d.DeviceId}  online={d.Online}  user={d.UserName}");
Console.WriteLine();

var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found"); return; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return; }

// If a session is already running the server hands that one back rather than starting a new one.
tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "audioprobe diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return; }

bool joinedExisting = ready.Session.HostConnected;
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}");
Console.WriteLine(joinedExisting
    ? "  -> joined a session that was already running; it will be left open"
    : "  -> started a new session; it will be ended when this finishes");
Console.WriteLine();

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);
var output = new StringBuilder();
var done = new TaskCompletionSource();
// Every handler goes on BEFORE RunAsync. The host's one-shot announcements — ParticipantsMsg,
// MonitorsMsg, SysInfoMsg — arrive in the first few milliseconds, and a handler added after the
// socket is already pumping misses them entirely. That cost an hour here: the wait below
// reported "the host never attached" while 601 CursorMsgs from that very host went past.
bool hostSeen = false;
var seen = new ConcurrentDictionary<string, int>();
vc.OnMessage += m =>
{
    seen.AddOrUpdate(m.GetType().Name, 1, (_, n) => n + 1);
    // Any of these can only have come from the host, so any of them proves it is there.
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is ScriptOutputMsg so)
    {
        output.Append(so.Text);
        if (so.Done) done.TrySetResult();
    }
};
_ = vc.RunAsync();

// Wait for the host to actually attach before sending it anything. The hub relays a viewer's
// message with `s.Host?.Send(m)` — if the host has not connected yet that is a silent no-op,
// and the script simply never runs. A brand-new session always has this gap; hostinfo only
// gets away with it because its four-second cursor watch happens to cover it.
if (!joinedExisting)
{
    Console.Write("waiting for the host to attach");
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline && !hostSeen) { Console.Write("."); await Task.Delay(500); }
    Console.WriteLine(hostSeen ? " attached." : " never attached.");
    if (!hostSeen)
    {
        // Say what DID arrive. "Nothing happened" is not a diagnosis, and an empty list here
        // means something quite different from a list full of ParticipantsMsg{HostConnected=false}.
        Console.WriteLine(seen.IsEmpty
            ? "  nothing at all arrived on the viewer socket in that time."
            : "  what did arrive: " + string.Join(", ", seen.Select(kv => $"{kv.Key} x{kv.Value}")));
        Console.WriteLine("the host never joined the session, so nothing could be run on it.");
        tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
        await Task.Delay(600);
        return;
    }
}

Console.WriteLine(listen
    ? $"running the LOOPBACK CAPTURE probe on the remote machine — {listenSeconds}s of capture, and it\n" +
      $"WILL play a 1 kHz tone there" + (unmute ? ", and will briefly unmute the speakers and put the mute back" : "") + "...\n"
    : tone
    ? "running the audio probe on the remote machine — it WILL play a 2-second tone there...\n"
    : "running the audio probe on the remote machine (read-only; pass --tone to play a tone)...\n");

vc.Send(new ScriptRunMsg { RunId = Guid.NewGuid().ToString("N"), Language = "powershell", Script = script });
bool finished = await Task.WhenAny(done.Task, Task.Delay(120000)) == done.Task;

Console.WriteLine(output.ToString());
if (!finished) Console.WriteLine("\n(the script did not report Done within 120s — output above may be partial)");

// Only tear down a session this tool started; ending a joined one closes the technician's
// window out from under them.
if (!joinedExisting)
{
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(600);
}
else Console.WriteLine("(joined an existing session — leaving it open)");
