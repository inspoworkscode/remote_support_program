using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// Signing in to the dashboard (2026-10-13): a platform admin's email and password, then a code from their
/// authenticator — the code skipped for 7 days on a browser that gave one (<see cref="PlatformAdmins"/>).
///
/// <para>The admin key + authenticator (2026-10-01, <see cref="OwnerTwoFactor"/>) is still here, behind "Use the
/// admin key instead", and is the form shown first while no admin exists yet — it is how the first Owner is
/// added, and the way in when all else fails.</para>
/// </summary>
public sealed class LoginModel : PageModel
{
    private readonly OwnerTwoFactor _owner;
    private readonly PlatformAdmins _admins;
    public LoginModel(OwnerTwoFactor owner, PlatformAdmins admins) { _owner = owner; _admins = admins; }

    [BindProperty(SupportsGet = true)] public bool UseKey { get; set; }
    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty] public string Key { get; set; } = "";
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Pending { get; set; } = "";
    public string Secret { get; private set; } = "";
    public string Qr { get; private set; } = "";
    public string Error { get; private set; } = "";
    /// <summary>No admin yet: the admin key is the only way in, so it is the form shown.</summary>
    public bool NoAdmins { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (User.IsInRole("owner")) return Redirect("/owner");
        NoAdmins = !await _admins.AnyAsync();
        if (NoAdmins) UseKey = true;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await Task.Delay(Random.Shared.Next(200, 500));
        var from = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        NoAdmins = !await _admins.AnyAsync();
        if (NoAdmins || (Key.Length > 0 && Email.Length == 0)) UseKey = true;
        if (UseKey) return await KeyAsync(from);

        var step = Pending.Length == 0
            ? await _admins.PasswordAsync(Email, Password, from, Request.Cookies[PlatformAdmins.TrustCookie])
            : await _admins.CodeAsync(Pending, Code, from);
        if (step.Done == null) { Error = step.Error; Pending = step.Pending; return Page(); }
        if (step.Trust.Length > 0)
            Response.Cookies.Append(PlatformAdmins.TrustCookie, step.Trust, new CookieOptions
            {
                HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/owner",
                Expires = DateTimeOffset.UtcNow.Add(_admins.TrustFor), IsEssential = true
            });
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AdminIdentity.Build(step.Done));
        return Redirect("/owner");
    }

    private async Task<IActionResult> KeyAsync(string from)
    {
        var step = Pending.Length == 0 ? await _owner.KeyAsync(Key, from) : await _owner.CodeAsync(Pending, Code, from);
        if (!step.Done)
        {
            Error = step.Error; Pending = step.Pending; Secret = step.Secret; Qr = step.Qr;
            return Page();
        }
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, PlatformAdmins.KeyName), new(ClaimTypes.Role, "owner"),
            new(PortalIdentity.StampClaim, await _owner.StampAsync())
        };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie")));
        await _admins.LogAsync(PlatformAdmins.KeyName, "Signed in with the admin key");
        return Redirect("/owner");
    }
}
