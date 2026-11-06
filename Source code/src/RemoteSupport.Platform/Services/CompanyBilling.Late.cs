using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Stage D of <c>docs/NEXT-SESSION-PAYFAST.md</c> (2026-10-14): a payment that did not come, and the end of a subscription.
/// Clint: <i>"go with 14 days"</i> — South Africa's terms say service may be suspended when a payment is "more than 14 days
/// late".
///
/// <para><b>PayFast sends nothing when a renewal fails</b> (it retries, emails the buyer, and in the end locks the
/// subscription), so lateness is the ABSENCE of a payment: a charge date passed by a day or more with no payment since.
/// Before calling it late, PayFast's API is asked — a <c>run_date</c> that has moved on means it was paid and only its notice
/// went missing; CANCELLED means it ended there. Then a reminder a day late, another at a week, a last on day 13, each with
/// PayFast's change-the-card link (it works for a company that can no longer sign in); on day 15 the company is suspended
/// through <see cref="AdminCompanies"/>. A payment restores ONLY a suspension made for it
/// (<see cref="PayfastSubscription.SuspendedUtc"/>) — never one an admin made.</para>
///
/// <para><b>Cancelled</b> — on Billing, at PayFast, or found by the sweep: service runs to
/// <see cref="PayfastSubscription.EndsOn"/>, the end of the month paid for (no part-month refunds), then the company is
/// Closed and told. A new card before then is first charged the day after it, and nothing is closed.</para>
/// </summary>
public sealed partial class CompanyBilling
{
    /// <summary>The terms: suspended only when a payment is MORE than this many days late.</summary>
    public const int GraceDays = 14;

    /// <summary>Which reminder a payment this many days late has earned: 1 (a day), 2 (a week), 3 (day 13), 4 = suspend.</summary>
    public static int LateStage(int daysLate) =>
        daysLate > GraceDays ? 4 : daysLate >= GraceDays - 1 ? 3 : daysLate >= 7 ? 2 : daysLate >= 1 ? 1 : 0;

    /// <summary>The charge date a subscription is waiting for: the next PayFast told us of, else its first.</summary>
    public static string Due(PayfastSubscription s) => s.NextRunDate.Length > 0 ? s.NextRunDate : s.BillingDate;

    private static DateTime? Day(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Ymd(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Words(DateTime d) => d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    private static DateTime Today(Tenant t) => DateTime.UtcNow.Local(CompanyClock.Find(t.TimeZone)).Date;

    /// <summary>
    /// A company that cancelled and comes back before its paid month ends is first charged the day after it ends — it has
    /// paid up to then. Null when there is no such month running.
    /// </summary>
    private async Task<string?> ResumeDateAsync(Tenant t)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var ends = await db.PayfastSubscriptions.AsNoTracking()
            .Where(s => s.TenantId == t.Id && s.Status == PayfastStatus.Cancelled && s.EndsOn != "" && s.EndedUtc == null && s.Purpose != "test")
            .Select(s => s.EndsOn).ToListAsync();
        var today = Today(t);
        var last = ends.Select(Day).Where(d => d >= today).Max();
        return last is { } l ? Ymd(l.AddDays(1)) : null;
    }

    // ---- late --------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One active subscription looked at: paid, late (and how late), moved on at PayFast, or cancelled there. What it found,
    /// in words, for the log; "" when there was nothing to do.
    /// </summary>
    public async Task<string> CheckLateAsync(int subscriptionId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var sub = await db.PayfastSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId);
        if (sub is not { Status: PayfastStatus.Active, Token: { Length: > 0 } token } || sub.Purpose == "test") return "";
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sub.TenantId);
        if (t == null || t.NotBilled || FreePlan.IsFree(t.Plan)) return "";   // the Basic version pays by card too since 2026-10-25; the Free version never
        if (Day(Due(sub)) is not { } due) return "";
        var zone = CompanyClock.Find(t.TimeZone);
        int daysLate = (Today(t) - due).Days;
        var dueStartUtc = CompanyClock.ToUtc(due, zone);
        bool paid = await db.PayfastPayments.AnyAsync(p => p.SubscriptionId == sub.Id && p.Status == "COMPLETE" && p.Applied && p.AmountGrossCents > 0 && p.ReceivedUtc >= dueStartUtc);
        if (daysLate < 1 || paid) return "";

        // Before calling it late: what does PayFast say?
        var (state, run, problem) = await _payfast.LookAsync(token);
        if (state == null) return $"{t.CompanyName}: PayFast could not be asked ({problem}); next sweep";
        if (state == "CANCELLED")
        {
            sub.Status = PayfastStatus.Cancelled; sub.CancelledUtc ??= DateTime.UtcNow;
            await db.SaveChangesAsync();
            await MarkEndingAsync(sub.Id, "PayFast");
            return $"{t.CompanyName}: cancelled at PayFast";
        }
        if (Day(run) is { } next && next > due)
        {
            sub.NextRunDate = Ymd(next); sub.LateDue = ""; sub.LateNotices = 0;
            await db.SaveChangesAsync();
            await _admins.LogAsync(PayfastActor.Name, $"PayFast had moved on to {Ymd(next)} — its notice for {Ymd(due)} never came", t.CompanyName);
            return $"{t.CompanyName}: PayFast moved on to {Ymd(next)}";
        }

        if (sub.LateDue != Ymd(due)) { sub.LateDue = Ymd(due); sub.LateNotices = 0; }
        var stage = LateStage(daysLate);
        var suspendOn = due.AddDays(GraceDays + 1);
        var cardUrl = _payfast.CardUpdateUrl(sub.Sandbox, token, _payfast.PublicUrl + "/portal/billing");
        if (stage == 4)
        {
            if (sub.SuspendedUtc != null || t.Status is not (TenantStatus.Active or TenantStatus.Trial)) { await db.SaveChangesAsync(); return ""; }
            sub.SuspendedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var done = await _companies.SetStatusAsync(PayfastActor, t.Id, TenantStatus.Suspended, SuspendedForPayment(t, sub, due, cardUrl));
            if (done.Error.Length > 0)
            {
                sub.SuspendedUtc = null; await db.SaveChangesAsync();
                _log.LogError("Could not suspend {Company} for its missing payment: {Why}", t.CompanyName, done.Error);
                return $"{t.CompanyName}: NOT suspended — {done.Error}";
            }
            return $"{t.CompanyName}: suspended, {daysLate} days late";
        }
        if (stage > sub.LateNotices)
        {
            sub.LateNotices = stage;
            await db.SaveChangesAsync();
            await TellOwnersAsync(t, LateReminder(t, sub, due, stage, suspendOn, cardUrl));
            await _admins.LogAsync(PayfastActor.Name, $"Payment due {Ymd(due)} not received — reminder {stage} of 3 sent", t.CompanyName);
            return $"{t.CompanyName}: reminder {stage}";
        }
        await db.SaveChangesAsync();
        return "";
    }

    /// <summary>
    /// Money came for this subscription: any lateness is over — and if WE suspended the company for it, it is back, and its
    /// owners are told. A suspension an admin made stays.
    /// </summary>
    private async Task PaidAsync(int subscriptionId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var sub = await db.PayfastSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId);
        if (sub == null || (sub.LateDue.Length == 0 && sub.SuspendedUtc == null)) return;
        bool weSuspended = sub.SuspendedUtc != null;
        sub.LateDue = ""; sub.LateNotices = 0; sub.SuspendedUtc = null;
        await db.SaveChangesAsync();
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sub.TenantId);
        if (t == null) return;
        if (weSuspended && t.Status == TenantStatus.Suspended)
        {
            var done = await _companies.SetStatusAsync(PayfastActor, t.Id, TenantStatus.Active, BackAfterPayment(t));
            if (done.Error.Length > 0) _log.LogError("Could not restore {Company} after its payment: {Why}", t.CompanyName, done.Error);
        }
        else await _admins.LogAsync(PayfastActor.Name, "The late payment came", t.CompanyName);
    }

    // ---- the end -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The owner cancels on Billing: PayFast is told first (nothing more is charged), then ours is cancelled with the service
    /// running to the end of the month paid for. "" and the last day, or the problem.
    /// </summary>
    public async Task<(string error, string endsOn)> CancelAsync(int tenantId, string by)
    {
        var current = await _payfast.CurrentForAsync(tenantId);
        if (current is not { Status: PayfastStatus.Active, Token: { Length: > 0 } token }) return ("There is no card subscription to cancel.", "");
        var r = await _payfast.CancelAsync(token);
        if (!r.Ok) return ("PayFast did not cancel it: " + r.Error + " Nothing has changed; please try again, or contact us.", "");
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var sub = await db.PayfastSubscriptions.FirstAsync(s => s.Id == current.Id);
            sub.Status = PayfastStatus.Cancelled; sub.CancelledUtc ??= DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var ends = await MarkEndingAsync(current.Id, by);
        return ("", ends);
    }

    /// <summary>
    /// A Basic company moving down to the Free version (2026-10-25, §163) — THE way it happens. The rules first
    /// (<see cref="TenantService.DowngradeProblemAsync"/>: the owner, the Free terms ticked, one computer at most); then PayFast is
    /// told to stop (nothing more is charged), and ours is marked ended TODAY — not "service until the end of the month", because
    /// the service carries on, free; the sweep that closes cancelled companies never sees it. Then the company becomes Free
    /// (<see cref="TenantService.DowngradeToFreeAsync"/>). If PayFast will not cancel, nothing changes. "" for done, or why not.
    /// </summary>
    public async Task<string> DowngradeToFreeAsync(int tenantId, TenantRole asker, string askerName, string askerEmail,
        bool accepted, string termsSha, string fromAddress)
    {
        var why = await _tenants.DowngradeProblemAsync(tenantId, asker, accepted, termsSha);
        if (why.Length > 0) return why;
        var current = await _payfast.CurrentForAsync(tenantId);
        if (current is { Status: PayfastStatus.Active, Token: { Length: > 0 } token })
        {
            var r = await _payfast.CancelAsync(token);
            if (!r.Ok) return "PayFast did not cancel the card: " + r.Error + " Nothing has changed; please try again, or contact us.";
            await using var db = await _dbf.CreateDbContextAsync();
            var sub = await db.PayfastSubscriptions.FirstAsync(s => s.Id == current.Id);
            sub.Status = PayfastStatus.Cancelled; sub.CancelledUtc ??= DateTime.UtcNow;
            var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == tenantId);
            sub.EndsOn = Ymd(Today(t)); sub.EndedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        why = await _tenants.DowngradeToFreeAsync(tenantId, askerName, askerEmail, fromAddress);
        if (why.Length > 0) return why;
        var company = (await _tenants.ByIdAsync(tenantId))?.CompanyName ?? "";
        await _admins.LogAsync(askerName, "Moved down from the Basic version to the Free version" + (current != null ? " — card cancelled at PayFast" : ""), company);
        return "";
    }

    /// <summary>
    /// A cancelled subscription is given its last day — the charge it was waiting for (the end of the month paid for), or
    /// today when that has passed unpaid — and the owners are told. Once only. A company already paying by another card is
    /// not ending: nothing is said and nothing will close. The last day, or "".
    /// </summary>
    public async Task<string> MarkEndingAsync(int subscriptionId, string by)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var sub = await db.PayfastSubscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId);
        if (sub == null || sub.Status != PayfastStatus.Cancelled || sub.Purpose == "test") return "";
        if (sub.EndsOn.Length > 0) return sub.EndsOn;
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sub.TenantId);
        if (t == null) return "";
        var today = Today(t);
        // Paid up to the day BEFORE the charge it was waiting for: a month paid on the 14th runs to the 13th, a trial ends the
        // day before its first charge, and a charge that never came means the paid time ended the day before it — already
        // past, so the sweep closes it at once (and no "carries on until" email is sent for a day gone by).
        var ends = (Day(Due(sub)) ?? today).AddDays(-1);
        sub.EndsOn = Ymd(ends);
        bool replaced = await db.PayfastSubscriptions.AnyAsync(s => s.TenantId == sub.TenantId && s.Id != sub.Id && s.Status == PayfastStatus.Active && s.Sandbox == sub.Sandbox && s.Purpose != "test");
        if (replaced) sub.EndedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        if (replaced || t.NotBilled || t.Status is not (TenantStatus.Active or TenantStatus.Trial) || ends < today) return sub.EndsOn;
        await _admins.LogAsync(by == "PayFast" ? PayfastActor.Name : by, $"Card subscription cancelled — service until {sub.EndsOn}", t.CompanyName);
        await TellOwnersAsync(t, CancelledMail(t, ends, _payfast.PublicUrl));
        return sub.EndsOn;
    }

    /// <summary>Cancelled subscriptions whose last day has passed: the company is Closed and told. And any cancellation not yet given its last day.</summary>
    public async Task<int> CloseEndedAsync()
    {
        List<PayfastSubscription> subs;
        await using (var db = await _dbf.CreateDbContextAsync())
            subs = await db.PayfastSubscriptions.AsNoTracking()
                .Where(s => s.Status == PayfastStatus.Cancelled && s.EndedUtc == null && s.Purpose != "test").ToListAsync();
        int closed = 0;
        foreach (var s in subs)
        {
            if (s.EndsOn.Length == 0) { await MarkEndingAsync(s.Id, "PayFast"); continue; }
            await using var db = await _dbf.CreateDbContextAsync();
            var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.TenantId);
            var sub = await db.PayfastSubscriptions.FirstAsync(x => x.Id == s.Id);
            if (t == null || Day(s.EndsOn) is not { } ends || Today(t) <= ends) continue;
            sub.EndedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            bool replaced = await db.PayfastSubscriptions.AnyAsync(x => x.TenantId == s.TenantId && x.Id != s.Id && x.Status == PayfastStatus.Active && x.Sandbox == s.Sandbox && x.Purpose != "test");
            if (replaced || t.NotBilled || t.Status is not (TenantStatus.Active or TenantStatus.Trial)) continue;
            var done = await _companies.SetStatusAsync(PayfastActor, t.Id, TenantStatus.Closed, EndedMail(t, ends));
            if (done.Error.Length > 0) _log.LogError("Could not close {Company} at the end of its subscription: {Why}", t.CompanyName, done.Error);
            else closed++;
        }
        return closed;
    }

    /// <summary>The sweep's stage D pass: every active card subscription checked for lateness; ended ones closed.</summary>
    public async Task LatePassAsync()
    {
        var acct = await _payfast.AccountAsync();
        if (acct == null) return;
        List<int> ids;
        await using (var db = await _dbf.CreateDbContextAsync())
            ids = await db.PayfastSubscriptions.Where(s => s.Status == PayfastStatus.Active && s.Sandbox == acct.Sandbox && s.Purpose != "test")
                .Select(s => s.Id).ToListAsync();
        foreach (var id in ids)
            try { var said = await CheckLateAsync(id); if (said.Length > 0) _log.LogInformation("PayFast sweep: {Said}", said); }
            catch (Exception ex) { _log.LogError(ex, "The PayFast sweep failed on subscription {Id}", id); }
        try { await CloseEndedAsync(); }
        catch (Exception ex) { _log.LogError(ex, "The PayFast sweep could not close ended subscriptions"); }
    }

    /// <summary>What the control dashboard says about a company's card: "", "Card", "Card — late since …", "Cancelled — ends …".</summary>
    public static string PaymentWords(PayfastSubscription? s) => s switch
    {
        null => "",
        { Status: PayfastStatus.Active, LateDue.Length: > 0 } => $"Card, unpaid since {s.LateDue}" + (s.SuspendedUtc != null ? " (suspended)" : ""),
        { Status: PayfastStatus.Active } => "Card",
        { Status: PayfastStatus.Cancelled, EndedUtc: null, EndsOn.Length: > 0 } => $"Cancelled, ends {s.EndsOn}",
        { Status: PayfastStatus.Cancelled } => "Cancelled",
        _ => "Card not added",
    };

    // ---- the emails (plain ASCII: §135) ------------------------------------------------------------------------------------

    private async Task TellOwnersAsync(Tenant t, (string subject, string body) mail)
    {
        var owners = await _companies.OwnersAsync(t.Id);
        if (owners.Count == 0) { _log.LogWarning("{Company} has nobody to tell: {Subject}", t.CompanyName, mail.subject); return; }
        var r = await _mail.SendAsync(owners.Select(o => o.email), mail.subject, mail.body, "The Remote Support Program", null, copyOwner: true);
        if (r != "sent") _log.LogWarning("{Subject} to {Company} did not go: {Why}", mail.subject, t.CompanyName, r);
    }

    public static (string subject, string body) LateReminder(Tenant t, PayfastSubscription sub, DateTime due, int stage, DateTime suspendOn, string cardUrl)
    {
        var amount = Pricing.Money(Pricing.For(t), sub.AmountCents / 100m);
        var b = new System.Text.StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"The {amount} due on {Words(due)} for {t.CompanyName}'s subscription to The Remote Support Program has not reached us.\n\n");
        b.Append("PayFast tries the card again by itself. If the card has expired or changed, change it here:\n");
        b.Append(cardUrl + "\n\n");
        b.Append($"If it is still unpaid on {Words(suspendOn)}, the service is suspended: every computer you look after stops connecting, ");
        b.Append("and your technicians cannot sign in. It starts again by itself as soon as the payment comes through. Nothing is deleted.\n\n");
        b.Append("If you think this is a mistake, reply to this email.\n\n");
        b.Append("The Remote Support Program\n");
        var subject = stage switch
        {
            1 => $"{t.CompanyName}: your payment did not go through",
            2 => $"{t.CompanyName}: your payment is a week late",
            _ => $"{t.CompanyName}: your service will be suspended on {Words(suspendOn)}",
        };
        return (subject, b.ToString());
    }

    public static (string subject, string body) SuspendedForPayment(Tenant t, PayfastSubscription sub, DateTime due, string cardUrl)
    {
        var amount = Pricing.Money(Pricing.For(t), sub.AmountCents / 100m);
        var b = new System.Text.StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"{t.CompanyName}'s service has been suspended: the {amount} due on {Words(due)} has not been paid, and it is now more than {GraceDays} days late.\n\n");
        b.Append("Every computer you look after is refused when it tries to connect, and your technicians cannot sign in. Nothing has been deleted.\n\n");
        b.Append("It starts again by itself as soon as the payment comes through. To change the card PayFast charges:\n");
        b.Append(cardUrl + "\n\n");
        b.Append("If you think this is a mistake, reply to this email.\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: your service is suspended - payment not received", b.ToString());
    }

    public static (string subject, string body) BackAfterPayment(Tenant t)
    {
        var b = new System.Text.StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"Thank you - your payment has come through, and {t.CompanyName}'s service is active again.\n\n");
        b.Append("Your people can sign in, and your customers' machines reconnect by themselves within a minute.\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: payment received - your service is back", b.ToString());
    }

    public static (string subject, string body) CancelledMail(Tenant t, DateTime ends, string publicUrl)
    {
        var b = new System.Text.StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"{t.CompanyName}'s subscription to The Remote Support Program has been cancelled. Nothing more will be charged.\n\n");
        b.Append($"Your service carries on until the end of {Words(ends)}, the last day you have paid for. After that every computer you ");
        b.Append("look after stops connecting and your people cannot sign in.\n\n");
        if (publicUrl.Length > 0) b.Append($"Changed your mind? Add your card again on Billing before then, and nothing stops: {publicUrl}/portal/billing\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: subscription cancelled", b.ToString());
    }

    public static (string subject, string body) EndedMail(Tenant t, DateTime ends)
    {
        var b = new System.Text.StringBuilder();
        b.Append("Hello,\n\n");
        b.Append($"{t.CompanyName}'s subscription to The Remote Support Program ended on {Words(ends)}, and the service has stopped.\n\n");
        b.Append("Nothing has been deleted yet. To start again, reply to this email.\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: your service has ended", b.ToString());
    }
}
