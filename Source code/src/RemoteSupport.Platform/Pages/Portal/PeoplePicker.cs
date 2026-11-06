using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// What <c>_People.cshtml</c> draws: one tick box per person who can work tickets, ticked for
/// everybody on this one (2026-09-28 — several people may work the same job at once).
///
/// Which boxes may be changed is <c>TicketService.UpdateAsync</c>'s rule drawn, not a second copy
/// of it: the service decides whatever is posted. A box that may not
/// be changed is shown greyed and its value posted anyway, so a greyed tick is never read as
/// "take them off".
/// </summary>
public sealed record PeoplePicker(
    string Current,
    IReadOnlyList<string> Team,
    string Me,
    bool AnyOne,
    bool MayJoin,
    bool MayPassOn,
    string Label = "People on this job")
{
    public List<string> On => Assignees.Parse(Current);

    /// <summary>Everybody to draw: the team, then anybody on it who is no longer on the team.</summary>
    public IEnumerable<string> Names => Team.Concat(On.Where(n => !Team.Contains(n)));

    public bool MayChange(string name)
    {
        if (AnyOne) return true;
        bool onIt = On.Contains(Me);
        if (name == Me) return onIt ? MayPassOn : MayJoin;
        // A colleague: a technician on the job may ADD them, and may never take one off.
        return onIt && MayPassOn && !On.Contains(name);
    }

    /// <summary>For a new ticket: everybody may be chosen, and nobody is on it yet.</summary>
    public static PeoplePicker ForNew(string current, IReadOnlyList<string> team, string label) =>
        new(current, team, "", true, true, true, label);
}
