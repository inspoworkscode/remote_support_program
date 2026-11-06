using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Latest activity (2026-10-26, HANDOVER §164): every entry on every ticket this person may open, newest first — the
/// console's window of the same name shows the same list. Every rule is <see cref="TicketActivityService"/>'s; this page
/// reads the address, draws the list, and — on the newest page — says this person has looked.
///
/// <para>The address keeps the filters, so refresh and Back work: <c>mine</c> (1 Mine, 0 All; absent is their last
/// choice), <c>c</c> (a client by its number in the company, §159), <c>who</c>, <c>own</c>, <c>auto</c>, <c>before</c>
/// (Show older). The client picker sends the client's id as <c>client</c>; that is turned into <c>c</c> at once, so an
/// id never stays in the address.</para>
/// </summary>
public sealed class ActivityModel : PortalPage
{
    private readonly TicketActivityService _activity;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    public ActivityModel(TenantService t, TicketActivityService activity, IDbContextFactory<PlatformDb> dbf) : base(t)
    { _activity = activity; _dbf = dbf; }

    [BindProperty(SupportsGet = true, Name = "mine")] public int? MineAsked { get; set; }
    [BindProperty(SupportsGet = true, Name = "c")] public int? ClientNumber { get; set; }
    [BindProperty(SupportsGet = true, Name = "client")] public int? ClientPicked { get; set; }
    [BindProperty(SupportsGet = true, Name = "who")] public string? Poster { get; set; }
    [BindProperty(SupportsGet = true, Name = "own")] public bool Own { get; set; }
    [BindProperty(SupportsGet = true, Name = "auto")] public bool Automatic { get; set; }
    [BindProperty(SupportsGet = true, Name = "before")] public long? Before { get; set; }
    /// <summary>The live refresh says whether the page is in front: only then has this person seen what it brings.</summary>
    [BindProperty(SupportsGet = true, Name = "seen")] public bool Seen { get; set; } = true;

    public bool Mine { get; private set; }
    public string ClientName { get; private set; } = "";
    public TicketActivityService.Page Result { get; private set; } = new();
    public DateTime NowUtc { get; } = DateTime.UtcNow;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (ClientPicked is int picked)
        {
            // The picker's choice, by id: into the company's own number, and the address says that instead.
            await using var db = await _dbf.CreateDbContextAsync();
            int number = picked > 0 ? await PortalAddresses.ClientNumberAsync(db, TenantId, picked) : 0;
            Mine = MineAsked == 1;   // the form carries it
            return Redirect(Address(clientNumber: number > 0 ? number : null, before: null));
        }
        await ReadAsync(markSeen: Before == null);
        return Page();
    }

    /// <summary>The list alone, for the page's live refresh when something new is posted.</summary>
    public async Task<IActionResult> OnGetListAsync()
    {
        if (!await LoadAsync()) return Unauthorized();
        await ReadAsync(markSeen: Before == null && Seen);
        return Partial("_ActivityList", this);
    }

    private async Task ReadAsync(bool markSeen)
    {
        await using (var db = await _dbf.CreateDbContextAsync())
        {
            Mine = MineAsked is int m ? m == 1
                 : await db.Technicians.Where(t => t.Id == TechnicianId && t.TenantId == TenantId).Select(t => t.ActivityMine).FirstOrDefaultAsync();
            int? clientId = null;
            if (ClientNumber is int n and > 0)
            {
                var c = await db.Clients.Where(x => x.TenantId == TenantId && x.Number == n).Select(x => new { x.Id, x.Name }).FirstOrDefaultAsync();
                clientId = c?.Id ?? -1;   // not this company's: an empty list, never every client's
                ClientName = c?.Name ?? "";
            }
            DateTime? before = Before is long ticks && ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
            Result = await _activity.ListAsync(TenantId, TechnicianId, MyRole, Me,
                new TicketActivityService.Filter(Mine, "", clientId, Poster ?? "", Automatic, Own, before, markSeen));
        }
    }

    /// <summary>This page's address with the filters it has now, one of them changed.</summary>
    public string Address(bool? mine = null, int? clientNumber = -1, string? who = null, bool? own = null, bool? auto = null, long? before = -1)
    {
        var q = new List<string> { "mine=" + ((mine ?? Mine) ? "1" : "0") };
        int? c = clientNumber == -1 ? ClientNumber : clientNumber;
        if (c is int cn and > 0) q.Add("c=" + cn.ToString(CultureInfo.InvariantCulture));
        var w = who ?? Poster ?? "";
        if (w.Length > 0) q.Add("who=" + Uri.EscapeDataString(w));
        if (own ?? Own) q.Add("own=true");
        if (auto ?? Automatic) q.Add("auto=true");
        long? b = before == -1 ? Before : before;
        if (b is long bt and > 0) q.Add("before=" + bt.ToString(CultureInfo.InvariantCulture));
        return "/portal/activity?" + string.Join("&", q);
    }

    /// <summary>Where a line opens: the ticket, at that entry of its story.</summary>
    public static string LinkOf(ActivityEntry e) => PortalAddresses.Ticket(e.TicketNumber) + (e.NoteId > 0 ? "#note-" + e.NoteId.ToString(CultureInfo.InvariantCulture) : "");

    public string Ago(ActivityEntry e) => ActivityWords.Ago(e.AtUtc, NowUtc, u => u.Local());

    /// <summary>The colour of a line's mark, by what it is.</summary>
    public static string KindClass(ActivityEntry e) => e.Kind switch
    {
        ActivityKinds.Customer => "act-customer", ActivityKinds.Step => "act-step", ActivityKinds.Back => "act-back",
        ActivityKinds.Opened => "act-opened", ActivityKinds.Automatic => "act-auto", ActivityKinds.Change => "act-change",
        _ => "act-comment"
    };

    public bool Filtered => ClientNumber is > 0 || !string.IsNullOrEmpty(Poster) || Own || Automatic;
}
