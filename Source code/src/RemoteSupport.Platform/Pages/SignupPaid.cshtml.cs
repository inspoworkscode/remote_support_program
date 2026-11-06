using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages;

/// <summary>
/// Where PayFast sends somebody back after the card (2026-10-13, stage B). PayFast's notice — not this visit — is what
/// switches the company on, and it can arrive a moment before or after; so the page asks
/// <c>/api/payfast/checkout/{ref}</c> every few seconds and says "ready" when it is. Back from PayFast with
/// <c>cancelled=1</c>, or still waiting a long while, it offers the card again: a NEW checkout for the same company, as
/// long as that company is still waiting for one. The reference is 80 random bits; knowing it shows only that state.
/// </summary>
public sealed class SignupPaidModel : PageModel
{
    private readonly CompanyBilling _billing;
    private readonly PayfastService _payfast;
    public SignupPaidModel(CompanyBilling billing, PayfastService payfast) { _billing = billing; _payfast = payfast; }

    [BindProperty(SupportsGet = true)] public string Ref { get; set; } = "";
    [BindProperty(SupportsGet = true)] public int Cancelled { get; set; }

    public string State { get; private set; } = "unknown";
    public Tenant? Company { get; private set; }
    public PayfastSubscription? Sub { get; private set; }
    public PayfastService.Checkout? Checkout { get; private set; }
    public string Error { get; private set; } = "";

    public async Task OnGetAsync() => (State, Company, Sub) = await _billing.StateAsync(Ref);

    /// <summary>The card again, for a company still waiting for one.</summary>
    public async Task OnPostAsync()
    {
        (State, Company, Sub) = await _billing.StateAsync(Ref);
        if (Company == null || Sub == null) return;
        if (State != "waiting") return;
        var back = _payfast.PublicUrl + "/signup/paid?ref={ref}";
        var (co, error) = await _billing.StartAsync(Company.Id, Sub.Purpose, "the return page", back, back + "&cancelled=1");
        Checkout = co; Error = error;
    }
}
