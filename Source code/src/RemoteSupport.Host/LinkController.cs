namespace RemoteSupport.Host;

/// <summary>
/// Stage 2.3: chooses the picture quality and the frame rate from what the link is actually doing,
/// instead of from a constant and a slider nobody moved.
///
/// ---- What it watches, and why that and not something else ---------------------------------
///
/// **Bytes waiting for the socket** (<c>WsConnection.PendingBytes</c>), plus the count of frames
/// the queue has refused. Not round-trip time: RTT describes the path, queue depth describes THIS
/// stream on it, and a link that is fine for a thirty-byte ping and hopeless for 2.8 MB/s is the
/// normal case rather than the exotic one. Depth is also the thing we can act on without waiting
/// for a round trip to come back over the link we are trying to unclog.
///
/// A refusal is harder evidence than depth, because it means picture has already been lost, so it
/// is allowed to force a step down immediately rather than waiting for the rate limit.
///
/// ---- Why it lives on the host --------------------------------------------------------------
///
/// The host has the queue and the capture cost; the console has the arrival times. Either could
/// drive this, but a controller whose corrections travel over the congested link it is correcting
/// has a delay in the worst possible place. The console keeps the slider, which is a CEILING: the
/// controller may go below what the technician asked for and may never go above it. A technician
/// on a bad link often wants it ugly on purpose, and an adaptive system that silently overrides
/// the control someone just moved is a bug report waiting to be written.
///
/// ---- Fast down, slow up ---------------------------------------------------------------------
///
/// Deliberately asymmetric. A technician tolerates the picture getting ugly the instant the link
/// sags far better than a picture that oscillates, and oscillation is the classic failure of this
/// kind of loop. Recovery also demands a quiet queue for a sustained period AND no refusals in
/// that window, so one lucky pass cannot climb the ladder.
///
/// ---- The ladders are a guess, and are meant to be replaced by a measurement ------------------
///
/// Quality and frame rate are not interchangeable. On scrolling text, frame rate is what makes it
/// readable and quality is what makes it legible; 3 fps at quality 90 and 12 fps at quality 30
/// cost about the same and look nothing alike. So there are two ladders and the content picks
/// between them — and the host cannot see what the content IS, only how much of the screen moved,
/// which is already computed for every frame. A screen where most tiles change every pass is
/// video-like and loses frame rate first; a screen where a few tiles change is text-like and
/// loses quality first.
/// </summary>
public sealed class LinkController
{
    /// <summary>Queue depth above which the picture must give way. About a fifth of a second of
    /// the old JPEG tile stream, so it is reached by a genuine backlog rather than by one frame
    /// in flight.</summary>
    public const long BackOffBytes = 512 * 1024;

    /// <summary>Queue depth below which recovery may be considered at all.</summary>
    public const long RecoverBytes = 64 * 1024;

    /// <summary>The queue must stay under <see cref="RecoverBytes"/> for this long, with no
    /// refusals, before anything climbs back.</summary>
    public static readonly TimeSpan RecoverAfter = TimeSpan.FromSeconds(2.5);

    /// <summary>The fastest the controller may step DOWN on queue depth alone. A refusal bypasses
    /// this, because by then picture is already being lost.</summary>
    public static readonly TimeSpan StepDownEvery = TimeSpan.FromMilliseconds(400);

    /// <summary>A screen with more of its tiles changing than this is treated as video.</summary>
    public const double VideoChangedFraction = 0.5;

    // Level 0 is "exactly what the technician asked for". Each step multiplies the ceiling.
    // (quality x, frame rate x)
    private static readonly (double q, double f)[] TextLadder =
    {
        (1.00, 1.00), (0.85, 1.00), (0.72, 1.00), (0.60, 0.75), (0.50, 0.55), (0.42, 0.40), (0.35, 0.25),
    };
    private static readonly (double q, double f)[] VideoLadder =
    {
        (1.00, 1.00), (1.00, 0.70), (0.90, 0.50), (0.80, 0.40), (0.65, 0.30), (0.55, 0.22), (0.45, 0.17),
    };

    public const int MaxLevel = 6;

    private int _qualityCeiling, _fpsCeiling;
    private long _lastDropped;
    private DateTime _lastStep = DateTime.MinValue;
    private DateTime _quietSince = DateTime.MinValue;
    private bool _video;

    public LinkController(int qualityCeiling, int fpsCeiling)
    {
        SetCeiling(qualityCeiling, fpsCeiling);
        Level = 0;
        Recompute();
    }

    /// <summary>What the technician asked for. The controller never exceeds this.</summary>
    public void SetCeiling(int quality, int fps)
    {
        _qualityCeiling = Math.Clamp(quality, 20, 95);
        _fpsCeiling = Math.Clamp(fps, 1, 30);
        Recompute();
    }

    /// <summary>0 means the link is carrying everything asked of it.</summary>
    public int Level { get; private set; }

    /// <summary>The quality to encode at right now.</summary>
    public int Quality { get; private set; }

    /// <summary>The frame rate to pace the capture loop at right now.</summary>
    public int Fps { get; private set; }

    /// <summary>One line a technician can read, for the Info tab. "The link" and "the slider" are
    /// the same complaint from the chair and only this distinguishes them.</summary>
    public string Describe() =>
        Level == 0
            ? $"carrying it: quality {Quality}, {Fps} fps"
            : $"holding back {Level} of {MaxLevel} — quality {Quality} of {_qualityCeiling}, {Fps} of {_fpsCeiling} fps ({(_video ? "moving picture" : "mostly still")})";

    /// <summary>
    /// One capture pass' worth of evidence. Returns true when the quality or the frame rate
    /// actually changed, which is the caller's cue to re-tune the encoder and say so.
    /// </summary>
    /// <param name="pendingBytes">Bytes queued for the socket right now.</param>
    /// <param name="droppedFrames">The connection's running count of refused frames.</param>
    /// <param name="changedFraction">Share of the screen's tiles that changed on this pass.</param>
    public bool Update(long pendingBytes, long droppedFrames, double changedFraction, DateTime now)
    {
        bool refused = droppedFrames > _lastDropped;
        _lastDropped = droppedFrames;

        // Which ladder. Smoothed with a deliberate bias to "still": one busy frame in a document
        // should not move a reader onto the video ladder, but a genuinely moving picture reaches
        // it within a few frames and stays.
        if (changedFraction >= VideoChangedFraction) _video = true;
        else if (changedFraction < VideoChangedFraction / 2) _video = false;

        int before = Level;

        if (refused || pendingBytes > BackOffBytes)
        {
            // A refusal is not congestion to be smoothed -- it is picture already lost -- so it
            // steps down at once. Depth alone waits for the rate limit.
            if (refused || now - _lastStep >= StepDownEvery)
            {
                if (Level < MaxLevel) Level++;
                _lastStep = now;
            }
            _quietSince = DateTime.MinValue;
        }
        else if (pendingBytes <= RecoverBytes)
        {
            if (_quietSince == DateTime.MinValue) _quietSince = now;
            else if (now - _quietSince >= RecoverAfter && Level > 0)
            {
                Level--;
                _quietSince = now;   // one step per quiet period, not a run back to the top
                _lastStep = now;
            }
        }
        else
        {
            // Between the two marks: neither struggling nor clearly fine. Hold. This band is what
            // stops the loop hunting, and it is why there are two thresholds rather than one.
            _quietSince = DateTime.MinValue;
        }

        int q = Quality, f = Fps;
        Recompute();
        return Level != before || Quality != q || Fps != f;
    }

    private void Recompute()
    {
        var ladder = _video ? VideoLadder : TextLadder;
        var (qs, fs) = ladder[Math.Clamp(Level, 0, MaxLevel)];
        Quality = Math.Clamp((int)Math.Round(_qualityCeiling * qs), 20, _qualityCeiling);
        Fps = Math.Clamp((int)Math.Round(_fpsCeiling * fs), 1, _fpsCeiling);
    }
}
