using System.Globalization;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// When a company's browser support link rings through (3.8.2, 2026-09-30): its own hours, day by
/// day, in its own time zone — "sometimes they may have longer support hours than office hours."
/// The one place <see cref="Tenant.WebSupportHours"/> is read or written, so the settings page, the
/// public page and the hub cannot disagree about whether it is open.
/// </summary>
public static class WebSupportHours
{
    public static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    /// <summary>The time zones offered — one list, <see cref="CompanyClock.Zones"/>.</summary>
    public static (string Id, string Label, string Windows, string Region)[] Zones => CompanyClock.Zones;

    /// <summary>One day: open and close, or null for closed.</summary>
    public readonly record struct Day(TimeSpan Open, TimeSpan Close);

    /// <summary>Seven days, Monday first; null where it is closed. Anything unreadable is closed.</summary>
    public static Day?[] Parse(string? stored)
    {
        var parts = (stored ?? "").Split(';');
        var days = new Day?[7];
        for (int i = 0; i < 7; i++)
            days[i] = i < parts.Length && TryDay(parts[i], out var d) ? d : null;
        return days;
    }

    public static string Format(Day?[] days) =>
        string.Join(";", Enumerable.Range(0, 7).Select(i => days.Length > i && days[i] is { } d ? $"{Hm(d.Open)}-{Hm(d.Close)}" : ""));

    private static bool TryDay(string s, out Day d)
    {
        d = default;
        var bits = s.Trim().Split('-');
        if (bits.Length != 2 || !TryTime(bits[0], out var open) || !TryTime(bits[1], out var close)) return false;
        d = new Day(open, close);
        return close != open;
    }

    /// <summary>"8:00", "08:00", "17:30", and "24:00" for the end of the day.</summary>
    public static bool TryTime(string? s, out TimeSpan t)
    {
        t = default;
        s = (s ?? "").Trim();
        if (s == "24:00") { t = TimeSpan.FromHours(24); return true; }
        return TimeSpan.TryParseExact(s, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out t) && t < TimeSpan.FromHours(24);
    }

    public static string Hm(TimeSpan t) => t >= TimeSpan.FromHours(24) ? "24:00" : $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    /// <summary>
    /// The zone for the hours. Through <see cref="CompanyClock.Find"/> since 2026-10-05: on Windows this used
    /// to fail for every zone and fall back to UTC without a word, so hours were two hours out there.
    /// </summary>
    public static TimeZoneInfo Zone(string? id) => CompanyClock.Find(id);

    /// <summary>
    /// Whether the link rings through at this moment. A day whose close is before its open runs past
    /// midnight — "18:00-02:00" — and the small hours belong to the day it started on.
    /// </summary>
    public static bool IsOpen(Tenant t, DateTime utcNow) => IsOpen(t.WebSupportEnabled, t.WebSupportHours, t.WebSupportTimeZone, utcNow);

    /// <summary>Website chat's hours (3.8.3): the same rule over its own switch, hours and zone.</summary>
    public static bool IsChatOpen(Tenant t, DateTime utcNow) => IsOpen(t.WebChatEnabled, t.WebChatHours, t.WebChatTimeZone, utcNow);

    public static bool IsOpen(bool enabled, string hours, string zone, DateTime utcNow)
    {
        if (!enabled) return false;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), Zone(zone));
        var days = Parse(hours);
        int today = ((int)local.DayOfWeek + 6) % 7;          // Monday = 0
        var now = local.TimeOfDay;
        if (days[today] is { } d && (d.Close > d.Open ? now >= d.Open && now < d.Close : now >= d.Open)) return true;
        int yesterday = (today + 6) % 7;
        return days[yesterday] is { } y && y.Close < y.Open && now < y.Close;
    }

    /// <summary>The hours in words, days with the same hours run together: "Monday to Friday 08:00–17:00".</summary>
    public static List<string> Describe(Tenant t) => Describe(t.WebSupportHours);

    public static List<string> Describe(string hours)
    {
        var days = Parse(hours);
        var lines = new List<string>();
        int i = 0;
        while (i < 7)
        {
            int j = i;
            while (j + 1 < 7 && Equals(days[j + 1], days[i])) j++;
            if (days[i] is { } d)
                lines.Add($"{(i == j ? Days[i] : j == i + 1 ? $"{Days[i]} and {Days[j]}" : $"{Days[i]} to {Days[j]}")} {Hm(d.Open)}–{Hm(d.Close)}");
            i = j + 1;
        }
        if (lines.Count == 0) lines.Add("No hours are set.");
        return lines;
    }
}
