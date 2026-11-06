using System.Runtime.InteropServices;

namespace RemoteSupport.Media;

/// <summary>
/// Plays a stream of mono 16-bit PCM packets through waveOut: Listen's player on the console, and
/// both ends of a voice call (3.3). It was the console's AudioPlayer until 2026-09-30 and moved
/// here unchanged except that its timings are now the caller's, because a conversation cannot
/// bear the buffer that suits a technician listening to music.
///
/// WHY waveOut. It is in every Windows since 1991, needs no dependency and no COM, works as SYSTEM
/// in the customer's session (the test tone proved that, HANDOVER 18), and for these streams it is
/// indistinguishable from the modern answer.
///
/// THE THREE THINGS THAT ACTUALLY MATTER, none of which are the audio API:
///
/// 1. A JITTER BUFFER. Packets arrive over a websocket, in bursts, competing with screen frames.
///    Playing each one the moment it lands means a click at every hiccup. This holds a prebuffer
///    before it starts and then plays continuously.
/// 2. WHAT TO DO WHEN THE QUEUE RUNS DRY. Silence is written to keep the device running: stopping
///    and restarting it clicks. For a voice call this is not a nicety but a REQUIREMENT — Windows'
///    echo canceller delivers nothing at all while no render stream is open (measured with
///    micprobe, 2026-09-30), so the customer's microphone would go dead every time the technician
///    stopped talking.
/// 3. CLOCK DRIFT. The far end's clock and this machine's are not the same crystal. The cheap fix
///    is to drop the oldest packet when the queue passes the maximum.
/// </summary>
public class PcmPlayer : IDisposable
{
    public int Rate { get; }
    private readonly int _bufferMs, _bufferBytes, _buffers, _prebufferMs, _maxQueueMs;
    private readonly bool _communications;
    private readonly Action<string> _log;
    private readonly string _name;

    private readonly Queue<byte[]> _queue = new();
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _run;
    private IntPtr _hwo = IntPtr.Zero;
    private IntPtr[] _headers = Array.Empty<IntPtr>();
    private IntPtr[] _blocks = Array.Empty<IntPtr>();
    private bool _priming = true;

    /// <summary>Loudest sample in the most recent packet, 0..1, for a level meter.</summary>
    public double Level { get; private set; }

    /// <summary>Counters for the closing trace line. "It sounded odd" is not a diagnosis; these
    /// tell a gap in the network apart from a gap we made ourselves.</summary>
    public int PacketsIn, PacketsPlayed, PacketsDropped, Starved;

    public string? Error { get; private set; }
    public bool Running => _run;

    /// <param name="bufferMs">One waveOut buffer: the size of the packets the far end sends.</param>
    /// <param name="buffers">waveOut buffers in flight.</param>
    /// <param name="spare">Packets held beyond the in-flight buffers before sound starts. The
    /// prebuffer must cover the buffers THEMSELVES as well as the slack: the instant playback
    /// begins, every buffer is free and takes a packet, so a prebuffer the size of the buffers
    /// collapses to nothing — Listen starved 9 times in 8 seconds that way (HANDOVER 18).</param>
    /// <param name="maxQueueMs">Past this the queue is drifting behind and the oldest is dropped.</param>
    /// <param name="communications">Play on the default COMMUNICATIONS device rather than the
    /// default multimedia one. A voice call must: that is the device the echo canceller watches,
    /// and a voice played anywhere else comes back through the microphone uncancelled.</param>
    public PcmPlayer(int rate, int bufferMs, int buffers, int spare, int maxQueueMs, bool communications,
                     Action<string>? log = null, string name = "audio")
    {
        Rate = rate;
        _bufferMs = bufferMs;
        _bufferBytes = rate * 2 * bufferMs / 1000;
        _buffers = buffers;
        _prebufferMs = (buffers + spare) * bufferMs;
        _maxQueueMs = maxQueueMs;
        _communications = communications;
        _log = log ?? (_ => { });
        _name = name;
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_run) return true;
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1,                 // WAVE_FORMAT_PCM
                nChannels = 1,
                nSamplesPerSec = (uint)Rate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = (uint)Rate * 2,
                cbSize = 0,
            };
            uint flags = CallbackNull | (_communications ? WaveMappedDefaultCommunicationDevice : 0);
            int mr = waveOutOpen(out _hwo, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, flags);
            if (mr != 0)
            {
                // Say which call failed and what it said. "No sound" with no reason is the thing
                // that turns a five-minute problem into an afternoon.
                Error = $"the speakers could not be opened (waveOutOpen returned {mr})";
                _log($"{_name}: " + Error);
                _hwo = IntPtr.Zero;
                return false;
            }

            _headers = new IntPtr[_buffers];
            _blocks = new IntPtr[_buffers];
            for (int i = 0; i < _buffers; i++)
            {
                _blocks[i] = Marshal.AllocHGlobal(_bufferBytes);
                _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
                var h = new WaveHdr { lpData = _blocks[i], dwBufferLength = (uint)_bufferBytes };
                Marshal.StructureToPtr(h, _headers[i], false);
                waveOutPrepareHeader(_hwo, _headers[i], Marshal.SizeOf<WaveHdr>());
                // Mark it done so the loop treats every buffer as free on the first pass.
                MarkDone(_headers[i]);
            }

            _priming = true;
            Error = null;
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "rs-" + _name + "-out" };
            _thread.Start();
            _log($"{_name}: playback opened at {Rate} Hz mono, {_buffers} x {_bufferMs} ms buffers" +
                 (_communications ? ", on the communications device" : ""));
            return true;
        }
    }

    /// <summary>Queues one packet.</summary>
    public void Feed(ReadOnlySpan<byte> pcm)
    {
        if (!_run || pcm.Length == 0) return;
        var copy = pcm.ToArray();

        double peak = 0;
        for (int i = 0; i + 1 < copy.Length; i += 2)
        {
            short v = (short)(copy[i] | (copy[i + 1] << 8));
            double a = Math.Abs(v) / 32768.0;
            if (a > peak) peak = a;
        }
        Level = peak;

        lock (_lock)
        {
            PacketsIn++;
            _queue.Enqueue(copy);
            // Drift, or a burst that arrived while the screen was busy. Drop from the FRONT: the
            // oldest audio is the least useful, and dropping the newest would keep us permanently
            // behind rather than catching up.
            int maxPackets = _maxQueueMs / _bufferMs;
            while (_queue.Count > maxPackets) { _queue.Dequeue(); PacketsDropped++; }
        }
    }

    private void Loop()
    {
        var silence = new byte[_bufferBytes];
        try
        {
            while (_run)
            {
                // How many buffers the driver has finished with. While even one is still playing
                // there is sound in hand, so an empty queue right now is not an emergency and
                // filling it with silence would be an unforced gap. Only when EVERY buffer has
                // come back is the device genuinely about to run dry.
                int free = 0;
                for (int i = 0; i < _headers.Length; i++) if (IsDone(_headers[i])) free++;
                bool aboutToRunDry = free >= _headers.Length;

                bool wroteAnything = false;
                for (int i = 0; i < _headers.Length && _run; i++)
                {
                    if (!IsDone(_headers[i])) continue;

                    byte[]? packet = null;
                    lock (_lock)
                    {
                        int have = _queue.Count * _bufferMs;
                        if (_priming && have < _prebufferMs) packet = null;
                        else
                        {
                            _priming = false;
                            if (_queue.Count > 0) { packet = _queue.Dequeue(); PacketsPlayed++; }
                            else if (aboutToRunDry) Starved++;
                            else break;   // wait a few ms for the next packet instead
                        }
                    }

                    var src = packet ?? silence;
                    Marshal.Copy(src, 0, _blocks[i], Math.Min(src.Length, _bufferBytes));
                    if (src.Length < _bufferBytes)
                        for (int z = src.Length; z < _bufferBytes; z++) Marshal.WriteByte(_blocks[i], z, 0);

                    ClearDone(_headers[i]);
                    int mr = waveOutWrite(_hwo, _headers[i], Marshal.SizeOf<WaveHdr>());
                    if (mr != 0)
                    {
                        Error = $"playback failed (waveOutWrite returned {mr})";
                        _log($"{_name}: " + Error);
                        _run = false;
                        break;
                    }
                    wroteAnything = true;
                }
                Thread.Sleep(wroteAnything ? 2 : Math.Max(2, _bufferMs / 8));
            }
        }
        catch (Exception ex)
        {
            Error = ex.GetType().Name + ": " + ex.Message;
            _log($"{_name}: playback thread died — " + Error);
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_lock) { if (!_run && _hwo == IntPtr.Zero) return; _run = false; t = _thread; _thread = null; _queue.Clear(); }
        try { t?.Join(1000); } catch { }
        lock (_lock)
        {
            if (_hwo != IntPtr.Zero)
            {
                // Reset before unpreparing: unpreparing a header that is still queued fails, and
                // then freeing its memory underneath the driver is how this would crash rather
                // than merely misbehave.
                waveOutReset(_hwo);
                for (int i = 0; i < _headers.Length; i++)
                {
                    if (_headers[i] != IntPtr.Zero)
                    {
                        waveOutUnprepareHeader(_hwo, _headers[i], Marshal.SizeOf<WaveHdr>());
                        Marshal.FreeHGlobal(_headers[i]);
                    }
                    if (_blocks[i] != IntPtr.Zero) Marshal.FreeHGlobal(_blocks[i]);
                }
                waveOutClose(_hwo);
                _hwo = IntPtr.Zero;
            }
            _headers = Array.Empty<IntPtr>();
            _blocks = Array.Empty<IntPtr>();
            Level = 0;
        }
        _log($"{_name}: playback closed — in={PacketsIn} played={PacketsPlayed} dropped={PacketsDropped} starved={Starved}");
    }

    public void Dispose() => Stop();

    /// <summary>One line for a closing trace.</summary>
    public string DiagnosticSummary() =>
        $"{_name}In={PacketsIn} {_name}Played={PacketsPlayed} {_name}Dropped={PacketsDropped} {_name}Starved={Starved}";

    // --------------------------------------------------------------------------------- interop

    private const uint WaveMapper = 0xFFFFFFFF;   // WAVE_MAPPER: whatever the user's default is
    private const uint CallbackNull = 0;
    /// <summary>WAVE_MAPPED_DEFAULT_COMMUNICATION_DEVICE: with WAVE_MAPPER, the default
    /// communications device rather than the default multimedia one.</summary>
    private const uint WaveMappedDefaultCommunicationDevice = 0x0010;
    private const uint WhdrDone = 0x00000001;
    // Asked for rather than assumed: it is 24 on x64 and 16 on x86, and a wrong guess here would
    // read some other field and quietly never see a buffer finish.
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));

    // The completion flag is read and written in place rather than through a marshalled struct:
    // the driver writes WHDR_DONE into this very memory, so a copy would be stale the moment it
    // was made.
    private static bool IsDone(IntPtr hdr) => ((uint)Marshal.ReadInt32(hdr, FlagsOffset) & WhdrDone) != 0;
    private static void MarkDone(IntPtr hdr) => Marshal.WriteInt32(hdr, FlagsOffset, (int)((uint)Marshal.ReadInt32(hdr, FlagsOffset) | WhdrDone));
    private static void ClearDone(IntPtr hdr) => Marshal.WriteInt32(hdr, FlagsOffset, (int)((uint)Marshal.ReadInt32(hdr, FlagsOffset) & ~WhdrDone));

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, uint uDeviceID, ref WaveFormatEx fmt, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
}

/// <summary>
/// The player for a voice call, either end. 20 ms packets, four in flight and two spare: 120 ms
/// before the first sound, against Listen's 280 — the difference between a conversation and a
/// walkie-talkie. Drift is cut back at 300 ms rather than 600 for the same reason.
/// </summary>
public sealed class VoicePlayer : PcmPlayer
{
    public VoicePlayer(Action<string>? log = null)
        : base(RemoteSupport.Shared.VoiceWire.Rate, RemoteSupport.Shared.VoiceWire.FrameMs, buffers: 4, spare: 2,
               maxQueueMs: 300, communications: true, log, name: "voice") { }
}
