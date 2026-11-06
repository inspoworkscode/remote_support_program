namespace RemoteSupport.Platform.Services;

/// <summary>
/// A short string that changes whenever this server is rebuilt, for hanging on the end of a
/// stylesheet URL.
///
/// Without it a browser is free to keep serving the `site.css` it already has, and a palette that
/// has changed on the server but not in the browser looks exactly like a feature that does not
/// work. That cost an afternoon on 2026-09-20: the light palette was live, the switch was saving
/// correctly to the database, and the page would not change colour.
///
/// Taken from the ASSEMBLY's write time rather than a GUID made at start-up. A GUID would change
/// on every restart and throw away a cache that is still perfectly good; the write time changes
/// exactly when the file it is describing might have.
/// </summary>
public static class BuildStamp
{
    public static readonly string Value = Compute();

    private static string Compute()
    {
        try
        {
            var path = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.GetLastWriteTimeUtc(path).Ticks.ToString("x");
        }
        catch { }
        // A single-file publish reports no assembly location. Start-up time is a poorer stamp —
        // it changes on every restart — but it is never wrong in the direction that matters.
        return DateTime.UtcNow.Ticks.ToString("x");
    }
}
