using System.Runtime.InteropServices;

namespace RemoteSupport.Media;

/// <summary>
/// One Media Foundation transform, driven the way both kinds want to be driven.
///
/// This exists because of a fork in the road that is easy to miss and impossible to work around
/// later: a SOFTWARE MFT is a plain function -- push a sample in, pull a sample out. A HARDWARE
/// MFT is asynchronous and refuses to be used that way. It raises METransformNeedInput when it is
/// ready for a frame and METransformHaveOutput when it has one, and calling ProcessInput without
/// being asked returns MF_E_NOTACCEPTING. Since the whole point of stage 2.2 is to reach Quick
/// Sync, NVENC and AMD VCE, the asynchronous shape is the one that matters, and the synchronous
/// one is what the fallbacks use.
///
/// So both live here behind <see cref="TryInput"/> and <see cref="TryOutput"/>, and the caller --
/// the encoder, the converter, the decoder -- never asks which it got.
///
/// One consequence worth stating: an async transform can refuse a frame, and that is not a
/// failure. It means the encoder is still busy with the last one, and the right answer is to drop
/// the frame rather than to queue it. A queued frame on a remote-support link is a frame the
/// technician sees late, which is worse than one they never see.
/// </summary>
internal sealed unsafe class MfTransform : IDisposable
{
    public IntPtr Handle { get; private set; }
    public string Name { get; }
    public bool Async { get; }
    /// <summary>ICodecAPI, when the transform has one. Only encoders do.</summary>
    public IntPtr CodecApi { get; private set; }
    /// <summary>The transform allocates its own output samples, so we must not allocate any.</summary>
    public bool OutputProvidesSamples { get; private set; }

    private IntPtr _events;             // IMFMediaEventGenerator, async only
    private int _needInput;             // input slots the transform has offered and we have not used
    private int _haveOutput;            // outputs it has announced and we have not collected
    private bool _streaming;

    // ---- counters, for when nothing comes out ----------------------------------------------
    // An asynchronous transform that produces nothing has several distinct causes that all look
    // identical from outside: it never asked for input, it asked and we never fed it, it took the
    // frames and never announced an output, or it announced one and ProcessOutput refused. These
    // separate them, and they cost nothing to keep.
    public int EventsSeen { get; private set; }
    public int UnknownEvents { get; private set; }
    public uint LastEventType { get; private set; }
    public int LastGetEventHr { get; private set; }
    public int NeedInputSeen { get; private set; }
    public int HaveOutputSeen { get; private set; }
    public int InputsAccepted { get; private set; }
    public int InputsRefused { get; private set; }
    public int OutputsTaken { get; private set; }
    public int LastInputHr { get; private set; }
    public int LastOutputHr { get; private set; }

    /// <summary>One line naming which of those four states this transform is actually in.</summary>
    public string Diagnostics =>
        $"{Name}: {(Async ? "asynchronous" : "synchronous")}, unlocked {Unlocked} ({Mf.Explain(UnlockHr)}), " +
        $"events {EventsSeen} (needInput {NeedInputSeen}, haveOutput {HaveOutputSeen}, other {UnknownEvents}, " +
        $"last type {LastEventType}, last GetEvent {Mf.Explain(LastGetEventHr)}), " +
        $"inputs accepted {InputsAccepted} refused {InputsRefused}, outputs taken {OutputsTaken}, " +
        $"providesSamples {OutputProvidesSamples}, outputSize {OutputSize}, " +
        $"beginStreaming {Mf.Explain(BeginHr)}, startOfStream {Mf.Explain(StartHr)}, " +
        $"last ProcessInput {Mf.Explain(LastInputHr)}, last ProcessOutput {Mf.Explain(LastOutputHr)}";

    private MfTransform(IntPtr mft, string name, bool async, IntPtr events, IntPtr codecApi)
    {
        Handle = mft; Name = name; Async = async; _events = events; CodecApi = codecApi;
    }

    /// <summary>
    /// Activates a transform from its MFTEnumEx activation object. Returns null rather than
    /// throwing when the transform will not start: enumeration routinely lists transforms that
    /// then refuse to activate, and the caller's job is to try the next one.
    /// </summary>
    public static MfTransform? TryActivate(IntPtr activate, out string why)
    {
        why = "";
        string name = Mf.FriendlyName(activate);
        int hr = Mf.ActivateObject(activate, ref Mf.IID_IMFTransform, out IntPtr mft);
        if (hr != Mf.S_OK || mft == IntPtr.Zero) { why = $"{name}: would not activate ({Mf.Explain(hr)})"; return null; }

        bool async = false, unlocked = false;
        int unlockHr = Mf.S_OK;
        if (Mf.GetAttributes(mft, out IntPtr attrs) == Mf.S_OK && attrs != IntPtr.Zero)
        {
            try
            {
                async = Mf.GetUINT32(attrs, ref Mf.MF_TRANSFORM_ASYNC, out uint a) == Mf.S_OK && a != 0;
                // An async MFT arrives LOCKED and every call fails until this is set. It is not a
                // licence check and there is no alternative to it; the flag exists so that an
                // application which does not understand the asynchronous model cannot half-drive
                // one and produce garbage.
                if (async)
                {
                    unlockHr = Mf.SetUINT32(attrs, ref Mf.MF_TRANSFORM_ASYNC_UNLOCK, 1);
                    // Read back rather than trust the return. A locked async MFT accepts every
                    // later call and reports success to every message, and then never raises an
                    // event -- which is indistinguishable from a broken encoder.
                    unlocked = Mf.GetUINT32(attrs, ref Mf.MF_TRANSFORM_ASYNC_UNLOCK, out uint u) == Mf.S_OK && u != 0;
                }
                // Tell the encoder we would rather have the frame now than have it smaller. This
                // also switches most encoders off B-frames, which is what makes one frame in mean
                // one frame out -- see the comment on Encode.
                Mf.SetUINT32(attrs, ref Mf.MF_LOW_LATENCY, 1);
            }
            finally { Mf.Release(attrs); }
        }

        IntPtr events = IntPtr.Zero;
        if (async && (Mf.QueryInterface(mft, ref Mf.IID_IMFMediaEventGenerator, out events) != Mf.S_OK))
            events = IntPtr.Zero;
        if (async && events == IntPtr.Zero)
        {
            Mf.Release(mft);
            why = $"{name}: says it is asynchronous but has no event generator";
            return null;
        }

        IntPtr codec = IntPtr.Zero;
        if (Mf.QueryInterface(mft, ref Mf.IID_ICodecAPI, out codec) != Mf.S_OK) codec = IntPtr.Zero;

        return new MfTransform(mft, name, async, events, codec) { UnlockHr = unlockHr, Unlocked = unlocked };
    }

    public int SetD3DManager(IntPtr manager) =>
        Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_SET_D3D_MANAGER, manager);

    /// <summary>
    /// Reads the output stream's allocation rules and starts the transform. Must be called after
    /// the media types are set: the answer to "do you allocate your own samples" changes with the
    /// output type, and asking too early gets the wrong one.
    /// </summary>
    public void BeginStreaming()
    {
        if (Mf.GetOutputStreamInfo(Handle, 0, out var info) == Mf.S_OK)
            OutputProvidesSamples = (info.Flags & (Mf.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | Mf.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        OutputSize = info.Size;
        BeginHr = Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
        StartHr = Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        _streaming = true;
    }

    /// <summary>What the two start-streaming messages returned. A transform that refused one of
    /// them will never ask for input, and says nothing else about it.</summary>
    public int UnlockHr { get; init; }
    public bool Unlocked { get; init; }
    public int BeginHr { get; private set; }
    public int StartHr { get; private set; }

    /// <summary>How big an output buffer the transform wants when it does not allocate its own.</summary>
    public uint OutputSize { get; private set; }

    /// <summary>
    /// Drains the event queue without blocking, counting the transform's requests. Safe and cheap
    /// to call as often as you like; a synchronous transform has no queue and this does nothing.
    /// </summary>
    public void Pump()
    {
        if (!Async || _events == IntPtr.Zero) return;
        while (true)
        {
            int hr = Mf.GetEvent(_events, Mf.MF_EVENT_FLAG_NO_WAIT, out IntPtr ev);
            LastGetEventHr = hr;
            if (hr != Mf.S_OK || ev == IntPtr.Zero) return;
            // Counted before the type is read, and the raw type kept. "No events arrived" and
            // "events arrived and their type could not be read" are the same two zeroes otherwise,
            // and they are a missing message versus a wrong vtable slot -- which is not a
            // distinction to make by staring at the code.
            EventsSeen++;
            try
            {
                if (Mf.GetEventType(ev, out uint type) != Mf.S_OK) continue;
                LastEventType = type;
                if (type == Mf.METransformNeedInput) { _needInput++; NeedInputSeen++; }
                else if (type == Mf.METransformHaveOutput) { _haveOutput++; HaveOutputSeen++; }
                else UnknownEvents++;
            }
            finally { Mf.Release(ev); }
        }
    }

    /// <summary>Pumps until the transform has announced an output, or the budget runs out.</summary>
    public bool WaitForOutput(int milliseconds)
    {
        if (!Async) return true;
        var until = Environment.TickCount64 + milliseconds;
        while (true)
        {
            Pump();
            if (_haveOutput > 0) return true;
            if (Environment.TickCount64 >= until) return false;
            Thread.Sleep(1);
        }
    }

    /// <summary>Whether the transform will take a frame right now. Always true for a synchronous one.</summary>
    public bool WantsInput { get { Pump(); return !Async || _needInput > 0; } }

    /// <summary>
    /// Feeds one sample. False means the transform is busy, which is backpressure rather than a
    /// fault -- see the class comment on why a refused frame is dropped and not queued.
    /// </summary>
    public bool TryInput(IntPtr sample, out int hr)
    {
        hr = Mf.S_OK;
        Pump();
        if (Async && _needInput <= 0) { InputsRefused++; return false; }
        hr = Mf.ProcessInput(Handle, 0, sample, 0);
        LastInputHr = hr;
        if (hr == Mf.MF_E_NOTACCEPTING) { InputsRefused++; return false; }
        if (Async && hr == Mf.S_OK) _needInput--;
        if (hr == Mf.S_OK) InputsAccepted++; else InputsRefused++;
        return hr == Mf.S_OK;
    }

    /// <summary>
    /// Collects one output sample if there is one. The caller owns the returned sample and must
    /// Release it.
    ///
    /// <paramref name="formatChanged"/> is the case that looks like an error and is not: a decoder
    /// discovers the picture size from the bitstream itself, so the FIRST thing a freshly started
    /// H.264 decoder does with real data is tell you that the output type you set is wrong. The
    /// caller re-negotiates and asks again.
    /// </summary>
    public bool TryOutput(out IntPtr sample, out bool formatChanged, out int hr)
    {
        sample = IntPtr.Zero; formatChanged = false; hr = Mf.S_OK;
        Pump();
        if (Async && _haveOutput <= 0) return false;

        IntPtr allocated = IntPtr.Zero;
        if (!OutputProvidesSamples)
        {
            if (Mf.MFCreateSample(out allocated) != Mf.S_OK) return false;
            uint size = Math.Max(OutputSize, 4096);
            if (Mf.MFCreateMemoryBuffer(size, out IntPtr buf) != Mf.S_OK) { Mf.Release(allocated); return false; }
            Mf.AddBuffer(allocated, buf);
            Mf.Release(buf);
        }

        var db = new Mf.MFT_OUTPUT_DATA_BUFFER { StreamID = 0, Sample = allocated, Status = 0, Events = IntPtr.Zero };
        hr = Mf.ProcessOutput(Handle, 0, 1, &db, out _);
        LastOutputHr = hr;
        if (hr == Mf.S_OK) OutputsTaken++;
        if (Async && (hr == Mf.S_OK || hr == Mf.MF_E_TRANSFORM_STREAM_CHANGE)) _haveOutput--;
        if (db.Events != IntPtr.Zero) Mf.Release(db.Events);

        if (hr == Mf.MF_E_TRANSFORM_STREAM_CHANGE)
        {
            formatChanged = true;
            if (db.Sample != IntPtr.Zero && db.Sample != allocated) Mf.Release(db.Sample);
            if (allocated != IntPtr.Zero) Mf.Release(allocated);
            return false;
        }
        if (hr != Mf.S_OK || db.Sample == IntPtr.Zero)
        {
            if (allocated != IntPtr.Zero) Mf.Release(allocated);
            return false;
        }
        sample = db.Sample;
        return true;
    }

    /// <summary>
    /// Throws away everything in flight. Used when the picture is about to jump -- a monitor
    /// switch, a desktop switch, a resolution change -- because the frames already inside the
    /// encoder belong to the old picture and arriving late is how a torn composite of two desktops
    /// gets onto a technician's screen.
    /// </summary>
    public void Flush()
    {
        Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero);
        _needInput = 0; _haveOutput = 0;
        if (_streaming) Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        if (_streaming)
        {
            Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero);
            Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
            Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
            // Hardware encoders hold a GPU context until the D3D manager is taken back. Leaving it
            // attached is how a machine ends a session with the encoder still reserved and the
            // next session finding it in use.
            Mf.ProcessMessage(Handle, Mf.MFT_MESSAGE_SET_D3D_MANAGER, IntPtr.Zero);
            _streaming = false;
        }
        Mf.ReleaseAndClear(ref _events);
        var codec = CodecApi; Mf.ReleaseAndClear(ref codec); CodecApi = IntPtr.Zero;
        var h = Handle; Mf.ReleaseAndClear(ref h); Handle = IntPtr.Zero;
    }

    // ---- media type helpers ----------------------------------------------------------------------

    /// <summary>A fresh video media type with the major type already set. Caller releases it.</summary>
    public static IntPtr NewVideoType(Guid subtype)
    {
        if (Mf.MFCreateMediaType(out IntPtr t) != Mf.S_OK || t == IntPtr.Zero)
            throw new NotSupportedException("Media Foundation would not create a media type");
        Mf.SetGUID(t, ref Mf.MF_MT_MAJOR_TYPE, ref Mf.MFMediaType_Video);
        Mf.SetGUID(t, ref Mf.MF_MT_SUBTYPE, ref subtype);
        Mf.SetUINT32(t, ref Mf.MF_MT_INTERLACE_MODE, Mf.MFVideoInterlace_Progressive);
        Mf.SetPair(t, ref Mf.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        return t;
    }

    /// <summary>Wraps a D3D11 texture as an MF sample without copying it. Caller releases the sample.</summary>
    public static IntPtr WrapTexture(IntPtr texture, long timeHns, long durationHns)
    {
        int hr = Mf.MFCreateDXGISurfaceBuffer(ref Mf.IID_ID3D11Texture2D, texture, 0, false, out IntPtr buffer);
        if (hr != Mf.S_OK || buffer == IntPtr.Zero)
            throw new NotSupportedException($"the frame texture could not be wrapped for the encoder: {Mf.Explain(hr)}");
        try
        {
            // A DXGI surface buffer starts with a current length of zero, and a transform handed a
            // zero-length buffer quietly does nothing. The true length is the 2D buffer's own
            // contiguous size.
            if (Mf.QueryInterface(buffer, ref Mf.IID_IMF2DBuffer, out IntPtr b2d) == Mf.S_OK && b2d != IntPtr.Zero)
            {
                try { if (Mf.GetContiguousLength(b2d, out uint len) == Mf.S_OK) Mf.SetCurrentLength(buffer, len); }
                finally { Mf.Release(b2d); }
            }
            if (Mf.MFCreateSample(out IntPtr sample) != Mf.S_OK || sample == IntPtr.Zero)
                throw new NotSupportedException("Media Foundation would not create a sample");
            Mf.AddBuffer(sample, buffer);
            Mf.SetSampleTime(sample, timeHns);
            Mf.SetSampleDuration(sample, durationHns);
            return sample;
        }
        finally { Mf.Release(buffer); }
    }

    /// <summary>Copies bytes into a fresh CPU-memory sample. Caller releases it.</summary>
    public static IntPtr WrapBytes(ReadOnlySpan<byte> data, long timeHns, long durationHns)
    {
        if (Mf.MFCreateMemoryBuffer((uint)data.Length, out IntPtr buffer) != Mf.S_OK || buffer == IntPtr.Zero)
            throw new NotSupportedException("Media Foundation would not create a memory buffer");
        try
        {
            if (Mf.Lock(buffer, out IntPtr p, out _, out _) != Mf.S_OK)
                throw new NotSupportedException("a memory buffer could not be locked");
            try { data.CopyTo(new Span<byte>((void*)p, data.Length)); }
            finally { Mf.Unlock(buffer); }
            Mf.SetCurrentLength(buffer, (uint)data.Length);

            if (Mf.MFCreateSample(out IntPtr sample) != Mf.S_OK || sample == IntPtr.Zero)
                throw new NotSupportedException("Media Foundation would not create a sample");
            Mf.AddBuffer(sample, buffer);
            Mf.SetSampleTime(sample, timeHns);
            Mf.SetSampleDuration(sample, durationHns);
            return sample;
        }
        finally { Mf.Release(buffer); }
    }

    /// <summary>Everything in a sample, copied out as bytes.</summary>
    public static byte[] ReadSample(IntPtr sample)
    {
        if (Mf.ConvertToContiguousBuffer(sample, out IntPtr buf) != Mf.S_OK || buf == IntPtr.Zero) return Array.Empty<byte>();
        try
        {
            if (Mf.Lock(buf, out IntPtr p, out _, out uint len) != Mf.S_OK) return Array.Empty<byte>();
            try
            {
                var bytes = new byte[len];
                Marshal.Copy(p, bytes, 0, (int)len);
                return bytes;
            }
            finally { Mf.Unlock(buf); }
        }
        finally { Mf.Release(buf); }
    }
}
