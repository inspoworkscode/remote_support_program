using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>
/// The portal addresses the console opens through the hand-off (2026-10-23, HANDOVER §159): a ticket by its number in
/// the company, never by its database id, which one sequence gives every company. The server still takes the old id
/// shapes and translates them, so a link this builds from an older server's message (no number in it) lands too.
/// </summary>
internal static class Portal
{
    public static string Ticket(TicketSummary t) => t.Number > 0 ? $"/portal/t/{t.Number}" : $"/portal/tickets/{t.Id}";
}
