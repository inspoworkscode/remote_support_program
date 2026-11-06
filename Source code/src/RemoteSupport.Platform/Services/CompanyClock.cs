using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The time it is FOR THE COMPANY (2026-10-05). Clint: "I see the time zone for Anywhere I.T has
/// changed again." It had not changed — it never existed. Every time on the portal was the SERVER's
/// local time (<c>ToLocalTime()</c>, <c>DateTime.Now</c>): right on the office PC, set to South
/// Africa, and two hours behind on the live server, which runs on UTC (<c>timedatectl</c>, measured).
/// A typed appointment was read the same way, so "09:00" was saved as 09:00 UTC.
///
/// <para>Now each company has its own <see cref="Tenant.TimeZone"/>, and every time the platform shows
/// or reads goes through here: <see cref="Local(DateTime)"/> to show, <see cref="ToUtc"/> for what was
/// typed, <see cref="Now"/> for "today". Which company is <see cref="Current"/> is set once per request
/// (Program.cs, from the signed-in person) and by the hub for console and machine traffic; anything with
/// no company — the platform owner's pages — uses the zone of this server's region.
/// <b>Never write <c>ToLocalTime()</c> or <c>DateTime.Now</c> in the platform again</b>: they ask the
/// server, which is in whatever zone its host chose.</para>
///
/// <para><b>Windows needs its own names.</b> The platform is built with invariant globalisation, and on
/// Windows that leaves .NET unable to find "Africa/Johannesburg" at all (measured: TimeZoneNotFound).
/// Linux knows the IANA names. So each zone carries its Windows name too, and <see cref="Find"/> tries
/// both — which also fixes web chat and browser support hours, silently checked in UTC on Windows.</para>
/// </summary>
public static class CompanyClock
{
    /// <summary>
    /// The zones offered, most likely first. Stored by IANA name; the Windows name is for <see cref="Find"/>
    /// on Windows; the region is which signup offers it (Clint: "selecting the us as location still could
    /// have different time zones") — "" is offered only on Branding and the hours pages.
    /// </summary>
    public static readonly (string Id, string Label, string Windows, string Region)[] Zones =
    {
        ("Africa/Johannesburg", "South Africa (SAST)", "South Africa Standard Time", "ZA"),
        ("Europe/London", "United Kingdom", "GMT Standard Time", "UK"),
        ("Europe/Dublin", "Ireland", "GMT Standard Time", "EU"),
        ("Europe/Lisbon", "Portugal", "GMT Standard Time", "EU"),
        ("Europe/Amsterdam", "Central Europe", "W. Europe Standard Time", "EU"),
        ("Europe/Helsinki", "Eastern Europe", "FLE Standard Time", "EU"),
        ("America/New_York", "US Eastern", "Eastern Standard Time", "US"),
        ("America/Chicago", "US Central", "Central Standard Time", "US"),
        ("America/Denver", "US Mountain", "Mountain Standard Time", "US"),
        ("America/Phoenix", "US Arizona", "US Mountain Standard Time", "US"),
        ("America/Los_Angeles", "US Pacific", "Pacific Standard Time", "US"),
        ("America/Anchorage", "US Alaska", "Alaskan Standard Time", "US"),
        ("Pacific/Honolulu", "US Hawaii", "Hawaiian Standard Time", "US"),
        ("America/St_Johns", "Canada Newfoundland", "Newfoundland Standard Time", "CA"),
        ("America/Halifax", "Canada Atlantic", "Atlantic Standard Time", "CA"),
        ("America/Toronto", "Canada Eastern", "Eastern Standard Time", "CA"),
        ("America/Winnipeg", "Canada Central", "Central Standard Time", "CA"),
        ("America/Regina", "Canada Saskatchewan", "Canada Central Standard Time", "CA"),
        ("America/Edmonton", "Canada Mountain", "Mountain Standard Time", "CA"),
        ("America/Vancouver", "Canada Pacific", "Pacific Standard Time", "CA"),
        ("Africa/Nairobi", "East Africa", "E. Africa Standard Time", ""),
        ("Africa/Lagos", "West Africa", "W. Central Africa Standard Time", ""),
        ("Asia/Dubai", "Gulf", "Arabian Standard Time", ""),
        ("Australia/Sydney", "Australia (Sydney)", "AUS Eastern Standard Time", ""),
        ("Pacific/Auckland", "New Zealand", "New Zealand Standard Time", ""),
        ("UTC", "UTC", "UTC", "")
    };

    /// <summary>The zones a company in this region chooses from at signup.</summary>
    public static IEnumerable<(string Id, string Label, string Windows, string Region)> For(string? region) =>
        Zones.Where(z => z.Region == Pricing.For(region).Code);

    /// <summary>
    /// Whether the region has more than one zone, so signup must ASK (the US, Canada, Europe) rather than
    /// fill it in (South Africa, the United Kingdom).
    /// </summary>
    public static bool MustChoose(string? region) => For(region).Count() > 1;

    public const string SouthAfrica = "Africa/Johannesburg";

    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Found = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A zone by its stored name, on Linux or Windows alike; anything unknown is South Africa, never a silent UTC.</summary>
    public static TimeZoneInfo Find(string? id) => Found.GetOrAdd(string.IsNullOrWhiteSpace(id) ? SouthAfrica : id.Trim(), k =>
    {
        foreach (var name in new[] { k, Zones.FirstOrDefault(z => z.Id.Equals(k, StringComparison.OrdinalIgnoreCase)).Windows })
        {
            if (string.IsNullOrEmpty(name)) continue;
            try { return TimeZoneInfo.FindSystemTimeZoneById(name); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        }
        return k == SouthAfrica ? TimeZoneInfo.CreateCustomTimeZone(SouthAfrica, TimeSpan.FromHours(2), "South Africa", "SAST") : Find(SouthAfrica);
    });

    /// <summary>Whether a name is one we offer.</summary>
    public static bool Offered(string? id) => Zones.Any(z => z.Id == id);

    /// <summary>
    /// The zone a new company starts with, from where it is: South Africa Johannesburg, the UK London,
    /// Europe by country, the US and Canada their Eastern zone (they choose another on Branding).
    /// </summary>
    public static string DefaultFor(string? region, string? country = null) => Pricing.For(region).Code switch
    {
        "UK" => "Europe/London",
        "US" => "America/New_York",
        "CA" => "America/Toronto",
        "EU" => (country ?? "").Trim() switch
        {
            "Ireland" => "Europe/Dublin",
            "Portugal" => "Europe/Lisbon",
            "Finland" or "Greece" => "Europe/Helsinki",
            _ => "Europe/Amsterdam"
        },
        _ => SouthAfrica
    };

    // ---- which company, now --------------------------------------------------------------------

    private static readonly AsyncLocal<TimeZoneInfo?> Ambient = new();

    /// <summary>For anything with no company (the platform owner's pages): this server's region. Set at start-up.</summary>
    public static TimeZoneInfo ServerDefault { get; set; } = Find(SouthAfrica);

    /// <summary>The zone of the company this request or connection is for.</summary>
    public static TimeZoneInfo Current => Ambient.Value ?? ServerDefault;

    /// <summary>From here on, in this request or connection, times are this company's.</summary>
    public static void Set(TimeZoneInfo zone) => Ambient.Value = zone;

    private static readonly ConcurrentDictionary<int, (TimeZoneInfo zone, DateTime at)> ByTenant = new();

    /// <summary>A company's zone, read at most once a minute — and at once after <see cref="Forget"/>.</summary>
    public static async Task<TimeZoneInfo> ForTenantAsync(IDbContextFactory<PlatformDb> dbf, int tenantId)
    {
        if (ByTenant.TryGetValue(tenantId, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(1)) return c.zone;
        await using var db = await dbf.CreateDbContextAsync();
        var id = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.TimeZone).FirstOrDefaultAsync();
        var zone = id == null ? ServerDefault : Find(id);
        ByTenant[tenantId] = (zone, DateTime.UtcNow);
        return zone;
    }

    /// <summary>The company's settings changed: read its zone again next time.</summary>
    public static void Forget(int tenantId) => ByTenant.TryRemove(tenantId, out _);

    // ---- the arithmetic ------------------------------------------------------------------------

    /// <summary>A stored (UTC) time as the company reads it.</summary>
    public static DateTime Local(this DateTime utc) => Local(utc, Current);
    public static DateTime? Local(this DateTime? utc) => utc?.Local();
    public static DateTime Local(this DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    /// <summary>The company's date and time now.</summary>
    public static DateTime Now => DateTime.UtcNow.Local();

    /// <summary>
    /// A time somebody typed, in the company's zone, as UTC for storing. A time that does not exist
    /// (the hour clocks skip in spring) is read an hour later rather than refused.
    /// </summary>
    public static DateTime ToUtc(DateTime companyLocal) => ToUtc(companyLocal, Current);
    public static DateTime ToUtc(DateTime companyLocal, TimeZoneInfo zone)
    {
        var t = DateTime.SpecifyKind(companyLocal, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(t)) t = t.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(t, zone);
    }
}
