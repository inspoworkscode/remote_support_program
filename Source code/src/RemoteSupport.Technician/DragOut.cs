using System.Collections.Specialized;

namespace RemoteSupport.Technician;

/// <summary>
/// Dragging files OUT of a session: off the customer's machine and onto this one, by the same
/// gesture everything else in Windows uses.
///
/// The ordering is the whole problem, and it is not a WinForms problem. A drag hands the drop
/// target a <c>CF_HDROP</c>, which is a list of PATHS, not bytes — so by the time the technician
/// releases the mouse over their desktop, an email or a ticket, the files have to exist on this
/// machine already. Explorer does not ask us for content; it copies from the paths it was given,
/// and a path to a file that is still in flight is a failed drop with no error worth reading.
///
/// That exact problem was solved once already, for paste: the bytes move first as a file set, and
/// only then is the clipboard pointed at where they landed (§8 of the handover, "A file on the
/// clipboard is a list of PATHS, not content"). This is the same order with a different ending —
/// fetch to a staging folder, then hand the shell that folder's paths — rather than a second
/// mechanism that would have to be got right separately.
///
/// Everything here is deliberately free of the session window so <c>tools/dragoutcheck</c> can
/// drive it with no server, no session and no customer machine. A real drag is a shell
/// interaction rather than a message, so what can be asserted is what we hand the shell: the
/// staging folder, and the file list inside the data object.
/// </summary>
/// <remarks>Public so tools/dragoutcheck can drive it directly.</remarks>
public static class DragOut
{
    /// <summary>
    /// Where fetched files wait to be dragged. Under the user's temp folder, not Downloads: these
    /// are a byproduct of a gesture, and Explorer copies them out on the drop. Named so a human
    /// finding it knows what it is and that deleting it is safe.
    /// </summary>
    public static string StagingRoot { get; } =
        Path.Combine(Path.GetTempPath(), "RemoteSupport", "drag");

    /// <summary>A folder of its own per drag, so two drags in a row cannot mix their files.</summary>
    public static string NewStagingDir(string machineName)
    {
        var safe = Safe(string.IsNullOrWhiteSpace(machineName) ? "machine" : machineName);
        var dir = Path.Combine(StagingRoot, $"{safe} {DateTime.Now:HHmmss} {Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Builds the data object the shell is handed, or explains why there is not one.
    ///
    /// Every path is checked against the staging folder BEFORE it goes in, for the same reason the
    /// receiving side validates before it rewrites (§8): the names in a file set came off another
    /// machine, and a drop hands whatever is in this list to Explorer, which will happily copy
    /// C:\Windows\System32\config\SAM if we put it there. It must also EXIST — a CF_HDROP naming
    /// a file that is not there is the exact failure this class is built to prevent, and the
    /// check is cheap.
    /// </summary>
    public static DataObject? Build(string stagingDir, IEnumerable<string> paths, out string why)
    {
        why = "";
        var kept = new StringCollection();
        var full = new List<string>();
        string root;
        try { root = Path.GetFullPath(stagingDir).TrimEnd('\\') + "\\"; }
        catch (Exception ex) { why = "the staging folder is not a usable path: " + ex.Message; return null; }

        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            string abs;
            try { abs = Path.GetFullPath(p); }
            catch { why = $"'{p}' is not a usable path"; return null; }

            // Inside the staging folder, resolved — so "..\..\Windows" and a bare "C:\Windows"
            // are both out, and so is a junction pointing away from it.
            if (!abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                why = $"'{abs}' is outside the staging folder — nothing outside it may be dragged";
                return null;
            }
            if (!File.Exists(abs) && !Directory.Exists(abs))
            {
                why = $"'{abs}' was not fetched — the drop would have handed Explorer a path to nothing";
                return null;
            }
            kept.Add(abs);
            full.Add(abs);
        }

        if (kept.Count == 0) { why = "nothing was fetched"; return null; }

        var data = new DataObject();
        data.SetFileDropList(kept);
        // "Preferred DropEffect" = Copy (1). Without it some targets offer a MOVE, and a move out
        // of the staging folder means the file is gone the next time the same drag is attempted.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        return data;
    }

    /// <summary>
    /// Clears out staging folders from earlier sessions. Called at session start, never during
    /// one: a folder is still referenced by any drop that has not finished copying, and Explorer
    /// does the copying on its own schedule.
    /// </summary>
    public static void SweepOlderThan(TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;
            var cutoff = DateTime.Now - age;
            foreach (var d in Directory.GetDirectories(StagingRoot))
            {
                try { if (Directory.GetCreationTime(d) < cutoff) Directory.Delete(d, true); }
                catch { }   // in use, or gone already; either is fine
            }
        }
        catch { }
    }

    private static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
