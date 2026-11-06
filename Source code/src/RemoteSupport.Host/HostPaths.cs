using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// Turns the sentinels in <see cref="RemotePaths"/> into real folders on this machine.
///
/// ONE place, called where a path ARRIVES rather than where it is used. That is the whole point:
/// the sentinel used to be translated for a dropped file set and nowhere else, so listing
/// %DESKTOP% or pulling a file out of it reached <c>FileTransfer</c> untranslated, where .NET
/// treated it as a relative path and resolved it against the host's working directory —
///
///   Could not find a part of the path 'C:\Program Files\Anywhere IT\Remote Support\%DESKTOP%'
///
/// — which is docs/BUGS.md #6, and reads as a broken transfer rather than an unresolved name.
/// Resolving at each use site is how it got half done; resolving on arrival is why it cannot.
///
/// Everything goes through <see cref="UserContext.ProfileDirectory"/>, the INTERACTIVE user's
/// profile. Environment.SpecialFolder would give SYSTEM's, which is the fault this whole family
/// of paths exists to avoid (§4 of the handover).
/// </summary>
/// <remarks>Public because tools/dragoutcheck asserts on it without a machine.</remarks>
public static class HostPaths
{
    /// <summary>
    /// A path as the technician's end sent it, resolved for this machine. Anything that is not a
    /// sentinel is handed back untouched — including empty, "\" and "/", which FileTransfer.List
    /// reads as "the drives".
    /// </summary>
    public static string? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '%') return path;

        var resolved = path switch
        {
            _ when Is(path, RemotePaths.Desktop) => Under("Desktop"),
            _ when Is(path, RemotePaths.Downloads) => Under("Downloads"),
            _ when Is(path, RemotePaths.Documents) => Under("Documents"),
            _ when Is(path, RemotePaths.PasteStaging) => PasteStaging(),
            _ when Is(path, RemotePaths.PrintStaging) => PrintStaging(),
            _ => null
        };

        if (resolved == null)
        {
            // An unknown sentinel must not be passed on as a relative path — that is exactly the
            // bug above, wearing a different name. Say so instead.
            if (RemotePaths.IsSentinel(path)) HostTrace.Write($"paths: {path} is not a name this host knows");
            return path;
        }

        HostTrace.Write($"paths: {path} -> {resolved}");
        return resolved;
    }

    /// <summary>Resolves every path in a list, for the pull of a whole set.</summary>
    public static List<string> ResolveAll(IEnumerable<string> paths) =>
        paths.Select(p => Resolve(p) ?? p).ToList();

    private static bool Is(string path, string sentinel) =>
        string.Equals(path, sentinel, StringComparison.OrdinalIgnoreCase);

    private static string Under(string folder) => Path.Combine(UserContext.ProfileDirectory, folder);

    /// <summary>
    /// A folder of its own per paste, under the interactive user's temp.
    ///
    /// Per paste, not one shared folder, for the same reason <c>DragOut.NewStagingDir</c> does it:
    /// two pastes in a row must not mix their files, and the second must not silently get a
    /// collision-renamed name when the first is still sitting there.
    ///
    /// The interactive user's temp rather than SYSTEM's, because Explorer will be reading these
    /// paths as that user when the paste happens — a staging folder under SYSTEM's profile is one
    /// they cannot open, which is this whole family of paths' original fault (§4).
    /// </summary>
    private static string PasteStaging()
    {
        var dir = Path.Combine(UserContext.ProfileDirectory, "AppData", "Local", "Temp",
                               "RemoteSupport", "paste", DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    /// <summary>
    /// Where a technician's file waits to be printed on the person's printer (remote printing,
    /// 2026-10-06): a folder of its own per job under their temp — the helper that prints it runs AS
    /// them and must be able to open it. <see cref="IsPrintStaging"/> is the only place the host prints from.
    /// </summary>
    public static string PrintInRoot() =>
        Path.Combine(UserContext.ProfileDirectory, "AppData", "Local", "Temp", "RemoteSupport", "print", "in");

    private static string PrintStaging()
    {
        var dir = Path.Combine(PrintInRoot(), DateTime.Now.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    /// <summary>True only for a file inside the print staging folder — never a path that merely starts the
    /// same way, and never one that climbs out of it with "..".</summary>
    public static bool IsPrintStaging(string? path, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var r = Path.GetFullPath(root ?? PrintInRoot()).TrimEnd('\\') + "\\";
            return full.StartsWith(r, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch { return false; }
    }

    /// <summary>
    /// Clears out paste staging from earlier sessions. Called at session start, never during one:
    /// a folder is still being read by any paste Explorer has not finished copying, and Explorer
    /// does that on its own schedule rather than ours. The mirror of DragOut.SweepOlderThan.
    /// </summary>
    public static void SweepPasteStaging(TimeSpan age)
    {
        try
        {
            var root = Path.Combine(UserContext.ProfileDirectory, "AppData", "Local", "Temp", "RemoteSupport", "paste");
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.Now - age;
            foreach (var d in Directory.GetDirectories(root))
            {
                try { if (Directory.GetCreationTime(d) < cutoff) Directory.Delete(d, true); }
                catch { }   // in use, or gone already; either is fine
            }
        }
        catch { }
    }
}
