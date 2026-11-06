using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RemoteSupport.Host;
using RemoteSupport.Media;

namespace CodecBench;

/// <summary>
/// How close does the technician's picture get to the customer's screen, and how fast?
///
/// Reported 2026-10-15 from internal testing on DESKTOP-STD8M9C (Quick Sync with a GPU colour
/// conversion, 1366x768): "very pixelated when connecting and then slowly starts to render
/// correctly, but is never clear." The console's trace showed a 72 KB key frame, a second key frame
/// of 11.8 KB ten milliseconds later, and then nothing but 286-byte frames.
///
///   codecbench --sharp [--quality Q] [--nv12] [--scene idle|colour] [--out DIR]
///
/// It shows a still page, takes it EXACTLY (GDI, no compression) as the reference, then drives the
/// host's own encoder and the console's own decoder: one forced key frame — what a technician sees
/// when they connect to a still screen, and all they ever see if it stays still — then the same
/// still frame fed again and again, scored as it goes. The second half is not what the host does
/// today; it is what "refining a still screen" would buy, measured rather than supposed.
///
/// Scores are PSNR in dB against the exact screen, brightness (Y) and colour (Cb/Cr) apart, because
/// they fail for different reasons: Y is the bit rate, colour is the 4:2:0 the encoder works in.
/// Above about 40 dB on Y, text on a desktop reads as clean; below about 32 it reads as smeared.
/// Every picture is saved, with a 2x crop, so the numbers can be checked by eye.
/// </summary>
public static class Sharp
{
    public static int Run(int monitor, int quality, Rectangle bounds, string outDir, string encoderFilter, SceneKind kind, bool twoKeys = false)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("Sharpness check — the console's picture against the exact screen, from the first frame on.");
        Console.WriteLine($"  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.MachineName}");
        Console.WriteLine();

        using var scene = SceneHost.Show(kind, bounds);
        Thread.Sleep(900);
        if (!scene.Covering) { Console.WriteLine("  the bench scene is not the window on screen; stopping."); return 1; }
        scene.Freeze();
        Thread.Sleep(300);

        using var reference = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(reference)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        reference.Save(Path.Combine(outDir, "sharp-screen.png"), ImageFormat.Png);

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
            Console.WriteLine($"  screen:  {mon.Width}x{mon.Height}, scene {kind}");
            Console.WriteLine($"  encoder: {enc.Name}");
            Console.WriteLine($"  quality {quality} -> {enc.Bitrate / 1000} kbit/s at {fps} fps ({enc.Bitrate / 8 / fps / 1024.0:0.0} KB a frame on average)");
            Console.WriteLine();
            Console.WriteLine("  frames fed      bytes this frame   key   Y dB    colour dB");

            long fedBytes = 0;
            int fed = 0;
            if (twoKeys) Console.WriteLine("  --twokeys: a second forced key frame straight after the first, as a session does when the encryption setting arrives");
            var marks = new HashSet<int> { 1, 2, 3, 6, 12, 18, 23, 24, 25, 26, 27, 36, 48, 50, 51, 60, 72 };
            double firstY = 0, bestY = 0;
            for (int i = 1; i <= 72; i++)
            {
                var t0 = Environment.TickCount64;
                if (!cap.TryAcquireGpuFrame(monitor, true, out var f)) { Thread.Sleep(20); continue; }
                int bytes = 0; bool key = false;
                foreach (var p in enc.Encode(f.Texture, i == 1 || (twoKeys && i == 2)))
                {
                    dec.TryDecode(p.Data, p.KeyFrame);
                    bytes += p.Data.Length; key |= p.KeyFrame;
                }
                fed++; fedBytes += bytes;
                if (marks.Contains(i))
                {
                    using var pic = Video.ToBitmap(dec);
                    var (y, c) = Psnr(reference, pic);
                    if (i == 1) firstY = y;
                    bestY = Math.Max(bestY, y);
                    Console.WriteLine($"  {i,6}          {bytes,9:N0}          {(key ? "key" : "   ")}   {y,5:0.0}   {c,6:0.0}");
                    if (i is 1 or 2 or 6 or 24 or 25 or 48)
                    {
                        pic.Save(Path.Combine(outDir, $"sharp-frame{i:00}.png"), ImageFormat.Png);
                        SaveCrop(pic, Path.Combine(outDir, $"sharp-frame{i:00}-crop.png"));
                    }
                }
                int wait = 1000 / fps - (int)(Environment.TickCount64 - t0);
                if (wait > 0) Thread.Sleep(wait);
            }
            SaveCrop(reference, Path.Combine(outDir, "sharp-screen-crop.png"));

            Console.WriteLine();
            Console.WriteLine($"  {fed} frames, {fedBytes / 1024.0:0.0} KB in all, {fedBytes / 1024.0 / (fed / (double)fps):0.0} KB/s while refining");
            Console.WriteLine($"  first picture {firstY:0.0} dB, best {bestY:0.0} dB (Y)");
            // A blank page encodes perfectly in a few hundred bytes, and that is how the first run of
            // the Desk scene "passed": it had never been drawn. A perfect score is a fault to look at.
            if (firstY >= 99) { Console.WriteLine("  FAIL: the picture IS the screen exactly - the page was never drawn. Open sharp-screen.png."); return 1; }
            Console.WriteLine($"  pictures in {outDir}");
            return 0;
        }
        finally { dec?.Dispose(); enc?.Dispose(); cap?.Dispose(); }
    }

    /// <summary>A 2x nearest-neighbour crop of the top-left of the page, where the text starts, to look at.</summary>
    internal static void SaveCrop(Bitmap src, string path, Rectangle? at = null)
    {
        var area = at ?? new Rectangle(0, 0, Math.Min(480, src.Width), Math.Min(270, src.Height));
        using var crop = new Bitmap(area.Width * 2, area.Height * 2, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, crop.Width, crop.Height), area, GraphicsUnit.Pixel);
        }
        crop.Save(path, ImageFormat.Png);
    }

    /// <summary>PSNR of brightness and of colour (Cb and Cr together), BT.601, over the overlap.</summary>
    internal static unsafe (double y, double colour) Psnr(Bitmap a, Bitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        var ra = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var rb = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            double sy = 0, sc = 0; long n = 0;
            for (int yy = 0; yy < h; yy++)
            {
                byte* pa = (byte*)ra.Scan0 + (long)yy * ra.Stride;
                byte* pb = (byte*)rb.Scan0 + (long)yy * rb.Stride;
                for (int x = 0; x < w; x++, pa += 4, pb += 4)
                {
                    var (ya, ua, va) = Yuv(pa[2], pa[1], pa[0]);
                    var (yb, ub, vb) = Yuv(pb[2], pb[1], pb[0]);
                    sy += (ya - yb) * (ya - yb);
                    sc += ((ua - ub) * (ua - ub) + (va - vb) * (va - vb)) / 2;
                    n++;
                }
            }
            static double P(double mse) => mse <= 1e-9 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
            return (P(sy / n), P(sc / n));
        }
        finally { a.UnlockBits(ra); b.UnlockBits(rb); }
    }

    private static (double y, double u, double v) Yuv(int r, int g, int b) =>
        (0.299 * r + 0.587 * g + 0.114 * b,
         -0.168736 * r - 0.331264 * g + 0.5 * b,
         0.5 * r - 0.418688 * g - 0.081312 * b);
}
