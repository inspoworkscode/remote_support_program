using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Five companies at a time (2026-10-22). Clint: "My thought process is allow 5 companies in at a time and rather test it
/// that way." Every rule about a place is here.
///
/// <para><b>What takes a place</b> (<see cref="Taking"/>): a paying company — not Anywhere IT (not billed), not a pilot, not
/// the free version — that is Active, on its Trial, or Suspended (it may come back), or a signup still Pending for fewer than
/// <see cref="PendingDays"/> days (a checkout abandoned at PayFast lets its place go after that; if PayFast's notice comes
/// later, the company is let in anyway — it has paid). A place offered from the waiting list and not yet used or expired
/// holds a place too, so the public cannot take the one being offered.</para>
///
/// <para><b>When every place is taken</b> a full-version signup is offered the waiting list instead
/// (<see cref="TenantService.JoinWaitingListAsync"/> with <c>full</c>), and the website says so. The free version is not
/// counted and never waits. An admin offers the next person a place from Owner → Approvals: a link that works once, for
/// <see cref="OfferDays"/> days, spent in the same save as the company (<see cref="TenantService.SignupAsync"/>) — the same
/// shape as a pilot link. Two strangers signing up in the very same second for the last place can both get in; that is
/// one company over, not worth a lock.</para>
///
/// <para><b>The number</b> is <see cref="PlatformSettings.OpenPlaces"/>, set on Owner → Approvals (0 = no limit).</para>
/// </summary>
public sealed class OpenPlaces
{
    /// <summary>The number of places a server starts with.</summary>
    public const int Default = 5;
    /// <summary>How long an offered place is held.</summary>
    public const int OfferDays = 7;
    /// <summary>How long a signup still waiting (for its card, or for approval) holds its place.</summary>
    public const int PendingDays = 3;

    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly PlatformAdmins _admins;
    private readonly PlatformMailService _mail;
    private readonly RegionServers _regions;
    private readonly IConfiguration _cfg;

    public OpenPlaces(IDbContextFactory<PlatformDb> dbf, PlatformAdmins admins, PlatformMailService mail, RegionServers regions, IConfiguration cfg)
    { _dbf = dbf; _admins = admins; _mail = mail; _regions = regions; _cfg = cfg; }

    /// <summary>The fingerprint a place link's token is kept as. The token itself is never stored.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("rsp-place-link:" + (token ?? "").Trim().ToUpperInvariant())));

    /// <summary>Whether a company takes a place, as a query — THE rule.</summary>
    public static Expression<Func<Tenant, bool>> Taking(DateTime utcNow)
    {
        var pendingSince = utcNow.AddDays(-PendingDays);
        // Neither one-person plan takes a place: the Free version, nor the Basic version (stored "basic" since §163).
        return t => !t.NotBilled && !t.Pilot && t.Plan != FreePlan.Name && t.Plan != FreePlan.BasicName
            && (t.Status == TenantStatus.Active || t.Status == TenantStatus.Trial || t.Status == TenantStatus.Suspended
                || (t.Status == TenantStatus.Pending && t.CreatedUtc > pendingSince));
    }

    /// <summary>The places now: how many there are (0 = no limit), the companies in them, and offers holding one.</summary>
    public sealed record State(int Limit, int Companies, int Offers)
    {
        public int Taken => Companies + Offers;
        public bool Limited => Limit > 0;
        public bool Full => Limited && Taken >= Limit;
        public int Left => Limited ? Math.Max(0, Limit - Taken) : int.MaxValue;
    }

    public async Task<State> StateAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await StateAsync(db);
    }

    /// <summary>The places, read through a context already open — for <see cref="TenantService.SignupAsync"/>.</summary>
    public static async Task<State> StateAsync(PlatformDb db)
    {
        var now = DateTime.UtcNow;
        var limit = (await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync())?.OpenPlaces ?? Default;
        var companies = await db.Tenants.CountAsync(Taking(now));
        var offers = await db.WaitingList.CountAsync(w => w.PlaceTokenHash != "" && w.PlaceUsedUtc == null && w.PlaceExpiresUtc > now);
        return new State(limit, companies, offers);
    }

    /// <summary>Change the number of places. A Manager or above; 0 for no limit. "" when done.</summary>
    public async Task<string> SetLimitAsync(PlatformAdmins.Actor by, int places)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return "Only a Manager or above can change the number of places.";
        if (places is < 0 or > 1000) return "Give a number of places from 0 (no limit) upwards.";
        await using var db = await _dbf.CreateDbContextAsync();
        var s = await db.PlatformSettings.FirstOrDefaultAsync();
        if (s == null) { s = new PlatformSettings { Id = 1 }; db.PlatformSettings.Add(s); }
        var was = s.OpenPlaces;
        if (was == places) return "";
        s.OpenPlaces = places;
        await db.SaveChangesAsync();
        await _admins.LogAsync(by.Name, $"Open places {Words(was)} → {Words(places)}", "");
        return "";
    }

    private static string Words(int n) => n == 0 ? "no limit" : n.ToString();

    /// <summary>
    /// Offer somebody on the waiting list a place: a link that works once, for <see cref="OfferDays"/> days, emailed to
    /// them — and returned, so the admin can send it by hand if the email does not go. A Manager or above; only for a
    /// region this server holds. Error "" when made.
    /// </summary>
    public async Task<(string link, string error, string emailed)> OfferAsync(PlatformAdmins.Actor by, int entryId)
    {
        if (!PlatformAdmins.MayManage(by.Level)) return ("", "Only a Manager or above can offer a place.", "");
        var token = Passwords.RandomToken(18);
        WaitingListEntry e;
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            var row = await db.WaitingList.FirstOrDefaultAsync(w => w.Id == entryId);
            if (row == null) return ("", "That entry is no longer on the waiting list.", "");
            if (row.PlaceUsedUtc != null) return ("", $"{row.CompanyName} has already signed up with the place they were offered.", "");
            if (_regions.Where(row.Region).place != RegionServers.Place.Here) return ("", $"{row.CompanyName} is waiting for a region this server does not hold.", "");
            row.PlaceTokenHash = Hash(token);
            row.PlaceOfferedUtc = DateTime.UtcNow;
            row.PlaceExpiresUtc = DateTime.UtcNow.AddDays(OfferDays);
            await db.SaveChangesAsync();
            e = row;
        }
        var link = $"{(_cfg["Platform:PublicUrl"] ?? "").TrimEnd('/')}/signup?region={e.Region}&plan=full&place={token}";
        var body = new StringBuilder($"Hello {e.ContactName},\n\n")
            .Append($"A place has opened on The Remote Support Program, and it is yours: sign {e.CompanyName} up with this link.\n\n")
            .Append(link).Append("\n\n")
            .Append($"It works once, and we hold the place for you for {OfferDays} days, until {e.PlaceExpiresUtc:d MMMM yyyy}.\n\n")
            .Append("The Remote Support Program\n").ToString();
        var r = await _mail.SendAsync(new[] { e.Email }, "A place for you on The Remote Support Program", body, "The Remote Support Program", null, copyOwner: true);
        await _admins.LogAsync(by.Name, $"Place offered (held until {e.PlaceExpiresUtc:yyyy-MM-dd}){(r == "sent" ? ", emailed" : ", NOT emailed: " + r)}", e.CompanyName);
        return (link, "", r == "sent" ? $"Emailed to {e.Email}." : $"The email did NOT go ({r}) — send them the link yourself.");
    }

    /// <summary>Why an offered place may not be used now, or "" when it may.</summary>
    public static string Why(WaitingListEntry? e, DateTime utcNow) =>
        e == null ? NotAPlace
        : e.PlaceUsedUtc != null ? "This place has already been used to sign a company up. Sign in instead."
        : e.PlaceExpiresUtc is not { } x || x <= utcNow ? "The place we held for you has run out. Reply to our email and we will offer you the next one."
        : "";

    public const string NotAPlace = "This is not a place link we made. Check that the whole link was copied.";

    /// <summary>The offer a token opens, if it may still be used — or null and why not.</summary>
    public async Task<(WaitingListEntry? entry, string why)> FindAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, NotAPlace);
        var hash = Hash(token);
        await using var db = await _dbf.CreateDbContextAsync();
        var e = await db.WaitingList.AsNoTracking().FirstOrDefaultAsync(w => w.PlaceTokenHash == hash);
        return (e, Why(e, DateTime.UtcNow));
    }

    /// <summary>The words a full signup sees.</summary>
    public static string FullWords(State s) =>
        $"{FullStart} {s.Limit} companies at a time, so that each one gets our full attention — and all {s.Limit} places are taken right now. "
        + "Join the waiting list and we will email you as soon as a place opens.";

    private const string FullStart = "We let in";

    /// <summary>Whether a signup was refused because every place is taken — the page then offers the waiting list.</summary>
    public static bool IsFullRefusal(string error) => error.StartsWith(FullStart, StringComparison.Ordinal);
}
