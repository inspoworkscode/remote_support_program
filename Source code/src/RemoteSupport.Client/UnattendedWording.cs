namespace RemoteSupport.Client;

/// <summary>What the tray app's unattended switch offers and what it says, for one set of facts.</summary>
public readonly record struct UnattendedUi(bool Usable, string Note, string MenuText);

/// <summary>
/// The words under the unattended switch, and whether the switch can be used at all.
///
/// A class of its own rather than a method on the form, for the reason DeviceList is: the states
/// that matter here are not all reachable on the machine running the test. "The agent service is
/// installed but not connected" needs a machine with the service installed, and the development
/// PC does not have one — so on that machine the single most important case would quietly never
/// be exercised, which is §30's lesson ("three faults of one shape, two of them invisible on this
/// machine") arriving a third time.
///
/// Asked as a function, every state is one call. tools/unattendedcheck asks it directly.
/// </summary>
public static class UnattendedWording
{
    /// <param name="serviceInstalled">The agent service exists on this machine (a registry key).</param>
    /// <param name="agentOnline">The agent is CONNECTED, as the server sees it. Not the same thing.</param>
    /// <param name="serverKnows">The server has told us the state at least once.</param>
    /// <param name="connected">This app has a link to the server.</param>
    /// <param name="on">The permission, as the server holds it.</param>
    /// <param name="hasPassword">An unattended password is set (2026-10-02).</param>
    /// <param name="awaitingConfirm">This computer was reinstalled and the IT company has not yet confirmed it is the
    /// same computer (2026-10-07): the tick is kept, and the hub holds unattended access back until they do.</param>
    public static UnattendedUi For(bool serviceInstalled, bool agentOnline, bool serverKnows, bool connected, bool on, string companyName,
        bool hasPassword = false, bool awaitingConfirm = false)
    {
        // INSTALLED IS NOT WORKING, and conflating the two is the whole of Clint's 2026-09-21
        // report: the switch "switches between attended and unattended on the console, but does
        // not allow for unattended access. the client still has to click on allow."
        //
        // The registry key that says "installed" is written by sc.exe and stays there whether the
        // service ever starts, ever reaches the server, or died months ago. agentOnline comes from
        // the hub, which is the one thing that can see the agent's socket, and it is the only
        // honest answer to "will unattended access work".
        bool installed = agentOnline || serviceInstalled;
        bool usable = serverKnows && connected && installed;
        bool live = serverKnows && agentOnline;

        // The box stays operable whenever there is a service and a server: permission is the
        // customer's to give or withdraw, and must not depend on a service being up at that
        // instant. What must not happen is this app claiming the permission has an effect it does
        // not have — that sentence is what sent Clint to the console, where the machine had duly
        // moved to the Unattended tab, and on to a session that asked for Allow. Three things
        // agreeing with each other and none of them true, because none was looking at the agent.
        string note =
            !installed
                ? "This computer was set up without the background service, so unattended access cannot be "
                  + "switched on here. Ask your IT company if you need it."
            : !serverKnows || !connected
                ? "Waiting for the support server before this can be changed."
            : on && !live
                // Allowed, and it still will not work. Named rather than glossed over: the person
                // here cannot start a service, but they CAN tell their IT company — and the
                // alternative is being asked to accept a session they were told they would not be
                // asked about.
                ? "Unattended access is allowed, but the background service on this computer is not "
                  + $"running, so technicians at {companyName} will still have to ask you first. Let them know."
            : on && awaitingConfirm
                // The tick is theirs and is kept; what waits is the office. Said, so a customer is not
                // surprised at being asked, and knows who to tell.
                ? $"This computer was installed again, so {companyName} must confirm it is the same computer before "
                  + "unattended access works. Until then, technicians will ask you first."
            : on && hasPassword
                ? $"Technicians at {companyName} can connect when nobody is at this computer, if they type the password set here."
            : on
                ? $"Technicians at {companyName} can connect when nobody is at this computer."
            : !live
                ? "Technicians can only connect when you ask for support. (The background service on this "
                  + "computer is not running, so unattended access would not work yet either.)"
                : "Technicians can only connect when you ask for support.";

        // A DISABLED menu item shows no tooltip — Windows does not raise the hover for one — so
        // the explanation is invisible in exactly the case where somebody needs it. Clint met that
        // on 2026-09-20: a greyed "Allow unattended access" and no way to find out which of three
        // reasons it was. So the reason goes in the item's own TEXT while it cannot be used.
        //
        // The same argument applies to an ENABLED item whose tick would not do anything. Its
        // tooltip does work, but it needs a hover, and this is precisely the case nobody knows to
        // go looking for: a service that is installed and dead.
        string menu = usable
            ? (live ? "Allow unattended access"
                    : "Allow unattended access — the background service is not running")
            : !installed
                ? "Allow unattended access — not set up on this computer"
                : !connected
                    ? "Allow unattended access — not connected to the server"
                    : "Allow unattended access — waiting for the server";

        return new UnattendedUi(usable, note, menu);
    }
}
