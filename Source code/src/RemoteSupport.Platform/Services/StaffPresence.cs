using System.Collections.Concurrent;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Who from the office is signed in right now, per company — a console connected to the hub, or a
/// portal page open (its live chat stream). Website chat asks it (3.8.3): Clint's answer was that when
/// nobody is signed in the bubble takes a message instead, so "is anybody there" must be a fact the
/// server knows, not a guess.
///
/// The office administrator has no console, so they count while a portal page is open in front of
/// them, which is exactly when they can answer.
/// </summary>
public sealed class StaffPresence
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<long, string>> _here = new();
    private long _next;

    /// <summary>Somebody arrived or left at this company.</summary>
    public event Action<int>? Changed;

    /// <summary>Counts them as here until the result is disposed.</summary>
    public IDisposable Arrive(int tenantId, string who)
    {
        var id = Interlocked.Increment(ref _next);
        _here.GetOrAdd(tenantId, _ => new()).TryAdd(id, who);
        Changed?.Invoke(tenantId);
        return new Leaving(this, tenantId, id);
    }

    public int Count(int tenantId) => _here.TryGetValue(tenantId, out var d) ? d.Count : 0;

    public List<string> Names(int tenantId) =>
        _here.TryGetValue(tenantId, out var d) ? d.Values.Distinct().OrderBy(n => n).ToList() : new();

    private sealed class Leaving : IDisposable
    {
        private readonly StaffPresence _p; private readonly int _tenant; private readonly long _id; private int _done;
        public Leaving(StaffPresence p, int tenant, long id) { _p = p; _tenant = tenant; _id = id; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            if (_p._here.TryGetValue(_tenant, out var d)) d.TryRemove(_id, out _);
            _p.Changed?.Invoke(_tenant);
        }
    }
}
