namespace RemoteSupport.Shared;

/// <summary>
/// Clipboard TEXT on the ticket, without burying it (roadmap 3.5.3). Every movement is recorded,
/// because seamless must not mean invisible; but a line per Ctrl+C would leave a ticket that is
/// mostly clipboard, so movements are added up per direction and reported once per quiet period.
/// The count and the character total survive; nothing else is ever kept — this is the feature
/// where the clipboard is a password.
///
/// <para><b>Why it is a class of its own (2026-09-29).</b> It lived inside <c>HostEngine</c> and
/// reported only when the NEXT movement arrived a minute after the first, or when the session
/// ended — and the end-of-session report was sent after the socket had closed. So a session with
/// one burst of copies never put a line on its ticket at all. Here a timer reports the minute
/// whether or not anything else happens, and <see cref="Flush"/> is what the host calls when it is
/// told the session is over, while it can still be heard. <c>tools/clipledger</c> drives it.</para>
/// </summary>
public sealed class ClipLedger : IDisposable
{
    /// <summary>fromMachine, how many movements, how many characters, and the refusal if it was one.</summary>
    public delegate void Reporter(bool fromMachine, int times, long chars, string? error);

    private readonly TimeSpan _quiet;
    private readonly Reporter _report;
    private readonly object _lock = new();
    private readonly Timer _timer;
    private int _outTimes, _inTimes;
    private long _outChars, _inChars;
    private bool _pending, _disposed;

    public ClipLedger(TimeSpan quiet, Reporter report)
    {
        _quiet = quiet;
        _report = report;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// One movement: <paramref name="fromMachine"/> when it left the customer's machine. A refusal
    /// is reported at once, on its own line — it is the one clipboard event somebody may be
    /// actively puzzled by, and it must not wait for a minute to pass.
    /// </summary>
    public void Note(bool fromMachine, long chars, string? error = null)
    {
        if (error != null) { _report(fromMachine, 1, chars, error); return; }
        lock (_lock)
        {
            if (_disposed) return;
            if (fromMachine) { _outTimes++; _outChars += chars; } else { _inTimes++; _inChars += chars; }
            // The period starts at the first movement it will cover, not at the latest: a steady
            // trickle must still produce a line a minute rather than postponing it for ever.
            if (!_pending) { _pending = true; _timer.Change(_quiet, Timeout.InfiniteTimeSpan); }
        }
    }

    /// <summary>Reports whatever is held, now. Nothing held, nothing reported.</summary>
    public void Flush()
    {
        int outTimes, inTimes; long outChars, inChars;
        lock (_lock)
        {
            if (!_pending) return;
            outTimes = _outTimes; inTimes = _inTimes; outChars = _outChars; inChars = _inChars;
            _outTimes = _inTimes = 0; _outChars = _inChars = 0;
            _pending = false;
            if (!_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        if (outTimes > 0) _report(true, outTimes, outChars, null);
        if (inTimes > 0) _report(false, inTimes, inChars, null);
    }

    /// <summary>Stops the timer. Does NOT flush — call <see cref="Flush"/> first if that is wanted.</summary>
    public void Dispose()
    {
        lock (_lock) { _disposed = true; }
        _timer.Dispose();
    }
}
