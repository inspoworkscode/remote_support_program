using RemoteSupport.Platform.Data;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// What somebody may do, asked as questions rather than answered by comparing a role.
///
/// This exists because of roadmap 3.6.8: a person will hold a SET of roles rather than one, since
/// a one-man business is owner, technician and office administrator at once, and "technician and
/// office administrator but not owner" cannot be said today at all. Every
/// <c>role == TenantRole.Admin</c> scattered through the pages would be a rewrite the day that
/// lands.
///
/// So pages and services ask these questions. **Roles became tick boxes on 2026-09-29** and only
/// this file changed its answers: every question asks whether ANY of the person's boxes allows it,
/// so the union of two roles is the OR of two answers. Nothing here may ask whether a person holds
/// EXACTLY one role — a technician who is also the office administrator is both.
///
/// Do not add a property that returns the role. The point is that callers cannot see it.
/// </summary>
public static class Permissions
{
    // ---- the company ----

    /// <summary>
    /// A supervisor, or the owner in either form. Private on purpose: callers ask what somebody may
    /// DO, and this is only the shared half of several answers.
    /// </summary>
    private static bool Supervises(TenantRole role) =>
        role.Has(TenantRole.Admin | TenantRole.Owner);

    /// <summary>
    /// Does ticket work at all: anybody with a paid box — Technician, Supervisor or Office administrator.
    ///
    /// <para><b>An owner-only login does not</b> (Clint, 2026-10-15): it sets the company up and oversees it — every
    /// ticket, client and machine in view, clients imported — and it may COMMENT and @mention, but it never moves,
    /// edits, assigns, takes, opens, invoices or closes a ticket, emails the client from one, or answers a website
    /// chat. That is what keeps the one free login free: a company that ran its office through it would never pay
    /// for the seat the work needs. Ticking any other box beside Owner gives everything back, and takes a seat.</para>
    ///
    /// <para>The portal also asks this before ANY handler on a ticket page runs, as an allow-list
    /// (<c>Pages/Portal/TicketWorkAttribute</c>): a handler added later is refused to an owner-only login until
    /// somebody decides it is not ticket work.</para>
    /// </summary>
    public static bool CanWorkTickets(TenantRole role) => role.Has(TenantRoles.Paid);

    /// <summary>
    /// <see cref="Supervises"/> for TICKET work: a supervisor, or an owner who also does ticket work. An owner-only
    /// login supervises the company, not the jobs (<see cref="CanWorkTickets"/>).
    /// </summary>
    private static bool SupervisesWork(TenantRole role) => Supervises(role) && CanWorkTickets(role);

    /// <summary>What an owner-only login is told when it tries ticket work — the same words everywhere.</summary>
    public const string OwnerOnlyRefusal =
        "An owner login without a seat oversees the tickets and may comment, but does not work them. "
        + "Tick Supervisor, Technician or Office administrator beside Owner on the People page to work tickets.";

    /// <summary>
    /// The team, branding, settings and the whole portal. Supervisors and the owner — the
    /// administrator of before 2026-09-24, now possibly several people.
    /// </summary>
    public static bool CanManageCompany(TenantRole role) => Supervises(role);

    /// <summary>
    /// Make somebody an owner, or change an owner's row, remove one or lock one out. **Owners alone** (a company
    /// may have several since 2026-10-08). A supervisor runs the team but cannot demote, remove or lock
    /// out the people who own the company.
    /// </summary>
    public static bool CanChangeOwner(TenantRole role) => role.Has(TenantRole.Owner);

    /// <summary>
    /// Download the whole company's data — every client, ticket, photo and article in one zip (roadmap 4.9,
    /// 2026-10-22). A supervisor or the owner, an owner-only login included — Clint's answer: the people who manage
    /// the company. Not a technician or the office administrator alone: it is everything the company holds, at once.
    /// Asked by <see cref="CompanyExportService"/> as well as by the page's policy.
    /// </summary>
    public static bool CanExportCompany(TenantRole role) => Supervises(role);

    /// <summary>
    /// Agree to a higher monthly price — adding a person past the package's seats (2026-10-05).
    /// **Owners alone**, by Clint's decision: it is their money. A supervisor may still add people
    /// while there is a seat in the package; past it they are told the figure and to ask the owner.
    /// </summary>
    public static bool CanAcceptPrice(TenantRole role) => role.Has(TenantRole.Owner);

    // ---- tickets ----

    /// <summary>
    /// See every ticket in the company, rather than only your own and the unassigned pool.
    ///
    /// The owner supervises and the office administrator books work in and chases it; both need
    /// the whole board. A technician does not — see <see cref="TicketService.VisibleTo"/>, which
    /// is where this is actually applied.
    /// </summary>
    public static bool CanSeeAllTickets(TenantRole role) => Supervises(role) || role.Has(TenantRole.Billing);

    /// <summary>
    /// Move a ticket's status or priority.
    ///
    /// **The office administrator may not**, and this is the line Clint drew: they decide who does
    /// the job, the technician decides where it has got to. Somebody who has not touched the
    /// machine cannot declare the machine fixed.
    /// </summary>
    public static bool CanChangeStatus(TenantRole role) => SupervisesWork(role) || role.Has(TenantRole.Technician);

    /// <summary>
    /// Give a ticket to somebody, or take it off them.
    ///
    /// The office administrator can, at any time — in most shops they are the person who opens
    /// tickets and hands them out, and work has to be able to move when somebody is off sick.
    /// A technician cannot hand work to a colleague; they can only claim from the pool, which is
    /// <see cref="CanClaim"/>.
    /// </summary>
    public static bool CanAssignToAnyone(TenantRole role) => SupervisesWork(role) || role.Has(TenantRole.Billing);

    /// <summary>
    /// Take an unassigned ticket for yourself. Everybody who works tickets can.
    ///
    /// This is deliberately not the same question as <see cref="CanAssignToAnyone"/>. A technician
    /// claiming from the pool is picking up work nobody has been given; assigning is deciding what
    /// somebody else will do today, and that is not theirs.
    /// </summary>
    public static bool CanClaim(TenantRole role) => SupervisesWork(role) || role.Has(TenantRole.Technician);

    /// <summary>
    /// Open a ticket by hand. **Everybody.** In most shops it is the office administrator who takes
    /// the call and opens it — Clint, 2026-09-20. Not an owner-only login (<see cref="CanWorkTickets"/>).
    /// </summary>
    public static bool CanOpenTicket(TenantRole role) => CanWorkTickets(role);

    /// <summary>
    /// Add a note. Everybody, including the office administrator asking for an update — and an owner-only login,
    /// whose comment and @mention ("please call them back") is oversight, not doing the job (Clint, 2026-10-15).
    /// </summary>
    public static bool CanComment(TenantRole role) => true;

    /// <summary>
    /// Email a comment to the client from the ticket (2026-10-09). Everybody who may comment — the office
    /// administrator chasing a quote is as likely to update the client as the technician. Asked by
    /// ClientUpdateService, so a change here changes the console and the portal alike. Not an owner-only login: an
    /// email to the client speaks for the job (Clint, 2026-10-15).
    /// </summary>
    public static bool CanEmailClient(TenantRole role) => CanComment(role) && CanWorkTickets(role);

    /// <summary>
    /// See the board of who is carrying what: every technician, their open tickets, their load,
    /// and the unassigned pool beside them. The owner and the office administrator.
    /// </summary>
    public static bool CanSeeWorkload(TenantRole role) => Supervises(role) || role.Has(TenantRole.Billing);

    /// <summary>
    /// Hand a ticket that is yours to a colleague. **Everybody who works tickets** — Clint,
    /// 2026-09-24: "if that technician can't do it, they have the ability to reassign to someone
    /// else." Still not the same as <see cref="CanAssignToAnyone"/>: a technician may pass on their
    /// OWN work, never take work off a colleague.
    /// </summary>
    public static bool CanPassOn(TenantRole role) => CanClaim(role);

    /// <summary>
    /// Name who does a job while opening it. **Everybody** — Clint, 2026-09-24: "Anyone can book in
    /// the machine, the person who books it in assigns it to the relevant technician immediately."
    /// A technician at the counter booking a machine in for the workshop technician is ordinary.
    /// Whoever may open one (<see cref="CanOpenTicket"/>).
    /// </summary>
    public static bool CanAssignWhenOpening(TenantRole role) => CanOpenTicket(role);

    // ---- workflows (stage 3.7) ----

    /// <summary>
    /// Press a bench button on a job that is not assigned to you. Supervisors and the owner, who
    /// may step in anywhere; a technician moves only their own.
    /// </summary>
    public static bool CanStepInAnywhere(TenantRole role) => SupervisesWork(role);

    /// <summary>
    /// Work the billing queue: quotes, invoices, payments, and closing tickets. The office
    /// administrator, and the owner and supervisors — Clint, answer 15. **Never a technician**:
    /// "At the end of the day, only the Office Administrator should be able to close off tickets."
    /// </summary>
    public static bool CanWorkBilling(TenantRole role) => SupervisesWork(role) || role.Has(TenantRole.Billing);

    /// <summary>
    /// Switch outside help on or off — for the company, or for one machine (roadmap 3.9). A supervisor
    /// or the owner: it decides whether people outside the company can be let into the machines the
    /// company looks after, which is not a technician's call to make for everyone.
    /// </summary>
    public static bool CanAllowOutsiders(TenantRole role) => Supervises(role);

    /// <summary>
    /// Choose which ticket categories and steps the company uses (Job types, 2026-10-27). A supervisor or the owner —
    /// the owner-only login too: it decides how everybody's jobs run, which is setting the company up, not working tickets.
    /// </summary>
    public static bool CanSetJobTypes(TenantRole role) => Supervises(role);

    /// <summary>
    /// See who has a machine's unattended password remembered, and clear anybody's (2026-10-06). A
    /// supervisor or the owner — Clint: useful when somebody leaves or a password may have got out.
    /// Names only: nobody, these included, ever sees the password. Forgetting one's OWN needs nothing.
    /// </summary>
    public static bool CanClearSavedPasswords(TenantRole role) => Supervises(role);

    /// <summary>
    /// Remove a machine from the company's list, with a reason (2026-10-06): a supervisor or the owner — Clint's
    /// answer. It cuts a customer's machine off for good (and uninstalls it if it is online), so it sits with the
    /// people who manage the company, like outside help. The free version's one person is its owner.
    /// </summary>
    public static bool CanRemoveDevices(TenantRole role) => Supervises(role);

    /// <summary>
    /// Say a reinstalled machine is the same machine (2026-10-07), which lets unattended access work on it
    /// again. A supervisor or the owner — Clint's answer: the customer's own tick cannot be enough, because
    /// whoever presents the fingerprint holds that tick too. The free version's one person is its owner.
    /// </summary>
    public static bool CanConfirmReinstall(TenantRole role) => Supervises(role);

    /// <summary>
    /// Told — a pop-up and a badge — when a job lands in the billing queue (Clint, 2026-10-05: "all
    /// office administrators should be notified"). Every office administrator; in a company with
    /// none, the supervisors and the owner, because the queue falls to them then (answer 16). Not
    /// both: a supervisor with an office administrator beside them would only be told about work
    /// that is somebody else's.
    /// </summary>
    public static bool HearsOfBilling(TenantRole role, bool companyHasOfficeAdmin) =>
        companyHasOfficeAdmin ? role.Has(TenantRole.Billing) : CanWorkBilling(role);

    /// <summary>
    /// Close a ticket that has a workflow. The same people as <see cref="CanWorkBilling"/>, said
    /// separately because it is the question a reader will come looking for.
    /// </summary>
    public static bool CanCloseWorkflowTicket(TenantRole role) => CanWorkBilling(role);

    /// <summary>Approve a technician's "do not invoice". A supervisor or the owner — answer 14 — when they work tickets.</summary>
    public static bool CanApprove(TenantRole role) => SupervisesWork(role);

    /// <summary>
    /// Delete a session recording (1.6). A supervisor or the owner: a recording is part of a ticket's
    /// record of what was done on a customer's machine, and removing it is not a technician's call.
    /// Watching one is anybody's who may see its ticket.
    /// </summary>
    public static bool CanDeleteRecordings(TenantRole role) => Supervises(role);

    /// <summary>Write a Work done entry. The people who touch machines — not the office administrator.</summary>
    public static bool CanAddWorkDone(TenantRole role) => CanChangeStatus(role);

    /// <summary>
    /// Rename and merge the company's list of quotable items. A supervisor, the owner, or the office
    /// administrator — answer 6. Anybody may ADD an item while quoting; tidying the list is theirs.
    /// Setting the company up, so an owner-only login too.
    /// </summary>
    public static bool CanEditItems(TenantRole role) => Supervises(role) || role.Has(TenantRole.Billing);

    /// <summary>
    /// Add, change and remove suppliers, and set what every quote request says about delivery. The
    /// people who request quotes — the office administrator, a supervisor, the owner (2026-09-29).
    /// Setting the company up, so an owner-only login too.
    /// </summary>
    public static bool CanEditSuppliers(TenantRole role) => Supervises(role) || role.Has(TenantRole.Billing);

    // ---- the knowledge base (3.8.5) ----

    /// <summary>
    /// Read, write and edit the knowledge base. **Everybody** — Clint, 2026-10-05: "an option on all
    /// user's dashboards to ... access and edit/create in the knowledgebase". Every save is kept, so
    /// an edit that goes wrong can be put back (<see cref="KnowledgeBaseService.RestoreAsync"/>).
    /// </summary>
    public static bool CanWriteKnowledge(TenantRole role) => true;

    /// <summary>
    /// Delete an article. A supervisor or the owner: everybody may edit, and that is safe because
    /// every version is kept — a deletion is the one thing an edit cannot undo.
    /// </summary>
    public static bool CanDeleteKnowledge(TenantRole role) => Supervises(role);

    /// <summary>
    /// Write a ticket up as an article from the ticket itself, besides the people on it. Supervisors
    /// and the owner, who may step in on any job; see <see cref="KnowledgeBaseService.Offered"/>.
    /// </summary>
    public static bool CanWriteUpAnyTicket(TenantRole role) => Supervises(role);

    // ---- website chat ----

    /// <summary>
    /// Hand a website chat somebody ELSE is answering to another person (2026-10-15). Supervisors and the
    /// owner. Whoever is answering may always hand over their own, and anybody may give one nobody has
    /// answered yet to a named person — see <see cref="WebChatService.HandOverAsync"/>.
    /// </summary>
    public static bool CanHandOverAnyWebChat(TenantRole role) => SupervisesWork(role);

    /// <summary>
    /// Answer a website chat, be rung for one, and count as somebody here to answer (<see cref="StaffPresence"/>).
    /// Everybody who works tickets; not an owner-only login (2026-10-15) — a visitor must never wait for somebody
    /// who may not reply.
    /// </summary>
    public static bool CanAnswerWebChat(TenantRole role) => CanWorkTickets(role);

    // ---- clients ----

    /// <summary>
    /// Bring a whole client list in from another program. The owner, a supervisor or the office
    /// administrator — Clint, 2026-09-28. Not a technician: a thousand records arriving at once is
    /// the office's decision, and the one-at-a-time Add a client form stays everybody's.
    /// </summary>
    public static bool CanImportClients(TenantRole role) => Supervises(role) || role.Has(TenantRole.Billing);

    /// <summary>
    /// Remove a client — a duplicate or a misspelling, its work moved onto the right one (Clint, 2026-10-27:
    /// "one of my staff created a client with the incorrect spelling and the proper client already exists").
    /// The same people as importing: it cannot be undone, and moving a customer's whole history is the
    /// office's decision. A technician who made the mistake asks them.
    /// </summary>
    public static bool CanRemoveClients(TenantRole role) => CanImportClients(role);

    // ---- machines ----

    /// <summary>
    /// See the list of machines, and say which customer or person each one belongs to.
    /// **Everybody**, and it is the console's job as much as the portal's — Clint, 2026-09-20:
    /// *"devices should be an option that everybody has, otherwise it's only the owner/supervisor
    /// that can link them to a client or contact."*
    ///
    /// The reason this is not a privilege: until a machine is linked, its tickets never reach a
    /// client page. So an unlinked machine is a hole in the records, and the person who notices
    /// it is whoever is looking at the list — usually a technician about to connect to it. Making
    /// them fetch the owner to tidy a record is how the strays pile up.
    ///
    /// Nothing is disclosed by it that the same person cannot already see: every role reaches
    /// <c>/portal/clients</c>, and a technician has always seen every machine in the console's
    /// own device list. This closes the gap between those two facts rather than opening anything.
    /// </summary>
    public static bool CanLinkDevices(TenantRole role) => true;

    /// <summary>
    /// Whether this person may download the technician console from the technicians' link
    /// (/tech/{slug}, 2026-09-30). Everybody who can sign in to the console — a seat — and the
    /// owner and supervisors besides, who hand it out. Not an office administrator alone: the
    /// console would refuse them (/api/login), and a download that cannot be used is a support call.
    /// </summary>
    public static bool CanDownloadConsole(TenantRole role) => role.Has(TenantRoles.Seated) || Supervises(role);

    /// <summary>
    /// Whether this person may say which customers and machines get the monthly health check.
    ///
    /// Everybody, for now, by the same reasoning as linking: Clint asked for it beside "link a
    /// client" on the same right-click, and the person who knows a customer has just signed an SLA
    /// is as often a technician as the owner. It is a QUESTION rather than an answer written into
    /// the callers so that the day it becomes the owner's alone — it is a commercial decision — the
    /// change is here and nowhere else.
    /// </summary>
    public static bool CanSetHealthCheck(TenantRole role) => true;

    /// <summary>
    /// A short sentence for the person being refused, so a disabled control can say why rather
    /// than simply not working. Empty when they are allowed.
    /// </summary>
    public static string WhyNotStatus(TenantRole role) =>
        CanChangeStatus(role) ? ""
            : "Only the technician working a ticket, or the owner, can change its status. Add a note to ask for an update.";
}
