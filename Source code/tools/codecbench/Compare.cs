using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RemoteSupport.Host;

namespace CodecBench;

/// <summary>
/// Is the fast picture the RIGHT picture?
///
/// Every number the bench prints is worthless if Desktop Duplication is handing back the wrong
/// pixels, and a capture path assembled out of raw vtable slots can be wrong in ways that do not
/// crash: a stride misread, a channel order swapped, a rectangle off by a tile. So this takes a
/// full frame through each source, decodes the tiles the way ScreenView decodes them, and compares
/// the two composed canvases pixel by pixel.
///
/// It does not demand they be identical. Both sides are JPEG at the same quality, and the two
/// capture routes do not produce bit-identical input, so a few units of mean difference is the
/// expected answer. What it rules out is the interesting failure: a picture that is shifted,
/// swapped, torn or blank reads as tens of units, and the PNGs are written out so the eye can
/// settle any argument the number starts.
/// </summary>
public static class Compare
{
    public static int Run(int monitor, int quality, Rectangle bounds, string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("Comparing the two capture paths on the same screen.");
        Console.WriteLine();

        // The video scene, because it repaints every pixel: after the moving phase below there is
        // no tile left anywhere holding the GDI-primed pixels Desktop Duplication started with.
        // A still scene would compare GDI against its own priming copy and pass perfectly while
        // proving nothing -- which is exactly what the first version of this did.
        using var scene = SceneHost.Show(SceneKind.Video, bounds);
        Thread.Sleep(500);

        Bitmap? gdiCanvas = null, dxgiCanvas = null;
        DxgiCapture? dxgi = null;
        try
        {
            try { dxgi = new DxgiCapture(); dxgi.Refresh(); }
            catch (Exception ex)
            {
                Console.WriteLine($"Desktop Duplication is unavailable on this machine: {ex.Message}");
                Console.WriteLine("Nothing to compare. The GDI path is what would run here.");
                return 0;
            }

            // Moving phase: pull real frames through the duplication for a second and a half.
            Console.WriteLine("  letting real frames through Desktop Duplication...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int real = 0;
            while (sw.Elapsed.TotalSeconds < 1.5)
            {
                if (dxgi.CaptureTiles(monitor, quality, false).Count > 0) real++;
                Thread.Sleep(15);
            }
            Console.WriteLine($"  {real} frames carried pixels");
            if (real == 0)
            {
                Console.WriteLine("FAIL - no frame ever came through Desktop Duplication, so there is nothing to compare.");
                return 1;
            }

            // Still phase: both sources now photograph the same motionless screen.
            scene.Freeze();
            Thread.Sleep(600);

            dxgiCanvas = Compose(dxgi, monitor, quality, bounds);
            using (var gdi = new ScreenCapture())
            {
                gdi.Refresh();
                gdiCanvas = Compose(gdi, monitor, quality, bounds);
            }

            string a = Path.Combine(outDir, "gdi.png"), b = Path.Combine(outDir, "dxgi.png");
            gdiCanvas.Save(a, ImageFormat.Png);
            dxgiCanvas.Save(b, ImageFormat.Png);

            var (mean, worst, differing) = Difference(gdiCanvas, dxgiCanvas);
            Console.WriteLine($"  canvas        {gdiCanvas.Width}x{gdiCanvas.Height}");
            Console.WriteLine($"  mean difference   {mean:0.00} of 255 per channel");
            Console.WriteLine($"  worst pixel       {worst} of 255");
            Console.WriteLine($"  pixels off by >16 {differing:0.00}%");
            Console.WriteLine();
            Console.WriteLine($"  {a}");
            Console.WriteLine($"  {b}");
            Console.WriteLine();

            // The threshold is not a guess: JPEG at quality 55 on the same source image differs
            // from itself by a couple of units, and two different capture routes by a little more.
            // Anything past this is a structural fault, not compression.
            if (mean < 6.0 && differing < 2.0)
            {
                Console.WriteLine("PASS - the two paths agree. The difference is JPEG, not geometry.");
                return 0;
            }
            Console.WriteLine("FAIL - the two paths disagree by more than compression explains.");
            Console.WriteLine("Open both PNGs. A shift means a stride or rectangle fault; wrong colours");
            Console.WriteLine("mean a channel order fault; a blank or torn half means the readback.");
            return 1;
        }
        finally { gdiCanvas?.Dispose(); dxgiCanvas?.Dispose(); dxgi?.Dispose(); }
    }

    /// <summary>Takes one forced-full frame and draws its tiles onto a canvas, exactly as ScreenView does.</summary>
    private static Bitmap Compose(IScreenSource src, int monitor, int quality, Rectangle bounds)
    {
        var tiles = src.CaptureTiles(monitor, quality, true);

        var canvas = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(canvas);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.Black);
        foreach (var (x, y, w, h, jpeg) in tiles)
        {
            using var ms = new MemoryStream(jpeg);
            using var tile = Image.FromStream(ms);
            g.DrawImage(tile, new Rectangle(x, y, w, h));
        }
        Console.WriteLine($"  {src.Name}: {tiles.Count} tiles");
        return canvas;
    }

    private static unsafe (double mean, int worst, double differing) Difference(Bitmap a, Bitmap b)
    {
        var ra = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var db = b.LockBits(ra, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            long total = 0, off = 0;
            int worst = 0;
            long samples = 0;
            for (int y = 0; y < a.Height; y++)
            {
                byte* pa = (byte*)da.Scan0 + (long)y * da.Stride;
                byte* pb = (byte*)db.Scan0 + (long)y * db.Stride;
                for (int x = 0; x < a.Width * 3; x++)
                {
                    int d = Math.Abs(pa[x] - pb[x]);
                    total += d; samples++;
                    if (d > worst) worst = d;
                    if (d > 16) off++;
                }
            }
            return ((double)total / samples, worst, 100.0 * off / samples);
        }
        finally { a.UnlockBits(da); b.UnlockBits(db); }
    }
}
