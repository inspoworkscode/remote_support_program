namespace RemoteSupport.Shared;

/// <summary>
/// THE FREE VERSION (2026-10-06, docs/NEXT-SESSION-FREE.md). Every limit of it is asked HERE, the way
/// <c>Permissions</c> and <see cref="OutsiderAccess"/> are — never <c>plan == "free"</c> scattered through
/// pages. Clint: one owner/technician, unattended access to two computers, no attended access, no
/// ticketing; remote printing and file transfer kept; for people with remote offices, on their own
/// machines.
///
/// <para>Stored in <c>Tenant.Plan</c>, which already existed and only ever said "five-user": anything
/// that is not <see cref="Name"/> is the full version.</para>
///
/// <para><b>TWO ONE-PERSON PLANS SINCE 2026-10-25 (HANDOVER §161–§163).</b> In the morning the free version became
/// <see cref="FullName"/> (R50 a month, by card); in the evening Clint brought a free one back beside it: <i>"The free version
/// should only allow one connection for unattended access with password as it was previously. The basic version we should change
/// to allow attended and unattended, but still only a maximum of 20 machines."</i> So <see cref="Is"/> means EITHER — one
/// person, no tickets, the hub's allow-lists, two removals a month — and the plan decides the rest: <see cref="IsFree"/>
/// (stored <see cref="Name"/>, nothing to pay, <see cref="FreeMachines"/> computer, unattended only, with a password) or
/// <see cref="IsBasic"/> (stored <see cref="BasicName"/>, R50, <see cref="MaxMachines"/> to <see cref="MostMachines"/>
/// computers, and attended too: the person at the computer is asked, by the agent service — never a tray app, never a
/// "Get help now", never Request Support). <b>Ask <see cref="MachinesFor"/>, <see cref="Attended"/> and the words by plan;
/// never compare the stored string.</b> The class keeps its old name: renaming it would touch every guard for nothing a
/// person sees.</para>
/// </summary>
public static class FreePlan
{
    /// <summary>The Free version (2026-10-06; gone the morning of 2026-10-25; back the same evening).</summary>
    public const string Name = "free";
    /// <summary>The Basic Version for Remote Workers (2026-10-25). Stored "free" until §163 moved it here.</summary>
    public const string BasicName = "basic";

    /// <summary>What the Basic version is called where it is offered or described (Clint, 2026-10-25).</summary>
    public const string FullName = "The Basic Version for Remote Workers";
    /// <summary>What it is called where space is tight — the portal, the console, a pill.</summary>
    public const string ShortName = "Basic";
    /// <summary>Inside a sentence: "the Basic version allows 3 computers".</summary>
    public const string TheName = "the Basic version";

    /// <summary>What the Free version is called where it is offered or described.</summary>
    public const string FreeFullName = "The Free Version";
    public const string FreeShortName = "Free";
    public const string FreeTheName = "the Free version";

    /// <summary>The Free version reaches ONE computer (two before 2026-10-25). A company from before with two keeps both until
    /// it removes one: the hub only ever refuses a NEW computer past the limit.</summary>
    public const int FreeMachines = 1;

    /// <summary>Machines in the Basic price (two until 2026-10-25). More are bought, up to <see cref="MostMachines"/>.</summary>
    public const int MaxMachines = 3;

    /// <summary>
    /// The most machines a Basic company may pay for (2026-10-25, Clint: extra computers at R10 / $1 / £1 / €1 each, "no owner
    /// approval from my side necessary" — and, asked whether an IT company could use it for its customers, "cap it at 20").
    /// </summary>
    public const int MostMachines = 20;

    /// <summary>Either one-person plan: the Free version or the Basic version. Everything they share is asked by this.</summary>
    public static bool Is(string? plan) => IsFree(plan) || IsBasic(plan);
    /// <summary>The Free version alone: nothing to pay, one computer, unattended only.</summary>
    public static bool IsFree(string? plan) => string.Equals(plan, Name, StringComparison.OrdinalIgnoreCase);
    /// <summary>The Basic version alone: R50, three to twenty computers, attended and unattended.</summary>
    public static bool IsBasic(string? plan) => string.Equals(plan, BasicName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a technician may connect with somebody at the computer saying yes (2026-10-25: the Basic version, not the Free one).</summary>
    public static bool Attended(string? plan) => IsBasic(plan);

    public static string FullNameOf(string? plan) => IsFree(plan) ? FreeFullName : FullName;
    public static string ShortNameOf(string? plan) => IsFree(plan) ? FreeShortName : ShortName;
    public static string TheNameOf(string? plan) => IsFree(plan) ? FreeTheName : TheName;

    /// <summary>
    /// The machines this company may have. The Free version: <see cref="FreeMachines"/>. The Basic version: what it pays for
    /// (<c>Tenant.BasicMachines</c>, 0 = the <see cref="MaxMachines"/> in the price), never fewer than those nor more than
    /// <see cref="MostMachines"/>. THE number the hub, the installer and every page ask.
    /// </summary>
    public static int MachinesFor(string? plan, int paidFor) =>
        IsFree(plan) ? FreeMachines : Math.Clamp(paidFor <= 0 ? MaxMachines : paidFor, MaxMachines, MostMachines);

    /// <summary>"The Basic version allows N computers" — with how to have more while there is more to have.</summary>
    public static string TooManyFor(string? plan, int allowed) =>
        IsFree(plan)
        ? $"The Free version allows {FreeMachines} computer. Remove it in the console or the portal, or upgrade to the Basic version for more."
        : allowed < MostMachines
        ? $"The Basic version allows {allowed} computers. Add a computer on Billing in the portal, remove one in the console or the portal, or upgrade."
        : $"The Basic version allows {allowed} computers at most. Remove one in the console or the portal, or upgrade.";

    /// <summary>Machines a one-person company may REMOVE per calendar month (company time). Clint: limited, so
    /// its places cannot be rotated round a whole customer base.</summary>
    public const int RemovalsPerMonth = 2;

    /// <summary>The Free version, asked for anything but an unattended connection with a password.</summary>
    public const string NoAttended =
        "The Free version is unattended only: nobody at the computer is asked. Upgrade to the Basic version to connect with somebody there.";

    /// <summary>The person at a one-person company's computer asked for help, or made a code for outside help. Neither version has it.</summary>
    public static string NoAskingFor(string? plan) => IsFree(plan) ? NoAttended
        : "The Basic version cannot ask for help from the computer: connect to it from the console, and the person there is asked to allow it.";

    public const string NeedsPassword =
        "This computer has no unattended password, which the Free version requires. Run the installer again and choose one.";

    /// <summary>
    /// What a free company's console may send a machine in a session — an ALLOW-list, like an outside
    /// visitor's, so anything added to the protocol later is the free version's only when somebody decides
    /// so. Clint's choice: remote control, printing (both ways), and Files with copy and paste and drag
    /// and drop. "Remote control" includes the System menu — Ctrl+Alt+Del, restart, lock — without which
    /// an unattended machine in another office cannot be looked after. Not: scripts, sound, talking,
    /// recording, the privacy screen, chat, the Info tab.
    /// </summary>
    public static bool MaySend(Msg m) =>
        m is SubscribeMsg or QualityMsg or MouseMsg or KeyMsg or PingMsg or SysActionMsg
        // end-to-end encryption's handshake (2026-10-07)
        or E2eHelloMsg
        // Files, and drag and drop off the picture
        or FileListRequestMsg or FileListMsg or FileOfferMsg or FileAcceptMsg or FileDoneMsg or FilePullMsg
        or FileSetOfferMsg or FileSetAcceptMsg or FileSetProgressMsg or FileSetDoneMsg or FileSetPullMsg
        or SelectionRequestMsg
        // copy and paste
        or ClipboardMsg or ClipboardRequestMsg
        // copy on one machine, paste on the other, straight between them (2026-10-15; Clint's answer 5)
        or BridgeAskMsg or BridgeKeyMsg or BridgeEndMsg
        // printing, both ways (the documents themselves travel as files)
        or RemotePrintMsg or PrintFileMsg;

    /// <summary>Binary a free company's console may send: file chunks. Never voice.</summary>
    public static bool MaySendBinary(ReadOnlySpan<byte> b) => b.Length > 0 && BinKind.Of(b[0]) == BinKind.FileChunk;

    /// <summary>The console's Tools menu on the free version — what <see cref="MaySend"/> lets work.</summary>
    public static readonly string[] Tools = { "Files", "Print on my printer", "Print a file on their printer" };
}

/// <summary>
/// Why a machine is taken off a company's list (2026-10-06, Clint: "this is important for when machines
/// get upgraded, replaced or a person has been using their own private device"). ONE list and ONE rule,
/// asked by the console before it sends and by the hub before it removes. Picked from the list, with a
/// note — required for Other.
/// </summary>
public static class RemovalReasons
{
    public const string Other = "Other";
    public static readonly string[] All = { "Upgraded", "Replaced", "Private device", "No longer a customer", Other };

    /// <summary>What is wrong with this reason and note, or empty.</summary>
    public static string Problem(string? reason, string? note)
    {
        if (!All.Contains(reason ?? "")) return "Choose why the machine is being removed.";
        if (reason == Other && (note ?? "").Trim().Length < 3) return "Say why in the note — it is kept with the removal.";
        if ((note ?? "").Length > 500) return "The note is too long (500 characters at most).";
        return "";
    }
}
