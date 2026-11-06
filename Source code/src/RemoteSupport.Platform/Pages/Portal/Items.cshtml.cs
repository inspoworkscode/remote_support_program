using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// The company's quotable items, for the people who tidy the list — a supervisor, the owner, or the
/// office administrator (Clint, answer 6). Rename a badly typed one; merge two that are the same part
/// written differently. Anybody may ADD, here or while quoting; only those people tidy.
/// </summary>
public sealed class ItemsModel : PortalPage
{
    private readonly QuoteService _quotes;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public ItemsModel(TenantService t, QuoteService quotes, IDbContextFactory<PlatformDb> dbf) : base(t) { _quotes = quotes; _dbf = dbf; }

    public List<CatalogItem> Items { get; private set; } = new();
    public Dictionary<int, int> Uses { get; private set; } = new();
    public bool CanEdit => Permissions.CanEditItems(MyRole);
    /// <summary>Set after "did you mean": what was typed, and the look-alikes already on the list.</summary>
    public List<CatalogItem> Similar { get; private set; } = new();
    public CatalogItem Pending { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ? await Render() : Redirect("/login");

    /// <summary>
    /// Adds an item before anybody quotes it (Clint, 2026-10-01: "allow items to be added here as well
    /// much like we can add suppliers"). Anybody may, as anybody may while quoting — the same rule,
    /// <see cref="QuoteService.AddItemAsync"/>, so the same words are never added twice here either.
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(string? name, string? partNumber, bool confirmNew)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var before = (await _quotes.ItemsAsync(TenantId)).Select(i => i.Id).ToHashSet();
        var (item, err, similar) = await _quotes.AddItemAsync(TenantId, name ?? "", Me, confirmNew, partNumber);
        if (item != null && before.Contains(item.Id)) Error = $"\"{item.Name}\" is already on the list — the same words in another order. Nothing was added.";
        else if (item != null) Message = $"{item.Name} added.";
        else
        {
            Similar = similar;
            if (similar.Count == 0) Error = err;
            Pending = new CatalogItem { Name = name ?? "", LastPartNumber = partNumber ?? "" };
        }
        return await Render();
    }

    private async Task<IActionResult> Render()
    {
        Items = await _quotes.ItemsAsync(TenantId);
        await using var db = await _dbf.CreateDbContextAsync();
        Uses = (await db.QuoteLines.Where(l => l.TenantId == TenantId).Select(l => l.ItemId).ToListAsync())
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        return Page();
    }

    public async Task<IActionResult> OnPostRenameAsync(int itemId, string name)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var err = await _quotes.RenameItemAsync(TenantId, itemId, name ?? "", MyRole);
        if (err.Length > 0) Error = err; else Message = "Renamed.";
        return await Render();
    }

    public async Task<IActionResult> OnPostMergeAsync(int fromId, int intoId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var err = await _quotes.MergeItemAsync(TenantId, fromId, intoId, MyRole);
        if (err.Length > 0) Error = err; else Message = "Merged. Every quote that used it now points at the one you kept.";
        return await Render();
    }
}
