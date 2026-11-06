namespace RemoteSupport.Platform.Services;

/// <summary>
/// The people on a ticket. Clint, 2026-09-28: *"multiple people must be able to be assigned to the
/// same ticket from the start or added at any stage as sometimes more than one person works on the
/// same problem at the same time."*
///
/// <para><b>Stored in the column that always held the one name</b> — <c>Ticket.AssignedTo</c>, and
/// <c>Ticket.WorkedBy</c> the same way — as display names joined by <see cref="Separator"/>:
/// "Jason, Ryan". A ticket with one person reads exactly as it always did, so every row ever
/// written is already a valid list, no migration was needed, and every screen that SHOWS the field
/// shows "Jason, Ryan" without being touched. Emptiness still means nobody: the pool and the billing
/// queue keep testing <c>AssignedTo == ""</c>.</para>
///
/// <para><b>What changed is every question "is this person on it?"</b> It can no longer be
/// <c>t.AssignedTo == me</c> — that is false for Jason on "Jason, Ryan". Ask <see cref="Has"/> in
/// memory, and in a database query write <c>(Assignees.Separator + t.AssignedTo + Assignees.Separator).Contains(Assignees.Tag(me))</c>,
/// which SQLite runs as <c>instr</c>. Wrapping both sides is what stops "Ryan" matching
/// "Ryan Smith". <b>Guard <c>me != ""</c></b>: the empty tag matches an empty list.</para>
///
/// <para>The one thing this costs is that a display name may not contain a comma —
/// <c>TenantService</c> refuses one.</para>
/// </summary>
public static class Assignees
{
    public const string Separator = ", ";

    /// <summary>The names, in the order they were added, with blanks and repeats dropped.</summary>
    public static List<string> Parse(string? stored)
    {
        var list = new List<string>();
        foreach (var raw in (stored ?? "").Split(','))
        {
            var n = raw.Trim();
            if (n.Length > 0 && !list.Contains(n)) list.Add(n);
        }
        return list;
    }

    /// <summary>The stored form of a list of names. Tidies as it goes, so any caller's list is safe.</summary>
    public static string Join(IEnumerable<string?>? names) =>
        string.Join(Separator, Parse(string.Join(",", (names ?? []).Where(n => n != null))));

    /// <summary>Tidies a stored or posted value: "Ryan,Jason , Ryan" becomes "Ryan, Jason".</summary>
    public static string Normalise(string? stored) => Join(Parse(stored));

    public static bool Has(string? stored, string? name) =>
        !string.IsNullOrWhiteSpace(name) && Parse(stored).Contains(name.Trim());

    public static string With(string? stored, string name) => Join(Parse(stored).Append(name));

    public static string Without(string? stored, string name) => Join(Parse(stored).Where(n => n != name.Trim()));

    /// <summary>What a query looks for inside the wrapped column. See the class summary.</summary>
    public static string Tag(string name) => Separator + name.Trim() + Separator;

    /// <summary>"Jason", "Jason and Ryan", "Jason, Ryan and Anthony" — for sentences in the story.</summary>
    public static string Say(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "nobody",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
    };
}
