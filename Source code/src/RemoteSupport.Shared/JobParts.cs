namespace RemoteSupport.Shared;

/// <summary>
/// What a job is told when somebody tries to close it while tickets that are part of it are still open (3.6.7).
///
/// Clint, 2026-10-09: "It should pop up a message saying which child tickets are still open and need to be closed
/// before the parent ticket can be closed." ONE wording for the server's refusal (TicketService.CheckChildrenClosedAsync),
/// the portal's pop-up and the console's, so the three never say it differently.
/// </summary>
public static class JobParts
{
    /// <summary>Null when nothing is open.</summary>
    public static string? Refusal(IEnumerable<(int Number, string Subject)> stillOpen)
    {
        var open = stillOpen.OrderBy(o => o.Number).ToList();
        if (open.Count == 0) return null;
        return open.Count == 1
            ? $"This job cannot be closed yet: #{open[0].Number} {open[0].Subject} is part of it and still open. Close it first."
            : $"This job cannot be closed yet: {open.Count} tickets that are part of it are still open — "
              + string.Join("; ", open.Select(o => $"#{o.Number} {o.Subject}")) + ". Close them first.";
    }
}
