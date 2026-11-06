using System.Drawing;
using System.Drawing.Imaging;
using RemoteSupport.Host;
using RemoteSupport.Media;

namespace CodecBench;

/// <summary>
/// Does the LAST frame of a change reach the technician, when the screen goes still straight after?
///
/// Reported 2026-10-06 from internal testing: on one machine of three, a program opened or closed
/// through a session did not show on the console until Refresh was pressed. That is the shape of a
/// window's open or close animation — a burst of frames, then a still screen — and on a still
/// screen Desktop Duplication hands over nothing more, so the host never calls the encoder again.
/// Whatever the encoder is still holding, or refused, at that moment is never sent.
///
///   codecbench --tail [--encoder "Quick Sync" | --encoder "H264 Encoder MFT"]
///
/// It drives the capture and the encoder exactly as HostEngine's loop does (acquire, encode only
/// when the acquire says something changed, 12 passes a second), decodes every packet the way the
/// console does, and compares the last decoded picture with the screen. Then it does what Refresh
/// does — a forced key frame — and compares again. If the first comparison fails and the second
/// passes, that is the reported fault, reproduced.
/// </summary>
public static class Tail
{
    /// <param name="hold">Packets held back before decoding, to make THIS machine's encoder behave
    /// like one with that many frames of pipeline. It proves the check can fail: with 1, the run
    /// must print FAIL before Refresh and PASS after, or the check is not measuring anything.</param>
    public static int Run(int monitor, int quality, Rectangle bounds, string outDir, string encoderFilter, int hold = 0)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("Tail check — does the last frame of a burst reach the viewer when the screen then goes still?");
        Console.WriteLine($"  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.MachineName}");
        Console.WriteLine();

        using var scene = SceneHost.Show(SceneKind.Video, bounds);
        Thread.Sleep(700);
        if (!scene.Covering) Console.WriteLine("  WARNING: the bench scene is not the window on screen.");

        DxgiCapture? cap = null; H264Encoder? enc = null; H264Decoder? dec = null;
        try
        {
            cap = new DxgiCapture(); cap.Refresh();
            var mon = cap.Monitors.First(m => m.Index == monitor);
            if (!cap.TryAcquireGpuFrame(monitor, true, out var first)) { Console.WriteLine("no frame: " + cap.LastError); return 1; }
            const int fps = 12;
            enc = new H264Encoder(first.Device, mon.Width, mon.Height, fps, quality, encoderFilter);
            dec = new H264Decoder(enc.Width, enc.Height);
            dec.Matrix709 = Nv12.Is709(enc.Width, enc.Height);
            Console.WriteLine($"  encoder: {enc.Name}");
            Console.WriteLine($"  decoder: {dec.Name}");

            int fed = 0, packetsOut = 0, fedAfterFreeze = 0, packetsAfterFreeze = 0;
            bool frozen = false, full = true;
            var held = new Queue<VideoPacket>();
            if (hold > 0) Console.WriteLine($"  SIMULATING an encoder {hold} frame(s) behind — this run is meant to FAIL before Refresh");
            void Pass()
            {
                var t0 = Environment.TickCount64;
                if (cap!.TryAcquireGpuFrame(monitor, full, out var f))
                {
                    fed++; if (frozen) fedAfterFreeze++;
                    foreach (var p in enc!.Encode(f.Texture, full))
                    {
                        packetsOut++; if (frozen) packetsAfterFreeze++;
                        held.Enqueue(p);
                        while (held.Count > hold) { var q = held.Dequeue(); dec!.TryDecode(q.Data, q.KeyFrame); }
                    }
                    full = false;
                }
                int wait = 1000 / fps - (int)(Environment.TickCount64 - t0);
                if (wait > 0) Thread.Sleep(wait);
            }

            // The "window opening": 1.5 s of change.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 1.5) Pass();
            int droppedAtFreeze = enc.Dropped;
            // The program appearing: one large change, then nothing. A frozen moving field would
            // not do — its last two frames are so alike that a picture one frame late still
            // "matches", which is how the first version of this check passed a simulated fault.
            scene.SetKind(SceneKind.Colour, bounds);
            frozen = true;
            // Then the screen sits still, as it does once a program has finished opening.
            sw.Restart();
            while (sw.Elapsed.TotalSeconds < 3.0) Pass();

            Console.WriteLine($"  during the burst: {fed - fedAfterFreeze} frames fed, {packetsOut - packetsAfterFreeze} packets out, {droppedAtFreeze} refused as busy");
            Console.WriteLine($"  after it stopped: {fedAfterFreeze} frames fed, {packetsAfterFreeze} packets out, {enc.Dropped - droppedAtFreeze} refused as busy");
            Console.WriteLine($"  {enc.Diagnostics}");

            using var reference = Video.ComposeWithGdi(monitor, mon.Width, mon.Height, 95)!;
            reference.Save(Path.Combine(outDir, "tail-screen.png"), ImageFormat.Png);
            using (var stale = Video.ToBitmap(dec)) { stale.Save(Path.Combine(outDir, "tail-console-before-refresh.png"), ImageFormat.Png); }
            double before;
            using (var b = Video.ToBitmap(dec)) before = Video.MeanAbsoluteDifference(reference, b);

            // What the Refresh button does: a forced key frame of what is on the card now. (With
            // --hold, the key frame overtakes what was held, as a rebuilt encoder's would.)
            held.Clear();
            for (int i = 0; i < 10; i++)
            {
                cap.TryAcquireGpuFrame(monitor, true, out var f);
                bool key = false;
                foreach (var p in enc.Encode(f.Texture, true)) { dec.TryDecode(p.Data, p.KeyFrame); key |= p.KeyFrame; }
                if (key) break;
                Thread.Sleep(80);
            }
            double after;
            using (var a = Video.ToBitmap(dec)) { a.Save(Path.Combine(outDir, "tail-console-after-refresh.png"), ImageFormat.Png); after = Video.MeanAbsoluteDifference(reference, a); }

            Console.WriteLine();
            Console.WriteLine($"  console picture vs screen, still, BEFORE Refresh: {before:0.0} of 255  {(before <= 12 ? "PASS" : "FAIL — stale")}");
            Console.WriteLine($"  console picture vs screen, AFTER Refresh:          {after:0.0} of 255  {(after <= 12 ? "PASS" : "FAIL")}");
            Console.WriteLine($"  pictures in {outDir}");
            return before <= 12 ? 0 : 1;
        }
        finally { dec?.Dispose(); enc?.Dispose(); cap?.Dispose(); }
    }
}
