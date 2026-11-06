using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// The fast capture path: DXGI Desktop Duplication. Windows has already worked out which
/// rectangles of the desktop changed while compositing them, so change detection stops being our
/// problem -- and an idle screen costs nothing at all, because there is no frame to fetch rather
/// than a frame to fetch and then discard.
///
/// It is not always available, which is the whole reason <see cref="IScreenSource"/> exists:
/// duplication is refused on some adapters, in some session states, and outright if any other
/// process on the machine already has the same output duplicated -- a running screen recorder is
/// enough. <see cref="ScreenCapture"/> stays as the fallback. Losing capture entirely is a worse
/// fault than a slow picture.
///
/// Two things here are subtler than they look and both have their reasoning at the point of use:
/// the persistent full-frame buffer (a forced-full frame must work on a screen that has not
/// changed, or a technician who subscribes to an idle desktop gets black) and ACCESS_LOST, which
/// is not an error but the ordinary consequence of the user hitting a UAC prompt.
/// </summary>
public sealed class DxgiCapture : IScreenSource
{
    public string Name => "DXGI Desktop Duplication";
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public CaptureTiming LastTiming { get; private set; }

    /// <summary>Why the last capture produced nothing, when the reason was not "nothing changed".</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// How many times duplication has had to be torn down and rebuilt. A handful over a session is
    /// normal -- every UAC prompt costs one. A number that climbs steadily is a fault.
    /// </summary>
    public int Rebuilds { get; private set; }

    private const int Tile = 128;

    /// <summary>
    /// Whether the tile path may rule tiles out by Windows' dirty rectangles. False, on purpose, for now.
    /// The struct those rectangles are found through was read wrongly until 2026-10-15 (Dxgi.cs), so the
    /// tile path has in fact compared every tile on every frame since the day it was written, and its
    /// rectangle shortcut has never run. Fixing the struct for the H.264 path's sharpening must not
    /// switch on, untested, a shortcut whose failure is a stale rectangle on the technician's screen.
    /// Turn it on with a codecbench --compare run, as its own change (HANDOVER §142).
    /// </summary>
    private const bool UseRectsForTiles = false;
    private double _lastDiffMs;

    private readonly Dictionary<int, Output> _outputs = new();

    /// <summary>
    /// One duplicated monitor: its own D3D11 device, the duplication object, the staging texture
    /// we read back through, and the last complete picture of that screen.
    ///
    /// The device belongs to the output rather than to the capturer because it must be created on
    /// the adapter that drives that screen. On a laptop with switchable graphics the panel and an
    /// external monitor are routinely on different GPUs, and duplicating an output through a
    /// device built on the other one fails with an argument error that mentions nothing about
    /// adapters.
    /// </summary>
    private sealed class Output : IDisposable
    {
        public IntPtr Device, Context;
        public IntPtr Duplication, Staging;
        public int X, Y, Width, Height, Stride;
        public IntPtr Frame;              // persistent full-frame BGRA, see PrimeFromGdi
        /// <summary>
        /// The same picture as <see cref="Frame"/>, but left on the graphics card, for the H.264
        /// path -- see <see cref="DxgiCapture.TryAcquireGpuFrame"/>. Created only if something asks
        /// for it, so a session that never negotiates video never pays for it.
        /// </summary>
        public IntPtr Video;
        public bool HoldingFrame;         // an acquired frame not yet released
        public bool Primed;
        /// <summary>
        /// A freshly built duplication owes the viewer a COMPLETE frame, and nothing else can be
        /// relied on to ask for one.
        ///
        /// The desktop switch that caused the rebuild did set the host's forceFull flag -- but the
        /// capture pass that read it is the same pass that then failed with ACCESS_LOST and
        /// returned nothing, so the flag was spent on a frame that never went out. Everything
        /// after that is incremental against a buffer the viewer has never seen, and the picture
        /// stays a mixture of the old desktop and the new one. That is what a UAC prompt looked
        /// like on real hardware: half the technician's screen was seconds out of date, with no
        /// error anywhere.
        /// </summary>
        public bool NeedsWholeFrame = true;
        public int Cols, Rows;

        public void Dispose()
        {
            if (HoldingFrame && Duplication != IntPtr.Zero) { Dxgi.ReleaseFrame(Duplication); HoldingFrame = false; }
            Dxgi.ReleaseAndClear(ref Duplication);
            Dxgi.ReleaseAndClear(ref Video);
            Dxgi.ReleaseAndClear(ref Staging);
            Dxgi.ReleaseAndClear(ref Context);
            Dxgi.ReleaseAndClear(ref Device);
            if (Frame != IntPtr.Zero) { Marshal.FreeHGlobal(Frame); Frame = IntPtr.Zero; }
        }
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> when Desktop Duplication cannot be used on this
    /// machine, which is the signal for the caller to use <see cref="ScreenCapture"/> instead.
    /// </summary>
    public DxgiCapture()
    {
        Refresh();
        // Availability is not a property worth guessing at: the only honest test of whether this
        // machine can duplicate its desktop is to duplicate it. Do it once here, on the primary
        // screen, so an unusable machine falls back at start-up rather than after a technician has
        // been looking at a black window for ten seconds. The object is kept, not thrown away --
        // the primary is the screen almost every session subscribes to.
        var primary = Monitors.FirstOrDefault(m => m.Primary) ?? Monitors.FirstOrDefault()
                      ?? throw new NotSupportedException("no monitors were found");
        _outputs[primary.Index] = Create(primary);
    }

    public void Refresh() => Monitors = ScreenCapture.EnumerateMonitors();

    public List<(int x, int y, int w, int h, byte[] jpeg)> CaptureTiles(int monitor, int jpegQuality, bool forceFull)
    {
        var result = new List<(int, int, int, int, byte[])>();
        var mon = Monitors.FirstOrDefault(m => m.Index == monitor);
        if (mon == null) { LastError = $"no monitor with index {monitor}"; return result; }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Output? o;
        try { o = GetOrCreate(mon); }
        catch (Exception ex) { LastError = ex.Message; return result; }
        if (o == null) return result;

        bool[] dirty;
        double grabMs;
        try
        {
            if (!TryTakeFrame(o, out dirty, forceFull)) { LastTiming = new CaptureTiming(ScreenCapture.Ms(t0), 0, 0, 0, o.Cols * o.Rows, 0); return result; }
            // The compare is charged to "diff" rather than to "grab", so the column means the
            // same thing in both sources: how long it took to decide what had changed.
            grabMs = ScreenCapture.Ms(t0) - _lastDiffMs;
        }
        catch (Exception ex) { LastError = ex.Message; Drop(mon.Index); return result; }

        // Everything below is encoding. Deciding what changed is already done.
        long te = System.Diagnostics.Stopwatch.GetTimestamp();
        long payload = 0;
        var encoder = JpegEncoder(jpegQuality);
        var codec = JpegCodec();
        for (int ty = 0; ty < o.Rows; ty++)
        for (int tx = 0; tx < o.Cols; tx++)
        {
            if (!dirty[ty * o.Cols + tx]) continue;
            int px = tx * Tile, py = ty * Tile;
            int tw = Math.Min(Tile, o.Width - px), th = Math.Min(Tile, o.Height - py);
            if (tw <= 0 || th <= 0) continue;
            // Wrapping the buffer in place, rather than cloning the tile out of a big bitmap the
            // way the GDI path has to, saves a copy per tile.
            using var tile = new Bitmap(tw, th, o.Stride, PixelFormat.Format32bppRgb, o.Frame + py * o.Stride + px * 4);
            using var ms = new MemoryStream();
            tile.Save(ms, codec, encoder);
            var jpeg = ms.ToArray();
            payload += jpeg.Length;
            result.Add((px, py, tw, th, jpeg));
        }

        LastTiming = new CaptureTiming(grabMs, _lastDiffMs, ScreenCapture.Ms(te), result.Count, o.Cols * o.Rows, payload);
        LastError = "";
        return result;
    }

    // ---- the GPU path, for H.264 (stage 2.2) ---------------------------------------------------
    //
    // Everything above this line brings the frame back to the CPU, because encoding a tile as JPEG
    // needs the bytes. H.264 does not: the encoder lives on the same graphics card the duplication
    // does, so the frame's whole journey is one GPU-to-GPU copy and the pixels never cross the bus.
    // Keeping a readback in this path would give back most of what stage 2.1 won.

    /// <summary>
    /// A duplicated frame still on the graphics card. The texture belongs to the capturer and is
    /// valid until the next call for the same monitor -- it is not a snapshot to hold on to.
    /// </summary>
    public readonly record struct GpuFrame(IntPtr Device, IntPtr Texture, int Width, int Height);

    /// <summary>
    /// The H.264 path's equivalent of <see cref="CaptureTiles"/>: brings the on-card copy of the
    /// screen up to date and says whether there is anything new to encode.
    ///
    /// False means the screen has not changed, which on an idle machine is almost every call and
    /// is the whole reason stage 2.1 was worth doing. <paramref name="forceFull"/> means a viewer
    /// with no picture is waiting, and is answered even from a screen that has not changed --
    /// the same obligation, and the same trap, as in <see cref="TryTakeFrame"/>.
    /// </summary>
    public bool TryAcquireGpuFrame(int monitor, bool forceFull, out GpuFrame frame)
    {
        frame = default;
        var mon = Monitors.FirstOrDefault(m => m.Index == monitor);
        if (mon == null) { LastError = $"no monitor with index {monitor}"; return false; }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Output? o;
        try { o = GetOrCreate(mon); }
        catch (Exception ex) { LastError = ex.Message; return false; }
        if (o == null) return false;

        try
        {
            if (!EnsureVideoTexture(o)) return false;
            if (!TryCopyToVideo(o, forceFull)) { LastTiming = new CaptureTiming(ScreenCapture.Ms(t0), 0, 0, 0, 1, 0); return false; }
        }
        catch (Exception ex) { LastError = ex.Message; Drop(mon.Index); return false; }

        LastTiming = new CaptureTiming(ScreenCapture.Ms(t0), 0, 0, 1, 1, 0);
        LastError = "";
        frame = new GpuFrame(o.Device, o.Video, o.Width, o.Height);
        return true;
    }

    /// <summary>
    /// Creates the on-card frame buffer the first time something asks for one, filled from the
    /// same GDI grab that primed the CPU buffer.
    ///
    /// Priming matters here for exactly the reason it does on the tile path, and the symptom is
    /// worse: a forced key frame of an uninitialised texture is not a stale picture, it is a
    /// screenful of whatever was in that memory.
    /// </summary>
    private bool EnsureVideoTexture(Output o)
    {
        if (o.Video != IntPtr.Zero) return true;
        var desc = new Dxgi.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)o.Width, Height = (uint)o.Height, MipLevels = 1, ArraySize = 1,
            Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, SampleQuality = 0,
            Usage = Dxgi.D3D11_USAGE_DEFAULT, BindFlags = Dxgi.D3D11_BIND_SHADER_RESOURCE | Dxgi.D3D11_BIND_RENDER_TARGET,
            CPUAccessFlags = 0, MiscFlags = 0,
        };
        var seed = new Dxgi.D3D11_SUBRESOURCE_DATA { pSysMem = o.Frame, SysMemPitch = (uint)o.Stride, SysMemSlicePitch = 0 };
        IntPtr initial = Marshal.AllocHGlobal(Marshal.SizeOf<Dxgi.D3D11_SUBRESOURCE_DATA>());
        try
        {
            Marshal.StructureToPtr(seed, initial, false);
            int hr = Dxgi.CreateTexture2D(o.Device, ref desc, o.Primed ? initial : IntPtr.Zero, out o.Video);
            if (hr != Dxgi.S_OK || o.Video == IntPtr.Zero)
            {
                o.Video = IntPtr.Zero;
                LastError = $"a video frame texture could not be created: {Dxgi.Explain(hr)}";
                return false;
            }
            return true;
        }
        finally { Marshal.FreeHGlobal(initial); }
    }

    /// <summary>
    /// Copies the newest duplicated frame into the on-card buffer. The same acquire-and-release
    /// dance as <see cref="TryTakeFrame"/>, minus everything to do with tiles.
    ///
    /// There is deliberately no change detection beyond what Desktop Duplication already told us.
    /// The tile path compares pixels because a dirty rectangle is a superset of what really
    /// changed and ten times the tiles is ten times the bandwidth; here the encoder is the thing
    /// that decides what a frame costs, and a frame that is identical to the last one costs it
    /// almost nothing. Comparing first would be a readback to save nothing.
    /// </summary>
    private bool TryCopyToVideo(Output o, bool forceFull)
    {
        if (o.HoldingFrame) { Dxgi.ReleaseFrame(o.Duplication); o.HoldingFrame = false; }

        int hr = Dxgi.AcquireNextFrame(o.Duplication, 0, out var info, out IntPtr resource);
        if (hr == Dxgi.DXGI_ERROR_WAIT_TIMEOUT)
        {
            // Nothing new. The texture still holds the last complete picture, so a demand for a
            // whole frame can still be answered -- and must be, or a technician subscribing to an
            // idle desktop waits for the screensaver.
            if ((forceFull || o.NeedsWholeFrame) && o.Primed) { o.NeedsWholeFrame = false; return true; }
            return false;
        }
        if (hr != Dxgi.S_OK)
        {
            if (hr == Dxgi.DXGI_ERROR_ACCESS_LOST) { LastError = Dxgi.Explain(hr); throw new InvalidOperationException(Dxgi.Explain(hr)); }
            LastError = Dxgi.Explain(hr);
            return false;
        }

        try
        {
            if (resource == IntPtr.Zero) return false;
            bool pixelsChanged = info.LastPresentTime != 0 || info.AccumulatedFrames > 0;
            if (!pixelsChanged && !forceFull && !o.NeedsWholeFrame) return false;

            var iid = Dxgi.IID_ID3D11Texture2D;
            if (Dxgi.QueryInterface(resource, ref iid, out IntPtr texture) != Dxgi.S_OK || texture == IntPtr.Zero)
            { LastError = "the duplicated frame was not a texture"; return false; }
            try { Dxgi.CopyResource(o.Context, o.Video, texture); }
            finally { Dxgi.Release(texture); }

            LastVideoChange = pixelsChanged ? ChangedShare(o, info) : 0;
            o.Primed = true;
            o.NeedsWholeFrame = false;
            return true;
        }
        finally
        {
            Dxgi.Release(resource);
            Dxgi.ReleaseFrame(o.Duplication);
        }
    }

    /// <summary>
    /// Brings the persistent frame buffer up to date and reports which tiles changed. Returns
    /// false when there is simply nothing new, which is the normal state of an idle desktop.
    /// </summary>
    private unsafe bool TryTakeFrame(Output o, out bool[] dirty, bool forceFull)
    {
        dirty = new bool[o.Cols * o.Rows];
        bool primedBefore = o.Primed;
        _lastDiffMs = 0;

        if (o.HoldingFrame) { Dxgi.ReleaseFrame(o.Duplication); o.HoldingFrame = false; }

        // Zero timeout: ask what has happened since last time and return at once either way. The
        // capture loop owns the pacing, and a blocking wait here would fight it.
        int hr = Dxgi.AcquireNextFrame(o.Duplication, 0, out var info, out IntPtr resource);

        if (hr == Dxgi.DXGI_ERROR_WAIT_TIMEOUT)
        {
            // Nothing changed. Normally that means send nothing -- but a forced-full frame is a
            // demand for the whole screen from a viewer that has none, and answering it with
            // silence is the black screen the trace counters in ScreenView were built to explain.
            // The buffer still holds the last complete picture, so serve that.
            // NeedsWholeFrame matters as much as forceFull here, and this is the case that bit on
            // hardware: a UAC prompt just sitting there is a STILL screen, so the rebuilt
            // duplication has nothing new to hand over and would say nothing at all -- leaving the
            // viewer on the previous desktop's picture for as long as the prompt is up.
            if ((forceFull || o.NeedsWholeFrame) && o.Primed) { MarkAll(dirty); o.NeedsWholeFrame = false; return true; }
            return false;
        }
        if (hr != Dxgi.S_OK)
        {
            // ACCESS_LOST is not a fault. It is what the user hitting a UAC prompt, changing
            // resolution or the driver resetting looks like from here, and the answer to all
            // three is the same: throw the duplication away and build another one.
            if (hr == Dxgi.DXGI_ERROR_ACCESS_LOST) { LastError = Dxgi.Explain(hr); throw new InvalidOperationException(Dxgi.Explain(hr)); }
            LastError = Dxgi.Explain(hr);
            return false;
        }

        try
        {
            if (resource == IntPtr.Zero) return false;

            // AccumulatedFrames == 0 with a successful acquire means only the mouse moved. The
            // cursor is not in the duplicated image and is drawn by the console, so there is
            // genuinely nothing to send -- and this is a large share of acquires on a machine
            // somebody is using.
            bool pixelsChanged = info.LastPresentTime != 0 || info.AccumulatedFrames > 0;
            if (!pixelsChanged && !forceFull) return false;

            IntPtr texture = IntPtr.Zero;
            var iid = Dxgi.IID_ID3D11Texture2D;
            if (Dxgi.QueryInterface(resource, ref iid, out texture) != Dxgi.S_OK || texture == IntPtr.Zero)
            { LastError = "the duplicated frame was not a texture"; return false; }

            try
            {
                Dxgi.CopyResource(o.Context, o.Staging, texture);
                if (Dxgi.Map(o.Context, o.Staging, 0, Dxgi.D3D11_MAP_READ, 0, out var mapped) != Dxgi.S_OK || mapped.pData == IntPtr.Zero)
                { LastError = "the staging texture could not be mapped"; return false; }
                try
                {
                    // Which rectangles changed: Windows' answer, not ours. Coalesced rects, an
                    // acquire that skipped frames, or no metadata at all all mean "assume the lot"
                    // -- being wrong in that direction costs bandwidth, the other way costs
                    // correctness, and a stale tile on screen is the bug nobody can reproduce.
                    // UseRectsForTiles: see its comment. Until it is measured, every tile is compared,
                    // which is what this path has in fact always done.
                    bool wholeFrame = forceFull || o.NeedsWholeFrame || !o.Primed || !UseRectsForTiles
                                   || info.RectsCoalesced != 0 || info.TotalMetadataBufferSize == 0;
                    if (!wholeFrame) wholeFrame = !TryMarkFromMetadata(o, info, dirty);
                    if (wholeFrame) MarkAll(dirty);

                    // A dirty rectangle says Windows REDREW those pixels. It does not say they
                    // came out different, and the gap between those two is enormous: an
                    // application that invalidates its whole window on every frame -- which is
                    // most of them -- makes Desktop Duplication report the whole window while
                    // almost none of it actually changed. Measured on the scrolling condition,
                    // trusting the rectangles sent 150 tiles a frame where 24 had changed: ten
                    // times the bandwidth, for an identical picture.
                    //
                    // So the rectangles are used for what they are genuinely good at -- ruling
                    // most of the screen out without looking at it -- and the tiles that survive
                    // are compared against the last frame for real. The comparison is against the
                    // buffer we are about to overwrite, so it costs no extra memory and no hash.
                    long tdiff = System.Diagnostics.Stopwatch.GetTimestamp();
                    for (int ty = 0; ty < o.Rows; ty++)
                    for (int tx = 0; tx < o.Cols; tx++)
                    {
                        int cell = ty * o.Cols + tx;
                        if (!dirty[cell]) continue;
                        int px = tx * Tile, py = ty * Tile;
                        int tw = Math.Min(Tile, o.Width - px), th = Math.Min(Tile, o.Height - py);
                        bool changed = CopyTileIfDifferent(mapped, o, px, py, tw, th);
                        // forceFull means a viewer with no picture at all is waiting, so every
                        // tile goes whether it changed or not.
                        dirty[cell] = changed || forceFull || o.NeedsWholeFrame || !primedBefore;
                    }
                    _lastDiffMs = ScreenCapture.Ms(tdiff);
                    o.Primed = true;
                    // Cleared only now, when a complete frame is genuinely on its way out.
                    o.NeedsWholeFrame = false;
                }
                finally { Dxgi.Unmap(o.Context, o.Staging, 0); }
            }
            finally { Dxgi.Release(texture); }
        }
        finally
        {
            Dxgi.Release(resource);
            Dxgi.ReleaseFrame(o.Duplication);
        }
        return true;
    }

    /// <summary>
    /// How much of the screen the last frame <see cref="TryAcquireGpuFrame"/> took had changed, 0 to 1,
    /// from Windows' own move and dirty rectangles — never a readback. 1 when Windows did not say.
    ///
    /// The host's still-screen sharpening (<see cref="StillRefiner"/>) asks it, so that a blinking text
    /// cursor — a change every 530 ms of a few dozen pixels — does not keep the sharpening running on
    /// an idle screen. An overestimate is the safe direction: a rectangle is a superset of what
    /// changed, and the worst it costs is sharpening that was not needed, at the rate already agreed.
    /// </summary>
    public double LastVideoChange { get; private set; } = 1;

    /// <summary>Why <see cref="LastVideoChange"/> fell back to 1, for the bench.</summary>
    public string ShareWhy { get; private set; } = "";

    private unsafe double ChangedShare(Output o, Dxgi.DXGI_OUTDUPL_FRAME_INFO info)
    {
        int size = (int)info.TotalMetadataBufferSize;
        if (size <= 0 || o.Width <= 0 || o.Height <= 0) { ShareWhy = $"no metadata ({size} bytes)"; return 1; }
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            long area = 0;
            int hrm = Dxgi.GetFrameMoveRects(o.Duplication, (uint)size, (Dxgi.DXGI_OUTDUPL_MOVE_RECT*)buf, out uint moveBytes);
            if (hrm != Dxgi.S_OK) { ShareWhy = $"move rects refused: {Dxgi.Explain(hrm)}"; return 1; }
            var moves = (Dxgi.DXGI_OUTDUPL_MOVE_RECT*)buf;
            int moveCount = (int)(moveBytes / (uint)sizeof(Dxgi.DXGI_OUTDUPL_MOVE_RECT));
            // A move changes its destination and uncovers its source, as in TryMarkFromMetadata.
            for (int i = 0; i < moveCount; i++) area += 2L * Area(moves[i].Destination);
            int left = size - (int)moveBytes;
            if (left > 0)
            {
                var dirtyBuf = (Dxgi.RECT*)(buf + (int)moveBytes);
                int hrd = Dxgi.GetFrameDirtyRects(o.Duplication, (uint)left, dirtyBuf, out uint dirtyBytes);
                if (hrd != Dxgi.S_OK) { ShareWhy = $"dirty rects refused: {Dxgi.Explain(hrd)}"; return 1; }
                int dirtyCount = (int)(dirtyBytes / (uint)sizeof(Dxgi.RECT));
                for (int i = 0; i < dirtyCount; i++) area += Area(dirtyBuf[i]);
            }
            return Math.Min(1.0, (double)area / ((long)o.Width * o.Height));
        }
        finally { Marshal.FreeHGlobal(buf); }

        static long Area(Dxgi.RECT r) => Math.Max(0L, r.Right - r.Left) * Math.Max(0L, r.Bottom - r.Top);
    }

    /// <summary>Marks the tiles touched by the frame's move and dirty rectangles. False if the metadata could not be read.</summary>
    private unsafe bool TryMarkFromMetadata(Output o, Dxgi.DXGI_OUTDUPL_FRAME_INFO info, bool[] dirty)
    {
        int size = (int)info.TotalMetadataBufferSize;
        if (size <= 0) return false;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            // Move rects come first; the docs are specific about the order, and the dirty-rect
            // call is defined in terms of what the move-rect call left.
            if (Dxgi.GetFrameMoveRects(o.Duplication, (uint)size, (Dxgi.DXGI_OUTDUPL_MOVE_RECT*)buf, out uint moveBytes) != Dxgi.S_OK) return false;
            var moves = (Dxgi.DXGI_OUTDUPL_MOVE_RECT*)buf;
            int moveCount = (int)(moveBytes / (uint)sizeof(Dxgi.DXGI_OUTDUPL_MOVE_RECT));
            for (int i = 0; i < moveCount; i++)
            {
                var m = moves[i];
                // Both ends of a move changed as far as the far end is concerned: the destination
                // has new content, and whatever was uncovered at the source has new content too.
                Mark(o, dirty, m.Destination);
                int w = m.Destination.Right - m.Destination.Left, h = m.Destination.Bottom - m.Destination.Top;
                Mark(o, dirty, new Dxgi.RECT { Left = m.SourceX, Top = m.SourceY, Right = m.SourceX + w, Bottom = m.SourceY + h });
            }

            int left = size - (int)moveBytes;
            if (left <= 0) return moveCount > 0;
            var dirtyBuf = (Dxgi.RECT*)(buf + (int)moveBytes);
            if (Dxgi.GetFrameDirtyRects(o.Duplication, (uint)left, dirtyBuf, out uint dirtyBytes) != Dxgi.S_OK) return false;
            int dirtyCount = (int)(dirtyBytes / (uint)sizeof(Dxgi.RECT));
            for (int i = 0; i < dirtyCount; i++) Mark(o, dirty, dirtyBuf[i]);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void MarkAll(bool[] dirty) { for (int i = 0; i < dirty.Length; i++) dirty[i] = true; }

    private static void Mark(Output o, bool[] dirty, Dxgi.RECT r)
    {
        int x0 = Math.Clamp(r.Left, 0, Math.Max(0, o.Width - 1)) / Tile;
        int y0 = Math.Clamp(r.Top, 0, Math.Max(0, o.Height - 1)) / Tile;
        int x1 = Math.Clamp(r.Right - 1, 0, Math.Max(0, o.Width - 1)) / Tile;
        int y1 = Math.Clamp(r.Bottom - 1, 0, Math.Max(0, o.Height - 1)) / Tile;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            dirty[y * o.Cols + x] = true;
    }

    /// <summary>
    /// Compares one tile in the freshly mapped texture against the same tile in the persistent
    /// frame buffer, and copies it over if it differs. Returns whether it differed.
    ///
    /// The comparison is a span equality rather than a hash, which is both exact -- no collisions
    /// to reason about -- and faster, because it is vectorised and it stops at the first differing
    /// byte. Most tiles that reach here differ in their first row or not at all.
    /// </summary>
    private static unsafe bool CopyTileIfDifferent(Dxgi.D3D11_MAPPED_SUBRESOURCE mapped, Output o, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        byte* src = (byte*)mapped.pData + (long)y * mapped.RowPitch + (long)x * 4;
        byte* dst = (byte*)o.Frame + (long)y * o.Stride + (long)x * 4;
        int bytes = w * 4;

        bool same = true;
        for (int row = 0; row < h; row++)
        {
            if (!new ReadOnlySpan<byte>(src + (long)row * mapped.RowPitch, bytes)
                 .SequenceEqual(new ReadOnlySpan<byte>(dst + (long)row * o.Stride, bytes)))
            { same = false; break; }
        }
        if (same) return false;

        for (int row = 0; row < h; row++)
            Buffer.MemoryCopy(src + (long)row * mapped.RowPitch, dst + (long)row * o.Stride, bytes, bytes);
        return true;
    }

    // ---- building and rebuilding the duplication ------------------------------------------------

    private Output? GetOrCreate(MonitorInfo mon)
    {
        if (_outputs.TryGetValue(mon.Index, out var existing))
        {
            // The rectangle, not just the size: after a monitor is unplugged the indices shift,
            // and two screens of the same resolution would otherwise keep each other's duplication
            // and show the technician the wrong desk.
            if (existing.X == mon.X && existing.Y == mon.Y && existing.Width == mon.Width && existing.Height == mon.Height) return existing;
            existing.Dispose(); _outputs.Remove(mon.Index);
        }
        var o = Create(mon);
        _outputs[mon.Index] = o;
        return o;
    }

    private void Drop(int monitorIndex)
    {
        if (_outputs.TryGetValue(monitorIndex, out var o)) { o.Dispose(); _outputs.Remove(monitorIndex); }
        Rebuilds++;
    }

    /// <summary>
    /// Gives up every duplication and the graphics devices behind them. See
    /// <see cref="IScreenSource.ReleaseIdle"/> for why that matters to anything other than memory.
    ///
    /// Deliberately does NOT touch <see cref="Rebuilds"/>. That counter means "the duplication
    /// broke and had to be remade", which the health check and the trace read as a sign of
    /// trouble; letting go because nobody is watching is the opposite of trouble, and counting it
    /// there would put a fault in the log of every idle session.
    /// </summary>
    public void ReleaseIdle()
    {
        if (_outputs.Count == 0) return;
        foreach (var o in _outputs.Values) o.Dispose();
        _outputs.Clear();
    }

    private Output Create(MonitorInfo mon)
    {
        var (adapter, output1) = FindOutput(mon);
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero;
        try
        {
            // DriverType must be UNKNOWN when an adapter is named, and must NOT be when one is
            // not. Getting that pair wrong is an E_INVALIDARG that says nothing about which of
            // the two arguments it disliked.
            // VIDEO_SUPPORT is asked for so the stage 2.2 encoder can share this device, and it is
            // asked for SECOND so that a machine which refuses it still gets the capture path that
            // 2.1 proved on hardware. A device without it captures perfectly and simply cannot
            // encode, which is a negotiation the session already knows how to lose.
            int hr = Dxgi.D3D11CreateDevice(adapter, Dxgi.D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero,
                                            Dxgi.D3D11_CREATE_DEVICE_BGRA_SUPPORT | Dxgi.D3D11_CREATE_DEVICE_VIDEO_SUPPORT, IntPtr.Zero, 0,
                                            Dxgi.D3D11_SDK_VERSION, out device, out _, out context);
            if (hr != Dxgi.S_OK || device == IntPtr.Zero)
                hr = Dxgi.D3D11CreateDevice(adapter, Dxgi.D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero,
                                            Dxgi.D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0,
                                            Dxgi.D3D11_SDK_VERSION, out device, out _, out context);
            if (hr != Dxgi.S_OK || device == IntPtr.Zero)
                throw new NotSupportedException($"Direct3D 11 would not start on the adapter driving monitor {mon.Index}: {Dxgi.Explain(hr)}");

            hr = Dxgi.DuplicateOutput(output1, device, out IntPtr dupl);
            if (hr != Dxgi.S_OK || dupl == IntPtr.Zero)
                throw new NotSupportedException($"Desktop Duplication was refused for monitor {mon.Index}: {Dxgi.Explain(hr)}");

            var o = new Output
            {
                Device = device, Context = context,
                Duplication = dupl,
                X = mon.X, Y = mon.Y, Width = mon.Width, Height = mon.Height,
                Stride = mon.Width * 4,
                Cols = (mon.Width + Tile - 1) / Tile,
                Rows = (mon.Height + Tile - 1) / Tile,
            };
            o.Frame = Marshal.AllocHGlobal(o.Stride * o.Height);

            var desc = new Dxgi.D3D11_TEXTURE2D_DESC
            {
                Width = (uint)mon.Width, Height = (uint)mon.Height, MipLevels = 1, ArraySize = 1,
                Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM, SampleCount = 1, SampleQuality = 0,
                Usage = Dxgi.D3D11_USAGE_STAGING, BindFlags = 0,
                CPUAccessFlags = Dxgi.D3D11_CPU_ACCESS_READ, MiscFlags = 0,
            };
            if (Dxgi.CreateTexture2D(device, ref desc, IntPtr.Zero, out o.Staging) != Dxgi.S_OK || o.Staging == IntPtr.Zero)
            { o.Dispose(); throw new NotSupportedException("a readback texture could not be created"); }

            device = context = IntPtr.Zero;   // the Output owns them now
            PrimeFromGdi(o, mon);
            return o;
        }
        finally
        {
            Dxgi.Release(output1);
            Dxgi.Release(adapter);
            // Only reached when construction threw before the Output took ownership.
            Dxgi.ReleaseAndClear(ref context);
            Dxgi.ReleaseAndClear(ref device);
        }
    }

    /// <summary>
    /// Fills the frame buffer with one GDI grab at creation.
    ///
    /// Duplication only ever hands over pixels that have changed, so a technician who subscribes
    /// to a machine sitting at an idle desktop would otherwise wait for the screensaver before
    /// seeing anything. One BitBlt, once per duplication, removes that entirely -- and it is the
    /// fallback path being useful rather than merely present.
    /// </summary>
    private static void PrimeFromGdi(Output o, MonitorInfo mon)
    {
        try
        {
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

            var data = bmp.LockBits(new Rectangle(0, 0, mon.Width, mon.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* src = (byte*)data.Scan0, dst = (byte*)o.Frame;
                    long bytes = (long)o.Width * 4;
                    for (int y = 0; y < o.Height; y++)
                        Buffer.MemoryCopy(src + (long)y * data.Stride, dst + (long)y * o.Stride, bytes, bytes);
                }
                o.Primed = true;
            }
            finally { bmp.UnlockBits(data); }
        }
        catch { /* not primed; the first real frame will fill it */ }
    }

    /// <summary>
    /// Finds the IDXGIOutput1 for a monitor, across every adapter.
    ///
    /// Walking all adapters is not defensive programming. On a laptop with switchable graphics the
    /// panel and an external screen are frequently on different GPUs, and duplicating an output
    /// with a device created on the wrong adapter fails with an argument error that says nothing
    /// about adapters.
    /// </summary>
    private static (IntPtr adapter, IntPtr output1) FindOutput(MonitorInfo mon)
    {
        var iidF = Dxgi.IID_IDXGIFactory1;
        int hr = Dxgi.CreateDXGIFactory1(ref iidF, out IntPtr factory);
        if (hr != Dxgi.S_OK || factory == IntPtr.Zero) throw new NotSupportedException($"DXGI would not start: {Dxgi.Explain(hr)}");
        try
        {
            for (uint a = 0; ; a++)
            {
                if (Dxgi.EnumAdapters1(factory, a, out IntPtr adapter) != Dxgi.S_OK || adapter == IntPtr.Zero) break;
                bool keepAdapter = false;
                try
                {
                    for (uint n = 0; ; n++)
                    {
                        if (Dxgi.EnumOutputs(adapter, n, out IntPtr output) != Dxgi.S_OK || output == IntPtr.Zero) break;
                        try
                        {
                            if (Dxgi.GetOutputDesc(output, out var desc) == Dxgi.S_OK && desc.AttachedToDesktop &&
                                desc.DesktopCoordinates.Left == mon.X && desc.DesktopCoordinates.Top == mon.Y &&
                                desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left == mon.Width &&
                                desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top == mon.Height)
                            {
                                var iid1 = Dxgi.IID_IDXGIOutput1;
                                if (Dxgi.QueryInterface(output, ref iid1, out IntPtr o1) == Dxgi.S_OK && o1 != IntPtr.Zero)
                                {
                                    keepAdapter = true;      // the caller needs it to build the device
                                    return (adapter, o1);    // caller releases both
                                }
                            }
                        }
                        finally { Dxgi.Release(output); }
                    }
                }
                finally { if (!keepAdapter) Dxgi.Release(adapter); }
            }
        }
        finally { Dxgi.Release(factory); }
        throw new NotSupportedException($"no DXGI output matches monitor {mon.Index} at {mon.X},{mon.Y} {mon.Width}x{mon.Height}");
    }

    private static ImageCodecInfo? _jpeg;
    private static ImageCodecInfo JpegCodec() => _jpeg ??= ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private static EncoderParameters JpegEncoder(int quality)
    {
        var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 20, 95));
        return p;
    }

    public void Dispose()
    {
        foreach (var o in _outputs.Values) o.Dispose();
        _outputs.Clear();


    }
}
