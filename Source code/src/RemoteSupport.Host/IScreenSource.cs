using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// Where a frame of the customer's desktop comes from. Two implementations exist and they must be
/// interchangeable at runtime, because the fast one is not always available: GDI BitBlt
/// (<see cref="ScreenCapture"/>) works everywhere and is slow, Desktop Duplication
/// (<see cref="DxgiCapture"/>) is fast and refuses to start in some session and driver states.
/// Losing capture entirely is a worse fault than a slow picture, so the slow one stays.
/// </summary>
public interface IScreenSource : IDisposable
{
    /// <summary>What to write in a trace line so a log says which path actually ran.</summary>
    string Name { get; }

    List<MonitorInfo> Monitors { get; }

    /// <summary>Re-enumerate monitors. Called at start and periodically by the capture loop.</summary>
    void Refresh();

    /// <summary>
    /// Let go of everything held only in order to stream, because nobody is watching.
    ///
    /// Desktop Duplication is EXCLUSIVE per output: while this process holds it, nothing else on
    /// the machine can duplicate that screen, and the next thing to try gets "access denied -
    /// another process already has this output duplicated". A session with no subscribed viewer
    /// was holding that, and a graphics device with it, for as long as the session stayed open --
    /// paying for a picture nobody was looking at and locking out anything else that wanted the
    /// screen. That is what stopped `codecbench` running on a machine over a live session, which
    /// is how it was found.
    ///
    /// Must be safe to call repeatedly, and the next <see cref="CaptureTiles"/> must rebuild
    /// whatever it needs. Called ONLY from the capture thread, because the duplication and the
    /// device belong to it.
    /// </summary>
    void ReleaseIdle();

    /// <summary>
    /// Captures the monitor and returns the changed tiles as JPEGs. <paramref name="forceFull"/>
    /// ignores whatever the source knows about what changed and returns every tile.
    /// </summary>
    List<(int x, int y, int w, int h, byte[] jpeg)> CaptureTiles(int monitor, int jpegQuality, bool forceFull);

    /// <summary>
    /// Where the time went in the most recent <see cref="CaptureTiles"/> call. The capture loop
    /// never reads this; it exists so `tools/codecbench` can say which half of a slow frame is
    /// slow, which is the difference between a capture problem and an encoder problem.
    /// </summary>
    CaptureTiming LastTiming { get; }
}

/// <summary>Cost of one captured frame, in milliseconds and bytes. See <see cref="IScreenSource.LastTiming"/>.</summary>
public readonly record struct CaptureTiming(
    double GrabMs,      // getting the pixels out of Windows
    double DiffMs,      // deciding which tiles changed
    double EncodeMs,    // JPEG
    int TilesChanged,
    int TilesTotal,
    long PayloadBytes);
