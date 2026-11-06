using RemoteSupport.Media;

namespace RemoteSupport.Technician;

/// <summary>
/// Plays the customer's sound on the technician's machine for Listen: 24 kHz mono 16-bit PCM in,
/// speakers out.
///
/// The engine moved to <see cref="PcmPlayer"/> in RemoteSupport.Media on 2026-09-30, because a
/// voice call (3.3) needs the same thing at both ends with a shorter buffer. These are Listen's
/// timings exactly as they were measured in HANDOVER 18: 40 ms packets, four in flight and three
/// spare — 280 ms before the first sound, which absorbs 120 ms of lateness — and drift cut back
/// at 600 ms. Do not "harmonise" them with the voice player's: music and conversation want
/// different buffers, and that is the whole reason there are two.
/// </summary>
public sealed class AudioPlayer : PcmPlayer
{
    public new const int Rate = 24000;

    public AudioPlayer()
        : base(Rate, bufferMs: 40, buffers: 4, spare: 3, maxQueueMs: 600, communications: false,
               log: Trace.Write, name: "audio") { }
}
