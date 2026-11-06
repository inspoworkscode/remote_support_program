using System.Media;
using System.Text.Json;
using RemoteSupport.Shared;

namespace RemoteSupport.UI;

/// <summary>
/// Plays the product's two sounds (<see cref="Chimes"/>) and remembers whether this person has
/// muted them (2026-10-07).
///
/// <see cref="Ding"/> for a chat message; <see cref="Ringing"/> on while somebody is waiting for an
/// answer and off the moment nobody is. Muted is per Windows user, in
/// <c>%LOCALAPPDATA%\RemoteSupport\sounds.json</c>, the same place window positions live — not on
/// the server, so it does not follow the person to another machine.
///
/// One sound at a time: Windows plays one per process, so a ding arriving mid-ring cuts that ring
/// short. The next ring is four seconds away, which is the trade for needing nothing bought in.
///
/// Like <see cref="WindowPlacement"/>, nothing here ever throws: a machine with no sound device
/// must still get its chat.
/// </summary>
public static class Sounds
{
    private static SoundPlayer? _ding, _ring;
    private static System.Windows.Forms.Timer? _ringTimer;
    private static DateTime _lastDing;
    private static bool? _muted;

    private static string Path_ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "sounds.json");

    /// <summary>Raised on the thread that changed it, after <see cref="Muted"/> has its new value.</summary>
    public static event Action? MutedChanged;

    /// <summary>"ding" or "ring", each time one is actually played (never while muted). For
    /// <c>tools/soundcheck</c>, which has to know what played without listening.</summary>
    public static event Action<string>? Played;

    /// <summary>Whether somebody is waiting, as <see cref="Ringing"/> was last told — muted or not.</summary>
    public static bool IsRinging => _ringTimer?.Enabled == true;

    public static bool Muted
    {
        get
        {
            if (_muted == null)
            {
                try { _muted = File.Exists(Path_) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(Path_))?.Muted == true; }
                catch { _muted = false; }
            }
            return _muted.Value;
        }
        set
        {
            if (Muted == value) return;
            _muted = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
                File.WriteAllText(Path_, JsonSerializer.Serialize(new Saved { Muted = value }));
            }
            catch { }
            // Muting while it rings silences it now, not after the ring in progress.
            if (value) try { _ring?.Stop(); } catch { }
            MutedChanged?.Invoke();
        }
    }

    /// <summary>
    /// A chat message has arrived. Several in the same moment — a pasted paragraph sent as lines, or
    /// a burst after a reconnect — ding once.
    /// </summary>
    public static void Ding()
    {
        if (Muted || DateTime.UtcNow - _lastDing < TimeSpan.FromSeconds(1.2)) return;
        _lastDing = DateTime.UtcNow;
        Play(ref _ding, Chimes.Ding);
        Played?.Invoke("ding");
    }

    /// <summary>
    /// On while somebody is waiting; rings at once, then every <see cref="Chimes.RingEverySeconds"/>.
    /// Call it with the truth every time the waiting changes — repeating "on" does not restart the
    /// ring. Must be called on the UI thread (the repeat is a Forms timer).
    /// </summary>
    public static void Ringing(bool on)
    {
        if (on == (_ringTimer?.Enabled == true)) return;
        if (!on)
        {
            _ringTimer!.Stop();
            try { _ring?.Stop(); } catch { }
            return;
        }
        if (_ringTimer == null)
        {
            _ringTimer = new System.Windows.Forms.Timer { Interval = Chimes.RingEverySeconds * 1000 };
            _ringTimer.Tick += (_, _) => RingOnce();
        }
        _ringTimer.Start();
        RingOnce();
    }

    private static void RingOnce()
    {
        if (Muted) return;
        Play(ref _ring, Chimes.Ring);
        Played?.Invoke("ring");
    }

    private static void Play(ref SoundPlayer? player, Func<byte[]> wav)
    {
        try
        {
            if (player == null) { player = new SoundPlayer(new MemoryStream(wav())); player.Load(); }
            player.Play();
        }
        catch { }
    }

    private sealed class Saved { public bool Muted { get; set; } }
}
