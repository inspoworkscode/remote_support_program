using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// What is wanted changed on a ticket. A null field is left as it is; the appointment says whether it was given at all,
/// because "no appointment" is an answer too.
/// </summary>
public sealed record TicketEdit(string? Subject = null, string? Description = null, int? Category = null,
    bool AppointmentGiven = false, DateTime? AppointmentUtc = null,
    // The due date (2026-10-26, HANDOVER §165): a day, given or not, as the appointment is.
    bool DueGiven = false, DateTime? DueDate = null);

/// <summary>
/// Changing a ticket after it is made (Clint, 2026-10-13): "Tickets, Subjects (names), scheduled appointment times,
/// category etc cannot be edited and needs to be fixed." His answers:
///
/// <list type="bullet">
/// <item>WHAT: the subject, the description (the fault as it was booked), the appointment, the category.</item>
/// <item>WHO: anybody who can see the ticket — office administrators included. The callers have already asked
/// <see cref="VisibleTo"/> (the portal) or <c>MayTouchTicketAsync</c> (the hub); this asks nothing more of the role.</item>
/// <item>CATEGORY: the job starts again at the new category's first stage. Every category has its own stages, so there is
/// no stage to carry across. Everything already done stays in the story.</item>
/// <item>EVERY CHANGE IS SAID: one entry in the story, who and when, each change with what it was. A description that was
/// changed keeps its old words in that entry, so nothing typed is lost.</item>
/// </list>
///
/// Everything is checked before anything is written, as <see cref="ActAsync"/> does: a refusal changes nothing at all.
/// </summary>
public sealed partial class TicketService
{
    /// <summary>The story's title for a category change. <see cref="LastStepAsync"/> will not go back across one.</summary>
    public const string CategoryChangedTitle = "Category changed";
    /// <summary>The story's title for any other change made here.</summary>
    public const string DetailsChangedTitle = "Details changed";
    /// <summary>Long enough for any real subject; a queue row and an email subject line are drawn from it.</summary>
    public const int SubjectMax = 200;

    /// <summary>
    /// Applies the edit. Empty error means it happened (or there was nothing to change — <paramref name="changes"/> is
    /// then empty); otherwise the reason, in words for the person, and nothing changed.
    /// </summary>
    public async Task<(string error, List<string> changes)> EditAsync(int tenantId, int ticketId, string by, TenantRole role, TicketEdit e)
    {
        var said = new List<string>();
        if (!Permissions.CanWorkTickets(role)) return (Permissions.OwnerOnlyRefusal, said);
        await using var db = await _dbf.CreateDbContextAsync();
        var t = await db.Tickets.FirstOrDefaultAsync(x => x.Id == ticketId && x.TenantId == tenantId);
        if (t == null) return ("That ticket is not on file.", said);
        var here = t.Stage.Length > 0 ? Workflow.Stage(t.Stage) : null;
        bool closed = here?.Lane == WorkLane.Done || t.Status is TicketStatus.Closed or TicketStatus.Resolved;

        // ---- what actually changes ----
        var subject = e.Subject?.Trim();
        if (subject == t.Subject) subject = null;
        var description = e.Description == null ? null : e.Description.Replace("\r\n", "\n").Trim();
        if (description != null && description == t.Description.Replace("\r\n", "\n").Trim()) description = null;
        TicketCategory? category = null;
        if (e.Category is int raw)
        {
            if (!Enum.IsDefined(typeof(TicketCategory), raw)) return ("That is not one of the ticket categories.", said);
            if ((TicketCategory)raw != t.Category) category = (TicketCategory)raw;
        }
        bool appointmentChanges = false;
        DateTime? at = e.AppointmentUtc is DateTime a ? DateTime.SpecifyKind(a, DateTimeKind.Utc) : null;
        // To the minute: a form shows hours and minutes, so seconds are never a change somebody made.
        if (e.AppointmentGiven && Minute(at) != Minute(t.AppointmentUtc)) appointmentChanges = true;
        var due = TicketDue.Day(e.DueDate);
        bool dueChanges = e.DueGiven && due?.Date != t.DueDate?.Date;

        // ---- may it? ----
        if (subject != null && subject.Length == 0) return ("A ticket needs a subject — say in a few words what it is about.", said);
        if (subject != null && subject.Length > SubjectMax) return ($"Keep the subject to {SubjectMax} characters — the rest belongs in the description.", said);
        if (category != null)
        {
            if (closed) return ("This job is closed, so its category can no longer be changed.", said);
            if (category == TicketCategory.Recurring)
                return ("A recurring task is set up on the Recurring tasks page — a ticket cannot be turned into one.", said);
            // A category the company has switched off since (Job types) — asked of the company NOW.
            if (JobTypes.CategoryRefusal(await CompanyOffAsync(db, tenantId), category.Value) is { Length: > 0 } off) return (off, said);
            if (category == TicketCategory.Workshop)
            {
                // The same two things a workshop booking needs (§35, answer 1): who it is booked in to, and its photos —
                // those as this job's own Job types say.
                if (CheckBookIn(TicketCategory.Workshop, t.AssignedTo) is { Length: > 0 })
                    return ("A workshop job is booked in to a technician. Put them on the ticket first (People on it), then change the category.", said);
                int photosIn = await db.TicketPhotos.CountAsync(p => p.TicketId == t.Id && p.Kind == PhotoKind.Before);
                if (JobTypes.PhotosInRequired(t.RouteOff) && photosIn < TicketPhotoService.Required)
                    return ($"A workshop job needs {TicketPhotoService.Required} photos of the machine as it arrived. Add them first — " +
                            (photosIn == 0 ? "none have been added yet." : photosIn == 1 ? "one has been added." : $"{photosIn} have been added."), said);
            }
        }
        if (appointmentChanges)
        {
            if (closed) return ("This job is closed, so its appointment can no longer be changed.", said);
            if (at != null && at <= DateTime.UtcNow) return ("That time has already passed. Choose a date and time still to come.", said);
        }
        if (dueChanges)
        {
            if (closed) return ("This job is closed, so its due date can no longer be changed.", said);
            if (TicketDue.Problem(due) is { Length: > 0 } late) return (late, said);
        }
        if (subject == null && description == null && category == null && !appointmentChanges && !dueChanges) return ("", said);

        // ---- change it, saying each ----
        if (subject != null) { said.Add($"Subject: {subject} (was: {t.Subject})"); t.Subject = subject; }
        if (description != null)
        {
            var old = t.Description.Trim();
            said.Add(old.Length == 0 ? "Description added." : description.Length == 0 ? $"Description removed. It was:\n{old}" : $"Description changed. It was:\n{old}");
            t.Description = description;
        }
        if (appointmentChanges)
        {
            var was = t.AppointmentUtc.HasValue ? Appointment(t.AppointmentUtc.Value) : "not scheduled";
            said.Add(at == null ? $"Appointment removed (was {was})" : $"Appointment: {Appointment(at.Value)} (was {was})");
            t.AppointmentUtc = at;
        }
        if (dueChanges)
        {
            var was = t.DueDate is DateTime old ? TicketDue.Say(old) : "not set";
            said.Add(due == null ? $"Due date removed (was {was})" : $"Due: {TicketDue.Say(due.Value)} (was {was})");
            t.DueDate = due;
        }
        var fromLane = here?.Lane;
        if (category != null)
        {
            var oldName = CategoryName(t.Category);
            t.Category = category.Value;
            // What the old route had decided means nothing on the new one. The money and the work recorded stay.
            t.StageReturn = ""; t.Outcome = ""; t.Repairable = null;
            var first = Workflow.Stage(Workflow.FirstFor(category.Value))!;
            MoveTo(t, first);
            said.Add($"Category: {CategoryName(category.Value)} (was {oldName}). It starts again at {first.Label}" +
                     (here != null ? $" — it was at {here.Label}." : "."));
            if (t.AssignedTo.Length == 0) said.Add("Nobody is on it yet — it is waiting for somebody to pick it up.");
        }
        t.UpdatedUtc = DateTime.UtcNow;
        // One entry, at the stage it was at when it was changed. It MOVES nothing in the route's sense (MovedTo empty):
        // a category change is not a step Back can undo, and the crumb's "came from" is not the old route's stage.
        db.TicketNotes.Add(new TicketNote
        {
            TenantId = tenantId, TicketId = t.Id, Author = by, Stage = here?.Key ?? "", MovedTo = "",
            FormTitle = category != null ? CategoryChangedTitle : DetailsChangedTitle, Body = string.Join("\n", said)
        });
        await db.SaveChangesAsync();
        // Out of the billing queue: the office's count moves.
        if (category != null && fromLane == WorkLane.Office) await RaiseBillingChangedAsync(tenantId, null);
        return ("", said);

        static DateTime? Minute(DateTime? d) => d is DateTime v ? new DateTime(v.Year, v.Month, v.Day, v.Hour, v.Minute, 0, DateTimeKind.Utc) : null;
    }
}
