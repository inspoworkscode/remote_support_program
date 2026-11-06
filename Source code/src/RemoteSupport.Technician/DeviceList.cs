using RemoteSupport.Shared;

namespace RemoteSupport.Technician;

/// <summary>Which of the console's two device tabs a machine belongs on.</summary>
public enum DeviceTab
{
    /// <summary>Somebody has to be there. Everything without unattended permission.</summary>
    Attended,
    /// <summary>The person at the machine has allowed a technician in with nobody there.</summary>
    Unattended,
    /// <summary>Every machine, in one list — the one-person plans (2026-10-25, §163), which have no tabs: the Basic version's
    /// computers may be attended or unattended, and there is nothing to switch between.</summary>
    All
}

/// <summary>One customer's machines, as one collapsible block in the device list.</summary>
public sealed class DeviceGroup
{
    /// <summary>The client's name, or the not-linked heading.</summary>
    public string Name = "";
    /// <summary>The ticketing client, or 0 for the machines nobody has linked.</summary>
    public int ClientId;
    public List<DeviceInfo> Machines = new();
    public bool IsUnlinked => ClientId == 0;
    public int OnlineCount => Machines.Count(m => m.Online);
}

/// <summary>
/// What the console's device list shows: which tab a machine is on, and how machines gather under
/// the customer who owns them.
///
/// It is a class of its own rather than a method on the form so that `dashtabs` can ask it the
/// questions directly. A harness that had to reach into a Form through reflection to find out
/// which tab a machine landed on would be testing the reflection.
/// </summary>
public static class DeviceList
{
    /// <summary>The heading for machines nobody has linked to a customer yet.</summary>
    public const string Unlinked = "Not linked to a client";

    /// <summary>
    /// Which tab a machine belongs on.
    ///
    /// The switch the CUSTOMER holds decides it, not whether their agent service happens to be
    /// connected. A machine whose owner allowed unattended access is an unattended machine even
    /// while it is switched off — that is what the technician needs to know when they are deciding
    /// who they can help tonight. Whether it can be connected to *right now* is the dot's job.
    /// (See handover §31: UnattendedEnabled is the permission, Unattended is permission AND
    /// capability. Using the latter here would move machines between tabs as their agent came and
    /// went, which is a list that will not hold still.)
    /// </summary>
    public static bool Belongs(DeviceInfo d, DeviceTab tab) =>
        tab == DeviceTab.All || (tab == DeviceTab.Unattended ? d.UnattendedEnabled : !d.UnattendedEnabled);

    /// <summary>
    /// The machine is sitting on the Unattended tab and unattended access will NOT work on it.
    ///
    /// Clint, 2026-09-21: the switch "switches between attended and unattended on the console, but
    /// does not allow for unattended access. the client still has to click on allow." This is that
    /// machine, and until now the console had no way of showing it. Belongs() goes by the
    /// permission, on purpose, so the list holds still. But the hub needs the permission AND a
    /// live agent connection before it will let the agent host a technician-initiated session
    /// (TenantHub.StartSession), and without the agent it falls back to the tray app — whose host
    /// is launched with --consent ask. So the customer is asked, on a machine the console has just
    /// called unattended.
    ///
    /// The dot cannot say it either: the dot is Online, which is the agent OR the tray app, so a
    /// machine with a dead agent service and a healthy tray app is as green as any other.
    ///
    /// ClientOnline is part of the question, not decoration. A machine that is simply switched off
    /// is already saying so with a red dot and is not claiming anything it cannot do; this is
    /// about a machine that is up, looks ready, and is not.
    ///
    /// And since 2026-10-07 a machine back from a reinstall that nobody in the office has confirmed
    /// (<see cref="DeviceInfo.ReinstallUnconfirmed"/>): ticked, up, and held back by the hub until somebody does.
    /// </summary>
    public static bool ClaimsUnattendedButCannot(DeviceInfo d) =>
        d.UnattendedEnabled && ((d.ReinstallUnconfirmed && d.Online) || (!d.AgentOnline && d.ClientOnline));

    /// <summary>How many machines are making that claim, for the tab that is making it for them.</summary>
    public static int CountNotReady(IEnumerable<DeviceInfo> devices) => devices.Count(ClaimsUnattendedButCannot);

    /// <summary>
    /// The machines on one tab, gathered under their customer.
    ///
    /// Groups are alphabetical, with the machines nobody has linked LAST: somebody here is trying
    /// to reach a customer, and their customers should not be below a pile of strays. The portal's
    /// Devices page follows the same order since 2026-09-30 (it used to put the strays first).
    /// </summary>
    public static List<DeviceGroup> Build(IEnumerable<DeviceInfo> devices, DeviceTab tab)
    {
        var groups = new List<DeviceGroup>();
        foreach (var d in devices.Where(d => Belongs(d, tab)))
        {
            // Machines are gathered by CLIENT, never by contact: three people at one company are
            // one customer with three PCs, and splitting them into three blocks would undo the
            // thing grouping is for. Which person each machine belongs to is the first column.
            var name = d.ClientId > 0
                ? (string.IsNullOrWhiteSpace(d.ClientName) ? $"Client #{d.ClientId}" : d.ClientName.Trim())
                : Unlinked;
            var g = groups.FirstOrDefault(x => x.ClientId == d.ClientId && x.Name == name);
            if (g == null) groups.Add(g = new DeviceGroup { Name = name, ClientId = d.ClientId });
            g.Machines.Add(d);
        }

        foreach (var g in groups)
            g.Machines = g.Machines
                .OrderByDescending(m => m.Online)
                .ThenBy(m => m.MachineName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return groups
            .OrderBy(g => g.IsUnlinked)                                  // strays last
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The count on a tab's own button, so a technician can see where the machines are
    /// without clicking to find out.</summary>
    public static int CountOn(IEnumerable<DeviceInfo> devices, DeviceTab tab) => devices.Count(d => Belongs(d, tab));
}
