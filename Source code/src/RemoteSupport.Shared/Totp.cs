using System.Security.Cryptography;

namespace RemoteSupport.Shared;

/// <summary>
/// Authenticator-app codes (RFC 6238 TOTP): the six digits Microsoft Authenticator, Google
/// Authenticator and every other such app show, changing every 30 seconds.
///
/// SHA-1, six digits and thirty seconds are not choices — they are the only settings every app
/// honours. An app given anything else either refuses the QR code or, worse, accepts it and shows
/// codes that never match. SHA-1's weaknesses are about collisions; inside an HMAC with a 160-bit
/// secret they do not apply, which is why the RFC and every app still use it.
///
/// In Shared, not the platform, so the harnesses compute a code exactly as the server checks one
/// — a second copy of this arithmetic in a tool could drift and pass against a broken server.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    /// <summary>A new secret: 20 random bytes (160 bits, what RFC 4226 recommends), as Base32.</summary>
    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

    /// <summary>Which 30-second step a moment falls in.</summary>
    public static long StepAt(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The code an app shows for this secret at this step.</summary>
    public static string Code(string secretBase32, long step)
    {
        var key = Base32.Decode(secretBase32);
        Span<byte> counter = stackalloc byte[8];
        for (int i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
        var hash = HMACSHA1.HashData(key, counter);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>The code an app shows right now — for the harnesses.</summary>
    public static string Now(string secretBase32) => Code(secretBase32, StepAt(DateTime.UtcNow));

    /// <summary>
    /// The step this code belongs to, if it is right within one step either side of now (a phone a
    /// little slow or fast, or a code typed just as it changed), else null. The caller refuses a
    /// step it has already accepted, so a code seen over someone's shoulder cannot be used twice.
    /// </summary>
    public static long? Match(string secretBase32, string? code, DateTime utcNow)
    {
        var typed = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (typed.Length != Digits || string.IsNullOrEmpty(secretBase32)) return null;
        long now = StepAt(utcNow);
        for (long s = now - 1; s <= now + 1; s++)
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(Code(secretBase32, s)),
                    System.Text.Encoding.ASCII.GetBytes(typed)))
                return s;
        return null;
    }

    /// <summary>
    /// What a QR code carries so an app can add the account: the issuer (the company) and the
    /// account name are what the app lists it under.
    /// </summary>
    public static string Uri(string issuer, string account, string secretBase32)
    {
        string e(string s) => System.Uri.EscapeDataString(s);
        return $"otpauth://totp/{e(issuer)}:{e(account)}?secret={secretBase32}&issuer={e(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>The secret in groups of four, for typing into an app by hand.</summary>
    public static string Grouped(string secretBase32) =>
        string.Join(" ", Enumerable.Range(0, (secretBase32.Length + 3) / 4)
            .Select(i => secretBase32.Substring(i * 4, Math.Min(4, secretBase32.Length - i * 4))));
}

/// <summary>RFC 4648 Base32 without padding — the alphabet authenticator apps expect.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        var sb = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>Forgiving of what people type: spaces, dashes, lower case and padding are ignored.</summary>
    public static byte[] Decode(string text)
    {
        var clean = text.ToUpperInvariant().Where(c => c != ' ' && c != '-' && c != '=').ToArray();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            int v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException($"'{c}' is not a Base32 character.");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { output.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return output.ToArray();
    }
}
