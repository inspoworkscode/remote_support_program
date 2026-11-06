namespace RemoteSupport.Platform.Services;

/// <summary>
/// The browser's half of signing in (2026-10-01), shared by the portal's sign-in page and the
/// technicians' download page (/tech/{slug}) so the two cannot drift: the password, then the code
/// step or the set-up step, then — once, after setting up — the recovery codes.
///
/// The page carries <see cref="Mode"/> and <see cref="Pending"/> in hidden fields between posts.
/// The pending id is a random 192-bit value that lives five minutes, names nobody, and is spent by
/// the step it belongs to; the password is never posted twice.
/// </summary>
public sealed class WebSignIn
{
    /// <summary>"" (asking for the password), "code", "enrol", or "saved" (showing recovery codes, already signed in).</summary>
    public string Mode { get; set; } = "";
    public string Pending { get; set; } = "";
    public string Secret { get; set; } = "";
    public string Qr { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>Why the code is asked on a trusted browser: they are signed in on another one, which this will sign out.</summary>
    public string Notice { get; set; } = "";
    public IReadOnlyList<string> RecoveryCodes { get; set; } = Array.Empty<string>();
    /// <summary>Set when the person is signed in; the page then issues the cookie.</summary>
    public TechSession? Session { get; set; }
    /// <summary>Where "Continue" goes after the recovery codes.</summary>
    public string ContinueTo { get; set; } = "";

    /// <summary>
    /// The browser's trusted-device cookie (2026-10-02): the tokens <see cref="AuthService.IssueTrust"/>
    /// gave this browser after a code, one per person who has signed in on it, so two people sharing
    /// an office PC are each asked for a code once a week rather than every time the other signs in.
    /// </summary>
    public const string TrustCookie = "rsp_trust";
    private const int TrustKept = 5;

    /// <summary>
    /// This browser's own random id (2026-10-05, one sign-in at a time), so signing in again on the
    /// SAME browser is not "somewhere else". Not a secret and not a way in: it only decides whether
    /// the code is asked again. A year, and set the first time it is needed.
    /// </summary>
    public const string DeviceCookie = "rsp_device";

    public static string DeviceOf(HttpContext http)
    {
        var d = AuthService.CleanDevice(http.Request.Cookies[DeviceCookie]);
        if (d.Length > 0) return d;
        d = Passwords.RandomToken(16);
        http.Response.Cookies.Append(DeviceCookie, d, new CookieOptions
        {
            HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true
        });
        return d;
    }

    public static async Task<WebSignIn> StepAsync(AuthService auth, string mode, string pending, string email, string password, string code,
        string address, HttpContext? http = null)
    {
        var kept = (http?.Request.Cookies[TrustCookie] ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        var r = mode switch
        {
            "code" => await auth.CodeAsync(pending, code, address),
            "enrol" => await auth.EnrolAsync(pending, code, address),
            _ => await auth.PasswordAsync(email, password, null, address, "browser", kept, http == null ? null : DeviceOf(http))
        };
        if (http != null && r.Trust.Length > 0 && r.Session != null) KeepTrust(auth, http, kept, r.Trust, r.Session.TechnicianId);
        var w = new WebSignIn { Error = r.Error, Notice = r.Notice, Pending = r.Pending, Secret = r.Secret };
        switch (r.Step)
        {
            case SignInStep.Done:
                w.Session = r.Session;
                // The console's token is not wanted in a browser; the cookie is the sign-in here.
                if (r.Token.Length > 0) auth.Revoke(r.Token);
                if (r.RecoveryCodes is { Count: > 0 }) { w.Mode = "saved"; w.RecoveryCodes = r.RecoveryCodes; }
                break;
            case SignInStep.Code: w.Mode = "code"; break;
            case SignInStep.Enrol: w.Mode = "enrol"; w.Qr = QrImage.DataUri(r.OtpUri); break;
            default: w.Mode = ""; break;
        }
        return w;
    }

    /// <summary>This person's new token first, then the others still good — never two of one person's.</summary>
    private static void KeepTrust(AuthService auth, HttpContext http, IEnumerable<string> kept, string fresh, int technicianId)
    {
        var others = kept.Where(t => auth.TrustOwner(t) is int id && id != technicianId).Take(TrustKept - 1);
        http.Response.Cookies.Append(TrustCookie, string.Join('.', new[] { fresh }.Concat(others)), new CookieOptions
        {
            HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/",
            Expires = DateTimeOffset.UtcNow.Add(auth.TrustFor), IsEssential = true
        });
    }
}
