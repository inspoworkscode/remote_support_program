namespace RemoteSupport.Media;

/// <summary>
/// The console's end of stage 2.2: H.264 in, BGRA out, on the technician's machine.
///
/// It uses the SOFTWARE decoder on purpose. A hardware decoder would need a Direct3D device, a
/// device manager and textures that then have to come back to the CPU anyway, because the console
/// draws its canvas with GDI+ -- so the hardware path would buy nothing and would add a second set
/// of machine-dependent failure modes to a component whose job is to always work. Decoding
/// 1920x1200 at twelve frames a second costs a few milliseconds a frame on the sort of machine a
/// technician sits at.
///
/// TWO THINGS HERE ARE NOT OPTIONAL AND BOTH LOOK LIKE THEY MIGHT BE.
///
/// A decoder must start at a key frame. Fed a predicted frame first it does not error -- it
/// produces a picture built from a reference frame it never saw, which is a smear of blocks that
/// slowly corrects itself over several seconds and looks exactly like a bad connection. So
/// everything before the first key frame is dropped, deliberately and counted.
///
/// And the first real data makes the decoder announce that the output type is wrong. That is not a
/// failure: the picture size lives in the bitstream, not in anything we told it, so the decoder
/// only learns the truth when it parses the sequence parameter set. The answer is to re-negotiate
/// the output type and ask again, which is what <see cref="TryDecode"/> does.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    /// <summary>Which decoder is running, for the console's trace.</summary>
    public string Name { get; private set; } = "";
    /// <summary>The picture size the bitstream actually declared, which may not be what was asked for.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>The most recent decoded frame as BGRA, valid until the next <see cref="TryDecode"/>.</summary>
    public byte[] Bgra { get; private set; } = Array.Empty<byte>();
    public int Stride { get; private set; }

    /// <summary>Packets thrown away because no key frame had arrived yet. See the class comment.</summary>
    public int DroppedBeforeKey { get; private set; }
    public int Decoded { get; private set; }
    public int Failures { get; private set; }
    public string LastError { get; private set; } = "";

    private readonly MfTransform _mft;
    private bool _started;
    private long _frame;
    private readonly long _frameDuration;

    public H264Decoder(int width, int height, int fps = 12)
    {
        Mf.Startup();
        Width = width + (width & 1);
        Height = height + (height & 1);
        _frameDuration = 10_000_000L / Math.Clamp(fps, 1, 60);

        // Not MFT_ENUM_FLAG_HARDWARE: see the class comment. LOCALMFT is included so that a
        // decoder registered only for this process is still found, which is what a future
        // self-contained build would need.
        var candidates = Mf.Enumerate(Mf.MFT_CATEGORY_VIDEO_DECODER,
            Mf.MFT_ENUM_FLAG_SYNCMFT | Mf.MFT_ENUM_FLAG_LOCALMFT | Mf.MFT_ENUM_FLAG_SORTANDFILTER,
            Mf.MFMediaType_Video, Mf.MFVideoFormat_H264, Mf.MFMediaType_Video, Mf.MFVideoFormat_NV12);
        if (candidates.Count == 0)
            throw new NotSupportedException("this machine has no H.264 decoder that Media Foundation can reach");

        var refused = new List<string>();
        MfTransform? chosen = null;
        try
        {
            foreach (var activate in candidates)
            {
                if (chosen != null) continue;
                var mft = MfTransform.TryActivate(activate, out string why);
                if (mft == null) { refused.Add(why); continue; }
                try { Configure(mft); chosen = mft; Name = mft.Name; }
                catch (Exception ex) { refused.Add($"{mft.Name}: {ex.Message}"); mft.Dispose(); }
            }
        }
        finally { foreach (var a in candidates) Mf.Release(a); }

        _mft = chosen ?? throw new NotSupportedException("no H.264 decoder would take this stream - " + string.Join("; ", refused));
        Resize(Width, Height);
    }

    private void Configure(MfTransform mft)
    {
        // Input first for a decoder -- the mirror image of the encoder, and for the same reason:
        // what it can produce depends on what it is being given.
        IntPtr inType = MfTransform.NewVideoType(Mf.MFVideoFormat_H264);
        try
        {
            Mf.SetPair(inType, ref Mf.MF_MT_FRAME_SIZE, (uint)Width, (uint)Height);
            Mf.SetPair(inType, ref Mf.MF_MT_FRAME_RATE, 30, 1);
            int hr = Mf.SetInputType(mft.Handle, 0, inType, 0);
            if (hr != Mf.S_OK) throw new NotSupportedException($"it would not take H.264 ({Mf.Explain(hr)})");
        }
        finally { Mf.Release(inType); }

        if (mft.CodecApi != IntPtr.Zero)
            Mf.CodecSetBool(mft.CodecApi, ref Mf.CODECAPI_AVLowLatencyMode, true);

        NegotiateOutput(mft);
        mft.BeginStreaming();
    }

    /// <summary>
    /// Picks the first NV12 output type the decoder offers and takes its frame size as the truth.
    ///
    /// Called again whenever the decoder says the format changed, which is guaranteed to happen
    /// once on the first real frame and can happen again if the customer changes resolution
    /// mid-session.
    /// </summary>
    private void NegotiateOutput(MfTransform mft)
    {
        for (uint i = 0; ; i++)
        {
            int hr = Mf.GetOutputAvailableType(mft.Handle, 0, i, out IntPtr t);
            if (hr != Mf.S_OK || t == IntPtr.Zero) break;
            try
            {
                if (Mf.GetGUID(t, ref Mf.MF_MT_SUBTYPE, out Guid sub) != Mf.S_OK || sub != Mf.MFVideoFormat_NV12) continue;
                if (Mf.SetOutputType(mft.Handle, 0, t, 0) != Mf.S_OK) continue;
                var (w, h) = Mf.GetPair(t, ref Mf.MF_MT_FRAME_SIZE);
                if (w > 0 && h > 0) Resize((int)w, (int)h);
                return;
            }
            finally { Mf.Release(t); }
        }
        throw new NotSupportedException("it offered no NV12 output");
    }

    private void Resize(int w, int h)
    {
        if (Width == w && Height == h && Bgra.Length > 0) return;
        Width = w; Height = h;
        Stride = w * 4;
        Bgra = new byte[(long)Stride * h <= int.MaxValue ? Stride * h : 0];
        if (Bgra.Length == 0) throw new NotSupportedException($"a {w}x{h} frame is too large to hold");
    }

    /// <summary>
    /// Decodes one packet from the wire. True means <see cref="Bgra"/> now holds a new picture.
    ///
    /// False is the ordinary case as often as it is the unusual one: a packet before the first key
    /// frame, or a packet the decoder swallowed without producing anything yet.
    /// </summary>
    public bool TryDecode(ReadOnlySpan<byte> packet, bool keyFrame)
    {
        if (!_started)
        {
            if (!keyFrame) { DroppedBeforeKey++; return false; }
            _started = true;
        }
        if (packet.Length == 0) return false;

        try
        {
            IntPtr sample = MfTransform.WrapBytes(packet, _frame * _frameDuration, _frameDuration);
            try
            {
                if (!_mft.TryInput(sample, out int hr) && hr != Mf.S_OK)
                { Failures++; LastError = $"the decoder refused a packet: {Mf.Explain(hr)}"; return false; }
                _frame++;
            }
            finally { Mf.Release(sample); }

            // Twice round at most: once for the frame, once more after a format change has been
            // dealt with. A third attempt would mean the decoder is renegotiating on every frame,
            // which is a fault rather than something to loop over.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_mft.TryOutput(out IntPtr outSample, out bool changed, out int hr))
                {
                    try { return Take(outSample); }
                    finally { Mf.Release(outSample); }
                }
                if (!changed) return false;
                NegotiateOutput(_mft);
            }
            return false;
        }
        catch (Exception ex) { Failures++; LastError = ex.Message; return false; }
    }

    /// <summary>Turns one decoded NV12 sample into the BGRA buffer the console draws from.</summary>
    private bool Take(IntPtr sample)
    {
        if (Mf.GetBufferByIndex(sample, 0, out IntPtr buf) != Mf.S_OK || buf == IntPtr.Zero) return false;
        try
        {
            // IMF2DBuffer when the buffer has it. A decoded NV12 buffer is usually NOT contiguous
            // -- the chroma plane starts at a stride-aligned offset, not immediately after the luma
            // -- and Lock() papers over that by linearising the whole frame into a scratch copy.
            // That is a full frame memcpy per frame, for nothing.
            IntPtr b2d = IntPtr.Zero;
            if (Mf.QueryInterface(buf, ref Mf.IID_IMF2DBuffer, out b2d) == Mf.S_OK && b2d != IntPtr.Zero)
            {
                try
                {
                    if (Mf.Lock2D(b2d, out IntPtr scan0, out int pitch) != Mf.S_OK || scan0 == IntPtr.Zero) return false;
                    try { Convert((byte*)scan0, pitch); }
                    finally { Mf.Unlock2D(b2d); }
                    Decoded++;
                    return true;
                }
                finally { Mf.Release(b2d); }
            }

            if (Mf.Lock(buf, out IntPtr p, out _, out _) != Mf.S_OK || p == IntPtr.Zero) return false;
            try { Convert((byte*)p, Width); }
            finally { Mf.Unlock(buf); }
            Decoded++;
            return true;
        }
        finally { Mf.Release(buf); }
    }

    /// <summary>
    /// Which YUV matrix to read the decoded frame with, overriding the size-based convention.
    ///
    /// It exists for `codecbench --h264check`, which decodes the same frame both ways and prints
    /// the colour error of each. Choosing a matrix by convention is a guess, and a wrong guess
    /// here does not break the picture, it tints it — which nobody reports as a bug. The override
    /// is what turns the guess into a measurement.
    /// </summary>
    public bool? Matrix709 { get; set; }

    private void Convert(byte* nv12, int pitch)
    {
        // The chroma plane sits one luma plane below the top of the buffer, at the same pitch.
        byte* uv = nv12 + (long)pitch * Height;
        fixed (byte* dst = Bgra)
            Nv12.ToBgra(nv12, pitch, uv, pitch, dst, Stride, Width, Height, Matrix709 ?? Nv12.Is709(Width, Height));
    }

    /// <summary>
    /// Forgets everything, so the next key frame starts a clean picture. Used when the session
    /// reconnects or the technician switches monitor: the frames the decoder is holding belong to
    /// a stream that no longer exists.
    /// </summary>
    public void Reset()
    {
        _mft.Flush();
        _started = false;
        _frame = 0;
    }

    public void Dispose() => _mft.Dispose();
}
