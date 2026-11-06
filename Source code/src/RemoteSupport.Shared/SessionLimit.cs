namespace RemoteSupport.Shared;

/// <summary>
/// How many machines one technician may be connected to at once (2026-10-15).
///
/// Clint: two sessions side by side, one on each monitor, "works great" — copy on one office machine, paste on the
/// other — and he asked for two at most. ONE number and ONE wording for the hub's refusal (TenantHub.SessionLimit.cs)
/// and the console's (DashboardForm), so the two never disagree about what the limit is or how it is said.
///
/// What counts is a MACHINE, not a socket or a window: going back to a machine already open — the Reconnect button,
/// a machine coming back from a restart, a dropped connection — is never refused, because it adds nothing.
/// </summary>
public static class SessionLimit
{
    public const int PerTechnician = 2;

    /// <summary>Null when one more is allowed. <paramref name="openOn"/> is the machines already open, by name.</summary>
    public static string? Refusal(IReadOnlyCollection<string> openOn)
    {
        if (openOn.Count < PerTechnician) return null;
        var names = openOn.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var which = names.Count == 0 ? "" : $" ({string.Join(" and ", names)})";
        return $"You are already connected to {PerTechnician} machines{which}. "
             + $"{PerTechnician} at a time is the most — close one of those sessions first.";
    }
}
