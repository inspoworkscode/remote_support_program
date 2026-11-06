namespace RemoteSupport.Shared;

/// <summary>
/// The words of a customer's support request: a SUBJECT and a DESCRIPTION (2026-10-09).
///
/// Clint: "change the request support to include a subject line and then the description as currently the description,
/// if long creates a mess of the tickets". The tray app asks for both now; this is the one rule for what becomes the
/// ticket's subject, shared by the tray app's box (<see cref="SubjectMax"/>) and the hub, which also tidies a request
/// from an older tray app that sends one box of words: its first line, cut to length, is the subject and the whole of
/// it the description — so even an old install stops putting a paragraph in the queue.
/// </summary>
public static class SupportRequest
{
    /// <summary>Long enough for "Outlook keeps asking for my password", short enough to stay one line in the queue.</summary>
    public const int SubjectMax = 80;

    /// <summary>The subject and description a request becomes.</summary>
    public static (string Subject, string Details) Words(string? subject, string? details)
    {
        var s = (subject ?? "").Replace("\r", "").Trim();
        var d = (details ?? "").Replace("\r", "").Trim();
        var first = s.Split('\n')[0].Trim();
        bool cut = first.Length > SubjectMax || first.Length < s.Length;
        // Nothing of what they wrote is lost: a subject that had to be cut goes into the description whole.
        if (cut) d = d.Length == 0 ? s : s + "\n\n" + d;
        if (first.Length > SubjectMax) first = first[..(SubjectMax - 1)].TrimEnd() + "…";
        return (first, d);
    }
}
