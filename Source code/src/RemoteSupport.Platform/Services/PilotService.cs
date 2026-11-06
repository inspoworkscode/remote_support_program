using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The pilot companies (Clint, 2026-10-15: "they each get 5 user license for free and have to pay for every extra user",
/// and "free forever for the 5 free seats"). Every rule about a pilot is here.
///
/// <para><b>The link.</b> A platform admin (Manager or above) makes one link per company at <c>/owner/pilots</c>. It opens
/// <c>/pilot/{token}</c> — outside the holding page, and open while public signup is closed: the link IS the way in. It works
/// ONCE, for <see cref="LinkDays"/> days, and only its SHA-256 is kept, so the link is shown to the admin once and nowhere
/// else. The company fills in its own details there; nothing is typed for them.</para>
///
/// <para><b>What a pilot is.</b> <see cref="Tenant.Pilot"/>: the five seats cost nothing while it is a pilot
/// (<see cref="Pricing.BillFor"/>). Since 2026-10-22 five seats is the most any company has, so a pilot owes nothing at all
/// and no card is taken (<see cref="CompanyBilling.Refusal"/>). Active at once — no trial, no approval. The terms are the
/// region's with <c>Terms/pilot.txt</c> ahead of section 1, kept word for word like every signup's.</para>
///
/// <para><b>Ending a pilot</b> (2026-10-22): <see cref="GiveNoticeAsync"/> sets a day; on it <see cref="PilotSweep"/> either
/// suspends the company and closes it <see cref="SuspendedDays"/> days later, or moves it to the standard price with a
/// trial's 14 days to add a card.</para>
/// </summary>
public sealed class PilotService
{
    /// <summary>How long a pilot link works.</summary>
    public const int LinkDays = 30;

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PlatformAdmins _admins;
    private readonly AdminCompanies _companies;
    private readonly IConfiguration _cfg;

    public PilotService(IDbContextFactory<PlatformDb> dbf, PlatformAdmins admins, AdminCompanies companies, IConfiguration cfg)
    { _dbf = dbf; _admins = admins; _companies = companies; _cfg = cfg; }

    /// <summary>The fingerprint a link's token is kept as. The token itself is never stored.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("rsp-pilot-link:" + (token ?? "").Trim().ToUpperInvariant())));

    /// <summary>The address a token opens.</summary>
    public string LinkFor(string token) => $"{(_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/')}/pilot/{token}";

    /// <summary>What a link is now, in a word — for the admin's list.</summary>
    public static string State(PilotInvite i, DateTime utcNow) =>
        i.UsedUtc != null ? "Used" : i.CancelledUtc != null ? "Cancelled" : i.ExpiresUtc <= utcNow ? "Expired" : "Waiting";

    /// <summary>
    /// A new link for one company. A Manager or above. The token comes back ONCE, to be copied and sent; "" and the
    /// problem when it may not be made.
    /// </summary>
    public async Task<(string token, PilotInvite? invite, string error)> MakeAsync(PlatformAdmins.Actor by, string? company, string? email, string? note)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return ("", null, "Only a Manager or above can make a pilot link.");
        company = (company ?? "").Trim(); email = (email ?? "").Trim().ToLowerInvariant(); note = (note ?? "").Trim();
        if (company.Length < 2) return ("", null, "Type the company's name, so the link can be told apart from the others.");
        if (email.Length > 0 && !TicketIntake.LooksLikeEmail(email)) return ("", null, "That email address does not look right — or leave it blank.");
        var token = Passwords.RandomToken(18);
        var i = new PilotInvite
        {
            TokenHash = Hash(token), CompanyName = company, Email = email, Note = note, MadeBy = by.Name,
            MadeUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddDays(LinkDays)
        };
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            db.PilotInvites.Add(i);
            await db.SaveChangesAsync();
        }
        await _admins.LogAsync(by.Name, $"Pilot link made (works once, until {i.ExpiresUtc:yyyy-MM-dd})", company);
        return (token, i, "");
    }

    /// <summary>A link that has not been used stops working. A Manager or above.</summary>
    public async Task<string> CancelAsync(PlatformAdmins.Actor by, int id)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return "Only a Manager or above can cancel a pilot link.";
        await using var db = await _dbf.CreateDbContextAsync();
        var i = await db.PilotInvites.FirstOrDefaultAsync(x => x.Id == id);
        if (i == null) return "That link is not on file.";
        if (i.UsedUtc != null) return "That link has been used — the company is signed up. Change the company itself on All companies.";
        if (i.CancelledUtc != null) return "";
        i.CancelledUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await _admins.LogAsync(by.Name, "Pilot link cancelled", i.CompanyName);
        return "";
    }

    /// <summary>Every link, newest first, with the company each one signed up.</summary>
    public async Task<List<(PilotInvite invite, string company)>> ListAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var list = await db.PilotInvites.AsNoTracking().OrderByDescending(i => i.Id).Take(200).ToListAsync();
        var ids = list.Where(i => i.TenantId != null).Select(i => i.TenantId!.Value).ToList();
        var names = await db.Tenants.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.CompanyName);
        return list.Select(i => (i, i.TenantId is int tid && names.TryGetValue(tid, out var n) ? n : "")).ToList();
    }

    /// <summary>
    /// The link a token opens, if it may still be used — or null and why not, in words for the person holding it. Asked when
    /// the page is drawn; <see cref="TenantService.SignupAsync"/> spends it, once, in the same save as the company.
    /// </summary>
    public async Task<(PilotInvite? invite, string why)> FindAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, NotAValidLink);
        await using var db = await _dbf.CreateDbContextAsync();
        var hash = Hash(token);
        var i = await db.PilotInvites.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == hash);
        return (i, Why(i, DateTime.UtcNow));
    }

    /// <summary>Why a link may not be used now, or "" when it may.</summary>
    public static string Why(PilotInvite? i, DateTime utcNow) =>
        i == null ? NotAValidLink
        : i.UsedUtc != null ? Used
        : i.CancelledUtc != null || i.ExpiresUtc <= utcNow ? Dead
        : "";

    public const string NotAValidLink = "This is not a link we made. Check that the whole link was copied, or ask us for a new one.";
    private const string Used = "This link has already been used to sign a company up. Sign in instead, or ask us for a new link.";
    private const string Dead = "This link no longer works. Ask us for a new one.";

    /// <summary>Whether a signup's refusal was about the link itself — the page then shows that alone, with no form.</summary>
    public static bool IsLinkProblem(string error) => error is NotAValidLink or Used or Dead;

    // ---- ending a pilot (2026-10-22) -------------------------------------------------------------
    //
    // Clint, a week after the two pilots signed up: "The two pilot companies have had their licenses for a week now and
    // have done absolutely nothing ... We need an option to revoke pilot licenses." He chose both endings, and that the
    // closing one suspends first (it can be undone) and closes 30 days later. The pilot terms they accepted end the
    // agreement as section 11 says — 30 days' notice — so a notice date is chosen, 30 days ahead by default; an earlier one
    // is allowed (by agreement with the company), and the page says what the terms say. Nothing happens to the company
    // until the day; the notice can be withdrawn until then.

    /// <summary>The notice the pilot terms give, in days — the date the page offers first.</summary>
    public const int NoticeDays = 30;

    /// <summary>How long a closing pilot stays suspended before it is closed.</summary>
    public const int SuspendedDays = 30;

    /// <summary>The ending that suspends, then closes.</summary>
    public const string Close = "close";
    /// <summary>The ending that moves the company to the standard price, with a trial's 14 days to add a card.</summary>
    public const string Pay = "pay";

    /// <summary>Who the automatic steps are logged as.</summary>
    public static readonly PlatformAdmins.Actor PilotActor = new(null, "Pilot ending (automatic)", AdminLevel.Owner);

    /// <summary>The moment the pilot stops — the start of the chosen day in the company's own zone.</summary>
    public static DateTime EndsAt(Tenant t, DateOnly day) => CompanyClock.ToUtc(day.ToDateTime(TimeOnly.MinValue), CompanyClock.Find(t.TimeZone));

    /// <summary>A day as the company reads it: "22 November 2026".</summary>
    public static string Day(Tenant t, DateTime utc) => utc.Local(CompanyClock.Find(t.TimeZone)).ToString("d MMMM yyyy");

    /// <summary>What the company's Billing page says while notice is running.</summary>
    public static string EndingWords(Tenant t, string price) => t.PilotEndsUtc is not { } e ? "" : t.PilotAfter == Pay
        ? $"Your pilot ends on {Day(t, e)}. From then your five seats are {price} a month, and you have 14 days to add your card here."
        : $"Your pilot ends on {Day(t, e)}. On that day this account is suspended, and it is closed {SuspendedDays} days later. Ask us before then for a copy of your data.";

    /// <summary>
    /// Give a pilot company notice that its pilot ends on <paramref name="day"/> (company time), and what happens then. A
    /// Manager or above. A day of today or earlier ends it now. Its owners are emailed, with <paramref name="message"/>
    /// at the top if one is given. Error "" for done, with what happened in <c>said</c>.
    /// </summary>
    public async Task<(string error, string said)> GiveNoticeAsync(PlatformAdmins.Actor by, int tenantId, DateOnly? day, string? after, string? message)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return ("Only a Manager or above can end a pilot.", "");
        if (after is not (Close or Pay)) return ("Choose what happens when the pilot ends.", "");
        if (day == null) return ("Choose the day the pilot ends.", "");
        message = (message ?? "").Trim();
        if (message.Length > 4000) return ("That message is too long for one email.", "");
        Tenant t;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var row = await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId);
            if (row == null) return ("That company no longer exists.", "");
            if (!row.Pilot) return ($"{row.CompanyName} is not a pilot company.", "");
            row.PilotEndsUtc = EndsAt(row, day.Value);
            row.PilotAfter = after;
            await db.SaveChangesAsync();
            t = row;
        }
        var ends = t.PilotEndsUtc!.Value;
        await _admins.LogAsync(by.Name, $"Pilot notice: ends {Day(t, ends)}, then {(after == Pay ? "the standard price" : "suspended, closed 30 days later")}", t.CompanyName);
        if (ends <= DateTime.UtcNow)
        {
            var now = await EndAsync(by, t.Id);
            return now.error.Length > 0 ? now : ("", $"{t.CompanyName}'s pilot has ended. {now.said}");
        }
        var emailed = await _companies.NoticeAsync(by, t, NoticeMail(t, message));
        return ("", $"{t.CompanyName}'s pilot ends on {Day(t, ends)}. {emailed}");
    }

    /// <summary>Take a notice back before its day: the pilot carries on as before, and the owners are told. A Manager or above.</summary>
    public async Task<(string error, string said)> WithdrawNoticeAsync(PlatformAdmins.Actor by, int tenantId)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return ("Only a Manager or above can change a pilot.", "");
        Tenant t;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var row = await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId);
            if (row == null) return ("That company no longer exists.", "");
            if (!row.Pilot || row.PilotEndsUtc == null) return ($"{row.CompanyName} has no notice to withdraw.", "");
            row.PilotEndsUtc = null; row.PilotAfter = "";
            await db.SaveChangesAsync();
            t = row;
        }
        await _admins.LogAsync(by.Name, "Pilot notice withdrawn — the pilot carries on", t.CompanyName);
        var emailed = await _companies.NoticeAsync(by, t, ($"{t.CompanyName}: your pilot carries on",
            $"Hello,\n\nWe have withdrawn our notice: {t.CompanyName}'s pilot on The Remote Support Program carries on as before, "
            + "with your five seats free.\n\nThe Remote Support Program\n"));
        return ("", $"Notice withdrawn — {t.CompanyName} stays a pilot. {emailed}");
    }

    /// <summary>
    /// Every pilot whose day has come, ended; every closing pilot suspended for <see cref="SuspendedDays"/>, closed. Run by
    /// <see cref="PilotSweep"/>. Returns how many companies it changed.
    /// </summary>
    public async Task<int> SweepAsync()
    {
        var now = DateTime.UtcNow;
        List<int> ending, closing;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            ending = await db.Tenants.Where(t => t.Pilot && t.PilotEndsUtc != null && t.PilotEndsUtc <= now).Select(t => t.Id).ToListAsync();
            var cut = now.AddDays(-SuspendedDays);
            closing = await db.Tenants.Where(t => !t.Pilot && t.PilotAfter == Close && t.Status == TenantStatus.Suspended
                && t.PilotEndedUtc != null && t.PilotEndedUtc <= cut).Select(t => t.Id).ToListAsync();
        }
        int changed = 0;
        foreach (var id in ending) if ((await EndAsync(PilotActor, id)).error.Length == 0) changed++;
        foreach (var id in closing) if (await CloseAsync(id)) changed++;
        return changed;
    }

    /// <summary>The day has come: no longer a pilot, and either suspended (closing) or on the standard price with a trial's 14 days.</summary>
    private async Task<(string error, string said)> EndAsync(PlatformAdmins.Actor by, int tenantId)
    {
        Tenant t;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            // Once only: a pilot already ended (or never one) is left alone, whoever asks second.
            var row = await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId && x.Pilot);
            if (row == null) return ("That company is not a pilot any more.", "");
            row.Pilot = false;
            row.PilotEndedUtc = DateTime.UtcNow;
            if (row.PilotAfter == Pay)
            {
                row.TrialEndsUtc = DateTime.UtcNow.AddDays(CompanyBilling.TrialDays);
                row.TrialNoticesSent = "";
            }
            await db.SaveChangesAsync();
            t = row;
        }
        await _admins.LogAsync(by.Name, t.PilotAfter == Pay ? "Pilot ended — on the standard price, 14 days to add a card" : "Pilot ended — suspended", t.CompanyName);
        var done = t.PilotAfter == Pay
            ? await _companies.SetStatusAsync(by, t.Id, TenantStatus.Trial, EndedPayMail(t))
            : await _companies.SetStatusAsync(by, t.Id, TenantStatus.Suspended, EndedCloseMail(t));
        return (done.Error, done.Said);
    }

    /// <summary>A closing pilot's 30 days of suspension are over: closed, and the owners told when the data goes.</summary>
    private async Task<bool> CloseAsync(int tenantId)
    {
        var done = await _companies.SetStatusAsync(PilotActor, tenantId, TenantStatus.Closed);
        if (done.Error.Length > 0) return false;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t != null) await _companies.NoticeAsync(PilotActor, t, ($"{t.CompanyName}: your account is closed",
            $"Hello,\n\n{t.CompanyName}'s account on The Remote Support Program is now closed.\n\n"
            + "As section 11 of the terms says, we delete your company's data 60 days after the agreement ends. Reply to this email "
            + "before then if you would like a copy of it.\n\nThe Remote Support Program\n"));
        return true;
    }

    private static string Price(Tenant t) { var r = Pricing.For(t); return Pricing.Money(r, r.Package); }

    private static (string subject, string body) NoticeMail(Tenant t, string message)
    {
        var day = Day(t, t.PilotEndsUtc!.Value);
        var b = new StringBuilder("Hello,\n\n");
        if (message.Length > 0) b.Append(message).Append("\n\n");
        b.Append($"This is our notice that {t.CompanyName}'s pilot on The Remote Support Program ends on {day}.\n\n");
        if (t.PilotAfter == Pay)
        {
            b.Append($"From that day your company is on our standard price: {Price(t)} a month for five seats. You will have 14 days to add ");
            b.Append("your card on the Billing page of the portal; if no card is added by then, the service pauses until one is. ");
            b.Append("Everything else stays as it is.\n\n");
        }
        else
        {
            b.Append("On that day your account is suspended: your people can no longer sign in, and your customers' machines stop ");
            b.Append($"connecting. It is closed {SuspendedDays} days later, and, as section 11 of the terms says, your data is deleted ");
            b.Append("60 days after that. Ask us before then for a copy of your data.\n\n");
            b.Append($"If you would rather carry on as a paying company — {Price(t)} a month for five seats — reply to this email before {day}.\n\n");
        }
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: your pilot ends on {day}", b.ToString());
    }

    private (string subject, string body) EndedPayMail(Tenant t)
    {
        var url = (_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/');
        var by = t.TrialEndsUtc is { } e ? Day(t, e) : "";
        var b = new StringBuilder("Hello,\n\n");
        b.Append($"{t.CompanyName}'s pilot on The Remote Support Program has ended, as our notice said. Your company is now on our ");
        b.Append($"standard price: {Price(t)} a month for five seats.\n\n");
        b.Append($"Please add your card on the Billing page{(by.Length > 0 ? $" by {by}" : "")}. Until then nothing changes for you; after that ");
        b.Append("the service pauses until a card is added.\n\n");
        if (url.Length > 0) b.Append($"Billing: {url}/portal/billing\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: your pilot has ended — please add your card", b.ToString());
    }

    private static (string subject, string body) EndedCloseMail(Tenant t)
    {
        var b = new StringBuilder("Hello,\n\n");
        b.Append($"{t.CompanyName}'s pilot on The Remote Support Program has ended, as our notice said, and your account is now suspended.\n\n");
        b.Append("Your people cannot sign in, and your customers' machines cannot connect. Nothing has been deleted yet: the account is ");
        b.Append($"closed {SuspendedDays} days from today, and your data is deleted 60 days after that. Reply to this email before then for a ");
        b.Append("copy of your data, or if you would like to carry on as a paying company.\n\n");
        b.Append("The Remote Support Program\n");
        return ($"{t.CompanyName}: your pilot has ended", b.ToString());
    }

    /// <summary>A pilot company as the admin's list shows it — with whether it has used the service at all.</summary>
    public sealed record PilotCompany(Tenant Tenant, int People, int Machines, int Tickets, int Sessions, DateTime? LastSignInUtc);

    /// <summary>Every company that is a pilot now, or whose pilot ended by notice, with how much it has used the service.</summary>
    public async Task<List<PilotCompany>> CompaniesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var list = await db.Tenants.AsNoTracking().Where(t => t.Pilot || t.PilotEndedUtc != null).OrderBy(t => t.CompanyName).ToListAsync();
        var result = new List<PilotCompany>();
        foreach (var t in list)
        {
            var people = db.Technicians.Where(x => x.TenantId == t.Id && !x.Disabled);
            result.Add(new PilotCompany(t, await people.CountAsync(),
                await db.Devices.CountAsync(d => d.TenantId == t.Id),
                await db.Tickets.CountAsync(x => x.TenantId == t.Id),
                await db.Sessions.CountAsync(s => s.TenantId == t.Id),
                await people.MaxAsync(x => x.LastLoginUtc)));
        }
        return result;
    }
}

/// <summary>Ends pilots whose notice day has come, and closes the closing ones after their 30 days (2026-10-22). Every ten minutes.</summary>
public sealed class PilotSweep : BackgroundService
{
    private readonly PilotService _pilots;
    private readonly ILogger<PilotSweep> _log;
    private readonly TimeSpan _every;

    public PilotSweep(PilotService pilots, IConfiguration cfg, ILogger<PilotSweep> log)
    {
        _pilots = pilots; _log = log;
        _every = TimeSpan.FromSeconds(int.TryParse(cfg["Platform:PilotSweepSeconds"], out var s) && s > 0 ? s : 600);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, _every.TotalSeconds)), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var n = await _pilots.SweepAsync();
                if (n > 0) _log.LogInformation("Pilot sweep: {N} company(ies) changed", n);
            }
            catch (Exception ex) { _log.LogError(ex, "The pilot sweep failed"); }
            try { await Task.Delay(_every, stop); } catch (OperationCanceledException) { return; }
        }
    }
}
