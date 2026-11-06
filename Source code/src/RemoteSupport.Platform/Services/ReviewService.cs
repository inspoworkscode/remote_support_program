using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Reviews from the companies that use it (2026-10-14). Clint: <i>"an option in the tenant portal to create a review, which
/// automatically uploads to the live website. It asks them after a week to do a review, which they can delay for a month, then
/// reminds them again with an option to opt out. The review would ask for their name and link to their business with a review
/// box"</i>; then <i>"after approval"</i>, <i>"a star rating as well as words"</i>, and <i>"they must put their company name as
/// well"</i>. Every rule is here.
///
/// <list type="bullet">
/// <item><b>Who is asked:</b> the owners of a full-version company that is in service, a week after it was approved
/// (<c>Platform:ReviewAskDays</c>, 7). <b>Later</b> puts it off a month (<c>Platform:ReviewLaterDays</c>, 30); from the second
/// ask on, <b>Don't ask again</b> is offered. Writing one ends the asking.</item>
/// <item><b>The review:</b> 1–5 stars, the words, the name to show and the COMPANY NAME (both required), a website (https only,
/// optional), and the tick that allows publishing them (POPIA) — refused without it. One per company; writing again replaces it
/// and it waits again.</item>
/// <item><b>Published only when a platform admin (Manager or above) approves it</b>; declined or taken down, it is not shown.
/// Admins of Full level and Owners are emailed when one arrives.</item>
/// </list>
/// </summary>
public sealed class ReviewService
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PlatformAdmins _admins;
    private readonly PlatformMailService _mail;
    private readonly ILogger<ReviewService> _log;
    private readonly TimeSpan _askAfter, _later;

    public ReviewService(IDbContextFactory<PlatformDb> dbf, PlatformAdmins admins, PlatformMailService mail, IConfiguration cfg, ILogger<ReviewService> log)
    {
        _dbf = dbf; _admins = admins; _mail = mail; _log = log;
        _askAfter = TimeSpan.FromDays(double.TryParse(cfg["Platform:ReviewAskDays"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) && a >= 0 ? a : 7);
        _later = TimeSpan.FromDays(double.TryParse(cfg["Platform:ReviewLaterDays"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l) && l >= 0 ? l : 30);
    }

    public const int MinWords = 20, MaxWords = 1000;

    /// <summary>What the portal shows this person: 0 nothing; 1 the first ask (Write, Later); 2 a later ask (and Don't ask again).</summary>
    public async Task<int> AskAsync(int tenantId, TenantRole role)
    {
        if (!Permissions.CanAcceptPrice(role)) return 0;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null || t.ReviewOptedOut || FreePlan.Is(t.Plan) || !t.IsServiceEnabled) return 0;
        var from = t.ReviewAskAfterUtc ?? (t.ApprovedUtc ?? t.CreatedUtc) + _askAfter;
        if (DateTime.UtcNow < from) return 0;
        if (await db.CompanyReviews.AnyAsync(r => r.TenantId == tenantId)) return 0;
        return t.ReviewLaters == 0 ? 1 : 2;
    }

    /// <summary>"Later": asked again in a month.</summary>
    public async Task LaterAsync(int tenantId, TenantRole role)
    {
        if (!Permissions.CanAcceptPrice(role)) return;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null) return;
        t.ReviewAskAfterUtc = DateTime.UtcNow + _later;
        t.ReviewLaters++;
        await db.SaveChangesAsync();
    }

    /// <summary>"Don't ask again" — offered from the second ask; refused before it, as the page never offers it then.</summary>
    public async Task NeverAsync(int tenantId, TenantRole role)
    {
        if (!Permissions.CanAcceptPrice(role)) return;
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null || t.ReviewLaters == 0) return;
        t.ReviewOptedOut = true;
        await db.SaveChangesAsync();
    }

    public async Task<CompanyReview?> ForAsync(int tenantId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanyReviews.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId);
    }

    public sealed record Draft(int Stars, string? Words, string? ShownName, string? BusinessName, string? BusinessUrl, bool Consent);

    /// <summary>The owner writes (or rewrites) the company's review. "" when it is in, waiting for approval; else why not.</summary>
    public async Task<string> WriteAsync(int tenantId, TenantRole role, string by, string byEmail, Draft d)
    {
        if (!Permissions.CanAcceptPrice(role)) return "Only the owner can write the company's review.";
        var words = (d.Words ?? "").Trim();
        var name = (d.ShownName ?? "").Trim();
        var business = (d.BusinessName ?? "").Trim();
        var url = (d.BusinessUrl ?? "").Trim();
        if (d.Stars is < 1 or > 5) return "Choose from one to five stars.";
        if (words.Length < MinWords) return $"Please write a little more — at least {MinWords} characters.";
        if (words.Length > MaxWords) return $"Please keep it to {MaxWords} characters.";
        if (name.Length < 2 || name.Length > 80) return "Please give the name to show with the review.";
        if (business.Length < 2 || business.Length > 100) return "Please give your company's name.";
        if (url.Length > 0)
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.Contains('.') || url.Length > 200)
                return "That website does not look right — it must be an https:// address, or leave it blank.";
            url = u.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        if (!d.Consent) return "Please tick the box that allows us to publish your review, name and company on our website.";
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tenantId);
        if (t == null || FreePlan.Is(t.Plan)) return "Reviews are for the full version.";
        var r = await db.CompanyReviews.FirstOrDefaultAsync(x => x.TenantId == tenantId);
        if (r == null) db.CompanyReviews.Add(r = new CompanyReview { TenantId = tenantId });
        r.Stars = d.Stars; r.Words = words; r.ShownName = name; r.BusinessName = business; r.BusinessUrl = url; r.ConsentToPublish = true;
        r.WrittenBy = by; r.WrittenByEmail = byEmail; r.WrittenUtc = DateTime.UtcNow;
        r.Status = ReviewStatus.Waiting; r.DecidedBy = ""; r.DecidedUtc = null;
        await db.SaveChangesAsync();
        await _admins.LogAsync(by, $"Wrote a review ({d.Stars} stars) — waiting for approval", t.CompanyName);
        await TellAdminsAsync(t, r);
        return "";
    }

    private async Task TellAdminsAsync(Tenant t, CompanyReview r)
    {
        try
        {
            var to = (await _admins.AllAsync()).Where(a => a.Level >= AdminLevel.Full && a.Email.Contains('@')).Select(a => a.Email).ToList();
            if (to.Count == 0) return;
            var body = $"{r.ShownName} of {r.BusinessName} ({t.CompanyName}) has written a review: {r.Stars} of 5 stars.\n\n\"{r.Words}\"\n\n"
                     + "It is not on the website until it is approved: /owner/reviews\n\nThe Remote Support Program\n";
            await _mail.SendAsync(to, $"A review from {t.CompanyName} is waiting for approval", body);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not tell the admins of a new review"); }
    }

    // ---- the admins' side --------------------------------------------------------------------------------------------------------

    public sealed record Row(CompanyReview Review, string Company);

    public async Task<List<Row>> AllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var list = await db.CompanyReviews.AsNoTracking().OrderBy(r => r.Status).ThenByDescending(r => r.WrittenUtc).ToListAsync();
        var names = await db.Tenants.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.CompanyName);
        return list.Select(r => new Row(r, names.GetValueOrDefault(r.TenantId, "#" + r.TenantId))).ToList();
    }

    /// <summary>Approve (publish), decline, or take down. A Manager or above; logged. "" when done.</summary>
    public async Task<string> DecideAsync(PlatformAdmins.Actor by, int id, ReviewStatus status)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return AdminCompanies.ViewerRefusal;
        await using var db = await _dbf.CreateDbContextAsync();
        var r = await db.CompanyReviews.FirstOrDefaultAsync(x => x.Id == id);
        if (r == null) return "That review no longer exists.";
        if (status == ReviewStatus.Published && !r.ConsentToPublish) return "They have not allowed it to be published.";
        r.Status = status; r.DecidedBy = by.Name; r.DecidedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var company = (await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.TenantId))?.CompanyName ?? "";
        await _admins.LogAsync(by.Name, status switch
        {
            ReviewStatus.Published => "Published the company's review on the website",
            ReviewStatus.Declined => "Declined the company's review",
            _ => "Took the company's review back to waiting",
        }, company);
        return "";
    }

    /// <summary>What the website shows: published reviews, newest first.</summary>
    public async Task<List<CompanyReview>> PublishedAsync(int take = 6)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanyReviews.AsNoTracking().Where(r => r.Status == ReviewStatus.Published && r.ConsentToPublish)
            .OrderByDescending(r => r.DecidedUtc).Take(take).ToListAsync();
    }
}
