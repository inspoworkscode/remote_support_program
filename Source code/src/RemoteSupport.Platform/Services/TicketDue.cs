using System.Globalization;
using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// A ticket's due date (2026-10-26, HANDOVER §165). Clint, shown SuperOps' ticket list with a Due Date column: "add a due
/// date now as well". A DAY, not a time: any ticket may have one, set when it is opened or changed later; a recurring
/// task's ticket gets its day; a call out with no due date is due on its appointment, for the list.
///
/// <para>Overdue is: the day has gone and the job is still the technicians' (the bench) and not done. Once it has gone to be
/// invoiced the people on it have done their part, as a recurring task's Overdue has always meant
/// (<see cref="RecurringTaskService.IsOverdue"/>, which this widens to every ticket).</para>
/// </summary>
public static class TicketDue
{
    /// <summary>A typed day — "2026-11-04", from a date box — as it is stored; null for an empty box; false for nonsense.</summary>
    public static bool TryRead(string? typed, out DateTime? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(typed)) return true;
        if (!DateTime.TryParseExact(typed.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        day = RecurringTaskService.AsDate(d);
        return true;
    }

    /// <summary>The day stored, from anything a caller hands in (a console sends a date at midnight).</summary>
    public static DateTime? Day(DateTime? d) => d is DateTime v ? RecurringTaskService.AsDate(v) : null;

    /// <summary>Why a NEW due day cannot be set, or empty: never before today. A day already on the ticket stays as it is.</summary>
    public static string Problem(DateTime? day, DateTime? companyToday = null) =>
        day is DateTime d && d.Date < (companyToday ?? CompanyClock.Now).Date
            ? "That day has already gone. Choose today or a day still to come." : "";

    /// <summary>When the list says it is due: its due date, else a call out's appointment.</summary>
    public static DateTime? Effective(Ticket t) => t.DueDate ?? t.AppointmentUtc;

    /// <summary>Past its due day with the work not done: still on the bench, not resolved or closed.</summary>
    public static bool IsOverdue(Ticket t, DateTime? companyToday = null) =>
        t.DueDate is DateTime d && d.Date < (companyToday ?? CompanyClock.Now).Date
        && t.Lane == (int)WorkLane.Bench && t.Status is not (TicketStatus.Resolved or TicketStatus.Closed)
        || RecurringTaskService.IsOverdue(t, companyToday);

    /// <summary>"Tue 4 Nov 2026" — how a due day is said in the story and on the ticket.</summary>
    public static string Say(DateTime day) => day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
}
