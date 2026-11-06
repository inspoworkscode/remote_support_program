using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Setting up a recurring task, and changing or cancelling one (2026-10-07). New ticket's
/// "Recurring task" comes here: a schedule is not a ticket, and it offers EVERYBODY as its people,
/// the office too, where a ticket's form offers the console's roster. Every rule is
/// <see cref="RecurringTaskService"/>'s; this page only carries the words across.
/// </summary>
[TicketWork]
public sealed class RecurringEditModel : PortalPage
{
    private readonly RecurringTaskService _rec;
    private readonly ClientService _clients;
    private readonly IDbContextFactory<PlatformDb> _dbf;

    public RecurringEditModel(TenantService t, RecurringTaskService rec, ClientService clients, IDbContextFactory<PlatformDb> dbf) : base(t)
    { _rec = rec; _clients = clients; _dbf = dbf; }

    /// <summary>Null when setting one up.</summary>
    public RecurringTask? Task { get; private set; }
    public List<Ticket> Opened { get; private set; } = new();
    public List<string> Kinds { get; private set; } = new();
    public List<string> Everyone { get; private set; } = new();
    public bool MayChange => Task == null || (Task.CancelledUtc == null && RecurringTaskService.MayChange(Task, MyRole, Me));
    public PeoplePicker Picker => new(AssignTo, Everyone, Me, MayChange, MayChange, MayChange, "Who it goes to");
    public string AssignTo => Assignees.Join(People);
    public string ClientLabel { get; private set; } = "";

    [BindProperty] public string Subject { get; set; } = "";
    [BindProperty] public string Details { get; set; } = "";
    [BindProperty] public string Kind { get; set; } = "";
    /// <summary>What was typed when "New type…" was chosen.</summary>
    [BindProperty] public string NewKind { get; set; } = "";
    [BindProperty] public int ClientId { get; set; }
    [BindProperty] public int Priority { get; set; } = 1;
    [BindProperty(Name = "people")] public List<string> People { get; set; } = new();
    [BindProperty] public int Repeats { get; set; } = (int)Data.Repeats.Weekly;
    [BindProperty] public bool WeekdaysOnly { get; set; }
    /// <summary>Each ticked day's bit; added up into <see cref="RecurringTask.Days"/>.</summary>
    [BindProperty] public List<int> Day { get; set; } = new();
    [BindProperty] public int MonthDay { get; set; } = 1;
    [BindProperty] public string Reason { get; set; } = "";

    public int DaysMask => Day.Aggregate(0, (a, b) => a | b);

    private async Task<bool> LoadCommonAsync(int? id)
    {
        if (!await LoadAsync()) return false;
        Kinds = await _rec.KindsAsync(TenantId);
        await using var db = await _dbf.CreateDbContextAsync();
        Everyone = await db.Technicians.Where(t => t.TenantId == TenantId && !t.Disabled && t.Role != 0)
            .OrderBy(t => t.DisplayName).Select(t => t.DisplayName).ToListAsync();
        if (id.HasValue)
        {
            Task = await _rec.GetAsync(TenantId, id.Value);
            if (Task != null && !RecurringTaskService.MaySee(Task, MyRole, Me)) Task = null;
            if (Task != null) Opened = await _rec.TicketsOfAsync(TenantId, Task.Id);
        }
        return true;
    }

    private async Task LabelClientAsync()
    {
        var c = ClientId > 0 ? await _clients.GetAsync(TenantId, ClientId) : null;
        ClientLabel = c?.Name ?? "";
        if (c == null) ClientId = 0;
    }

    /// <summary>"new", or — once <see cref="OnPageHandlerExecutionAsync"/> has looked it up — the task's id.</summary>
    private static int? IdOf(string? which) => int.TryParse(which, out var n) ? n : null;

    /// <summary>
    /// /portal/r/new, or /portal/r/{the task's NUMBER in this company} (2026-10-23, HANDOVER §159): the number is looked up
    /// within the company and the handlers are given the id — 0, "no such task", when it is not this company's. The old
    /// /portal/recurring/{id} (and /portal/recurring/new) is sent on to the new address, 302 for a GET and 307 for a POST.
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        var which = RouteData.Values["which"]?.ToString() ?? "";
        bool digits = which.Length is > 0 and <= 9 && which.All(char.IsAsciiDigit);
        if (Request.Path.StartsWithSegments("/portal/recurring"))
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var to = which == "new" ? "/portal/r/new" + Request.QueryString
                : digits ? await PortalAddresses.FromOldAsync(db, TenantId, Request.Path + Request.QueryString) : null;
            if (to != null) { ctx.Result = HttpMethods.IsGet(Request.Method) ? Redirect(to) : RedirectPreserveMethod(to); return; }
            ctx.HandlerArguments["which"] = "0";
        }
        else if (digits)
        {
            await using var db = await _dbf.CreateDbContextAsync();
            ctx.HandlerArguments["which"] = (await PortalAddresses.RecurringIdAsync(db, TenantId, int.Parse(which))).ToString();
        }
        else if (which != "new") ctx.HandlerArguments["which"] = "0";
        await base.OnPageHandlerExecutionAsync(ctx, next);
    }

    public async Task<IActionResult> OnGetAsync(string which, int? clientId)
    {
        var id = IdOf(which);
        if (id == null && which != "new") return NotFound();
        if (!await LoadCommonAsync(id)) return Redirect("/login");
        if (id.HasValue && Task == null) return NotFound();
        if (Task != null)
        {
            Subject = Task.Title; Details = Task.Details; Kind = Task.Kind; ClientId = Task.ClientId ?? 0;
            Priority = (int)Task.Priority; People = Assignees.Parse(Task.AssignedTo); Repeats = (int)Task.Repeats;
            WeekdaysOnly = Task.WeekdaysOnly; MonthDay = Task.MonthDay;
            Day = RecurringTaskService.Week.Select(d => 1 << (int)d).Where(b => (Task.Days & b) != 0).ToList();
        }
        else
        {
            // A new one goes to whoever sets it up, until they tick somebody else (as New ticket does).
            if (Everyone.Contains(Me)) People = new() { Me };
            Kind = Kinds.FirstOrDefault() ?? "";
            ClientId = clientId ?? 0;
            // Today's weekday and date, so the obvious schedule is one tick away.
            var today = CompanyClock.Now;
            Day = new() { 1 << (int)today.DayOfWeek };
            MonthDay = today.Day;
        }
        await LabelClientAsync();
        return Page();
    }

    private RecurringInput Input() => new()
    {
        Title = Subject ?? "", Details = Details ?? "",
        Kind = Kind == "__new" ? NewKind ?? "" : Kind ?? "",
        ClientId = ClientId > 0 ? ClientId : null,
        AssignedTo = AssignTo, Priority = (TicketPriority)Math.Clamp(Priority, 0, 3),
        Repeats = Enum.IsDefined(typeof(Repeats), Repeats) ? (Repeats)Repeats : (Repeats)(-1),
        WeekdaysOnly = WeekdaysOnly, Days = DaysMask, MonthDay = MonthDay
    };

    public async Task<IActionResult> OnPostSaveAsync(string which)
    {
        var id = IdOf(which);
        if (!await LoadCommonAsync(id)) return Redirect("/login");
        if (id.HasValue && Task == null) return NotFound();
        await LabelClientAsync();
        if (Task == null)
        {
            var (task, error, _) = await _rec.CreateAsync(TenantId, Me.Length > 0 ? Me : "Office", Input());
            if (task == null) { Error = error; return Page(); }
            // Codes in the address, never the words (§105): the list page says it from the row.
            return Redirect($"/portal/recurring?done=set&n={task.Number}");
        }
        var problem = await _rec.UpdateAsync(TenantId, Task.Id, Me, MyRole, Input());
        if (problem.Length > 0) { Error = problem; return Page(); }
        return Redirect($"/portal/recurring?done=changed&n={Task.Number}");
    }

    public async Task<IActionResult> OnPostCancelAsync(string which)
    {
        if (IdOf(which) is not int id) return NotFound();
        if (!await LoadCommonAsync(id)) return Redirect("/login");
        if (Task == null) return NotFound();
        var problem = await _rec.CancelAsync(TenantId, id, Me, MyRole, Reason);
        if (problem.Length > 0)
        {
            Error = problem;
            Subject = Task.Title; Details = Task.Details; Kind = Task.Kind; ClientId = Task.ClientId ?? 0;
            Priority = (int)Task.Priority; People = Assignees.Parse(Task.AssignedTo); Repeats = (int)Task.Repeats;
            WeekdaysOnly = Task.WeekdaysOnly; MonthDay = Task.MonthDay;
            Day = RecurringTaskService.Week.Select(d => 1 << (int)d).Where(b => (Task.Days & b) != 0).ToList();
            await LabelClientAsync();
            return Page();
        }
        return Redirect($"/portal/recurring?done=cancelled&n={Task.Number}");
    }
}
