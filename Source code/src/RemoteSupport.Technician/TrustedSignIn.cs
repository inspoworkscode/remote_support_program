using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteSupport.Technician;

/// <summary>
/// What the server gave this console after its last two-factor code (2026-10-02 — Clint: "sign in
/// requires two factor authentication on every sign in. can we change that to require
/// authentication every 7 days?"). Sent back with the next sign-in, the code is not asked again
/// until the server says it has run out; the password always is.
///
/// <para>Kept per Windows user, per server and per username, beside <see cref="LastSignIn"/>, and
/// sealed with Windows' own per-user protection (DPAPI), so it is no use to anybody who copies the
/// file to another account or machine. The token itself names the person and their security stamp
/// and is dated by the server (AuthService.IssueTrust); a password change, Reset two-factor or Sign
/// out on the People page voids it there, whatever this file still holds.</para>
/// </summary>
public static class TrustedSignIn
{
    /// <summary>Where it lives. Settable so a harness can point it at a folder of its own.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteSupport", "trust.json");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RemoteSupport.TrustedSignIn.v1");

    /// <summary>
    /// This console's own random id, per Windows user, sent with every sign-in (2026-10-05, one
    /// sign-in at a time). It lets the server tell "the same console again" (a restart, Sign out
    /// and back) from "another computer", which signs this person's other console out and asks for
    /// the code. Not a secret and not a way in. Made the first time it is asked for.
    /// </summary>
    public static string DeviceId()
    {
        var path = Path.Combine(Path.GetDirectoryName(FilePath)!, "console-id");
        try
        {
            if (File.Exists(path))
            {
                var kept = File.ReadAllText(path).Trim();
                if (kept.Length is >= 16 and <= 64 && kept.All(char.IsAsciiLetterOrDigit)) return kept;
            }
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, id);
            return id;
        }
        catch { return ""; }   // none: the server treats every sign-in as possibly somewhere else, and asks the code
    }

    /// <summary>The token kept for this person on this server, or "" if there is none.</summary>
    public static string For(string server, string user)
    {
        try
        {
            if (!ReadAll().TryGetValue(Key(server, user), out var sealedB64)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedB64), Entropy, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }   // missing, damaged or another user's: the code is asked, as before
    }

    /// <summary>Keep the token the server has just given; an empty one forgets what was kept.</summary>
    public static void Remember(string server, string user, string token)
    {
        try
        {
            var all = ReadAll();
            if (string.IsNullOrEmpty(token)) all.Remove(Key(server, user));
            else all[Key(server, user)] = Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }   // failing to remember only means the code is asked next time
    }

    private static Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(FilePath)) return new(StringComparer.OrdinalIgnoreCase);
        var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
        return new Dictionary<string, string>(read ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    private static string Key(string server, string user) => (server ?? "").Trim().TrimEnd('/') + "|" + (user ?? "").Trim().ToLowerInvariant();
}
