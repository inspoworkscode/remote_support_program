using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// Turns what the host reported about a machine's sound into plain-English reasons it is silent.
///
/// THIS IS THE FEATURE. A device list is data; a technician can read one themselves and most
/// will not know that Windows keeps three separate defaults, or that an endpoint can be muted
/// independently of the volume they can see, or that "Display Audio" means the sound is going to
/// a television. Neither TeamViewer nor AnyDesk says any of it — they stream the sound and leave
/// you to guess. The findings below are what make this worth having.
///
/// Kept separate from the window on purpose: these rules are pure, so they can be exercised
/// against made-up machines that would be a nuisance to arrange in real life — a machine with no
/// default device, one whose defaults disagree, one whose speakers are unplugged. See
/// `audiolocal --findings`.
/// </summary>
public static class AudioFindings
{
    public static List<string> For(AudioDevicesMsg m)
    {
        var findings = new List<string>();
        var play = m.Devices.Where(d => !d.IsCapture).ToList();
        var active = play.Where(d => d.State == "ACTIVE").ToList();
        var defConsole = play.FirstOrDefault(d => d.IsDefaultConsole);
        var defMulti = play.FirstOrDefault(d => d.IsDefaultMultimedia);
        var def = defMulti ?? defConsole;

        if (play.Count == 0)
            findings.Add("Windows can see NO playback devices at all on that machine.");
        else if (active.Count == 0)
            findings.Add("No playback device is active. Every one is disabled, unplugged or absent — "
                       + "that alone is why there is no sound.");

        if (def == null && play.Count > 0)
            findings.Add("There is no default playback device, so applications have nowhere to send sound.");

        if (def is { Muted: true })
            findings.Add($"The default playback device ({def.Name}) is MUTED.");

        if (def is { VolumePercent: >= 0 and <= 5 })
            findings.Add($"The default playback device's volume is {def.VolumePercent}%.");

        // The classic "it works in Teams but not in YouTube": Windows has three defaults and
        // nothing in its own interface makes it obvious when they disagree.
        if (defConsole != null && defMulti != null && defConsole.Id != defMulti.Id)
            findings.Add($"System sounds go to \"{defConsole.Name}\" but music and video go to "
                       + $"\"{defMulti.Name}\". Those are different devices, which is why some things "
                       + "are audible and others are not.");

        // The television case. This project's own test machine sat like this for weeks: everything
        // reported healthy, nothing audible, because the default output was an HDMI television.
        if (def != null && LooksLikeDisplayAudio(def))
            findings.Add($"The default output is \"{def.Name}\", which is display or television audio. "
                       + "If nothing is connected to it, or the screen is off, the sound is going nowhere "
                       + "even though Windows reports everything as working.");

        foreach (var a in m.Apps.Where(a => a.Muted))
            findings.Add($"{a.Name} is MUTED in the volume mixer — that application alone will be silent.");
        foreach (var a in m.Apps.Where(a => !a.Muted && a.VolumePercent is >= 0 and <= 5))
            findings.Add($"{a.Name} is set to {a.VolumePercent}% in the volume mixer.");

        foreach (var s in m.Services.Where(s => !s.Contains("running", StringComparison.OrdinalIgnoreCase)))
            findings.Add($"Audio service {s} — nothing will play until that is running.");

        return findings;
    }

    /// <summary>
    /// Whether an endpoint is a screen or television rather than speakers. Judged on the adaptor
    /// as well as the name, because the friendly name is often just the model number of somebody's
    /// monitor while the adaptor says "HD Audio Driver for Display Audio" plainly.
    /// </summary>
    public static bool LooksLikeDisplayAudio(AudioDeviceInfo d)
    {
        var hay = (d.Name + " " + d.Adaptor).ToLowerInvariant();
        return hay.Contains("display audio") || hay.Contains("hdmi") || hay.Contains("displayport")
            || hay.Contains(" tv") || hay.Contains("television");
    }
}
