using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// What one step changed on the ticket, written down just BEFORE it (2026-10-13) — so going back puts every one of
/// these back as it was. Only the route's own fields: the stage and whose desk, and what the steps record (the
/// decision, the time, the amounts, the invoice and payment, the quote's suppliers, collection). Never the client,
/// the machine or the words: going back undoes a STEP, not the ticket.
/// </summary>
public sealed record StepSnapshot(
    string Stage, int Lane, TicketStatus Status, string StageReturn, bool? Repairable, string Outcome, string WorkedBy,
    int? TimeSpentMinutes, decimal? InvoiceAmount, string InvoiceNumber, string PaymentMethod, string PaymentReference,
    decimal? QuoteAmount, string QuoteReference, string QuoteSupplierEmail, string QuoteSupplierNames,
    string DoNotInvoiceReason, string AbandonedReason, DateTime? CollectedUtc, string CollectedBy, bool Delivered,
    string AssignedTo, DateTime? ClosedUtc)
{
    public static StepSnapshot Of(Ticket t) => new(
        t.Stage, t.Lane, t.Status, t.StageReturn, t.Repairable, t.Outcome, t.WorkedBy,
        t.TimeSpentMinutes, t.InvoiceAmount, t.InvoiceNumber, t.PaymentMethod, t.PaymentReference,
        t.QuoteAmount, t.QuoteReference, t.QuoteSupplierEmail, t.QuoteSupplierNames,
        t.DoNotInvoiceReason, t.AbandonedReason, t.CollectedUtc, t.CollectedBy, t.Delivered,
        t.AssignedTo, t.ClosedUtc);

    public string Json() => JsonSerializer.Serialize(this);

    public static StepSnapshot? Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<StepSnapshot>(json); } catch { return null; }
    }
}

/// <summary>The step "Back" would undo: the entry that moved the job here, where it goes back to, and what to warn of.</summary>
public sealed record BackStep(TicketNote Step, string ToStage, string ToLabel, string Warning);

/// <summary>
/// Going back a stage (Clint, 2026-10-13: "we should have an option to go back at every stage. Something may have
/// changed or a person clicked the next stage by mistake"). His answers to the questions, all as recommended:
///
/// <list type="bullet">
/// <item>ONE STEP AT A TIME: "Back to {where it was}" undoes the step that brought the job here; pressed again, the one
/// before that.</item>
/// <item>A REASON is required.</item>
/// <item>NOTHING IS DELETED: the undone step stays in the story, marked undone, and a "Back to …" entry says who and
/// why. What the step recorded stops counting — every route field goes back to what it was (<see cref="StepSnapshot"/>),
/// so an amount to invoice is no longer invoiced.</item>
/// <item>WHO: the people on the job, whoever pressed the step, supervisors and the owner — and the office, on its own
/// stages. A CLOSED job is reopened only by a supervisor or the owner.</item>
/// <item>WARNED, NOT REFUSED: an email already sent, an invoice issued, a payment taken, a machine gone — going back is
/// allowed, and the form says so first.</item>
/// <item>NEVER across a change of category: a call out or walk in that became a workshop job stays one.</item>
/// </list>
///
/// It is a step like any other — key <see cref="GoBackKey"/>, offered by <see cref="ActionsFor"/> when the caller
/// passes it the <see cref="BackStep"/>, pressed through <see cref="ActAsync"/> — so the portal and the console (old
/// consoles included) draw and send it with no code of their own.
/// </summary>
public sealed partial class TicketService
{
    public const string GoBackKey = "go-back";

    /// <summary>
    /// The step Back would undo on this ticket, if this person may press it — null when there is none, or not theirs.
    /// The last step that MOVED the job and has not been gone back over, and only when it is the one that brought the
    /// job to where it is now (anything else would be a jump, not a step back).
    /// </summary>
    public async Task<BackStep?> BackStepAsync(int tenantId, Ticket t, TenantRole role, string me)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var (step, _) = await LastStepAsync(db, tenantId, t);
        if (step == null || !MayGoBack(t, step, role, me)) return null;
        return new BackStep(step, step.Stage, Workflow.Label(step.Stage), await WarningAsync(db, tenantId, t, step));
    }

    /// <summary>The step to undo, or why there is none (in words, for a press that is refused).</summary>
    private static async Task<(TicketNote? step, string why)> LastStepAsync(PlatformDb db, int tenantId, Ticket t)
    {
        if (!Workflow.Has(t.Category) || t.Stage.Length == 0) return (null, "This ticket has no workflow.");
        // Waiting on customer has its own way back: "Back to work". Two buttons doing one thing would be one too many.
        if (t.Stage == "waiting-customer") return (null, "Use \"Back to work\" to take it off hold.");
        var step = await db.TicketNotes
            .Where(n => n.TenantId == tenantId && n.TicketId == t.Id && n.MovedTo != "" && n.UndoneUtc == null && !n.WentBack)
            .OrderByDescending(n => n.Id).FirstOrDefaultAsync();
        if (step == null || step.MovedTo != t.Stage || step.Stage.Length == 0 || Workflow.Stage(step.Stage) == null)
            return (null, "There is no step to go back over — this is where the job started.");
        if (Workflow.ToWorkshopKeys.Select(k => Workflow.Action(k)?.Label).Contains(step.FormTitle))
            return (null, "This job became a workshop job at this step, and it cannot go back to being a call out or a walk in. Book it out again as a new ticket if it must.");
        // Nor across a category changed by hand since (2026-10-13): the step belongs to the old route. Its entry moves
        // nothing, so this is usually caught above — asked anyway, so a route that shares a stage key cannot slip past.
        if (await db.TicketNotes.AnyAsync(n => n.TenantId == tenantId && n.TicketId == t.Id && n.Id > step.Id && n.FormTitle == CategoryChangedTitle))
            return (null, "The category was changed after that step, so there is no step to go back over. Change the category back if it was wrong.");
        return (step, "");
    }

    /// <summary>Who may go back: see the class. Asked again when it is pressed.</summary>
    public static bool MayGoBack(Ticket t, TicketNote step, TenantRole role, string me)
    {
        if (!Permissions.CanWorkTickets(role)) return false;
        if (Permissions.CanStepInAnywhere(role)) return true;
        var here = Workflow.Stage(t.Stage);
        if (here == null || here.Lane == WorkLane.Done) return false;   // reopening a closed job: supervisor or owner
        if (me.Length > 0 && step.Author == me) return true;
        if (Permissions.CanChangeStatus(role) && me.Length > 0 && (Assignees.Has(t.AssignedTo, me) || Assignees.Has(t.WorkedBy, me))) return true;
        var to = Workflow.Stage(step.Stage);
        return Permissions.CanWorkBilling(role) && (here.Lane == WorkLane.Office || to?.Lane == WorkLane.Office);
    }

    /// <summary>Whether this step is the machine leaving: the only step whose undoing puts a machine back on the shelf.</summary>
    private static bool UndoesHandover(TicketNote step) => step.FormTitle == Workflow.Action("delivered")?.Label;

    /// <summary>What going back cannot take back, in words for the form — empty when there is nothing to say.</summary>
    private static async Task<string> WarningAsync(PlatformDb db, int tenantId, Ticket t, TicketNote step)
    {
        var said = new List<string>();
        var was = StepSnapshot.Read(step.Before);
        if (Workflow.Stage(t.Stage)?.Lane == WorkLane.Done) said.Add("This reopens a closed job.");
        if (was == null)
            said.Add("This step was recorded before going back kept what each step changed, so only the stage goes back — any time, amount or invoice it recorded stays as it is on the ticket.");
        else
        {
            if (t.InvoiceNumber.Length > 0 && was.InvoiceNumber != t.InvoiceNumber)
                said.Add($"Invoice {t.InvoiceNumber} was recorded as issued. Going back clears it here; cancel or credit it in your accounting package if it has gone out.");
            if (t.PaymentMethod.Length > 0 && was.PaymentMethod != t.PaymentMethod)
                said.Add($"A payment{(t.InvoiceAmount.HasValue ? $" of {Money(t.InvoiceAmount.Value)}" : "")} was recorded. Going back clears it here; refund it if it should not have been taken.");
            if (t.CollectedUtc != null && was.CollectedUtc == null && UndoesHandover(step))
                said.Add(t.Delivered ? "The machine was recorded as delivered. Going back records it as not delivered yet." : "The machine was recorded as collected. Going back records it as still here.");
            if (t.QuoteSupplierEmail.Length > 0 && was.QuoteSupplierEmail != t.QuoteSupplierEmail)
                say(said, $"The quote request has already been sent to {(t.QuoteSupplierNames.Length > 0 ? t.QuoteSupplierNames : t.QuoteSupplierEmail)} — let them know if it no longer stands.");
            if (t.QuoteAmount.HasValue && was.QuoteAmount != t.QuoteAmount)
                said.Add("The quote has already been sent to the client — let them know if it no longer stands.");
        }
        var emailed = await db.TicketClientUpdates.Where(u => u.TenantId == tenantId && u.NoteId == step.Id && u.Sent)
            .Select(u => u.ToAddress).Distinct().ToListAsync();
        if (emailed.Count > 0)
            said.Add($"It was emailed to {string.Join(", ", emailed)} already, and they will not be told it was taken back.");
        return string.Join(" ", said);

        static void say(List<string> l, string s) { if (!l.Contains(s)) l.Add(s); }
    }

    /// <summary>
    /// Goes back over the last step. Empty means done. Everything the step changed goes back as it was (see the class);
    /// the step is marked undone, and a "Back to …" entry with the reason is the newest in the story.
    /// </summary>
    private async Task<string> GoBackAsync(PlatformDb db, Ticket t, string by, TenantRole role, string? reason)
    {
        var (step, why) = await LastStepAsync(db, t.TenantId, t);
        if (step == null) return why;
        if (!MayGoBack(t, step, role, by))
            return Workflow.Stage(t.Stage)?.Lane == WorkLane.Done
                ? "Only a supervisor or the owner can reopen a closed job."
                : "Going back is for the people on the job, whoever made the step, the office on its own stages, or a supervisor.";
        if (string.IsNullOrWhiteSpace(reason)) return "Say why it is going back.";

        var here = Workflow.Stage(t.Stage)!;
        var to = Workflow.Stage(step.Stage)!;
        var warning = await WarningAsync(db, t.TenantId, t, step);
        string assignedNow = t.AssignedTo;
        var was = StepSnapshot.Read(step.Before);
        if (was != null)
        {
            t.Stage = was.Stage; t.Lane = was.Lane; t.Status = was.Status; t.StageReturn = was.StageReturn;
            t.Repairable = was.Repairable; t.Outcome = was.Outcome; t.WorkedBy = was.WorkedBy;
            t.TimeSpentMinutes = was.TimeSpentMinutes; t.InvoiceAmount = was.InvoiceAmount; t.InvoiceNumber = was.InvoiceNumber;
            t.PaymentMethod = was.PaymentMethod; t.PaymentReference = was.PaymentReference;
            t.QuoteAmount = was.QuoteAmount; t.QuoteReference = was.QuoteReference;
            t.QuoteSupplierEmail = was.QuoteSupplierEmail; t.QuoteSupplierNames = was.QuoteSupplierNames;
            t.DoNotInvoiceReason = was.DoNotInvoiceReason; t.AbandonedReason = was.AbandonedReason;
            // The machine leaving is put back only when the step undone WAS it leaving (Delivered). "Machine collected" is
            // not a step: done after the step being undone, it is not that step's to take back — restoring it made a
            // machine already gone "still here", so Payment received sent the job to Awaiting collection, whose Close was
            // refused as not collected, and round it went (2026-10-13).
            if (UndoesHandover(step)) { t.CollectedUtc = was.CollectedUtc; t.CollectedBy = was.CollectedBy; t.Delivered = was.Delivered; }
            t.ClosedUtc = was.ClosedUtc;
            t.UpdatedUtc = DateTime.UtcNow;
        }
        else MoveTo(t, to);

        // Whose desk: back to the billing queue or a supervisor, nobody in particular. Back to the bench from one of
        // those, the people it was with before the step. On the bench already, the people on it now stay — somebody who
        // joined since is not taken off by somebody else's mistake.
        if (to.Lane is WorkLane.Office or WorkLane.Supervisor) t.AssignedTo = "";
        else if (here.Lane == WorkLane.Bench) t.AssignedTo = assignedNow;
        else t.AssignedTo = was?.AssignedTo is { Length: > 0 } a ? a : t.WorkedBy;

        step.UndoneUtc = DateTime.UtcNow;
        step.UndoneBy = by;
        var undone = step.FormTitle.Length > 0 ? step.FormTitle : step.Body.Split(" → ")[0];
        var said = new List<string> { $"Reason: {reason.Trim()}", $"Undid: {undone} ({step.Author}, {step.CreatedUtc.Local():d MMM HH:mm})" };
        if (warning.Length > 0) said.Add(warning);
        if (t.AssignedTo != assignedNow)
            said.Add(t.AssignedTo.Length == 0 ? $"{Assignees.Say(Assignees.Parse(assignedNow))} taken off — {QueuePhrase(to.Lane)}."
                                              : $"Back with {Assignees.Say(Assignees.Parse(t.AssignedTo))}.");
        db.TicketNotes.Add(new TicketNote
        {
            TenantId = t.TenantId, TicketId = t.Id, Author = by, Stage = here.Key, MovedTo = to.Key, WentBack = true,
            FormTitle = $"Back to {to.Label}", Body = string.Join("\n", said)
        });
        await db.SaveChangesAsync();
        if (here.Lane == WorkLane.Office || to.Lane == WorkLane.Office)
            await RaiseBillingChangedAsync(t.TenantId, here.Lane != WorkLane.Office && to.Lane == WorkLane.Office
                ? new BillingArrival(t.Id, t.Number, t.Subject, to.Label, by) : null);
        return "";
    }
}
