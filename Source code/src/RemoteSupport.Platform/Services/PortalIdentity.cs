using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The one place that turns a signed-in technician into a cookie, and a cookie back into a person.
///
/// Before stage 3.6.3 the portal had no idea who you were — only which COMPANY you belonged to.
/// Every page resolved a tenant from the cookie and nothing else, which was fine while only
/// administrators could sign in and is the reason "a technician sees only their own tickets" was
/// not expressible at all. Sign-in happens in two places now (the login form, and the hand-off
/// from the console), so the claims are built here rather than twice.
/// </summary>
public static class PortalIdentity
{
    public const string TenantIdClaim = "tenantId";
    public const string TechnicianIdClaim = "technicianId";
    /// <summary>The boxes a person holds, as a number. Renamed from <see cref="OldTenantRoleClaim"/> — see <see cref="RoleOf"/>.</summary>
    public const string TenantRolesClaim = "tenantRoles";
    /// <summary>The single role of before 2026-09-29, numbered 0–4. Read, never written.</summary>
    public const string OldTenantRoleClaim = "tenantRole";
    public const string SlugClaim = "slug";
    public const string AppearanceClaim = "appearance";
    /// <summary>
    /// The person's security stamp when this cookie was issued (2026-10-01). Checked on every
    /// request in Program.cs; a cookie whose stamp no longer matches is signed out — that is how
    /// removing somebody, or changing their password, reaches a browser they left signed in.
    /// </summary>
    public const string StampClaim = "stamp";

    public static string StampOf(ClaimsPrincipal user) => user.FindFirst(StampClaim)?.Value ?? "";

    /// <summary>
    /// Which sign-in this cookie is (2026-10-05, one at a time): "b:{id}" for a browser sign-in,
    /// "c:{id}" for one the console's Tickets button made. See <see cref="AuthService.CookieCheckAsync"/>.
    /// A page that re-issues the cookie must carry it over, or it would sign itself out.
    /// </summary>
    public const string SignInClaim = "signin";

    public static string SignInOf(ClaimsPrincipal user) => user.FindFirst(SignInClaim)?.Value ?? "";

    /// <summary>
    /// "light", "dark", or empty to follow the company. Carried in the cookie so the layout can
    /// paint the right palette without a database read on every page — and re-issued when somebody
    /// changes it, which is the only way a claim stays honest.
    /// </summary>
    public static string AppearanceOf(ClaimsPrincipal user) => Clean(user.FindFirst(AppearanceClaim)?.Value);

    /// <summary>Anything that is not light or dark means "follow the company".</summary>
    public static string Clean(string? v)
    {
        var s = (v ?? "").Trim().ToLowerInvariant();
        return s is "light" or "dark" ? s : "";
    }

    /// <summary>
    /// The ASP.NET role names for the boxes a person holds (<see cref="TenantRole"/>).
    ///
    /// Administrators are still "tenant" and billing users still "billing" — every policy and page
    /// convention in Program.cs is written against those two strings, and renaming them to
    /// something tidier would be a rename with no benefit and a lot of blast radius. Technicians
    /// are the new one.
    /// </summary>
    public static IEnumerable<string> RoleNames(TenantRole role)
    {
        // One ASP.NET role per box held, so a technician who is also the office administrator
        // passes the policies of both. "tenant" is the supervisor's and the owner's alike.
        if (role.Has(TenantRole.Admin | TenantRole.Owner)) yield return "tenant";
        if (role.Has(TenantRole.Billing)) yield return "billing";
        if (role.Has(TenantRole.Technician)) yield return "technician";
    }

    /// <summary>Everything the portal needs to know about who is signed in.</summary>
    public static ClaimsPrincipal Build(TechSession session, string slug, string appearance = "")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, session.DisplayName),
            new(ClaimTypes.Email, session.Email),
            new(TenantIdClaim, session.TenantId.ToString()),
            new(TechnicianIdClaim, session.TechnicianId.ToString()),
            // The boxes are carried as their own claim as well as roles, so a page can ask what
            // somebody IS without a chain of IsInRole tests against strings.
            new(TenantRolesClaim, ((int)session.Role).ToString()),
            new(SlugClaim, slug),
            new(AppearanceClaim, Clean(appearance)),
            new(StampClaim, session.Stamp)
        };
        if (session.SignIn.Length > 0) claims.Add(new Claim(SignInClaim, session.SignIn));
        claims.AddRange(RoleNames(session.Role).Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>Where a person lands when they sign in, which is not the same page for everyone.</summary>
    public static string HomePage(TenantRole role) =>
        role.Has(TenantRole.Admin | TenantRole.Owner) ? "/portal"
        : role.Has(TenantRole.Billing) ? "/portal/billing"
        // A technician cannot reach /portal — that page is the company dashboard. Their home is
        // the work in front of them.
        : "/portal/tickets";

    /// <summary>
    /// The boxes a cookie says this person holds.
    ///
    /// <para>The claim was renamed when roles became tick boxes (2026-09-29), because the NUMBERS
    /// changed: an old cookie's 1 meant supervisor and would now read as technician, and its 0
    /// would read as no role at all. So a cookie with the new claim is read as it stands, one with
    /// only the old claim is translated from the old numbers, and one with neither — issued before
    /// 3.6.3, when only an administrator could sign in at all — is read from the ASP.NET roles it
    /// was issued with. Those are in every cookie ever issued, so nothing is GUESSED: the fallback
    /// used to be "administrator" whatever the cookie said, and a guess that grants everything is
    /// the wrong way round for a guess. Anything unreadable is the least a person can hold.</para>
    /// </summary>
    public static TenantRole RoleOf(ClaimsPrincipal user)
    {
        if (int.TryParse(user.FindFirst(TenantRolesClaim)?.Value, out var r)
            && r != 0 && ((TenantRole)r & ~TenantRoles.All) == 0)
            return (TenantRole)r;
        if (int.TryParse(user.FindFirst(OldTenantRoleClaim)?.Value, out var old))
            return FromOldNumber(old);
        var fromRoles = (user.IsInRole("tenant") ? TenantRole.Admin : 0)
                      | (user.IsInRole("billing") ? TenantRole.Billing : 0)
                      | (user.IsInRole("technician") ? TenantRole.Technician : 0);
        return fromRoles != TenantRole.None ? fromRoles : TenantRole.Technician;
    }

    /// <summary>What the numbers 0–4 meant before 2026-09-29, and what the migration turned them into.</summary>
    public static TenantRole FromOldNumber(int old) => old switch
    {
        1 => TenantRole.Admin,
        2 => TenantRole.Billing,
        3 => TenantRole.Owner,
        4 => TenantRole.OwnerSupervisor,
        _ => TenantRole.Technician
    };

    public static int TechnicianIdOf(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst(TechnicianIdClaim)?.Value, out var id) ? id : 0;
}
