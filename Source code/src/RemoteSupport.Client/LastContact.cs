using System.Text.Json;
using RemoteSupport.Shared;

namespace RemoteSupport.Client;

/// <summary>
/// The name the person at this machine last gave when asking for help, so the request window starts with it.
///
/// Clint, 2026-10-13: "the app should remember the client name, they should not need to put it in every time they
/// request support. It should default to the last name entered." It was only ever held in memory, so every restart
/// and every logon put the Windows username back. Kept per Windows user (LOCALAPPDATA, beside windows.json and
/// sounds.json) — the tray app runs as the person, so two people sharing a machine each get their own — and written
/// only from a request that was actually sent, so a name typed into a window that was then cancelled is not what
/// comes back. Like the console's <c>LastSignIn</c>, failing to read or write it is never an error anybody sees.
///
/// 2026-10-23: with the email sent beside it; and before anything is remembered, the name the machine was INSTALLED for
/// (<see cref="GivenPerson"/>) comes ahead of the Windows username.
/// </summary>
public static class LastContact
{
    /// <summary>Where it lives. Settable so a harness can point it at a folder of its own.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "contact.json");

    /// <summary>The name last given, or <paramref name="fallback"/> when none has been (or it cannot be read).</summary>
    public static string Load(string fallback)
    {
        try
        {
            if (!File.Exists(FilePath)) return fallback;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath))?.Name?.Trim();
            return string.IsNullOrEmpty(saved) ? fallback : saved;
        }
        catch { return fallback; }   // a damaged file is just the old default, as before
    }

    /// <summary>
    /// The email to start the request window with for <paramref name="name"/> (2026-10-23): the one sent with that name last
    /// time, else the install's when it is the install's name — never somebody else's email beside this person's name.
    /// </summary>
    public static string EmailFor(string name, GivenPerson? installed)
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) is { } s
                && string.Equals(s.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.Email))
                return s.Email.Trim();
        }
        catch { }
        return installed != null && string.Equals(installed.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) ? installed.Email : "";
    }

    /// <summary>Remember the name (and email) just sent with a request. A blank name is not remembered: it says nothing.</summary>
    public static void Remember(string name, string email = "")
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Saved { Name = name, Email = (email ?? "").Trim() }));
        }
        catch { }   // failing to remember must never stop the request
    }

    private sealed class Saved { public string? Name { get; set; } public string? Email { get; set; } }
}
