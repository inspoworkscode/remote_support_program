using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// The contact form a company puts on its website (3.8.4, 2026-10-05): /contact/{slug}, a page of its
/// own to link to, and the same page in a frame placed by wwwroot/contactform-embed.js. Ours, light,
/// in the company's colour. Sending it opens a General ticket through <see cref="TicketIntake"/> — the
/// same rules as an email.
///
/// No antiforgery token, on purpose: it is a public form with nobody signed in to protect, and in a
/// frame on another website the token's cookie is a third-party cookie that Safari throws away —
/// every message would have been refused. What stands in front of it instead is a field a person never
/// sees, a form that must have been open three seconds, and a limit per internet address.
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class ContactFormModel : PageModel
{
    private readonly TenantService _tenants;
    private readonly TicketIntake _intake;
    private readonly ITimeLimitedDataProtector _stamp;
    public ContactFormModel(TenantService tenants, TicketIntake intake, IDataProtectionProvider dp)
    {
        _tenants = tenants; _intake = intake;
        _stamp = dp.CreateProtector("RemoteSupport.ContactFormOpened.v1").ToTimeLimitedDataProtector();
    }

    public Tenant? Tenant { get; private set; }
    public TicketIntakeSettings S { get; private set; } = new();
    public string Fill { get; private set; } = "#2E9BFF";
    /// <summary>When the form was drawn, sealed — so a robot that posts at once is known.</summary>
    public string Opened { get; private set; } = "";
    public bool Preview { get; private set; }
    public bool Embedded { get; private set; }
    public string Error { get; private set; } = "";
    public int Number { get; private set; }

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Phone { get; set; }
    [BindProperty] public string? Company { get; set; }
    [BindProperty] public string? Subject { get; set; }
    [BindProperty] public string? Message { get; set; }
    /// <summary>The field a person never sees: anything in it is a robot, which is thanked and ignored.</summary>
    [BindProperty] public string? Website { get; set; }
    [BindProperty(Name = "o")] public string? OpenedPosted { get; set; }
    // "from", never "page" — page is Razor Pages' own route value (§70).
    [BindProperty(SupportsGet = true, Name = "from")] public string? From { get; set; }

    /// <summary>Sent — by a person (Number is their ticket) or a robot (thanked the same, with no number).</summary>
    public bool Thanked { get; private set; }
    /// <summary>The reply with the number really left — only then does the page say "we have emailed you".</summary>
    public bool Replied { get; private set; }
    public string ThanksText => Number <= 0 ? "Thank you — we will be in touch soon." : (S.FormThanks.Length > 0 ? S.FormThanks : "Thank you — we have opened ticket #{number} and will be in touch soon.")
        .Replace("{number}", Number.ToString()).Replace("#1234", "#" + Number);

    private async Task<bool> LoadAsync(string slug)
    {
        var t = await _tenants.BySlugAsync(slug);
        Tenant = t != null && t.Status is not (TenantStatus.Pending or TenantStatus.Rejected or TenantStatus.Closed) ? t : null;
        if (Tenant == null) return false;
        S = await _intake.GetAsync(Tenant.Id);
        Fill = BrandPalette.FillForWhiteText(Tenant.AccentColor);
        Preview = Request.Query["preview"] == "1";
        Embedded = Request.Query["embed"] == "1" || (Request.HasFormContentType && Request.Form["embed"] == "1");
        Opened = _stamp.Protect(DateTime.UtcNow.Ticks.ToString(), TimeSpan.FromHours(12));
        return true;
    }

    public async Task<IActionResult> OnGetAsync(string slug)
    {
        await LoadAsync(slug);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string slug)
    {
        if (!await LoadAsync(slug)) return Page();
        // A robot: thanked, and nothing happens.
        if (!string.IsNullOrEmpty(Website) || !OpenedLongEnough()) { Thanked = true; return Page(); }
        // Drawn again with a problem to fix, it keeps the time it was FIRST opened: a person who
        // corrects their email address in two seconds is not a robot.
        Opened = OpenedPosted!;
        if (Preview) { Error = "This is the preview in your portal — nothing is sent from here."; return Page(); }
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (!TicketIntake.AllowForm(Tenant!.Id, ip)) { Error = "There have been several messages from here in the last hour. Please email or phone us instead."; return Page(); }
        var (number, error, replied) = await _intake.SubmitFormAsync(Tenant!, new TicketIntake.FormInput(Name, Email, Phone, Company, Subject, Message, From));
        if (error.Length > 0) { Error = error; return Page(); }
        Number = number; Thanked = true; Replied = replied;
        return Page();
    }

    private bool OpenedLongEnough()
    {
        try
        {
            var ticks = long.Parse(_stamp.Unprotect(OpenedPosted ?? ""));
            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) >= TimeSpan.FromSeconds(3);
        }
        catch { return false; }
    }
}
