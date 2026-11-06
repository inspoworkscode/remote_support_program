using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

public sealed class BrandingModel : PortalPage
{
    public BrandingModel(TenantService t) : base(t) { }

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? Page() : Redirect("/login");

    /// <summary>Only "light" or "dark" ever reach the database, whatever the form posts.</summary>
    private static string CleanTheme(string? v) =>
        (v ?? "").Trim().Equals("dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";

    public async Task<IActionResult> OnPostAsync(string companyName, string productName, string? welcomeText, string? supportEmail, string? supportPhone,
        string primaryColor, string accentColor, IFormFile? logo, bool requireConsent, bool unattendedRequireConsent, int consentTimeoutSeconds,
        string? technicianTheme, string? clientTheme, string? timeZone = null)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var err = await Tenants.UpdateBrandingAsync(TenantId, t =>
        {
            t.CompanyName = companyName.Trim(); t.ProductName = productName.Trim();
            t.WelcomeText = (welcomeText ?? "").Trim(); t.SupportEmail = (supportEmail ?? "").Trim(); t.SupportPhone = (supportPhone ?? "").Trim();
            t.PrimaryColor = primaryColor; t.AccentColor = accentColor;
            t.TechnicianTheme = CleanTheme(technicianTheme); t.ClientTheme = CleanTheme(clientTheme);
            t.RequireConsent = requireConsent; t.UnattendedRequireConsent = unattendedRequireConsent;
            t.ConsentTimeoutSeconds = Math.Clamp(consentTimeoutSeconds, 10, 600);
            // Only a zone we offer; a stale page that sends none keeps what is there.
            if (CompanyClock.Offered(timeZone)) t.TimeZone = timeZone!;
        });
        if (err.Length == 0 && logo is { Length: > 0 })
        {
            if (logo.Length > 2 * 1024 * 1024) err = "Logo must be under 2 MB.";
            else { await using var s = logo.OpenReadStream(); err = await Tenants.SaveLogoAsync(TenantId, s, logo.FileName); }
        }
        await LoadAsync();
        if (err.Length > 0) Error = err; else Message = "Branding saved.";
        return Page();
    }
}
