namespace RemoteSupport.Shared;

/// <summary>
/// The orders a ticket list can be put in. One list for the portal and the console, so the two offer the same words and
/// mean the same thing by them; the server sorts (it takes the first few hundred, so sorting what arrived would sort the
/// wrong few hundred).
///
/// <para>2026-10-13, Clint: "we need the ability to sort tickets by date" — four choices in a Sort box (<see cref="Choices"/>).
/// 2026-10-26 (HANDOVER §165), from SuperOps' ticket list: "Every column ... can be sorted by simply clicking on Last
/// Updated, for example" — so every column is a key, and a key carries its direction: <c>"due"</c> (the column's own first
/// direction, <see cref="FirstDescending"/>), <c>"due:asc"</c>, <c>"due:desc"</c>. Clicking the column in use turns it round
/// (<see cref="Toggle"/>). The four old keys keep their old meaning, so a console from before still gets what it asks.</para>
/// </summary>
public static class TicketSort
{
    // ---- the columns ----
    public const string Number = "number";
    public const string Type = "type";
    public const string Subject = "subject";
    public const string Client = "client";
    public const string Assigned = "assigned";
    public const string Status = "status";
    public const string Priority = "priority";
    /// <summary>Last updated — newest first is the order every list has always had, and what an empty or unknown key means.</summary>
    public const string Updated = "updated";
    /// <summary>The due date, or a call out's appointment; tickets with neither last, whichever way round.</summary>
    public const string Due = "due";

    // ---- the four from before (2026-10-13), still understood ----
    public const string Newest = "newest";
    public const string Oldest = "oldest";
    /// <summary>Soonest appointment first; tickets with none after them, last updated first.</summary>
    public const string Appointment = "appointment";

    /// <summary>Every column that sorts, with its heading, in the order the portal draws them.</summary>
    public static readonly (string Key, string Heading)[] Columns =
    {
        (Number, "#"), (Type, "Type"), (Subject, "Subject"), (Client, "Client"), (Assigned, "Assigned to"),
        (Status, "Status"), (Priority, "Priority"), (Updated, "Last updated"), (Due, "Due"),
    };

    /// <summary>The Sort box's four (2026-10-13), for a console from before — the new ones sort by their headings.</summary>
    public static readonly (string Key, string Label)[] Choices =
    {
        (Updated, "Last updated"),
        (Newest, "Newest first"),
        (Oldest, "Oldest first"),
        (Appointment, "Appointment, soonest first"),
    };

    /// <summary>
    /// The way a column goes the first time it is clicked: what somebody clicking it wants to see first — the newest
    /// number and the latest update, the most urgent. Everything else A to Z, and the soonest due first.
    /// </summary>
    public static bool FirstDescending(string column) => column is Number or Updated or Priority;

    /// <summary>
    /// A key from a request, a cookie or a saved choice, made safe and written out in full — <c>"column:asc"</c> or
    /// <c>"column:desc"</c> — or one of the four from before, as it was. Anything not understood is last updated, newest first.
    /// </summary>
    public static string Of(string? key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (k is Newest or Oldest or Appointment) return k;
        var parts = k.Split(':');
        var col = parts[0];
        if (!Columns.Any(c => c.Key == col)) return Updated + ":desc";
        bool desc = parts.Length > 1 ? parts[1] == "desc" : FirstDescending(col);
        return col + (desc ? ":desc" : ":asc");
    }

    /// <summary>The column a key sorts by, and whether it is the wrong way up. The old keys say which column they were.</summary>
    public static (string Column, bool Descending) Parse(string? key) => Of(key) switch
    {
        Newest => (Number, true),
        Oldest => (Number, false),
        Appointment => (Due, false),
        var k => (k.Split(':')[0], k.EndsWith(":desc"))
    };

    /// <summary>A click on a heading: the column in use turns round; another column starts its own first way.</summary>
    public static string Toggle(string? current, string column)
    {
        var (col, desc) = Parse(current);
        return Of(column + (col == column ? (desc ? ":asc" : ":desc") : FirstDescending(column) ? ":desc" : ":asc"));
    }

    /// <summary>"▲" or "▼" beside the heading in use, else nothing — down is Z to A, the newest, the most urgent first.</summary>
    public static string Arrow(string? current, string column)
    {
        var (col, desc) = Parse(current);
        return col != column ? "" : desc ? "▼" : "▲";
    }

    /// <summary>Which date a list sorted this way should show beside each ticket — for a console from before.</summary>
    public static string DateHeading(string key) => Of(key) switch
    {
        Newest or Oldest => "Opened",
        Appointment => "Appointment",
        _ => "Updated"
    };
}
