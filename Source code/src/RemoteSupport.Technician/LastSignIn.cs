using System.Text.Json;

namespace RemoteSupport.Technician;

/// <summary>
/// The username the console last signed in with, so a technician types only their password.
///
/// Technicians, 2026-09-30: every sign-in asked for the username again. It is kept per Windows
/// user (LOCALAPPDATA, beside windows.json), per SERVER — so a harness platform or a second
/// brand never offers somebody the live account's name — and written only after a sign-in that
/// WORKED, so a typo is never what comes back tomorrow. The password is never stored.
/// </summary>
public static class LastSignIn
{
    /// <summary>Where it lives. Settable so a harness can point it at a folder of its own.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "signin.json");

    /// <summary>The username last used against this server, or "" if there is none.</summary>
    public static string For(string server)
    {
        try { return ReadAll().TryGetValue(Key(server), out var u) ? u : ""; }
        catch { return ""; }   // a missing or damaged file is just a blank field, as before
    }

    /// <summary>Remember the username that has just signed in to this server.</summary>
    public static void Remember(string server, string user)
    {
        try
        {
            var all = ReadAll();
            all[Key(server)] = user.Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }   // failing to remember must never fail the sign-in
    }

    private static Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
        var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
        return new Dictionary<string, string>(read ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string server) => (server ?? "").Trim().TrimEnd('/');
}
