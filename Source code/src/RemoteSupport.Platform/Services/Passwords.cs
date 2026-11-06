using System.Security.Cryptography;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Password hashing for technician accounts. Public rather than internal so the tools that
/// create accounts - tools/testtech - can produce a hash the server will actually accept, instead
/// of carrying their own copy of the derivation that could silently drift out of step with this
/// one and write accounts nobody can sign in to.
/// </summary>
public static class Passwords
{
    /// <summary>
    /// PBKDF2-SHA256 rounds for every password made from now (2026-10-07, roadmap 4.4, §72's "left for soon
    /// after" item 2): OWASP's figure. It was 120,000, and those hashes still verify — see <see cref="RoundsOf"/>.
    /// </summary>
    public const int Rounds = 600_000;

    /// <summary>What every hash made before 2026-10-07 used: a salt with no rounds written on it means this.</summary>
    public const int OldRounds = 120_000;

    /// <summary>
    /// A salt and its hash. The rounds travel IN the salt string — <c>"600000$&lt;base64&gt;"</c> — so a stored
    /// hash says how it was made and no column had to be added; a bare base64 salt is an old one, at
    /// <see cref="OldRounds"/>. The salt string as a whole stays what it always was to everything else: an
    /// opaque value that changes when the password does (the remembered unattended passwords key on it).
    /// </summary>
    public static (string salt, string hash) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return ($"{Rounds}${Convert.ToBase64String(salt)}", Convert.ToBase64String(Derive(password, salt, Rounds)));
    }

    public static bool Verify(string password, string salt, string hash)
    {
        if (string.IsNullOrEmpty(salt) || string.IsNullOrEmpty(hash)) return false;
        var (rounds, bytes) = Parse(salt);
        if (bytes == null) return false;
        var h = Derive(password, bytes, rounds);
        return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(hash));
    }

    /// <summary>The rounds a stored salt was made with.</summary>
    public static int RoundsOf(string salt) => Parse(salt).rounds;

    /// <summary>
    /// Made with fewer rounds than <see cref="Rounds"/>: worth making again from the password just proved — at a
    /// person's next sign-in (AuthService), which is the only moment the server holds the password.
    /// NOT a machine's unattended password: remembered passwords are keyed on its salt, and making it again
    /// would forget everybody's. Those move to the new rounds when somebody at the machine next sets one.
    /// </summary>
    public static bool NeedsUpgrade(string salt) => !string.IsNullOrEmpty(salt) && RoundsOf(salt) < Rounds;

    private static (int rounds, byte[]? bytes) Parse(string salt)
    {
        try
        {
            var dollar = salt.IndexOf('$');
            if (dollar < 0) return (OldRounds, Convert.FromBase64String(salt));
            if (!int.TryParse(salt[..dollar], out var rounds) || rounds < 1000) return (Rounds, null);
            return (rounds, Convert.FromBase64String(salt[(dollar + 1)..]));
        }
        catch (FormatException) { return (Rounds, null); }
    }

    private static byte[] Derive(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, rounds, HashAlgorithmName.SHA256, 32);

    public static string RandomToken(int bytes = 24) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>
    /// What is wrong with a new password, or empty if nothing — the ONE rule, wherever a password is
    /// chosen: added on the command line, set from an invitation or a reset link, or changed by its
    /// owner (2026-10-01). Eight characters, as it always was: the second factor is the strength now.
    /// </summary>
    public static string Problem(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8) return "A password must be at least 8 characters.";
        if (password.Length > 200) return "That password is too long.";
        if (!string.IsNullOrEmpty(email) && password.Trim().Equals(email.Trim(), StringComparison.OrdinalIgnoreCase))
            return "A password cannot be your email address.";
        return "";
    }
}
