using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// PayFast as this server sees it (2026-10-13, roadmap 4.8 stage A): which account (sandbox or live), whether PayFast's
/// API accepts it, every subscription and payment, and the notices it refused. Every admin sees it; testing the API and
/// a <b>sandbox</b> test checkout are a Full admin's or an Owner's. The account itself is set on the server's command
/// line (deploy/Set-PayfastKeys.ps1) — the key and passphrase never pass through a web page.
/// </summary>
public sealed class PayfastModel : PageModel
{
    private readonly PayfastService _payfast;
    private readonly PlatformAdmins _admins;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public PayfastModel(PayfastService payfast, PlatformAdmins admins, IDbContextFactory<PlatformDb> dbf) { _payfast = payfast; _admins = admins; _dbf = dbf; }

    public PlatformSettings Settings { get; private set; } = new();
    public string NotifyUrl => _payfast.NotifyUrl;
    public List<PayfastSubscription> Subscriptions { get; private set; } = new();
    public List<PayfastPayment> Payments { get; private set; } = new();
    public IReadOnlyList<(DateTime when, string from, string why)> Refusals => _payfast.RecentRefusals;
    public Dictionary<int, string> Companies { get; private set; } = new();
    public bool MayRun { get; private set; }
    public string Message { get; private set; } = "";
    public bool IsError { get; private set; }
    /// <summary>A checkout to hand to PayFast: the page posts it as soon as it loads.</summary>
    public PayfastService.Checkout? Checkout { get; private set; }

    /// <summary>The test checkout's defaults: nothing now, R5.00 tomorrow and again a month later — a real charge to watch the next day.</summary>
    public string Tomorrow => DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<IActionResult> Render()
    {
        Settings = await _payfast.SettingsAsync();
        Subscriptions = await _payfast.RecentSubscriptionsAsync();
        Payments = await _payfast.RecentPaymentsAsync();
        await using var db = await _dbf.CreateDbContextAsync();
        Companies = await db.Tenants.AsNoTracking().OrderBy(t => t.CompanyName).ToDictionaryAsync(t => t.Id, t => t.CompanyName);
        MayRun = PlatformAdmins.MayRun(AdminIdentity.LevelOf(User));
        return Page();
    }

    public async Task<IActionResult> OnGetAsync(string? back)
    {
        if (back == "done") Message = "PayFast sent you back. Its notice arrives on its own — refresh in a moment to see it below.";
        else if (back == "cancelled") { Message = "The checkout was cancelled at PayFast. Nothing was charged; the subscription stays awaiting its card."; IsError = true; }
        return await Render();
    }

    private bool Refused()
    {
        if (PlatformAdmins.MayRun(AdminIdentity.LevelOf(User))) return false;
        Message = "Only a Full admin or an Owner can do that here."; IsError = true;
        return true;
    }

    public async Task<IActionResult> OnPostPingAsync()
    {
        if (Refused()) return await Render();
        var r = await _payfast.PingAsync();
        IsError = !r.Ok;
        Message = r.Ok ? $"PayFast's API accepts this server ({r.Body.Trim().Trim('"')})." : r.Error;
        await _admins.LogAsync(AdminIdentity.ActorOf(User).Name, "Tested PayFast's API: " + (r.Ok ? "accepted" : "refused"));
        return await Render();
    }

    /// <summary>Asks PayFast's API what one subscription is, and puts ours right — for a notice that never came or was refused.</summary>
    public async Task<IActionResult> OnPostCheckAsync(int id)
    {
        if (Refused()) return await Render();
        var (ok, said) = await _payfast.CheckAsync(id);
        IsError = !ok; Message = said;
        await _admins.LogAsync(AdminIdentity.ActorOf(User).Name, "Checked a subscription with PayFast: " + said);
        return await Render();
    }

    /// <summary>
    /// A real checkout at PayFast's SANDBOX for a company, to prove the whole round on this server: card, notice,
    /// renewal. Refused on a live account — a test must never take real money.
    /// </summary>
    public async Task<IActionResult> OnPostTestAsync(int tenantId, string? initial, string? amount, string? billingDate, int frequency, int cycles)
    {
        if (Refused()) return await Render();
        var s = await _payfast.SettingsAsync();
        if (!s.PayfastSandbox) { Message = "This server is on the LIVE account. A test checkout is for the sandbox only."; IsError = true; return await Render(); }
        var who = AdminIdentity.ActorOf(User).Name;
        var (co, error) = await _payfast.BeginAsync(new PayfastService.CheckoutRequest(
            tenantId, "test", who, "The Remote Support Program (sandbox test)",
            PayfastWire.Cents(initial) ?? -1, PayfastWire.Cents(amount) ?? 0, (billingDate ?? "").Trim(), frequency, cycles,
            _payfast.PublicUrl + "/owner/payfast?back=done", _payfast.PublicUrl + "/owner/payfast?back=cancelled"));
        if (co == null) { Message = error; IsError = true; return await Render(); }
        Checkout = co;
        var page = await Render();
        await _admins.LogAsync(who, $"Started a PayFast sandbox test checkout {co.Subscription.Reference}", Companies.GetValueOrDefault(tenantId, ""));
        return page;
    }
}
