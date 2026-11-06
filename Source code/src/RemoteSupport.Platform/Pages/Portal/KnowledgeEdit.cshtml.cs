using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Writing an article (3.8.5): a new one, an edit, or a ticket written up — Clint: "the option to
/// commit to the knowledgebase that then opens it as an editable item, so that it can be worded
/// correctly". Nothing is saved until Save, so opening it from a ticket twice makes nothing twice.
/// </summary>
public sealed class KnowledgeEditModel : PortalPage
{
    private readonly KnowledgeBaseService _kb;
    public KnowledgeEditModel(TenantService t, KnowledgeBaseService kb) : base(t) => _kb = kb;

    /// <summary>The article being edited, by id — looked up from the NUMBER in /portal/a/{number}/edit within the company
    /// (HANDOVER §159); 0 when it is not this company's; absent for a new one (/portal/kb/edit).</summary>
    public int? Id { get; private set; }
    /// <summary>A ticket to write up (the ticket's "Add to knowledge base"), asked by its number: <c>?t=</c>.</summary>
    [BindProperty(SupportsGet = true, Name = "t")] public int? FromTicketAsked { get; set; }
    /// <summary>The same ticket by id, looked up within the company.</summary>
    public int? FromTicket { get; private set; }
    /// <summary>A new article for this client (New article on one client's list), asked by its number: <c>?c=</c>.</summary>
    [BindProperty(SupportsGet = true, Name = "c")] public int? ForClientAsked { get; set; }
    public int? ForClient { get; private set; }
    /// <summary>A new article in this folder (New article here, inside a folder).</summary>
    [BindProperty(SupportsGet = true, Name = "folder")] public int? ForFolder { get; set; }

    public string TitleText { get; private set; } = "";
    public string BodyText { get; private set; } = "";
    public int ClientId { get; private set; }
    /// <summary>The folder it goes in; 0 is the top of its section.</summary>
    public int FolderId { get; private set; }
    /// <summary>The chosen section's folders, nested, for the Folder box.</summary>
    public List<(int id, string label)> FolderChoices { get; private set; } = new();
    public string ClientLabel { get; private set; } = "";
    /// <summary>The version the editor opened, sent back with Save so a colleague's save in between is caught.</summary>
    public int Version { get; private set; }
    public int FromTicketNumber { get; private set; }
    public bool IsNew => Id == null;
    public bool Missing { get; private set; }
    /// <summary>The chosen client's number in the company, for the links this page draws.</summary>
    public int ClientNumber { get; private set; }

    /// <summary>
    /// Everything this page is asked by number (2026-10-23, HANDOVER §159) is looked up within the company before any
    /// handler: the article in the address, <c>?t=</c> and <c>?c=</c>. An old <c>/portal/kb/edit/{id}</c>,
    /// <c>?ticket={id}</c> or <c>?client={id}</c> is sent on to the new address first.
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        if (PortalAddresses.HasOldQuery(Request.Query)) { ctx.Result = await FromOldQueryAsync(); return; }
        if (RouteData.Values.ContainsKey("number") || RouteData.Values.ContainsKey("id"))
        {
            if (await AddressedIdAsync(ctx, PortalAddresses.ArticleIdAsync) is not int id) return;
            Id = id;
        }
        var dbf = HttpContext.RequestServices.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Data.PlatformDb>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            // A ticket or client that is not this company's is none at all: a plain new article.
            if (FromTicketAsked is int tn) FromTicket = await PortalAddresses.TicketIdAsync(db, TenantId, tn) is var t and > 0 ? t : null;
            if (ForClientAsked is int cn) ForClient = await PortalAddresses.ClientIdAsync(db, TenantId, cn) is var c and > 0 ? c : null;
        }
        await base.OnPageHandlerExecutionAsync(ctx, next);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (Id is int id)
        {
            var a = await _kb.GetAsync(TenantId, id);
            if (a == null) { Missing = true; return Page(); }
            TitleText = a.Title; BodyText = a.Body; ClientId = a.ClientId ?? 0; ClientLabel = a.Client?.Name ?? "";
            Version = a.Version; FromTicketNumber = a.FromTicketNumber; FolderId = a.FolderId ?? 0;
            FromTicket = null;
        }
        else if (ForFolder is int fid && await _kb.GetFolderAsync(TenantId, fid) is { } folder)
        {
            // New article here, inside a folder: the folder's section, and the folder.
            FolderId = folder.Id; ClientId = folder.ClientId ?? 0;
            ClientLabel = ClientId > 0 ? await _kb.ClientNameAsync(TenantId, ClientId) : "";
        }
        else if (FromTicket is int tid)
        {
            var (draft, why) = await _kb.DraftFromTicketAsync(TenantId, tid, MyRole, Me);
            if (draft == null) { Error = why; FromTicket = null; }
            else
            {
                TitleText = draft.Title; BodyText = draft.Body; ClientId = draft.ClientId ?? 0; ClientLabel = draft.ClientName;
                FromTicketNumber = draft.FromTicketNumber;
            }
        }
        else if (ForClient is > 0)
        {
            ClientLabel = await _kb.ClientNameAsync(TenantId, ForClient.Value);
            ClientId = ClientLabel.Length > 0 ? ForClient.Value : 0;
        }
        await LoadFoldersAsync();
        return Page();
    }

    /// <summary>The Folder box's choices: the chosen section's folders. The page's script refills it
    /// when the client changes, from the same rule (/api/portal/kb/folders).</summary>
    private async Task LoadFoldersAsync()
    {
        var section = await _kb.FoldersAsync(TenantId, ClientId);
        FolderChoices = KnowledgeBaseService.Tree(section).Select(t => (t.folder.Id, KnowledgeBaseService.TreeLabel(t.folder.Name, t.depth))).ToList();
        if (!FolderChoices.Any(c => c.id == FolderId)) FolderId = 0;
        ClientNumber = ClientId > 0 ? (await _kb.ClientNumbersAsync(TenantId, new[] { ClientId })).GetValueOrDefault(ClientId) : 0;
    }

    public async Task<IActionResult> OnPostAsync(string? title, string? body, int clientId, int folderId, int version, int fromTicketNumber)
    {
        if (!await LoadAsync()) return Redirect("/login");
        int? client = clientId > 0 ? clientId : null;
        int? folder = folderId > 0 ? folderId : null;
        var (a, why) = Id is int id
            ? await _kb.SaveAsync(TenantId, id, version, title, body, client, folder, Me, MyRole)
            : await _kb.CreateAsync(TenantId, title, body, client, folder, FromTicket, Me, MyRole);
        if (a != null) return Redirect(PortalAddresses.Article(a.Number) + "?saved=true");

        // Refused: everything typed comes back, so nothing is lost to a missing title or a colleague's save.
        Error = why;
        TitleText = title ?? ""; BodyText = body ?? ""; ClientId = clientId; Version = version; FromTicketNumber = fromTicketNumber;
        ClientLabel = client is int c ? await _kb.ClientNameAsync(TenantId, c) : "";
        FolderId = folderId;
        await LoadFoldersAsync();
        if (Id != null && await _kb.GetAsync(TenantId, Id.Value) == null) Missing = true;
        return Page();
    }

    public string Heading =>
        !IsNew ? "Edit article" : FromTicketNumber > 0 ? $"Write up ticket #{FromTicketNumber}" : "New article";

    /// <summary>
    /// Where Save goes. Spelled out rather than left to the address bar, because a write-up the ticket
    /// refused falls back to a plain new article, and must not post the ticket's number again.
    /// </summary>
    public string Action => Id != null ? PortalAddresses.ArticleEdit(AddressNumber) : FromTicket != null ? $"/portal/kb/edit?t={FromTicketAsked}" : "/portal/kb/edit";

    public string CancelTo =>
        Id != null ? PortalAddresses.Article(AddressNumber) : FromTicket != null ? PortalAddresses.Ticket(FromTicketAsked ?? 0)
        : FolderId > 0 ? $"/portal/kb?folder={FolderId}" : ClientId > 0 ? PortalAddresses.KbFor(ClientNumber) : "/portal/kb";
}
