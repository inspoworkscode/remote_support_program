using System.Drawing;
using System.Drawing.Imaging;
using RemoteSupport.Host;
using RemoteSupport.Media;

namespace CodecBench;

/// <summary>
/// The picture a technician sees over twenty seconds of a real session, through the host's own H.264
/// loop — run twice, as the host was before 2026-10-15 and as it is now (HANDOVER §142).
///
///   codecbench --hostloop [--quality Q] [--nv12] [--out DIR]
///
/// The session: connect (a forced key frame, then a second one straight after, as the encryption
/// setting arriving causes), a text cursor blinking at 530 ms the whole time, and a window opening at
/// 8 s. Each pass does what HostEngine.SendVideo does — take a frame if the screen changed, and in the
/// new host, encode the still screen again while <see cref="StillRefiner"/> wants it — with the same
/// changed-area reading (<see cref="DxgiCapture.LastVideoChange"/>) and the same rule object. Scored
/// against the exact screen at each moment, as --sharp is.
///
/// "Clean" is within 1 dB of the best the new host reaches on the page — converged, whatever the budget.
/// PASS means: 3 s after connecting the new host is at least 4 dB sharper than the old; from 3 s after
/// the window opens it is clean and stays clean; the blinking cursor alone does not keep it sharpening
/// (it stops one window after the last large change); sharpening is not counted as change; and the old
/// host, run first, is NOT clean throughout — or the check is not measuring anything.
///
/// On a tight budget (quality 35, like the squeezed second key frame DESKTOP-STD8M9C sent) two seconds
/// of sharpening get most of the way and the cursor's own frames do the rest: clean by about 8 s, where
/// the old host never was. The line "clean after connecting by" says which, every run.
/// </summary>
public static class HostLoop
{
    private const int Fps = 12;
    private static readonly double[] Marks = { 0.2, 1, 2, 3, 5, 7.9, 8.3, 9, 10, 11, 14, 17, 19.9 };
    /// <summary>"Clean" is within this many dB of the best the new host reaches on the page: converged, on any budget.</summary>
    private const double Within = 1.0;
    /// <summary>How much sharper than the old host the new one must be 3 s after connecting. Measured 2026-10-15
    /// on Quick Sync with the colour conversion: 4.9 dB at quality 55, 6.6 at 35.</summary>
    private const double Ahead = 4.0;

    public static int Run(int monitor, int quality, Rectangle bounds, string outDir, string encoderFilter)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("Host loop — twenty seconds of a session through the host's H.264 path, old host then new.");
        Console.WriteLine($"  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.MachineName}");
        Console.WriteLine();

        using var scene = SceneHost.Show(SceneKind.Desk, bounds);
        Thread.Sleep(900);
        if (!scene.Covering) { Console.WriteLine("  the bench scene is not the window on screen; stopping."); return 1; }

        var before = Pass("BEFORE: key frame every 2 s, no sharpening", 2, false, monitor, quality, bounds, outDir, encoderFilter, scene);
        Console.WriteLine();
        var after = Pass("AFTER:  key frame every 60 s, still screen sharpened", 60, true, monitor, quality, bounds, outDir, encoderFilter, scene);
        if (before == null || after == null) return 1;

        Console.WriteLine();
        Console.WriteLine("  seconds   before dB   after dB");
        for (int i = 0; i < Marks.Length; i++)
            Console.WriteLine($"  {Marks[i],6:0.0}    {before.Scores[i],7:0.0}    {after.Scores[i],7:0.0}");
        Console.WriteLine();
        Console.WriteLine($"  bytes:            before {before.Bytes / 1024.0,7:0.0} KB   after {after.Bytes / 1024.0,7:0.0} KB");
        Console.WriteLine($"  frames encoded:   before {before.Frames,5}        after {after.Frames,5} ({after.Refined} of them sharpening)");
        Console.WriteLine($"  key frames:       before {before.Keys,5}        after {after.Keys,5}");
        Console.WriteLine($"  passes the link controller saw as a change: before {before.SeenAsChange}, after {after.SeenAsChange}");
        Console.WriteLine($"  last sharpening frame at {after.LastRefineAt:0.0} s");
        Console.WriteLine($"  share of the screen each change covered (new host): {string.Join(" ", after.Shares.Select(x => x.ToString("0.####")))}");
        Console.WriteLine();

        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fail++; }
        double Clean = after.Scores.Max() - Within;
        Console.WriteLine($"  clean means {Clean:0.0} dB or better (the best the new host reached, less {Within})");
        bool Good(Outcome r, double from) => Marks.Select((m, i) => (m, i)).Where(x => x.m >= from && (from >= 8 || x.m < 7.95)).All(x => r.Scores[x.i] >= Clean);
        int at3 = Array.IndexOf(Marks, 3.0);
        var cleanAt = Marks.Select((m, i) => (m, i)).FirstOrDefault(x => x.m < 7.95 && after.Scores[x.i] >= Clean);
        Console.WriteLine($"  new host clean after connecting by {(cleanAt.m > 0 ? $"{cleanAt.m:0.0} s" : "never, before the window opened")}");
        Check(after.Scores[at3] >= before.Scores[at3] + Ahead,
              $"new host: 3 s after connecting, at least {Ahead} dB sharper than the old host ({after.Scores[at3]:0.0} against {before.Scores[at3]:0.0})");
        Check(Good(after, 11), "new host: clean from 3 s after the window opens to the end");
        Check(after.LastRefineAt < 8 + StillRefiner.Window.TotalSeconds + 0.5,
              "new host: the blinking cursor alone does not keep the sharpening running");
        Check(after.SeenAsChange <= before.SeenAsChange, "new host: sharpening frames are not counted by the link controller as changes");
        Check(!(Good(before, 3) && Good(before, 11)), "old host FAILS the same test — the check can see the fault");
        Console.WriteLine($"  pictures in {outDir}");
        return fail == 0 ? 0 : 1;
    }

    private sealed class Outcome
    {
        public double[] Scores = new double[Marks.Length];
        public long Bytes; public int Frames, Refined, Keys, SeenAsChange;
        public double LastRefineAt;
        public List<double> Shares = new();
    }

    private static Outcome? Pass(string title, int keySeconds, bool sharpen, int monitor, int quality, Rectangle bounds,
                             string outDir, string encoderFilter, SceneHost scene)
    {
        Console.WriteLine($"  {title}");
        scene.Dialog(false); scene.Blink(false);
        Thread.Sleep(400);
        H264Encoder.KeyFrameSeconds = keySeconds;
        DxgiCapture? cap = null; H264Encoder? enc = null; H264Decoder? dec = null;
        try
        {
            cap = new DxgiCapture(); cap.Refresh();
            var mon = cap.Monitors.First(m => m.Index == monitor);
            if (!cap.TryAcquireGpuFrame(monitor, true, out var first)) { Console.WriteLine("no frame: " + cap.LastError); return null; }
            enc = new H264Encoder(first.Device, mon.Width, mon.Height, Fps, quality, encoderFilter);
            dec = new H264Decoder(enc.Width, enc.Height) { Matrix709 = Nv12.Is709(enc.Width, enc.Height) };
            Console.WriteLine($"    {enc.Name}, {enc.Bitrate / 1000} kbit/s");
            var refiner = new StillRefiner();
            var run = new Outcome();
            scene.Blink(true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int pass = 0, mark = 0; bool dialogShown = false;
            while (mark < Marks.Length)
            {
                long t0 = sw.ElapsedMilliseconds;
                double now = t0 / 1000.0;
                if (!dialogShown && now >= 8) { scene.Dialog(true); dialogShown = true; }
                pass++;
                bool forceKey = pass <= 2;   // the connect, and the second one the encryption setting causes
                bool refining = false;
                // HostEngine.SendVideo, line for line in what it decides.
                if (cap.TryAcquireGpuFrame(monitor, forceKey, out var f))
                {
                    if (sharpen) refiner.Saw(cap.LastVideoChange, forceKey, DateTime.UtcNow);
                    if (!forceKey) { run.Shares.Add(cap.LastVideoChange); if (cap.LastVideoChange >= 1 && run.Shares.Count < 4) Console.WriteLine("    share 1 because: " + cap.ShareWhy); }
                }
                else if (!forceKey && sharpen && refiner.Wanted(DateTime.UtcNow) && cap.TryAcquireGpuFrame(monitor, true, out f))
                    refining = true;
                else f = default;

                if (f.Texture != IntPtr.Zero)
                {
                    foreach (var p in enc.Encode(f.Texture, forceKey))
                    {
                        dec.TryDecode(p.Data, p.KeyFrame);
                        run.Bytes += p.Data.Length; run.Frames++;
                        if (p.KeyFrame) run.Keys++;
                    }
                    if (refining) { run.Refined++; run.LastRefineAt = now; }
                    else run.SeenAsChange++;
                }

                while (mark < Marks.Length && sw.Elapsed.TotalSeconds >= Marks[mark])
                {
                    using var screen = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                    using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    using var pic = Video.ToBitmap(dec);
                    using (var masked = (Bitmap)pic.Clone())
                    {
                        var caret = scene.CaretArea;
                        using (var g = Graphics.FromImage(screen)) g.FillRectangle(Brushes.White, caret);
                        using (var g = Graphics.FromImage(masked)) g.FillRectangle(Brushes.White, caret);
                        run.Scores[mark] = Sharp.Psnr(screen, masked).y;
                    }
                    if (Marks[mark] is 0.2 or 3 or 8.3 or 11)
                    {
                        string tag = $"{(sharpen ? "after" : "before")}-{Marks[mark]:0.0}s";
                        pic.Save(Path.Combine(outDir, $"{tag}.png"), ImageFormat.Png);
                        Sharp.SaveCrop(pic, Path.Combine(outDir, $"{tag}-crop.png"), DialogCrop(bounds));
                    }
                    mark++;
                }
                int wait = 1000 / Fps - (int)(sw.ElapsedMilliseconds - t0);
                if (wait > 0) Thread.Sleep(wait);
            }
            scene.Blink(false);
            return run;
        }
        finally { dec?.Dispose(); enc?.Dispose(); cap?.Dispose(); H264Encoder.KeyFrameSeconds = 60; }
    }

    /// <summary>The window that opens, and the list behind it.</summary>
    private static Rectangle DialogCrop(Rectangle bounds) => new(380, 220, Math.Min(600, bounds.Width - 380), Math.Min(380, bounds.Height - 220));
}
