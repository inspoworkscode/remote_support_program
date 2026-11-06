using System.Text.Json;

namespace RemoteSupport.Tools;

/// <summary>
/// Portal login for the diagnostic harnesses. The password used to be hardcoded in each tool,
/// which put it in every copy of the source; it is now read from the environment or from a
/// git-ignored file so it stays out of the repository.
///
/// Order: RS_EMAIL / RS_PASSWORD (and RS_TOTP) environment variables, then tools/rs-credentials.json.
///
/// <para><b>Two-factor (2026-10-01, HANDOVER §72).</b> The account now needs a code as well. Put
/// the authenticator's secret — the key shown under the QR code when it was set up, spaces and all
/// — in the file as <c>"totpSecret"</c>, or in RS_TOTP, and <see cref="Code"/> computes the code
/// the phone would show, with the same arithmetic the server checks it with (Shared/Totp.cs). The
/// secret is as good as the phone, which is why the file is git-ignored.</para>
/// </summary>
internal static class RsCredentials
{
    public sealed class Creds
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string TotpSecret { get; set; } = "";

        /// <summary>The six digits for now, or null when no secret is set (a server that does not require it).</summary>
        public string? Code() => string.IsNullOrWhiteSpace(TotpSecret) ? null : RemoteSupport.Shared.Totp.Now(TotpSecret);
    }

    public static Creds Load()
    {
        var email = Environment.GetEnvironmentVariable("RS_EMAIL");
        var password = Environment.GetEnvironmentVariable("RS_PASSWORD");
        var totp = Environment.GetEnvironmentVariable("RS_TOTP") ?? "";
        if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password))
            return new Creds { Email = email, Password = password, TotpSecret = totp };

        // Walk up from the binary towards the repo root looking for tools/rs-credentials.json.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "rs-credentials.json");
            if (!File.Exists(path)) continue;
            try
            {
                var c = JsonSerializer.Deserialize<Creds>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (c != null && !string.IsNullOrWhiteSpace(c.Email) && !string.IsNullOrWhiteSpace(c.Password))
                {
                    if (!string.IsNullOrWhiteSpace(email)) c.Email = email;
                    if (totp.Length > 0) c.TotpSecret = totp;
                    return c;
                }
            }
            catch (Exception ex) { throw new InvalidOperationException($"Could not read {path}: {ex.Message}"); }
        }

        throw new InvalidOperationException(
            "No portal credentials. Either set RS_EMAIL and RS_PASSWORD (and RS_TOTP), or create " +
            "tools/rs-credentials.json containing {\"email\":\"you@example.com\",\"password\":\"...\",\"totpSecret\":\"...\"}. " +
            "That file is git-ignored on purpose.");
    }
}
