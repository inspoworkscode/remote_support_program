using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Every recurring task this person may see (2026-10-07): the active ones by when they are next
/// due, then the cancelled ones. Whoever sees every ticket sees them all; a technician sees the
/// ones they are on.
/// </summary>
public sealed class RecurringModel : PortalPage
{
    private readonly RecurringTaskService _rec;
    public RecurringModel(TenantService t, RecurringTaskService rec) : base(t) => _rec = rec;

    public List<RecurringRow> Rows { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(string? done, int? n)
    {
        if (!await LoadAsync()) return Redirect("/login");
        Rows = await _rec.ListAsync(TenantId, MyRole, Me);
        // The address carries a code and the task's number, never words (§105) nor its id (§159); the words are the row's.
        var row = Rows.FirstOrDefault(x => x.Task.Number == n)?.Task;
        if (row != null)
            Message = done switch
            {
                "set" => $"\"{row.Title}\" is set up. " + (row.LastOpenedFor != null
                    ? "Today's ticket is open."
                    : $"The first one opens on the morning of {RecurringTaskService.Day(row.NextDue)}."),
                "changed" => $"\"{row.Title}\" is changed. Next: {RecurringTaskService.Day(row.NextDue)}.",
                "cancelled" => $"\"{row.Title}\" is cancelled. No more will be opened.",
                _ => ""
            };
        return Page();
    }
}
