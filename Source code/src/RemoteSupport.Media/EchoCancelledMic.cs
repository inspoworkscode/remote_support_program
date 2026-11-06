using System.Runtime.InteropServices;
using RemoteSupport.Shared;

namespace RemoteSupport.Media;

/// <summary>
/// The microphone for a voice call (3.3), either end, with the machine's own speakers taken out of
/// it by Windows' echo canceller — the Voice Capture DSP (CLSID_CWMAudioAEC), part of Windows since
/// Vista, so nothing is bought in.
///
/// WHY THE CANCELLER IS NOT OPTIONAL. Clint chose an open microphone with a Mute button rather than
/// push-to-talk (2026-09-30). The technician's voice comes out of the customer's speakers, their
/// microphone hears it, and without this the technician hears themselves half a second later for
/// the whole call. The same happens the other way on a technician without a headset.
///
/// In its SYSTEM MODE the DSP opens the default communications microphone AND watches the default
/// speakers itself, and hands back the microphone with the speakers taken out: 16 kHz mono 16-bit,
/// which is exactly <see cref="VoiceWire"/>. What micprobe measured before this was written, on
/// this laptop and as SYSTEM on two customer machines:
///
/// - It works as SYSTEM in the customer's session: no helper running as the person is needed.
/// - IT DELIVERS NOTHING UNTIL A RENDER STREAM IS OPEN. ProcessOutput is S_FALSE for ever with no
///   sound playing, and delivers at once when one opens — even one of silence. The call's
///   <see cref="VoicePlayer"/> is that stream, and it must be started FIRST and kept open.
/// - ProcessOutput's array must be marshalled as a C array (LPArray). As a SAFEARRAY — the default
///   on a COM interface — the DSP reads garbage and the process dies of an access violation.
///
/// Runs on its own MTA thread: the DSP is polled every 10 ms, and nothing about it belongs on a UI
/// thread or a socket thread.
/// </summary>
public sealed class EchoCancelledMic : IDisposable
{
    private readonly Action<byte[]> _onFrame;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _run;

    /// <summary>Loudest sample of the latest frame, 0..1 — for a meter, and for the trace to say
    /// whether anything was ever heard.</summary>
    public double Level { get; private set; }
    public string DeviceName { get; private set; } = "";
    public bool Running => _run;
    public int Frames { get; private set; }

    /// <param name="onFrame">Called on the microphone's thread with one frame of
    /// <see cref="VoiceWire.FrameSamples"/> 16-bit samples (640 bytes).</param>
    public EchoCancelledMic(Action<byte[]> onFrame, Action<string>? log = null)
    {
        _onFrame = onFrame;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Opens the microphone. Returns once it is open or has failed, so the caller can say which;
    /// <paramref name="why"/> is a sentence for a person, not an HRESULT.
    /// </summary>
    public bool Start(out string why)
    {
        why = "";
        lock (_lock)
        {
            if (_run) return true;
            string? failure = null;
            // Not disposed: the thread sets it again in its finally, after Start may have returned.
            var opened = new ManualResetEventSlim(false);
            _run = true;
            _thread = new Thread(() =>
            {
                try { Loop(opened, f => failure = f); }
                catch (Exception ex) { failure ??= "the microphone stopped unexpectedly: " + ex.Message; _log("voice mic: thread died — " + ex); }
                finally { _run = false; opened.Set(); }
            })
            { IsBackground = true, Name = "rs-voice-mic" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            // The DSP opens both devices in AllocateStreamingResources, which takes tens of ms.
            opened.Wait(5000);
            if (failure != null || !_run)
            {
                why = failure ?? "the microphone did not open";
                _run = false;
                return false;
            }
            return true;
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_lock) { _run = false; t = _thread; _thread = null; }
        try { t?.Join(2000); } catch { }
        Level = 0;
    }

    public void Dispose() => Stop();

    private void Loop(ManualResetEventSlim opened, Action<string> fail)
    {
        // Is there a microphone at all? Asked first and separately, because "this computer has no
        // microphone" is an answer a technician can act on, and the DSP's own failure for it is a
        // bare HRESULT from AllocateStreamingResources.
        DeviceName = DefaultCaptureName(out int hr);
        if (DeviceName.Length == 0)
        {
            fail(hr == unchecked((int)0x80070490)
                ? "this computer has no microphone"
                : $"no microphone could be found (hr=0x{hr:X8})");
            opened.Set();
            return;
        }

        object? dsp = null;
        IntPtr wfx = IntPtr.Zero, bufPtr = IntPtr.Zero;
        MediaBuffer? buf = null;
        bool allocated = false;
        IMediaObject? mo = null;
        try
        {
            try { dsp = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidAec)!); }
            catch (Exception ex) { fail("Windows' echo canceller is not available on this computer (" + ex.Message + ")"); opened.Set(); return; }

            var props = (IPropertyStore)dsp!;
            var mode = new PropertyKey { fmtid = AecProps, pid = 2 };        // MFPKEY_WMAAECMA_SYSTEM_MODE
            var v = new PropVariant { vt = 3 /* VT_I4 */, i4 = 0 /* SINGLE_CHANNEL_AEC */ };
            hr = props.SetValue(ref mode, ref v);
            if (hr != 0) { fail($"the echo canceller refused its settings (hr=0x{hr:X8})"); opened.Set(); return; }

            mo = (IMediaObject)dsp!;
            wfx = Marshal.AllocCoTaskMem(18);
            Marshal.WriteInt16(wfx, 0, 1);                              // WAVE_FORMAT_PCM
            Marshal.WriteInt16(wfx, 2, 1);                              // mono
            Marshal.WriteInt32(wfx, 4, VoiceWire.Rate);
            Marshal.WriteInt32(wfx, 8, VoiceWire.Rate * 2);
            Marshal.WriteInt16(wfx, 12, 2);
            Marshal.WriteInt16(wfx, 14, 16);
            Marshal.WriteInt16(wfx, 16, 0);
            var mt = new DmoMediaType
            {
                majortype = MediaTypeAudio, subtype = MediaSubtypePcm, bFixedSizeSamples = 1, lSampleSize = 2,
                formattype = FormatWaveFormatEx, cbFormat = 18, pbFormat = wfx,
            };
            hr = mo!.SetOutputType(0, ref mt, 0);
            if (hr != 0) { fail($"the echo canceller refused 16 kHz mono (hr=0x{hr:X8})"); opened.Set(); return; }

            // This is where the DSP opens the microphone and the speakers. A refusal here is the
            // device, the driver or Windows' privacy, and E_ACCESSDENIED says which.
            hr = mo.AllocateStreamingResources();
            if (hr != 0)
            {
                fail(hr == unchecked((int)0x80070005)
                    ? "Windows refused the microphone (its privacy settings)"
                    : $"the microphone \"{DeviceName}\" could not be opened (hr=0x{hr:X8})");
                opened.Set();
                return;
            }
            allocated = true;
            _log($"voice mic: open on \"{DeviceName}\" through the echo canceller, {VoiceWire.Rate} Hz mono");
            opened.Set();

            buf = new MediaBuffer(VoiceWire.Rate * 2);   // a second: far more than one call returns
            bufPtr = Marshal.GetComInterfaceForObject(buf, typeof(IMediaBuffer));
            var outs = new DmoOutputDataBuffer[1];
            var pending = new List<short>(VoiceWire.FrameSamples * 4);
            int quietPolls = 0;

            while (_run)
            {
                outs[0] = new DmoOutputDataBuffer { pBuffer = bufPtr };
                buf.Length = 0;
                hr = mo.ProcessOutput(0, 1, outs, out _);
                if (hr != 0 && hr != 1)
                {
                    _log($"voice mic: ProcessOutput failed hr=0x{hr:X8}");
                    fail($"the microphone stopped (hr=0x{hr:X8})");
                    break;
                }
                if (buf.Length > 0)
                {
                    quietPolls = 0;
                    int n = buf.Length / 2;
                    var samples = new short[n];
                    Marshal.Copy(buf.Data, samples, 0, n);
                    pending.AddRange(samples);
                    while (pending.Count >= VoiceWire.FrameSamples)
                    {
                        var pcm = new byte[VoiceWire.FrameSamples * 2];
                        double peak = 0;
                        for (int i = 0; i < VoiceWire.FrameSamples; i++)
                        {
                            short s = pending[i];
                            pcm[i * 2] = (byte)(s & 0xFF);
                            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                            double a = Math.Abs(s) / 32768.0;
                            if (a > peak) peak = a;
                        }
                        pending.RemoveRange(0, VoiceWire.FrameSamples);
                        Level = peak;
                        Frames++;
                        _onFrame(pcm);
                    }
                    // Incomplete means more is waiting: take it without sleeping.
                    if ((outs[0].dwStatus & DmoOutputIncomplete) != 0) continue;
                }
                else if (++quietPolls == 300)
                {
                    // Three seconds of nothing. The one cause seen is no render stream (see the
                    // class note); say so once in the trace rather than looking like a dead mic.
                    _log("voice mic: nothing from the echo canceller for 3 s — is the call's playback open?");
                }
                Thread.Sleep(10);
            }
        }
        finally
        {
            if (allocated && mo != null) { try { mo.FreeStreamingResources(); } catch { } }
            if (bufPtr != IntPtr.Zero) Marshal.Release(bufPtr);
            buf?.Dispose();
            if (wfx != IntPtr.Zero) Marshal.FreeCoTaskMem(wfx);
            if (dsp != null) { try { Marshal.ReleaseComObject(dsp); } catch { } }
            _log($"voice mic: closed after {Frames} frames");
        }
    }

    /// <summary>The default communications microphone's name, or "" with the HRESULT.</summary>
    private static string DefaultCaptureName(out int hr)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        IPropertyStore? store = null;
        hr = 0;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            hr = en.GetDefaultAudioEndpoint(1 /* eCapture */, 2 /* eCommunications */, out dev);
            if (hr != 0 || dev == null) return "";
            if (dev.OpenPropertyStore(0, out store) != 0 || store == null) return "the microphone";
            var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            if (store.GetValue(ref key, out var pv) != 0 || pv.vt != 31 || pv.p == IntPtr.Zero) return "the microphone";
            return Marshal.PtrToStringUni(pv.p) ?? "the microphone";
        }
        catch (Exception ex) { hr = ex.HResult; return ""; }
        finally
        {
            foreach (var o in new object?[] { store, dev, en })
                if (o != null) { try { Marshal.ReleaseComObject(o); } catch { } }
        }
    }

    // ------------------------------------------------------------------------------ interop
    // Vtable order IS the interface (mediaobj.h, mmdeviceapi.h): do not reorder, do not delete.

    private static readonly Guid ClsidAec = new("745057C7-F353-4F2D-A7EE-58434477730E");
    private static readonly Guid AecProps = new("6f52c567-0360-4bd2-9617-ccbf1421c939");
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MediaSubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatWaveFormatEx = new("05589f81-c356-11ce-bf01-00aa0055595a");
    private const int DmoOutputIncomplete = 0x01000000;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid fmtid; public int pid; }

    // Size = 24 is not optional: a PROPVARIANT is 24 bytes on x64 (AudioInterop says what 16 cost).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public short vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public int i4;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore? store);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DmoMediaType
    {
        public Guid majortype, subtype;
        public int bFixedSizeSamples, bTemporalCompression;
        public uint lSampleSize;
        public Guid formattype;
        public IntPtr pUnk;
        public uint cbFormat;
        public IntPtr pbFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DmoOutputDataBuffer
    {
        public IntPtr pBuffer;
        public int dwStatus;
        public long rtTimestamp, rtTimelength;
    }

    [ComImport, Guid("D8AD0F58-5494-4102-97C5-EC798E59BCF4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMediaObject
    {
        [PreserveSig] int GetStreamCount(out int inputs, out int outputs);
        [PreserveSig] int GetInputStreamInfo(int index, out int flags);
        [PreserveSig] int GetOutputStreamInfo(int index, out int flags);
        [PreserveSig] int GetInputType(int index, int typeIndex, IntPtr mt);
        [PreserveSig] int GetOutputType(int index, int typeIndex, IntPtr mt);
        [PreserveSig] int SetInputType(int index, ref DmoMediaType mt, int flags);
        [PreserveSig] int SetOutputType(int index, ref DmoMediaType mt, int flags);
        [PreserveSig] int GetInputCurrentType(int index, IntPtr mt);
        [PreserveSig] int GetOutputCurrentType(int index, IntPtr mt);
        [PreserveSig] int GetInputSizeInfo(int index, out int size, out int lookahead, out int align);
        [PreserveSig] int GetOutputSizeInfo(int index, out int size, out int align);
        [PreserveSig] int GetInputMaxLatency(int index, out long latency);
        [PreserveSig] int SetInputMaxLatency(int index, long latency);
        [PreserveSig] int Flush();
        [PreserveSig] int Discontinuity(int index);
        [PreserveSig] int AllocateStreamingResources();
        [PreserveSig] int FreeStreamingResources();
        [PreserveSig] int GetInputStatus(int index, out int flags);
        [PreserveSig] int ProcessInput(int index, IntPtr buffer, int flags, long timestamp, long timelength);
        // LPArray, NOT the default SAFEARRAY — see the class note. This line is the difference
        // between a working microphone and a process that dies without a word.
        [PreserveSig] int ProcessOutput(int flags, int count,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] DmoOutputDataBuffer[] buffers, out int status);
        [PreserveSig] int Lock(int bLock);
    }

    [ComImport, Guid("59EFF8B9-938C-4A26-82F2-95CB84CDC837"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMediaBuffer
    {
        [PreserveSig] int SetLength(int length);
        [PreserveSig] int GetMaxLength(out int maxLength);
        // Pointers rather than "out": either may be NULL (mediaobj.h), and an out parameter would
        // write through it.
        [PreserveSig] int GetBufferAndLength(IntPtr ppBuffer, IntPtr pcbLength);
    }

    /// <summary>Our side of the DMO's output: a fixed block of unmanaged memory it writes into.
    /// PUBLIC, with its interface, only because a COM callable wrapper refuses a type COM cannot see.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class MediaBuffer : IMediaBuffer, IDisposable
    {
        public IntPtr Data;
        public readonly int Max;
        public int Length;
        public MediaBuffer(int max) { Max = max; Data = Marshal.AllocHGlobal(max); }
        public int SetLength(int length) { if (length < 0 || length > Max) return unchecked((int)0x80070057); Length = length; return 0; }
        public int GetMaxLength(out int maxLength) { maxLength = Max; return 0; }
        public int GetBufferAndLength(IntPtr ppBuffer, IntPtr pcbLength)
        {
            if (ppBuffer != IntPtr.Zero) Marshal.WriteIntPtr(ppBuffer, Data);
            if (pcbLength != IntPtr.Zero) Marshal.WriteInt32(pcbLength, Length);
            return 0;
        }
        public void Dispose() { if (Data != IntPtr.Zero) { Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; } }
    }
}
