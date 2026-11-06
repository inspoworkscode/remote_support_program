using System.Diagnostics;
using RemoteSupport.Media;

// ---------------------------------------------------------------------------------------------
// recordcheck - roadmap 1.6: does a session recording come out as a video that PLAYS, the right
// way up, the right size and the right length?
//
// It writes recordings through the real ScreenRecorder and reads them back through the real
// Mp4Reader - Windows' own decoder, not ours - so "a file was written" is never mistaken for
// "a recording exists". The picture is red above and blue below, so a recording stored upside
// down (Media Foundation's default for RGB) cannot pass.
//
// It also measures what a real session costs: a full-HD recording's encode time per frame and
// its size per minute, which decide whether 8 frames a second is affordable.
//
//   dotnet run -c Debug --project tools/recordcheck/recordcheck.csproj
// ---------------------------------------------------------------------------------------------

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

Console.WriteLine("recordcheck - session recordings (1.6)");
Console.WriteLine();
var dir = Path.Combine(Path.GetTempPath(), "rs-recordcheck-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(dir);

try
{
    // ---- a small one, with a known picture ----
    Console.WriteLine("== 641x361, 3 seconds at 8 frames a second ==");
    var small = Path.Combine(dir, "small.mp4");
    const int Fps = 8, Seconds = 3;
    var frame = TimeSpan.FromSeconds(1.0 / Fps);
    using (var rec = ScreenRecorder.Create(small, 641, 361, Fps))
    {
        Check("an odd size is rounded down to even, as H.264 needs", rec.Width == 640 && rec.Height == 360, $"{rec.Width}x{rec.Height}");
        using var bmp = new Bitmap(rec.Width, rec.Height);
        for (int i = 0; i < Fps * Seconds; i++)
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Blue);
                g.FillRectangle(Brushes.Red, 0, 0, bmp.Width, bmp.Height / 2);
                // Something that moves, so the encoder has real work and the frames differ.
                g.FillRectangle(Brushes.White, i * 20 % (bmp.Width - 40), bmp.Height / 2 - 20, 40, 40);
            }
            rec.AddFrame(bmp, frame * i, frame);
        }
        rec.Finish();
        Check("every frame was taken", rec.Frames == Fps * Seconds, rec.Frames.ToString());
    }
    var info = Mp4Reader.Read(small);
    Check("it reads back as a video Windows can decode", info.Frames > 0, $"{info.Frames} frame(s)");
    Check("...at the size it was written", info.Width == 640 && info.Height == 360, $"{info.Width}x{info.Height}");
    Check("...with every frame", info.Frames == Fps * Seconds, $"{info.Frames} of {Fps * Seconds}");
    Check("...lasting as long as it should", Math.Abs(info.Length.TotalSeconds - (Seconds - 1.0 / Fps)) < 0.2, $"last frame at {info.Length.TotalSeconds:0.00} s");
    if (info.First != null)
    {
        var top = info.First.GetPixel(320, 60); var bottom = info.First.GetPixel(320, 300);
        Check("...the right way up: red at the top", top.R > 180 && top.B < 80, $"top #{top.R:X2}{top.G:X2}{top.B:X2}");
        Check("...and blue at the bottom", bottom.B > 180 && bottom.R < 80, $"bottom #{bottom.R:X2}{bottom.G:X2}{bottom.B:X2}");
    }
    else Check("the first frame could be read", false);

    // ---- full HD: what a real session costs ----
    Console.WriteLine();
    Console.WriteLine("== 1920x1080, 10 seconds - the cost of a real session ==");
    var big = Path.Combine(dir, "big.mp4");
    var sw = Stopwatch.StartNew();
    using (var rec = ScreenRecorder.Create(big, 1920, 1080, Fps))
    using (var bmp = new Bitmap(1920, 1080))
    {
        // A desktop-like picture: mostly still, some text-sized detail, a window that moves.
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(0x1F, 0x4E, 0x79)); }
        var rng = new Random(7);
        using (var g = Graphics.FromImage(bmp))
            for (int k = 0; k < 4000; k++) g.FillRectangle(Brushes.White, rng.Next(1900), rng.Next(1060), 6, 2);
        for (int i = 0; i < Fps * 10; i++)
        {
            using (var g = Graphics.FromImage(bmp)) g.FillRectangle(Brushes.Gainsboro, 200 + i * 8, 300, 600, 400);
            rec.AddFrame(bmp, frame * i, frame);
        }
        rec.Finish();
    }
    sw.Stop();
    var perFrame = sw.Elapsed.TotalMilliseconds / (Fps * 10);
    var mbPerMinute = new FileInfo(big).Length / 1048576.0 * 6;
    Check("encoding keeps up with 8 frames a second, with room to spare", perFrame < 125.0 / 2, $"{perFrame:0.0} ms a frame against a 125 ms budget");
    Check("a minute of it is a reasonable size", mbPerMinute < 20, $"{mbPerMinute:0.0} MB a minute");
    var bigInfo = Mp4Reader.Read(big);
    Check("and it plays back at full size", bigInfo.Width == 1920 && bigInfo.Height == 1080 && bigInfo.Frames == Fps * 10, $"{bigInfo.Width}x{bigInfo.Height}, {bigInfo.Frames} frames");
}
catch (Exception ex) { fail++; Console.WriteLine("  FAIL  threw: " + ex.Message); }
finally { try { Directory.Delete(dir, true); } catch { } }

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;
