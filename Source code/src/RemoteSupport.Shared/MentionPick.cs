namespace RemoteSupport.Shared;

/// <summary>
/// The list that pops up when somebody types @ in a ticket's comment box (2026-10-21). Clint: "putting in
/// the @ should list everyone assigned to the task" — the people on the ticket first, then everybody else,
/// narrowing as they type. The console uses this; the portal's <c>wwwroot/mentionpick.js</c> is the same
/// rule written again in the browser — change one, change the other.
///
/// It only saves typing. Who is flagged is still decided by the server reading the posted words
/// (<c>TicketService.MentionsIn</c>), so a name typed out by hand works exactly as before.
/// </summary>
public static class MentionPick
{
    /// <summary>Longer than any name: past this, whatever follows the @ is not somebody being looked for.</summary>
    public const int MaxQuery = 40;

    /// <summary>
    /// The @ being typed at the caret, and what follows it so far — or null when the caret is not in one.
    /// An @ straight after a letter or digit is an email address, as the server reads it.
    /// </summary>
    public static (int At, string Query)? Typing(string text, int caret)
    {
        if (string.IsNullOrEmpty(text) || caret <= 0 || caret > text.Length) return null;
        for (int i = caret - 1; i >= 0 && caret - i - 1 <= MaxQuery; i--)
        {
            var c = text[i];
            if (c == '\n' || c == '\r') return null;
            if (c != '@') continue;
            if (i > 0 && char.IsLetterOrDigit(text[i - 1])) return null;
            var q = text.Substring(i + 1, caret - i - 1);
            return q.StartsWith(' ') ? null : (i, q);
        }
        return null;
    }

    /// <summary>One line of the list.</summary>
    public readonly record struct Choice(string Name, bool OnTicket);

    /// <summary>
    /// The people on the ticket first, in the ticket's order, then everybody else in the order given —
    /// never the person typing, who would be asking nobody. A name matches when it, or any word in it,
    /// starts with what is typed; with a space typed, only the whole name from its start.
    /// </summary>
    public static List<Choice> Matches(string query, IEnumerable<string> onTicket, IEnumerable<string> everybody, string me)
    {
        var first = onTicket.Where(n => !string.IsNullOrWhiteSpace(n) && n != me).Distinct().ToList();
        var rest = everybody.Where(n => !string.IsNullOrWhiteSpace(n) && n != me && !first.Contains(n)).Distinct();
        return first.Where(n => Fits(n, query)).Select(n => new Choice(n, true))
            .Concat(rest.Where(n => Fits(n, query)).Select(n => new Choice(n, false))).ToList();
    }

    private static bool Fits(string name, string query)
    {
        if (query.Length == 0) return true;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;
        return !query.Contains(' ') && name.Split(' ').Any(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The words with the @ being typed replaced by the whole name and a space, and where the caret goes.</summary>
    public static (string Text, int Caret) Insert(string text, int at, int caret, string name)
    {
        var after = text[caret..];
        var put = "@" + name + (after.StartsWith(' ') ? "" : " ");
        var result = text[..at] + put + after;
        return (result, at + put.Length + (after.StartsWith(' ') ? 1 : 0));
    }
}
