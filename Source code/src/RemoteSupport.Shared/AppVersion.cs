using System.Reflection;

namespace RemoteSupport.Shared;

/// <summary>
/// Which build an app is (2026-10-16, auto-update stage A, docs/NEXT-SESSION-AUTOUPDATE.md).
///
/// <para>Until now every app said 1.0.0 — <c>Directory.Build.props</c> — so nothing could tell one build from another,
/// and auto-update has nothing to compare without that. <c>build.ps1</c> now stamps every project of one build with the
/// SAME version, made from the moment it was built in UTC: <c>2026.10.16.1432</c> is 14:32 UTC on 16 October 2026.
/// It only ever goes up, needs no counter kept anywhere, and reads as a date. Each part fits the 65535 a Windows
/// version allows.</para>
///
/// <para>Anything built any other way — <c>dotnet run</c>, a harness, every install made before this day — says
/// <c>1.0.0.0</c>. That is "older than any stamped build", which is what it is.</para>
/// </summary>
public static class AppVersion
{
    /// <summary>This build's version, as every app reports it.</summary>
    public static string Current { get; } = Of(typeof(AppVersion).Assembly);

    /// <summary>An assembly's version as text: <c>2026.10.16.1432</c>, or <c>1.0.0.0</c> for an unstamped build.</summary>
    public static string Of(Assembly a) => a.GetName().Version?.ToString() ?? "";

    /// <summary>A stamped build — made by build.ps1 — rather than 1.0.0.0 or nothing.</summary>
    public static bool IsStamped(string? v) => Version.TryParse(v, out var p) && p.Major >= 2000;

    /// <summary>
    /// Older first. Nothing at all (a machine that never said) is older than anything, so it is never mistaken for
    /// up to date.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        bool okA = Version.TryParse(a, out var va), okB = Version.TryParse(b, out var vb);
        if (!okA || !okB) return okA.CompareTo(okB);
        return va!.CompareTo(vb);
    }

    /// <summary>
    /// The oldest of the versions given, ignoring blanks — a machine's version is the older of its agent's and its tray
    /// app's, because the older one is the one that still needs updating. Empty when none was given.
    /// </summary>
    public static string Oldest(params string?[] versions)
    {
        string best = "";
        foreach (var v in versions)
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            if (best.Length == 0 || Compare(v, best) < 0) best = v;
        }
        return best;
    }

    /// <summary>
    /// How a version is shown to a person: the stamp as it is; <c>1.0 (old)</c> for a build from before version numbers,
    /// which is every machine installed before 2026-10-16; a dash when the machine never said.
    /// </summary>
    public static string Label(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "—";
        if (IsStamped(v)) return v!;
        return Version.TryParse(v, out var p) ? $"{p.Major}.{p.Minor} (old)" : v!;
    }

    /// <summary>When a stamped build was made, in UTC; null for anything else.</summary>
    public static DateTime? BuiltUtc(string? v)
    {
        if (!Version.TryParse(v, out var p) || p.Major < 2000 || p.Revision < 0) return null;
        try { return new DateTime(p.Major, p.Minor, p.Build, p.Revision / 100, p.Revision % 100, 0, DateTimeKind.Utc); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
