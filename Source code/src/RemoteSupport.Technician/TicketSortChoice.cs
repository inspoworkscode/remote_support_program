using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// The order the console's ticket list was last put in (2026-10-13), so it opens that way next time. Per Windows user,
/// beside windows.json and signin.json; the portal remembers its own in the browser. Failing to read or write it is
/// never an error anybody sees — the list is simply in its usual order.
/// </summary>
public static class TicketSortChoice
{
    /// <summary>Where it lives. Settable so a harness can point it at a folder of its own.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "ticketsort.txt");

    public static string Load()
    {
        try { return File.Exists(FilePath) ? TicketSort.Of(File.ReadAllText(FilePath).Trim()) : TicketSort.Updated; }
        catch { return TicketSort.Updated; }
    }

    public static void Remember(string key)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, TicketSort.Of(key));
        }
        catch { }
    }
}
