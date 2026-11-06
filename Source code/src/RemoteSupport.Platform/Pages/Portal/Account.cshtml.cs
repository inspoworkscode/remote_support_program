using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// My account (2026-10-01): change your own password — which nobody could before; only a
/// supervisor could, by typing one for you. It asks for the current password AND a code, so a
/// browser left open on a desk cannot be used to lock its owner out. Every other console and
/// browser is signed out; this one stays in (the cookie is re-issued with the new stamp).
/// </summary>
public sealed class AccountModel : PortalPage
{
    private readonly AuthService _auth;
    public AccountModel(TenantService t, AuthService auth) : base(t) => _auth = auth;

    public string Email => User.FindFirst(ClaimTypes.Email)?.Value ?? "";

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : Redirect("/login");

    public async Task<IActionResult> OnPostAsync(string? current, string? password, string? again, string? code)
    {
        if (!await LoadAsync()) return Redirect("/login");
        await Task.Delay(Random.Shared.Next(100, 300));
        var (error, stamp) = await _auth.ChangePasswordAsync(TechnicianId, current, password, again, code,
            HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?");
        if (error.Length > 0) { Error = error; return Page(); }

        var session = new TechSession(TenantId, TechnicianId, Me, Email, Permissions.CanManageCompany(MyRole), MyRole,
            PortalIdentity.AppearanceOf(User)) { Stamp = stamp, SignIn = PortalIdentity.SignInOf(User) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            PortalIdentity.Build(session, Tenant.Slug, PortalIdentity.AppearanceOf(User)));
        Message = "Password changed. Every other console and browser you were signed in on has been signed out — sign in there with the new one.";
        return Page();
    }
}
