using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Latest activity from the console (2026-10-26, HANDOVER §164). Every rule is <see cref="TicketActivityService"/>'s —
/// this carries the list across and keeps each console's count true, as the portal's live stream does for its pages.
/// The one-person plans have no tickets, so nothing here reaches them (and their consoles may not ask:
/// <see cref="ConsoleMaySendOnFree"/>).
/// </summary>
public sealed partial class TenantHub
{
    private async Task SendActivityAsync(TechConn tc, ActivityRequestMsg m)
    {
        if (_registry.Activity is not { } activity || Free) return;
        try
        {
            var page = await activity.ListAsync(TenantId, tc.Who.TechnicianId, tc.Who.Role, tc.Who.DisplayName,
                new TicketActivityService.Filter(m.Mine, m.Client ?? "", null, m.Poster ?? "", m.Automatic, m.Own, m.BeforeUtc, m.MarkSeen));
            tc.Ws.Send(new ActivityListMsg
            {
                Mine = m.Mine, Client = m.Client ?? "", Poster = m.Poster ?? "", Automatic = m.Automatic, Own = m.Own,
                BeforeUtc = m.BeforeUtc, Entries = page.Entries, More = page.More, People = page.People
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] latest activity", TenantName); }
    }

    /// <summary>
    /// Each console its own count — what is new to that person, in what they chose. <paramref name="person"/> null is
    /// everybody (something was posted); a name is only them (they looked, or chose All or Mine, somewhere).
    /// </summary>
    public async Task PushActivityCountsAsync(string? person)
    {
        if (_registry.Activity is not { } activity || Free) return;
        List<TechConn> techs;
        lock (_lock) techs = _techs.Where(t => person == null || t.Who.DisplayName == person).ToList();
        // One count per person, however many consoles they have open (one, since §82 — but a hand-off is not a sign-in).
        foreach (var who in techs.GroupBy(t => t.Who.TechnicianId))
        {
            try
            {
                var (count, mine) = await activity.NewCountAsync(TenantId, who.Key);
                foreach (var t in who) t.Ws.Send(new ActivityCountMsg { Count = count, Mine = mine });
            }
            catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] latest activity count", TenantName); }
        }
    }
}
