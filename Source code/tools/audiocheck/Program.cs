using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// Proves the audio pipeline end to end, by measurement, without anybody listening.
//
// This is treecheck's rule applied to sound: DO NOT LET A TRANSFER MARK ITS OWN HOMEWORK. The
// customer's machine is told to play a known 1 kHz tone; this harness records what actually
// arrives at the technician's end of the socket and runs a Goertzel filter over it. If the
// energy at 1 kHz is far above its neighbours, the bytes that arrived really are the sound that
// machine made — through capture, resampling, the wire and the framing. That is a pass or a
// fail at two in the morning with every speaker in the building muted, and it is what makes the
// feature provable rather than plausible.
//
//   audiocheck <machine> [--seconds N] [--wav out.wav] [--screen]
//
// --wav also writes what arrived, so a human can listen to it afterwards if they want to.
// --screen subscribes to a monitor as well, so the audio is measured while screen tiles are
//   competing for the same socket — which is the case that matters and the one the bandwidth
//   question in docs/NEXT-SESSION-AUDIO.md turns on.
//
// It reports, and every one of these has been a real failure mode in this codebase:
//   - whether any audio arrived at all
//   - whether the 1 kHz tone is in it
//   - sequence gaps (frames the socket lost or the host never sent)
//   - received duration against wall-clock duration, which is CLOCK DRIFT in its raw form
//   - bandwidth, audio and screen separately
// ---------------------------------------------------------------------------------------------

string http = "http://localhost:8090", ws = "ws://localhost:8090";
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
string want = positional.Length > 0 ? positional[0] : "DESKTOP-OPHQIDH";
int seconds = 6;
string wavPath = "";
bool withScreen = args.Any(a => a.Equals("--screen", StringComparison.OrdinalIgnoreCase));
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i].Equals("--seconds", StringComparison.OrdinalIgnoreCase)) int.TryParse(args[i + 1], out seconds);
    if (args[i].Equals("--wav", StringComparison.OrdinalIgnoreCase)) wavPath = args[i + 1];
}
seconds = Math.Clamp(seconds, 2, 60);

string scriptPath = Path.Combine(AppContext.BaseDirectory, "audiotone.ps1");
if (!File.Exists(scriptPath)) scriptPath = Path.Combine("tools", "hostinfo", "audiotone.ps1");
if (!File.Exists(scriptPath)) { Console.WriteLine($"cannot find audiotone.ps1 (looked in {scriptPath})"); return 2; }
string toneScript = File.ReadAllText(scriptPath);
{
    // The host runs scripts with -File and no arguments, so the length is patched into the
    // marked line. If the marker has gone, say so rather than silently playing the default.
    const string marker = "$Seconds = 6   # AUDIOTONE_SECONDS";
    if (!toneScript.Contains(marker)) { Console.WriteLine("audiotone.ps1 no longer carries the AUDIOTONE_SECONDS line."); return 2; }
    toneScript = toneScript.Replace(marker, $"$Seconds = {seconds + 2}   # AUDIOTONE_SECONDS");
}

using var hc = new HttpClient();
var creds = RemoteSupport.Tools.RsCredentials.Load();
var lr = await hc.PostAsJsonAsync($"{http}/api/login", new { email = creds.Email, password = creds.Password, code = creds.Code() });
if (!lr.IsSuccessStatusCode) { Console.WriteLine($"login failed: {lr.StatusCode} — is the Platform server running on {http}?"); return 2; }
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
if (st == null) { Console.WriteLine("no state from the server"); return 2; }
var dev = st.Devices.FirstOrDefault(d => d.MachineName.Equals(want, StringComparison.OrdinalIgnoreCase));
if (dev == null) { Console.WriteLine($"device {want} not found"); return 2; }
if (!dev.Online) { Console.WriteLine($"device {want} is offline"); return 2; }

tc.Send(new ConnectDeviceMsg { DeviceId = dev.DeviceId, Reason = "audiocheck diagnostic" });
var ready = await W<SessionReadyMsg>(20000, s => s.Session.DeviceId == dev.DeviceId);
if (ready == null) { Console.WriteLine("no session"); return 2; }
bool joinedExisting = ready.Session.HostConnected;
Console.WriteLine($"session {ready.Session.SessionId} on {ready.Session.MachineName}");
Console.WriteLine(joinedExisting ? "  -> joined a session that was already running" : "  -> started a new session");

var v = new ClientWebSocket();
await v.ConnectAsync(new Uri($"{ws}/ws/session?role=viewer&id={ready.Session.SessionId}&token={tok}"), default);
var vc = new WsConnection(v);

// EVERY handler goes on BEFORE RunAsync. The host's one-shot announcements arrive in the first
// few milliseconds and a handler added afterwards misses them for good (HANDOVER 10).
var samples = new List<short>();
var audioLock = new object();
long audioBytes = 0, tileBytes = 0;
int audioFrames = 0, seqGaps = 0, missedFrames = 0;
uint lastSeq = 0; bool haveSeq = false;
int gotRate = 0, gotChannels = 0, gotFormat = 0;
bool hostSeen = false;
AudioStatusMsg? lastStatus = null;
var statuses = new List<string>();
var scriptOut = new System.Text.StringBuilder();
var scriptDone = new TaskCompletionSource();
var seen = new ConcurrentDictionary<string, int>();
DateTime firstFrameAt = DateTime.MinValue, lastFrameAt = DateTime.MinValue;
var monitors = new List<MonitorInfo>();

vc.OnMessage += m =>
{
    seen.AddOrUpdate(m.GetType().Name, 1, (_, n) => n + 1);
    if (m is CursorMsg or MonitorsMsg or SysInfoMsg) hostSeen = true;
    if (m is ParticipantsMsg p && p.HostConnected) hostSeen = true;
    if (m is MonitorsMsg mm) { lock (audioLock) { monitors.Clear(); monitors.AddRange(mm.Monitors); } }
    if (m is AudioStatusMsg a)
    {
        lastStatus = a;
        statuses.Add(a.Error.Length > 0
            ? $"ERROR: {a.Error}"
            : a.Listening
                ? $"listening to \"{a.Device}\" at {a.SampleRate} Hz {a.Channels} ch, their volume {a.VolumePercent}%, mute {(a.Muted ? "ON" : "off")}"
                : "the host says it is no longer listening");
    }
    if (m is ScriptOutputMsg so) { scriptOut.Append(so.Text); if (so.Done) scriptDone.TrySetResult(); }
};
vc.OnBinary += frame =>
{
    if (frame.Length < 1) return;
    var kind = frame.Span[0];
    if (kind == BinKind.Tiles) { Interlocked.Add(ref tileBytes, frame.Length); return; }
    if (kind != BinKind.Audio || frame.Length <= Bin.AudioHeader) return;
    var (seq, rate, channels, format, pcm) = Bin.ParseAudio(frame);
    lock (audioLock)
    {
        gotRate = rate; gotChannels = channels; gotFormat = format;
        if (haveSeq && seq != lastSeq + 1)
        {
            seqGaps++;
            // How MANY frames went missing matters as much as how often: one gap of fifty is a
            // stall, fifty gaps of one is a socket dropping packets, and they need different fixes.
            if (seq > lastSeq) missedFrames += (int)(seq - lastSeq - 1);
        }
        lastSeq = seq; haveSeq = true;
        audioFrames++;
        audioBytes += frame.Length;
        if (firstFrameAt == DateTime.MinValue) firstFrameAt = DateTime.UtcNow;
        lastFrameAt = DateTime.UtcNow;
        var s = pcm.Span;
        for (int i = 0; i + 1 < s.Length; i += 2) samples.Add((short)(s[i] | (s[i + 1] << 8)));
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
        Console.WriteLine(seen.IsEmpty
            ? "  nothing at all arrived on the viewer socket in that time."
            : "  what did arrive: " + string.Join(", ", seen.Select(kv => $"{kv.Key} x{kv.Value}")));
        tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
        await Task.Delay(600);
        return 2;
    }
}

if (withScreen)
{
    // Subscribe the way the console does, so the audio is measured against a socket that is
    // genuinely busy rather than an idle one.
    int mon = monitors.FirstOrDefault()?.Index ?? 0;
    vc.Send(new SubscribeMsg { Monitor = mon, On = true });
    Console.WriteLine($"subscribed to monitor {mon}, so screen tiles are competing for the same socket");
}

Console.WriteLine("asking the host to start listening...");
vc.Send(new AudioListenMsg { On = true });
var listenStart = DateTime.UtcNow;
while (DateTime.UtcNow - listenStart < TimeSpan.FromSeconds(10) && lastStatus == null) await Task.Delay(100);
if (lastStatus == null) Console.WriteLine("  (the host said nothing about audio in 10 s — carrying on so the numbers tell the story)");
else if (lastStatus.Error.Length > 0)
{
    Console.WriteLine("  the host refused: " + lastStatus.Error);
    vc.Send(new AudioListenMsg { On = false });
    if (!joinedExisting) { tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId }); await Task.Delay(600); }
    return 1;
}

// ---- phase one: nothing playing -------------------------------------------------------------
// WASAPI loopback sends nothing at all when the machine is quiet, so "no frames" here is the
// expected, correct answer — and knowing that is what stops it being reported as a fault.
//
// Zero the counter FIRST. Without this the count includes everything that arrived while we were
// waiting up to ten seconds for the host to answer, and this line once read "288 frames in 1.5 s"
// — eleven seconds of audio in a second and a half, which is arithmetic nobody should have to
// do in their head to work out that the harness is talking nonsense.
lock (audioLock) audioFrames = 0;
await Task.Delay(1500);
int quietFrames; lock (audioLock) quietFrames = audioFrames;
Console.WriteLine($"with nothing playing: {quietFrames} audio frame(s) in 1.5 s" +
                  (quietFrames > 5 ? "  <-- something on that machine IS making a noise" : ""));

// ---- phase two: a known tone ----------------------------------------------------------------
Console.WriteLine($"playing a 1 kHz tone on {want} and recording {seconds} s of what comes back...");
vc.Send(new ScriptRunMsg { RunId = Guid.NewGuid().ToString("N"), Language = "powershell", Script = toneScript });

// Let the tone actually start, THEN zero everything in one place. The first version zeroed the
// counters, waited, and then cleared only the samples — so a second's worth of frames counted
// towards the frame and byte totals while their audio was thrown away. The totals then disagreed
// with each other by exactly that much and the bandwidth figure came out 20% high, which is a
// very believable wrong answer to the one question this tool exists to answer.
await Task.Delay(1200);
lock (audioLock)
{
    samples.Clear();
    audioFrames = 0; audioBytes = 0; tileBytes = 0;
    seqGaps = 0; missedFrames = 0;
    firstFrameAt = DateTime.MinValue; lastFrameAt = DateTime.MinValue;
}
var recordStart = DateTime.UtcNow;
await Task.Delay(seconds * 1000);
var wall = (DateTime.UtcNow - recordStart).TotalSeconds;

vc.Send(new AudioListenMsg { On = false });
await Task.Delay(400);

// ---- the verdict ----------------------------------------------------------------------------
short[] got; int frames, gaps, missed; long aBytes, tBytes; DateTime f0, f1;
lock (audioLock) { got = samples.ToArray(); frames = audioFrames; gaps = seqGaps; missed = missedFrames; aBytes = audioBytes; tBytes = tileBytes; f0 = firstFrameAt; f1 = lastFrameAt; }

Console.WriteLine();
Console.WriteLine("=== what the host said ===");
foreach (var s in statuses.Distinct()) Console.WriteLine("  " + s);
if (statuses.Count == 0) Console.WriteLine("  (nothing)");

Console.WriteLine();
Console.WriteLine("=== what arrived ===");
Console.WriteLine($"  format        : {gotRate} Hz, {gotChannels} channel(s), format id {gotFormat}");
Console.WriteLine($"  frames        : {frames}   sequence gaps: {gaps} ({missed} frame(s) missing)");
Console.WriteLine($"  samples       : {got.Length}");
// The frame count and the sample count are two accounts of the same thing and must agree. When
// they did not, the bandwidth figure was 20% high and nothing else looked wrong at all. Say so
// rather than letting the reader do the division.
if (gotRate > 0 && frames > 0)
{
    int perFrame = gotRate * 40 / 1000;
    int expected = frames * perFrame;
    if (Math.Abs(got.Length - expected) > perFrame)
        Console.WriteLine($"  MISMATCH      : {frames} frames should be {expected} samples, not {got.Length}." +
                          "\n                  These are two accounts of the same audio and they disagree, so the" +
                          "\n                  numbers below are unsafe. That is a fault in THIS TOOL, not the product.");
}
double heard = gotRate > 0 ? (double)got.Length / gotRate : 0;
Console.WriteLine($"  audio         : {heard:F2} s of sound in a {wall:F2} s window");

// Drift must be measured against the time frames were ARRIVING, not against the whole window.
// WASAPI loopback sends nothing at all while the machine is quiet, so a slow start at the far
// end — a tone generator still thinking, or simply nothing playing yet — is silence that was
// never sent, not audio that was lost. Measuring against the window called exactly that a "32%
// clock error" on the first real run, which is a harness telling a lie about the thing it exists
// to measure. The span below is honest, and the silent lead-in is reported as what it is.
if (frames > 1 && heard > 0)
{
    double span = (f1 - f0).TotalSeconds + 0.04;   // the last frame carries 40 ms of its own
    double quiet = Math.Max(0, wall - span);
    Console.WriteLine($"  arriving over : {span:F2} s   (silent, nothing sent: {quiet:F2} s)");
    if (span > 0.5)
    {
        // A pipeline 1% short over the span it was actually running is losing a packet every
        // hundred; 0.01% is two crystals disagreeing, which is normal and is what the console's
        // queue management exists to absorb.
        double drift = (heard - span) / span * 100;
        Console.WriteLine($"  clock         : {drift:+0.00;-0.00;0.00}% over that span " +
                          (Math.Abs(drift) < 1.0 ? "(normal)" : "<-- worth a look"));
    }
    if (quiet > 0.5)
        Console.WriteLine($"                  {quiet:F2} s of this window had NO audio at all. That is what a quiet\n" +
                          "                  machine looks like, not a fault — but if the tone was meant to be\n" +
                          "                  playing throughout, look at how long audiotone.ps1 took to build it.");
}
// Audio is rated over the span it was actually flowing — averaging it over a window that was
// half silent understates it by exactly the amount of silence, and this number is the input to
// the codec decision, so it has to mean something.
double rateSpan = frames > 1 ? Math.Max(0.001, (f1 - f0).TotalSeconds + 0.04) : Math.Max(0.001, wall);
Console.WriteLine($"  bandwidth     : audio {aBytes / rateSpan / 1024:F1} KB/s while flowing" +
                  (withScreen ? $", screen {tBytes / Math.Max(0.001, wall) / 1024:F1} KB/s" : " (screen not subscribed; pass --screen)"));
if (withScreen && tBytes / Math.Max(0.001, wall) / 1024 < 5)
    Console.WriteLine("                  the screen figure is near zero because an IDLE desktop sends almost\n" +
                      "                  nothing — only changed tiles go on the wire. To measure real contention,\n" +
                      "                  have something moving on that machine's screen while this runs.");

double peak = 0, rms = 0;
foreach (var s in got) { double a = Math.Abs(s) / 32768.0; if (a > peak) peak = a; rms += a * a; }
if (got.Length > 0) rms = Math.Sqrt(rms / got.Length);
Console.WriteLine($"  peak          : {peak:F5}   rms: {rms:F5}");

double g1000 = Goertzel(got, gotRate, 1000), g700 = Goertzel(got, gotRate, 700), g1300 = Goertzel(got, gotRate, 1300);
Console.WriteLine($"  goertzel      : 1000 Hz={g1000:F5}   700 Hz={g700:F5}   1300 Hz={g1300:F5}");

if (wavPath.Length > 0 && got.Length > 0)
{
    WriteWav(wavPath, got, gotRate);
    Console.WriteLine($"  wrote         : {Path.GetFullPath(wavPath)} — listen to it if you want the other half of the answer");
}

Console.WriteLine();
int exit;
double neighbour = Math.Max(g700, g1300);
if (frames == 0)
{
    Console.WriteLine("FAIL: no audio arrived at all while the machine was playing a tone.");
    Console.WriteLine("      Look at what the host said above, then at %ProgramData%\\RemoteSupport\\host-trace.log on that machine.");
    exit = 1;
}
else if (g1000 > 0.001 && g1000 > neighbour * 8)
{
    Console.WriteLine("PASS: the audio that arrived CONTAINS the 1 kHz tone that machine was playing.");
    Console.WriteLine("      Capture, resampling, framing, transport and reassembly are all proven by this.");
    exit = 0;
}
else
{
    Console.WriteLine("FAIL: audio arrived, but it is not the tone.");
    Console.WriteLine("      Sound got through the pipeline, so this is a CONTENT fault — the resampler, the");
    Console.WriteLine("      channel downmix, or the machine playing something else — not a transport one.");
    exit = 1;
}
if (gaps > 0) Console.WriteLine($"NOTE: {gaps} sequence gap(s), {missed} frame(s) lost. That is audible as a click each time.");

if (!joinedExisting)
{
    tc.Send(new EndSessionMsg { SessionId = ready.Session.SessionId });
    await Task.Delay(600);
}
else Console.WriteLine("(joined an existing session — leaving it open)");
return exit;

// A dozen lines, no FFT library. Returns the normalised magnitude at one frequency.
static double Goertzel(short[] x, int rate, double freq)
{
    int n = x.Length;
    if (n < 256 || rate <= 0) return 0;
    double k = Math.Round(n * freq / rate);
    double w = 2.0 * Math.PI * k / n;
    double coeff = 2.0 * Math.Cos(w);
    double s1 = 0, s2 = 0;
    for (int i = 0; i < n; i++)
    {
        double s0 = x[i] / 32768.0 + coeff * s1 - s2;
        s2 = s1; s1 = s0;
    }
    double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
    return power <= 0 ? 0 : Math.Sqrt(power) / (n / 2.0);
}

static void WriteWav(string path, short[] samples, int rate)
{
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    int dataBytes = samples.Length * 2;
    bw.Write("RIFF".ToCharArray()); bw.Write(36 + dataBytes);
    bw.Write("WAVE".ToCharArray()); bw.Write("fmt ".ToCharArray());
    bw.Write(16); bw.Write((short)1); bw.Write((short)1);
    bw.Write(rate); bw.Write(rate * 2);
    bw.Write((short)2); bw.Write((short)16);
    bw.Write("data".ToCharArray()); bw.Write(dataBytes);
    foreach (var s in samples) bw.Write(s);
}
