using Microsoft.EntityFrameworkCore;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;
using RemoteSupport.Shared;

namespace RemoteSupport.Platform.Hub;

/// <summary>
/// A reinstalled machine waits for the office (2026-10-07, roadmap 4.4; HANDOVER §111).
///
/// <para>A machine that comes back with a new install is matched to its old row by its hardware fingerprint
/// (PersistDeviceAsync), so its client link, history, password and tick follow it. But the fingerprint is the
/// board's UUID and Windows' MachineGuid — readable by any program on the machine — so a stranger's PC holding
/// those two values and the company's public key could say "I am that machine, reinstalled" and a technician
/// would connect to the wrong computer, clipboard and files and all.</para>
///
/// <para>Clint's answer: <b>the office confirms it</b>. The row is marked
/// <see cref="DeviceInfo.ReinstallUnconfirmed"/> and unattended access is held back (<c>MayGoUnattended</c>)
/// until a supervisor or the owner says it is the same machine — in the console (right-click) or on the
/// portal's Devices page. The customer's own tick is NOT enough, because whoever presents the fingerprint
/// holds the tray app too. Attended help is untouched: the person there is asked, as on any attended
/// machine. A reinstall only happens after an uninstall (which deletes device.json); an ordinary upgrade
/// keeps its id and never comes here.</para>
/// </summary>
public sealed partial class TenantHub
{
    /// <summary>What a technician is told when they try to go in unattended on a machine nobody has confirmed.</summary>
    private const string ReinstallUnconfirmedWords =
        "That machine was reinstalled, and nobody has confirmed yet that it is the same machine. Unattended access "
        + "waits until a supervisor or the owner does — right-click it and choose \"Confirm it is the same machine\". "
        + "Until then, connecting asks the person at it.";

    /// <summary>
    /// A supervisor or the owner says this reinstalled machine IS the one on the list. Returns why not, or empty.
    /// The ONE rule, for the console and the portal alike.
    /// </summary>
    public async Task<string> ConfirmReinstallAsync(string deviceId, string who, TenantRole role)
    {
        if (!Permissions.CanConfirmReinstall(role)) return "Only a supervisor or the owner can confirm a reinstalled machine.";
        string machine;
        lock (_lock)
        {
            if (!_devices.TryGetValue(deviceId, out var info) || IsWeb(deviceId)) return "That machine is not on the list.";
            if (!info.ReinstallUnconfirmed) return "";   // already confirmed (twice from two consoles is not an error)
            machine = info.MachineName;
        }

        await using var db = await _dbf.CreateDbContextAsync();
        var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == TenantId && x.DeviceId == deviceId);
        if (d == null) return "That machine is not on the list.";
        d.ReinstallUnconfirmed = false;
        d.ReinstallConfirmedBy = who;
        d.ReinstallConfirmedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        lock (_lock)
            if (_devices.TryGetValue(deviceId, out var live))
            {
                live.ReinstallUnconfirmed = false;
                Recompute(live);
            }
        _log.LogInformation("[{Tenant}] {Who} confirmed {Machine} ({Id}) is the same machine after its reinstall — unattended access as its person set it",
            TenantName, who, machine, deviceId);
        TellDeviceItsUnattendedState(deviceId);
        BroadcastTechs(new DeviceUpdateMsg { Device = Snapshot(deviceId) });
        return "";
    }

    private async Task ConfirmReinstallForConsoleAsync(TechConn tc, ConfirmReinstallMsg m)
    {
        var why = await ConfirmReinstallAsync(m.DeviceId, tc.Who.DisplayName, tc.Who.Role);
        if (why.Length > 0) tc.Ws.Send(new ErrorMsg { Message = why });
    }
}
