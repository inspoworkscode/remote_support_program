using System.Drawing;
using System.Drawing.Imaging;
using RemoteSupport.Host;
using RemoteSupport.Media;

namespace CodecBench;

/// <summary>
/// The H.264 half of the bench: stage 2.2 measured on the same scenes, at the same frame rate,
/// against the same screen as the two tile paths, so the rows sit in one table and the comparison
/// is arithmetic rather than recollection.
///
/// It does NOT go through IScreenSource, and that is deliberate. That interface's whole vocabulary
/// is tiles, and an H.264 source has none: it produces one bitstream per frame and its whole point
/// is that the frame never comes back to the CPU to be cut up. Bending it to fit would have meant
/// either a fake tile or a readback, and the readback is the thing being removed. What has to
/// match between the paths is the MEASUREMENT -- bytes on the wire, milliseconds a frame, frames a
/// second -- and that is what <see cref="Program.Row"/> holds.
///
/// The check below matters more than the numbers. Stage 2.1 ended with a lesson that cost three
/// rounds: a harness which cannot distinguish two states will confidently report the wrong one,
/// and "frames arrived" never meant "the picture is right". For a codec that is even easier to get
/// wrong, because a stream can decode perfectly and still be the wrong colour -- so this decodes
/// what it encoded and compares it to the screen.
/// </summary>
public static class Video
{
    /// <summary>One bandwidth-and-speed pass, filling the same Row the tile paths do.</summary>
    internal static Program.Row Pass(SceneKind cond, bool live, int monitor, int quality, bool capped, int capFps, int seconds)
    {
        var r = new Program.Row
        {
            Source = "h264",
            Condition = live ? "live" : cond.ToString().ToLowerInvariant(),
            Capped = capped, CapFps = capFps,
        };
        var done = new ManualResetEventSlim();

        // A dedicated thread, for the same reasons as Program.Pass: desktop attachment is a
        // per-thread property, and GetThreadTimes then measures the capture and encode and nothing
        // else. Here it matters more, not less -- a hardware encoder does its work on the GPU and
        // on its own threads, so the CPU figure in this row is the cost to US, which is the
        // honest thing to compare against a JPEG path that does all of its work on this thread.
        var t = new Thread(() =>
        {
            IntPtr me = Program.GetCurrentThread();
            DxgiCapture? cap = null;
            H264Encoder? enc = null;
            try
            {
                try { cap = new DxgiCapture(); cap.Refresh(); }
                catch (Exception ex) { r.Error = $"Desktop Duplication is unavailable: {ex.Message}"; return; }

                var mon = cap.Monitors.FirstOrDefault(m => m.Index == monitor);
                if (mon == null) { r.Error = $"no monitor with index {monitor}"; return; }

                // A frame on the card before the encoder is built around its device -- the same
                // order the host uses, and for the same reason.
                if (!cap.TryAcquireGpuFrame(monitor, true, out var first) || first.Device == IntPtr.Zero)
                { r.Error = cap.LastError.Length > 0 ? cap.LastError : "no frame came off the graphics card"; return; }

                try { enc = new H264Encoder(first.Device, mon.Width, mon.Height, capFps, quality); }
                catch (Exception ex) { r.Error = ex.Message; return; }
                r.Note = $"{enc.Name}, {enc.Bitrate / 1000} kbit/s asked for";

                double warmup = Math.Min(1.5, seconds / 3.0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool first_ = true, counting = false;
                long k0 = 0, u0 = 0;
                double startedAt = 0;

                while (true)
                {
                    double now = sw.Elapsed.TotalSeconds;
                    if (!counting && now >= warmup) { counting = true; startedAt = now; Program.GetThreadTimes(me, out _, out _, out k0, out u0); }
                    if (counting && now - startedAt >= seconds) break;

                    long frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    long bytes = 0; int packets = 0; double grab = 0, encode = 0;

                    if (cap.TryAcquireGpuFrame(monitor, first_, out var frame))
                    {
                        grab = cap.LastTiming.GrabMs;
                        long te = System.Diagnostics.Stopwatch.GetTimestamp();
                        foreach (var p in enc.Encode(frame.Texture, first_))
                        {
                            // Exactly what HostEngine puts on the socket, header and all, so the
                            // KB/s column means the same thing it does for the tile rows.
                            bytes += RemoteSupport.Shared.Bin.VideoHeader + p.Data.Length;
                            packets++;
                        }
                        encode = Program.Ms(te);
                    }
                    else grab = cap.LastTiming.GrabMs;

                    if (first_) { r.FirstFrameBytes = bytes; r.FirstFrameMs = Program.Ms(frameStart); first_ = false; }
                    else if (counting)
                    {
                        r.Polls++;
                        r.Bytes += bytes;
                        r.GrabMs += grab; r.EncodeMs += encode;
                        r.FrameMs += Program.Ms(frameStart);
                        if (packets > 0) r.Frames++;
                    }

                    if (capped)
                    {
                        int wait = 1000 / capFps - (int)Program.Ms(frameStart);
                        if (wait > 0) Thread.Sleep(wait);
                    }
                }
                r.Elapsed = sw.Elapsed.TotalSeconds - startedAt;
                Program.GetThreadTimes(me, out _, out _, out long k1, out long u1);
                r.CpuMs = ((k1 - k0) + (u1 - u0)) / 10000.0;
                if (enc.Dropped > 0) r.Note += $"; {enc.Dropped} frames the encoder was too busy for";
            }
            finally
            {
                enc?.Dispose();
                cap?.Dispose();
                done.Set();
            }
        });
        t.IsBackground = true;
        t.Start();
        done.Wait();
        t.Join(4000);
        return r;
    }

    /// <summary>
    /// Encode one frame, decode it again, and compare it to what is actually on the screen.
    ///
    /// This is the codec's version of `--compare`, and it exists because of the specific way this
    /// work can be wrong. Media Foundation is driven here through raw vtable slots, and a wrong
    /// slot does not throw; a wrong colour matrix does not throw either. Both produce a stream that
    /// arrives, decodes, and is not the customer's screen. The only test that catches them is to
    /// look at the pixels, so both matrices are tried and both errors printed -- the one that is
    /// clearly lower is the right one, and if neither is low, something else is wrong and the PNGs
    /// are there to say what.
    /// </summary>
    public static int Check(int monitor, int quality, Rectangle bounds, string outDir, string encoderFilter = "")
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("Encoding one frame, decoding it again, and comparing it to the screen.");
        Console.WriteLine();

        // A still scene, and a COLOURED one. Two grabs milliseconds apart on a moving scene would
        // differ by the animation rather than by the codec -- and the text scene, which was the
        // first choice here, is black ink on white paper. That is grey, and on grey BT.601 and
        // BT.709 produce identical pixels: the check ran, compared both matrices, got the same
        // number for each and reported a pass without having tested anything. See SceneKind.Colour.
        // difference between them would be the animation rather than the codec -- which is a
        // harness that cannot distinguish two states, and therefore worse than no harness.
        using var scene = SceneHost.Show(SceneKind.Colour, bounds);
        Thread.Sleep(700);
        if (!scene.Covering)
            Console.WriteLine("  WARNING: the bench scene is not the window on screen, so this is measuring something else.");

        DxgiCapture? cap = null;
        H264Encoder? enc = null;
        H264Decoder? dec = null;
        try
        {
            try { cap = new DxgiCapture(); cap.Refresh(); }
            catch (Exception ex) { Console.WriteLine($"Desktop Duplication is unavailable: {ex.Message}"); return 1; }
            var mon = cap.Monitors.FirstOrDefault(m => m.Index == monitor);
            if (mon == null) { Console.WriteLine($"no monitor with index {monitor}"); return 1; }

            // Let real duplicated frames land first. Straight after construction the on-card
            // buffer holds the GDI priming copy, and comparing that to a GDI grab would pass
            // perfectly while proving nothing about the duplication -- the same trap Compare.Run
            // records.
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (settle.Elapsed.TotalSeconds < 1.0) { cap.TryAcquireGpuFrame(monitor, false, out _); Thread.Sleep(15); }

            if (!cap.TryAcquireGpuFrame(monitor, true, out var frame))
            { Console.WriteLine($"no frame came off the graphics card: {cap.LastError}"); return 1; }

            try { enc = new H264Encoder(frame.Device, mon.Width, mon.Height, 12, quality, encoderFilter); }
            catch (Exception ex) { Console.WriteLine($"no encoder on this machine: {ex.Message}"); return 1; }
            Console.WriteLine($"  encoder: {enc.Name}");
            Console.WriteLine($"  {enc.Width}x{enc.Height}, {enc.Bitrate / 1000} kbit/s{(enc.DirectBgra ? ", taking BGRA directly" : ", through a GPU colour conversion")}");

            // Ask until a key frame comes out. An encoder is a pipeline and the first frame in is
            // not always the first frame out, so one attempt proves nothing either way.
            VideoPacket? key = null;
            for (int i = 0; i < 20 && key == null; i++)
            {
                cap.TryAcquireGpuFrame(monitor, true, out frame);
                foreach (var p in enc.Encode(frame.Texture, true)) if (p.KeyFrame) { key = p; break; }
                Thread.Sleep(30);
            }
            if (key == null)
            {
                Console.WriteLine("  FAIL  the encoder never produced a key frame");
                Console.WriteLine($"        {enc.Diagnostics}");
                Console.WriteLine($"        frames the encoder was too busy for: {enc.Dropped}");
                return 1;
            }
            Console.WriteLine($"  key frame: {key.Value.Data.Length / 1024.0:0.0} KB for a whole {mon.Width}x{mon.Height} screen");

            // The screen as GDI sees it, composed the way ScreenView composes tiles. This is the
            // reference: a second, independent route to the same pixels.
            using var reference = ComposeWithGdi(monitor, mon.Width, mon.Height, 95);
            if (reference == null) { Console.WriteLine("  FAIL  the GDI reference grab produced nothing"); return 1; }
            reference.Save(Path.Combine(outDir, "h264-reference.png"), ImageFormat.Png);

            try { dec = new H264Decoder(enc.Width, enc.Height); }
            catch (Exception ex) { Console.WriteLine($"  FAIL  no decoder on this machine: {ex.Message}"); return 1; }
            Console.WriteLine($"  decoder: {dec.Name}");

            int fails = 0;
            double best = double.MaxValue; bool bestIs709 = false;
            foreach (bool is709 in new[] { false, true })
            {
                dec.Reset();
                dec.Matrix709 = is709;
                if (!dec.TryDecode(key.Value.Data, true))
                { Console.WriteLine($"  FAIL  the decoder produced nothing from the key frame ({dec.LastError})"); fails++; continue; }

                using var decoded = ToBitmap(dec);
                decoded.Save(Path.Combine(outDir, is709 ? "h264-decoded-bt709.png" : "h264-decoded-bt601.png"), ImageFormat.Png);
                double err = MeanAbsoluteDifference(reference, decoded);
                Console.WriteLine($"  {(is709 ? "BT.709" : "BT.601")}: mean difference from the screen {err:0.0} of 255");
                if (err < best) { best = err; bestIs709 = is709; }
            }

            Console.WriteLine();
            // The threshold is not tight, and should not be: this is lossy compression at a
            // support-session bitrate compared against a JPEG-95 reference of the same screen, so
            // a few units of mean difference is the expected answer. What it rules out is the
            // interesting failure -- a picture shifted, channel-swapped, torn or blank -- which
            // reads as tens.
            if (best <= 12)
                Console.WriteLine($"  PASS  the decoded picture matches the screen ({best:0.0} of 255) using {(bestIs709 ? "BT.709" : "BT.601")}.");
            else
            {
                Console.WriteLine($"  FAIL  the decoded picture does not match the screen (best {best:0.0} of 255).");
                Console.WriteLine($"        Look at the PNGs in {outDir} before changing any code: a tint is a colour matrix,");
                Console.WriteLine("        a shift or a swap is a vtable slot, and a blank is the encoder never seeing the frame.");
                fails++;
            }
            bool convention = Nv12.Is709(enc.Width, enc.Height);
            if (bestIs709 != convention)
                Console.WriteLine($"  NOTE  Nv12.Is709 says {convention} for {enc.Width}x{enc.Height} and the measurement says {bestIs709}. One of them is wrong.");
            Console.WriteLine($"  pictures written to {outDir}");
            return fails == 0 ? 0 : 1;
        }
        finally { dec?.Dispose(); enc?.Dispose(); cap?.Dispose(); }
    }

    /// <summary>
    /// Everything in the H.264 path EXCEPT the encoder, proved on a machine that has no encoder.
    ///
    /// That is not a contrived case. The laptop this was written on is an Intel N-series, whose
    /// graphics have video decode and no video encode at all, and Windows on it registers no
    /// software H.264 encoder either -- so `--h264check` cannot run here and never will. Left at
    /// that, the device manager, the transform plumbing, the media types, the texture wrapping and
    /// the colour conversion would all have gone to a customer machine completely unexercised.
    ///
    /// They are all in the colour converter's path, so this drives the converter alone: a real
    /// duplicated frame in, NV12 out on the card, read back HERE (which the product never does)
    /// and compared to the screen. What it cannot prove is the encoder and the decoder; what it
    /// can prove is that everything they stand on works.
    /// </summary>
    public static unsafe int PipeCheck(int monitor, Rectangle bounds, string outDir)
    {
        Directory.CreateDirectory(outDir);
        Console.WriteLine("Taking a frame through the GPU colour conversion and comparing it to the screen.");
        Console.WriteLine();

        using var scene = SceneHost.Show(SceneKind.Colour, bounds);
        Thread.Sleep(700);
        if (!scene.Covering)
            Console.WriteLine("  WARNING: the bench scene is not the window on screen, so this is measuring something else.");

        DxgiCapture? cap = null;
        MediaDevice? dev = null;
        GpuColourConverter? conv = null;
        try
        {
            try { cap = new DxgiCapture(); cap.Refresh(); }
            catch (Exception ex) { Console.WriteLine($"Desktop Duplication is unavailable: {ex.Message}"); return 1; }
            var mon = cap.Monitors.FirstOrDefault(m => m.Index == monitor);
            if (mon == null) { Console.WriteLine($"no monitor with index {monitor}"); return 1; }

            // Real duplicated frames first, so this is not comparing the GDI priming copy against
            // a GDI grab and passing while proving nothing -- the trap Compare.Run records.
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (settle.Elapsed.TotalSeconds < 1.0) { cap.TryAcquireGpuFrame(monitor, false, out _); Thread.Sleep(15); }
            if (!cap.TryAcquireGpuFrame(monitor, true, out var frame))
            { Console.WriteLine($"no frame came off the graphics card: {cap.LastError}"); return 1; }

            int w = mon.Width + (mon.Width & 1), h = mon.Height + (mon.Height & 1);
            try
            {
                dev = new MediaDevice(frame.Device);
                conv = GpuColourConverter.Create(dev, w, h, 12);
            }
            catch (Exception ex) { Console.WriteLine($"  FAIL  no GPU colour conversion on this machine: {ex.Message}"); return 1; }
            Console.WriteLine($"  converter: {conv.Name}  ({w}x{h})");

            IntPtr inSample = MfTransform.WrapTexture(frame.Texture, 0, 10_000_000L / 12);
            IntPtr nv12;
            try { nv12 = conv.Convert(inSample, 1000); }
            finally { Mf.Release(inSample); }
            if (nv12 == IntPtr.Zero) { Console.WriteLine("  FAIL  the converter produced nothing"); return 1; }

            using var reference = ComposeWithGdi(monitor, mon.Width, mon.Height, 95);
            if (reference == null) { Console.WriteLine("  FAIL  the GDI reference grab produced nothing"); return 1; }
            reference.Save(Path.Combine(outDir, "pipe-reference.png"), ImageFormat.Png);

            int fails = 0;
            double best = double.MaxValue; bool bestIs709 = false;
            try
            {
                foreach (bool is709 in new[] { false, true })
                {
                    using var got = Nv12SampleToBitmap(nv12, w, h, is709);
                    if (got == null) { Console.WriteLine("  FAIL  the NV12 frame could not be read"); fails++; break; }
                    got.Save(Path.Combine(outDir, is709 ? "pipe-bt709.png" : "pipe-bt601.png"), ImageFormat.Png);
                    double err = MeanAbsoluteDifference(reference, got);
                    Console.WriteLine($"  {(is709 ? "BT.709" : "BT.601")}: mean difference from the screen {err:0.0} of 255");
                    if (err < best) { best = err; bestIs709 = is709; }
                }
            }
            finally { Mf.Release(nv12); }

            Console.WriteLine();
            // Tighter than the codec's threshold, because nothing here is lossy except the chroma
            // being halved and the JPEG-95 reference. A tint reads as tens; a swapped channel or a
            // misread stride reads as a hundred.
            if (best <= 8)
                Console.WriteLine($"  PASS  the converted picture matches the screen ({best:0.0} of 255) using {(bestIs709 ? "BT.709" : "BT.601")}.");
            else
            {
                Console.WriteLine($"  FAIL  the converted picture does not match the screen (best {best:0.0} of 255).");
                Console.WriteLine($"        Look at the PNGs in {outDir}: a tint is the colour matrix, a shift or a swap is");
                Console.WriteLine("        a vtable slot or a stride, and a blank is the frame never reaching the transform.");
                fails++;
            }
            bool convention = Nv12.Is709(w, h);
            if (bestIs709 != convention)
                Console.WriteLine($"  NOTE  Nv12.Is709 says {convention} for {w}x{h} and the measurement says {bestIs709}. One of them is wrong.");
            Console.WriteLine($"  pictures written to {outDir}");
            return fails == 0 ? 0 : 1;
        }
        finally { conv?.Dispose(); dev?.Dispose(); cap?.Dispose(); }
    }

    /// <summary>
    /// Reads an NV12 sample back to the CPU and converts it. THE PRODUCT NEVER DOES THIS on the
    /// host side — it is the readback stage 2.1 removed and 2.2 must not put back — which is why
    /// it lives in the bench.
    /// </summary>
    private static unsafe Bitmap? Nv12SampleToBitmap(IntPtr sample, int w, int h, bool is709)
    {
        if (Mf.GetBufferByIndex(sample, 0, out IntPtr buf) != Mf.S_OK || buf == IntPtr.Zero) return null;
        try
        {
            IntPtr data; int pitch;
            IntPtr b2d = IntPtr.Zero;
            bool two = Mf.QueryInterface(buf, ref Mf.IID_IMF2DBuffer, out b2d) == Mf.S_OK && b2d != IntPtr.Zero;
            if (two)
            {
                if (Mf.Lock2D(b2d, out data, out pitch) != Mf.S_OK || data == IntPtr.Zero) { Mf.Release(b2d); return null; }
            }
            else
            {
                if (Mf.Lock(buf, out data, out _, out _) != Mf.S_OK || data == IntPtr.Zero) return null;
                pitch = w;
            }
            try
            {
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    byte* y = (byte*)data;
                    Nv12.ToBgra(y, pitch, y + (long)pitch * h, pitch, (byte*)bits.Scan0, bits.Stride, w, h, is709);
                }
                finally { bmp.UnlockBits(bits); }
                return bmp;
            }
            finally
            {
                if (two) { Mf.Unlock2D(b2d); Mf.Release(b2d); } else Mf.Unlock(buf);
            }
        }
        finally { Mf.Release(buf); }
    }

    /// <summary>The same screen through GDI, composed from its tiles, as an independent reference.</summary>
    internal static Bitmap? ComposeWithGdi(int monitor, int width, int height, int quality)
    {
        using var gdi = new ScreenCapture();
        gdi.Refresh();
        var tiles = gdi.CaptureTiles(monitor, quality, true);
        if (tiles.Count == 0) return null;
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        foreach (var t in tiles)
        {
            using var ms = new MemoryStream(t.jpeg);
            using var img = Image.FromStream(ms);
            g.DrawImage(img, new Rectangle(t.x, t.y, t.w, t.h));
        }
        return bmp;
    }

    internal static unsafe Bitmap ToBitmap(H264Decoder dec)
    {
        var bmp = new Bitmap(dec.Width, dec.Height, PixelFormat.Format32bppRgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, dec.Width, dec.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            fixed (byte* src = dec.Bgra)
                for (int y = 0; y < dec.Height; y++)
                    Buffer.MemoryCopy(src + (long)y * dec.Stride, (byte*)bits.Scan0 + (long)y * bits.Stride, dec.Stride, dec.Stride);
        }
        finally { bmp.UnlockBits(bits); }
        return bmp;
    }

    /// <summary>Mean absolute per-channel difference over the overlapping area, in units of 255.</summary>
    internal static unsafe double MeanAbsoluteDifference(Bitmap a, Bitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        var ra = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        var rb = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            long total = 0, n = 0;
            for (int y = 0; y < h; y++)
            {
                byte* pa = (byte*)ra.Scan0 + (long)y * ra.Stride;
                byte* pb = (byte*)rb.Scan0 + (long)y * rb.Stride;
                for (int x = 0; x < w; x++, pa += 4, pb += 4)
                {
                    total += Math.Abs(pa[0] - pb[0]) + Math.Abs(pa[1] - pb[1]) + Math.Abs(pa[2] - pb[2]);
                    n += 3;
                }
            }
            return n == 0 ? 255 : (double)total / n;
        }
        finally { a.UnlockBits(ra); b.UnlockBits(rb); }
    }
}
