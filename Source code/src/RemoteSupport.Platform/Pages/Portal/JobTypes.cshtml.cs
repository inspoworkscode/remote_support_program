using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Job types (2026-10-27, HANDOVER §166–§167, brief <c>docs/NEXT-SESSION-JOBTYPES.md</c>): which categories and steps this
/// company uses. A card per category, every line in route order — the switches as tick boxes, the locked steps greyed with
/// why — and under each the way a job usually goes, redrawn as boxes are ticked. Every role may READ it (the portal
/// folder's policy); only a supervisor or the owner may change it, which <see cref="TenantService.SetJobTypesAsync"/>
/// asks again. Nothing about the routes is decided here: the cards are <see cref="JobTypes.Cards"/>, the line is
/// <see cref="JobTypes.Path"/> and the judge is <see cref="JobTypes.Problem"/>, so the browser never has its own copy.
/// </summary>
public sealed class JobTypesModel : PortalPage
{
    private readonly Hub.HubRegistry _hubs;
    public JobTypesModel(TenantService t, Hub.HubRegistry hubs) : base(t) { _hubs = hubs; }

    public bool MayChange => Permissions.CanSetJobTypes(MyRole);
    /// <summary>Sent here by the dashboard the first time (<c>?first=1</c>): the line at the top that says what this is.</summary>
    public bool First { get; private set; }
    public HashSet<string> Off { get; private set; } = new();

    /// <param name="first">1 from the dashboard's redirect. A number, not a bool: "1" does not bind to a bool, and the line
    /// at the top silently never showed (jobtypepage found it).</param>
    public async Task<IActionResult> OnGetAsync(int first = 0)
    {
        if (!await LoadAsync()) return Redirect("/login");
        First = first == 1 && MayChange && Tenant.JobTypesChosenUtc == null;
        Off = JobTypes.Parse(Tenant.JobTypesOff);
        return Page();
    }

    /// <summary>
    /// Saves the ticks. A tick box that is not posted is OFF — which includes every line of a category switched off on the
    /// page (the browser disables them), and <see cref="JobTypes.Clean"/> then leaves those out, so switching the category
    /// on again brings it back whole.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(List<string>? on)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var ticked = (on ?? new()).ToHashSet();
        var off = JobTypes.Switches.Select(s => s.Key).Where(k => !ticked.Contains(k));
        return await SaveAsync(off, "Saved. New tickets follow this from now on; tickets already open finish the way they started.");
    }

    /// <summary>"Keep everything": the first-visit answer that changes nothing, and is still an answer.</summary>
    public async Task<IActionResult> OnPostKeepAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        return await SaveAsync(Array.Empty<string>(), "Everything is switched on. You can change this here any time.");
    }

    private async Task<IActionResult> SaveAsync(IEnumerable<string> off, string said)
    {
        var why = await Tenants.SetJobTypesAsync(TenantId, MyRole, Me, off);
        if (why.Length > 0) Error = why; else Message = said;
        await LoadAsync();
        Off = JobTypes.Parse(Tenant.JobTypesOff);
        // Every console signed in follows it at once (§168): New ticket and Edit offer what the company uses now.
        if (why.Length == 0 && _hubs.Get(TenantId) is { } hub) await hub.JobTypesChangedAsync(Tenant.JobTypesOff);
        return Page();
    }

    /// <summary>
    /// The line under each card and whether the set would be refused, for ticks not yet saved — asked as boxes are ticked.
    /// Read only: it changes nothing, so anybody who can read the page may ask it.
    /// </summary>
    public async Task<IActionResult> OnGetPathAsync(string? off)
    {
        if (!await LoadAsync()) return Unauthorized();
        var clean = JobTypes.Clean((off ?? "").Split(','));
        return new JsonResult(new { problem = JobTypes.Problem(clean), lines = Lines(clean) });
    }

    /// <summary>Each card's line, by its title: the stages joined by arrows, or why there is none.</summary>
    public static Dictionary<string, string> Lines(string off)
    {
        var lines = new Dictionary<string, string>();
        foreach (var card in JobTypes.Cards)
        {
            lines[card.Title] = card.Category is TicketCategory c
                ? JobTypes.CategoryOn(off, c) ? string.Join(" → ", JobTypes.Path(c, off)) : "Switched off: no new tickets of this kind."
                : card.Title == JobTypes.GroupQuotes
                    ? JobTypes.On(off, JobTypes.Quotes) ? "A quote: " + string.Join(" → ", JobTypes.Path(TicketCategory.General, off, "quote-needed")) : "No quotes."
                    : "";
        }
        return lines;
    }
}
