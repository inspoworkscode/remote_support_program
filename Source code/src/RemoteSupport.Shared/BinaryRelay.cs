namespace RemoteSupport.Shared;

/// <summary>
/// Passing a binary frame from one socket to another without losing the ones that cannot be lost.
///
/// A hub relays by handing the frame straight to the other connection and ignoring what it says
/// back. For screen tiles that is right: a frame that will not fit is a frame better dropped than
/// queued, and the next one is along in a moment. For a FILE CHUNK it is a corruption, and a
/// silent one — the sender's own socket already accepted the chunk, so it has no reason to retry
/// and every reason to believe the file arrived.
///
/// Found on 2026-09-18 from the receiving end's own words:
///
///   RemoteSupport.Technician.exe (the link lost part of this file — expected the next piece at
///   9 699 328 bytes and it arrived at 10 485 760)
///
/// — a gap of exactly twelve chunks, in a 70 MB file, over a link fast enough to fill the far
/// socket's queue. The technician's FileSetSender.SendChunkAsync waits patiently for room in ITS
/// queue and had no idea the hub was throwing chunks away one hop later.
///
/// The fix is back-pressure rather than a bigger buffer. Waiting here stalls the READ loop of the
/// connection the frame came from, which is precisely the right thing: it stops that sender
/// outrunning the far end, TCP flow control does the rest, and the sender's own queue fills until
/// it too waits. A bigger buffer only moves the cliff.
///
/// This is the same fault, in the same shape, as the stage 2.3 floor: the send channel dropped
/// screen frames and the host marked them delivered anyway (docs/NEXT-SESSION-ADAPTIVE.md).
/// </summary>
public static class BinaryRelay
{
    /// <summary>
    /// How long to wait for room before giving up on a chunk. Generous, because the alternative
    /// is a corrupt file: a far end that cannot take a chunk in thirty seconds has a real problem,
    /// and by then the transfer deserves to fail loudly rather than quietly.
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>True when losing this frame would corrupt something rather than blur it.</summary>
    public static bool MustArrive(ReadOnlySpan<byte> frame) =>
        // By BinKind.Of, and a sealed MESSAGE too (2026-10-07): a sealed file chunk is still a file chunk, and a sealed
        // message is text, which is never dropped.
        // A bridge frame (2026-10-15) is file chunks between two machines, sealed under a key of their own.
        frame.Length > 0 && (BinKind.Of(frame[0]) is BinKind.FileChunk or BinKind.Bridge || frame[0] == BinKind.Sealed);

    /// <summary>
    /// True when a dropped frame leaves the viewer's picture broken until the next key frame, so the
    /// host must be asked for one (<see cref="KeyFrameRequestMsg"/>, HANDOVER §142). Only video: a
    /// dropped tile leaves one stale rectangle, and sound simply has a gap.
    /// </summary>
    public static bool LossNeedsKeyFrame(ReadOnlySpan<byte> frame) =>
        frame.Length > 0 && BinKind.Of(frame[0]) == BinKind.Video;

    /// <summary>The fewest milliseconds between two requests for a complete picture in one session: a
    /// viewer behind on its link drops frame after frame, and each request costs every viewer a key frame.</summary>
    public const long KeyFrameRequestEveryMs = 1000;

    /// <summary>
    /// Relays one frame. Frames that may be dropped are offered once. Frames that may not are
    /// waited on, and a failure to place one is reported rather than swallowed.
    /// </summary>
    /// <returns>False only when a MUST-ARRIVE frame could not be placed before the deadline.</returns>
    public static bool Send(Func<byte[], bool> to, byte[] frame)
    {
        if (to(frame)) return true;
        if (!MustArrive(frame)) return true;   // a dropped tile is not a failure, it is the design

        var until = DateTime.UtcNow + Deadline;
        int wait = 5;
        while (DateTime.UtcNow < until)
        {
            // Coarse on purpose, for the same reason SendChunkAsync is: a queue that is full is
            // waiting on a socket, which is measured in hundreds of milliseconds. Spinning faster
            // would burn a core on a machine that is already behind.
            Thread.Sleep(wait);
            wait = Math.Min(wait * 2, 100);
            if (to(frame)) return true;
        }
        return false;
    }
}
