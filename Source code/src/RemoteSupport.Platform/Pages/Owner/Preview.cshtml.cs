using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// Gives THIS browser a look at the real site while the public still sees the holding page (2026-10-13,
/// <see cref="SitePreview"/>): home, features, pricing, and signup open to it. Any admin; logged.
/// </summary>
public sealed class PreviewModel : PageModel
{
    private readonly SitePreview _preview;
    private readonly PlatformAdmins _admins;
    public PreviewModel(SitePreview preview, PlatformAdmins admins) { _preview = preview; _admins = admins; }

    public bool On { get; private set; }
    public string Message { get; private set; } = "";

    public void OnGet() => On = _preview.Holds(HttpContext);

    public async Task<IActionResult> OnPostAsync(bool on)
    {
        if (on)
            Response.Cookies.Append(SitePreview.Cookie, _preview.Issue(), new CookieOptions
            {
                HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true,
                Expires = DateTimeOffset.UtcNow.Add(SitePreview.Life)
            });
        else Response.Cookies.Delete(SitePreview.Cookie, new CookieOptions { Path = "/" });
        await _admins.LogAsync(AdminIdentity.ActorOf(User).Name, on ? "Turned the site preview on in a browser" : "Turned the site preview off in a browser");
        On = on;
        Message = on ? "This browser now sees the real site, and may sign a company up. Everybody else still sees the holding page."
                     : "This browser sees the holding page again.";
        return Page();
    }
}
