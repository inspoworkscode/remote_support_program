using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteSupport.Media;

/// <summary>
/// Writes a session recording as an ordinary .mp4 (H.264), through Windows' own Media Foundation
/// sink writer — nothing bought in (roadmap 1.6). The console hands it the picture the technician
/// is looking at, a few frames a second, as 32-bit BGRA; the sink writer converts, encodes and
/// muxes, and the file plays in anything.
///
/// <para>Same rules as <c>Mf.cs</c>: COM objects are IntPtrs and methods are called by vtable slot,
/// counted from IUnknown and written out below so the count can be re-derived rather than trusted.
/// <c>tools/recordcheck</c> writes a file with a known picture and reads it back through
/// <see cref="Mp4Reader"/> — a wrong slot shows up as a wrong or missing picture, not as a crash on
/// a technician's machine.</para>
/// </summary>
public sealed unsafe class ScreenRecorder : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public long Frames { get; private set; }
    private IntPtr _writer;
    private readonly uint _stream;
    private bool _finished;

    private ScreenRecorder(IntPtr writer, uint stream, int w, int h) { _writer = writer; _stream = stream; Width = w; Height = h; }

    /// <summary>
    /// Opens <paramref name="path"/> for writing. Width and height are rounded down to even numbers,
    /// which H.264 requires; frames must then be exactly that size.
    /// </summary>
    public static ScreenRecorder Create(string path, int width, int height, int fps = 8, int bitsPerSecond = 1_200_000)
    {
        width &= ~1; height &= ~1;
        if (width < 16 || height < 16) throw new ArgumentException($"a {width}x{height} recording is too small");
        Check(Mf.MFStartup(Mf.MF_VERSION, Mf.MFSTARTUP_FULL), "starting Media Foundation");

        IntPtr attrs = IntPtr.Zero, writer = IntPtr.Zero, outType = IntPtr.Zero, inType = IntPtr.Zero;
        try
        {
            Check(MFCreateAttributes(out attrs, 1), "creating the writer's attributes");
            // Written as fast as it is given frames: the console paces them, and throttling would
            // stall the thread that is taking them off the screen.
            var noThrottle = MF_SINK_WRITER_DISABLE_THROTTLING;
            Mf.SetUINT32(attrs, ref noThrottle, 1);
            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attrs, out writer), "opening the file for the recording");

            Check(Mf.MFCreateMediaType(out outType), "creating the H.264 type");
            SetVideo(outType, Mf.MFVideoFormat_H264, width, height, fps);
            var br = Mf.MF_MT_AVG_BITRATE; Mf.SetUINT32(outType, ref br, (uint)bitsPerSecond);
            Check(AddStream(writer, outType, out var stream), "adding the video stream");

            Check(Mf.MFCreateMediaType(out inType), "creating the input type");
            SetVideo(inType, MFVideoFormat_RGB32, width, height, fps);
            // Positive stride: rows top first, as a Bitmap holds them. Media Foundation's default for
            // RGB is bottom-up, and a recording that plays upside down is the result of leaving it.
            var stride = Mf.MF_MT_DEFAULT_STRIDE; Mf.SetUINT32(inType, ref stride, (uint)(width * 4));
            Check(SetInputMediaType(writer, stream, inType), "setting the input to 32-bit frames (the H.264 encoder may be missing)");

            Check(BeginWriting(writer), "starting to write");
            var r = new ScreenRecorder(writer, stream, width, height);
            writer = IntPtr.Zero;
            return r;
        }
        finally
        {
            Mf.ReleaseAndClear(ref attrs); Mf.ReleaseAndClear(ref outType); Mf.ReleaseAndClear(ref inType);
            if (writer != IntPtr.Zero) Mf.Release(writer);
        }
    }

    /// <summary>One frame, exactly Width x Height, 32 bits a pixel, at <paramref name="at"/> since the start.</summary>
    public void AddFrame(Bitmap frame, TimeSpan at, TimeSpan duration)
    {
        if (_finished) return;
        if (frame.Width != Width || frame.Height != Height) throw new ArgumentException($"frame is {frame.Width}x{frame.Height}, the recording is {Width}x{Height}");
        var data = frame.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        IntPtr buf = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            uint size = (uint)(Width * Height * 4);
            Check(Mf.MFCreateMemoryBuffer(size, out buf), "creating a frame buffer");
            Check(Mf.Lock(buf, out var dst, out _, out _), "locking a frame buffer");
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy((byte*)data.Scan0 + (long)y * data.Stride, (byte*)dst + (long)y * Width * 4, Width * 4, Width * 4);
            Mf.Unlock(buf);
            Mf.SetCurrentLength(buf, size);
            Check(Mf.MFCreateSample(out sample), "creating a sample");
            Mf.AddBuffer(sample, buf);
            Mf.SetSampleTime(sample, at.Ticks);          // TimeSpan ticks ARE 100 ns, Media Foundation's unit
            Mf.SetSampleDuration(sample, duration.Ticks);
            Check(WriteSample(_writer, _stream, sample), "writing a frame");
            Frames++;
        }
        finally
        {
            frame.UnlockBits(data);
            Mf.ReleaseAndClear(ref sample); Mf.ReleaseAndClear(ref buf);
        }
    }

    /// <summary>Finishes the file so it plays. After this nothing more can be added.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        try { Check(FinalizeWriter(_writer), "finishing the file"); }
        finally { Mf.ReleaseAndClear(ref _writer); }
    }

    public void Dispose() { try { Finish(); } catch { } }

    private static void SetVideo(IntPtr type, Guid subtype, int w, int h, int fps)
    {
        var major = Mf.MF_MT_MAJOR_TYPE; var video = Mf.MFMediaType_Video; Mf.SetGUID(type, ref major, ref video);
        var sub = Mf.MF_MT_SUBTYPE; Mf.SetGUID(type, ref sub, ref subtype);
        var il = Mf.MF_MT_INTERLACE_MODE; Mf.SetUINT32(type, ref il, 2);      // progressive
        var fs = Mf.MF_MT_FRAME_SIZE; Mf.SetPair(type, ref fs, (uint)w, (uint)h);
        var fr = Mf.MF_MT_FRAME_RATE; Mf.SetPair(type, ref fr, (uint)fps, 1);
        var par = Mf.MF_MT_PIXEL_ASPECT_RATIO; Mf.SetPair(type, ref par, 1, 1);
    }

    internal static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"recording: {what} failed — {Mf.Explain(hr)}");
    }

    // ---- exports and GUIDs not in Mf.cs ------------------------------------------------------------
    internal static Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    [DllImport("mfplat.dll")] internal static extern int MFCreateAttributes(out IntPtr attrs, uint initialSize);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attrs, out IntPtr writer);

    // ---- IMFSinkWriter : IUnknown --------------------------------------------------------------
    // IUnknown 0-2, then AddStream(3), SetInputMediaType(4), BeginWriting(5), WriteSample(6),
    // SendStreamTick(7), PlaceMarker(8), NotifyEndOfSegment(9), Flush(10), Finalize(11),
    // GetServiceForStream(12), GetStatistics(13).
    private static void** V(IntPtr o) => *(void***)o;
    private static int AddStream(IntPtr w, IntPtr type, out uint index)
    { fixed (uint* p = &index) return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)V(w)[3])(w, type, p); }
    private static int SetInputMediaType(IntPtr w, uint stream, IntPtr type) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)V(w)[4])(w, stream, type, IntPtr.Zero);
    private static int BeginWriting(IntPtr w) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(w)[5])(w);
    private static int WriteSample(IntPtr w, uint stream, IntPtr sample) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)V(w)[6])(w, stream, sample);
    private static int FinalizeWriter(IntPtr w) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)V(w)[11])(w);
}

/// <summary>
/// Reads an .mp4 back: its size, how many frames and how long, and the first frame as a picture.
/// For <c>tools/recordcheck</c>, which must know a recording PLAYS rather than that a file was
/// written — and the right way up, which only looking at a decoded frame can say.
/// </summary>
public static unsafe class Mp4Reader
{
    public sealed record Info(int Width, int Height, int Frames, TimeSpan Length, Bitmap? First);

    public static Info Read(string path)
    {
        ScreenRecorder.Check(Mf.MFStartup(Mf.MF_VERSION, Mf.MFSTARTUP_FULL), "starting Media Foundation");
        IntPtr attrs = IntPtr.Zero, reader = IntPtr.Zero, want = IntPtr.Zero, cur = IntPtr.Zero;
        try
        {
            ScreenRecorder.Check(ScreenRecorder.MFCreateAttributes(out attrs, 1), "creating the reader's attributes");
            var vp = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING; Mf.SetUINT32(attrs, ref vp, 1);
            ScreenRecorder.Check(MFCreateSourceReaderFromURL(path, attrs, out reader), "opening the recording");

            ScreenRecorder.Check(Mf.MFCreateMediaType(out want), "creating the output type");
            var major = Mf.MF_MT_MAJOR_TYPE; var video = Mf.MFMediaType_Video; Mf.SetGUID(want, ref major, ref video);
            var sub = Mf.MF_MT_SUBTYPE; var rgb = ScreenRecorder.MFVideoFormat_RGB32; Mf.SetGUID(want, ref sub, ref rgb);
            ScreenRecorder.Check(SetCurrentMediaType(reader, FirstVideo, want), "asking for 32-bit frames");
            ScreenRecorder.Check(GetCurrentMediaType(reader, FirstVideo, out cur), "reading the frame format");
            var fsKey = Mf.MF_MT_FRAME_SIZE;
            var (w, h) = Mf.GetPair(cur, ref fsKey);
            var strideKey = Mf.MF_MT_DEFAULT_STRIDE;
            int stride = Mf.GetUINT32(cur, ref strideKey, out var s) >= 0 ? (int)s : (int)w * 4;

            int frames = 0; long last = 0; Bitmap? first = null;
            while (true)
            {
                ScreenRecorder.Check(ReadSample(reader, FirstVideo, out var flags, out var ts, out var sample), "reading a frame");
                if (sample != IntPtr.Zero)
                {
                    frames++; last = ts;
                    if (first == null) first = ToBitmap(sample, (int)w, (int)h, stride);
                    Mf.Release(sample);
                }
                if ((flags & 0x2) != 0) break;   // MF_SOURCE_READERF_ENDOFSTREAM
            }
            return new Info((int)w, (int)h, frames, TimeSpan.FromTicks(last), first);
        }
        finally
        {
            Mf.ReleaseAndClear(ref attrs); Mf.ReleaseAndClear(ref want); Mf.ReleaseAndClear(ref cur); Mf.ReleaseAndClear(ref reader);
        }
    }

    private static Bitmap ToBitmap(IntPtr sample, int w, int h, int stride)
    {
        ScreenRecorder.Check(Mf.ConvertToContiguousBuffer(sample, out var buf), "reading the frame's pixels");
        try
        {
            ScreenRecorder.Check(Mf.Lock(buf, out var src, out _, out _), "locking the frame");
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            // A negative stride means the rows are stored bottom first.
            for (int y = 0; y < h; y++)
            {
                byte* row = stride >= 0 ? (byte*)src + (long)y * stride : (byte*)src + (long)(h - 1 - y) * -stride;
                Buffer.MemoryCopy(row, (byte*)d.Scan0 + (long)y * d.Stride, w * 4, w * 4);
            }
            bmp.UnlockBits(d);
            Mf.Unlock(buf);
            return bmp;
        }
        finally { Mf.Release(buf); }
    }

    private const uint FirstVideo = 0xFFFFFFFC;   // MF_SOURCE_READER_FIRST_VIDEO_STREAM
    private static Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attrs, out IntPtr reader);

    // ---- IMFSourceReader : IUnknown ------------------------------------------------------------
    // IUnknown 0-2, then GetStreamSelection(3), SetStreamSelection(4), GetNativeMediaType(5),
    // GetCurrentMediaType(6), SetCurrentMediaType(7), SetCurrentPosition(8), ReadSample(9),
    // Flush(10), GetServiceForStream(11), GetPresentationAttribute(12).
    private static void** V(IntPtr o) => *(void***)o;
    private static int GetCurrentMediaType(IntPtr r, uint stream, out IntPtr type)
    { fixed (IntPtr* p = &type) return ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(r)[6])(r, stream, p); }
    private static int SetCurrentMediaType(IntPtr r, uint stream, IntPtr type) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)V(r)[7])(r, stream, null, type);
    private static int ReadSample(IntPtr r, uint stream, out uint flags, out long ts, out IntPtr sample)
    {
        uint actual;
        fixed (uint* pf = &flags) fixed (long* pt = &ts) fixed (IntPtr* ps = &sample)
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)V(r)[9])(r, stream, 0, &actual, pf, pt, ps);
    }
}
