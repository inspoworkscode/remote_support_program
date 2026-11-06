using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// Branding reaches installed machines (2026-10-07, roadmap 4.6, the settings half). A company that changed its
/// logo or colours used to see it only on new downloads: the look was baked in at install time and nothing ever
/// sent a machine another. Now a machine says in its hello which look it has (<see cref="HelloMsg.BrandHash"/>);
/// when that is not the company's current one it is sent <see cref="BrandingMsg"/>, and saving the branding sends it
/// to every machine connected at that moment. Only to links that said <see cref="DeviceCan.Branding"/>.
///
/// <para>What travels is <see cref="BrandLook"/> alone — never the server address or the company key. Machines keep
/// it beside their installed branding (<see cref="BrandRefresh"/>).</para>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>The company's look as last built; null until first asked. Guarded by <c>_lock</c>.</summary>
    private BrandingMsg? _brandNow;
    private DateTime _brandBuiltUtc;
    private int _brandToken;

    /// <summary>The page saves the words, then the logo: two saves a moment apart are one change to a machine.</summary>
    private static readonly TimeSpan BrandingSettle = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// How long a built look is trusted. The portal's saves rebuild it at once; this is for the command line
    /// (<c>set-brand</c>, <c>set-logo</c>), a separate process this hub never hears from. <c>Testing:BrandingStaleMs</c>
    /// shortens it for tools/brandpush alone; never set in production.
    /// </summary>
    private TimeSpan BrandingStale =>
        _cfg.GetValue<int>("Testing:BrandingStaleMs") is > 0 and var ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromMinutes(1);

    private async Task<BrandingMsg?> BrandNowAsync()
    {
        lock (_lock) if (_brandNow != null && DateTime.UtcNow - _brandBuiltUtc < BrandingStale) return _brandNow;
        var built = _registry.Tenants == null ? null : await _registry.Tenants.BrandingMessageAsync(TenantId);
        lock (_lock)
        {
            if (built != null) { _brandNow = built; _brandBuiltUtc = DateTime.UtcNow; }
            return _brandNow;
        }
    }

    /// <summary>A machine that has just connected: sent the look if it has another.</summary>
    private async Task TellDeviceItsBrandingAsync(DeviceConn dc)
    {
        if (!dc.CanBranding) return;
        try
        {
            var now = await BrandNowAsync();
            if (now == null) return;
            string theirs;
            lock (_lock) { if (dc.BrandHash == now.Hash) return; theirs = dc.BrandHash; dc.BrandHash = now.Hash; }
            dc.Ws.Send(now);
            _log.LogInformation("[{Tenant}] sent {Role} on {Device} the company's current branding (it had {Theirs})",
                TenantName, dc.Role, dc.DeviceId, theirs.Length == 0 ? "the installed look" : theirs[..Math.Min(12, theirs.Length)]);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] branding for {Device}", TenantName, dc.DeviceId); }
    }

    /// <summary>
    /// The company saved its branding. Built again; if what a machine would see changed, every connected machine that
    /// can take it is sent it now.
    /// </summary>
    public async Task BrandingChangedAsync()
    {
        int token = Interlocked.Increment(ref _brandToken);
        await Task.Delay(BrandingSettle);
        if (Volatile.Read(ref _brandToken) != token) return;   // a later save will do it
        if (_registry.Tenants == null) return;
        try
        {
            var fresh = await _registry.Tenants.BrandingMessageAsync(TenantId);
            if (fresh == null) return;
            // The name this hub goes by (log lines, "Name (Company)" for a visit) follows a renamed company too.
            if (fresh.Look.CompanyName.Length > 0) Rename(fresh.Look.CompanyName);
            List<DeviceConn> send;
            lock (_lock)
            {
                _brandNow = fresh; _brandBuiltUtc = DateTime.UtcNow;
                // Per connection, not "did it change": a machine that already has it is simply not in the list.
                send = _deviceConns.Where(d => d.CanBranding && d.BrandHash != fresh.Hash).ToList();
                foreach (var d in send) d.BrandHash = fresh.Hash;
            }
            foreach (var d in send) d.Ws.Send(fresh);
            if (send.Count > 0) _log.LogInformation("[{Tenant}] branding changed: sent to {Count} connection(s)", TenantName, send.Count);
        }
        catch (Exception ex) { _log.LogWarning(ex, "[{Tenant}] branding changed", TenantName); }
    }
}
