using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => Redirect("/");

    /// <summary>
    /// Signs out, and goes home — or back to the technicians' download page when that is where
    /// "not you?" was pressed, so the next person can sign in there. Only a /tech/ page on this
    /// site: anything else would make this an open redirect.
    /// </summary>
    public async Task<IActionResult> OnPostAsync([FromServices] AuthService auth, string? back = null)
    {
        // Their one browser sign-in is given up, so signing in on another browser afterwards pushes
        // nobody off and asks no extra code (one at a time, 2026-10-05).
        if (User.Identity?.IsAuthenticated == true && !User.IsInRole("owner"))
            await auth.BrowserSignedOutAsync(PortalIdentity.TechnicianIdOf(User), PortalIdentity.SignInOf(User));
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return back != null && back.StartsWith("/tech/", StringComparison.Ordinal) && Url.IsLocalUrl(back)
            ? Redirect(back) : Redirect("/");
    }
}
