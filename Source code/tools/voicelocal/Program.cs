using RemoteSupport.Media;
using RemoteSupport.Shared;

// ---------------------------------------------------------------------------------------------
// The voice engine (3.3) on THIS machine alone: no server, no session. Audiolocal's trick again.
//
// It opens the call's two halves exactly as both ends of a call do — the VoicePlayer FIRST, then
// the EchoCancelledMic, because the canceller delivers nothing until a render stream is open
// (micprobe, 2026-09-30) — and checks, by numbers:
//
//   1. the microphone delivers frames at the rate it should (50 a second), in .NET 8, through the
//      COM callable wrapper the DSP writes into — micprobe proved the DSP in PowerShell only;
//   2. while the player plays SILENCE (nobody talking): the case that silenced the microphone
//      in the probe until a render stream was found to be the reason;
//   3. with --tone, while the player plays 250 ms PULSES of 1 kHz through the same Feed() path a
//      technician's voice takes: how much of it reaches the far end. A Goertzel filter compares
//      the microphone's own 1 kHz reading with the pulses on and off. Pulses, because a steady
//      tone is removed by noise suppression and would flatter the canceller (micprobe again).
//
//   voicelocal [--seconds N] [--tone]
//
// --tone BEEPS through this machine's speakers. Without it nothing is heard at all.
// ---------------------------------------------------------------------------------------------

bool tone = args.Any(a => a.Equals("--tone", StringComparison.OrdinalIgnoreCase));
int seconds = 4;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--seconds" && int.TryParse(args[i + 1], out var n)) seconds = Math.Clamp(n, 2, 60);

int pass = 0, fail = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what); if (ok) pass++; else fail++; }

Console.WriteLine("voicelocal — the call's player and echo-cancelled microphone, on this machine.");
var log = new List<string>();
var player = new VoicePlayer(l => { lock (log) log.Add(l); });
Check(player.Start(), "the voice player opened on the communications device" + (player.Error != null ? " — " + player.Error : ""));

var heard = new List<short>();
var mic = new EchoCancelledMic(pcm =>
{
    lock (heard) for (int i = 0; i + 1 < pcm.Length; i += 2) heard.Add((short)(pcm[i] | (pcm[i + 1] << 8)));
}, l => { lock (log) log.Add(l); });

var t0 = DateTime.UtcNow;
bool open = mic.Start(out var why);
Check(open, $"the echo-cancelled microphone opened ({(open ? mic.DeviceName : why)}) in {(DateTime.UtcNow - t0).TotalMilliseconds:F0} ms");
if (!open) { player.Stop(); Finish(); return; }

// Feed the player exactly as the host feeds it from the wire: one 20 ms packet every 20 ms.
var frame = new byte[VoiceWire.FrameSamples * 2];
long sent = 0;
var start = DateTime.UtcNow;
var toneOnSamples = new List<bool>();   // for each 20 ms frame fed: was it tone?
while ((DateTime.UtcNow - start).TotalSeconds < seconds)
{
    long due = (long)((DateTime.UtcNow - start).TotalMilliseconds / VoiceWire.FrameMs);
    while (sent <= due)
    {
        bool on = tone && (sent * VoiceWire.FrameMs / 250) % 2 == 0;
        for (int i = 0; i < VoiceWire.FrameSamples; i++)
        {
            short v = on ? (short)(12000 * Math.Sin(2 * Math.PI * 1000 * (sent * VoiceWire.FrameSamples + i) / VoiceWire.Rate)) : (short)0;
            frame[i * 2] = (byte)(v & 0xFF); frame[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        player.Feed(frame);
        toneOnSamples.Add(on);
        sent++;
    }
    Thread.Sleep(5);
}
var elapsed = (DateTime.UtcNow - start).TotalSeconds;
mic.Stop();
player.Stop();

short[] all; lock (heard) all = heard.ToArray();
double got = all.Length / (double)VoiceWire.Rate;
Console.WriteLine($"  the microphone gave {mic.Frames} frames = {got:F2} s of audio in {elapsed:F2} s");
// The first ~200 ms can be missing while the DSP starts; anything past 80% is a live stream.
Check(got >= elapsed * 0.8 && got <= elapsed * 1.1, "the microphone ran at its real rate (50 frames a second), with the player playing silence" + (tone ? " and pulses" : ""));
double peak = all.Length == 0 ? 0 : all.Max(s => Math.Abs((int)s)) / 32768.0;
Console.WriteLine($"  peak {peak:F4}");
Check(all.Length > 0 && all.Any(s => s != 0), "the microphone is not all zeroes (a refusal dressed as a quiet room would be)");

if (tone && all.Length > VoiceWire.Rate)
{
    // Skip the first second: the canceller is learning the room. Then compare the 1 kHz energy in
    // what the microphone gave with that of its neighbours.
    var tail = all.Skip(VoiceWire.Rate).Select(s => s / 32768.0).ToArray();
    double g1k = Goertzel(tail, 1000), g700 = Goertzel(tail, 700), g1300 = Goertzel(tail, 1300);
    Console.WriteLine($"  after the first second: 1000 Hz={g1k:F6}  700={g700:F6}  1300={g1300:F6}");
    Console.WriteLine(g1k > 0.0005 && g1k > 8 * Math.Max(g700, g1300)
        ? "  NOTE  the far end WOULD hear the pulses: the canceller left them in (speakers loud and close, or it is still learning)"
        : "  NOTE  the pulses did not come back through the microphone: cancelled, or never heard (this laptop's Realtek removes its own speakers)");
}

Finish();

void Finish()
{
    Console.WriteLine();
    lock (log) foreach (var l in log) Console.WriteLine("  trace: " + l);
    Console.WriteLine();
    Console.WriteLine($"{pass} of {pass + fail} passed");
    Environment.ExitCode = fail == 0 ? 0 : 1;
}

static double Goertzel(double[] x, double freq)
{
    int n = x.Length; if (n < 256) return 0;
    double k = Math.Round(n * freq / VoiceWire.Rate), w = 2 * Math.PI * k / n, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
    foreach (var v in x) { double s0 = v + c * s1 - s2; s2 = s1; s1 = s0; }
    double p = s1 * s1 + s2 * s2 - c * s1 * s2;
    return Math.Sqrt(Math.Max(p, 0)) / (n / 2.0);
}
