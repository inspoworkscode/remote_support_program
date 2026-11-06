using RemoteSupport.Shared;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Hub;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// Support in the browser (3.8.2, 2026-09-30): the link an IT company gives its customers. They put
/// in their details, it rings the technicians as the support app does, and they share their screen
/// from the page — see <c>wwwroot/help.js</c> and <see cref="TenantHub.RunWebCallerAsync"/>.
///
/// It is a PUBLIC form that rings people, so it asks the company's switch and hours first
/// (<see cref="WebSupportHours"/>), refuses a busy address, and keeps a hidden field a person never
/// fills in. Anything that fails says what to do instead: the company's phone and email.
/// </summary>
public sealed class HelpModel : PageModel
{
    private readonly TenantService _tenants;
    private readonly HubRegistry _hubs;
    private readonly PayloadService _payloads;

    public HelpModel(TenantService tenants, HubRegistry hubs, PayloadService payloads)
    { _tenants = tenants; _hubs = hubs; _payloads = payloads; }

    /// <summary>At most this many requests from one address in an hour, per company.</summary>
    public const int PerAddressPerHour = 5;
    private static readonly ConcurrentDictionary<string, List<DateTime>> Recent = new();

    public Tenant? Tenant { get; private set; }
    public bool HasLogo => Tenant != null && !string.IsNullOrEmpty(Tenant.LogoFile);
    public bool AppAvailable => Tenant != null && Tenant.IsServiceEnabled && _payloads.ClientAvailable;
    /// <summary>"off" (not offered), "closed" (outside its hours), "form", or "call" (ringing).</summary>
    public string State { get; private set; } = "off";
    public List<string> Hours { get; private set; } = new();
    public string Error { get; private set; } = "";
    public string CallerId { get; private set; } = "";
    public string CallerToken { get; private set; } = "";

    // All nullable: with nullable references on, ASP.NET treats a non-nullable string as REQUIRED, and
    // the hidden field is always empty and Company is optional — so every form was refused, as "too
    // long", until tools/websupportcheck posted one. Only the lengths are the binder's to check.
    [BindProperty, StringLength(80)] public string? Name { get; set; }
    [BindProperty, StringLength(120)] public string? Company { get; set; }
    [BindProperty, StringLength(40)] public string? Phone { get; set; }
    [BindProperty, StringLength(160)] public string? Email { get; set; }
    [BindProperty, StringLength(2000)] public string? Issue { get; set; }
    /// <summary>Hidden from people by the page's styles; a form-filling robot fills it in.</summary>
    [BindProperty] public string? Website { get; set; }

    private async Task<bool> LoadAsync(string slug)
    {
        var t = await _tenants.BySlugAsync(slug ?? "");
        if (t == null || !t.IsServiceEnabled || !t.WebSupportEnabled || FreePlan.Is(t.Plan)) { State = "off"; Tenant = t is { IsServiceEnabled: true } ? t : null; return false; }
        Tenant = t;
        Hours = WebSupportHours.Describe(t);
        if (!WebSupportHours.IsOpen(t, DateTime.UtcNow)) { State = "closed"; return false; }
        State = "form";
        return true;
    }

    public async Task OnGetAsync(string slug) => await LoadAsync(slug);

    public async Task<IActionResult> OnPostAsync(string slug)
    {
        if (!await LoadAsync(slug)) return Page();
        var t = Tenant!;

        Name = (Name ?? "").Trim(); Company = (Company ?? "").Trim(); Phone = (Phone ?? "").Trim();
        Email = (Email ?? "").Trim(); Issue = (Issue ?? "").Trim();
        if (!ModelState.IsValid) { Error = "Something you typed is too long. Please shorten it."; return Page(); }
        if (Name.Length < 2) { Error = "Please tell us your name."; return Page(); }
        if (Phone.Length == 0 && Email.Length == 0) { Error = "Please give a phone number or an email address, so we can reach you if the call drops."; return Page(); }
        if (Email.Length > 0 && !TicketService.LooksLikeEmail(Email)) { Error = "That email address does not look right."; return Page(); }
        // The same floor the support app's own request uses (§30): it stops a keystroke, not a short true answer.
        if (Issue.Length < 3) { Error = "Please say briefly what is wrong."; return Page(); }

        // A robot: it is told it worked, and nothing rings.
        if (!string.IsNullOrEmpty(Website)) { State = "sent"; return Page(); }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (!Allow($"{t.Id}|{ip}"))
        { Error = "There have been several requests from here in the last hour. Please phone us instead."; return Page(); }

        var hub = _hubs.Get(t.Id);
        if (hub == null) { Error = "Support is not available at the moment. Please phone us."; return Page(); }
        var (id, token, err) = hub.RegisterWebCaller(Name, Company, Phone, Email, Issue, ip);
        if (err.Length > 0) { Error = err; return Page(); }
        CallerId = id; CallerToken = token;
        State = "call";
        return Page();
    }

    /// <summary>A sliding hour per company and address. In memory: a restart forgets, which is fine.</summary>
    public static bool Allow(string key, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var list = Recent.GetOrAdd(key, _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(x => at - x > TimeSpan.FromHours(1));
            if (list.Count >= PerAddressPerHour) return false;
            list.Add(at);
            return true;
        }
    }
}
