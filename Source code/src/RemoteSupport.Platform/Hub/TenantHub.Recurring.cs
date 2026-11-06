using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Recurring tasks from the console (2026-10-07): set up from New ticket, cancelled from one of
/// their tickets. Every rule is <see cref="RecurringTaskService"/>'s — this only carries the words
/// across, as the portal's pages do. Changing a schedule's days or people is the portal's
/// (the console's ticket window opens it there).
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>The types the company has, and everybody a task may go to — the office too.</summary>
    private async Task SendRecurringKindsAsync(TechConn tc)
    {
        var answer = new RecurringKindsMsg();
        if (_registry.Recurring is { } rec) answer.Kinds = await rec.KindsAsync(TenantId);
        await using var db = await _dbf.CreateDbContextAsync();
        answer.People = await db.Technicians.Where(t => t.TenantId == TenantId && !t.Disabled && t.Role != 0)
            .OrderBy(t => t.DisplayName).Select(t => t.DisplayName).ToListAsync();
        tc.Ws.Send(answer);
    }

    private async Task SetUpRecurringAsync(TechConn tc, TicketCreateMsg m)
    {
        if (_registry.Recurring is not { } rec) { tc.Ws.Send(new ErrorMsg { Message = "Recurring tasks are not available on this server." }); return; }
        var input = new RecurringInput
        {
            Title = m.Subject ?? "", Details = m.Description ?? "", Kind = m.RecurringKind ?? "",
            AssignedTo = m.AssignTo ?? "", Priority = (TicketPriority)Math.Clamp(m.Priority, 0, 3),
            Repeats = Enum.IsDefined(typeof(Repeats), m.Repeats) ? (Repeats)m.Repeats : (Repeats)(-1),
            WeekdaysOnly = m.WeekdaysOnly, Days = m.Days, MonthDay = m.MonthDay
        };
        // Refused before a new client is written, as a refused booking is.
        var problem = RecurringTaskService.Problem(input);
        if (problem.Length > 0) { tc.Ws.Send(new ErrorMsg { Message = problem }); return; }

        Client? client = m.ClientId > 0 ? await _clients.GetAsync(TenantId, m.ClientId) : null;
        if (client == null && !string.IsNullOrWhiteSpace(m.NewClientName))
        {
            var (created, _) = await _clients.CreateAsync(TenantId, m.NewClientName, m.ContactName ?? "",
                m.NewClientPhone, m.NewClientEmail, m.NewClientAddress, "");
            client = created;
        }
        input.ClientId = client?.Id;

        var (task, error, first) = await rec.CreateAsync(TenantId, tc.Who.DisplayName, input);
        if (task == null) { tc.Ws.Send(new ErrorMsg { Message = error }); return; }
        tc.Ws.Send(new RecurringSetMsg
        {
            TaskId = task.Id,
            Message = $"\"{task.Title}\" is set up: {RecurringTaskService.Describe(task).ToLowerInvariant()}." +
                      (first != null ? $" Today's is open as #{first.Number}." : $" The first one opens on the morning of {RecurringTaskService.Day(task.NextDue)}.")
        });
        if (first != null) await SendTicketDetailAsync(tc, first.Id);
    }

    private async Task CancelRecurringAsync(TechConn tc, RecurringCancelMsg m)
    {
        if (_registry.Recurring is not { } rec) return;
        if (m.TicketId > 0 && !await MayTouchTicketAsync(tc, m.TicketId)) { tc.Ws.Send(new ErrorMsg { Message = "Ticket not found." }); return; }
        var error = await rec.CancelAsync(TenantId, m.TaskId, tc.Who.DisplayName, tc.Who.Role, m.Reason);
        if (m.TicketId > 0)
            await AfterWorkflowChangeAsync(tc, m.TicketId, error, "Recurring task cancelled. No more will be opened.");
        else if (error.Length > 0) tc.Ws.Send(new ErrorMsg { Message = error });
    }
}
