using System.Drawing;
using RemoteSupport.Host;

namespace CodecBench;

/// <summary>
/// Which tiles will not sit still?
///
/// The bench found Desktop Duplication sending something on 84 of 85 polls at a screen the GDI
/// path called completely static. Both cannot be right, and the difference is not a rounding
/// error -- it is 100 KB/s against nothing. This prints a map of the screen showing how often
/// each tile changed, because "something is animating" and "the capture path is broken" look
/// identical in a bandwidth figure and are fixed in different files.
/// </summary>
public static class IdleWatch
{
    public static int Run(int monitor, int quality, Rectangle bounds, bool withScene, bool warmGdi = false)
    {
        Console.WriteLine(withScene
            ? "Watching an idle scene for tiles that keep changing."
            : "Watching the REAL desktop (no scene) for tiles that keep changing.");
        Console.WriteLine();

        // --warmgdi reproduces the bench's own ordering: a GDI pass, its scene torn down, then a
        // fresh scene and the DXGI pass. The symptom only appears in that order, so the watch has
        // to be able to stand in the same place.
        if (warmGdi)
        {
            Console.WriteLine("  running a GDI pass first, the way the bench does...");
            using (var warm = SceneHost.Show(SceneKind.Idle, bounds))
            {
                Thread.Sleep(900);
                using var gdi = new ScreenCapture();
                gdi.Refresh();
                var sw0 = System.Diagnostics.Stopwatch.StartNew();
                bool f = true;
                while (sw0.Elapsed.TotalSeconds < 8) { gdi.CaptureTiles(monitor, quality, f); f = false; }
            }
        }

        var scene = withScene ? SceneHost.Show(SceneKind.Idle, bounds) : null;
        try
        {
            Thread.Sleep(900);
            using var dxgi = new DxgiCapture();
            dxgi.Refresh();
            dxgi.CaptureTiles(monitor, quality, true);       // prime
            Thread.Sleep(300);
            dxgi.CaptureTiles(monitor, quality, true);

            int cols = (bounds.Width + 127) / 128, rows = (bounds.Height + 127) / 128;
            var hits = new int[cols * rows];
            int polls = 0, carried = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 5)
            {
                var tiles = dxgi.CaptureTiles(monitor, quality, false);
                polls++;
                if (tiles.Count > 0) carried++;
                foreach (var (x, y, _, _, _) in tiles)
                {
                    int c = x / 128, rw = y / 128;
                    if (c < cols && rw < rows) hits[rw * cols + c]++;
                }
                Thread.Sleep(1000 / 12);
            }

            Console.WriteLine($"  {carried} of {polls} polls carried tiles over {sw.Elapsed.TotalSeconds:0.0} s");
            Console.WriteLine();
            Console.WriteLine("  Each cell is one 128 px tile. '.' never changed, digits are how often,");
            Console.WriteLine("  '#' means it changed on nearly every poll.");
            Console.WriteLine();
            for (int y = 0; y < rows; y++)
            {
                Console.Write("  ");
                for (int x = 0; x < cols; x++)
                {
                    int n = hits[y * cols + x];
                    Console.Write(n == 0 ? '.' : n >= polls * 0.8 ? '#' : (char)('0' + Math.Min(9, n * 10 / Math.Max(1, polls))));
                }
                Console.WriteLine($"   y={y * 128}");
            }
            Console.WriteLine();
            var busiest = hits.Select((n, i) => (n, i)).Where(t => t.n > 0).OrderByDescending(t => t.n).Take(6);
            foreach (var (n, i) in busiest)
                Console.WriteLine($"  tile at {(i % cols) * 128},{(i / cols) * 128} changed on {n} of {polls} polls");
            if (!hits.Any(h => h > 0)) Console.WriteLine("  Nothing changed at all.");

            // A map says where; only a picture says what. Restless tiles on a screen that is
            // supposed to be covered by the scene almost always mean the scene is not covering it.
            if (hits.Any(h => h > 0))
            {
                var shot = new System.Drawing.Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = System.Drawing.Graphics.FromImage(shot))
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.Clear(System.Drawing.Color.Black);
                    foreach (var (x, y, w, h, jpeg) in dxgi.CaptureTiles(monitor, quality, true))
                    {
                        using var msT = new MemoryStream(jpeg);
                        using var t = System.Drawing.Image.FromStream(msT);
                        g.DrawImage(t, new Rectangle(x, y, w, h));
                    }
                }
                var dir = Path.Combine(Path.GetTempPath(), "codecbench");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "idlewatch.png");
                shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                shot.Dispose();
                Console.WriteLine();
                Console.WriteLine($"  what was on screen: {path}");
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine($"  Desktop Duplication is unavailable: {ex.Message}"); return 1; }
        finally { scene?.Dispose(); }
    }
}
