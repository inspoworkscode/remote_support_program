using Microsoft.AspNetCore.DataProtection;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Seeing the real site before the public does (2026-10-13, <c>docs/NEXT-SESSION-PAYFAST.md</c>, "Keeping it hidden").
/// Until go-live the public front is the holding page (Caddy) and signups are off (<c>Platform:SignupsEnabled</c>). A
/// platform admin opens <c>/owner/preview</c>, which gives THIS browser a cookie for 30 days: Caddy then lets it through
/// to the real home, features, pricing and signup pages (go-live.sh: the holding matcher skips a request carrying
/// <see cref="Cookie"/>), and signup is open to it.
///
/// <para>Caddy only sees that the cookie is there; the platform checks it is one it made (Data Protection, its own
/// purpose, time-limited). So a hand-made cookie shows somebody the unfinished pages — nothing secret — and still cannot
/// sign a company up.</para>
/// </summary>
public sealed class SitePreview
{
    public const string Cookie = "rsp_preview";
    public static readonly TimeSpan Life = TimeSpan.FromDays(30);
    private readonly ITimeLimitedDataProtector _p;
    private readonly IConfiguration _cfg;

    public SitePreview(IDataProtectionProvider dp, IConfiguration cfg)
    {
        _p = dp.CreateProtector("RemoteSupport.SitePreview.v1").ToTimeLimitedDataProtector();
        _cfg = cfg;
    }

    public string Issue() => _p.Protect("preview", Life);

    /// <summary>This browser holds a preview cookie the platform made, still in date.</summary>
    public bool Holds(HttpContext ctx)
    {
        var v = ctx.Request.Cookies[Cookie];
        if (string.IsNullOrEmpty(v)) return false;
        try { return _p.Unprotect(v) == "preview"; }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
    }

    /// <summary>Signups are open to this visitor: for everybody once the platform says so, or to a preview browser.</summary>
    public bool SignupsOn(HttpContext ctx) =>
        (!bool.TryParse(_cfg["Platform:SignupsEnabled"], out var on) || on) || Holds(ctx);
}
