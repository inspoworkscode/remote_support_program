using RemoteSupport.Host;
using RemoteSupport.Shared;
using RemoteSupport.Technician;

// ---------------------------------------------------------------------------------------------
// Tests the audio engine on THIS machine alone — no server, no session, no customer machine.
// Ten seconds.
//
// This is filesetcheck's trick applied to sound. Everything genuinely hard about streaming audio
// is hard here rather than on the wire: opening loopback capture, downmixing, resampling 48 kHz
// stereo float to 24 kHz mono 16-bit without a discontinuity at every packet boundary, framing,
// parsing the frame back, and the playback queue. The socket in between is the part that is
// already proven — tiles and whole file trees have used it for a fortnight.
//
// It plays a known 1 kHz tone through this machine's own speakers, captures it with the REAL
// host-side AudioCapture, sends the frames through the REAL protocol encoder and parser, and runs
// a Goertzel filter over what comes out the other end. So it is a pass or a fail, not an opinion.
//
//   audiolocal [--seconds N] [--play]
//
// --play also feeds the result to the REAL console-side AudioPlayer, so the machine plays back
// what it just heard itself play. That is the only part a human ear is better at judging than a
// filter, and it is the fastest way to hear a resampler that is subtly wrong.
// ---------------------------------------------------------------------------------------------

bool play = args.Any(a => a.Equals("--play", StringComparison.OrdinalIgnoreCase));
// Length matters for one particular reason: a FIXED error at each end of a run — the part-frame
// still in hand when capture stops, and the moment before the tone starts — looks exactly like a
// resampler running slow, until you run it three times as long and watch the percentage fall.
// Do that before believing either diagnosis.
int ToneSeconds = 5;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i].Equals("--seconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var n))
        ToneSeconds = Math.Clamp(n, 2, 120);

Console.WriteLine("audiolocal — capture, resample, frame, parse and play, all on this machine.");
Console.WriteLine();

// --devices runs the OTHER audio feature on this machine: the diagnostic that explains why a
// machine is silent. Same code the Sound tab shows, so what prints here is what a technician
// sees, and it can be read against Windows' own sound settings page to check it is telling the
// truth. It changes nothing and takes no time, so it runs before the capture test.
if (args.Any(a => a.Equals("--devices", StringComparison.OrdinalIgnoreCase)))
{
    var d = AudioDiagnostics.Collect();
    Console.WriteLine("=== services ===");
    foreach (var s in d.Services) Console.WriteLine("  " + s);
    Console.WriteLine();
    Console.WriteLine("=== endpoints ===");
    foreach (var dev in d.Devices)
    {
        var roles = new List<string>();
        if (dev.IsDefaultConsole) roles.Add("default: system sounds");
        if (dev.IsDefaultMultimedia) roles.Add("default: music and video");
        if (dev.IsDefaultCommunications) roles.Add("default: calls");
        Console.WriteLine($"  [{(dev.IsCapture ? "rec " : "play")}] {dev.State,-10} {dev.Name}");
        if (dev.Adaptor.Length > 0) Console.WriteLine($"         adaptor: {dev.Adaptor}");
        if (roles.Count > 0) Console.WriteLine($"         {string.Join(", ", roles)}");
        if (dev.VolumePercent >= 0) Console.WriteLine($"         volume {dev.VolumePercent}%   mute {(dev.Muted ? "ON" : "off")}");
        if (dev.Error.Length > 0) Console.WriteLine($"         {dev.Error}");
    }
    Console.WriteLine();
    Console.WriteLine("=== applications on the default playback device ===");
    if (d.Apps.Count == 0) Console.WriteLine("  none");
    foreach (var a in d.Apps)
        Console.WriteLine($"  {a.Name,-26} {a.State,-8} mixer {a.VolumePercent}%{(a.Muted ? "  MUTED" : "")}");
    if (d.Error.Length > 0) { Console.WriteLine(); Console.WriteLine("ERROR: " + d.Error); }
    Console.WriteLine();
}

// --findings exercises the Sound tab's diagnosis rules against MADE-UP machines. Every one of
// these is a real support call, and arranging them on actual hardware would mean unplugging
// speakers, stopping services and muting things one at a time on somebody's computer. The rules
// are pure, so they can simply be asked.
if (args.Any(a => a.Equals("--findings", StringComparison.OrdinalIgnoreCase)))
{
    int bad = 0;
    void Case(string name, AudioDevicesMsg m, string mustSay)
    {
        var found = AudioFindings.For(m);
        bool hit = found.Any(f => f.Contains(mustSay, StringComparison.OrdinalIgnoreCase));
        if (!hit) bad++;
        Console.WriteLine($"  {(hit ? "pass" : "FAIL")}  {name}");
        foreach (var f in found) Console.WriteLine("          • " + f);
        if (!hit) Console.WriteLine($"          expected something containing: \"{mustSay}\"");
    }

    static AudioDeviceInfo Dev(string name, string state = "ACTIVE", bool muted = false, int vol = 50,
                               bool console = false, bool multi = false, string adaptor = "", string id = "")
        => new()
        {
            Id = id.Length > 0 ? id : name, Name = name, Adaptor = adaptor, State = state,
            Muted = muted, VolumePercent = vol, IsDefaultConsole = console, IsDefaultMultimedia = multi
        };

    Console.WriteLine("=== Sound tab findings, against machines that are a nuisance to arrange ===");

    // The healthy machine is the one case that must produce NOTHING, so it is checked the other
    // way round. A diagnostic that invents faults on a working machine is worse than none —
    // that lesson was paid for twice in the audio harnesses already.
    {
        var healthy = new AudioDevicesMsg { Devices = { Dev("Speakers (Realtek)", console: true, multi: true) }, Services = { "Audiosrv: running" } };
        var f = AudioFindings.For(healthy);
        bool ok = f.Count == 0;
        if (!ok) bad++;
        Console.WriteLine($"  {(ok ? "pass" : "FAIL")}  a healthy machine produces no findings ({f.Count} found)");
        foreach (var x in f) Console.WriteLine("          • " + x);
    }

    Case("muted default device",
        new AudioDevicesMsg { Devices = { Dev("Speakers (Realtek)", muted: true, console: true, multi: true) } },
        "MUTED");

    Case("volume at 0%",
        new AudioDevicesMsg { Devices = { Dev("Speakers (Realtek)", vol: 0, console: true, multi: true) } },
        "volume is 0%");

    Case("no playback devices at all",
        new AudioDevicesMsg(), "NO playback devices");

    Case("every device disabled or unplugged",
        new AudioDevicesMsg { Devices = { Dev("Speakers", "DISABLED"), Dev("Headphones", "UNPLUGGED") } },
        "No playback device is active");

    Case("devices exist but none is the default",
        new AudioDevicesMsg { Devices = { Dev("Speakers") } },
        "no default playback device");

    Case("the defaults disagree — works in Teams, not in YouTube",
        new AudioDevicesMsg { Devices = { Dev("Speakers", console: true, id: "a"), Dev("LCDTV16", multi: true, id: "b") } },
        "different devices");

    Case("default output is a television",
        new AudioDevicesMsg { Devices = { Dev("LCDTV16", console: true, multi: true, adaptor: "HD Audio Driver for Display Audio") } },
        "television audio");

    Case("one application muted in the mixer",
        new AudioDevicesMsg
        {
            Devices = { Dev("Speakers", console: true, multi: true) },
            Apps = { new AudioAppSession { Name = "firefox", State = "playing", VolumePercent = 100, Muted = true } }
        },
        "firefox is MUTED in the volume mixer");

    Case("the audio service is stopped",
        new AudioDevicesMsg
        {
            Devices = { Dev("Speakers", console: true, multi: true) },
            Services = { "Audiosrv: STOPPED", "AudioEndpointBuilder: running" }
        },
        "nothing will play until that is running");

    Console.WriteLine();
    Console.WriteLine(bad == 0
        ? "PASS: every one of those machines was diagnosed correctly."
        : $"FAIL: {bad} case(s) were not diagnosed.");
    Console.WriteLine();
    if (bad > 0) return 1;
}

// ---- the tone this machine will play at itself ----------------------------------------------
var wav = ToneWav(44100, ToneSeconds + 1, 1000);
var player = new System.Media.SoundPlayer(wav);

var frames = new List<byte[]>();
var statuses = new List<AudioStatusMsg>();
var capture = new AudioCapture();
// --exclude-self (3.3): capture as Listen does DURING A VOICE CALL — every process but this one,
// which is the one playing the tone. The verdict turns round: the tone must NOT be heard, while
// the stream itself keeps running. That is the technician not hearing their own voice come back.
bool excludeSelf = args.Any(a => a.Equals("--exclude-self", StringComparison.OrdinalIgnoreCase));
capture.ExcludeSelf = excludeSelf;
if (excludeSelf) Console.WriteLine("--exclude-self: capturing everything EXCEPT this process, which plays the tone");

capture.Start(f => { lock (frames) frames.Add(f); }, s => { lock (frames) statuses.Add(s); });

// Give the capture thread a moment to open the endpoint, then start the tone. Capturing before
// the sound starts is deliberate: it also measures what arrives when nothing is playing.
Thread.Sleep(700);
int quietFrames; lock (frames) quietFrames = frames.Count;
Console.WriteLine($"with nothing playing: {quietFrames} frame(s) captured in 0.7 s");

try { player.Play(); } catch (Exception ex) { Console.WriteLine("the tone would not start: " + ex.Message); }
var started = DateTime.UtcNow;
Thread.Sleep(ToneSeconds * 1000);
var wall = (DateTime.UtcNow - started).TotalSeconds;
try { player.Stop(); } catch { }
capture.Stop();

byte[][] got; AudioStatusMsg[] said;
lock (frames) { got = frames.Skip(quietFrames).ToArray(); said = statuses.ToArray(); }

Console.WriteLine();
Console.WriteLine("=== what the capture side said ===");
foreach (var s in said)
    Console.WriteLine(s.Error.Length > 0
        ? "  ERROR: " + s.Error
        : s.Listening
            ? $"  listening to \"{s.Device}\" — sending {s.SampleRate} Hz {s.Channels} ch, this machine's volume {s.VolumePercent}%, mute {(s.Muted ? "ON" : "off")}"
            : "  stopped");
if (said.Length == 0) Console.WriteLine("  (nothing — which is itself a finding)");

// ---- parse every frame back through the real protocol ----------------------------------------
var samples = new List<short>();
int gaps = 0, missed = 0, rate = 0, channels = 0, format = 0;
uint last = 0; bool haveSeq = false;
foreach (var f in got)
{
    if (f.Length <= Bin.AudioHeader || f[0] != BinKind.Audio) { Console.WriteLine("  a frame was not an audio frame at all — that is a framing bug"); continue; }
    var (seq, r, ch, fmt, pcm) = Bin.ParseAudio(f);
    rate = r; channels = ch; format = fmt;
    if (haveSeq && seq != last + 1) { gaps++; if (seq > last) missed += (int)(seq - last - 1); }
    last = seq; haveSeq = true;
    var s = pcm.Span;
    for (int i = 0; i + 1 < s.Length; i += 2) samples.Add((short)(s[i] | (s[i + 1] << 8)));
}

Console.WriteLine();
Console.WriteLine("=== what came out of the frames ===");
Console.WriteLine($"  frames        : {got.Length}   sequence gaps: {gaps} ({missed} missing)");
Console.WriteLine($"  format        : {rate} Hz, {channels} channel(s), format id {format}");
Console.WriteLine($"  samples       : {samples.Count}");
double heard = rate > 0 ? (double)samples.Count / rate : 0;
Console.WriteLine($"  audio         : {heard:F2} s captured in {wall:F2} s of wall clock");
if (heard > 0)
{
    // Capture and the machine clock are the SAME clock here, so anything but ~0 is the
    // resampler losing or inventing samples — which is exactly what this is here to catch.
    double err = (heard - wall) / wall * 100;
    Console.WriteLine($"  resampler     : {err:+0.00;-0.00;0.00}% {(Math.Abs(err) < 1.0 ? "(sound)" : "<-- the resampler is dropping or inventing samples)")}");
}

var arr = samples.ToArray();
double peak = 0, rms = 0;
foreach (var s in arr) { double a = Math.Abs(s) / 32768.0; if (a > peak) peak = a; rms += a * a; }
if (arr.Length > 0) rms = Math.Sqrt(rms / arr.Length);
Console.WriteLine($"  peak          : {peak:F5}   rms: {rms:F5}");

double g1000 = Goertzel(arr, rate, 1000), g700 = Goertzel(arr, rate, 700), g1300 = Goertzel(arr, rate, 1300);
Console.WriteLine($"  goertzel      : 1000 Hz={g1000:F5}   700 Hz={g700:F5}   1300 Hz={g1300:F5}");

Console.WriteLine();
int exit;
bool toneHeard = g1000 > 0.001 && g1000 > Math.Max(g700, g1300) * 8;
if (excludeSelf)
{
    if (arr.Length == 0) { Console.WriteLine("FAIL: nothing was captured at all — process loopback did not run."); exit = 1; }
    else if (toneHeard) { Console.WriteLine("FAIL: this process's own tone was captured — the technician would hear their own voice."); exit = 1; }
    else { Console.WriteLine("PASS: the stream ran and this process's own tone was left out of it."); exit = 0; }
}
else if (arr.Length == 0)
{
    Console.WriteLine("FAIL: nothing was captured while this machine was playing a tone at itself.");
    exit = 1;
}
else if (g1000 > 0.001 && g1000 > Math.Max(g700, g1300) * 8)
{
    Console.WriteLine("PASS: capture, downmix, resampling, framing and parsing all carried the 1 kHz tone.");
    exit = 0;
}
else
{
    Console.WriteLine("FAIL: audio was captured, but the 1 kHz tone is not in it.");
    Console.WriteLine("      Sound reached the frames, so look at the downmix and the resampler, not the plumbing.");
    exit = 1;
}

// ---- optionally hand it to the real player ---------------------------------------------------
if (play && arr.Length > 0)
{
    Console.WriteLine();
    Console.WriteLine("playing back what was captured, through the console's own AudioPlayer...");
    var outp = new AudioPlayer();
    if (!outp.Start()) Console.WriteLine("  the player refused: " + outp.Error);
    else
    {
        // Fed at real time rather than all at once, so the jitter buffer and the starvation
        // handling are exercised the way a live session exercises them.
        //
        // Fed against a SCHEDULE, not Sleep(40) per frame. Thread.Sleep(40) reliably sleeps a
        // little longer than 40 ms, so a loop that sleeps per frame feeds a few percent slower
        // than real time and starves the player on purpose — and then the starvation count
        // measures the harness rather than the player. That was the first reading taken here:
        // 34 starvations out of 124 frames, all of them self-inflicted.
        int perFrame = AudioPlayer.Rate * 40 / 1000;
        var feedStart = DateTime.UtcNow;
        int frameNo = 0;
        for (int i = 0; i + perFrame <= arr.Length; i += perFrame)
        {
            var pcm = new byte[perFrame * 2];
            for (int j = 0; j < perFrame; j++)
            {
                short v = arr[i + j];
                pcm[j * 2] = (byte)(v & 0xFF);
                pcm[j * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            outp.Feed(pcm);
            frameNo++;
            var due = feedStart.AddMilliseconds(frameNo * 40.0);
            var wait = (int)(due - DateTime.UtcNow).TotalMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
        Thread.Sleep(500);
        Console.WriteLine("  " + outp.DiagnosticSummary());
        outp.Stop();
    }
}

return exit;

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

static MemoryStream ToneWav(int rate, double seconds, int freq)
{
    int samples = (int)(rate * seconds);
    var ms = new MemoryStream();
    var bw = new BinaryWriter(ms);
    int dataBytes = samples * 2;
    bw.Write("RIFF".ToCharArray()); bw.Write(36 + dataBytes);
    bw.Write("WAVE".ToCharArray()); bw.Write("fmt ".ToCharArray());
    bw.Write(16); bw.Write((short)1); bw.Write((short)1);
    bw.Write(rate); bw.Write(rate * 2);
    bw.Write((short)2); bw.Write((short)16);
    bw.Write("data".ToCharArray()); bw.Write(dataBytes);
    double fade = rate * 0.05;
    for (int i = 0; i < samples; i++)
    {
        double env = 1.0;
        if (i < fade) env = i / fade;
        else if (i > samples - fade) env = (samples - i) / fade;
        bw.Write((short)(12000 * env * Math.Sin(2 * Math.PI * freq * i / rate)));
    }
    bw.Flush();
    ms.Seek(0, SeekOrigin.Begin);
    return ms;
}
