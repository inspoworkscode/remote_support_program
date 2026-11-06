using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// The fallback capture path: GDI BitBlt with tile-based change detection, JPEG per changed tile.
/// Slow and CPU-hungry, and blind to some fullscreen and protected content — but it works in
/// every session and driver state, which <see cref="DxgiCapture"/> does not. See
/// <see cref="IScreenSource"/> for why both exist.
/// </summary>
public sealed class ScreenCapture : IScreenSource, IDisposable
{
    public string Name => "GDI BitBlt";
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public CaptureTiming LastTiming { get; private set; }
    private readonly Dictionary<int, TileState> _states = new();
    private const int Tile = 128;

    private sealed class TileState { public uint[]? Hashes; public int Cols, Rows; }

    public void Refresh() => Monitors = EnumerateMonitors();

    /// <summary>
    /// Nothing to release. This path holds no duplication and no graphics device — it asks Windows
    /// for the pixels each time, which is exactly why it is the fallback and why it is slow. The
    /// per-tile hashes stay: they cost a few KB, and the capture loop demands a complete frame on
    /// resubscribe anyway.
    /// </summary>
    public void ReleaseIdle() { }

    /// <summary>
    /// The one place monitors are enumerated. Both capture sources call it, because the index in a
    /// MonitorsMsg has to mean the same screen whichever of them is running — a console that
    /// subscribed to monitor 1 and a host that switched sources underneath it must not disagree.
    /// </summary>
    public static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        int idx = 0;
        Native.MonitorEnumProc cb = (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
            {
                list.Add(new MonitorInfo
                {
                    Index = idx, X = mi.rcMonitor.Left, Y = mi.rcMonitor.Top,
                    Width = mi.rcMonitor.Right - mi.rcMonitor.Left, Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    Name = mi.szDevice, Primary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0
                });
                idx++;
            }
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        if (list.Count == 0)
            list.Add(new MonitorInfo { Index = 0, X = 0, Y = 0, Width = Native.GetSystemMetrics(0), Height = Native.GetSystemMetrics(1), Name = "Display", Primary = true });
        return list;
    }

    /// <summary>Captures the monitor and returns changed tiles as JPEGs. forceFull ignores the diff cache.</summary>
    public List<(int x, int y, int w, int h, byte[] jpeg)> CaptureTiles(int monitor, int jpegQuality, bool forceFull)
    {
        var result = new List<(int, int, int, int, byte[])>();
        var mon = Monitors.FirstOrDefault(m => m.Index == monitor);
        if (mon == null) return result;

        double grabMs, diffMs = 0, encodeMs = 0;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        using var bmp = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppArgb);
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            using var g = Graphics.FromImage(bmp);
            IntPtr memDc = g.GetHdc();
            Native.BitBlt(memDc, 0, 0, mon.Width, mon.Height, screenDc, mon.X, mon.Y, Native.SRCCOPY | Native.CAPTUREBLT);
            g.ReleaseHdc(memDc);
        }
        finally { Native.ReleaseDC(IntPtr.Zero, screenDc); }
        grabMs = Ms(t0);

        int cols = (mon.Width + Tile - 1) / Tile, rows = (mon.Height + Tile - 1) / Tile;
        if (!_states.TryGetValue(monitor, out var st) || st.Cols != cols || st.Rows != rows)
            _states[monitor] = st = new TileState { Cols = cols, Rows = rows, Hashes = new uint[cols * rows] };

        long payload = 0;
        var data = bmp.LockBits(new Rectangle(0, 0, mon.Width, mon.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* basep = (byte*)data.Scan0;
                int stride = data.Stride;
                var encoder = JpegEncoder(jpegQuality);
                var codec = JpegCodec();
                for (int ty = 0; ty < rows; ty++)
                for (int tx = 0; tx < cols; tx++)
                {
                    int px = tx * Tile, py = ty * Tile;
                    int tw = Math.Min(Tile, mon.Width - px), th = Math.Min(Tile, mon.Height - py);
                    long td = System.Diagnostics.Stopwatch.GetTimestamp();
                    uint hash = 2166136261u;
                    for (int yy = 0; yy < th; yy++)
                    {
                        byte* row = basep + (py + yy) * stride + px * 4;
                        for (int xx = 0; xx < tw * 4; xx++) { hash = (hash ^ row[xx]) * 16777619u; }
                    }
                    int cell = ty * cols + tx;
                    bool unchanged = !forceFull && st.Hashes![cell] == hash;
                    st.Hashes![cell] = hash;
                    diffMs += Ms(td);
                    if (unchanged) continue;

                    long te = System.Diagnostics.Stopwatch.GetTimestamp();
                    using var tile = bmp.Clone(new Rectangle(px, py, tw, th), PixelFormat.Format24bppRgb);
                    using var ms = new MemoryStream();
                    tile.Save(ms, codec, encoder);
                    var jpeg = ms.ToArray();
                    encodeMs += Ms(te);
                    payload += jpeg.Length;
                    result.Add((px, py, tw, th, jpeg));
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        LastTiming = new CaptureTiming(grabMs, diffMs, encodeMs, result.Count, cols * rows, payload);
        return result;
    }

    internal static double Ms(long since) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static ImageCodecInfo? _jpeg;
    private static ImageCodecInfo JpegCodec() => _jpeg ??= ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private static EncoderParameters JpegEncoder(int quality)
    {
        var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 20, 95));
        return p;
    }

    public void Dispose() { }
}
