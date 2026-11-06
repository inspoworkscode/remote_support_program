namespace RemoteSupport.Platform.Data;

/// <summary>
/// How much a platform admin may do (2026-10-13, docs/NEXT-SESSION-CONTROL.md). In order: each level can do
/// everything the one below it can. <see cref="Services.PlatformAdmins"/> holds every rule about them.
/// </summary>
public enum AdminLevel
{
    /// <summary>Sees everything, changes nothing.</summary>
    Viewer = 1,
    /// <summary>Approves, rejects, activates and suspends companies; seats, billing, country; emails their owners.</summary>
    Manager = 2,
    /// <summary>All of that, plus Viewer and Manager admins, servers and the platform's mail.</summary>
    Full = 3,
    /// <summary>Clint. Makes and changes Full admins and Owners; never removed or lowered from the pages.</summary>
    Owner = 4
}

/// <summary>
/// A person who helps run the platform (2026-10-13) — Clint: <i>"an option for me to add admins that help me
/// manage everything like activating and suspending clients"</i>. Not a company's staff: these sign in at
/// <c>/owner</c>, never <c>/login</c>, and belong to no company. They live only on the CONTROL server — the one
/// the dashboard is opened on — which reaches every other server with that server's key, never a password.
///
/// <para>Set up from an emailed link (<see cref="LinkHash"/>, SHA-256 only, like <see cref="AccountToken"/>):
/// a password and an authenticator, saved together only once the code proves the app works. Removed is
/// deleted — what they did stays in <see cref="AdminLogEntry"/> by name.</para>
/// </summary>
public sealed class PlatformAdmin
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public AdminLevel Level { get; set; } = AdminLevel.Viewer;
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    /// <summary>The authenticator secret, encrypted with Data Protection. Empty until set up.</summary>
    public string TotpSecretEnc { get; set; } = "";
    /// <summary>The last code step used, so one code cannot be used twice.</summary>
    public long TotpLastStep { get; set; }
    /// <summary>Moves whenever anything about them changes: every cookie and trusted browser of theirs then stops working.</summary>
    public string SecurityStamp { get; set; } = "";
    /// <summary>The set-up link's secret, hashed. Empty once used; a new link replaces it.</summary>
    public string LinkHash { get; set; } = "";
    public DateTime? LinkExpiresUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    public DateTime? LastSignInUtc { get; set; }

    /// <summary>A password and an authenticator, both — until then they cannot sign in.</summary>
    public bool IsSetUp => PasswordHash.Length > 0 && TotpSecretEnc.Length > 0;
}

/// <summary>
/// One thing an admin did (2026-10-13): approved, suspended, changed seats, added an admin, emailed a
/// company. Clint: <i>"every action is logged with the admin's name"</i>. By NAME, not id, so the record
/// outlives the admin. <see cref="Server"/> is where it happened — this server, or another reached through
/// the dashboard.
/// </summary>
public sealed class AdminLogEntry
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Who { get; set; } = "";
    public string What { get; set; } = "";
    /// <summary>The company it concerned, by name, or empty.</summary>
    public string Company { get; set; } = "";
    public string Server { get; set; } = "";
}

/// <summary>
/// A server the dashboard watches (2026-10-13, stage B). The control server keeps one row per OTHER server — its public
/// address and its server key (encrypted; <c>Platform:ServerKey</c> on that server) — and one for ITSELF
/// (<see cref="IsLocal"/>, no address or key), made the first time the dashboard is opened, so its switch and its
/// alert state live in the same place as every other server's.
/// </summary>
public sealed class ControlServer
{
    public int Id { get; set; }
    public bool IsLocal { get; set; }
    /// <summary>What the dashboard calls it — "za1". The server's own name, as it reports it, until changed here.</summary>
    public string Name { get; set; } = "";
    /// <summary>Its public address, https://za2.theremotesupportprogram.com. Empty for this server.</summary>
    public string Address { get; set; } = "";
    public string KeyEnc { get; set; } = "";
    /// <summary>The region it said it holds at its last report.</summary>
    public string Region { get; set; } = "";
    /// <summary>Whether signup may send new companies here (stage C). On by default; an admin can stop it.</summary>
    public bool TakingNewCompanies { get; set; } = true;
    /// <summary>The connections it carries at 100%, once MEASURED for its size. 0: worked out from its memory (ServerCapacity).</summary>
    public int CapacityConnections { get; set; }
    /// <summary>The highest level the Full admins have been emailed about and not seen fall since (ServerCapacity.Level).</summary>
    public int AlertedLevel { get; set; }
    /// <summary>The level at the previous sweep, so a level is emailed only once it has held for two sweeps.</summary>
    public int LastLevel { get; set; }
    public DateTime? LastReportUtc { get; set; }
    public string LastError { get; set; } = "";
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public string AddedBy { get; set; } = "";
}
