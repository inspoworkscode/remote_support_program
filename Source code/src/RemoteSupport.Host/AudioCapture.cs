using System.Runtime.InteropServices;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// Captures what the customer's speakers are being fed — WASAPI loopback on the default render
/// endpoint — and hands it out as small packets of 24 kHz mono 16-bit PCM.
///
/// WHY LOOPBACK ON A RENDER ENDPOINT. There is no "what the speakers are playing" device. You
/// open the speakers themselves and ask for the mix they are being fed. That is a different
/// permission and a different code path from rendering a sound, which is why it was measured
/// before any of this was written (tools/hostinfo/audiolisten.ps1, run through audioprobe
/// --listen). What that measurement established, on DESKTOP-OPHQIDH as SYSTEM in session 1:
///
/// - Loopback capture WORKS as SYSTEM in the interactive session. The captured samples were
///   checked with a Goertzel filter against a tone the machine was playing, so this is proven
///   rather than assumed.
/// - A MUTED endpoint still delivers full-amplitude audio. Loopback is taken before the
///   endpoint's volume and mute are applied, so the technician hears the machine regardless of
///   what the speakers are doing. Good news, and the opposite of what was feared.
/// - When nothing is playing, WASAPI delivers NO PACKETS AT ALL — not packets of zeroes. Any
///   loop that waits for a packet stalls forever and looks broken. This one polls and simply
///   sends nothing, and the console's own silence handling covers the gap.
/// - The mix format was 48 kHz stereo float32 on both machines tested.
///
/// WHY 24 kHz MONO 16-BIT ON THE WIRE. It needs no library and no codec, it is 48 KB/s rather
/// than the 384 KB/s the device hands us, and it is comfortably good enough to tell silence from
/// music from crackle — which is the whole job. Opus is a decision for Clint once the bandwidth
/// alongside screen tiles has been measured, not an assumption to build in now.
///
/// This runs on a DEDICATED thread like capture, cursor and input. Not Task.Run: COM
/// apartment state and the open stream are thread affine, and an async continuation would
/// resume somewhere else.
/// </summary>
public sealed class AudioCapture
{
    /// <summary>What goes on the wire. A round divisor of 48 kHz, so the common case is exact.</summary>
    public const int TargetRate = 24000;
    /// <summary>Milliseconds of audio per frame. Small enough not to add audible latency, big
    /// enough that the socket is not carrying more header than sound.</summary>
    private const int FrameMs = 40;
    private const int FrameSamples = TargetRate * FrameMs / 1000;   // 960 samples, 1920 bytes

    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _run;
    private uint _seq;
    /// <summary>The last status reported, so a second technician asking to listen can be told what
    /// is already happening rather than nothing at all.</summary>
    private AudioStatusMsg? _last;

    /// <summary>Peak of the last frame, 0..1. The console draws a meter from this; the host
    /// keeps it only so a trace line can say whether anything was actually heard.</summary>
    public double LastPeak { get; private set; }

    public bool Running => _run;

    private volatile bool _excludeSelf;
    /// <summary>
    /// Leave THIS process's own sound out of the capture — set while a voice call (3.3) is on,
    /// because the call's player is in this process and the technician would otherwise hear their
    /// own voice come back to them. Takes effect at once: the loop reopens the stream when it
    /// changes. Only while a call is on, never always: EXCLUDE takes the whole process TREE, and a
    /// script the technician runs to play a test tone is a child of this process.
    /// </summary>
    public bool ExcludeSelf { get => _excludeSelf; set => _excludeSelf = value; }

    /// <summary>
    /// Starts capturing. <paramref name="onFrame"/> is called on the capture thread with a ready
    /// binary frame; <paramref name="onStatus"/> whenever the picture changes — started, stopped,
    /// failed, or the user switched playback device under us.
    /// </summary>
    public void Start(Action<byte[]> onFrame, Action<AudioStatusMsg> onStatus)
    {
        lock (_lock)
        {
            // Already listening — because another technician in the same session turned it on, or
            // because this one asked twice. Answer anyway: the asker is waiting to be told what
            // device it is and whether it is muted, and silence here leaves their status bar
            // saying "asking for sound…" for ever. Measured on 2026-09-13: a harness joining a
            // session where listening was already on waited ten seconds and was told nothing.
            if (_run)
            {
                var s = _last;
                if (s != null) onStatus(s);
                return;
            }
            _run = true;
            _thread = new Thread(() =>
            {
                try { Loop(onFrame, onStatus); }
                catch (Exception ex)
                {
                    HostTrace.Write($"audio: capture thread died — {ex.GetType().Name}: {ex.Message}");
                    onStatus(new AudioStatusMsg { Listening = false, Error = "the audio capture stopped unexpectedly: " + ex.Message });
                }
                finally { _run = false; }
            })
            { IsBackground = true, Name = "rs-audio" };
            // MTA said out loud: process loopback's client cannot cross apartments (see AudioInterop).
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        // Forget the remembered status too: a technician who asks to listen after this must be
        // told what is true then, not what was true last time.
        lock (_lock) { _run = false; t = _thread; _thread = null; _last = null; }
        try { t?.Join(2000); } catch { }
    }

    // ------------------------------------------------------------------------------ the loop

    private void Loop(Action<byte[]> onFrame, Action<AudioStatusMsg> onStatus)
    {
        string openDeviceId = "";
        Session? s = null;
        var pending = new List<short>(FrameSamples * 2);
        var nextCheck = DateTime.UtcNow.AddSeconds(2);
        // Whether the technician has already been told why this stopped. Without it the tidy-up
        // below sends a blank "not listening" that lands AFTER the explanation and wipes it off
        // the status bar — a failure that explains itself and then hides the explanation.
        bool explained = false;

        try
        {
            while (_run)
            {
                // A voice call started or ended: reopen with or without this process in it.
                if (s != null && s.ExcludingSelf != _excludeSelf)
                {
                    HostTrace.Write(_excludeSelf ? "audio: a voice call started; leaving this process's own sound out"
                                                 : "audio: the voice call ended; capturing everything again");
                    s.Dispose(); s = null; pending.Clear();
                }

                if (s == null)
                {
                    s = Session.Open(_excludeSelf, out var why);
                    if (s == null)
                    {
                        onStatus(new AudioStatusMsg { Listening = false, Error = why });
                        HostTrace.Write("audio: could not open loopback capture — " + why);
                        explained = true;
                        return;
                    }
                    openDeviceId = s.DeviceId;
                    var started = Describe(s, listening: true);
                    lock (_lock) _last = started;
                    onStatus(started);
                    HostTrace.Write($"audio: capturing from \"{s.DeviceName}\" at {s.SampleRate} Hz {s.Channels} ch" +
                                    $"{(s.Muted ? " (endpoint muted — loopback is unaffected)" : "")}");
                }

                // The user can change their playback device in the middle of a session. Polling
                // the default endpoint's id is a good deal simpler than an IMMNotificationClient
                // callback, and two seconds late is not a problem anybody can hear.
                if (DateTime.UtcNow >= nextCheck)
                {
                    nextCheck = DateTime.UtcNow.AddSeconds(2);
                    var now = Session.DefaultRenderId();
                    if (now.Length > 0 && now != openDeviceId)
                    {
                        HostTrace.Write("audio: the default playback device changed; reopening");
                        s.Dispose(); s = null; pending.Clear();
                        continue;
                    }
                }

                var got = s.Read(pending, out var invalidated);
                if (invalidated)
                {
                    // AUDCLNT_E_DEVICE_INVALIDATED: the endpoint went away under us (unplugged,
                    // driver restarted). Reopening is the correct response, and saying so in the
                    // trace is what stops this being reported as "the sound just stopped".
                    HostTrace.Write("audio: the endpoint was invalidated; reopening");
                    s.Dispose(); s = null; pending.Clear();
                    continue;
                }

                while (pending.Count >= FrameSamples)
                {
                    var pcm = new byte[FrameSamples * 2];
                    double peak = 0;
                    for (int i = 0; i < FrameSamples; i++)
                    {
                        short v = pending[i];
                        pcm[i * 2] = (byte)(v & 0xFF);
                        pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
                        double a = Math.Abs(v) / 32768.0;
                        if (a > peak) peak = a;
                    }
                    pending.RemoveRange(0, FrameSamples);
                    LastPeak = peak;
                    onFrame(Bin.Audio(unchecked(_seq++), TargetRate, 1, AudioFormat.Pcm16, pcm));
                }

                // Nothing arrived, or not a whole frame's worth yet. Sleeping here is what makes
                // "nothing is playing" cost nothing, and it is the branch that runs most of the
                // time on a quiet machine.
                if (!got) Thread.Sleep(10);
            }
        }
        finally
        {
            s?.Dispose();
            if (!explained) onStatus(new AudioStatusMsg { Listening = false });
            HostTrace.Write("audio: stopped");
        }
    }

    private static AudioStatusMsg Describe(Session s, bool listening) => new()
    {
        Listening = listening,
        Device = s.DeviceName,
        Muted = s.Muted,
        VolumePercent = s.VolumePercent,
        SampleRate = TargetRate,
        Channels = 1,
    };

    // ------------------------------------------------------------------- one open loopback stream

    /// <summary>
    /// An open loopback stream, plus the resampler state that has to survive between packets.
    /// Separate from the loop above so that reopening after a device change is one clean call.
    /// </summary>
    private sealed class Session : IDisposable
    {
        private AudioInterop.IAudioClient _client = null!;
        private AudioInterop.IAudioCaptureClient _capture = null!;
        private IntPtr _fmt = IntPtr.Zero;
        private bool _started;
        private bool _isFloat;
        private int _bits, _blockAlign;

        // Resampling state. Position is fractional and carried across packets: resetting it every
        // packet would put a discontinuity at every packet boundary, which is audible as a buzz
        // at the packet rate and is a classic way to make a working pipeline sound broken.
        private double _pos;

        public string DeviceId = "", DeviceName = "";
        public bool Muted;
        public int VolumePercent, SampleRate, Channels;
        /// <summary>Opened as process loopback leaving this process out (a voice call is on).</summary>
        public bool ExcludingSelf;
        /// <summary>The format we allocated ourselves for process loopback, which has none of its
        /// own; freed with FreeCoTaskMem exactly like the one GetMixFormat returns.</summary>
        private static IntPtr OwnFormat()
        {
            var f = Marshal.AllocCoTaskMem(18);
            Marshal.WriteInt16(f, 0, 1);            // WAVE_FORMAT_PCM
            Marshal.WriteInt16(f, 2, 2);            // stereo
            Marshal.WriteInt32(f, 4, 48000);
            Marshal.WriteInt32(f, 8, 48000 * 4);
            Marshal.WriteInt16(f, 12, 4);
            Marshal.WriteInt16(f, 14, 16);
            Marshal.WriteInt16(f, 16, 0);
            return f;
        }

        public static string DefaultRenderId()
        {
            AudioInterop.IMMDeviceEnumerator? en = null;
            AudioInterop.IMMDevice? dev = null;
            try
            {
                en = (AudioInterop.IMMDeviceEnumerator)new AudioInterop.MMDeviceEnumeratorComObject();
                if (en.GetDefaultAudioEndpoint(AudioInterop.ERender, AudioInterop.EMultimedia, out dev) != 0 || dev == null) return "";
                return dev.GetId(out var id) == 0 && id != null ? id : "";
            }
            catch { return ""; }
            finally { Rel(dev); Rel(en); }
        }

        public static Session? Open(bool excludeSelf, out string why)
        {
            if (excludeSelf)
            {
                var p = OpenExcludingSelf(out why);
                if (p != null) return p;
                // Windows before 10 2004 has no process loopback. Listen still works; the
                // technician will hear their own voice in it, and the trace says why.
                HostTrace.Write("audio: could not leave our own sound out (" + why + "); capturing everything");
            }
            return Open(out why);
        }

        /// <summary>
        /// Process loopback, every process but this one. The endpoint's id, name, mute and volume
        /// are still read from the default playback device — for the status line, and so a change
        /// of device is noticed the same way as before.
        /// </summary>
        private static Session? OpenExcludingSelf(out string why)
        {
            why = "";
            var s = new Session { ExcludingSelf = true };
            AudioInterop.IMMDeviceEnumerator? en = null;
            AudioInterop.IMMDevice? dev = null;
            try
            {
                en = (AudioInterop.IMMDeviceEnumerator)new AudioInterop.MMDeviceEnumeratorComObject();
                if (en.GetDefaultAudioEndpoint(AudioInterop.ERender, AudioInterop.EMultimedia, out dev) != 0 || dev == null)
                { why = "no default playback device"; s.Dispose(); return null; }
                dev.GetId(out var id); s.DeviceId = id ?? "";
                s.DeviceName = FriendlyName(dev);
                ReadVolume(dev, out s.Muted, out s.VolumePercent);

                var client = AudioInterop.ActivateProcessLoopbackExcluding(Environment.ProcessId, out int hr);
                if (client == null) { why = $"process loopback refused (hr=0x{hr:X8})"; s.Dispose(); return null; }
                s._client = client;
                s._fmt = OwnFormat();
                s.SampleRate = 48000; s.Channels = 2; s._bits = 16; s._blockAlign = 4; s._isFloat = false;

                hr = s._client.Initialize(0, AudioInterop.StreamFlagsLoopback | AudioInterop.StreamFlagsEventCallback | AudioInterop.StreamFlagsAutoConvertPcm,
                                          2_000_000L, 0L, s._fmt, IntPtr.Zero);
                if (hr != 0) { why = $"process loopback would not initialise (hr=0x{hr:X8})"; s.Dispose(); return null; }
                // EVENTCALLBACK wants a handle even though this loop polls rather than waits.
                s._event = new EventWaitHandle(false, EventResetMode.AutoReset);
                s._client.SetEventHandle(s._event.SafeWaitHandle.DangerousGetHandle());

                var capIid = typeof(AudioInterop.IAudioCaptureClient).GUID;
                hr = s._client.GetService(ref capIid, out var rawCap);
                if (hr != 0 || rawCap == IntPtr.Zero) { why = $"no capture side (hr=0x{hr:X8})"; s.Dispose(); return null; }
                s._capture = (AudioInterop.IAudioCaptureClient)Marshal.GetObjectForIUnknown(rawCap);
                Marshal.Release(rawCap);

                hr = s._client.Start();
                if (hr != 0) { why = $"would not start (hr=0x{hr:X8})"; s.Dispose(); return null; }
                s._started = true;
                return s;
            }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; s.Dispose(); return null; }
            finally { Rel(dev); Rel(en); }
        }

        private EventWaitHandle? _event;

        private static Session? Open(out string why)
        {
            why = "";
            var s = new Session();
            AudioInterop.IMMDeviceEnumerator? en = null;
            AudioInterop.IMMDevice? dev = null;
            try
            {
                en = (AudioInterop.IMMDeviceEnumerator)new AudioInterop.MMDeviceEnumeratorComObject();
                int hr = en.GetDefaultAudioEndpoint(AudioInterop.ERender, AudioInterop.EMultimedia, out dev);
                if (hr != 0 || dev == null)
                {
                    // 0x80070490 is ERROR_NOT_FOUND: there is no playback device at all, which is
                    // a real answer a technician can act on rather than a failure to report.
                    why = hr == unchecked((int)0x80070490)
                        ? "that machine has no playback device at all, so there is no sound to listen to"
                        : $"no default playback device (hr=0x{hr:X8})";
                    s.Dispose(); return null;
                }
                dev.GetId(out var id); s.DeviceId = id ?? "";
                s.DeviceName = FriendlyName(dev);
                ReadVolume(dev, out s.Muted, out s.VolumePercent);

                var clientIid = typeof(AudioInterop.IAudioClient).GUID;
                hr = dev.Activate(ref clientIid, AudioInterop.ClsCtxInproc, IntPtr.Zero, out var rawClient);
                if (hr != 0 || rawClient == IntPtr.Zero)
                { why = $"the audio engine refused to open that device (hr=0x{hr:X8})"; s.Dispose(); return null; }
                s._client = (AudioInterop.IAudioClient)Marshal.GetObjectForIUnknown(rawClient);
                Marshal.Release(rawClient);

                hr = s._client.GetMixFormat(out s._fmt);
                if (hr != 0 || s._fmt == IntPtr.Zero)
                { why = $"could not read the device's audio format (hr=0x{hr:X8})"; s.Dispose(); return null; }

                if (!ParseFormat(s._fmt, out s.SampleRate, out s.Channels, out s._bits, out s._blockAlign, out s._isFloat, out var fmtName))
                { why = $"that device's audio format is one this cannot read ({fmtName})"; s.Dispose(); return null; }

                // AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, one second of buffer.
                // Periodicity must be 0 in shared mode.
                hr = s._client.Initialize(0, AudioInterop.StreamFlagsLoopback, 10_000_000L, 0L, s._fmt, IntPtr.Zero);
                if (hr != 0)
                {
                    why = $"Windows refused loopback capture on that device (hr=0x{hr:X8})";
                    s.Dispose(); return null;
                }

                var capIid = typeof(AudioInterop.IAudioCaptureClient).GUID;
                hr = s._client.GetService(ref capIid, out var rawCap);
                if (hr != 0 || rawCap == IntPtr.Zero)
                { why = $"could not open the capture side of that device (hr=0x{hr:X8})"; s.Dispose(); return null; }
                s._capture = (AudioInterop.IAudioCaptureClient)Marshal.GetObjectForIUnknown(rawCap);
                Marshal.Release(rawCap);

                hr = s._client.Start();
                if (hr != 0) { why = $"the capture stream would not start (hr=0x{hr:X8})"; s.Dispose(); return null; }
                s._started = true;
                return s;
            }
            catch (Exception ex)
            {
                why = ex.GetType().Name + ": " + ex.Message;
                s.Dispose();
                return null;
            }
            finally { Rel(dev); Rel(en); }
        }

        /// <summary>
        /// Drains whatever WASAPI has, appending 24 kHz mono 16-bit samples to
        /// <paramref name="into"/>. Returns whether anything at all arrived.
        /// </summary>
        public bool Read(List<short> into, out bool invalidated)
        {
            invalidated = false;
            bool any = false;
            while (true)
            {
                int hr = _capture.GetNextPacketSize(out uint next);
                if (hr != 0) { invalidated = hr == AudioInterop.AudclntDeviceInvalidated; return any; }
                if (next == 0) return any;          // nothing playing: no packet, not silence

                hr = _capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _);
                if (hr != 0) { invalidated = hr == AudioInterop.AudclntDeviceInvalidated; return any; }
                any = true;

                bool silent = (flags & AudioInterop.BufferFlagsSilent) != 0;
                AppendResampled(into, data, (int)frames, silent);
                _capture.ReleaseBuffer(frames);
            }
        }

        /// <summary>
        /// Downmix to mono and resample to <see cref="TargetRate"/> in one pass.
        ///
        /// Each output sample AVERAGES the input samples it spans rather than picking one of
        /// them. At 48 kHz that is a two-sample box filter, which costs nothing and takes the
        /// worst of the aliasing off — plain decimation folds anything above 12 kHz back down
        /// into the audible band as a whistle, and on a machine playing music that is very
        /// audible indeed.
        /// </summary>
        private void AppendResampled(List<short> into, IntPtr data, int frames, bool silent)
        {
            double step = (double)SampleRate / TargetRate;
            if (silent || data == IntPtr.Zero || frames <= 0)
            {
                // A silent-flagged packet still occupies time, and dropping it would make the
                // stream run ahead of the clock. Emit the zeroes it stands for, stepping the
                // resampler exactly as the real path below does.
                while (_pos < frames) { into.Add(0); _pos += step; }
                _pos -= frames;
                return;
            }

            int total = frames * Channels;
            var mono = new double[frames];
            if (_isFloat)
            {
                var f = new float[total];
                Marshal.Copy(data, f, 0, total);
                for (int i = 0; i < frames; i++)
                {
                    double sum = 0;
                    for (int c = 0; c < Channels; c++) sum += f[i * Channels + c];
                    mono[i] = sum / Channels;
                }
            }
            else
            {
                var sh = new short[total];
                Marshal.Copy(data, sh, 0, total);
                for (int i = 0; i < frames; i++)
                {
                    double sum = 0;
                    for (int c = 0; c < Channels; c++) sum += sh[i * Channels + c] / 32768.0;
                    mono[i] = sum / Channels;
                }
            }

            while (_pos < frames)
            {
                int from = (int)_pos;
                int to = Math.Min(frames, (int)(_pos + step) + 1);
                double sum = 0; int count = 0;
                for (int i = from; i < to; i++) { sum += mono[i]; count++; }
                double v = count > 0 ? sum / count : 0;
                int q = (int)Math.Round(v * 32767.0);
                into.Add((short)Math.Clamp(q, short.MinValue, short.MaxValue));
                _pos += step;
            }
            _pos -= frames;   // carry the fraction into the next packet
        }

        public void Dispose()
        {
            if (_started && _client != null) { try { _client.Stop(); } catch { } }
            _started = false;
            if (_fmt != IntPtr.Zero) { Marshal.FreeCoTaskMem(_fmt); _fmt = IntPtr.Zero; }
            Rel(_capture); Rel(_client);
            _capture = null!; _client = null!;
            _event?.Dispose(); _event = null;
        }

        private static bool ParseFormat(IntPtr fmt, out int rate, out int channels, out int bits,
                                        out int blockAlign, out bool isFloat, out string name)
        {
            // WAVEFORMATEX is 18 bytes (cbSize at 16); WAVEFORMATEXTENSIBLE then carries Samples
            // at 18, dwChannelMask at 20 and the SubFormat GUID at 24. Offset 26 was tried first
            // in the probe and read a GUID that changed between runs — the signature of an
            // unaligned struct read, and worth recognising on sight.
            int tag = Marshal.ReadInt16(fmt, 0) & 0xFFFF;
            channels = Marshal.ReadInt16(fmt, 2) & 0xFFFF;
            rate = Marshal.ReadInt32(fmt, 4);
            blockAlign = Marshal.ReadInt16(fmt, 12) & 0xFFFF;
            bits = Marshal.ReadInt16(fmt, 14) & 0xFFFF;
            isFloat = tag == 3;
            name = "tag " + tag;
            if (tag == 1) name = "PCM";
            else if (tag == 3) name = "IEEE float";
            else if (tag == 0xFFFE)
            {
                var g = new byte[16];
                Marshal.Copy(fmt + 24, g, 0, 16);
                var sub = new Guid(g);
                if (sub == AudioInterop.SubFormatIeeeFloat) { isFloat = true; name = "EXTENSIBLE/IEEE float"; }
                else if (sub == AudioInterop.SubFormatPcm) name = "EXTENSIBLE/PCM";
                else name = "EXTENSIBLE/" + sub;
            }
            name += $" {rate} Hz {channels} ch {bits}-bit";
            // Refuse anything we cannot decode rather than filling the stream with zeroes. The
            // probe did the silent-zeroes version first and reported "300 packets, peak 0" — a
            // broken reader that looks exactly like a silent machine.
            if (channels <= 0 || rate <= 0) return false;
            return (isFloat && bits == 32) || (!isFloat && bits == 16);
        }

        private static string FriendlyName(AudioInterop.IMMDevice dev)
        {
            AudioInterop.IPropertyStore? store = null;
            try
            {
                if (dev.OpenPropertyStore(0, out store) != 0 || store == null) return "the default playback device";
                var key = AudioInterop.PkeyFriendlyName;
                if (store.GetValue(ref key, out var pv) != 0) return "the default playback device";
                if (pv.vt != 31 || pv.p == IntPtr.Zero) return "the default playback device";
                return Marshal.PtrToStringUni(pv.p) ?? "the default playback device";
            }
            catch { return "the default playback device"; }
            finally { Rel(store); }
        }

        private static void ReadVolume(AudioInterop.IMMDevice dev, out bool muted, out int percent)
        {
            muted = false; percent = -1;
            AudioInterop.IAudioEndpointVolume? vol = null;
            try
            {
                var iid = typeof(AudioInterop.IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, AudioInterop.ClsCtxInproc, IntPtr.Zero, out var raw) != 0 || raw == IntPtr.Zero) return;
                vol = (AudioInterop.IAudioEndpointVolume)Marshal.GetObjectForIUnknown(raw);
                Marshal.Release(raw);
                if (vol.GetMute(out int m) == 0) muted = m != 0;
                if (vol.GetMasterVolumeLevelScalar(out float f) == 0) percent = (int)Math.Round(f * 100);
            }
            catch { }
            finally { Rel(vol); }
        }

        /// <summary>Releasing a COM wrapper must never be the thing that fails; it threw from
        /// inside a finally in the probe and swallowed the real answer.</summary>
        private static void Rel(object? o)
        {
            if (o == null) return;
            try { Marshal.ReleaseComObject(o); } catch { }
        }
    }

    // The Core Audio interfaces live in AudioInterop, shared with AudioDiagnostics — the tab
    // that explains why a machine is silent needs the same endpoints this one listens to.
}
