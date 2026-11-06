namespace RemoteSupport.Shared;

// ---- Latest activity (2026-10-26, HANDOVER §164) ----
//
// Clint, raised by one of his technicians: "if one clicks on latest activity, it will show any updates on all tickets as
// they are posted or when stages change ... i asked technicians for updates, they updated, but i had no way of seeing that
// there was updates posted unless i actually went back into that ticket." Newest first, All or only one's own tickets,
// narrowed by client and by who posted it; a count of what is new since you last looked. And (his follow-up): "i assign a
// ticket to Ryan, it shows up in his recent activity?" — yes, said as "assigned it to you".
//
// The portal and the console show the same entries: the platform's TicketActivityService makes them, and both draw them.

/// <summary>What one line of Latest activity is about. A string, so an older console shows a new kind as plain text.</summary>
public static class ActivityKinds
{
    /// <summary>Somebody's own words on the ticket.</summary>
    public const string Comment = "comment";
    /// <summary>The customer's emailed reply.</summary>
    public const string Customer = "customer";
    /// <summary>A step: the job moved stage, or a step's form was filled in.</summary>
    public const string Step = "step";
    /// <summary>A step was gone back over.</summary>
    public const string Back = "back";
    /// <summary>A change the ticket recorded by itself that people want to see — assigned, priority, linked, a quote.</summary>
    public const string Change = "change";
    /// <summary>A new ticket.</summary>
    public const string Opened = "opened";
    /// <summary>A line written during a session and the like — only shown with "Include automatic entries".</summary>
    public const string Automatic = "auto";
}

/// <summary>One line of Latest activity.</summary>
public sealed class ActivityEntry
{
    /// <summary>"n{note id}" or "t{ticket id}" (a new ticket) — unique in the list, and what a refresh keys on.</summary>
    public string Key { get; set; } = "";
    public DateTime AtUtc { get; set; }
    /// <summary>The ticket's id, for the console's ticket window. The portal links by <see cref="TicketNumber"/>.</summary>
    public int TicketId { get; set; }
    public int TicketNumber { get; set; }
    /// <summary>The note's id, so the portal can open the ticket AT this entry (<c>#note-{id}</c>). 0 for a new ticket.</summary>
    public long NoteId { get; set; }
    public string Subject { get; set; } = "";
    public string Client { get; set; } = "";
    public string Who { get; set; } = "";
    /// <summary>One of <see cref="ActivityKinds"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>What happened, in a few words: "Moved Booked in → Inspection findings", "Comment", "Customer replied".</summary>
    public string Headline { get; set; } = "";
    /// <summary>The first lines of what was written, shortened. May be empty (a step with no words, files alone).</summary>
    public string Text { get; set; } = "";
    public int Files { get; set; }
    /// <summary>Why this is about the person looking, or empty: "assigned it to you", "asked you". Drawn stronger.</summary>
    public string ForYou { get; set; } = "";
    /// <summary>A step that was gone back over since.</summary>
    public bool Undone { get; set; }
    /// <summary>Written after this person last looked.</summary>
    public bool IsNew { get; set; }
}

/// <summary>
/// The console asks for a page of Latest activity. <see cref="BeforeUtc"/> null is the newest page; otherwise the page
/// older than it ("Show older"). <see cref="MarkSeen"/> on the newest page says this person has now looked, which clears
/// their count; <see cref="Mine"/> is also remembered as their choice, so the badge counts what they chose to see.
/// </summary>
public sealed class ActivityRequestMsg : Msg
{
    public bool Mine { get; set; }
    /// <summary>Part of a client's name (or contact, phone, email) — the Clients page's own matching. Empty is every client.</summary>
    public string Client { get; set; } = "";
    /// <summary>Only what this person posted. Empty is everybody.</summary>
    public string Poster { get; set; } = "";
    public bool Automatic { get; set; }
    /// <summary>Include what I posted myself. Hidden by default: you know what you wrote.</summary>
    public bool Own { get; set; }
    public DateTime? BeforeUtc { get; set; }
    public bool MarkSeen { get; set; }
}

public sealed class ActivityListMsg : Msg
{
    /// <summary>Echoed, so an answer to an older request (a filter changed since) can be told apart.</summary>
    public bool Mine { get; set; }
    public string Client { get; set; } = "";
    public string Poster { get; set; } = "";
    public bool Automatic { get; set; }
    public bool Own { get; set; }
    /// <summary>The page asked for: null the newest, else "Show older" — appended, not replacing.</summary>
    public DateTime? BeforeUtc { get; set; }
    public List<ActivityEntry> Entries { get; set; } = new();
    /// <summary>There is more, older than the last entry.</summary>
    public bool More { get; set; }
    /// <summary>Everybody in the company, for "Posted by".</summary>
    public List<string> People { get; set; } = new();
}

/// <summary>
/// How many entries are new since this person last looked, in what they chose to see (All or Mine). Sent at sign-in and
/// whenever it may have changed — something was posted, or they looked somewhere else. <see cref="Mine"/> is their choice,
/// so a second console follows the first. The console's open Latest activity window takes it as "fetch again".
/// </summary>
public sealed class ActivityCountMsg : Msg
{
    public int Count { get; set; }
    public bool Mine { get; set; }
}

public static class ActivityWords
{
    /// <summary>The badge never says more than this: past it, the number is not what is being read.</summary>
    public const int MostCounted = 99;

    /// <summary>"12", or "99+" past <see cref="MostCounted"/>.</summary>
    public static string Count(int n) => n > MostCounted ? MostCounted + "+" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>How long ago, as the list says it: "just now", "5 min ago", "3 h ago", else the date and time.</summary>
    public static string Ago(DateTime atUtc, DateTime nowUtc, Func<DateTime, DateTime> local)
    {
        var d = nowUtc - atUtc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromHours(12)) return $"{(int)d.TotalHours} h ago";
        return local(atUtc).ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }
}
