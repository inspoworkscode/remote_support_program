using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// One knowledge base article (3.8.5), with every version it has had. Any version can be looked at
/// and put back — which is what makes "everybody may edit" safe.
/// </summary>
public sealed class KnowledgeArticleModel : PortalPage
{
    private readonly KnowledgeBaseService _kb;
    public KnowledgeArticleModel(TenantService t, KnowledgeBaseService kb) : base(t) => _kb = kb;

    [BindProperty(SupportsGet = true)] public int Id { get; set; }
    /// <summary>An older version to look at, from the history.</summary>
    [BindProperty(SupportsGet = true, Name = "version")] public int? ShowVersion { get; set; }
    [BindProperty(SupportsGet = true, Name = "saved")] public bool Saved { get; set; }

    public KbArticle? Article { get; private set; }
    public List<KbRevision> History { get; private set; } = new();
    /// <summary>The older version being looked at, or null for the current one.</summary>
    public KbRevision? Old { get; private set; }
    public string Title => Old?.Title ?? Article?.Title ?? "";
    public string Body => Old?.Body ?? Article?.Body ?? "";
    /// <summary>The words drawn, each picture named by its portal address (HANDOVER §159). Set by Show.</summary>
    public string Html { get; private set; } = "";
    /// <summary>The line that seems to give a password away, with the secret hidden — or empty.</summary>
    public string PasswordLine => Old == null ? KbText.LooksLikePassword(Body) : "";
    public bool CanDelete => Permissions.CanDeleteKnowledge(MyRole);
    public bool CanWrite => Permissions.CanWriteKnowledge(MyRole);

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (Saved) Message = "Saved.";
        return await Show();
    }

    private async Task<IActionResult> Show()
    {
        Article = await _kb.GetAsync(TenantId, Id);
        if (Article == null) { Response.StatusCode = StatusCodes.Status404NotFound; return Page(); }   // shown, but as a 404, as a ticket is
        History = await _kb.HistoryAsync(TenantId, Id);
        if (ShowVersion is int v && v != Article.Version) Old = History.FirstOrDefault(r => r.Version == v);
        var section = await _kb.FoldersAsync(TenantId, Article.ClientId);
        FolderPath = KnowledgeBaseService.PathOf(section, Article.FolderId);
        FolderChoices = KnowledgeBaseService.Tree(section).Select(t => (t.folder.Id, KnowledgeBaseService.TreeLabel(t.folder.Name, t.depth))).ToList();
        if (Article.ClientId is int cid) ClientNumber = (await _kb.ClientNumbersAsync(TenantId, new[] { cid })).GetValueOrDefault(cid);
        Html = KbText.Render(Body, await _kb.ImageAddressesAsync(TenantId, Body));
        return Page();
    }

    /// <summary>The folder it is in and those that folder is inside, outermost first. Empty at the top.</summary>
    public List<KbFolder> FolderPath { get; private set; } = new();
    /// <summary>Its section's folders, for Move to.</summary>
    public List<(int id, string label)> FolderChoices { get; private set; } = new();
    public string SectionName => Article?.ClientId != null ? Article.Client?.Name ?? "This client" : "General";
    /// <summary>Where "← back" goes: the folder it is in, else its section.</summary>
    public string BackTo => FolderPath.Count > 0 ? $"/portal/kb?folder={FolderPath[^1].Id}" : SectionAddress;
    /// <summary>Its section's top: General, or its client's articles by the client's number.</summary>
    public string SectionAddress => PortalAddresses.KbFor(Article?.ClientId != null ? ClientNumber : 0);
    /// <summary>Its client's number in the company, when it has one.</summary>
    public int ClientNumber { get; private set; }

    /// <summary>
    /// /portal/a/{the article's NUMBER in this company} (2026-10-23, HANDOVER §159): looked up within the company before any
    /// handler runs, so whatever was posted as an id is never read; not this company's is "not found". The old
    /// /portal/kb/{id} is sent on to the new address.
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        if (await AddressedIdAsync(ctx, PortalAddresses.ArticleIdAsync) is not int id) return;
        Id = id;
        await base.OnPageHandlerExecutionAsync(ctx, next);
    }

    /// <summary>Into another folder of its own section, or to the top. Changing the client is the editor's.</summary>
    public async Task<IActionResult> OnPostMoveAsync(int folderId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var why = await _kb.MoveArticleAsync(TenantId, Id, folderId > 0 ? folderId : null, Me, MyRole);
        if (why.Length > 0) Error = why; else Message = "Moved.";
        return await Show();
    }

    public async Task<IActionResult> OnPostRestoreAsync(int version)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var why = await _kb.RestoreAsync(TenantId, Id, version, Me, MyRole);
        if (why.Length > 0) { Error = why; ShowVersion = version; return await Show(); }
        Message = $"Version {version} is back, saved as the newest version. Nothing was lost — the one it replaced is in the history too.";
        ShowVersion = null;
        return await Show();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await Show();
        var back = Article != null ? BackTo : null;
        var why = await _kb.DeleteAsync(TenantId, Id, Me, MyRole);
        if (why.Length > 0) { Error = why; return await Show(); }
        // Back where it was — its folder, or its section — rather than the top of everything.
        return Redirect(back != null ? back + "&deleted=true" : "/portal/kb?deleted=true");
    }

    public static string When(DateTime utc) => utc.Local().ToString("d MMM yyyy HH:mm");
}
