using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteSupport.Host;
using RemoteSupport.Shared;

namespace CodecBench;

/// <summary>
/// Stage 2.4: the customer's real pointer, rendered and put on the wire.
///
///   codecbench --cursor
///
/// It does not use whatever cursor happens to be under the mouse — that would test a different
/// thing every run. It loads the standard system cursors by name, which covers both formats that
/// matter: the arrow and the I-beam are MONOCHROME (a mask twice as tall as the pointer, AND over
/// XOR) and the busy ones are usually COLOUR with a real alpha channel. Handling only the colour
/// case would work on every hand and I-beam and fail on the one pointer a session spends its life
/// looking at.
///
/// ---- What a broken pointer looks like, which is what these checks are aimed at ---------------
///
/// There are exactly two ways to get this wrong and both render without error:
///
///   * every pixel transparent — an invisible pointer, which looks like the feature never shipped
///   * every pixel opaque — a rectangle of black or white dragged around the technician's screen
///
/// So every shape is checked for having BOTH transparent and opaque pixels. A check that only
/// asserted "it produced a PNG" would pass on both failures.
/// </summary>
public static class CursorCheck
{
    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("Cursor — the customer's real pointer, rendered and round-tripped.");
        Console.WriteLine();

        int pass = 0, fail = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
            else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
        }

        var standard = new (string name, int id)[]
        {
            ("arrow", 32512), ("I-beam", 32513), ("wait", 32514), ("cross", 32515),
            ("hand", 32649), ("size all", 32646), ("busy arrow", 32650),
        };

        int rendered = 0;
        foreach (var (name, id) in standard)
        {
            IntPtr h = LoadCursor(IntPtr.Zero, id);
            if (h == IntPtr.Zero) { Console.WriteLine($"  ..    {name}: not on this machine, skipped"); continue; }

            Console.WriteLine($"  ..    {name}: {CursorShape.Describe(h.ToInt64())}");
            var img = CursorShape.TryRender(h.ToInt64());
            if (img == null) { Check($"{name}: renders", false, "TryRender returned nothing"); continue; }
            rendered++;

            // The two failure modes, both of which render perfectly happily.
            var (opaque, clear, total, maxA) = Census(img.Png);
            Check($"{name}: has pixels you can see", opaque > 0, $"{opaque} of {total} opaque, strongest alpha {maxA}");
            Check($"{name}: has pixels you can see THROUGH", clear > 0, $"{clear} of {total} transparent");
            Check($"{name}: the hot spot is inside the bitmap",
                  img.HotX >= 0 && img.HotY >= 0 && img.HotX < img.Width && img.HotY < img.Height,
                  $"hot {img.HotX},{img.HotY} in {img.Width}x{img.Height}");

            // The wire, against the REAL encoder rather than a copy of it.
            var frame = HostEngine.EncodeCursor(img);
            Check($"{name}: survives the wire",
                  RoundTrips(frame, img, out string why), why);

            File.WriteAllBytes(Path.Combine(outDir, $"cursor-{name.Replace(' ', '-')}.png"), img.Png);
        }

        Check("the standard cursors were found at all", rendered >= 4, $"{rendered} rendered");

        // The one under the mouse right now, which is the path the host actually uses.
        if (CursorShape.TryPoll(out long liveId, out bool visible, out var at))
        {
            Check("the live pointer can be polled", liveId != 0, $"id {liveId:X}, visible {visible}, at {at.X},{at.Y}");
            var live = CursorShape.TryRender(liveId);
            Check("and the live pointer renders", live != null,
                  live != null ? $"{live.Width}x{live.Height}" : "TryRender returned nothing");
            if (live != null) File.WriteAllBytes(Path.Combine(outDir, "cursor-live.png"), live.Png);
        }
        else Check("the live pointer can be polled", false, "GetCursorInfo refused");

        Console.WriteLine();
        Console.WriteLine($"  the pointers are written to {outDir} — OPEN THEM.");
        Console.WriteLine("  Pixel counts cannot tell you an arrow is an arrow, and this is the one");
        Console.WriteLine("  check that would catch a mask applied upside down.");
        Console.WriteLine();
        Console.WriteLine($"{pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Opaque, fully transparent, and total pixels in a PNG.</summary>
    private static (int opaque, int clear, int total, int maxAlpha) Census(byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        int opaque = 0, clear = 0, maxA = 0;
        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width; x++)
        {
            int a = bmp.GetPixel(x, y).A;
            if (a > maxA) maxA = a;
            if (a > 200) opaque++;
            else if (a < 40) clear++;
        }
        return (opaque, clear, bmp.Width * bmp.Height, maxA);
    }

    /// <summary>
    /// Parses the frame the way the console does and checks everything came back. Written out
    /// here rather than calling the console's parser on purpose: two independent readings of the
    /// same layout is what catches a header whose size nobody recalculated.
    /// </summary>
    private static bool RoundTrips(byte[] frame, CursorShape.Image original, out string why)
    {
        why = "";
        if (frame.Length < 25) { why = $"frame is only {frame.Length} bytes"; return false; }
        if (frame[0] != BinKind.Cursor) { why = $"kind byte is {frame[0]}, expected {BinKind.Cursor}"; return false; }
        long id = BitConverter.ToInt64(frame, 1);
        int w = BitConverter.ToInt32(frame, 9), h = BitConverter.ToInt32(frame, 13);
        int hx = BitConverter.ToInt32(frame, 17), hy = BitConverter.ToInt32(frame, 21);
        if (id != original.Id) { why = "the id changed"; return false; }
        if (w != original.Width || h != original.Height) { why = $"size came back {w}x{h}, sent {original.Width}x{original.Height}"; return false; }
        if (hx != original.HotX || hy != original.HotY) { why = "the hot spot changed"; return false; }
        try
        {
            using var ms = new MemoryStream(frame, 25, frame.Length - 25);
            using var bmp = new Bitmap(ms);
            if (bmp.Width != w || bmp.Height != h) { why = $"the PNG is {bmp.Width}x{bmp.Height} but the header says {w}x{h}"; return false; }
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb) { why = $"the PNG lost its alpha channel ({bmp.PixelFormat})"; return false; }
        }
        catch (Exception ex) { why = "the PNG would not decode: " + ex.Message; return false; }
        why = $"{w}x{h}, hot {hx},{hy}, {frame.Length} B";
        return true;
    }

    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
}
