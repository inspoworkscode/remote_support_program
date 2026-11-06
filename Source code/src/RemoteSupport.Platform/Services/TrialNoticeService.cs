using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Warns a tenant before its trial lapses, and tells it on the day it does.
///
/// WHY THIS EXISTS. `Tenant.IsServiceEnabled` is `Active || (Trial &amp;&amp; !expired)`, so the moment a
/// 14-day trial runs out, every one of that company's customer machines is refused at /ws/device
/// at the same instant. The refusal is at least visible now — the server logs it and the tray app
/// says so (section 19 of the handover) — but that only explains the outage after it has happened.
/// This is the half that stops it happening: the company is told while there is still time to act.
///
/// It is the FIRST BackgroundService in this codebase. Two consequences worth knowing:
///   * nothing else on the server runs on a timer, so if a second periodic job is ever needed,
///     this is the pattern to copy rather than a second mechanism;
///   * it must never be able to take the server down. Every pass is wrapped, and a tenant that
///     throws is logged and skipped rather than stopping the loop.
/// </summary>
public sealed class TrialNoticeService : BackgroundService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PlatformMailService _mail;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TrialNoticeService> _log;

    /// <summary>
    /// Days before expiry that earn a notice, plus 0 for the day it lapses. Clint's call on
    /// 2026-09-13. Ordered widest-first so a tenant that somehow skipped a milestone — the server
    /// was off for two days, say — still gets the most urgent one that still applies rather than
    /// nothing at all.
    /// </summary>
    private static readonly int[] Milestones = { 7, 1, 0 };

    /// <summary>Hourly. The decision is day-grained, so this is only about not sleeping through
    /// a whole day if the server happens to restart at an awkward time.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public TrialNoticeService(IDbContextFactory<PlatformDb> dbf, PlatformMailService mail, IConfiguration cfg, ILogger<TrialNoticeService> log)
    { _dbf = dbf; _mail = mail; _cfg = cfg; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the application finish starting before touching the database. Migrations run at
        // startup and a query racing them is a confusing way to fail.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await RunPassAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "trial notice pass failed; will try again in an hour"); }
            try { await Task.Delay(Interval, ct); } catch { return; }
        }
    }

    /// <summary>Exposed so it can be driven directly by a test or a one-off, rather than waiting
    /// an hour to find out whether it works.</summary>
    public async Task RunPassAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var trials = await db.Tenants
            .Where(t => t.Status == TenantStatus.Trial && t.TrialEndsUtc != null)
            .ToListAsync(ct);
        if (trials.Count == 0) return;

        var productName = _cfg["Platform:ProductName"] ?? "The Remote Support Program";
        var portalUrl = (_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');

        // No mailbox at all is a different thing from a send that failed, and conflating them is
        // how a log fills with one warning per tenant per hour for ever. Said once per pass, and
        // nothing is marked as sent — so configuring a mailbox later still delivers every notice
        // that is due. The portal banner has been warning them the whole time regardless.
        var settings = await _mail.GetAsync();
        if (!settings.MailConfigured)
        {
            var due = trials.Count(t => t.TrialDaysLeft is <= 7);
            if (due > 0)
                _log.LogWarning("{Count} tenant(s) are within a week of their trial ending, but no platform mailbox is configured " +
                                "so none of them can be emailed. Set one at /owner/mail. The portal banner still warns them.", due);
            return;
        }

        foreach (var t in trials)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var daysLeft = t.TrialDaysLeft;
                if (daysLeft == null) continue;

                // The most urgent milestone this tenant now qualifies for and has not been sent.
                var sent = Sent(t.TrialNoticesSent);
                int? due = null;
                foreach (var m in Milestones)
                    if (daysLeft <= m && !sent.Contains(m)) due = m;   // widest first, so the last match is the tightest
                if (due == null) continue;

                var admins = await db.Technicians
                    .Where(x => x.TenantId == t.Id && !x.Disabled && (x.Role & (TenantRole.Owner | TenantRole.Admin)) != 0)
                    .OrderBy(x => x.CreatedUtc)
                    .Select(x => x.Email)
                    .ToListAsync(ct);
                if (admins.Count == 0)
                {
                    // Nobody to write to. Say so once rather than retrying hourly for ever, and
                    // mark it sent — the portal banner is still doing its job.
                    _log.LogWarning("tenant {Tenant}'s trial ends in {Days} day(s) and it has no enabled admin to write to", t.CompanyName, daysLeft);
                }

                var (subject, body) = Compose(t, daysLeft.Value, productName, portalUrl);
                var result = admins.Count == 0 ? "no admin address" : await _mail.SendAsync(admins, subject, body);

                if (result == "sent" || admins.Count == 0)
                {
                    // Recorded only when it actually went, so a mail outage does not silently
                    // consume the warning. "no admin address" is recorded too: retrying that
                    // hourly changes nothing and fills the log.
                    t.TrialNoticesSent = string.Join(",", sent.Append(due.Value).Distinct().OrderByDescending(x => x));
                    t.LastTrialNoticeUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    _log.LogInformation("trial notice ({Milestone}-day) for {Tenant}: {Result}", due, t.CompanyName, result);
                }
                else
                {
                    // NOT marked as sent: it will be retried on the next pass. A trial warning
                    // that was swallowed by a mail failure is the exact outage this exists to stop.
                    _log.LogWarning("trial notice ({Milestone}-day) for {Tenant} could NOT be sent: {Result}. It will be retried.",
                        due, t.CompanyName, result);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "trial notice for tenant {Tenant}", t.Slug); }
        }
    }

    private static HashSet<int> Sent(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Select(x => int.TryParse(x, out var n) ? n : -1)
         .Where(n => n >= 0)
         .ToHashSet();

    /// <summary>
    /// Plain text, and it says what will actually happen rather than "your trial is ending". The
    /// consequence is specific and severe — every customer machine stops connecting — and a notice
    /// that does not say so will be filed as marketing.
    /// </summary>
    private static (string subject, string body) Compose(Tenant t, int daysLeft, string productName, string portalUrl)
    {
        // The Basic version (2026-10-25) is one person reaching their own computers — and, for a free account from before,
        // its "trial" is the 30 days' notice. Its own words; the full version's below.
        if (RemoteSupport.Shared.FreePlan.Is(t.Plan)) return ComposeBasic(t, daysLeft, productName, portalUrl);
        var ends = t.TrialEndsUtc!.Value;
        var b = new System.Text.StringBuilder();

        string subject;
        if (daysLeft <= 0)
        {
            subject = $"{t.CompanyName}: your trial has ended";
            b.AppendLine($"Your trial of {productName} ended on {ends:d MMMM yyyy}.");
            b.AppendLine();
            b.AppendLine("Your support software has stopped working. Every computer you have installed");
            b.AppendLine("it on is now being refused when it tries to connect, and your technicians");
            b.AppendLine("cannot start a session on any of them.");
            b.AppendLine();
            b.AppendLine("Nothing has been deleted. Your tickets, clients, machines and history are all");
            b.AppendLine("exactly as you left them, and everything starts working again the moment the");
            b.AppendLine("account is activated.");
        }
        else
        {
            subject = daysLeft == 1
                ? $"{t.CompanyName}: your trial ends tomorrow"
                : $"{t.CompanyName}: your trial ends in {daysLeft} days";
            b.AppendLine($"Your trial of {productName} ends on {ends:d MMMM yyyy}" +
                         (daysLeft == 1 ? ", which is tomorrow." : $", which is {daysLeft} days from now."));
            b.AppendLine();
            b.AppendLine("What happens when it does, so there is no surprise: the support software stops");
            b.AppendLine("working. Every computer you have installed it on will be refused when it tries");
            b.AppendLine("to connect, and your technicians will not be able to start a session on any of");
            b.AppendLine("them. It happens at once, on all of them.");
            b.AppendLine();
            b.AppendLine("Nothing is deleted, and everything resumes as soon as the account is activated.");
        }

        b.AppendLine();
        // Paying by card (2026-10-13, stage B): in South Africa the owner can do it themselves, before it ends.
        if (daysLeft > 0 && portalUrl.Length > 0 && !t.NotBilled && Pricing.For(t).Code == "ZA")
            b.AppendLine($"To carry on, the owner adds the company's card on Billing, or replies to this email: {portalUrl}/portal/billing");
        else
            b.AppendLine("To carry on, reply to this email and we will activate your account.");
        if (portalUrl.Length > 0)
        {
            b.AppendLine();
            b.AppendLine($"Your portal: {portalUrl}/portal");
        }
        b.AppendLine();
        b.AppendLine("--");
        b.AppendLine(productName);
        return (subject, b.ToString());
    }

    /// <summary>The Basic version's notice: its price, and that its computers stop connecting until the card is in.</summary>
    private static (string subject, string body) ComposeBasic(Tenant t, int daysLeft, string productName, string portalUrl)
    {
        var day = t.TrialEndsUtc!.Value.Local(CompanyClock.Find(t.TimeZone));
        var r = Pricing.For(t);
        var price = Pricing.Money(r, r.Basic);
        var b = new System.Text.StringBuilder();
        string subject;
        if (daysLeft <= 0)
        {
            subject = $"{t.CompanyName}: add your card to keep connecting";
            b.AppendLine($"Since {day:d MMMM yyyy} {RemoteSupport.Shared.FreePlan.TheName} of {productName} is {price} a month, and no card has been added.");
            b.AppendLine();
            b.AppendLine("Your computers are being refused when they try to connect, so you cannot reach them.");
            b.AppendLine("Nothing has been deleted: add your card and everything works again at once.");
        }
        else
        {
            subject = daysLeft == 1 ? $"{t.CompanyName}: add your card by tomorrow" : $"{t.CompanyName}: add your card within {daysLeft} days";
            b.AppendLine($"From {day:d MMMM yyyy}" + (daysLeft == 1 ? " (tomorrow)" : $" ({daysLeft} days from now)")
                + $" {RemoteSupport.Shared.FreePlan.TheName} of {productName} is {price} a month, by card. Nothing is charged before then.");
            b.AppendLine();
            b.AppendLine("If no card has been added by then, your computers stop connecting until it is. Nothing is deleted.");
        }
        b.AppendLine();
        b.AppendLine(portalUrl.Length > 0 && !t.NotBilled && r.Code == "ZA"
            ? $"Add your card on Billing, or reply to this email: {portalUrl}/portal/billing"
            : "To carry on, reply to this email.");
        b.AppendLine();
        b.AppendLine("--");
        b.AppendLine(productName);
        return (subject, b.ToString());
    }
}
