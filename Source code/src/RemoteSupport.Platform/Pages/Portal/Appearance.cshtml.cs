using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Light or dark, chosen by the PERSON. Stage 3.6.9.
///
/// It has no page of its own to look at — it saves the choice, re-issues the cookie so the very
/// next render is in the new palette, and sends them back where they were. A settings page for one
/// switch would be a place to go to do something that should take one click.
///
/// A POST rather than a link, because it changes something. It is a cosmetic preference and not a
/// dangerous one, but a GET that writes to the database is the kind of thing that gets tripped by a
/// link prefetcher and then has to be explained.
/// </summary>
public sealed class AppearanceModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly TenantService _tenants;
    public AppearanceModel(TenantService t, IDbContextFactory<PlatformDb> dbf) : base(t)
    { _dbf = dbf; _tenants = t; }

    public IActionResult OnGet() => Redirect("/portal");

    public async Task<IActionResult> OnPostAsync(string? theme, string? returnUrl)
    {
        if (!await LoadAsync()) return Redirect("/login");

        var wanted = PortalIdentity.Clean(theme);

        // Saved against the PERSON, so it follows them to any browser and to the console. A cookie
        // or localStorage would make it a property of this machine, which is exactly the lock-in
        // this stage exists to remove — somebody who prefers dark prefers it on the workshop PC too.
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var me = await db.Technicians.FirstOrDefaultAsync(t => t.Id == TechnicianId && t.TenantId == TenantId);
            if (me != null) { me.Appearance = wanted; await db.SaveChangesAsync(); }
        }

        // The cookie carries the appearance so the layout needs no database read per page — which
        // means it has to be re-issued here, or the choice would not take effect until they signed
        // in again and the switch would look broken.
        var tenant = await _tenants.ByIdAsync(TenantId);
        var session = new TechSession(TenantId, TechnicianId, Me, User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "",
            Permissions.CanManageCompany(MyRole), MyRole, wanted) { Stamp = PortalIdentity.StampOf(User), SignIn = PortalIdentity.SignInOf(User) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            PortalIdentity.Build(session, tenant?.Slug ?? "", wanted));

        return Redirect(Safe(returnUrl));
    }

    /// <summary>
    /// Back where they came from, and only if that is somewhere on this site.
    ///
    /// <see cref="Url.IsLocalUrl"/> rather than "starts with a slash", for the reason
    /// PortalHandoffService already records: "//evil.example.com" starts with a slash and a browser
    /// reads it as another host entirely. A returnUrl is a redirect somebody else can put in a link.
    /// </summary>
    private string Safe(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/portal";
}
