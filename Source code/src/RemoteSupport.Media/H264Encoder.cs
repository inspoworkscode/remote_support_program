using System.Runtime.InteropServices;

namespace RemoteSupport.Media;

/// <summary>One encoded frame, and whether a viewer with no picture could start from it.</summary>
public readonly record struct VideoPacket(byte[] Data, bool KeyFrame);

/// <summary>
/// H.264 encoding of a Desktop Duplication frame, on the GPU, through Media Foundation --
/// Intel Quick Sync, NVENC or AMD VCE depending on the machine, with the Microsoft software
/// encoder as the last resort.
///
/// WHY THIS EXISTS. Stage 2.1 made capture fast and deliberately did not touch bandwidth: a
/// scrolling document cost 2.8 MB/s as JPEG tiles, which is about 22 Mbit/s of customer uplink and
/// is not a number any South African ADSL or LTE line can produce. The tiles were never the
/// problem; JPEG knowing nothing about the previous frame was. H.264 does, and that is the whole
/// of the difference.
///
/// THE FRAME NEVER TOUCHES THE CPU. The duplicated desktop arrives as a D3D11 texture, is wrapped
/// in an MF sample in place, converted to NV12 by the GPU if the encoder insists on NV12, and
/// handed to the encoder on the same device. The only thing that crosses back to us is the
/// compressed bitstream. The JPEG path's staging texture and readback exist because encoding a
/// tile requires the bytes; nothing here does, and re-introducing a readback would give back most
/// of what 2.1 won.
///
/// WHAT IS NEGOTIATED AND WHAT IS NOT. Construction throws <see cref="NotSupportedException"/>
/// when this machine has no usable encoder, which is the signal for the caller to keep sending
/// JPEG tiles. That is not a rare path to be tidied away later: a VM with no GPU, a server core
/// install without the media feature pack, and a machine whose encoder is already held by another
/// application all land there.
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    /// <summary>Which encoder is running, for the trace line and the console's Info tab.</summary>
    public string Name { get; private set; } = "";
    /// <summary>True when the frame goes straight into the encoder without a colour conversion pass.</summary>
    public bool DirectBgra { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int Bitrate { get; }
    /// <summary>Frames the encoder refused because it was still busy with the one before.</summary>
    public int Dropped { get; private set; }

    /// <summary>
    /// What the transforms have actually been doing, for when nothing comes out. An encoder that
    /// produces no bitstream is silent about which of four different things went wrong, and this
    /// is what separates them.
    /// </summary>
    public string Diagnostics =>
        $"encoder {_encode.Diagnostics}" + (_convert == null ? "" : $"\n  converter {_convert.Diagnostics}");

    private readonly MediaDevice _device;
    private GpuColourConverter? _convert;   // null when the encoder takes the desktop frame as it comes
    private MfTransform _encode;
    private byte[] _sequenceHeader = Array.Empty<byte>();
    private long _frame;
    private readonly long _frameDuration;

    /// <summary>
    /// What to spend on a screen of this size at this frame rate, from the technician's existing
    /// quality slider.
    ///
    /// The slider is labelled for JPEG and runs 20..95, and it stays that way on purpose: stage
    /// 2.3 is where quality stops being a slider at all, and inventing a second control now would
    /// be a control to remove later. The curve is squared so the bottom half of the slider buys
    /// real savings -- which is the half a technician on a bad line actually uses.
    /// </summary>
    public static int BitrateFor(int quality, int width, int height, int fps)
    {
        double q = Math.Clamp((quality - 20) / 75.0, 0, 1);
        double mbitsAt1080p12 = 0.4 + q * q * 5.6;                 // 0.4 .. 6.0
        double scale = (double)width * height / (1920.0 * 1080.0) * (Math.Max(1, fps) / 12.0);
        return (int)Math.Clamp(mbitsAt1080p12 * 1e6 * scale, 200_000, 20_000_000);
    }

    /// <summary>
    /// Builds an encoder for one monitor on the device that duplicates it.
    ///
    /// The device must be the SAME one the frames come from. A texture cannot cross devices
    /// without a shared-handle dance, and doing that per frame would cost more than the encode.
    /// </summary>
    /// <summary>
    /// Only consider encoders whose name contains this. Empty means take the best the machine
    /// offers, which is what the product does; the bench uses it to try each one in turn and say
    /// which of them actually work, because "no encoder produced anything" and "the FIRST encoder
    /// produced nothing" are very different findings.
    /// </summary>
    private readonly string _nameFilter;

    /// <summary>For the bench only: take the colour-conversion path even on an encoder that would take
    /// BGRA, so a machine that never needs it can still exercise it.</summary>
    public static bool ForceColourConversion { get; set; }

    /// <summary>
    /// The automatic key frame's spacing, in seconds' worth of frames at the encoder's rate — FRAMES,
    /// not time: frames are only encoded when the screen changes, so on a quiet screen the old "2
    /// seconds" (24 frames) came minutes apart, each one knocking a sharpened picture back below the
    /// first one a technician saw. Measured by codecbench --sharp --gop, 2026-10-15: 46.9 dB down to
    /// 36.1 at the key frame with 2; a steady climb to 48.4 with 60, for 16% fewer bytes (HANDOVER
    /// §142). It was 2 to guarantee a picture to a viewer whose request for one was ignored; the host
    /// now repeats that request until a key frame comes (SendVideo), so it no longer needs to be.
    /// The bench moves it to compare; the product does not.
    /// </summary>
    public static int KeyFrameSeconds { get; set; } = 60;

    public H264Encoder(IntPtr d3dDevice, int width, int height, int fps, int quality, string nameFilter = "")
    {
        _nameFilter = nameFilter;
        Mf.Startup();
        // Even width and height. H.264 is defined on macroblocks and every encoder here refuses an
        // odd dimension outright; the console crops back to the monitor's real size, which is why
        // the wire carries both.
        Width = width + (width & 1);
        Height = height + (height & 1);
        Fps = Math.Clamp(fps, 1, 60);
        Bitrate = BitrateFor(quality, Width, Height, Fps);
        CurrentBitrate = Bitrate;
        _frameDuration = 10_000_000L / Fps;

        _device = new MediaDevice(d3dDevice);
        try { _encode = BuildEncoder(); }
        catch { _device.Dispose(); throw; }
    }

    private MfTransform BuildEncoder()
    {
        // Filtering the enumeration on NV12 in and H.264 out finds every encoder on the machine:
        // NV12 is the one input format they all take, and whether a particular one will ALSO take
        // BGRA is decided further down by asking it, not by guessing from the registration.
        var candidates = Mf.Enumerate(Mf.MFT_CATEGORY_VIDEO_ENCODER,
            Mf.MFT_ENUM_FLAG_HARDWARE | Mf.MFT_ENUM_FLAG_ASYNCMFT | Mf.MFT_ENUM_FLAG_SYNCMFT | Mf.MFT_ENUM_FLAG_SORTANDFILTER,
            Mf.MFMediaType_Video, Mf.MFVideoFormat_NV12, Mf.MFMediaType_Video, Mf.MFVideoFormat_H264);

        if (candidates.Count == 0)
            throw new NotSupportedException(Mf.LastEnumResult == Mf.S_OK
                ? "this machine has no H.264 encoder that Media Foundation can reach"
                : $"the encoder enumeration itself failed: {Mf.Explain(Mf.LastEnumResult)}");

        var refused = new List<string>();
        try
        {
            foreach (var activate in candidates)
            {
                if (_nameFilter.Length > 0 && !Mf.FriendlyName(activate).Contains(_nameFilter, StringComparison.OrdinalIgnoreCase)) continue;
                var mft = MfTransform.TryActivate(activate, out string why);
                if (mft == null) { refused.Add(why); continue; }
                try
                {
                    Configure(mft);
                    Name = $"{mft.Name}{(DirectBgra ? "" : " with a GPU colour conversion")}";
                    return mft;
                }
                catch (Exception ex)
                {
                    refused.Add($"{mft.Name}: {ex.Message}");
                    _convert?.Dispose(); _convert = null;
                    mft.Dispose();
                }
            }
        }
        finally { foreach (var a in candidates) Mf.Release(a); }

        throw new NotSupportedException("no H.264 encoder would take this screen - " + string.Join("; ", refused));
    }

    /// <summary>
    /// Sets the types and the rate control on one candidate encoder. Throws if it will not have
    /// them, which is the caller's signal to try the next candidate.
    /// </summary>
    private void Configure(MfTransform mft)
    {
        // Hand over the Direct3D device before anything else. A hardware encoder that has not been
        // given one will still accept media types and will then reject every textured sample with
        // an error about the surface, which reads like a bug in the sample and is not.
        bool d3dAware = false;
        if (Mf.GetAttributes(mft.Handle, out IntPtr attrs) == Mf.S_OK && attrs != IntPtr.Zero)
        {
            try { d3dAware = Mf.GetUINT32(attrs, ref Mf.MF_SA_D3D11_AWARE, out uint v) == Mf.S_OK && v != 0; }
            finally { Mf.Release(attrs); }
        }
        if (!d3dAware)
            throw new NotSupportedException("it cannot take frames from the graphics card, so using it would mean copying every frame back to the CPU");
        int hr = mft.SetD3DManager(_device.Manager);
        if (hr != Mf.S_OK) throw new NotSupportedException($"it refused our Direct3D device ({Mf.Explain(hr)})");

        // Rate control BEFORE the output type. The encoders differ about which of the two wins
        // when they disagree, and setting it in this order is the one that is consistent across
        // Intel, NVIDIA and the software encoder.
        if (mft.CodecApi != IntPtr.Zero)
        {
            // Constant bit rate, not quality-targeted. On a support link the thing that must not
            // vary is the bytes per second, because that is the constraint; the picture getting
            // softer for a moment during a scroll is exactly the trade a technician wants.
            Mf.CodecSetUInt32(mft.CodecApi, ref Mf.CODECAPI_AVEncCommonRateControlMode, Mf.eAVEncCommonRateControlMode_CBR);
            Mf.CodecSetUInt32(mft.CodecApi, ref Mf.CODECAPI_AVEncCommonMeanBitRate, (uint)Bitrate);
            Mf.CodecSetBool(mft.CodecApi, ref Mf.CODECAPI_AVLowLatencyMode, true);
            // A key frame now and then independently of anything asking for one: the floor under
            // a viewer whose explicit request for one is ignored by this encoder. Rarely, because
            // at a constant bit rate every key frame is a soft picture that throws away the
            // sharpening the frames before it built up. See KeyFrameSeconds.
            Mf.CodecSetUInt32(mft.CodecApi, ref Mf.CODECAPI_AVEncMPVGOPSize, (uint)(Fps * KeyFrameSeconds));
        }

        // Output first. Every H.264 encoder MFT requires the output type to be set before it will
        // even enumerate its input types, because what it can take depends on what it must produce.
        IntPtr outType = MfTransform.NewVideoType(Mf.MFVideoFormat_H264);
        try
        {
            Mf.SetPair(outType, ref Mf.MF_MT_FRAME_SIZE, (uint)Width, (uint)Height);
            Mf.SetPair(outType, ref Mf.MF_MT_FRAME_RATE, (uint)Fps, 1);
            Mf.SetUINT32(outType, ref Mf.MF_MT_AVG_BITRATE, (uint)Bitrate);
            // Main, not High. High buys a few percent on desktop content and is the profile most
            // likely to be missing from a decoder on an older machine, and the console has to
            // decode this on whatever the technician is sitting at.
            Mf.SetUINT32(outType, ref Mf.MF_MT_MPEG2_PROFILE, Mf.eAVEncH264VProfile_Main);
            hr = Mf.SetOutputType(mft.Handle, 0, outType, 0);
            if (hr != Mf.S_OK) throw new NotSupportedException($"it would not produce H.264 at {Width}x{Height} ({Mf.Explain(hr)})");
        }
        finally { Mf.Release(outType); }

        // Input. BGRA straight in when the encoder takes it -- that is one whole GPU pass saved
        // per frame -- and NV12 through a video processor when it does not. Which one this machine
        // gets is settled by asking, because it varies by vendor and by driver version, and a
        // wrong guess here is an encoder that produces a green picture rather than an error.
        DirectBgra = !ForceColourConversion && TrySetInput(mft, Mf.MFVideoFormat_ARGB32);
        if (!DirectBgra)
        {
            if (!TrySetInput(mft, Mf.MFVideoFormat_NV12))
                throw new NotSupportedException("it would take neither BGRA nor NV12 at this size");
            _convert = GpuColourConverter.Create(_device, Width, Height, Fps);
        }

        mft.BeginStreaming();
        CaptureSequenceHeader(mft);
    }

    /// <summary>How many times the encoder has re-stated its output type. One on the first frame
    /// is normal and expected; a number that keeps climbing is a fault.</summary>
    public int StreamChanges { get; private set; }

    /// <summary>The bitrate actually in force, which the controller moves at runtime.</summary>
    public int CurrentBitrate { get; private set; }

    /// <summary>
    /// Moves the bitrate WITHOUT rebuilding the encoder, which is what makes stage 2.3's
    /// controller affordable here at all.
    ///
    /// Rate control is configured when an encoder is built, so the original code marked the codec
    /// dirty and rebuilt on every quality change. That is fine for a slider a human drags twice a
    /// session and ruinous for a controller that nudges twice a second -- a rebuild costs a key
    /// frame and a stall, on the congested link that caused the nudge.
    ///
    /// `ICodecAPI` takes it live on every encoder that implements it. False means this one will
    /// not, and the caller has to decide whether a rebuild is worth it; it must NOT assume the
    /// bitrate changed, which is why the answer is returned rather than swallowed.
    /// </summary>
    public bool TrySetBitrate(int bitsPerSecond)
    {
        if (_encode.CodecApi == IntPtr.Zero) return false;
        int want = Math.Clamp(bitsPerSecond, 100_000, 20_000_000);
        if (want == CurrentBitrate) return true;
        if (Mf.CodecSetUInt32(_encode.CodecApi, ref Mf.CODECAPI_AVEncCommonMeanBitRate, (uint)want) != Mf.S_OK) return false;
        CurrentBitrate = want;
        return true;
    }

    /// <summary>
    /// Takes the output type the encoder has settled on and sets it back, which is what
    /// MF_E_TRANSFORM_STREAM_CHANGE is asking for. Index 0 is the encoder's own first preference
    /// -- it has just told us the one it wants, so there is nothing to choose between.
    /// </summary>
    private bool RenegotiateOutput()
    {
        if (Mf.GetOutputAvailableType(_encode.Handle, 0, 0, out IntPtr t) != Mf.S_OK || t == IntPtr.Zero) return false;
        try { return Mf.SetOutputType(_encode.Handle, 0, t, 0) == Mf.S_OK; }
        finally { Mf.Release(t); }
    }

    private bool TrySetInput(MfTransform mft, Guid subtype)
    {
        IntPtr t = MfTransform.NewVideoType(subtype);
        try
        {
            Mf.SetPair(t, ref Mf.MF_MT_FRAME_SIZE, (uint)Width, (uint)Height);
            Mf.SetPair(t, ref Mf.MF_MT_FRAME_RATE, (uint)Fps, 1);
            return Mf.SetInputType(mft.Handle, 0, t, 0) == Mf.S_OK;
        }
        finally { Mf.Release(t); }
    }


    /// <summary>
    /// Keeps the SPS and PPS the encoder settled on.
    ///
    /// A decoder cannot start without them. Most encoders put them in front of every key frame in
    /// the bitstream, which makes this redundant -- but "most" is not a thing to rely on when the
    /// symptom of being wrong is a console that shows black against a stream that is arriving
    /// perfectly, and the cost of carrying them is forty bytes on the frames that already cost the
    /// most.
    /// </summary>
    private void CaptureSequenceHeader(MfTransform mft)
    {
        if (Mf.GetOutputCurrentType(mft.Handle, 0, out IntPtr t) != Mf.S_OK || t == IntPtr.Zero) return;
        try
        {
            if (Mf.GetBlobSize(t, ref Mf.MF_MT_MPEG_SEQUENCE_HEADER, out uint size) != Mf.S_OK || size == 0 || size > 4096) return;
            var blob = new byte[size];
            fixed (byte* p = blob)
                if (Mf.GetBlob(t, ref Mf.MF_MT_MPEG_SEQUENCE_HEADER, p, size, out uint written) == Mf.S_OK && written > 0)
                    _sequenceHeader = blob[..(int)written];
        }
        finally { Mf.Release(t); }
    }

    /// <summary>
    /// Encodes one duplicated frame and returns whatever bitstream came out -- usually one packet,
    /// occasionally none, occasionally two.
    ///
    /// It is NOT one frame in, one frame out, and code that assumes otherwise breaks on real
    /// hardware. An asynchronous encoder is a pipeline: it may hold this frame and hand back the
    /// previous one, it may refuse this frame because it is still busy (which is counted in
    /// <see cref="Dropped"/> and is correct behaviour, not a fault), and it may have nothing at
    /// all to say. Low-latency mode keeps that pipeline one frame deep, which is why a short
    /// bounded wait for the output is reasonable rather than a guess.
    ///
    /// <paramref name="forceKey"/> is the forced-full frame of the JPEG path, and it carries the
    /// same obligation: a viewer with no picture is waiting, and a stream of predicted frames is
    /// worth nothing to them.
    /// </summary>
    public List<VideoPacket> Encode(IntPtr bgraTexture, bool forceKey)
    {
        var packets = new List<VideoPacket>();
        long time = _frame * _frameDuration;

        if (forceKey && _encode.CodecApi != IntPtr.Zero)
            Mf.CodecSetUInt32(_encode.CodecApi, ref Mf.CODECAPI_AVEncVideoForceKeyFrame, 1);

        IntPtr input = MfTransform.WrapTexture(bgraTexture, time, _frameDuration);
        try
        {
            // The colour conversion, when this machine needs one. Its output sample is a GPU
            // texture on the same device, so it goes straight on to the encoder.
            if (_convert != null)
            {
                IntPtr nv12 = _convert.Convert(input);
                if (nv12 == IntPtr.Zero) { Dropped++; return packets; }
                Mf.Release(input);
                input = nv12;
            }

            if (!_encode.TryInput(input, out _)) { Dropped++; }
            _frame++;
        }
        finally { Mf.Release(input); }

        // Collect everything the encoder is ready to give. The wait is bounded by a frame budget:
        // beyond that the frame is late enough that the next one is a better use of the link.
        _encode.WaitForOutput(Math.Max(5, 1000 / Fps));
        // A hardware encoder settles its output type on the FIRST frame and says so by failing
        // that first ProcessOutput with MF_E_TRANSFORM_STREAM_CHANGE. It is not an error and it is
        // not optional: until the type is read back and set again, the encoder has announced an
        // output it will not hand over, and every frame after it is refused. The old loop declared
        // this flag and never read it, so the very first frame was dropped and the encoder sat
        // there for the life of the session with one output nobody would collect.
        //
        // Bounded, because "renegotiate and try again" is a shape that spins for ever the day an
        // encoder reports a change it will not then accept.
        for (int renegotiations = 0; ; )
        {
            if (_encode.TryOutput(out IntPtr outSample, out bool changed, out _))
            {
                try
                {
                    bool key = Mf.GetUINT32(outSample, ref Mf.MFSampleExtension_CleanPoint, out uint cp) == Mf.S_OK && cp != 0;
                    var bytes = MfTransform.ReadSample(outSample);
                    if (bytes.Length > 0) packets.Add(new VideoPacket(WithSequenceHeader(bytes, key), key));
                }
                finally { Mf.Release(outSample); }
                continue;
            }
            if (!changed || ++renegotiations > 3) break;
            if (!RenegotiateOutput()) break;
            // The SPS and PPS belong to the type that just changed, so the copy kept for stamping
            // on key frames is now the wrong one.
            CaptureSequenceHeader(_encode);
            StreamChanges++;
        }
        return packets;
    }

    /// <summary>
    /// Puts the SPS and PPS in front of a key frame that does not already carry them.
    ///
    /// The test is on the first NAL unit's type rather than on a byte pattern: 7 is a sequence
    /// parameter set, and a key frame that opens with one needs nothing from us.
    /// </summary>
    private byte[] WithSequenceHeader(byte[] frame, bool key)
    {
        if (!key || _sequenceHeader.Length == 0 || StartsWithSps(frame)) return frame;
        var joined = new byte[_sequenceHeader.Length + frame.Length];
        _sequenceHeader.CopyTo(joined, 0);
        frame.CopyTo(joined, _sequenceHeader.Length);
        return joined;
    }

    private static bool StartsWithSps(byte[] f)
    {
        // Annex B: a start code of 00 00 01 or 00 00 00 01, then one byte whose low five bits are
        // the NAL unit type.
        for (int i = 0; i + 4 < f.Length && i < 8; i++)
        {
            if (f[i] != 0 || f[i + 1] != 0) continue;
            if (f[i + 2] == 1) return (f[i + 3] & 0x1F) == 7;
            if (f[i + 2] == 0 && f[i + 3] == 1 && i + 4 < f.Length) return (f[i + 4] & 0x1F) == 7;
        }
        return false;
    }

    /// <summary>
    /// Throws away everything in flight, for when the picture is about to jump. The caller must
    /// follow it with a forced key frame: after a flush the encoder's idea of the previous frame
    /// and the console's no longer agree.
    /// </summary>
    public void Reset()
    {
        _convert?.Flush();
        _encode.Flush();
    }

    public void Dispose()
    {
        _encode.Dispose();
        _convert?.Dispose();
        _device.Dispose();
    }
}
