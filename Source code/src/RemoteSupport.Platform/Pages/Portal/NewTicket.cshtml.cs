using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Raise a job from the portal. Reception, accounts and administrators can all use this, which is
/// why it is authorised for billing as well as tenant admins.
/// </summary>
[TicketWork]
public sealed class NewTicketModel : PortalPage
{
    private readonly ClientService _clients;
    private readonly TicketService _tickets;
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly TicketPhotoService _photos;

    public NewTicketModel(TenantService t, ClientService c, TicketService tk, IDbContextFactory<PlatformDb> dbf,
        TicketPhotoService photos) : base(t)
    { _clients = c; _tickets = tk; _dbf = dbf; _photos = photos; }

    public string[] CategoryNames => RemoteSupport.Shared.TicketSummary.CategoryNames;

    /// <summary>The categories in the order they are offered — General first, because it is the default — leaving out the
    /// ones the company has switched off (Job types). The post asks again (CategoryRefusalAsync).</summary>
    public int[] CategoryOrder => RemoteSupport.Shared.TicketSummary.CategoryOrder
        .Where(v => JobTypes.CategoryOn(Tenant.JobTypesOff, (TicketCategory)v)).ToArray();

    public List<string> Technicians { get; private set; } = new();

    /// <summary>The job type. General unless the person picks something else.</summary>
    [BindProperty] public int Category { get; set; } = (int)TicketCategory.General;
    [BindProperty] public int ClientId { get; set; }
    [BindProperty] public string ClientName { get; set; } = "";
    [BindProperty] public string NewClientPhone { get; set; } = "";
    [BindProperty] public string NewClientEmail { get; set; } = "";
    [BindProperty] public string NewClientAddress { get; set; } = "";
    [BindProperty] public string Subject { get; set; } = "";
    [BindProperty] public string Description { get; set; } = "";
    [BindProperty] public int Priority { get; set; } = 1;
    /// <summary>Everybody ticked on the form (2026-09-28: several people may share a job from the start).</summary>
    [BindProperty] public List<string> People { get; set; } = new();
    /// <summary>The ticked people as the ticket stores them: "Jason, Ryan".</summary>
    public string AssignTo => Assignees.Join(People);
    public PeoplePicker Picker => PeoplePicker.ForNew(AssignTo, Technicians, "People on this job");
    [BindProperty] public string SiteAddress { get; set; } = "";
    [BindProperty] public string AppointmentDate { get; set; } = "";
    [BindProperty] public string AppointmentTime { get; set; } = "";
    /// <summary>When the job is due (§165): "yyyy-MM-dd" from the date box, or empty.</summary>
    [BindProperty] public string DueDate { get; set; } = "";
    [BindProperty] public string AssetMake { get; set; } = "";
    [BindProperty] public string AssetModel { get; set; } = "";
    [BindProperty] public string AssetSerial { get; set; } = "";
    [BindProperty] public string Accessories { get; set; } = "";
    [BindProperty] public bool DevicePasswordProtected { get; set; }
    [BindProperty] public string DevicePassword { get; set; } = "";

    /// <summary>
    /// Identifies THIS booking form, so photos uploaded before the ticket exists can be found and
    /// attached to it when it does. Round-trips in a hidden field; a fresh one per GET, so two
    /// people booking machines in at one counter never share a pile of photographs.
    /// </summary>
    [BindProperty] public string StageId { get; set; } = "";

    /// <summary>Photos already uploaded against this form, so a refused submit does not lose them.</summary>
    public List<TicketPhoto> Staged { get; private set; } = new();
    /// <summary>None when the company does not ask for photos at booking (Job types) — what the new ticket will copy.</summary>
    public int PhotosRequired => JobTypes.PhotosInRequired(Tenant.JobTypesOff) ? TicketPhotoService.Required : 0;

    private async Task LoadTechniciansAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        Technicians = await db.Technicians
            .Where(t => t.TenantId == TenantId && !t.Disabled && (t.Role & TenantRoles.Seated) != 0)
            .OrderBy(t => t.DisplayName).Select(t => t.DisplayName).ToListAsync();
    }

    public async Task<IActionResult> OnGetAsync(int? clientId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        await LoadTechniciansAsync();
        // A fresh id per visit, so two people booking machines in at one counter never share a
        // pile of photographs.
        StageId = Guid.NewGuid().ToString("N");
        if (clientId.HasValue)
        {
            var c = await _clients.GetAsync(TenantId, clientId.Value);
            if (c != null) { ClientId = c.Id; ClientName = c.Name; }
        }
        return Page();
    }

    /// <summary>Re-reads the staged photos, so a refused submit redraws them rather than losing them.</summary>
    private async Task<IActionResult> BackToFormAsync()
    {
        Staged = await _photos.StagedAsync(TenantId, StageId, Me);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        await LoadTechniciansAsync();

        // A recurring task is a schedule with a form of its own; this form never makes one by hand,
        // which would be a "recurring" ticket that no schedule opened.
        if (TicketService.CategoryOf(Category) == TicketCategory.Recurring) return Redirect("/portal/r/new");
        // A category the company has switched off (Job types) — a page drawn before it was, or a typed-in number.
        var off = await _tickets.CategoryRefusalAsync(TenantId, TicketService.CategoryOf(Category));
        if (off.Length > 0) { Error = off; return await BackToFormAsync(); }
        if (string.IsNullOrWhiteSpace(Subject)) { Error = "Please enter a subject."; return await BackToFormAsync(); }
        // A General ticket may be an instruction to somebody in the office, with no client at all
        // (Clint, 2026-09-27). It can be linked to one later, at any stage.
        if (string.IsNullOrWhiteSpace(ClientName) && TicketService.CategoryOf(Category) != TicketCategory.General)
        { Error = "Please choose or name a client."; return await BackToFormAsync(); }

        // Three photos of the machine as it arrived, or it is not booked in. Checked before the
        // client is created, so a refused booking does not leave a new customer record behind for
        // a job that was never opened.
        var photoProblem = await _photos.CheckBookInAsync(TenantId,
            TicketService.CategoryOf(Category), StageId, Me);
        if (photoProblem.Length > 0) { Error = photoProblem; return await BackToFormAsync(); }
        var bookIn = TicketService.CheckBookIn(TicketService.CategoryOf(Category), AssignTo);
        if (bookIn.Length > 0) { Error = bookIn; return await BackToFormAsync(); }
        if (!TicketDue.TryRead(DueDate, out var due)) { Error = "That due date could not be read. Choose it again."; return await BackToFormAsync(); }
        if (TicketDue.Problem(due) is { Length: > 0 } late) { Error = late; return await BackToFormAsync(); }

        // link to the picked client, else reuse an exact name match, else create
        Client? client = ClientId > 0 ? await _clients.GetAsync(TenantId, ClientId) : null;
        if (client == null && !string.IsNullOrWhiteSpace(ClientName))
        {
            var (created, err) = await _clients.CreateAsync(TenantId, ClientName, "", NewClientPhone, NewClientEmail, NewClientAddress, "");
            if (created == null) { Error = err; return Page(); }
            client = created;
        }

        DateTime? appointment = null;
        if (!string.IsNullOrWhiteSpace(AppointmentDate))
        {
            var text = AppointmentDate + (string.IsNullOrWhiteSpace(AppointmentTime) ? " 09:00" : " " + AppointmentTime);
            if (DateTime.TryParse(text, out var local)) appointment = CompanyClock.ToUtc(local);   // typed in the company's zone, not the server's
        }

        var category = TicketService.CategoryOf(Category);
        var who = Me.Length > 0 ? Me : "Office";

        // Anyone may open a ticket — in most shops it is the office administrator who takes the
        // call and opens it — and since 2026-09-24 whoever opens it names who does it, technician
        // or not (Permissions.CanAssignWhenOpening). The line stays so the rule has one place to
        // change if that is ever narrowed again. It used to say: a technician opening
        // a job may keep it or leave it in the pool, and may not put it on a colleague's list.
        if (!Permissions.CanAssignWhenOpening(MyRole)) People = People.Where(p => p == who).ToList();

        var ticket = await _tickets.CreateAsync(TenantId, Subject, Description ?? "", who, TicketSource.Manual,
            "", "", client?.ContactName ?? "", (TicketPriority)Math.Clamp(Priority, 0, 3),
            AssignTo, category, client?.Id, t =>
            {
                t.SiteAddress = string.IsNullOrWhiteSpace(SiteAddress) ? client?.Address ?? "" : SiteAddress;
                t.AppointmentUtc = appointment;
                t.DueDate = due;
                t.AssetMake = AssetMake ?? ""; t.AssetModel = AssetModel ?? "";
                t.AssetSerial = AssetSerial ?? ""; t.Accessories = Accessories ?? "";
                t.DevicePasswordProtected = DevicePasswordProtected;
                t.DevicePasswordEnc = DevicePasswordProtected ? _clients.Protect(DevicePassword ?? "") : "";
            });

        // The photographs follow the ticket that was just created, in the same breath.
        var attached = await _photos.AttachStagedAsync(TenantId, ticket.Id, StageId, Me);
        if (attached > 0)
            await _tickets.AddNoteAsync(TenantId, ticket.Id, who,
                $"{attached} photo{(attached == 1 ? "" : "s")} of the machine as it arrived.", true, automatic: true);

        await _tickets.AddNoteAsync(TenantId, ticket.Id, who,
            $"{TicketService.CategoryName(category)} ticket opened{(client != null ? $" for {client.Name}" : "")}" +
            (People.Count == 0 ? " (unassigned)" : $", assigned to {Assignees.Say(Assignees.Parse(AssignTo))}") +
            (appointment.HasValue ? $". Appointment {appointment.Value.Local():yyyy-MM-dd HH:mm}" : "") + ".", true, automatic: true);

        return Redirect(PortalAddresses.Ticket(ticket.Number));
    }
}
