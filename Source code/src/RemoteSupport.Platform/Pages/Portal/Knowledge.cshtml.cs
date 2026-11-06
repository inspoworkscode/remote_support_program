using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// The knowledge base (3.8.5): a General section and a Clients section, searched as you type.
/// Everybody's — Clint: "an option on all user's dashboards to click on their dashboard and access
/// and edit/create in the knowledgebase". The rules are KnowledgeBaseService's.
///
/// <para>Folders and subfolders in both (2026-10-06). Opening a section or a folder shows what is
/// directly in it — its folders, then its articles — like a folder on a PC; a search looks through
/// everything below where you are and says where each match lives.</para>
/// </summary>
public sealed class KnowledgeModel : PortalPage
{
    private readonly KnowledgeBaseService _kb;
    public KnowledgeModel(TenantService t, KnowledgeBaseService kb) : base(t) => _kb = kb;

    [BindProperty(SupportsGet = true, Name = "search")] public string? Search { get; set; }
    /// <summary>Asked by the client's NUMBER in the company, <c>?c=</c> (2026-10-23, HANDOVER §159). Absent: everything.
    /// 0: General only. Anything else: that one client's articles. The old <c>?client={id}</c> is sent on to it.</summary>
    [BindProperty(SupportsGet = true, Name = "c")] public int? ClientNumber { get; set; }
    /// <summary>The client shown, by id (looked up from <see cref="ClientNumber"/> within the company); 0 is General.</summary>
    public int? ClientFilter { get; private set; }
    /// <summary>Each client this page names, id → number, so every link it draws names the number.</summary>
    public Dictionary<int, int> ClientNumbers { get; private set; } = new();
    /// <summary>One folder, opened. Its section follows from it, whatever <see cref="ClientFilter"/> says.</summary>
    [BindProperty(SupportsGet = true, Name = "folder")] public int? FolderFilter { get; set; }
    /// <summary>Set after a delete, so the person is told it worked. A flag, never words from the address.</summary>
    [BindProperty(SupportsGet = true, Name = "deleted")] public bool Deleted { get; set; }
    /// <summary>What was just done to a folder — one of a fixed few words, never shown as typed.</summary>
    [BindProperty(SupportsGet = true, Name = "done")] public string? Done { get; set; }

    public string ClientName { get; private set; } = "";
    public List<KnowledgeBaseService.Listed> Articles { get; private set; } = new();
    /// <summary>Every folder of the company — a small table, and every path is drawn from it.</summary>
    public List<KbFolder> Folders { get; private set; } = new();
    /// <summary>The folder opened, or null for a section or everything.</summary>
    public KbFolder? Folder { get; private set; }
    /// <summary>The opened folder and those it is inside, outermost first.</summary>
    public List<KbFolder> FolderPath { get; private set; } = new();
    public bool Searching => !string.IsNullOrWhiteSpace(Search);
    public bool CanWrite => Permissions.CanWriteKnowledge(MyRole);
    public bool CanRemoveFull => Permissions.CanDeleteKnowledge(MyRole);
    /// <summary>The section a new folder or article made here goes in: General is 0.</summary>
    public int HereClient => Folder?.ClientId ?? (ClientFilter > 0 ? ClientFilter.Value : 0);
    /// <summary>Where this page is in a section — General, a client, or a folder — rather than everything.</summary>
    public bool InSection => Folder != null || ClientFilter != null;

    public List<KnowledgeBaseService.Listed> General => Articles.Where(a => a.Article.ClientId == null).ToList();
    /// <summary>Each client with articles or folders, by name — the Clients section.</summary>
    public List<(int id, string name)> ClientsShown { get; private set; } = new();

    /// <summary>An old <c>?client={id}</c> — a bookmark, or a page left open since before 2026-10-23 — is sent on to <c>?c={number}</c>
    /// first, for a GET or a post alike, so a folder made from an old page still lands in the right section.</summary>
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        if (PortalAddresses.HasOldQuery(Request.Query)) { ctx.Result = await FromOldQueryAsync(); return; }
        await base.OnPageHandlerExecutionAsync(ctx, next);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await ShowAsync();
        if (Deleted) Message = "Deleted. It is gone from the knowledge base for everybody.";
        Message = Done switch
        {
            "made" => "Folder made.",
            "changed" => "Folder saved.",
            "removed" => "Folder removed. Anything that was in it is here now.",
            _ => Message
        };
        return Page();
    }

    private async Task ShowAsync()
    {
        Folders = await _kb.AllFoldersAsync(TenantId);
        // A number that is not one of this company's clients shows everything, as an unknown client always has.
        ClientFilter = ClientNumber is int asked ? await _kb.ClientIdOfNumberAsync(TenantId, asked) is var cid and >= 0 ? cid : null : null;
        if (FolderFilter is int fid)
        {
            Folder = Folders.FirstOrDefault(f => f.Id == fid);
            // A folder since removed: where it was is unknown, so the whole knowledge base.
            if (Folder == null) FolderFilter = null;
            else ClientFilter = Folder.ClientId ?? 0;
        }
        if (ClientFilter > 0)
        {
            ClientName = await _kb.ClientNameAsync(TenantId, ClientFilter.Value);
            // A client of another company, or none at all: everything, rather than an empty page
            // that looks as if the knowledge base were empty.
            if (ClientName.Length == 0) { ClientFilter = null; Folder = null; FolderFilter = null; }
        }
        FolderPath = KnowledgeBaseService.PathOf(Folders, Folder?.Id);
        Articles = await _kb.SearchAsync(TenantId, Search, ClientFilter);
        if (Folder != null)
        {
            var inside = KnowledgeBaseService.WithInside(Folders, Folder.Id);
            Articles = Articles.Where(a => FolderOf(a.Article) is int f && inside.Contains(f)).ToList();
        }
        if (ClientFilter == null)
        {
            var names = await _kb.ClientNamesAsync(TenantId, Folders.Where(f => f.ClientId != null).Select(f => f.ClientId!.Value)
                .Concat(Articles.Where(a => a.Article.ClientId != null).Select(a => a.Article.ClientId!.Value)).Distinct());
            // Searching: only clients with a match. Not searching: every client with anything at all.
            ClientsShown = names
                .Where(c => Searching ? Articles.Any(a => a.Article.ClientId == c.Key) : true)
                .OrderBy(c => c.Value, StringComparer.OrdinalIgnoreCase).Select(c => (c.Key, c.Value)).ToList();
        }
        ClientNumbers = await _kb.ClientNumbersAsync(TenantId, ClientsShown.Select(c => c.id).Append(HereClient));
    }

    // ---------------------------------------------------------------- what is where

    /// <summary>
    /// The folder an article is shown in: its own, when that folder is still there and in the same
    /// section; otherwise the top of its section. Nothing is ever hidden by a folder gone astray.
    /// </summary>
    public int? FolderOf(KbArticle a) =>
        a.FolderId is int f && Folders.FirstOrDefault(x => x.Id == f) is { } folder && folder.ClientId == a.ClientId ? f : null;

    /// <summary>The folders directly inside a section's top (<paramref name="parentId"/> null) or a folder.</summary>
    public List<KbFolder> FoldersIn(int? clientId, int? parentId)
    {
        int? c = clientId is > 0 ? clientId : null;
        var section = Folders.Where(f => f.ClientId == c).ToList();
        var ids = section.Select(f => f.Id).ToHashSet();
        return section.Where(f => f.ParentId == parentId || (parentId == null && f.ParentId is int p && !ids.Contains(p)))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The articles directly in a section's top or a folder.</summary>
    public List<KnowledgeBaseService.Listed> ArticlesIn(int? clientId, int? folderId)
    {
        int? c = clientId is > 0 ? clientId : null;
        return Articles.Where(a => a.Article.ClientId == c && FolderOf(a.Article) == folderId).ToList();
    }

    /// <summary>How many articles a folder holds, the folders inside it included.</summary>
    public int CountInside(KbFolder f)
    {
        var inside = KnowledgeBaseService.WithInside(Folders, f.Id);
        return Articles.Count(a => FolderOf(a.Article) is int x && inside.Contains(x));
    }

    /// <summary>"Printers › HP" for a search result, so a match says where it lives.</summary>
    public string WhereIs(KbArticle a) => KnowledgeBaseService.PathText(Folders, FolderOf(a));

    /// <summary>Where a folder may be moved to: the top of its section, or any folder of the same section not inside it.</summary>
    public List<(KbFolder folder, int depth)> MoveTargets(KbFolder f)
    {
        var section = Folders.Where(x => x.ClientId == f.ClientId).ToList();
        var inside = KnowledgeBaseService.WithInside(section, f.Id);
        return KnowledgeBaseService.Tree(section).Where(t => !inside.Contains(t.folder.Id)).ToList();
    }

    // ---------------------------------------------------------------- folders

    public async Task<IActionResult> OnPostNewFolderAsync(string? name)
    {
        if (!await LoadAsync()) return Redirect("/login");
        await ShowAsync();
        var (f, why) = await _kb.CreateFolderAsync(TenantId, name, HereClient, Folder?.Id, Me, MyRole);
        if (f == null) { Error = why; PendingName = name ?? ""; return Page(); }
        return Redirect(Here(Folder?.Id, HereClient) + "&done=made");
    }

    public async Task<IActionResult> OnPostEditFolderAsync(string? name, int parentId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        await ShowAsync();
        if (Folder == null) return Redirect("/portal/kb");
        var why = await _kb.EditFolderAsync(TenantId, Folder.Id, name, parentId > 0 ? parentId : null, Me, MyRole);
        if (why.Length > 0) { Error = why; EditOpen = true; return Page(); }
        return Redirect(Here(Folder.Id, HereClient) + "&done=changed");
    }

    public async Task<IActionResult> OnPostRemoveFolderAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await ShowAsync();
        if (Folder == null) return Redirect("/portal/kb");
        var (parent, why) = await _kb.RemoveFolderAsync(TenantId, Folder.Id, Me, MyRole);
        if (why.Length > 0) { Error = why; return Page(); }
        return Redirect(Here(parent, HereClient) + "&done=removed");
    }

    /// <summary>What was typed for a new folder that was refused, so it is not lost.</summary>
    public string PendingName { get; private set; } = "";
    /// <summary>The rename/move box open, because what was asked of it was refused.</summary>
    public bool EditOpen { get; private set; }

    /// <summary>The address of a folder, or of a section's top — a client named by its number.</summary>
    public string Here(int? folderId, int clientId) =>
        folderId is int f ? $"/portal/kb?folder={f}" : SectionAddress(clientId);

    /// <summary>A section's top: General (client 0), or a client's articles, by the client's number.</summary>
    public string SectionAddress(int clientId) => PortalAddresses.KbFor(clientId > 0 ? NumberOf(clientId) : 0);

    /// <summary>A client's number, from those this page has looked up.</summary>
    public int NumberOf(int clientId) => ClientNumbers.TryGetValue(clientId, out var n) ? n : -1;

    public static string Meta(KnowledgeBaseService.Listed l) =>
        $"{(l.Article.Version > 1 ? "Updated" : "Written")} by {l.Article.UpdatedBy} on {l.Article.UpdatedUtc.Local():d MMM yyyy}"
        + (l.Article.FromTicketNumber > 0 ? $" · from ticket #{l.Article.FromTicketNumber}" : "");
}
