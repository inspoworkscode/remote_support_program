namespace RemoteSupport.Host;

/// <summary>
/// Whether the H.264 path should encode the screen again although nothing on it has changed —
/// the one rule for "sharpening a still screen" (HANDOVER §142).
///
/// WHY. The encoder runs at a constant bit rate, so a picture that changed a lot is first sent soft,
/// and only becomes sharp as later frames spend their budget on it. On a screen that then goes still,
/// Desktop Duplication hands over nothing more, the encoder is never called again, and the technician
/// is left looking at the soft one. Reported 2026-10-15 on DESKTOP-STD8M9C: "very pixelated when
/// connecting and then slowly starts to render correctly, but is never clear." codecbench --sharp
/// measured the climb: about 38 dB on the first picture, about 47 dB after two seconds of the same
/// still frame encoded again.
///
/// WHEN. For <see cref="Window"/> after a forced complete picture or a change covering at least
/// <see cref="ArmsAt"/> of the screen. A smaller change does not re-arm it: its one frame already has
/// the whole frame's budget to itself, and a blinking text cursor — a few dozen pixels every 530 ms —
/// would otherwise keep the sharpening running on an idle screen for ever.
///
/// The frames it asks for are not changes, and the host tells the link controller so: counted as
/// changes they would put a still screen on the video ladder.
/// </summary>
public sealed class StillRefiner
{
    /// <summary>How long a still screen goes on being encoded after a change worth sharpening.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    /// <summary>The share of the screen a change must cover to start the sharpening again.</summary>
    public const double ArmsAt = 0.01;

    private DateTime _until;

    /// <summary>A frame was taken because something changed (<paramref name="share"/> of the screen) or a complete one was demanded.</summary>
    public void Saw(double share, bool forced, DateTime now)
    {
        if (forced || share >= ArmsAt) _until = now + Window;
    }

    /// <summary>True while a still screen should be encoded again.</summary>
    public bool Wanted(DateTime now) => now < _until;

    /// <summary>Stops at once — the encoder went away, or nobody is watching.</summary>
    public void Stop() => _until = default;
}
