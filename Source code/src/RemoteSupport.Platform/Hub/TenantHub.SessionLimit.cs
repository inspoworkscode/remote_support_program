using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// TWO MACHINES AT ONCE, AT MOST (2026-10-15). Clint: two sessions side by side, copying from one office machine and
/// pasting on the other, and no more than two. The console refuses a third before it asks (it knows its own windows);
/// this is the same rule where an old console, or a socket driven by hand, cannot get round it. The number and the
/// words are <see cref="SessionLimit"/>.
///
/// What counts is a session on ANOTHER machine in which this technician has a viewer socket open now. Nothing else:
/// <list type="bullet">
/// <item>Not the machine being asked for — joining or returning to it (Reconnect, a restart, a dropped connection)
/// adds nothing, so it is never refused.</item>
/// <item>Not a session waiting for them with nobody in it (held after a drop, or not yet joined). Counting those
/// would be stricter, but such a session can outlive the console that would have closed it, and one stuck there
/// would lock the technician out of a second machine until the server restarted. The console counts its waiting
/// sessions itself, where it can see them end.</item>
/// <item>Not somebody from another company: their own console counts their windows, and this hub cannot see the
/// sessions their own company's hub holds.</item>
/// </list>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>Null when this technician may open one more session on <paramref name="deviceId"/>; else the words.</summary>
    private string? SessionLimitProblem(int technicianId, string deviceId)
    {
        List<string> open;
        lock (_lock)
            open = _sessions.Values
                .Where(s => s.Info.DeviceId != deviceId && s.ViewerIds.ContainsValue(technicianId))
                .Select(s => s.Info.MachineName)
                .Distinct()
                .ToList();
        return SessionLimit.Refusal(open);
    }

    /// <summary>The same question, refused to the console in words. True when refused.</summary>
    private bool RefusedOverSessionLimit(TechConn tc, string deviceId)
    {
        var problem = SessionLimitProblem(tc.Who.TechnicianId, deviceId);
        if (problem == null) return false;
        _log.LogInformation("[{Tenant}] {Tech} asked for a session on {Device} while already in {Max} — refused",
            TenantName, tc.Who.DisplayName, deviceId, SessionLimit.PerTechnician);
        tc.Ws.Send(new ErrorMsg { Message = problem });
        return true;
    }
}
