using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// A company paying by card (2026-10-13, roadmap 4.8, <c>docs/NEXT-SESSION-PAYFAST.md</c> stage B). Clint's answers: the
/// card is taken at signup and nothing is charged for 14 days; a full-version company is switched on BY ITSELF once
/// PayFast confirms the card — nobody approves it. Since 2026-10-25 the Basic version (the old free version, R50) goes the same way.
///
/// <para><b>Who may pay by card</b> (<see cref="Refusal"/>): a full-version or Basic company, billed, not a pilot, in South Africa (PayFast is
/// rand only), on a server with a PayFast account. Everybody else goes the old way — approved by hand, invoiced.</para>
///
/// <para><b>What it costs</b> is <see cref="Pricing.BillFor"/> for the people in seats now — the package for a new
/// signup — and <b>the first charge</b> (<see cref="FirstChargeDate"/>) is 14 days from signing up, or the end of the
/// trial a company is already on, or tomorrow; always a date in the company's own time zone.</para>
///
/// <para><b>When the card is confirmed</b> (PayFast's first notice, R0.00), the company is made Active through
/// <see cref="AdminCompanies"/> — the one door every change to a company goes through — logged as "PayFast", and its
/// owners are emailed that it is ready. A suspended company is left for stage D.</para>
/// </summary>
public sealed partial class CompanyBilling
{
    /// <summary>The days a new company has before its first charge — the trial, with the card already in.</summary>
    public const int TrialDays = 14;

    /// <summary>What every change made by a payment is logged as.</summary>
    public static readonly PlatformAdmins.Actor PayfastActor = new(null, "PayFast", AdminLevel.Owner);

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PayfastService _payfast;
    private readonly AdminCompanies _companies;
    private readonly PlatformMailService _mail;
    private readonly ILogger<CompanyBilling> _log;

    private readonly PlatformAdmins _admins;
    private readonly TenantService _tenants;

    public CompanyBilling(IDbContextFactory<PlatformDb> dbf, PayfastService payfast, AdminCompanies companies, PlatformMailService mail,
        PlatformAdmins admins, TenantService tenants, ILogger<CompanyBilling> log)
    {
        _dbf = dbf; _payfast = payfast; _companies = companies; _mail = mail; _admins = admins; _log = log; _tenants = tenants;
        payfast.Listen(OnPaymentAsync);
        payfast.Listen(ReceiptAsync);
        // Somebody added, removed or re-boxed: the month's price may have moved (stage C). Not awaited — the person's change
        // is done; PayFast is told after, and the sweep tries again if it cannot be.
        tenants.PeopleChanged += id => _ = Task.Run(async () =>
        {
            try { await SyncAmountAsync(id); } catch (Exception ex) { _log.LogError(ex, "Could not bring PayFast's amount up to date for company {Id}", id); }
        });
    }

    // ---- stage C: keeping PayFast's amount true ----------------------------------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> Turns = new();

    /// <summary>What this company owes a month now, in cents (<see cref="Pricing.BillFor"/> for the people in seats).</summary>
    public static long OwedCents(Tenant t, IEnumerable<Technician> people) =>
        (long)Math.Round(Pricing.BillFor(t, Math.Max(1, Pricing.PeopleInSeats(people))).Monthly * 100m);

    /// <summary>
    /// Tells PayFast the price this company owes now, if it is not what PayFast has — from the NEXT charge, as the terms say
    /// ("a change takes effect from your next bill"); the run date is never touched. One company at a time, so two changes
    /// together cannot send two amounts out of order. Accepted: the subscription's amount moves and the log says so. Not:
    /// <see cref="PayfastSubscription.AmountProblem"/> says why, Billing shows it, and the sweep tries again. "" when done or
    /// nothing to do, else the problem.
    /// </summary>
    public async Task<string> SyncAmountAsync(int tenantId)
    {
        var turn = Turns.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync();
        try
        {
            var current = await _payfast.CurrentForAsync(tenantId);
            if (current is not { Status: PayfastStatus.Active, Token: { Length: > 0 } token }) return "";
            await using var db = await _dbf.CreateDbContextAsync();
            var t = await db.Tenants.AsNoTracking().Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
            var sub = await db.PayfastSubscriptions.FirstOrDefaultAsync(s => s.Id == current.Id);
            // The Basic version is billed too since 2026-10-25 (R50), so it is not skipped here any more.
            // The Free version (§163) owes nothing; a Basic company moving down to it has its card cancelled first (DowngradeAsync).
            if (t == null || sub == null || t.NotBilled || FreePlan.IsFree(t.Plan)) return "";
            var owed = OwedCents(t, t.Technicians);
            if (owed == sub.AmountCents)
            {
                if (sub.AmountProblem.Length > 0) { sub.AmountProblem = ""; await db.SaveChangesAsync(); }
                return "";
            }
            var r = await _payfast.UpdateAmountAsync(token, owed);
            if (!r.Ok)
            {
                sub.AmountProblem = r.Error.Length > 0 ? r.Error : "PayFast did not accept the new amount.";
                await db.SaveChangesAsync();
                _log.LogWarning("PayFast did not take {Company}'s new amount {Amount}: {Why}", t.CompanyName, PayfastWire.Rand(owed), sub.AmountProblem);
                return sub.AmountProblem;
            }
            var region = Pricing.For(t);
            var was = sub.AmountCents;
            sub.AmountCents = owed; sub.AmountChangedUtc = DateTime.UtcNow; sub.AmountProblem = "";
            await db.SaveChangesAsync();
            await _admins.LogAsync(PayfastActor.Name, $"Monthly amount {Pricing.Money(region, was / 100m)} → {Pricing.Money(region, owed / 100m)} from the next charge", t.CompanyName);
            return "";
        }
        finally { turn.Release(); }
    }

    /// <summary>Every active card subscription on this server's account brought up to date — the sweep's pass.</summary>
    public async Task<int> SyncAllAsync()
    {
        var acct = await _payfast.AccountAsync();
        if (acct == null) return 0;
        List<int> ids;
        await using (var db = await _dbf.CreateDbContextAsync())
            ids = await db.PayfastSubscriptions.Where(s => s.Status == PayfastStatus.Active && s.Sandbox == acct.Sandbox && s.Purpose != "test")
                .Select(s => s.TenantId).Distinct().ToListAsync();
        int problems = 0;
        foreach (var id in ids)
            try { if ((await SyncAmountAsync(id)).Length > 0) problems++; }
            catch (Exception ex) { problems++; _log.LogError(ex, "The PayFast sweep failed on company {Id}", id); }
        return problems;
    }

    /// <summary>
    /// A receipt from us for every payment that took money (stage C) — PayFast sends its own too. The company's owners,
    /// what was paid and when, PayFast's reference, the next charge, and no VAT (the terms: we are not registered).
    /// </summary>
    private async Task ReceiptAsync(PayfastSubscription sub, PayfastPayment pay, bool first)
    {
        if (pay.Status != "COMPLETE" || pay.AmountGrossCents <= 0 || sub.Purpose == "test") return;
        Tenant? t;
        await using (var db = await _dbf.CreateDbContextAsync())
            t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sub.TenantId);
        if (t == null) return;
        var owners = await _companies.OwnersAsync(t.Id);
        if (owners.Count == 0) return;
        var mail = Receipt(t, sub, pay);
        var r = await _mail.SendAsync(owners.Select(o => o.email), mail.subject, mail.body, "The Remote Support Program", null, copyOwner: true);
        if (r != "sent") _log.LogWarning("The receipt to {Company} did not go: {Why}", t.CompanyName, r);
    }

    public static (string subject, string body) Receipt(Tenant t, PayfastSubscription sub, PayfastPayment pay)
    {
        var region = Pricing.For(t);
        var amount = Pricing.Money(region, pay.AmountGrossCents / 100m);
        var b = new System.Text.StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"Thank you. We have received {amount} for {t.CompanyName}'s subscription to The Remote Support Program, ");
        b.Append($"paid by card through PayFast on {CompanyClock.Local(pay.ReceivedUtc, CompanyClock.Find(t.TimeZone)):d MMMM yyyy}.\n\n");
        b.Append($"PayFast's reference: {pay.PfPaymentId}\n");
        if (pay.BillingDate.Length > 0) b.Append($"The next charge: {pay.BillingDate}\n");
        b.Append("\nNo VAT is charged: we are not registered for VAT.\n\n");
        b.Append("Your payments are listed on Billing in the portal.\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: payment received, {amount}", b.ToString());
    }

    /// <summary>What the company has paid by card, newest first — for Billing.</summary>
    public async Task<List<PayfastPayment>> PaymentsAsync(int tenantId, int take = 24)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayfastPayments.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.Status == "COMPLETE" && p.Applied && p.AmountGrossCents > 0)
            .OrderByDescending(p => p.Id).Take(take).ToListAsync();
    }

    /// <summary>The purposes that switch a company on when their card is confirmed.</summary>
    public static bool SwitchesOn(string purpose) => purpose is "signup" or "card" or "upgrade";

    /// <summary>Why this company cannot pay by card here, or null when it can.</summary>
    public static string? Refusal(Tenant t, PayfastService.Account? acct)
    {
        if (t.NotBilled) return "This company is not billed.";
        // The Free version is back (2026-10-25, §163): nothing to pay, so no card. Upgrading to Basic or the full version brings one.
        if (FreePlan.IsFree(t.Plan)) return "The Free version has nothing to pay.";
        // The Basic version pays by card like the full version since 2026-10-25 (R50 a month).
        // A pilot (2026-10-15) owes nothing, and PayFast cannot hold a subscription of R0. Its card comes when its pilot ends
        // (PilotService.GiveNoticeAsync, "pay").
        if (t.Pilot) return "Your five pilot seats are free — no card is needed. Any seats past five that we allow are invoiced by us.";
        if (Pricing.For(t).Code != "ZA") return "Card payment is in rand, for South Africa; we will invoice you.";
        if (acct == null) return "Card payment is not set up on this server yet; we will invoice you.";
        return null;
    }

    /// <summary>
    /// The first charge, as PayFast's "yyyy-MM-dd": a new signup's 14 days from today; a company on a trial that has not
    /// ended, its last day; anybody else, tomorrow. In the company's own time zone (<see cref="CompanyClock"/>).
    /// </summary>
    public static string FirstChargeDate(Tenant t, DateTime utcNow)
    {
        var zone = CompanyClock.Find(t.TimeZone);
        var today = utcNow.Local(zone).Date;
        var day = t.Status == TenantStatus.Pending ? today.AddDays(TrialDays)
            : t.Status == TenantStatus.Trial && t.TrialEndsUtc is { } end && end > utcNow ? end.Local(zone).Date
            : today.AddDays(1);
        if (day <= today) day = today.AddDays(1);
        return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The checkout that puts this company's card on a monthly subscription: R0.00 now, the month's price from
    /// <see cref="FirstChargeDate"/>. <paramref name="returnUrl"/> and <paramref name="cancelUrl"/> may carry "{ref}".
    /// </summary>
    public async Task<(PayfastService.Checkout? checkout, string error)> StartAsync(int tenantId, string purpose, string by, string returnUrl, string cancelUrl)
    {
        if (!SwitchesOn(purpose)) return (null, "Not a reason to take a card.");
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.AsNoTracking().Include(x => x.Technicians).FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return (null, "No such company.");
        if (Refusal(t, await _payfast.AccountAsync()) is { } why) return (null, why);
        if (await _payfast.CurrentForAsync(tenantId) is { Status: PayfastStatus.Active }) return (null, "This company is already paying by card.");
        if (t.Status is TenantStatus.Suspended or TenantStatus.Closed or TenantStatus.Rejected) return (null, $"This company is {t.Status.ToString().ToLowerInvariant()}; contact us.");

        var bill = Pricing.BillFor(t, Math.Max(1, Pricing.PeopleInSeats(t.Technicians)));
        var owner = t.Technicians.Where(p => !p.Disabled && (p.Role & TenantRoles.Owners) != 0).OrderBy(p => p.Id).FirstOrDefault();
        var name = (owner?.DisplayName ?? t.ContactName).Trim();
        var space = name.IndexOf(' ');
        return await _payfast.BeginAsync(new PayfastService.CheckoutRequest(
            t.Id, purpose, by, $"The Remote Support Program — {t.CompanyName}",
            InitialCents: 0, AmountCents: (long)Math.Round(bill.Monthly * 100m), BillingDate: await ResumeDateAsync(t) ?? FirstChargeDate(t, DateTime.UtcNow),
            Frequency: 3, Cycles: 0, ReturnUrl: returnUrl, CancelUrl: cancelUrl,
            FirstName: space > 0 ? name[..space] : name, LastName: space > 0 ? name[(space + 1)..] : "",
            Email: owner?.Email ?? t.SupportEmail));
    }

    /// <summary>What the return page shows while PayFast's notice is on its way: "waiting", "ready" or "stopped".</summary>
    public async Task<(string state, Tenant? tenant, PayfastSubscription? sub)> StateAsync(string? reference)
    {
        var sub = await _payfast.ByReferenceAsync(reference);
        if (sub == null || !SwitchesOn(sub.Purpose)) return ("unknown", null, null);
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sub.TenantId);
        if (t == null) return ("unknown", null, sub);
        var state = sub.Status == PayfastStatus.Active && t.IsServiceEnabled ? "ready"
            : sub.Status == PayfastStatus.Cancelled || t.Status is TenantStatus.Suspended or TenantStatus.Closed or TenantStatus.Rejected ? "stopped"
            : "waiting";
        return (state, t, sub);
    }

    private async Task OnPaymentAsync(PayfastSubscription sub, PayfastPayment pay, bool first)
    {
        if (pay.Status == "CANCELLED") { await MarkEndingAsync(sub.Id, "PayFast"); return; }
        if (pay.Status == "COMPLETE" && pay.AmountGrossCents > 0) await PaidAsync(sub.Id);
        if (!first || pay.Status != "COMPLETE" || !SwitchesOn(sub.Purpose)) return;
        Tenant? t;
        await using (var db = await _dbf.CreateDbContextAsync())
            t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sub.TenantId);
        if (t == null) return;
        if (t.Status is TenantStatus.Suspended or TenantStatus.Closed or TenantStatus.Rejected)
        {
            _log.LogWarning("A card was confirmed for {Company}, which is {Status} — left as it is", t.CompanyName, t.Status);
            return;
        }
        var was = t.Status;
        if (was != TenantStatus.Active)
        {
            var done = await _companies.SetStatusAsync(PayfastActor, t.Id, TenantStatus.Active);
            if (done.Error.Length > 0) { _log.LogError("Could not switch {Company} on: {Why}", t.CompanyName, done.Error); return; }
        }
        var owners = await _companies.OwnersAsync(t.Id);
        if (owners.Count == 0) return;
        var mail = Ready(t, sub, was, _payfast.PublicUrl);
        var r = await _mail.SendAsync(owners.Select(o => o.email), mail.subject, mail.body, "The Remote Support Program", null, copyOwner: true);
        if (r != "sent") _log.LogWarning("The ready email to {Company} did not go: {Why}", t.CompanyName, r);
    }

    /// <summary>The email that says the card is in and the account is ready — and when the first charge is.</summary>
    public static (string subject, string body) Ready(Tenant t, PayfastSubscription sub, TenantStatus was, string publicUrl)
    {
        var first = DateTime.ParseExact(sub.BillingDate, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        var amount = Pricing.Money(Pricing.For(t), sub.AmountCents / 100m);
        var b = new System.Text.StringBuilder();
        b.Append("Hello,\n\n");
        b.Append(was == TenantStatus.Pending
            ? $"Your card is in, and {t.CompanyName}'s account is ready to use.\n\n"
            : $"Your card is in, and {t.CompanyName}'s subscription is active.\n\n");
        if (was == TenantStatus.Pending) b.Append($"Sign in: {publicUrl}/login\n\n");
        b.Append($"Nothing is charged until {first}. From then it is {amount} a month, by card through PayFast. ");
        b.Append(FreePlan.IsBasic(t.Plan) ? "You can cancel any time from Billing in the portal.\n\n"
            : "Adding people past the package changes it from the next charge, and you can cancel any time from Billing in the portal.\n\n");
        b.Append("The Remote Support Program\n");
        return (was == TenantStatus.Pending ? $"{t.CompanyName}: your account is ready" : $"{t.CompanyName}: your card is in", b.ToString());
    }
}

/// <summary>
/// The PayFast sweep (2026-10-13, stage C): every <c>Platform:PayfastSweepSeconds</c> (an hour) it brings every card
/// subscription's amount up to date — what a refused or unreachable update, or a change made on the command line (another
/// process), left behind; then (stage D) the payments that did not come, and the subscriptions that have ended.
/// </summary>
public sealed class PayfastSweep : BackgroundService
{
    private readonly CompanyBilling _billing;
    private readonly ILogger<PayfastSweep> _log;
    private readonly TimeSpan _every;

    public PayfastSweep(CompanyBilling billing, IConfiguration cfg, ILogger<PayfastSweep> log)
    {
        _billing = billing; _log = log;
        _every = TimeSpan.FromSeconds(int.TryParse(cfg["Platform:PayfastSweepSeconds"], out var s) && s > 0 ? s : 3600);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // A first pass soon after starting: a deploy is often when an update was owed.
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, _every.TotalSeconds)), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var problems = await _billing.SyncAllAsync();
                if (problems > 0) _log.LogWarning("PayFast sweep: {N} subscription(s) still could not be brought up to date", problems);
                // Stage D: payments that did not come, and subscriptions that have ended.
                await _billing.LatePassAsync();
            }
            catch (Exception ex) { _log.LogError(ex, "The PayFast sweep failed"); }
            try { await Task.Delay(_every, stop); } catch (OperationCanceledException) { return; }
        }
    }
}
