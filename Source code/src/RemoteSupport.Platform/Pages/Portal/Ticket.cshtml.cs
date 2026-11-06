using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>One ticket with its full history and the remote time booked to it (for invoicing).</summary>
// Ticket work, but an owner-only login still sees the ticket and comments on it (2026-10-15). "Update" is the classic
// page's comment box; TicketService.UpdateAsync drops its status, priority and people for a login that may not change
// them, so only the comment lands. "Eml" downloads a quote request already sent - reading the record.
[TicketWork("Get", "Comment", "Update", "Eml")]
public sealed class TicketModel : PortalPage
{
    private readonly IDbContextFactory<PlatformDb> _dbf;
    private readonly ClientService _clients;
    private readonly TicketService _tickets;
    private readonly TicketPhotoService _photos;
    private readonly QuoteService _quotes;
    private readonly TicketIntake _intake;
    private readonly KnowledgeBaseService _kb;
    private readonly QuoteSignatureService _sigs;
    private readonly RecurringTaskService _recurring;
    private readonly ClientUpdateService _updates;
    private readonly TicketAttachmentService _files;
    public TicketModel(TenantService t, IDbContextFactory<PlatformDb> dbf, ClientService clients,
        TicketService tickets, TicketPhotoService photos, QuoteService quotes, TicketIntake intake, KnowledgeBaseService kb,
        QuoteSignatureService sigs, RecurringTaskService recurring, ClientUpdateService updates, TicketAttachmentService files) : base(t)
    { _dbf = dbf; _clients = clients; _tickets = tickets; _photos = photos; _quotes = quotes; _intake = intake; _kb = kb; _sigs = sigs; _recurring = recurring; _updates = updates; _files = files; }

    // ---- who sent it (3.8.4): a ticket opened by email or the website form ----

    /// <summary>
    /// The sender checked against the client list, asked afresh every time the page is drawn — so a
    /// contact added since links the ticket here without anybody redoing anything. Null when the
    /// ticket did not come from an address, or is already linked.
    /// </summary>
    public TicketIntake.SenderMatch? Sender { get; private set; }

    /// <summary>
    /// Clint's answer 3: the client is known but the person is not — add them as a contact there, and
    /// the ticket is linked to both. The ticket's own sender only; the client must be one of those
    /// the match suggested or any other on file (the office may know better).
    /// </summary>
    public async Task<IActionResult> OnPostAddSenderAsync(int id, int clientId, string? contactName)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var why = await _intake.AddSenderAsContactAsync(TenantId, id, clientId, contactName, Me.Length > 0 ? Me : "Office");
        if (why.Length > 0) { Error = why; return await LoadTicketAsync(id); }
        return Redirect($"{Here}");
    }

    /// <summary>
    /// The knowledge base, as this ticket shows it (3.8.5): its client's articles, the ones written up
    /// from it, and whether "Add to knowledge base" is offered to the person looking. Drawn by
    /// _Knowledge in BOTH layouts, as everything shown on a ticket must be (§61).
    /// </summary>
    public KnowledgeBaseService.TicketKnowledge? Knowledge { get; private set; }

    /// <summary>The recurring task this ticket came from (2026-10-07), or null. Drawn by _Recurring in both layouts.</summary>
    public RemoteSupport.Shared.RecurringInfo? Recurring { get; private set; }

    // ---- workshop photos ----
    public List<TicketPhoto> Before { get; private set; } = new();
    public List<TicketPhoto> After { get; private set; } = new();
    /// <summary>Photos in of each machine — none when this job's Job types did not ask for them (2026-10-27).</summary>
    public int PhotosRequired => JobTypes.PhotosInRequired(Ticket?.RouteOff) ? TicketPhotoService.Required : 0;
    /// <summary>Photos out of each machine, likewise.</summary>
    public int PhotosOutEach => JobTypes.PhotosOutRequired(Ticket?.RouteOff) ? TicketPhotoService.Required : 0;
    /// <summary>Photos out before the machines leave: three of each machine on the job (2026-10-02).</summary>
    public int PhotosOutRequired => PhotosOutEach * (1 + Machines.Count);
    /// <summary>The further machines on THIS job (2026-10-02) — machine 1 is the ticket's own details.</summary>
    public List<TicketMachine> Machines { get; private set; } = new();
    /// <summary>Their passwords, decrypted for the page as the ticket's own is, by machine id.</summary>
    public Dictionary<int, string> MachinePasswords { get; private set; } = new();
    /// <summary>Photos in, by which machine they are of: null is the ticket's own.</summary>
    public List<TicketPhoto> BeforeOf(int? machineId) => Before.Where(p => p.MachineId == machineId).ToList();
    public bool IsWorkshop => Ticket?.Category == TicketCategory.Workshop;
    /// <summary>Whether this job has the after photos it needs before anybody can close it.</summary>
    public bool ReadyToClose => !IsWorkshop || After.Count >= PhotosOutEach;

    // ---- one job, many sessions ----
    /// <summary>The job this ticket is part of, if any.</summary>
    public Ticket? Parent { get; private set; }
    /// <summary>The sessions filed under this one.</summary>
    public List<Ticket> Children { get; private set; } = new();
    /// <summary>Recent tickets on the same machine, which is almost always what a link is to.</summary>
    public List<Ticket> LinkCandidates { get; private set; } = new();
    /// <summary>Minutes booked to this ticket and everything under it — the job's real time.</summary>
    public int JobMinutes { get; private set; }
    /// <summary>A child cannot itself be a parent, so it is never offered the choice.</summary>
    public bool MayHaveParent => Children.Count == 0;

    /// <summary>The parts of this job still open, by number.</summary>
    public List<Ticket> OpenChildren => Children.Where(c => c.Status is not (TicketStatus.Closed or TicketStatus.Resolved)).OrderBy(c => c.Number).ToList();

    /// <summary>
    /// What pressing a step that finishes this job is told while parts of it are open — shown as a pop-up before the form
    /// is sent (Clint, 2026-10-09), in the server's own words (TicketService.CheckChildrenClosedAsync), which refuses it anyway.
    /// Null when nothing is open.
    /// </summary>
    public string? OpenPartsRefusal => RemoteSupport.Shared.JobParts.Refusal(OpenChildren.Select(c => (c.Number, c.Subject)));

    /// <summary>The pop-up for this step, or null when it may be sent.</summary>
    public string? RefusalFor(string actionKey) => Ticket != null && TicketService.ClosesJob(actionKey, Ticket) ? OpenPartsRefusal : null;

    public async Task<IActionResult> OnPostLinkAsync(int id, int parentId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var error = await _tickets.SetParentAsync(TenantId, id, parentId > 0 ? parentId : null, Me);
        if (error.Length > 0) Error = error;
        else Message = parentId > 0 ? "Filed under that job." : "No longer part of another job.";
        return await LoadTicketAsync(id);
    }

    // ---- the workflow (stage 3.7). Everything drawn from TicketService.ActionsFor — this page
    // never decides for itself which stage comes next or who may press what. ----

    public bool HasWorkflow => Ticket is { Stage.Length: > 0 };
    public StageDef? Stage => Ticket == null ? null : Workflow.Stage(Ticket.Stage);
    public List<AvailableAction> Actions { get; private set; } = new();
    public List<TicketPhoto> PartPhotos { get; private set; } = new();
    /// <summary>Pictures of the customer's screen that came with their support requests (2026-10-09).</summary>
    public List<TicketPhoto> Screenshots { get; private set; } = new();
    public List<QuoteLine> Lines { get; private set; } = new();
    public bool MayEditLines { get; private set; }
    public List<TicketNote> WorkDone => Notes.Where(n => n.IsWorkDone).ToList();
    public bool CanAddWorkDone => HasWorkflow && Permissions.CanAddWorkDone(MyRole) && Stage?.Lane == WorkLane.Bench;
    /// <summary>Everybody who can be @mentioned, for the list that pops up on @ in the comment box.</summary>
    public List<string> People { get; private set; } = new();
    /// <summary>Those of them on this ticket, offered first (<see cref="TicketService.OnTicket"/>).</summary>
    public List<string> OnTicket { get; private set; } = new();

    /// <summary>What <c>wwwroot/mentionpick.js</c> reads off a comment box: who comes first, everybody, and who is typing.</summary>
    public string MentionData => System.Text.Json.JsonSerializer.Serialize(new { first = OnTicket, all = People, me = Me });
    /// <summary>Questions on THIS ticket waiting for the person looking at it.</summary>
    public List<TicketMention> AskedMe { get; private set; } = new();
    public string MyMailClient { get; private set; } = "";
    /// <summary>Set after "Did you mean": the item they typed and the look-alikes already on the list.</summary>
    public List<CatalogItem> Similar { get; private set; } = new();
    public string PendingItem { get; private set; } = "";
    public string PendingPart { get; private set; } = "";
    public string PendingSerial { get; private set; } = "";
    public int PendingQty { get; private set; } = 1;
    /// <summary>Photos already uploaded for the item they were adding, carried through "Did you mean".</summary>
    public List<long> PendingPhotos { get; private set; } = new();
    /// <summary>Straight after Request quote: draw the download, or the web mail button.</summary>
    public bool QuoteJustRequested { get; private set; }
    public string ComposeUrl { get; private set; } = "";

    // ---- quote signatures (2026-10-06) ----
    /// <summary>The company's signatures, for Request quote's "Which signature?".</summary>
    public List<QuoteSignature> Signatures { get; private set; } = new();
    /// <summary>The one ticked: what was posted when the form was refused, else the one this person used last.</summary>
    public int SignatureTicked { get; private set; }
    /// <summary>Straight after Request quote to web mail: the signature, drawn, to copy and paste in.</summary>
    public QuoteSignature? PasteSignature { get; private set; }
    private int? _postedSignature;

    // ---- the machine and the suppliers (2026-09-29) ----
    public QuoteMachine Machine => Ticket == null ? new("", "", "", "") : QuoteService.MachineOf(Ticket);
    public List<Supplier> Suppliers { get; private set; } = new();
    public bool CanEditSuppliers => Permissions.CanEditSuppliers(MyRole);
    /// <summary>Set after "did you mean": the supplier typed, and the look-alikes already on the list.</summary>
    public List<Supplier> SimilarSuppliers { get; private set; } = new();
    public Supplier PendingSupplier { get; private set; } = new();
    private int _justAddedSupplier;
    private List<int>? _postedSuppliers;

    /// <summary>
    /// Ticked when the form comes back: what was ticked when it was refused, the supplier just added,
    /// or — asking again after a rejected quote — who was asked last time.
    /// </summary>
    public bool SupplierTicked(Supplier s) =>
        _postedSuppliers?.Contains(s.Id) ?? (s.Id == _justAddedSupplier
            || QuoteService.Addresses(Ticket?.QuoteSupplierEmail).Contains(s.Email, StringComparer.OrdinalIgnoreCase));

    // ---- the timeline page (2026-09-27) ----

    /// <summary>Activity, Photos, Quote or Details — kept across a post so the page comes back where it was.</summary>
    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "activity";
    /// <summary>The step whose form is open at the top of the timeline, kept across a post (adding a quote item reloads).</summary>
    [BindProperty(SupportsGet = true)] public string Open { get; set; } = "";

    /// <summary>The stage the step into this one came from — the middle of the crumb.</summary>
    public string PreviousStageLabel =>
        Ticket == null ? "" : Notes.Where(n => n.MovedTo == Ticket.Stage && !n.WentBack && n.UndoneUtc == null).OrderByDescending(n => n.Id)
            .Select(n => Workflow.Label(n.Stage)).FirstOrDefault() ?? "";

    /// <summary>The comment box that is always there, at every stage — and, ticked, emailed to the client (2026-10-09).</summary>
    public async Task<IActionResult> OnPostCommentAsync(int id, string? note, bool emailClient, string? emailTo, string? emailOther, List<long>? attach)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        // Files alone are a comment too (2026-10-13): progress photos need no caption.
        if (string.IsNullOrWhiteSpace(note) && (attach == null || attach.Count == 0)) return Redirect($"{Here}");
        var to = AddressChosen(emailTo, emailOther);
        // A box ticked with an address that cannot be used, or files too large to email: nothing is posted, and their
        // words and files come back.
        if (emailClient && await _updates.CheckWithFilesAsync(TenantId, id, to, MyRole, attach, Me.Length > 0 ? Me : "Office") is { Length: > 0 } refused)
        {
            Error = refused + " Your comment has not been posted.";
            PostedNote = note ?? ""; PostedTo = to; PostedEmail = true;
            await KeepPickedAsync(id, attach, "comment");
            return await LoadTicketAsync(id);
        }
        var written = await _tickets.AddNoteAsync(TenantId, id, Me.Length > 0 ? Me : "Office", note ?? "", false, attachments: attach);
        if (emailClient && written != null)
        {
            await _updates.SendAsync(TenantId, id, written.Id, to, Me.Length > 0 ? Me : "Office", MyRole);
            // By a GET, so a refresh cannot post and email it twice; ?sent= says how it went.
            return Redirect($"{Here}?sent={await PlaceOfNoteAsync(id, written.Id)}");
        }
        return Redirect($"{Here}");
    }

    /// <summary>"Email to the client" on a comment already posted (2026-10-09).</summary>
    public async Task<IActionResult> OnPostEmailNoteAsync(int id, long noteId, string? emailTo, string? emailOther)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var to = AddressChosen(emailTo, emailOther);
        if (await _updates.CheckAsync(TenantId, to, MyRole) is { Length: > 0 } refused) { Error = refused; return await LoadTicketAsync(id); }
        var (ok, words) = await _updates.SendAsync(TenantId, id, noteId, to, Me.Length > 0 ? Me : "Office", MyRole);
        // Refused before it was tried (not a comment, not this ticket's): no row says so, so the page does.
        if (!ok && !(await _updates.ForTicketAsync(TenantId, id)).ContainsKey(noteId)) { Error = words; return await LoadTicketAsync(id); }
        return Redirect($"{Here}?sent={await PlaceOfNoteAsync(id, noteId)}");
    }

    // ---- files and photos on comments (2026-10-13) ----

    /// <summary>Every file on this ticket that belongs to a comment or step, by its note.</summary>
    public Dictionary<long, List<TicketAttachment>> Files { get; private set; } = new();
    /// <summary>Files picked for a form that was refused — drawn back into that form (<see cref="PickedFor"/>), not lost.</summary>
    public List<TicketAttachment> PickedFiles { get; private set; } = new();
    /// <summary>Which form <see cref="PickedFiles"/> belong to: "comment", or a step's key.</summary>
    public string PickedFor { get; private set; } = "";

    private async Task KeepPickedAsync(int id, List<long>? attach, string form)
    {
        PickedFiles = await _files.LooseAsync(TenantId, id, attach, Me.Length > 0 ? Me : "Office");
        PickedFor = form;
    }

    /// <summary>What was typed into a step's form that was refused, by field name — drawn back in so it is corrected, not retyped.</summary>
    public Dictionary<string, string> Posted { get; private set; } = new();
    public string PostedValue(string field) => Posted.TryGetValue(field, out var v) ? v : "";

    /// <summary>The address from the chooser: one of the offered, or "other" and what was typed.</summary>
    private static string AddressChosen(string? emailTo, string? emailOther) =>
        (emailTo == "other" || string.IsNullOrWhiteSpace(emailTo) ? emailOther : emailTo)?.Trim() ?? "";

    // ---- emailing the client (2026-10-09) ----

    /// <summary>Whether comments may be emailed to the client, from what address, and to whom by default.</summary>
    public ClientUpdateService.Offer ClientOffer { get; private set; } = new(false, "", "", new());
    /// <summary>Every update sent from this ticket, by the comment it sent.</summary>
    public Dictionary<long, List<TicketClientUpdate>> Emailed { get; private set; } = new();
    /// <summary>After a send, the comment it was for — its result is shown at the top. A GET, so nothing is sent twice.
    /// Its PLACE in the story (1 is the first entry), not its id: the address bar names nothing by id (HANDOVER §159).</summary>
    [BindProperty(SupportsGet = true)] public long Sent { get; set; }

    private async Task<int> PlaceOfNoteAsync(int id, long noteId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var n = await db.TicketNotes.Where(x => x.TenantId == TenantId && x.TicketId == id && x.Id == noteId)
            .Select(x => new { x.CreatedUtc }).FirstOrDefaultAsync();
        if (n == null) return 0;
        return await db.TicketNotes.CountAsync(x => x.TenantId == TenantId && x.TicketId == id
            && (x.CreatedUtc < n.CreatedUtc || (x.CreatedUtc == n.CreatedUtc && x.Id <= noteId)));
    }

    // ---- the address (2026-10-23, HANDOVER §159) ----

    /// <summary>This ticket's address: /portal/t/{its number}.</summary>
    public string Here => PortalAddresses.Ticket(AddressNumber);

    /// <summary>
    /// Every handler is given the ticket's id from the NUMBER in the address, looked up within this company — whatever a
    /// form posted as "id" is never read. A number that is not this company's is id 0, which every handler already
    /// answers as "no such ticket". The old /portal/tickets/{id} is sent on to the new address (PortalPage).
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        if (await AddressedIdAsync(ctx, PortalAddresses.TicketIdAsync) is not int id) return;
        ctx.HandlerArguments["id"] = id;
        await base.OnPageHandlerExecutionAsync(ctx, next);
    }
    /// <summary>A comment refused because its address could not be used, drawn back into the box with the box ticked.</summary>
    public string PostedNote { get; private set; } = "";
    public string PostedTo { get; private set; } = "";
    public bool PostedEmail { get; private set; }

    /// <summary>Who the job is with, in words, for the stage banner.</summary>
    public string WithWhom => Stage?.Lane switch
    {
        WorkLane.Office => "In the billing queue",
        WorkLane.Supervisor => "Waiting for a supervisor",
        WorkLane.Done => "",
        _ => string.IsNullOrEmpty(Ticket?.AssignedTo) ? "Nobody yet" : $"With {Assignees.Say(Assignees.Parse(Ticket!.AssignedTo))}"
    };

    private async Task LoadWorkflowAsync(PlatformDb db, int id)
    {
        People = await db.Technicians.Where(t => t.TenantId == TenantId && !t.Disabled)
            .OrderBy(t => t.DisplayName).Select(t => t.DisplayName).ToListAsync();
        OnTicket = TicketService.OnTicket(Ticket?.AssignedTo, Ticket?.WorkedBy, People);
        AskedMe = await db.TicketMentions.Include(m => m.Note).Where(m => m.TicketId == id && m.Person == Me && m.AnsweredUtc == null).ToListAsync();
        // Opening it is seeing the customer's reply (2026-10-09): cleared now, after this page has read it to say so.
        if (AskedMe.Any(m => m.Note.FromCustomer)) await _tickets.SeenCustomerRepliesAsync(TenantId, id, Me);
        if (!HasWorkflow) return;
        // With "Back to …" when this person may go back over the last step (2026-10-13).
        Actions = TicketService.ActionsFor(Ticket!, MyRole, Me, await _tickets.BackStepAsync(TenantId, Ticket!, MyRole, Me));
        Lines = await _quotes.LinesAsync(TenantId, id);
        MayEditLines = QuoteService.MayEditLines(Ticket!, MyRole, Me);
        MyMailClient = await db.Technicians.Where(t => t.Id == TechnicianId).Select(t => t.MailClient).FirstOrDefaultAsync() ?? "";
        if (Actions.Any(a => a.Key == "quote-requested"))
        {
            Suppliers = await _quotes.SuppliersAsync(TenantId);
            Signatures = await _sigs.ListAsync(TenantId);
            var last = await db.Technicians.Where(t => t.Id == TechnicianId).Select(t => t.LastQuoteSignatureId).FirstOrDefaultAsync();
            SignatureTicked = Signatures.Any(s => s.Id == _postedSignature) ? _postedSignature!.Value : QuoteSignatureService.Preselected(Signatures, last);
        }
    }

    /// <summary>Same scope as reading: a ticket they may not see is a ticket they may not change.</summary>
    private async Task<bool> MayTouchAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == TenantId), MyRole, Me).AnyAsync(t => t.Id == id);
    }

    /// <summary>One stage button. The service checks everything again; this only reads the form.</summary>
    public async Task<IActionResult> OnPostActAsync(int id, string action, string? minutes, string? amount,
        string? reference, string? reason, string? supplierEmail, string? repairable, string? details,
        string? assetMake, string? assetModel, string? assetSerial, string? accessories, string? devicePassword,
        int? parentTicketId, string? photoStage, string? sameJob, string? appointmentDate, string? appointmentTime,
        string? comments, List<long>? attach, bool emailClient, string? emailTo, string? emailOther)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var who = Me.Length > 0 ? Me : "Office";
        // What was typed, kept for a refusal: corrected, never retyped.
        void Keep()
        {
            foreach (var (k, v) in new[] { ("minutes", minutes), ("amount", amount), ("reference", reference), ("reason", reason), ("details", details), ("comments", comments) })
                if (!string.IsNullOrEmpty(v)) Posted[k] = v;
            if (action == Workflow.DamageReport) { PostedEmail = emailClient; PostedTo = AddressChosen(emailTo, emailOther); StepEmailPosted = true; }
        }
        // The damage report, emailed from its step (2026-10-13): an address that cannot be used, or a report too large to
        // email, refuses the step before anything moves.
        var to = AddressChosen(emailTo, emailOther);
        bool emailing = action == Workflow.DamageReport && emailClient;
        if (emailing && await _updates.CheckWithFilesAsync(TenantId, id, to, MyRole, attach, who) is { Length: > 0 } refused)
        {
            Error = refused + " Nothing has been recorded."; Open = action; Keep();
            await KeepPickedAsync(id, attach, action);
            return await LoadTicketAsync(id);
        }
        // Reschedule: typed in the company's own time zone, as New ticket's appointment is.
        DateTime? appointment = null;
        if (DateTime.TryParseExact($"{appointmentDate} {appointmentTime}", "yyyy-MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var typed))
            appointment = CompanyClock.ToUtc(typed);
        var input = new StageInput
        {
            AppointmentUtc = appointment,
            PhotoStageId = photoStage,
            Minutes = TicketService.ParseMinutes(minutes), Amount = TicketService.ParseMoney(amount),
            Reference = reference, Reason = reason, SupplierEmail = supplierEmail, Details = details,
            Repairable = repairable == "yes" ? true : repairable == "no" ? false : null,
            AssetMake = assetMake, AssetModel = assetModel, AssetSerial = assetSerial, Accessories = accessories,
            DevicePasswordEnc = string.IsNullOrEmpty(devicePassword) ? "" : _clients.Protect(devicePassword),
            ParentTicketId = parentTicketId,
            SameJob = sameJob == "yes",
            Comments = comments, AttachmentIds = attach ?? new(), EmailTo = emailing ? to : ""
        };
        var why = await _tickets.ActAsync(TenantId, id, Me, MyRole, action, input);
        // Refused: the form stays open with what was typed lost only if it was the problem. Done: it
        // closes, and the step is in the timeline — the stage changing is the confirmation.
        if (why.Length > 0)
        {
            Error = why; Open = action; Keep();
            await KeepPickedAsync(id, attach, action);
            // And which kind of machine it was: a refusal must never quietly turn "same job" into a separate ticket.
            SameJobChosen = sameJob == "yes";
            // And the date and time typed, so a refusal ("that has passed") is corrected, not retyped.
            RescheduleDate = appointmentDate; RescheduleTime = appointmentTime;
            // Photos already picked for another machine stay staged under the id they went up with.
            if (!string.IsNullOrWhiteSpace(photoStage)) { MachineStageId = photoStage; MachinePhotos = (await _photos.StagedAsync(TenantId, photoStage, Me)).Count; }
            return await LoadTicketAsync(id);
        }
        // The damage report, emailed now it is recorded; how it went comes back by ?sent=, as a comment's does.
        if (emailing)
        {
            var (_, _, sentNote) = await _updates.SendStepAsync(TenantId, id, Workflow.DamageReportLabel, to, who, MyRole);
            if (sentNote > 0) return Redirect($"{Here}?sent={await PlaceOfNoteAsync(id, sentNote)}");
        }
        // Done: back to the ticket by a GET, so a refresh cannot send the same step twice.
        return Redirect($"{Here}");
    }

    /// <summary>A refused damage report: whether its email box was ticked, so it is drawn as it was posted.</summary>
    public bool StepEmailPosted { get; private set; }

    /// <summary>
    /// Which client this ticket is for — at any stage, by anybody who may see it. Chosen by name from
    /// the company's list; an empty name unlinks. A name not on the list is refused rather than
    /// created here: adding a customer is the Clients page's job, and a typo should not become one.
    /// </summary>
    public async Task<IActionResult> OnPostSetClientAsync(int id, string? clientName)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        int? clientId = null;
        var name = (clientName ?? "").Trim();
        if (name.Length > 0)
        {
            await using var db = await _dbf.CreateDbContextAsync();
            clientId = await db.Clients.Where(c => c.TenantId == TenantId && c.Name.ToLower() == name.ToLower())
                .Select(c => (int?)c.Id).FirstOrDefaultAsync();
            if (clientId == null) { Error = $"No client called \"{name}\". Add them on the Clients page first."; Tab = "details"; return await LoadTicketAsync(id); }
        }
        var why = await _tickets.SetClientAsync(TenantId, id, clientId, null, Me);
        if (why.Length > 0) { Error = why; Tab = "details"; return await LoadTicketAsync(id); }
        return Redirect($"{Here}?tab=details");
    }

    public async Task<IActionResult> OnPostWorkDoneAsync(int id, string? body)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var why = await _tickets.AddWorkDoneAsync(TenantId, id, Me, MyRole, body ?? "");
        if (why.Length > 0) Error = why; else Message = "Work done recorded.";
        return await LoadTicketAsync(id);
    }

    /// <summary>
    /// Adds a line to the quote: an item from the list, or a new one typed in. A new name that looks
    /// like something already on the list comes back as "Did you mean" instead of being added.
    /// </summary>
    public async Task<IActionResult> OnPostAddLineAsync(int id, int itemId, string? itemName, string? partNumber, string? serialNumber,
        int quantity, bool confirmNew, List<long>? photoIds, List<IFormFile>? photo)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        // The page's script uploads each photo first, made smaller, one at a time (as workshop photos
        // go, §62) and sends their ids. Without script the files come with the form itself.
        var ids = new List<long>(photoIds ?? new());
        var (added, perr) = await PartPhotosFromFormAsync(id, photo);
        if (perr.Length > 0) { Error = perr; return await LoadTicketAsync(id); }
        ids.AddRange(added);
        if (itemId == 0)
        {
            var (item, err, similar) = await _quotes.AddItemAsync(TenantId, itemName ?? "", Me, confirmNew);
            if (item == null)
            {
                Error = err; Similar = similar;
                PendingItem = itemName ?? ""; PendingPart = partNumber ?? ""; PendingSerial = serialNumber ?? ""; PendingQty = Math.Max(1, quantity);
                PendingPhotos = ids;
                return await LoadTicketAsync(id);
            }
            itemId = item.Id;
        }
        var (line, lerr) = await _quotes.AddLineAsync(TenantId, id, itemId, partNumber, Math.Max(1, quantity), ids, Me, MyRole, serialNumber);
        if (line == null) Error = lerr;
        else Message = $"Added {line.ItemName}" + (line.PhotoIds.Count switch { 0 => ".", 1 => ", with its photo.", var n => $", with {n} photos." });
        return await LoadTicketAsync(id);
    }

    /// <summary>More photos for an item already on the quote.</summary>
    public async Task<IActionResult> OnPostAddLinePhotosAsync(int id, long lineId, List<long>? photoIds, List<IFormFile>? photo)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var ids = new List<long>(photoIds ?? new());
        var (added, perr) = await PartPhotosFromFormAsync(id, photo);
        if (perr.Length > 0) { Error = perr; return await LoadTicketAsync(id); }
        ids.AddRange(added);
        // The line must be on THIS ticket — the service checks the ticket the line names.
        var line = (await _quotes.LinesAsync(TenantId, id)).FirstOrDefault(l => l.Id == lineId);
        var why = line == null ? "That item is not on the quote any more." : await _quotes.AddLinePhotosAsync(TenantId, lineId, ids, Me, MyRole);
        if (why.Length > 0) Error = why; else Message = ids.Count == 1 ? $"Photo added to {line!.ItemName}." : $"{ids.Count} photos added to {line!.ItemName}.";
        return await LoadTicketAsync(id);
    }

    /// <summary>Photos that came with the form itself, for a browser that did not run the page's script.</summary>
    private async Task<(List<long> ids, string error)> PartPhotosFromFormAsync(int ticketId, List<IFormFile>? files)
    {
        var ids = new List<long>();
        foreach (var f in (files ?? new()).Where(f => f is { Length: > 0 }))
        {
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            var (p, perr) = await _photos.AddAsync(TenantId, ms.ToArray(), f.FileName, PhotoKind.Part, Me, ticketId, "");
            if (p == null) return (ids, perr);
            ids.Add(p.Id);
        }
        return (ids, "");
    }

    public async Task<IActionResult> OnPostRemoveLineAsync(int id, long lineId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var why = await _quotes.RemoveLineAsync(TenantId, lineId, Me, MyRole);
        if (why.Length > 0) Error = why;
        return await LoadTicketAsync(id);
    }

    /// <summary>Which machine the quote is for — or, all empty, that it is not for one.</summary>
    public async Task<IActionResult> OnPostSetMachineAsync(int id, string? machineType, string? machineMake, string? machineModel, string? machineSerial)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var why = await _quotes.SetMachineAsync(TenantId, id, machineType, machineMake, machineModel, machineSerial, Me, MyRole);
        if (why.Length > 0) Error = why; else Message = "Machine saved.";
        return await LoadTicketAsync(id);
    }

    /// <summary>
    /// Adds a supplier from inside Request quote, and comes back to it with them ticked — somebody
    /// quoting from a new supplier should not have to leave the ticket to do it.
    /// </summary>
    public async Task<IActionResult> OnPostAddSupplierAsync(int id, string? supplierName, string? supplierEmail, string? contactPerson, string? phone, bool confirmNew)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var (s, err, similar) = await _quotes.AddSupplierAsync(TenantId, supplierName, supplierEmail, contactPerson, phone, Me, MyRole, confirmNew);
        Open = "quote-requested";
        if (s == null)
        {
            SimilarSuppliers = similar;
            if (similar.Count == 0) Error = err;
            PendingSupplier = new Supplier { Name = supplierName ?? "", Email = supplierEmail ?? "", ContactPerson = contactPerson ?? "", Phone = phone ?? "" };
        }
        else { _justAddedSupplier = s.Id; Message = $"{s.Name} added to your suppliers."; }
        return await LoadTicketAsync(id);
    }

    /// <summary>
    /// Request quote: remembers which mail this person uses (asked once), moves the job to Quote
    /// requested, and then hands them the email — a draft file for desktop mail, or their web mail's
    /// compose window with the photos to attach beside it. The suppliers are the ones ticked.
    /// </summary>
    public async Task<IActionResult> OnPostRequestQuoteAsync(int id, List<int>? supplierIds, string? mail, int? signatureId)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        if (MailClients.Valid(mail))
        {
            await using var db = await _dbf.CreateDbContextAsync();
            var me = await db.Technicians.FirstOrDefaultAsync(t => t.Id == TechnicianId && t.TenantId == TenantId);
            if (me != null) { me.MailClient = mail!; await db.SaveChangesAsync(); }
        }
        var ids = supplierIds ?? new();
        // Which signature, BEFORE the job moves: with several, a request is never signed by a guess.
        var (sig, sigWhy) = await _sigs.ChooseAsync(TenantId, signatureId);
        if (sigWhy.Length > 0) { Error = sigWhy; Open = "quote-requested"; _postedSuppliers = ids; _postedSignature = signatureId; return await LoadTicketAsync(id); }
        var chosen = (await _quotes.SuppliersAsync(TenantId)).Where(s => ids.Contains(s.Id)).ToList();
        var why = await _tickets.ActAsync(TenantId, id, Me, MyRole, "quote-requested", new StageInput
        {
            SupplierEmail = string.Join(", ", chosen.Select(s => s.Email)),
            SupplierNames = string.Join(", ", chosen.Select(s => s.Name))
        });
        if (why.Length > 0) { Error = why; Open = "quote-requested"; _postedSuppliers = ids; _postedSignature = signatureId; return await LoadTicketAsync(id); }
        await _sigs.RememberAsync(TenantId, id, TechnicianId, sig?.Id);
        var result = await LoadTicketAsync(id);
        QuoteJustRequested = true;
        if (MyMailClient is MailClients.Gmail or MailClients.OutlookWeb)
        {
            // Web mail takes plain words through a link, nothing more. With a signature the words stop
            // after "Our reference" and the page copies the signature, drawn, to paste in below.
            var (subject, body) = QuoteService.QuoteRequestText(Tenant, Ticket!, Lines, Me);
            if (sig != null) { body = QuoteService.QuoteRequestWords(Tenant, Ticket!, Lines).words; PasteSignature = sig; }
            var (to, bcc) = QuoteService.Addressing(Tenant, Ticket!.QuoteSupplierEmail);
            ComposeUrl = QuoteService.ComposeUrl(MyMailClient, to, subject, body, bcc);
        }
        Message = "Quote requested.";
        return result;
    }

    /// <summary>
    /// The request as a draft email file. Available once the quote has been requested, so it can be
    /// downloaded again — somebody who closed the draft by mistake should not have to re-quote.
    /// </summary>
    public async Task<IActionResult> OnGetEmlAsync(int id)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!await MayTouchAsync(id)) return NotFound();
        var t = await _tickets.GetAsync(TenantId, id);
        if (t == null || t.QuoteSupplierEmail.Length == 0) return NotFound();
        var lines = await _quotes.LinesAsync(TenantId, id);
        // Signed as it was requested — the signature is kept on the ticket, removed from the list or not.
        var sig = await _sigs.GetAsync(TenantId, t.QuoteSignatureId);
        var (subject, body) = QuoteService.QuoteRequestText(Tenant, t, lines, Me, sig);
        var html = sig == null ? null : QuoteService.QuoteRequestHtml(Tenant, t, lines, sig);
        (string, byte[])? inline = sig != null && QuoteSignatures.HasImage(sig) ? (sig.ImageType, sig.Image!) : null;
        var files = new List<(string, string, byte[])>();
        // Named by the item's number in the email, so "item-2-photo-3" is plainly the second item's.
        for (int i = 0; i < lines.Count; i++)
            for (int k = 0; k < lines[i].PhotoIds.Count; k++)
            {
                var p = await _photos.GetAsync(TenantId, lines[i].PhotoIds[k]);
                if (p == null) continue;
                var path = _photos.FilePath(TenantId, p.FileName);
                if (!System.IO.File.Exists(path)) continue;
                var bytes = await System.IO.File.ReadAllBytesAsync(path);
                var type = TicketPhotoService.SniffType(bytes);
                files.Add((QuoteService.PhotoFileName(i + 1, k + 1, lines[i].PhotoIds.Count, Path.GetExtension(p.FileName)), type, bytes));
            }
        var (to, bcc) = QuoteService.Addressing(Tenant, t.QuoteSupplierEmail);
        return File(QuoteService.QuoteRequestEml(to, subject, body, files, bcc, html, inline), "message/rfc822", $"quote-request-job-{t.Number}.eml");
    }

    public List<string> Technicians { get; private set; } = new();
    public string NameOf(TicketStatus s) => s switch
    { TicketStatus.InProgress => "In progress", TicketStatus.OnHold => "On hold", _ => s.ToString() };

    public Ticket? Ticket { get; private set; }
    public Client? Client { get; private set; }
    /// <summary>What "Link to parent ticket" offers: this client's other open tickets.</summary>
    public List<RemoteSupport.Shared.TicketChoiceInfo> ParentChoices { get; private set; } = new();
    /// <summary>The other machines booked in with this one.</summary>
    public List<RemoteSupport.Shared.TicketChoiceInfo> BookedWith { get; private set; } = new();
    /// <summary>Where Add another machine stages its photos: fresh per page load, like the booking form's.</summary>
    public string MachineStageId { get; private set; } = Guid.NewGuid().ToString("N");
    /// <summary>How many photos are staged under <see cref="MachineStageId"/> — none on a fresh page.</summary>
    public int MachinePhotos { get; private set; }
    /// <summary>A refused Add another machine was "same job" — drawn checked again, so a second Submit
    /// cannot make a separate ticket the person never chose.</summary>
    public bool SameJobChosen { get; private set; }
    /// <summary>A refused Reschedule's date and time, drawn again. Null: the form starts at the
    /// current appointment, so moving it a day is one change.</summary>
    public string? RescheduleDate { get; private set; }
    public string? RescheduleTime { get; private set; }
    public string DevicePassword { get; private set; } = "";
    public List<TicketNote> Notes { get; private set; } = new();
    public List<SessionLog> Sessions { get; private set; } = new();
    /// <summary>Session recordings on this ticket (1.6), watched in the page and deleted only by a supervisor or the owner.</summary>
    public List<SessionRecording> Recordings { get; private set; } = new();
    public bool CanDeleteRecordings => Permissions.CanDeleteRecordings(MyRole);
    public int TotalMinutes => Sessions.Where(s => s.EndedUtc.HasValue)
        .Sum(s => Math.Max(1, (int)(s.EndedUtc!.Value - s.StartedUtc).TotalMinutes));

    public string StatusName => Ticket == null ? "" : Ticket.Status switch
    { TicketStatus.InProgress => "In progress", TicketStatus.OnHold => "On hold", _ => Ticket.Status.ToString() };

    public string StatusPill => Ticket == null ? "pill-muted" : Ticket.Status switch
    {
        TicketStatus.Resolved or TicketStatus.Closed => "pill-ok",
        TicketStatus.OnHold => "pill-warn",
        TicketStatus.InProgress => "pill-muted",
        _ => "pill-warn"
    };

    // ---- what this person may do, asked rather than worked out from their role ----
    public bool CanChangeStatus => Permissions.CanChangeStatus(MyRole);
    public bool CanAssignToAnyone => Permissions.CanAssignToAnyone(MyRole);
    public bool CanClaim => Permissions.CanClaim(MyRole);
    public string WhyNotStatus => Permissions.WhyNotStatus(MyRole);
    /// <summary>True when this is an unassigned ticket the viewer could pick up themselves.</summary>
    /// Only from the bench: a job in the billing queue is assigned to nobody too, and it is not theirs.
    public bool CanClaimThis => CanClaim && !CanAssignToAnyone && Ticket is { AssignedTo.Length: 0, Lane: (int)WorkLane.Bench };

    /// <summary>The tick boxes for who is on this ticket, with what this person may change drawn in.</summary>
    public PeoplePicker OnThisJob => new(Ticket?.AssignedTo ?? "", Technicians, Me, CanAssignToAnyone,
        MayJoin: CanClaim && Ticket?.Lane == (int)WorkLane.Bench,
        MayPassOn: Permissions.CanPassOn(MyRole));

    public async Task<IActionResult> OnPostUpdateAsync(int id, List<string>? people, bool peopleShown, int status, int priority, string? note,
        bool emailClient, string? emailTo, string? emailOther, List<long>? attach = null)
    {
        if (!await LoadAsync()) return Redirect("/login");
        var who = Me.Length > 0 ? Me : "Office";

        // Out of scope is refused before anything is written, so a technician cannot edit a
        // colleague's ticket by posting to its number.
        await using (var guard = await _dbf.CreateDbContextAsync())
            if (!await TicketService.VisibleTo(guard.Tickets.Where(t => t.TenantId == TenantId), MyRole, Me)
                    .AnyAsync(t => t.Id == id))
                return NotFound();

        // "Also email this to the client" with an address that cannot be used: nothing saved, the words kept.
        var to = AddressChosen(emailTo, emailOther);
        bool hasFiles = attach is { Count: > 0 };
        bool emailing = emailClient && (!string.IsNullOrWhiteSpace(note) || hasFiles);
        if (emailing && await _updates.CheckWithFilesAsync(TenantId, id, to, MyRole, attach, who) is { Length: > 0 } refused)
        {
            Error = refused + " Nothing has been saved.";
            PostedNote = note ?? ""; PostedTo = to; PostedEmail = true;
            await KeepPickedAsync(id, attach, "comment");
            return await LoadTicketAsync(id);
        }

        var (_, changes) = await _tickets.UpdateAsync(TenantId, id, who, MyRole,
            (TicketStatus)Math.Clamp(status, 0, 4), (TicketPriority)Math.Clamp(priority, 0, 3),
            // The whole list of people wanted on it, or null when the form did not carry the list.
            peopleShown ? Assignees.Join(people) : null, null);
        var blocked = _tickets.LastBlockedReason;
        var written = string.IsNullOrWhiteSpace(note) && !hasFiles ? null : await _tickets.AddNoteAsync(TenantId, id, who, note ?? "", false, attachments: attach);
        string emailWords = "";
        if (emailing && written != null)
        {
            var (ok, words) = await _updates.SendAsync(TenantId, id, written.Id, to, who, MyRole);
            if (ok) emailWords = " " + words; else Error = words;
        }

        // A workshop job refused for want of after photos says so, and says it loudly. Everything
        // else in the same post still applied, including the note.
        if (blocked.Length > 0) Error = (Error.Length > 0 ? Error + " " : "") + blocked;

        // Says what actually happened rather than "Ticket updated." regardless. Somebody who is
        // not allowed to move a status should not be told they just did.
        Message = changes.Count > 0
            ? "Ticket updated."
            : string.IsNullOrWhiteSpace(note) ? (blocked.Length > 0 ? "" : "Nothing changed.") : "Note added.";
        Message += emailWords;
        return await LoadTicketAsync(id);
    }

    // ---- Edit the ticket (2026-10-13): what was typed, kept when it is refused, so nothing has to be typed again ----
    public string? EditSubject { get; private set; }
    public string? EditDescription { get; private set; }
    public int? EditCategory { get; private set; }
    public string? EditDate { get; private set; }
    public string? EditTime { get; private set; }
    /// <summary>A refused edit's due date as it was typed (§165); null: show the ticket's own.</summary>
    public string? EditDue { get; private set; }

    /// <summary>Subject, description, category and appointment — anybody who can see the ticket (TicketService.EditAsync).</summary>
    /// <param name="appointmentShown">The appointment boxes were on the form: blank then means "no appointment". Not shown,
    /// the appointment is left alone.</param>
    public async Task<IActionResult> OnPostEditAsync(int id, string? subject, string? description, int? category,
        string? appointmentDate, string? appointmentTime, bool appointmentShown, string? dueDate, bool dueShown)
    {
        if (!await LoadAsync()) return Redirect("/login");
        await using (var guard = await _dbf.CreateDbContextAsync())
            if (!await TicketService.VisibleTo(guard.Tickets.Where(t => t.TenantId == TenantId), MyRole, Me).AnyAsync(t => t.Id == id))
                return NotFound();
        Tab = "details";
        // Read as sent: model binding turns an EMPTY box into null, which EditAsync reads as "leave it alone" — so an
        // emptied subject would be answered "Nothing changed" instead of being refused, and a description could never
        // be taken away. A box not on the form at all is still null.
        subject = Request.Form.TryGetValue("subject", out var s) ? s.ToString() : null;
        description = Request.Form.TryGetValue("description", out var dsc) ? dsc.ToString() : null;
        DateTime? at = null;
        string why = "";
        if (appointmentShown && !string.IsNullOrWhiteSpace(appointmentDate))
        {
            // Typed in the company's own time zone, as New ticket's and Reschedule's are.
            var time = string.IsNullOrWhiteSpace(appointmentTime) ? "09:00" : appointmentTime.Trim();
            if (DateTime.TryParseExact($"{appointmentDate.Trim()} {time}", "yyyy-MM-dd HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var typed))
                at = CompanyClock.ToUtc(typed);
            else why = "That appointment date or time could not be read. Choose them again.";
        }
        DateTime? due = null;
        if (why.Length == 0 && dueShown && !TicketDue.TryRead(dueDate, out due)) why = "That due date could not be read. Choose it again.";
        var changes = new List<string>();
        if (why.Length == 0)
            (why, changes) = await _tickets.EditAsync(TenantId, id, Me.Length > 0 ? Me : "Office", MyRole,
                new TicketEdit(subject, description, category, appointmentShown, at, dueShown, due));
        if (why.Length > 0)
        {
            Error = why + " Nothing has been changed.";
            EditSubject = subject; EditDescription = description; EditCategory = category;
            EditDate = appointmentDate ?? ""; EditTime = appointmentTime ?? ""; EditDue = dueShown ? dueDate ?? "" : null;
        }
        else Message = changes.Count > 0 ? "Saved — the change is in the ticket's story." : "Nothing changed.";
        return await LoadTicketAsync(id);
    }

    /// <summary>Takes an unassigned ticket for yourself. The pool is how work reaches a technician.</summary>
    public async Task<IActionResult> OnPostClaimAsync(int id)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanClaim(MyRole)) return Forbid();
        var current = await _tickets.GetAsync(TenantId, id);
        if (current == null) return NotFound();
        var (_, changes) = await _tickets.UpdateAsync(TenantId, id, Me, MyRole, null, null, Assignees.With(current.AssignedTo, Me), null);
        Message = changes.Count > 0 ? "That ticket is yours." : "Somebody else got there first.";
        return await LoadTicketAsync(id);
    }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync()) return Redirect("/login");
        return await LoadTicketAsync(id);
    }

    private async Task<IActionResult> LoadTicketAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        Technicians = await db.Technicians
            .Where(t => t.TenantId == TenantId && !t.Disabled && (t.Role & TenantRoles.Seated) != 0)
            .OrderBy(t => t.DisplayName).Select(t => t.DisplayName).ToListAsync();
        // Scoped, so a technician cannot read a colleague's ticket by typing its number into the
        // address bar. Out of scope looks exactly like "no such ticket", which is the right answer
        // to give: whether a ticket they may not see exists is not their business either.
        Ticket = await TicketService.VisibleTo(db.Tickets.Where(t => t.TenantId == TenantId), MyRole, Me)
            .FirstOrDefaultAsync(t => t.Id == id);
        // "Ticket not found." is still shown, but as a 404 (the pentest's F6): a scan of ticket numbers stands out in
        // the logs, as it does on the Clients pages.
        if (Ticket == null) { Response.StatusCode = StatusCodes.Status404NotFound; return Page(); }
        if (Ticket.ClientId.HasValue) Client = await _clients.GetAsync(TenantId, Ticket.ClientId.Value);
        if (Ticket.ClientId == null && Ticket.RequesterEmail.Length > 0)
        {
            Sender = await _intake.MatchAsync(TenantId, Ticket.RequesterEmail, (await _intake.GetAsync(TenantId)).Address);
            // Somebody has put them on file since (the Clients page, the console, an import): the
            // ticket links itself now, and its story says so — nobody has to come back and do it.
            if (Sender.ClientId is int cid && await _tickets.SetClientAsync(TenantId, id, cid, Sender.ContactName.Length > 0 ? Sender.ContactName : null,
                    "Email", Sender.ContactId) == "")
            {
                Ticket.ClientId = cid; Ticket.ContactId = Sender.ContactId;
                Client = await _clients.GetAsync(TenantId, cid);
                Sender = null;
            }
        }
        if (Ticket.DevicePasswordProtected) DevicePassword = _clients.Unprotect(Ticket.DevicePasswordEnc);
        ParentChoices = await _tickets.ParentChoicesAsync(TenantId, Ticket);
        BookedWith = await _tickets.BookedWithAsync(TenantId, Ticket);
        var photos = await _photos.ForTicketAsync(TenantId, id);
        Before = photos.Where(p => p.Kind == PhotoKind.Before).ToList();
        After = photos.Where(p => p.Kind == PhotoKind.After).ToList();
        PartPhotos = photos.Where(p => p.Kind == PhotoKind.Part).ToList();
        Screenshots = photos.Where(p => p.Kind == PhotoKind.Screenshot).ToList();
        Machines = await _tickets.MachinesAsync(TenantId, id);
        foreach (var m in Machines.Where(m => m.PasswordProtected)) MachinePasswords[m.Id] = _clients.Unprotect(m.PasswordEnc);
        await LoadWorkflowAsync(db, id);
        Notes = await db.TicketNotes.Where(n => n.TenantId == TenantId && n.TicketId == id).OrderBy(n => n.CreatedUtc).ThenBy(n => n.Id).ToListAsync();
        Sessions = await db.Sessions.Where(s => s.TenantId == TenantId && s.TicketId == id).OrderBy(s => s.StartedUtc).ToListAsync();
        Recordings = await db.SessionRecordings.Where(r => r.TenantId == TenantId && r.TicketId == id).OrderBy(r => r.RecordedUtc).ToListAsync();
        Knowledge = await _kb.ForTicketAsync(TenantId, Ticket, MyRole, Me);
        Recurring = await _recurring.InfoAsync(Ticket, MyRole, Me);
        ClientOffer = await _updates.OfferAsync(TenantId, Ticket, MyRole);
        Emailed = await _updates.ForTicketAsync(TenantId, id);
        Files = await _files.ForTicketAsync(TenantId, id);
        // Back from a send: how it went, at the top, in the words the comment's marker uses.
        if (Sent > 0 && Sent <= Notes.Count && Emailed.TryGetValue(Notes[(int)Sent - 1].Id, out var sends) && sends.Count > 0 && Error.Length == 0)
        {
            var last = sends[^1];
            if (last.Sent) Message = $"Emailed to {last.ToAddress}." + (last.Result.StartsWith("sent from our mailbox") ? " It went from our mailbox, because yours said: " + last.Result[(last.Result.IndexOf(':') + 1)..].Trim() : "");
            else Error = "The comment is saved, but it was not emailed. " + ClientUpdateService.Marker(last);
        }

        Children = await _tickets.ChildrenAsync(TenantId, id);
        if (Ticket.ParentTicketId.HasValue)
            Parent = await db.Tickets.FirstOrDefaultAsync(t => t.Id == Ticket.ParentTicketId.Value && t.TenantId == TenantId);

        // The job's time: this ticket's sessions plus every child's. That total is what gets
        // billed, which is the point of letting a three-day job be one thing.
        var ids = Children.Select(c => c.Id).Append(id).ToList();
        var all = await db.Sessions
            .Where(s => s.TenantId == TenantId && s.TicketId != null && ids.Contains(s.TicketId!.Value) && s.EndedUtc != null)
            .ToListAsync();
        JobMinutes = TicketService.TotalMinutes(all);

        // What to offer as a parent: recent work on the same machine, which is what a link is for
        // in practice — yesterday's session on the PC you are on again today. Never itself, never
        // something that is already part of something, and never a ticket with children.
        if (MayHaveParent && !string.IsNullOrWhiteSpace(Ticket.MachineName))
        {
            // ParentTicketId == null is the whole exclusion: a ticket that already has children is
            // a perfectly good parent — it is the one that already IS the job. Only tickets that
            // are themselves somebody's child are kept out, because that would make a tree.
            LinkCandidates = await db.Tickets
                .Where(t => t.TenantId == TenantId && t.Id != id
                         && t.MachineName == Ticket.MachineName && t.ParentTicketId == null)
                .OrderByDescending(t => t.CreatedUtc).Take(15).ToListAsync();
        }
        return Page();
    }
}
