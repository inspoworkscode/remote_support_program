namespace RemoteSupport.Media;

/// <summary>
/// BGRA to NV12, on the graphics card, for the encoders that will not take a desktop frame as it
/// comes.
///
/// H.264 is defined on YUV, so somewhere between the duplicated desktop and the bitstream a colour
/// conversion has to happen. Some encoders do it themselves and take BGRA straight in; the rest
/// need it done first, and doing it on the CPU would mean a full-frame read back and a full-frame
/// write forward, per frame -- which is the cost stage 2.1 spent a session removing. So it is done
/// by the same graphics card that captured the frame, by Windows' own video processor, and the
/// pixels never leave the card.
///
/// It is a separate class from <see cref="H264Encoder"/> for a reason that turned out to matter on
/// the first machine this ran on: that machine has no H.264 encoder at all, and every line of this
/// -- the device manager, the transform plumbing, the media types, the colour conversion itself --
/// can still be run and measured there. A converter buried inside the encoder could not have been.
/// </summary>
public sealed class GpuColourConverter : IDisposable
{
    public string Name => _mft.Name;
    /// <summary>See <see cref="MfTransform.Diagnostics"/>.</summary>
    public string Diagnostics => _mft.Diagnostics;
    public int Width { get; }
    public int Height { get; }

    private readonly MfTransform _mft;

    private GpuColourConverter(MfTransform mft, int width, int height) { _mft = mft; Width = width; Height = height; }

    /// <summary>
    /// Builds a converter on a device, or throws <see cref="NotSupportedException"/> saying which
    /// candidates were tried and what each of them refused.
    /// </summary>
    public static GpuColourConverter Create(MediaDevice device, int width, int height, int fps)
    {
        var candidates = Mf.Enumerate(Mf.MFT_CATEGORY_VIDEO_PROCESSOR,
            Mf.MFT_ENUM_FLAG_HARDWARE | Mf.MFT_ENUM_FLAG_SYNCMFT | Mf.MFT_ENUM_FLAG_ASYNCMFT | Mf.MFT_ENUM_FLAG_LOCALMFT | Mf.MFT_ENUM_FLAG_SORTANDFILTER,
            Mf.MFMediaType_Video, Mf.MFVideoFormat_ARGB32, Mf.MFMediaType_Video, Mf.MFVideoFormat_NV12);
        if (candidates.Count == 0)
            throw new NotSupportedException(Mf.LastEnumResult == Mf.S_OK
                ? "this machine has no video processor that turns BGRA into NV12"
                : $"the video processor enumeration itself failed: {Mf.Explain(Mf.LastEnumResult)}");

        var refused = new List<string>();
        try
        {
            foreach (var activate in candidates)
            {
                var mft = MfTransform.TryActivate(activate, out string why);
                if (mft == null) { refused.Add(why); continue; }
                try
                {
                    if (mft.SetD3DManager(device.Manager) != Mf.S_OK)
                        throw new NotSupportedException("it refused our Direct3D device");
                    SetType(mft, Mf.MFVideoFormat_ARGB32, width, height, fps, input: true);
                    SetType(mft, Mf.MFVideoFormat_NV12, width, height, fps, input: false);
                    mft.BeginStreaming();
                    // If it will not allocate its own output we would have to allocate system
                    // memory and upload the result, which is the copy through the CPU this whole
                    // class exists to avoid. Refusing it lets the session fall back to JPEG tiles,
                    // where at least the cost is honest and already understood.
                    if (!mft.OutputProvidesSamples)
                        throw new NotSupportedException("it wants us to allocate its output, which would mean a copy through the CPU every frame");
                    return new GpuColourConverter(mft, width, height);
                }
                catch (Exception ex) { refused.Add($"{mft.Name}: {ex.Message}"); mft.Dispose(); }
            }
        }
        finally { foreach (var a in candidates) Mf.Release(a); }
        throw new NotSupportedException("no video processor would take this screen - " + string.Join("; ", refused));
    }

    private static void SetType(MfTransform mft, Guid subtype, int width, int height, int fps, bool input)
    {
        IntPtr t = MfTransform.NewVideoType(subtype);
        try
        {
            Mf.SetPair(t, ref Mf.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            Mf.SetPair(t, ref Mf.MF_MT_FRAME_RATE, (uint)fps, 1);
            int hr = input ? Mf.SetInputType(mft.Handle, 0, t, 0) : Mf.SetOutputType(mft.Handle, 0, t, 0);
            if (hr != Mf.S_OK)
                throw new NotSupportedException($"it would not {(input ? "take BGRA" : "produce NV12")} at {width}x{height} ({Mf.Explain(hr)})");
        }
        finally { Mf.Release(t); }
    }

    /// <summary>
    /// Converts one BGRA sample. The returned NV12 sample belongs to the CALLER, who must release
    /// it. <see cref="IntPtr.Zero"/> means the transform was busy or had nothing to give, which is
    /// backpressure rather than a fault — see <see cref="MfTransform"/>.
    /// </summary>
    public IntPtr Convert(IntPtr bgraSample, int waitMs = 200)
    {
        if (!_mft.TryInput(bgraSample, out _)) return IntPtr.Zero;
        if (!_mft.WaitForOutput(waitMs)) return IntPtr.Zero;
        // Ask for output until the transform says it wants more input, not just once. Windows'
        // Video Processor MFT does not count a frame as finished until ProcessOutput has answered
        // MF_E_TRANSFORM_NEED_MORE_INPUT, and until then it refuses the next frame as busy. Asked
        // once, frames were refused (on Windows 11 here: the first taken, every one after refused),
        // and a refused frame on a screen that then goes still is never replaced — so a program
        // opening or closing showed on the console only after Refresh. Reported on DESKTOP-STD8M9C
        // (Windows 10, the only test machine whose encoder needs this conversion), 2026-10-06;
        // `codecbench --tail --nv12` FAILS with a single ask and passes with this loop.
        // Bounded, and only the newest kept: one frame in is one frame out on this transform.
        IntPtr newest = IntPtr.Zero;
        for (int i = 0; i < 4 && _mft.TryOutput(out IntPtr nv12, out _, out _); i++)
        {
            if (newest != IntPtr.Zero) Mf.Release(newest);
            newest = nv12;
        }
        return newest;
    }

    public void Flush() => _mft.Flush();
    public void Dispose() => _mft.Dispose();
}
