using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RemoteSupport.Platform.Data;

/// <summary>One IT company using the platform.</summary>
public sealed class Tenant
{
    public int Id { get; set; }
    /// <summary>URL-safe short name, unique (e.g. "acme-it").</summary>
    public string Slug { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string ContactNumber { get; set; } = "";
    public string Country { get; set; } = "";
    /// <summary>What the tenant's customers see as the product name (defaults to the tenant's brand).</summary>
    public string ProductName { get; set; } = "Remote Support";
    public string SupportEmail { get; set; } = "";
    public string SupportPhone { get; set; } = "";
    public string PrimaryColor { get; set; } = "#1F4E79";
    public string AccentColor { get; set; } = "#2E9BFF";
    public string WelcomeText { get; set; } = "Need help? Click Request Support and one of our technicians will connect with you.";

    /// <summary>
    /// Appearance, chosen per app in the tenant's portal: "light" or "dark". Two settings rather
    /// than one because the console is a tool their staff live in and the client app is how they
    /// present themselves to a customer — those are different decisions. On dark, the signature
    /// colour is the tenant's own <see cref="AccentColor"/>; we never push our colours into a
    /// reseller's apps.
    ///
    /// Both default to light, so every existing tenant keeps exactly what they have today.
    /// </summary>
    public string TechnicianTheme { get; set; } = "light";
    public string ClientTheme { get; set; } = "light";
    /// <summary>File name of the uploaded logo under data/logos, or empty.</summary>
    public string LogoFile { get; set; } = "";
    /// <summary>Secret baked into the tenant's installers; devices present it in hello.</summary>
    public string TenantKey { get; set; } = "";
    /// <summary>
    /// Seats IN THE PACKAGE PRICE (2026-10-05; five since 2026-10-08). Not the cap: that is <see cref="MostSeats"/>,
    /// which only we raise. Five for everyone, unless the platform owner has set fewer for a company.
    /// </summary>
    public int Seats { get; set; } = Services.Pricing.IncludedSeats;
    /// <summary>
    /// The most seats this company may have, when the platform owner has allowed it more than its package (2026-10-25,
    /// Clint: "they can request more seats, and if I decide it's fine ... allow more seats"). 0 = the package and no more,
    /// which is every company until we say otherwise. Each seat past the package is the region's extra-seat price, agreed
    /// by the owner first (<see cref="Services.Pricing.MostSeatsFor"/>, <see cref="SeatAgreement"/>).
    /// </summary>
    public int MostSeats { get; set; }
    /// <summary>
    /// The Basic version's computers, paid for in all (2026-10-25, Clint: extra machines at R10 / $1 / £1 / €1 each, set by the owner
    /// with no approval from us, at most 20). 0 = the three in the price. Read it through <see cref="RemoteSupport.Shared.FreePlan.MachinesFor"/>;
    /// changed only by <see cref="Services.TenantService.SetBasicMachinesAsync"/>, which asks the owner to agree a rise first.
    /// </summary>
    public int BasicMachines { get; set; }
    /// <summary>What the owner last asked for on Billing (<see cref="Services.TenantService.AskForSeatsAsync"/>): the seats in all, 0 = nothing asked.</summary>
    public int SeatsAskedFor { get; set; }
    public DateTime? SeatsAskedUtc { get; set; }
    /// <summary>Their reason, in their words, and who asked — for Owner → Approvals.</summary>
    public string SeatsAskedNote { get; set; } = "";
    public string SeatsAskedBy { get; set; } = "";
    /// <summary>
    /// Where the company is billed, for as long as it is a customer: ZA, US, CA, UK or EU
    /// (<see cref="Services.Pricing.Regions"/>). Filled from <see cref="Country"/> for companies that
    /// signed up before it was asked.
    /// </summary>
    public string Region { get; set; } = "ZA";
    /// <summary>
    /// The company's own time zone, by IANA name (2026-10-05): every time its people see on the portal,
    /// and every time they type, is in it (<see cref="Services.CompanyClock"/>) — never the server's,
    /// which is UTC on the live server. Set from the region at signup; changed on Branding.
    /// </summary>
    public string TimeZone { get; set; } = Services.CompanyClock.SouthAfrica;
    /// <summary>The platform's own company (Anywhere IT), or another the platform owner does not bill: no price, and nothing to agree to.</summary>
    public bool NotBilled { get; set; }
    /// <summary>
    /// A pilot company (2026-10-15, Clint): its five seats cost nothing while it is a pilot. Signed up from a pilot link
    /// (<see cref="Services.PilotService"/>); Active at once. <see cref="Services.Pricing.BillFor"/> is where it counts.
    /// Ended by notice (2026-10-22): <see cref="PilotEndsUtc"/>, then false.
    /// </summary>
    public bool Pilot { get; set; }
    /// <summary>When a pilot that was given notice stops being one (<see cref="Services.PilotService.GiveNoticeAsync"/>); null while no notice is given.</summary>
    public DateTime? PilotEndsUtc { get; set; }
    /// <summary>What happens when the pilot ends: <c>"close"</c> (suspended, then closed 30 days later) or <c>"pay"</c> (the standard price, 14 days to add a card). Kept after it ends.</summary>
    public string PilotAfter { get; set; } = "";
    /// <summary>When the pilot ended — for <c>"close"</c>, the suspension the closing counts from.</summary>
    public DateTime? PilotEndedUtc { get; set; }
    public bool RequireConsent { get; set; } = true;
    public bool UnattendedRequireConsent { get; set; } = false;
    public int ConsentTimeoutSeconds { get; set; } = 60;
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    /// <summary>
    /// "free" is the Free version (2026-10-06, back 2026-10-25), "basic" the Basic version (§163) — ask <c>FreePlan.Is</c> / <c>IsFree</c> / <c>IsBasic</c>, never compare the string.
    /// Anything else, "five-user" included (all it ever said before), is the full version.
    /// </summary>
    public string Plan { get; set; } = "five-user";
    public string BillingNotes { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedUtc { get; set; }

    // ---- reviews (2026-10-14, Clint: asked a week in, "later" puts it off a month, then once more with an opt-out) ----
    /// <summary>When the owner is next asked for a review; null = a week after approval (ReviewService.AskFrom).</summary>
    public DateTime? ReviewAskAfterUtc { get; set; }
    /// <summary>How many times the owner has said "later" — from the second ask on, "don't ask again" is offered.</summary>
    public int ReviewLaters { get; set; }
    public bool ReviewOptedOut { get; set; }
    public DateTime? TrialEndsUtc { get; set; }

    /// <summary>
    /// Which trial notices have already gone out, as comma-separated milestones: "7", "1" and "0"
    /// (the day it lapses). Stored rather than derived so a server restart, or two servers, cannot
    /// send the same warning twice — and kept readable in the database on purpose, because the
    /// question being asked when someone looks is always "did it actually send, and which one".
    /// </summary>
    public string TrialNoticesSent { get; set; } = "";
    public DateTime? LastTrialNoticeUtc { get; set; }

    /// <summary>
    /// Days left on the trial, 0 on the last day and negative once it has lapsed. Null when not
    /// on trial.
    ///
    /// CEILING, not floor, and it matters twice. Somebody with 7 days and 20 hours left says
    /// their trial ends in 8 days, and floor called that 7 — so the seven-day warning went out a
    /// day early and, worse, disagreed with the number the portal was showing them at the same
    /// moment, because StatusText had always used ceiling. Floor also turned the last twelve
    /// hours into 0, which sent the "your trial has ended" notice while it was still running.
    /// Both were caught by tools/trialnoticecheck. Everything that counts trial days now comes
    /// through here, so the two cannot drift apart again.
    /// </summary>
    [NotMapped]
    public int? TrialDaysLeft => Status == TenantStatus.Trial && TrialEndsUtc.HasValue
        ? (int)Math.Ceiling((TrialEndsUtc.Value - DateTime.UtcNow).TotalDays)
        : null;

    // ---- monthly health check ----
    // Set once by the IT company and baked into the client installer, so a technician never types
    // these on a customer machine. See docs/PLAN-HEALTHCHECK.md.

    /// <summary>
    /// The company's master switch for the monthly health check. Since 2026-09-28 it no longer means
    /// "every install gets it": which machines do is the customer's tick and the machine's override,
    /// and this only has to be on (with the mail finished — <see cref="HealthCheckReady"/>) for
    /// any of them to.
    /// </summary>
    public bool HealthCheckEnabled { get; set; }
    /// <summary>Where the monthly report is sent. The IT company's own address, not the end client's.</summary>
    public string HealthCheckEmailTo { get; set; } = "";
    /// <summary>
    /// Day of the month the task runs. 0 keeps the toolkit's own behaviour: the day is chosen from
    /// the weekday the machine was installed, which spreads a client base across five days by itself
    /// instead of every machine reporting on the 1st.
    /// </summary>
    public int HealthCheckDayOfMonth { get; set; }
    /// <summary>
    /// Run maintenance (restore point, temp files, TRIM or defragment, sfc) before the read-only
    /// check. Off means inspect only and change nothing. Maintenance never runs without a restore
    /// point, so it always skips on Windows Server, which has no System Restore.
    /// </summary>
    public bool HealthCheckRunMaintenance { get; set; } = true;

    // ---- outgoing mail, held once per tenant ----
    // The standalone toolkit stored these on every client machine because it had no server. This
    // platform has one, so the endpoint uploads its result and the server sends the mail: the
    // password exists in one place, and changing it is one edit here rather than a reinstall
    // everywhere. Nothing below is ever written into brand.json.

    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    /// <summary>
    /// Encrypted with ASP.NET Data Protection, like workshop device passwords. A copy of the
    /// database is not a copy of the password - but losing data/keys loses it (handover §10).
    /// </summary>
    public string SmtpPasswordEnc { get; set; } = "";
    /// <summary>Address the report is sent from. Falls back to SmtpUser when blank.</summary>
    public string SmtpFrom { get; set; } = "";
    public bool SmtpUseSsl { get; set; } = true;

    // ---- quote requests (2026-09-29), set on the Suppliers page ----

    /// <summary>
    /// What goes in To when several suppliers are asked at once: they go in BCC so none sees the
    /// others, and a mail with an empty To is treated as spam. The company's own address — for
    /// Anywhere IT, accounts@. Blank falls back to <see cref="SupportEmail"/>.
    /// </summary>
    public string QuoteToAddress { get; set; } = "";
    /// <summary>Where parts are delivered, written into every quote request with a question about the delivery fee.</summary>
    public string QuoteDeliveryAddress { get; set; } = "";

    // ---- support in the browser (3.8.2, 2026-09-30) ----
    // Clint: "they must be able to switch it off and limit it to business hours they can set for
    // it, as sometimes they may have longer support hours than office hours." Off by default: it is
    // a public page that rings the technicians, so a company turns it on when it wants it.

    public bool WebSupportEnabled { get; set; }

    // Outside help (roadmap 3.9, 2026-10-06): may the person at one of this company's machines give a code to
    // somebody OUTSIDE it — an accounting firm, another company on the platform? Off by default (Clint);
    // a machine can differ either way with Device.OutsiderMode, and OutsiderAccess.Allowed decides.
    public bool OutsidersAllowed { get; set; }
    /// <summary>
    /// May a technician IN a session bring somebody from outside into it with a code (2026-10-22, §155)? On by
    /// default — the company's own technician is there and lets them in. A supervisor or the owner may switch it
    /// off; off stops every such code and removes anybody let in that way. Not the customer's switch above.
    /// </summary>
    public bool TechnicianInvitesAllowed { get; set; } = true;

    // ---- Job types (2026-10-27, HANDOVER §166): which categories and steps this company uses ----
    /// <summary>The <c>JobTypes</c> switches this company has OFF, comma-separated; empty is everything on. Stored as what
    /// is off so a switch added later is on for everybody. Only <c>TenantService.SetJobTypesAsync</c> writes it, after
    /// <c>JobTypes.Problem</c> has passed it. Every new ticket copies it (<see cref="Ticket.RouteOff"/>).</summary>
    public string JobTypesOff { get; set; } = "";
    /// <summary>When somebody last saved the Job types page, and who — null until anybody has.</summary>
    public DateTime? JobTypesChosenUtc { get; set; }
    public string JobTypesChosenBy { get; set; } = "";
    /// <summary>When the dashboard first sent an owner to the Job types page — null until it has. Set BY that redirect, so
    /// it happens once whether or not they press anything there (2026-10-28: it sent them back on every Dashboard click
    /// until Save or Keep everything).</summary>
    public DateTime? JobTypesShownUtc { get; set; }
    /// <summary>Seven entries, Monday first, each "08:00-17:00" or empty for closed, joined by ";".
    /// <see cref="Services.WebSupportHours"/> is the only thing that reads or writes it.</summary>
    public string WebSupportHours { get; set; } = "08:00-17:00;08:00-17:00;08:00-17:00;08:00-17:00;08:00-17:00;;";
    /// <summary>An IANA time zone name — "Africa/Johannesburg" — which works on Windows and on Linux alike.</summary>
    public string WebSupportTimeZone { get; set; } = "Africa/Johannesburg";

    // ---- website chat (3.8.3, 2026-09-30) ----
    // Clint: "a chat window for a tenants website ... when one chats in it goes through to every
    // logged in technician including supervisors and office administrators." Off by default, like
    // browser support, and with hours of its own in the same form — WebSupportHours reads both.

    public bool WebChatEnabled { get; set; }
    public string WebChatHours { get; set; } = "08:00-17:00;08:00-17:00;08:00-17:00;08:00-17:00;08:00-17:00;;";
    public string WebChatTimeZone { get; set; } = "Africa/Johannesburg";

    /// <summary>Everything the server needs before it can actually send a monthly report.</summary>
    [NotMapped]
    public bool HealthCheckReady =>
        HealthCheckEnabled && !string.IsNullOrWhiteSpace(HealthCheckEmailTo) && !string.IsNullOrWhiteSpace(SmtpHost);

    public List<Technician> Technicians { get; set; } = new();
    public List<Device> Devices { get; set; } = new();

    /// <summary>A South African company — its phone numbers are repaired as South African ones on import.</summary>
    public bool IsSouthAfrica => Region == "ZA";
    /// <summary>
    /// The month's price with the people in seats now — needs <see cref="Technicians"/> loaded.
    /// "Not billed" for the platform's own company.
    /// </summary>
    // A pilot's package is free; seats we allowed past it are not, and are invoiced (2026-10-25). The label says when a pilot ends.
    [NotMapped] public string PriceLabel => NotBilled ? "Not billed"
        : Pilot ? (Services.Pricing.BillFor(this, Services.Pricing.PeopleInSeats(Technicians)) is { Extra: > 0 } pb ? $"Pilot · {pb.Total} / month invoiced" : "Pilot · free")
            + (PilotEndsUtc is { } e ? $" until {e:yyyy-MM-dd}" : "")
        : Services.Pricing.BillFor(this, Services.Pricing.PeopleInSeats(Technicians)).Total + " / month";
    public bool TrialExpired => Status == TenantStatus.Trial && TrialEndsUtc.HasValue && TrialEndsUtc.Value < DateTime.UtcNow;
    /// <summary>Can technicians and devices use the service right now?</summary>
    public bool IsServiceEnabled => Status == TenantStatus.Active || (Status == TenantStatus.Trial && !TrialExpired);
}

/// <summary>Pending = signed up, waiting for owner approval. Trial = approved, 14-day trial running. Active = paying.</summary>
public enum TenantStatus { Active, Suspended, Trial, Closed, Pending, Rejected }

/// <summary>
/// What a person may do — a SET of tick boxes since 2026-09-29 (roadmap 3.6.8), not one role. A
/// one-man business is owner, technician and office administrator at once, and "technician and
/// office administrator but not owner" is ordinary in a small shop. What somebody may do is the
/// UNION of what their boxes allow; <c>Permissions</c> is where each answer is worked out.
///
/// <para><b>Admin is the SUPERVISOR and Billing the OFFICE ADMINISTRATOR</b> (2026-09-24). The old
/// names stay so every caller keeps compiling; only the labels say otherwise.</para>
///
/// <para><b>The numbers changed</b> with the migration <c>RolesAsTickBoxes</c>: they were 0–4, one
/// per role, and a set of flags cannot have a box worth zero. 0 now means "no box ticked", which is
/// never stored. <see cref="OwnerSupervisor"/> is no longer a role of its own but the two boxes
/// together, kept as a name because it is what the owner who works in the console was called.</para>
///
/// <para><b>Owners: at least one, possibly several (since 2026-10-08).</b> ONE owner-only login uses no
/// seat — Clint: "the owner role must not take away from any of the seats unless they select
/// owner/supervisor" — and ticking another box beside it is what buys the seat; every further owner-only
/// login is a seat (<c>Pricing.SeatsFor</c>). Only an owner makes, changes or removes an owner, and
/// <c>TenantService</c> never lets the last one go.</para>
/// </summary>
[Flags]
public enum TenantRole
{
    None = 0,
    Technician = 1,
    /// <summary>The supervisor.</summary>
    Admin = 2,
    /// <summary>The office administrator.</summary>
    Billing = 4,
    Owner = 8,
    OwnerSupervisor = Owner | Admin
}

/// <summary>
/// Role sets that can be asked of the DATABASE, which the properties on <see cref="Technician"/>
/// cannot. They are masks: a person is in the set when ANY of their boxes is in the mask, which a
/// query says as <c>(t.Role &amp; TenantRoles.Seated) != 0</c>. An array and <c>Contains</c> would
/// compare the whole number and miss every combination.
/// </summary>
public static class TenantRoles
{
    /// <summary>Sit in the desktop console.</summary>
    public const TenantRole Seated = TenantRole.Technician | TenantRole.Admin;
    /// <summary>
    /// Take one of the company's paid seats (2026-10-05, Clint: every role costs a seat, the owner
    /// alone is free). Not the same as <see cref="Seated"/>: an office administrator pays for a
    /// seat and still never reaches the console.
    /// </summary>
    public const TenantRole Paid = TenantRole.Technician | TenantRole.Admin | TenantRole.Billing;
    public const TenantRole Owners = TenantRole.Owner;
    /// <summary>Every box that exists; anything outside it in a stored number is not a role.</summary>
    public const TenantRole All = TenantRole.Technician | TenantRole.Admin | TenantRole.Billing | TenantRole.Owner;

    /// <summary>Whether <paramref name="r"/> holds ANY of the boxes in <paramref name="any"/>.</summary>
    public static bool Has(this TenantRole r, TenantRole any) => (r & any) != 0;

    /// <summary>The boxes in the order a person reads them, with the words used on screen.</summary>
    public static readonly (TenantRole box, string label)[] Boxes =
    {
        (TenantRole.Owner, "Owner"),
        (TenantRole.Admin, "Supervisor"),
        (TenantRole.Technician, "Technician"),
        (TenantRole.Billing, "Office administrator")
    };

    /// <summary>"Owner, Supervisor" — every ticked box, in reading order.</summary>
    public static string Label(TenantRole r)
    {
        var s = string.Join(", ", Boxes.Where(b => r.Has(b.box)).Select(b => b.label));
        return s.Length > 0 ? s : "No role";
    }
}

public sealed class Technician
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    /// <summary>Login identity; unique across the platform.</summary>
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    /// <summary>The boxes this person holds — see <see cref="TenantRole"/>. Never <c>None</c>.</summary>
    public TenantRole Role { get; set; } = TenantRole.Technician;

    /// <summary>
    /// This person's own light or dark, for the portal AND the console: "light", "dark", or empty
    /// to follow whatever the company chose.
    ///
    /// ONE setting rather than one per surface. A person who prefers a dark screen prefers it in
    /// both places, and two dropdowns would be two decisions where nobody has two preferences.
    ///
    /// It belongs to the PERSON and not to the machine, which is why it is a column here rather
    /// than a file beside the console: a technician who sets it at their desk finds it already set
    /// on the workshop PC. Empty is the default, so nobody's appearance changes until they ask.
    ///
    /// The company's own <see cref="Tenant.TechnicianTheme"/> stays and is still the fallback —
    /// what changed is that it is no longer the last word. <see cref="Tenant.ClientTheme"/> is
    /// untouched and deliberately so: how an IT company presents itself to its customers is
    /// branding, not a preference of the person at the desk.
    /// </summary>
    public string Appearance { get; set; } = "";

    /// <summary>
    /// Which mail this person uses — desktop, gmail or outlook-web (<c>MailClients</c>) — or empty
    /// until they are asked, the first time they request a quote. Decides whether Request quote
    /// hands them a draft file or opens their web mail's compose window.
    /// </summary>
    public string MailClient { get; set; } = "";
    /// <summary>The quote signature this person signed with last — ticked for them next time when
    /// the company has several (Clint, 2026-10-06).</summary>
    public int? LastQuoteSignatureId { get; set; }
    /// <summary>When this person last looked at Latest activity (2026-10-26, HANDOVER §164): what came after is "new" and
    /// counted on their badge. Null until they first look — the badge then counts the last day.</summary>
    public DateTime? ActivitySeenUtc { get; set; }
    /// <summary>Latest activity on only their own tickets rather than All — their choice, remembered, and what the badge counts.</summary>
    public bool ActivityMine { get; set; }

    public bool Disabled { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }

    // ---- signing in safely (2026-10-01, before go-live; HANDOVER §72) ----

    /// <summary>
    /// Changes whenever what this person may do changes — password, boxes, two-factor, or a
    /// "sign out everywhere". Every portal cookie and console token carries the stamp it was issued
    /// with, and one that no longer matches is signed out (<see cref="Services.AuthService"/>).
    /// Before this, removing a technician left their console in until the server restarted and
    /// their portal cookie renewing itself for as long as they kept using it.
    /// </summary>
    public string SecurityStamp { get; set; } = "";

    /// <summary>The authenticator app's secret, encrypted with Data Protection. Empty until they set it up.</summary>
    public string TotpSecretEnc { get; set; } = "";
    public DateTime? TotpEnabledUtc { get; set; }
    /// <summary>The last 30-second step a code was accepted for, so one code can never be used twice.</summary>
    public long TotpLastStep { get; set; }
    /// <summary>SHA-256 of each unused recovery code, comma separated. One is crossed off as it is used.</summary>
    public string RecoveryCodeHashes { get; set; } = "";
    /// <summary>
    /// Where this person has signed in from before (network prefixes, most recent first), so a
    /// sign-in from anywhere new can be mailed to them. Never shown; only compared.
    /// </summary>
    public string SignInPlaces { get; set; } = "";

    // ---- one sign-in at a time (2026-10-05, Clint; HANDOVER §82) ----

    /// <summary>
    /// The one browser this person is signed in on: a random id, carried by that browser's cookie
    /// as "b:{id}". A sign-in on another browser replaces it, and the old cookie is refused on its
    /// next page. Empty when nobody has signed in on a browser since this arrived, or after Sign out.
    /// </summary>
    public string BrowserSignIn { get; set; } = "";
    /// <summary>Which browser holds <see cref="BrowserSignIn"/> — its <c>rsp_device</c> cookie — so signing in again on the SAME browser is not "somewhere else".</summary>
    public string BrowserDevice { get; set; } = "";
    /// <summary>
    /// Part of every "skip the code for 7 days" token (<see cref="Services.AuthService.IssueTrust"/>).
    /// Moved when a sign-in on another machine pushes this person off the first, so every machine
    /// they trusted asks for the code again — in case the second sign-in was somebody else. Kept
    /// apart from <see cref="SecurityStamp"/> because moving that would sign out BOTH the console
    /// and the browser, and the rule is one of each.
    /// </summary>
    public string TrustStamp { get; set; } = "";

    [NotMapped] public bool HasTwoFactor => TotpEnabledUtc != null && TotpSecretEnc.Length > 0;
    /// <summary>
    /// Invited and not yet set up: no password exists until they choose one from their invitation
    /// (<see cref="AccountToken"/>). Nobody — not the supervisor who added them — ever knows it.
    /// </summary>
    [NotMapped] public bool AwaitingSetup => Hash.Length == 0;

    /// <summary>A supervisor or the owner: the whole portal and the company's settings.</summary>
    [NotMapped] public bool IsAdmin => Services.Permissions.CanManageCompany(Role);
    [NotMapped] public bool IsOwner => Role.Has(TenantRoles.Owners);
    /// <summary>Technicians and supervisors sign in to the console — and an owner who is also one.</summary>
    [NotMapped] public bool CanUseConsole => Role.Has(TenantRoles.Seated);
    [NotMapped] public bool CanUsePortal => Role.Has(TenantRoles.All & ~TenantRole.Technician);
    /// <summary>
    /// One of the company's paid seats (<see cref="Services.Pricing"/>). Everybody takes one except
    /// an owner-only login — office administrators too, since 2026-10-05 (they were free before). This is
    /// what a person's OWN boxes say; a second owner-only login also takes a seat (2026-10-08), which only
    /// the whole list can tell — count seats with <see cref="Services.Pricing.PeopleInSeats"/>, never with this.
    /// Three boxes on one person is still one seat.
    /// </summary>
    [NotMapped] public bool UsesSeat => Role.Has(TenantRoles.Paid);
    [NotMapped] public string RoleLabel => TenantRoles.Label(Role);
}

public enum TicketStatus { Open = 0, InProgress = 1, OnHold = 2, Resolved = 3, Closed = 4 }
public enum TicketPriority { Low = 0, Normal = 1, High = 2, Urgent = 3 }
/// <summary>Email and Form (2026-10-05, 3.8.4): a ticket a customer opened by writing to the
/// company's support address, or through the contact form on its website.</summary>
public enum TicketSource { Manual = 0, Call = 1, Chat = 2, Unattended = 3, Email = 4, Form = 5,
    /// <summary>Opened by a recurring task's schedule on the morning it fell due (2026-10-07).</summary>
    Recurring = 6 }
/// <summary>
/// The kind of job. Each category shows its own extra fields on the ticket form.
///
/// <para>General is the DEFAULT — the job that is simply a job — and it is numbered 4 rather than
/// 0 because 0 has meant Remote support in every row ever written. Do not renumber these: the
/// number is what is in the database. Display order lives in <c>TicketSummary.CategoryOrder</c>.</para>
/// </summary>
public enum TicketCategory { RemoteSupport = 0, WalkIn = 1, Workshop = 2, CallOut = 3, General = 4,
    /// <summary>A ticket a <see cref="RecurringTask"/> opened (2026-10-07). Offered last; what is set up is the schedule.</summary>
    Recurring = 5 }

/// <summary>A customer of the IT company. Tickets link here so work and history group per client.</summary>
public sealed class Client : ICompanyNumbered
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Its number within the company: 1, 2, 3 (2026-10-23, HANDOVER §159) — what the portal's addresses name,
    /// never <see cref="Id"/>, which one sequence gives every company. Given on save (<see cref="PlatformDb"/>).</summary>
    public int Number { get; set; }
    /// <summary>Company or person the work is billed to.</summary>
    public string Name { get; set; } = "";

    // ---- the original single contact ----
    // Kept, and still written, for two reasons. It is what the Display type-ahead searches, and
    // migration 20260920 copied each non-empty one into the client's first Contact rather than
    // moving it — so anything that still reads these columns keeps working while the portal moves
    // over to Contacts. New work should read Contacts. See docs/NEXT-SESSION-DASHBOARD.md stage A.
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Archived { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Every machine of this customer gets the monthly health check — an SLA client, or one the IT
    /// company has chosen. Off by default: not every customer is paying for it (Clint, 2026-09-28).
    /// A machine can differ from this with <see cref="Device.HealthCheckMode"/>; what it all comes
    /// to is decided in one place, <c>HealthCheckModes.Wanted</c>.
    /// </summary>
    public bool HealthCheck { get; set; }

    /// <summary>What the type-ahead shows so staff can tell similar names apart.</summary>
    [NotMapped]
    public string Display
    {
        get
        {
            var bits = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(ContactName)) bits.Add(ContactName);
            if (!string.IsNullOrWhiteSpace(Phone)) bits.Add(Phone);
            return string.Join(" · ", bits);
        }
    }
}

/// <summary>
/// A named person at a client. A client may be one person or a company with thirty, and the two
/// used to be the same row — <see cref="Client.ContactName"/> held exactly one name, so the second
/// person at a company had nowhere to live.
///
/// A contact always belongs to a client; there is no such thing as a contact floating on its own.
/// A machine may hang off a contact (one person's PC) or off the client directly (a server, or a
/// shared machine at reception) — see <see cref="Device.ContactId"/>.
/// </summary>
public sealed class Contact
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    /// <summary>Their job or where they sit — "Accounts", "Reception", "Workshop floor".</summary>
    public string Role { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Archived rather than deleted: a contact who has left is still the person who was
    /// on a job last March, and deleting them would empty that ticket's name.</summary>
    public bool Archived { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public string Display
    {
        get
        {
            var bits = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(Role)) bits.Add(Role);
            if (!string.IsNullOrWhiteSpace(Phone)) bits.Add(Phone);
            return string.Join(" · ", bits);
        }
    }
}

public sealed class Ticket
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Per-tenant sequential number shown to technicians (#1, #2, …).</summary>
    public int Number { get; set; }
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;
    public TicketSource Source { get; set; } = TicketSource.Manual;
    /// <summary>
    /// What kind of job this is. General unless something says otherwise — a session or a call
    /// states Remote support for itself, and a booking form states what was picked.
    /// </summary>
    public TicketCategory Category { get; set; } = TicketCategory.General;

    /// <summary>Client the job belongs to. Null only for legacy tickets raised before clients existed.</summary>
    public int? ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>
    /// The person this job is for, when one is known.
    ///
    /// <see cref="ContactName"/> stays and is still written alongside it, deliberately. This link
    /// answers "who is this, now" — it follows a contact whose phone number changes. The string
    /// answers "whose job was this, then", and survives that contact being archived or the machine
    /// being reassigned. Same argument as <see cref="HealthCheckRun.ClientId"/> being copied
    /// rather than joined: last March's job was for the person who owned it in March.
    /// </summary>
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }

    /// <summary>
    /// The job this ticket is part of.
    ///
    /// Every remote session gets its own ticket and its own number (3.6.6), which is what makes
    /// time and tracking mean anything — but real work carries over a lunch break and a night, and
    /// the 30-minute reuse window is a guess about one sitting. So a ticket can say it is part of
    /// another one: the parent lists its children and totals their time, and that total is the
    /// job's time.
    ///
    /// **ONE LEVEL.** A child may not itself be a parent. Two levels would mean a tree, and a tree
    /// means a technician deciding where in it something belongs — a question nobody wants at the
    /// end of a job. <see cref="TicketService.SetParentAsync"/> is the only thing that writes this
    /// and it refuses anything deeper, along with a ticket being its own parent.
    /// </summary>
    public int? ParentTicketId { get; set; }
    public Ticket? ParentTicket { get; set; }

    // ---- Call out ----
    public string SiteAddress { get; set; } = "";
    public DateTime? AppointmentUtc { get; set; }

    // ---- Workshop ----
    public string AssetMake { get; set; } = "";
    public string AssetModel { get; set; } = "";
    public string AssetSerial { get; set; } = "";
    public string Accessories { get; set; } = "";
    public bool DevicePasswordProtected { get; set; }
    /// <summary>Encrypted at rest with the platform data-protection key; never stored in plain text.</summary>
    public string DevicePasswordEnc { get; set; } = "";

    // ---- the workflow (stage 3.7) — Services/Workflow.cs is the route; these are where a job is on it ----

    /// <summary>
    /// The stage key from <c>Workflow.Stages</c>, or empty for a category with no workflow. A
    /// string rather than a number so no stage is ever renumbered by inserting one before it (§42).
    /// <see cref="Status"/> is still written — derived from the stage — so every list that only
    /// knows Open / In progress / On hold / Closed keeps working.
    /// </summary>
    public string Stage { get; set; } = "";
    /// <summary>Whose desk the stage is on (<c>WorkLane</c>). Copied from the stage so a queue is one query.</summary>
    public int Lane { get; set; }
    /// <summary>Where "Back to work" returns to from Waiting on customer.</summary>
    public string StageReturn { get; set; } = "";
    /// <summary>
    /// The company's Job types switches that were OFF when this ticket was opened (<c>Tenant.JobTypesOff</c>, copied once
    /// and never changed): the job finishes the route it started on, whatever is switched since. Empty — every ticket
    /// from before Job types — is everything on. The machines booked in with it copy it from it.
    /// </summary>
    public string RouteOff { get; set; } = "";
    public bool? Repairable { get; set; }
    /// <summary>solved, inspection, quote or dni — the technician's decision.</summary>
    public string Outcome { get; set; } = "";
    /// <summary>
    /// The technician who did the work and made the decision. Parts fitted and a rejected quote go
    /// back to them automatically, and they can still see the job while it is in billing.
    /// </summary>
    public string WorkedBy { get; set; } = "";
    public int? TimeSpentMinutes { get; set; }
    public decimal? InvoiceAmount { get; set; }
    public string InvoiceNumber { get; set; } = "";
    /// <summary>A call out invoiced and paid on site (2026-09-27): "cash" or "card", with the amount in
    /// <see cref="InvoiceAmount"/>. Empty for everything invoiced through the office.</summary>
    public string PaymentMethod { get; set; } = "";
    /// <summary>The card machine's reference number, for a card payment on site.</summary>
    public string PaymentReference { get; set; } = "";
    public decimal? QuoteAmount { get; set; }
    public string QuoteReference { get; set; } = "";
    /// <summary>Every supplier the quote was asked of, as addresses joined by ", " — one, or several in BCC.</summary>
    public string QuoteSupplierEmail { get; set; } = "";
    /// <summary>The same suppliers by name, joined by ", ", for the timeline and the console.</summary>
    public string QuoteSupplierNames { get; set; } = "";
    /// <summary>The signature the request was signed with (2026-10-06), so downloading it again signs
    /// it the same way. Null: none — the person's name and the company's, as before signatures.</summary>
    public int? QuoteSignatureId { get; set; }

    // The machine a quote is for (Clint, 2026-09-29): "a battery replacement for an Asus VivoBook
    // 123, with SN123". One per quote, and optional — a quote can be for parts no machine owns. Until
    // somebody saves it, a workshop job's quote is for the machine it was booked in with
    // (QuoteService.MachineOf); once saved, what was saved is the answer, blanks included.
    public bool QuoteMachineSaved { get; set; }
    /// <summary>"Desktop", "Laptop", "All-in-one" (<c>QuoteService.MachineTypes</c>), or empty.</summary>
    public string QuoteMachineType { get; set; } = "";
    public string QuoteMachineMake { get; set; } = "";
    public string QuoteMachineModel { get; set; } = "";
    public string QuoteMachineSerial { get; set; } = "";

    public string DoNotInvoiceReason { get; set; } = "";
    public string AbandonedReason { get; set; } = "";
    /// <summary>When the machine left the building, with three photos of it as it went.</summary>
    public DateTime? CollectedUtc { get; set; }
    public string CollectedBy { get; set; } = "";
    /// <summary>It left with a technician who took it to the customer, rather than being collected
    /// (2026-09-30). <see cref="CollectedUtc"/> is then when it was handed over, and by whom.</summary>
    public bool Delivered { get; set; }
    /// <summary>Machines the same client brought in together (2026-09-30) share this: the first
    /// one's ticket id. Null for a machine booked in on its own. Equals — none is the parent.</summary>
    public int? BookingId { get; set; }

    public string AssignedTo { get; set; } = "";
    public string ContactName { get; set; } = "";
    /// <summary>
    /// The address a customer wrote from, for a ticket opened by email or the contact form
    /// (2026-10-05). It is who the auto-reply went to, whose replies may land on this ticket, and
    /// what the ticket page checks against the client list — every time it is drawn, so a contact
    /// added later links the ticket without anybody redoing anything. Empty for everything else.
    /// </summary>
    public string RequesterEmail { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OpenedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedUtc { get; set; }

    // ---- Recurring task (2026-10-07) ----
    /// <summary>The schedule that opened this ticket, or null for every other ticket.</summary>
    public int? RecurringTaskId { get; set; }
    /// <summary>The day it was due, as the COMPANY's date (midnight, no zone). Still open after that
    /// day, it is overdue — <c>RecurringTaskService.IsOverdue</c>.</summary>
    public DateTime? DueOn { get; set; }
    /// <summary>
    /// When the job is due (2026-10-26, HANDOVER §165): a DAY, the company's, stored as <c>RecurringTaskService.AsDate</c>
    /// stores one. Any ticket may have one — set when it is opened or later, by anybody who can see it; a recurring task's
    /// ticket is given its day. Null: none. Every rule is <c>TicketDue</c>. Kept apart from <see cref="DueOn"/>, which is
    /// the SCHEDULE's day and what stops a schedule opening the same day twice — moving this must not open another.
    /// </summary>
    public DateTime? DueDate { get; set; }

    public List<TicketNote> Notes { get; set; } = new();
    public List<TicketMention> Mentions { get; set; } = new();
}

/// <summary>How often a <see cref="RecurringTask"/> falls due. Stored as the number: never renumber.</summary>
public enum Repeats { Daily = 0, Weekly = 1, Monthly = 2 }

/// <summary>
/// A job set up once that opens a ticket every time it falls due (Clint, 2026-10-07: "setup the task
/// once assign it to the correct person/persons and forget about it"). It is never a ticket itself:
/// each time it is due, <c>RecurringTaskService</c> opens an ordinary <see cref="TicketCategory.Recurring"/>
/// ticket with these words and these people, in the morning of that day in the company's time zone.
/// Cancelling stops future tickets and nothing else; the row is kept so the tickets it made still say
/// where they came from. Every rule is <c>Services/RecurringTaskService.cs</c>.
/// </summary>
public sealed class RecurringTask : ICompanyNumbered
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Its number within the company (2026-10-23, HANDOVER §159): <c>/portal/r/{Number}</c>. Given on save.</summary>
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    /// <summary>Maintenance, Billing, Readings, or one the company added — a <see cref="RecurringKind"/>'s name, copied.</summary>
    public string Kind { get; set; } = "";
    public int? ClientId { get; set; }
    /// <summary>Everybody each ticket goes to, as <c>Assignees</c> keeps them: "Jason, Ryan".</summary>
    public string AssignedTo { get; set; } = "";
    public TicketPriority Priority { get; set; } = TicketPriority.Normal;

    public Repeats Repeats { get; set; }
    /// <summary>Daily: Monday to Friday only.</summary>
    public bool WeekdaysOnly { get; set; }
    /// <summary>Weekly: the days ticked, one bit per <see cref="DayOfWeek"/> (Sunday 1, Monday 2 … Saturday 64).</summary>
    public int Days { get; set; }
    /// <summary>Monthly: the date, 1 to 31, or 0 for the last day of the month.</summary>
    public int MonthDay { get; set; }

    /// <summary>The next day a ticket is due, as the company's date. Moved on as each is opened.</summary>
    public DateTime NextDue { get; set; }
    /// <summary>The last day a ticket was opened for, so a change to the schedule never opens that day twice.</summary>
    public DateTime? LastOpenedFor { get; set; }

    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledUtc { get; set; }
    public string CancelledBy { get; set; } = "";
    public string CancelReason { get; set; } = "";
}

/// <summary>A type of recurring task. Every company starts with Maintenance, Billing and Readings
/// and may add its own (Clint's answer 1, 2026-10-07); the item list's guard keeps out duplicates.</summary>
public sealed class RecurringKind
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
    public string MatchKey { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Whether a workshop photo was taken when the machine came in, or when it went out — or is a
/// picture of a PART, attached to a quote line so the supplier can see what is being asked for.
/// Part photos are never counted by the in and out gates.
/// </summary>
/// <summary>Screenshot (2026-10-09): a picture of the customer's screen that came with their support request.</summary>
public enum PhotoKind { Before = 0, After = 1, Part = 2, Screenshot = 3 }

/// <summary>
/// A photograph attached to a workshop job: three of the machine as it arrived, three of it as it
/// leaves. The whole point is the argument that does not happen six weeks later about a scratch on
/// a lid, so what matters is that they exist and cannot be skipped.
///
/// <see cref="TicketId"/> is NULLABLE, and that is the awkward part of the design rather than an
/// oversight. Three photos are required to CREATE a workshop ticket, so they are uploaded before
/// there is a ticket to hang them on. They are staged against <see cref="StagedBy"/> and
/// <see cref="StageId"/> until the booking form is submitted, and attached in the same transaction
/// that writes the ticket. Anything still staged after a day was an abandoned form and is swept.
/// </summary>
/// <summary>
/// A recording of a remote session (roadmap 1.6), made in the technician's console when they pressed
/// Record and uploaded when they stopped. Kept on the server with the session's ticket, so anyone who
/// may see the ticket may watch it - Clint, 2026-09-29.
/// </summary>
public sealed class SessionRecording
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Its portal address, <c>/portal/v/{Token}</c> (HANDOVER §159). See <see cref="FileToken"/>.</summary>
    public string Token { get; set; } = FileToken.New();
    public int TicketId { get; set; }
    public string SessionId { get; set; } = "";
    public string MachineName { get; set; } = "";
    /// <summary>File name under the tenant's recordings folder. Generated, never taken from the upload.</summary>
    public string FileName { get; set; } = "";
    public long Bytes { get; set; }
    public int Seconds { get; set; }
    public string RecordedBy { get; set; } = "";
    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TicketPhoto
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Its portal address, <c>/portal/p/{Token}</c> (HANDOVER §159). See <see cref="FileToken"/>.</summary>
    public string Token { get; set; } = FileToken.New();
    public int? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public PhotoKind Kind { get; set; }
    /// <summary>File name under the tenant's photo folder. Generated, never the customer's own.</summary>
    public string FileName { get; set; } = "";
    /// <summary>What the person called it when they picked it, kept only so the page can say so.</summary>
    public string OriginalName { get; set; } = "";
    public long Bytes { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;

    // ---- staging, for photos that exist before their ticket does ----
    /// <summary>The booking form this photo was uploaded for. Empty once it is attached.</summary>
    public string StageId { get; set; } = "";
    /// <summary>Who staged it, so one person's half-finished booking cannot be claimed by another.</summary>
    public string StagedBy { get; set; } = "";

    [NotMapped] public bool IsStaged => TicketId == null;

    /// <summary>
    /// Which machine of the ticket this is a photo of, when the job has more than one (2026-10-02):
    /// a <see cref="TicketMachine"/>'s id. Null is the ticket's own machine — every photo before this
    /// existed, and every photo of a job with one machine.
    /// </summary>
    public int? MachineId { get; set; }

    /// <summary>
    /// The quote line this is a photo of, for a photo of kind Part (2026-10-06: "uploading photos only
    /// allows for one photo"). A line has as many as were taken; each photo belongs to one line. Null
    /// until the line is added — the photos go up first, one at a time, and the line then names them.
    /// </summary>
    public long? QuoteLineId { get; set; }
}

/// <summary>
/// Another machine on the SAME job (Clint, 2026-10-02): "when we need to do a data transfer. the
/// client would bring in their old and new machines which needs to be booked in together under one
/// ticket." The ticket's own Asset* columns are its first machine; each further one is a row here,
/// with the same booking questions and its own photos in. One fault, one route, one invoice — a
/// separate job is still a ticket of its own (<see cref="Ticket.BookingId"/>).
/// </summary>
public sealed class TicketMachine
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    /// <summary>What this machine is on the job, in their words: "the new laptop — data goes onto this one".</summary>
    public string Purpose { get; set; } = "";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Accessories { get; set; } = "";
    public bool PasswordProtected { get; set; }
    /// <summary>Encrypted at rest, as the ticket's own device password is.</summary>
    public string PasswordEnc { get; set; } = "";
    public string AddedBy { get; set; } = "";
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    [NotMapped] public string Name => $"{Make} {Model}".Trim();
}

public sealed class TicketNote
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public string Author { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Automatic entries (call accepted, script run, session ended) rather than typed notes.</summary>
    public bool IsSystem { get; set; }
    /// <summary>
    /// The workflow stage the ticket was in when this was written, so the timeline can be grouped
    /// under stage headings and read like a story — Clint, answer 17. Empty for a ticket with no
    /// workflow, and for everything written before there were stages.
    /// </summary>
    public string Stage { get; set; } = "";
    /// <summary>
    /// A Work done entry rather than a comment: what was actually done to the machine. Internal,
    /// and the thing that gets copied into the customer's invoice by hand (answer 3).
    /// </summary>
    public bool IsWorkDone { get; set; }
    /// <summary>
    /// Set when this entry is a step's FORM, filled in — "Inspection findings", "Work done", "To be
    /// invoiced" — so the timeline draws it as that form rather than as a comment (2026-09-27).
    /// </summary>
    public string FormTitle { get; set; } = "";
    /// <summary>The stage this step moved the job to, or empty for a comment.</summary>
    public string MovedTo { get; set; } = "";
    /// <summary>
    /// The CUSTOMER's words, from an emailed reply (2026-10-09) — never a comment of ours, so never
    /// offered "Email to the client", and the people on the ticket are flagged until they open it.
    /// </summary>
    public bool FromCustomer { get; set; }
    /// <summary>
    /// A line written by itself while something ELSE happened — a session's lines (connected, ended, clipboard, scripts,
    /// recordings), a ticket's own "opened" line — which Latest activity leaves out unless asked (2026-10-26, HANDOVER
    /// §164). Not the same as <see cref="IsSystem"/>: "reassigned from Sue to Ryan" is automatic in its wording but is
    /// news, and stays in. Every entry from before 2026-10-26 that was IsSystem was marked Automatic by the migration.
    /// </summary>
    public bool Automatic { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // ---- going back a stage (Clint, 2026-10-13: "an option to go back at every stage. Something may have changed or
    //      a person clicked the next stage by mistake"). Every rule is TicketService.Back.cs. ----

    /// <summary>
    /// For a step that moved the job (<see cref="MovedTo"/> set): what the job's route fields were JUST BEFORE it —
    /// <c>TicketService.StepSnapshot</c> as JSON — so going back puts every one of them back as it was, the amount to
    /// invoice and the time included. Empty for every step written before 2026-10-13: going back over one of those
    /// moves the stage only.
    /// </summary>
    public string Before { get; set; } = "";
    /// <summary>This step was gone back over: it stays in the story, marked, and its figures no longer count.</summary>
    public DateTime? UndoneUtc { get; set; }
    public string UndoneBy { get; set; } = "";
    /// <summary>This entry IS a "went back" — never itself gone back over (a second Back goes one step further).</summary>
    public bool WentBack { get; set; }
}

/// <summary>
/// A file or photo on a comment (Clint, 2026-10-13: "an option to upload files or photos on all comments. Sometimes the
/// technician wants to upload photos during stages to show progress they are making. This should also be able to be
/// emailed") — and the damage report on Not repairable — Damage report needed. Uploaded one at a time as it is picked
/// (photos made smaller first, as workshop photos are), held loose on the ticket by whoever picked it, and taken by
/// the comment when it is posted (<see cref="NoteId"/>). Every rule is <c>Services/TicketAttachmentService.cs</c>.
/// </summary>
public sealed class TicketAttachment
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Its portal address, <c>/portal/f/{Token}</c> (HANDOVER §159). See <see cref="FileToken"/>.</summary>
    public string Token { get; set; } = FileToken.New();
    public int TicketId { get; set; }
    /// <summary>The comment or step it belongs to. Null while it is picked and the comment not yet posted.</summary>
    public long? NoteId { get; set; }
    /// <summary>File name under the tenant's attachment folder. Generated, never the person's own.</summary>
    public string FileName { get; set; } = "";
    /// <summary>What it was called when it was picked — shown, and the name it is downloaded and emailed as.</summary>
    public string OriginalName { get; set; } = "";
    /// <summary>What its bytes are (sniffed for pictures and PDFs), or application/octet-stream.</summary>
    public string ContentType { get; set; } = "";
    public long Bytes { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;

    [NotMapped] public bool IsImage => ContentType.StartsWith("image/");
}

/// <summary>
/// One comment emailed to the client (Clint, 2026-10-09): "the client then gets the original fault as
/// per the ticket description with the comment whoever has posted, but not the entire string of the
/// ticket." A row per send, kept whether it went or not, so the ticket says what the client has seen
/// — and so a reply from the address it went to is accepted onto the ticket, typed by hand or not.
/// Every rule is <c>Services/ClientUpdates.cs</c>.
/// </summary>
public sealed class TicketClientUpdate
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int TicketId { get; set; }
    /// <summary>The comment that went — its words are read from the note, never copied here.</summary>
    public long NoteId { get; set; }
    public string ToAddress { get; set; } = "";
    public string ToName { get; set; } = "";
    public string SentBy { get; set; } = "";
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
    /// <summary>It reached a mail server — ours or theirs. False: <see cref="Result"/> says why not.</summary>
    public bool Sent { get; set; }
    /// <summary>"sent", "sent from our mailbox, because yours said: …", or "not sent: …".</summary>
    public string Result { get; set; } = "";
    /// <summary>Ours, carrying the ticket (rsp.{tenant}.{number}.…@), so a reply's References finds it.</summary>
    public string MessageId { get; set; } = "";
}

/// <summary>
/// Something a company quotes for — "DDR4 8GB SO-DIMM", "Laptop screen 15.6 FHD". One list per
/// company, shared by everyone (Clint, answer 6), so the dropdown gets better the more it is used.
///
/// <see cref="MatchKey"/> is what stops the list filling with near-copies: the name's words
/// lower-cased and SORTED, so "DDR memory" and "memory DDR" are the same key. Adding an item whose
/// key already exists is refused with the existing one offered instead; one whose words merely
/// overlap is allowed after a "did you mean" — "DDR4 8GB" and "DDR4 16GB" are different parts.
/// </summary>
public sealed class CatalogItem
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
    public string MatchKey { get; set; } = "";
    /// <summary>The part number it was last quoted with, offered again next time.</summary>
    public string LastPartNumber { get; set; } = "";
    /// <summary>
    /// Set when a supervisor or office administrator merged this item into another. Kept rather
    /// than deleted so old quote lines still say what they said; hidden from the dropdown.
    /// </summary>
    public int? MergedIntoId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One thing a workshop quote asks the supplier for (answer 5: a quote can have several). The item's
/// NAME is copied here rather than only joined: last month's quote asked for what it asked for,
/// even if the item has since been renamed or merged.
/// </summary>
public sealed class QuoteLine
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int TicketId { get; set; }
    public int ItemId { get; set; }
    public string ItemName { get; set; } = "";
    public string PartNumber { get; set; } = "";
    /// <summary>The serial number of THIS part, when there is one (Clint, 2026-09-27). Optional, and
    /// never remembered on the item the way the part number is: a serial belongs to one part.</summary>
    public string SerialNumber { get; set; } = "";
    public int Quantity { get; set; } = 1;
    /// <summary>
    /// The ONE photo a line could have before 2026-10-06. Kept only so the migration could carry it
    /// across: a line's photos are now every <see cref="TicketPhoto"/> whose
    /// <see cref="TicketPhoto.QuoteLineId"/> names it. Nothing reads or writes this any more.
    /// </summary>
    public long? PhotoId { get; set; }
    /// <summary>Its photos, oldest first — filled in by <c>QuoteService.LinesAsync</c>, never stored here.</summary>
    [NotMapped] public List<long> PhotoIds { get; set; } = new();
    /// <summary>The same photos' portal addresses (<see cref="TicketPhoto.Token"/>), in the same order — what a page links to (HANDOVER §159).</summary>
    [NotMapped] public List<string> PhotoTokens { get; set; } = new();
    public string AddedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Somebody a company asks for quotes (Clint, 2026-09-29): kept once, like the item list, so the
/// office administrator ticks the suppliers wanted rather than typing an address every time. The
/// same guard as items: <see cref="MatchKey"/> is the name's words sorted, so "Rectron Cape Town"
/// and "Cape Town Rectron" are one supplier, and one address is never two suppliers.
/// </summary>
public sealed class Supplier
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
    public string MatchKey { get; set; } = "";
    public string Email { get; set; } = "";
    public string ContactPerson { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>Set when taken off the list. Kept, because a ticket's timeline still names them.</summary>
    public DateTime? RemovedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A signature for quote requests (Clint, 2026-10-06): "an option to upload a signature or multiple
/// signatures that one can select before sending in case there is more than one person in the
/// organization that requests quotes" — text, an image, or both; the text with "font, size and color
/// selection ... so that they can make it look the way they want it".
///
/// <para>The text is LINES, each with its own font, size, colour and weight (<see cref="LinesJson"/>,
/// <c>QuoteSignatures.Line</c>) — never HTML: the email's HTML is written from these, every word
/// encoded, every style from a fixed list. The image is kept in the row itself; it is small.</para>
/// </summary>
public sealed class QuoteSignature
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>What the picker shows: "Clint — Accounts".</summary>
    public string Name { get; set; } = "";
    public string LinesJson { get; set; } = "[]";
    public byte[]? Image { get; set; }
    /// <summary>image/png, image/jpeg or image/gif — sniffed from the bytes, never the file name.</summary>
    public string ImageType { get; set; } = "";
    /// <summary>
    /// Random, new with every image: the image's public address (<c>/quote-signature/{token}</c>).
    /// Public because web mail fetches it once the signature is pasted in — it goes to suppliers anyway.
    /// </summary>
    public string ImageToken { get; set; } = "";
    /// <summary>How wide the image is drawn in the email, in pixels.</summary>
    public int ImageWidth { get; set; }
    /// <summary>The image above the text rather than below it.</summary>
    public bool ImageFirst { get; set; }
    /// <summary>Set when taken off the list. Kept, because a ticket already signed with it is downloaded again the same.</summary>
    public DateTime? RemovedUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One article in the company's knowledge base (roadmap 3.8.5, Clint 2026-10-05): "a client may have
/// a ticket to create a new user on their network. Once the technician completes the job, he can
/// commit the ticket to the knowledgebase, then edit with step by step instructions, so that any other
/// technician then knows how to add that user to that client."
///
/// <para>General, or ONE client (<see cref="ClientId"/>) — his choice, through "go with your
/// recommendations". Staff only; nothing here is ever shown to a customer. Every save is a
/// <see cref="KbRevision"/>, so an article anybody may edit can always be put back.</para>
/// </summary>
public sealed class KbArticle : ICompanyNumbered
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Its number within the company (2026-10-23, HANDOVER §159): <c>/portal/a/{Number}</c>. Given on save.</summary>
    public int Number { get; set; }
    /// <summary>The client this is about, or null for the General section.</summary>
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    /// <summary>The folder it is in (2026-10-06), always one of its own section's — or null, at the
    /// top of the section.</summary>
    public int? FolderId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>The words, in the knowledge base's own small markup — <c>KbText</c> draws it.</summary>
    public string Body { get; set; } = "";
    /// <summary>
    /// The ticket it was written up from, when it was. The id AND the number, and no foreign key:
    /// an article outlives anything that happens to its ticket, and the number is what a person reads.
    /// </summary>
    public int? FromTicketId { get; set; }
    public int FromTicketNumber { get; set; }
    /// <summary>Goes up by one on every save. A save made against an older number is refused, so two
    /// people editing at once never silently overwrite each other.</summary>
    public int Version { get; set; } = 1;
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Set when a supervisor or the owner deletes it. The row and its history stay; nobody sees it.</summary>
    public DateTime? DeletedUtc { get; set; }
    public string DeletedBy { get; set; } = "";
}

/// <summary>Every save of a <see cref="KbArticle"/>, the first included — what "put this version back" restores.</summary>
public sealed class KbRevision
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int ArticleId { get; set; }
    public int Version { get; set; }
    public string Title { get; set; } = "";
    public int? ClientId { get; set; }
    /// <summary>The folder it was in at this version, so putting it back puts it back where it was.</summary>
    public int? FolderId { get; set; }
    public string Body { get; set; } = "";
    public string SavedBy { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>"Written up from #123", "Put back version 2" — empty for an ordinary edit.</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// A folder in the knowledge base (Clint, 2026-10-06: "it would be nice to be able to create folders
/// and subfolders ... under general and clients"). It belongs to ONE section — General
/// (<see cref="ClientId"/> null) or one client — and so does everything in it; a subfolder names its
/// <see cref="ParentId"/>, which is always in the same section. The rules are KnowledgeBaseService's.
/// </summary>
public sealed class KbFolder
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>The client whose section it is in, or null for General.</summary>
    public int? ClientId { get; set; }
    /// <summary>The folder it is inside, or null at the top of its section.</summary>
    public int? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A picture in an article — usually a screenshot pasted straight into the editor. It belongs to the
/// company, not to one article, because an old version may still show it: never deleted.
/// </summary>
public sealed class KbImage
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Its portal address, <c>/portal/i/{Token}</c> (HANDOVER §159). See <see cref="FileToken"/>.</summary>
    public string Token { get; set; } = FileToken.New();
    /// <summary>File name under the company's knowledge base folder. Generated, never the upload's own.</summary>
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Bytes { get; set; }
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Somebody was asked something on a ticket: "@Sue what is going on here". Open until that person
/// writes anything on the same ticket — a comment or a Work done entry (Clint, answer 18: the flag
/// stays until they reply). Staff only; the console and the portal show the open ones as a badge.
/// </summary>
public sealed class TicketMention
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public long NoteId { get; set; }
    public TicketNote Note { get; set; } = null!;
    /// <summary>The display name of the person asked — the same string tickets are assigned by.</summary>
    public string Person { get; set; } = "";
    public string MentionedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AnsweredUtc { get; set; }
}

public sealed class Device
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;
    public string DeviceId { get; set; } = "";
    public string DeviceKey { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Os { get; set; } = "";
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The version the machine's agent service and its tray app last said they were (2026-10-16, auto-update stage A;
    /// <see cref="RemoteSupport.Shared.AppVersion"/>). Kept apart because they can differ — an agent updated while the
    /// tray app waits to restart — and kept at all so a machine that is OFF still shows what it runs. Empty until that
    /// half has connected since this was added; "1.0.0.0" is every build from before version numbers.
    /// </summary>
    public string AgentVersion { get; set; } = "";
    public string ClientVersion { get; set; } = "";
    /// <summary>Its agent said it updates the machine itself (DeviceCan.Update) when it last connected. False: one more
    /// reinstall by hand is needed before auto-update can reach it (auto-update stage F).</summary>
    public bool SelfUpdates { get; set; }

    // hardware summary, refreshed each time the machine connects
    public string Cpu { get; set; } = "";
    public int Cores { get; set; }
    public int RamMb { get; set; }
    public int DiskTotalGb { get; set; }
    public int DiskFreeGb { get; set; }

    /// <summary>Ticketing client that owns this machine.</summary>
    public int? ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>
    /// The person whose machine this is, when it is one person's machine. Null for a server, a
    /// shared machine, or a machine linked only to the company.
    ///
    /// A contact here must belong to <see cref="ClientId"/>. Nothing in SQLite enforces that —
    /// it is a rule the service layer keeps, because the alternative is a foreign key pair that
    /// EF cannot express against a nullable client. <see cref="ContactService"/> refuses the
    /// mismatch and the portal never offers it; anything else writing this field must do the same.
    /// </summary>
    public int? ContactId { get; set; }
    public Contact? Contact { get; set; }

    /// <summary>
    /// The name the person at this machine last typed into Request Support, and when (2026-10-18). Clint: machines
    /// were not being linked straight away, so the list filled with DESKTOP-names and Windows sign-ins nobody knew.
    /// It is what they SAID, never checked — shown, marked, only while nobody has linked the machine
    /// (<see cref="ContactService.OwnerName"/>), and offered as a suggestion when somebody does. The latest name wins.
    /// </summary>
    public string RequestedBy { get; set; } = "";
    public DateTime? RequestedByUtc { get; set; }
    /// <summary>The email given with <see cref="RequestedBy"/> (2026-10-23) — at the install or a support request — or empty.
    /// Linking the machine to a client adds that person as a contact with it (ContactService.LinkDeviceToNewContactAsync).</summary>
    public string RequestedByEmail { get; set; } = "";
    /// <summary>
    /// The installer window's details last kept for this machine, by their id (2026-10-23, GivenPerson). Every hello says
    /// them; they become <see cref="RequestedBy"/> only when the id is new here, so a support request's later name is not
    /// put back by the next reconnect.
    /// </summary>
    public string InstalledForId { get; set; } = "";

    /// <summary>
    /// Hashed hardware identity. Lets a machine keep this record through an uninstall/reinstall,
    /// which deletes device.json and would otherwise create a second row (handover §15).
    /// Empty for machines that predate this or will not report one.
    /// </summary>
    public string HardwareId { get; set; } = "";
    /// <summary>How many times this machine has come back with a new DeviceId.</summary>
    public int ReinstallCount { get; set; }
    /// <summary>When it last did, so the device list can say so rather than just showing one row.</summary>
    public DateTime? LastReinstallUtc { get; set; }
    /// <summary>
    /// Matched to this record by its hardware alone and not yet confirmed by a supervisor or the owner
    /// (2026-10-07). Unattended access is held back while it is set — see DeviceInfo.ReinstallUnconfirmed.
    /// </summary>
    public bool ReinstallUnconfirmed { get; set; }
    /// <summary>Who confirmed the last reinstall, and when — kept, so the Devices page can say so.</summary>
    public string ReinstallConfirmedBy { get; set; } = "";
    public DateTime? ReinstallConfirmedUtc { get; set; }

    /// <summary>
    /// Whether the person at this machine has allowed unattended access.
    ///
    /// Off by default, and off is the honest default: installing a support client is not the same
    /// as agreeing that somebody can connect while you are at lunch. The tray app is where it is
    /// turned on, the hub is what enforces it — an unattended session to a device with this off
    /// is refused even when the agent service is connected and willing.
    ///
    /// Stored here rather than on the machine because the machine is the thing being protected:
    /// a flag that lives where the agent lives is one the agent can decide to believe.
    /// </summary>
    public bool UnattendedEnabled { get; set; }
    /// <summary>When it was last changed and by whom, in the customer's own words ("Lenovo at
    /// DESKTOP-OPHQIDH"). Nobody has asked for an audit trail yet; the day somebody asks who let
    /// a technician into a machine, an empty column is not an answer.</summary>
    public DateTime? UnattendedChangedUtc { get; set; }
    public string UnattendedChangedBy { get; set; } = "";

    /// <summary>
    /// The machine's unattended password (2026-10-02), as a PBKDF2 hash and its salt — the same
    /// derivation as a technician's (<c>Passwords</c>). Empty when there is none. Set only from the
    /// tray app; checked by the hub before any unattended connection, and before anybody joins a
    /// session that was opened with it. Nobody at the company can read it or get round it: a
    /// forgotten one is removed at the machine.
    /// </summary>
    public string UnattendedPasswordHash { get; set; } = "";
    public string UnattendedPasswordSalt { get; set; } = "";
    public DateTime? UnattendedPasswordChangedUtc { get; set; }
    public string UnattendedPasswordChangedBy { get; set; } = "";

    /// <summary>
    /// When the person at the machine chose Exit in the tray app, or null while it is running
    /// (2026-09-29). While set, the machine is OFFLINE to everyone and no session reaches it, the
    /// agent's included; the tray app connecting again clears it. Stored rather than held in
    /// memory, because the agent reconnects after a server restart and would otherwise bring the
    /// machine back on its own.
    /// </summary>
    public DateTime? ClosedByUserUtc { get; set; }

    /// <summary>
    /// This machine's monthly health check: 0 follow its customer's tick, 1 on, 2 off
    /// (<c>HealthCheckModes</c>). 0 is what every existing row reads as, which is the point — the
    /// customer's tick is the rule and this is the exception.
    /// </summary>
    public int HealthCheckMode { get; set; }
    /// <summary>Outside help on this machine: 0 as the company's switch, 1 on, 2 off (<c>OutsiderAccess</c>).</summary>
    public int OutsiderMode { get; set; }
    /// <summary>
    /// Wake-on-LAN (2026-10-06): the machine's network cards as it last reported them — MACs and the
    /// networks they were on — as JSON (<c>List&lt;NetworkCard&gt;</c>). Empty until a build that reports
    /// them has connected. Kept because the machine is switched off when anybody needs it.
    /// </summary>
    public string NetworkCardsJson { get; set; } = "";
    /// <summary>The address the machine last connected from, as the server saw it — its site's internet
    /// address. Two machines on the same one, sharing a network, are at the same place.</summary>
    public string PublicAddress { get; set; } = "";
    /// <summary>What the machine's agent last said is on it. Null until it has said anything —
    /// the portal is the record, and the record must not claim what nobody measured.</summary>
    public bool? HealthCheckInstalled { get; set; }
    public string HealthCheckNote { get; set; } = "";
    public DateTime? HealthCheckReportedUtc { get; set; }
}

/// <summary>
/// One monthly maintenance and health check run, uploaded by a machine after it has done the work
/// and built its own report. The endpoint builds the PDF because it has Edge and the server does
/// not; the server stores it, shows it in the portal and sends the mail, so no customer machine
/// ever holds a mail password. See docs/PLAN-HEALTHCHECK.md.
/// </summary>
/// <summary>
/// Settings that belong to the PLATFORM rather than to any tenant — currently the mailbox the
/// product itself writes from. A single row, id 1.
///
/// It is separate from a tenant's own SMTP on purpose. A tenant's mailbox is for writing to that
/// tenant's customers (the monthly health check report); this one is for writing to the tenant.
/// Using theirs would mean a reseller's own mail server telling them their trial had ended, and
/// would simply not work for a brand-new trial that has configured nothing at all.
///
/// The password is encrypted with Data Protection exactly as the tenant one is, which means it is
/// tied to data/keys and to SetApplicationName. See section 10 of the handover before touching
/// either.
/// </summary>
public sealed class PlatformSettings
{
    public int Id { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPasswordEnc { get; set; } = "";
    /// <summary>Address the platform writes from. Falls back to SmtpUser when blank.</summary>
    public string SmtpFrom { get; set; } = "";
    /// <summary>Shown as the sender's name, e.g. "The Remote Support Program".</summary>
    public string SmtpFromName { get; set; } = "";
    public bool SmtpUseSsl { get; set; } = true;
    /// <summary>A copy of every tenant notice goes here too, when set — so the owner knows a
    /// trial is lapsing without waiting for the customer to notice.</summary>
    public string NoticeCopyTo { get; set; } = "";
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>
    /// The platform owner's authenticator secret (2026-10-01), encrypted. The owner portal asks for
    /// the admin key AND a code; whoever first signs in with the key sets it up, and
    /// <c>reset-owner-2fa</c> on the server's command line clears it.
    /// </summary>
    public string OwnerTotpSecretEnc { get; set; } = "";
    public long OwnerTotpLastStep { get; set; }

    // ---- the forwarding mailbox (3.8.4, 2026-10-05) ----
    // One mailbox on our own domain — tickets@theremotesupportprogram.com — that companies forward
    // their support address to when we cannot (or they would rather we did not) read their mailbox.
    // The platform checks it for everybody and sorts each message by the address it was sent to.

    /// <summary>The address companies forward to. Empty means forwarding is not offered.</summary>
    public string IntakeAddress { get; set; } = "";
    public string IntakeImapHost { get; set; } = "";
    public int IntakeImapPort { get; set; } = 993;
    public string IntakeImapSecurity { get; set; } = "ssl";
    public string IntakeImapUser { get; set; } = "";
    public string IntakeImapPasswordEnc { get; set; } = "";
    public long IntakeUidValidity { get; set; }
    public long IntakeLastUid { get; set; }
    public DateTime? IntakeLastCheckUtc { get; set; }
    public string IntakeLastProblem { get; set; } = "";
    public bool IntakePaused { get; set; }
    public int IntakeFailures { get; set; }
    public DateTime? IntakeNextTryUtc { get; set; }

    // ---- PayFast (2026-10-13, roadmap 4.8, docs/NEXT-SESSION-PAYFAST.md) ----
    // Set by `payfast set` on the server's command line (deploy/Set-PayfastKeys.ps1), never in appsettings: the key and
    // passphrase are encrypted like the mail password. Sandbox or live goes WITH the keys — a sandbox key used against
    // the live host (or the other way round) cannot work, so the two are only ever changed together.

    public string PayfastMerchantId { get; set; } = "";
    public string PayfastMerchantKeyEnc { get; set; } = "";
    public string PayfastPassphraseEnc { get; set; } = "";
    /// <summary>True until go-live: checkouts go to sandbox.payfast.co.za, where no money moves.</summary>
    public bool PayfastSandbox { get; set; } = true;
    public DateTime? PayfastUpdatedUtc { get; set; }

    // ---- Our Microsoft Entra app (2026-10-19, Tickets by email's "Sign in with Microsoft", HANDOVER §151) ----
    // ONE multi-tenant app, ours, that every company's Microsoft 365 sign-in goes through. Set by `microsoft-app set` on
    // the server's command line (deploy/Set-MicrosoftApp.ps1), never in appsettings: the secret is encrypted like the
    // mail password. docs/MICROSOFT-365.md is how the app is registered in Entra.

    /// <summary>The app's Application (client) ID — not a secret; it is in every sign-in address.</summary>
    public string MsClientId { get; set; } = "";
    public string MsClientSecretEnc { get; set; } = "";
    /// <summary>When the secret runs out (Entra gives them two years at most), if we were told. Shown by `microsoft-app show`.</summary>
    public DateTime? MsSecretExpiresUtc { get; set; }
    public DateTime? MsUpdatedUtc { get; set; }

    /// <summary>
    /// How many paying companies this server lets in at a time (2026-10-22, Clint: "allow 5 companies in at a time and
    /// rather test it that way"); past it, signup offers the waiting list. 0 = no limit. Set on Owner → Approvals.
    /// <see cref="Services.OpenPlaces"/> is every rule.
    /// </summary>
    public int OpenPlaces { get; set; } = Services.OpenPlaces.Default;

    [NotMapped] public bool MicrosoftConfigured => MsClientId.Length > 0 && MsClientSecretEnc.Length > 0;
    [NotMapped] public bool PayfastConfigured => PayfastMerchantId.Length > 0 && PayfastMerchantKeyEnc.Length > 0 && PayfastPassphraseEnc.Length > 0;
    [NotMapped] public bool MailConfigured => !string.IsNullOrWhiteSpace(SmtpHost);
    [NotMapped] public bool ForwardingOffered => !string.IsNullOrWhiteSpace(IntakeAddress) && !string.IsNullOrWhiteSpace(IntakeImapHost);
}

public sealed class HealthCheckRun
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>The reporting machine's DeviceId, as it presents on the websocket.</summary>
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";

    /// <summary>
    /// The ticketing client this machine belonged to when the run was uploaded, copied rather than
    /// joined. A machine can be reassigned, and last March's report was still about the customer
    /// who owned it in March.
    /// </summary>
    public int? ClientId { get; set; }

    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>When the machine actually ran the check, which is not when it managed to upload it.</summary>
    public DateTime RunUtc { get; set; }

    // Counts are recomputed from the uploaded JSON rather than trusted from the uploader, so the
    // portal and the report can never disagree about how many Criticals a machine had.
    public int Critical { get; set; }
    public int Warning { get; set; }
    public int Info { get; set; }
    public int Ok { get; set; }

    public bool MaintenanceRan { get; set; }
    /// <summary>Maintenance never runs without one. False with MaintenanceRan false is the normal Windows Server case.</summary>
    public bool RestorePointMade { get; set; }

    /// <summary>File names under the tenant's health check folder. Empty when that part did not arrive.</summary>
    public string JsonFile { get; set; } = "";
    public string PdfFile { get; set; } = "";

    /// <summary>Empty until a send is attempted, then "sent", or the reason it did not go.</summary>
    public string MailStatus { get; set; } = "";
    public DateTime? MailedUtc { get; set; }

    [NotMapped] public bool MailSent => MailStatus == "sent";
    [NotMapped] public int Attention => Critical + Warning;
}

/// <summary>
/// One message between people at the office (stage 3.8.1, 2026-09-30) — never a customer. To one
/// person by display name, or to everybody when <see cref="To"/> is empty.
/// </summary>
public sealed class StaffMessage
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public string From { get; set; } = "";
    /// <summary>A display name, or empty for Everyone.</summary>
    public string To { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One conversation with a visitor to the company's own website (3.8.3, 2026-09-30), through the
/// chat bubble the company pastes onto its site. Or, when nobody was there to answer, the message
/// they left (<see cref="LeftMessage"/>), which became a ticket.
/// </summary>
public sealed class WebChat
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>SHA-256 of the secret the visitor's browser holds. Their socket must show the secret.</summary>
    public string TokenHash { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>The page of their website the visitor started on, as their browser reported it.</summary>
    public string Page { get; set; } = "";
    public string Ip { get; set; } = "";
    public int? ClientId { get; set; }
    public int? ContactId { get; set; }
    /// <summary>Who replied first, and so owns it. Empty while it is waiting for anybody.</summary>
    public string AnsweredBy { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    /// <summary>"visitor", a staff member's name, or "idle".</summary>
    public string EndedBy { get; set; } = "";
    public bool LeftMessage { get; set; }
    public int? TicketId { get; set; }
}

public sealed class WebChatLine
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int WebChatId { get; set; }
    public bool FromVisitor { get; set; }
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// How a company takes tickets by email and through a form on its website (3.8.4, 2026-10-05).
/// One row per company, made the first time its page is saved. Off until they switch it on.
///
/// Clint: "build it so that it works for any hosted setup explaining the possible issues they may
/// face". Two ways in: <see cref="Way"/> "mailbox" — the platform checks the company's own mailbox
/// over IMAP — or "forward" — their support address forwards to one mailbox on OUR domain, which the
/// platform checks for everybody and sorts by the address each message was sent to (which is why
/// the address must be <see cref="AddressConfirmed"/> by a link mailed to it before anything is
/// sorted to them). A third since 2026-10-19: "microsoft" — somebody signs in with Microsoft and the platform
/// reads the mailbox with Microsoft's token instead of a password (§151).
/// <c>Services/TicketIntake</c> holds every rule; this is only where they are kept.
/// </summary>
public sealed class TicketIntakeSettings
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    // ---- email ----
    public bool EmailEnabled { get; set; }
    /// <summary>"mailbox" (a password), "microsoft" (Sign in with Microsoft) or "forward".</summary>
    public string Way { get; set; } = "mailbox";
    /// <summary>The address customers write to — support@anywhereit.co.za. The auto-reply comes
    /// from it; mail FROM it is never made a ticket (that is a loop, or a forward that lost the sender).</summary>
    public string Address { get; set; } = "";
    /// <summary>For forwarding: proved theirs by a link mailed to it. A mailbox they gave us the
    /// password to needs no proof.</summary>
    public bool AddressConfirmed { get; set; }
    public string ConfirmTokenHash { get; set; } = "";
    public DateTime? ConfirmSentUtc { get; set; }

    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    /// <summary>"ssl" (993) or "starttls" (143). Never plain: a mail password on the wire in the clear.</summary>
    public string ImapSecurity { get; set; } = "ssl";
    public string ImapUser { get; set; } = "";
    /// <summary>Data Protection, its own purpose string — see TicketIntake.</summary>
    public string ImapPasswordEnc { get; set; } = "";
    public string Folder { get; set; } = "INBOX";

    /// <summary>
    /// "own": the auto-reply leaves through the company's own mail server, so it really is from
    /// their address and passes their SPF/DKIM. "platform": from our mailbox with their name, and
    /// their address as Reply-To — for forwarding, or a mailbox with no way to send.
    /// </summary>
    public string SendVia { get; set; } = "own";
    /// <summary>Sending uses the reading login, and the same server — cPanel's usual arrangement.</summary>
    public bool SmtpSameLogin { get; set; } = true;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    /// <summary>"ssl" (465) or "starttls" (587).</summary>
    public string SmtpSecurity { get; set; } = "ssl";
    public string SmtpUser { get; set; } = "";
    public string SmtpPasswordEnc { get; set; } = "";

    // ---- Sign in with Microsoft (2026-10-19, §151) ----
    // The mailbox read is always <see cref="Address"/>: the account signed in is that mailbox, or somebody who may open
    // it (a shared mailbox). Delegated IMAP.AccessAsUser.All and SMTP.Send only — never an app-wide Mail.Read.

    /// <summary>Microsoft's refresh token, Data Protection with its own purpose. Empty = not connected: never signed in,
    /// Disconnected, or Microsoft refused it (then <see cref="MsUser"/> is kept, so the page can say who to sign in as again).</summary>
    public string MsRefreshTokenEnc { get; set; } = "";
    /// <summary>The Microsoft account that signed in — support@ itself, or a person who can open it.</summary>
    public string MsUser { get; set; } = "";
    /// <summary>Their organisation's Entra tenant id (the "tid"), which their token is refreshed against.</summary>
    public string MsTenant { get; set; } = "";
    public DateTime? MsConnectedUtc { get; set; }
    /// <summary>Who on OUR side pressed Sign in with Microsoft.</summary>
    public string MsConnectedBy { get; set; } = "";

    public bool AutoReply { get; set; } = true;
    /// <summary>The words of the auto-reply, with {name}, {number}, {subject} and {company}. Empty
    /// means the standard words.</summary>
    public string AutoReplyText { get; set; } = "";

    // ---- where checking has got to ----
    /// <summary>The folder's UIDVALIDITY when <see cref="LastUid"/> was taken. If it changes, the
    /// numbers mean something else and checking starts again from "now".</summary>
    public long UidValidity { get; set; }
    /// <summary>The highest message number already looked at. Mail is found by number, never by
    /// "unread": somebody reading support@ in Outlook first must not stop it becoming a ticket.</summary>
    public long LastUid { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public DateTime? LastOkUtc { get; set; }
    /// <summary>What went wrong last time, in words for the company, or empty.</summary>
    public string LastProblem { get; set; } = "";
    /// <summary>
    /// Checking has STOPPED because the server refused the password. Not retried every minute: a
    /// cPanel server blocks an address that keeps failing to sign in (cPHulk), and that block would
    /// fall on our server's address — every company on that host at once. Saving the form or
    /// pressing Test starts it again.
    /// </summary>
    public bool Paused { get; set; }
    public int Failures { get; set; }
    public DateTime? NextTryUtc { get; set; }
    /// <summary>A Gmail forwarding confirmation that arrived at our mailbox for their address: the
    /// code and link they need, shown on their page. Gmail will not forward until it is used.</summary>
    public string GmailConfirmation { get; set; } = "";
    public DateTime? GmailConfirmationUtc { get; set; }

    // ---- the contact form ----
    public bool FormEnabled { get; set; }
    public string FormHeading { get; set; } = "Contact us";
    public string FormIntro { get; set; } = "Tell us what you need and we'll get back to you.";
    /// <summary>"off", "optional" or "required".</summary>
    public string FormPhone { get; set; } = "optional";
    public bool FormCompany { get; set; } = true;
    public string FormThanks { get; set; } = "";

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Every message the intake looked at — an email or a form — and what it did with it (3.8.4). So
/// nothing is ever silently dropped: a newsletter that was left alone, a sender over the limit, an
/// out-of-office reply, each is on the company's page with the reason. TenantId 0 is our own
/// forwarding mailbox's messages that belonged to nobody.
/// </summary>
public sealed class InboundMail
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>"mailbox", "forward" or "form".</summary>
    public string Channel { get; set; } = "";
    /// <summary>The message's own Message-ID, so the same mail is never made a ticket twice.</summary>
    public string MessageId { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>"ticket", "reply" (added to an open ticket), "skipped" or "failed".</summary>
    public string Outcome { get; set; } = "";
    /// <summary>Why, in words for the company.</summary>
    public string Reason { get; set; } = "";
    public int? TicketId { get; set; }
    public int TicketNumber { get; set; }
    /// <summary>"sent", or why the auto-reply was not, or empty when none was due.</summary>
    public string ReplyResult { get; set; } = "";
}

/// <summary>How far one person has read one conversation: with a person, or "" for Everyone.</summary>
public sealed class StaffChatRead
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Person { get; set; } = "";
    public string With { get; set; } = "";
    public long LastReadId { get; set; }
}

public sealed class ChatLine
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public string DeviceId { get; set; } = "";
    public string From { get; set; } = "";
    public bool FromTech { get; set; }
    public string Text { get; set; } = "";
    public DateTime TsUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SessionLog
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>Ticket this session was worked under, so billing can total the time per ticket.</summary>
    public int? TicketId { get; set; }
    public string SessionId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Technicians { get; set; } = "";
    public string StartedBy { get; set; } = "";
    public bool Unattended { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public string EndReason { get; set; } = "";
}

public partial class PlatformDb : DbContext
{
    public PlatformDb(DbContextOptions<PlatformDb> o) : base(o) { }
    /// <summary>For <see cref="PgPlatformDb"/>, the same database on PostgreSQL with migrations of its own.</summary>
    protected PlatformDb(DbContextOptions o) : base(o) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Technician> Technicians => Set<Technician>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ChatLine> Chat => Set<ChatLine>();
    public DbSet<SessionLog> Sessions => Set<SessionLog>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketNote> TicketNotes => Set<TicketNote>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<TicketPhoto> TicketPhotos => Set<TicketPhoto>();
    public DbSet<TicketMachine> TicketMachines => Set<TicketMachine>();
    public DbSet<SessionRecording> SessionRecordings => Set<SessionRecording>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<QuoteSignature> QuoteSignatures => Set<QuoteSignature>();
    public DbSet<KbArticle> KbArticles => Set<KbArticle>();
    public DbSet<KbRevision> KbRevisions => Set<KbRevision>();
    public DbSet<KbImage> KbImages => Set<KbImage>();
    public DbSet<KbFolder> KbFolders => Set<KbFolder>();
    public DbSet<TicketMention> TicketMentions => Set<TicketMention>();
    public DbSet<StaffMessage> StaffMessages => Set<StaffMessage>();
    public DbSet<StaffChatRead> StaffChatReads => Set<StaffChatRead>();
    public DbSet<WebChat> WebChats => Set<WebChat>();
    public DbSet<WebChatLine> WebChatLines => Set<WebChatLine>();
    public DbSet<TicketIntakeSettings> TicketIntake => Set<TicketIntakeSettings>();
    public DbSet<InboundMail> InboundMail => Set<InboundMail>();
    public DbSet<TicketClientUpdate> TicketClientUpdates => Set<TicketClientUpdate>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<HealthCheckRun> HealthCheckRuns => Set<HealthCheckRun>();
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();
    public DbSet<AccountToken> AccountTokens => Set<AccountToken>();
    public DbSet<SeatAgreement> SeatAgreements => Set<SeatAgreement>();
    public DbSet<TermsAcceptance> TermsAcceptances => Set<TermsAcceptance>();
    public DbSet<WaitingListEntry> WaitingList => Set<WaitingListEntry>();
    public DbSet<OutsiderCode> OutsiderCodes => Set<OutsiderCode>();
    public DbSet<SavedUnattendedPassword> SavedUnattendedPasswords => Set<SavedUnattendedPassword>();
    public DbSet<DeviceRemoval> DeviceRemovals => Set<DeviceRemoval>();
    public DbSet<RecurringTask> RecurringTasks => Set<RecurringTask>();
    public DbSet<RecurringKind> RecurringKinds => Set<RecurringKind>();
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<AdminLogEntry> AdminLog => Set<AdminLogEntry>();
    public DbSet<ControlServer> ControlServers => Set<ControlServer>();
    public DbSet<PayfastSubscription> PayfastSubscriptions => Set<PayfastSubscription>();
    public DbSet<PayfastPayment> PayfastPayments => Set<PayfastPayment>();
    public DbSet<CompanyReview> CompanyReviews => Set<CompanyReview>();
    public DbSet<PilotInvite> PilotInvites => Set<PilotInvite>();
    public DbSet<UpdateRollout> UpdateRollouts => Set<UpdateRollout>();
    public DbSet<DeviceUpdateEvent> DeviceUpdateEvents => Set<DeviceUpdateEvent>();
    public DbSet<CompanyExport> CompanyExports => Set<CompanyExport>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // Pilot links (2026-10-15): found by the fingerprint of the token in the address, which is unique.
        mb.Entity<PilotInvite>().HasIndex(i => i.TokenHash).IsUnique();
        // A company's export (2026-10-22): its own listed newest first; the one we made found by its emailed link.
        mb.Entity<CompanyExport>().HasIndex(e => new { e.TenantId, e.CreatedUtc });
        mb.Entity<CompanyExport>().HasIndex(e => e.LinkHash);
        // Auto-update (2026-10-16): one rollout per version; a machine's update history read by version and by machine.
        mb.Entity<UpdateRollout>().HasIndex(r => r.Version).IsUnique();
        mb.Entity<DeviceUpdateEvent>().HasIndex(e => new { e.Version, e.Step });
        mb.Entity<DeviceUpdateEvent>().HasIndex(e => new { e.TenantId, e.DeviceId, e.AtUtc });
        mb.Entity<AccountToken>().HasIndex(t => t.TokenHash).IsUnique();
        // Platform admins (2026-10-13): found by email at sign-in, by link when they set up; the log read newest first.
        mb.Entity<PlatformAdmin>().HasIndex(a => a.Email).IsUnique();
        mb.Entity<PlatformAdmin>().HasIndex(a => a.LinkHash);
        mb.Entity<AdminLogEntry>().HasIndex(e => e.AtUtc);
        mb.Entity<AccountToken>().HasIndex(t => t.TechnicianId);
        mb.Entity<SeatAgreement>().HasIndex(a => new { a.TenantId, a.AgreedUtc });
        mb.Entity<TermsAcceptance>().HasIndex(a => a.TenantId);
        mb.Entity<WaitingListEntry>().HasIndex(w => new { w.Region, w.Email });
        mb.Entity<Tenant>().HasIndex(t => t.Slug).IsUnique();
        mb.Entity<Tenant>().HasIndex(t => t.TenantKey).IsUnique();
        mb.Entity<Technician>().HasIndex(t => t.Email).IsUnique();
        mb.Entity<Device>().HasIndex(d => new { d.TenantId, d.DeviceId }).IsUnique();
        // Not unique: machines that report no fingerprint all store "", and older rows have none.
        mb.Entity<Device>().HasIndex(d => new { d.TenantId, d.HardwareId });
        mb.Entity<ChatLine>().HasIndex(c => new { c.TenantId, c.TsUtc });
        mb.Entity<SessionLog>().HasIndex(s => new { s.TenantId, s.StartedUtc });
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.Number }).IsUnique();
        // Per-company numbers and file addresses (2026-10-23, HANDOVER §159): what the portal's addresses name.
        mb.Entity<Client>().HasIndex(c => new { c.TenantId, c.Number }).IsUnique();
        mb.Entity<KbArticle>().HasIndex(a => new { a.TenantId, a.Number }).IsUnique();
        mb.Entity<RecurringTask>().HasIndex(r => new { r.TenantId, r.Number }).IsUnique();
        mb.Entity<TicketPhoto>().HasIndex(p => new { p.TenantId, p.Token });
        mb.Entity<TicketAttachment>().HasIndex(a => new { a.TenantId, a.Token });
        mb.Entity<KbImage>().HasIndex(i => new { i.TenantId, i.Token });
        mb.Entity<SessionRecording>().HasIndex(r => new { r.TenantId, r.Token });
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.Status });
        mb.Entity<Ticket>().HasMany(t => t.Notes).WithOne(n => n.Ticket).HasForeignKey(n => n.TicketId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<TicketNote>().HasIndex(n => new { n.TenantId, n.TicketId });
        // Latest activity (HANDOVER §164) reads a company's entries newest first, and counts those since a moment.
        mb.Entity<TicketNote>().HasIndex(n => new { n.TenantId, n.CreatedUtc });
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.CreatedUtc });

        // The three ways photos are read: one ticket's, one half-finished booking's, and the
        // sweep that clears staged photos nobody ever submitted.
        mb.Entity<TicketPhoto>().HasIndex(p => new { p.TenantId, p.TicketId });
        mb.Entity<TicketPhoto>().HasIndex(p => new { p.TenantId, p.StageId });
        mb.Entity<TicketPhoto>().HasIndex(p => p.UploadedUtc);
        mb.Entity<TicketMachine>().HasIndex(m => new { m.TenantId, m.TicketId });
        mb.Entity<TicketMachine>().HasOne(m => m.Ticket).WithMany().HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
        // Cascade: a deleted ticket's photos have nothing left to be evidence about. The files on
        // disk are removed by TicketPhotoService, which is the only thing that deletes either.
        mb.Entity<TicketPhoto>().HasOne(p => p.Ticket).WithMany().HasForeignKey(p => p.TicketId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<Client>().HasIndex(c => new { c.TenantId, c.Name });
        mb.Entity<Device>().HasOne(d => d.Client).WithMany().HasForeignKey(d => d.ClientId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<Ticket>().HasOne(t => t.Client).WithMany().HasForeignKey(t => t.ClientId).OnDelete(DeleteBehavior.SetNull);

        // The two ways contacts are read: everyone at one client, and the type-ahead by name.
        mb.Entity<Contact>().HasIndex(c => new { c.TenantId, c.ClientId });
        mb.Entity<Contact>().HasIndex(c => new { c.TenantId, c.Name });
        // Cascade, unlike everything else here: a contact has no meaning without its client, so
        // deleting the client and leaving the people behind would leave rows nothing can reach.
        mb.Entity<Contact>().HasOne(c => c.Client).WithMany().HasForeignKey(c => c.ClientId).OnDelete(DeleteBehavior.Cascade);
        // SetNull on both of these, for the same reason ClientId is: archiving is the normal way
        // a contact leaves, and on the rare hard delete a machine must lose its person rather than
        // be deleted along with them.
        mb.Entity<Device>().HasOne(d => d.Contact).WithMany().HasForeignKey(d => d.ContactId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<Ticket>().HasOne(t => t.Contact).WithMany().HasForeignKey(t => t.ContactId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.ClientId });
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.Category });
        // The billing queue and the technician's own list both ask by lane.
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.Lane });
        mb.Entity<CatalogItem>().HasIndex(i => new { i.TenantId, i.MatchKey });
        mb.Entity<QuoteLine>().HasIndex(q => new { q.TenantId, q.TicketId });
        mb.Entity<Supplier>().HasIndex(s => new { s.TenantId, s.MatchKey });
        mb.Entity<QuoteSignature>().HasIndex(s => s.TenantId);
        mb.Entity<QuoteSignature>().HasIndex(s => s.ImageToken);
        // The knowledge base (3.8.5): read by section (a client's, or General), by the ticket it was
        // written up from, and an article's history newest first.
        mb.Entity<KbArticle>().HasIndex(a => new { a.TenantId, a.ClientId });
        mb.Entity<KbArticle>().HasIndex(a => new { a.TenantId, a.FromTicketId });
        // SetNull: a client deleted outright leaves its articles in General rather than taking the
        // know-how with it. Archiving, the normal way a client goes, touches nothing.
        mb.Entity<KbArticle>().HasOne(a => a.Client).WithMany().HasForeignKey(a => a.ClientId).OnDelete(DeleteBehavior.SetNull);
        mb.Entity<KbRevision>().HasIndex(r => new { r.TenantId, r.ArticleId, r.Version });
        // Everybody may edit, so two people may save the same version at once: the second is refused.
        mb.Entity<KbArticle>().Property(a => a.Version).IsConcurrencyToken();
        // Folders (2026-10-06) are read a section at a time. No foreign keys on purpose: the service
        // is the only thing that removes a folder, and it moves what was in it up first.
        mb.Entity<KbFolder>().HasIndex(f => new { f.TenantId, f.ClientId });
        mb.Entity<KbArticle>().HasIndex(a => new { a.TenantId, a.FolderId });
        // A quote line's photos (2026-10-06) are found by the line.
        mb.Entity<TicketPhoto>().HasIndex(p => new { p.TenantId, p.QuoteLineId });
        // The badge asks "what is waiting on me", which is by person and still open.
        mb.Entity<TicketMention>().HasIndex(m => new { m.TenantId, m.Person, m.AnsweredUtc });
        // Office chat (3.8.1): a conversation is read by who it is to and who it is from; how far
        // each person has read each one is one row.
        mb.Entity<StaffMessage>().HasIndex(m => new { m.TenantId, m.To, m.Id });
        mb.Entity<StaffMessage>().HasIndex(m => new { m.TenantId, m.From, m.Id });
        mb.Entity<StaffChatRead>().HasIndex(r => new { r.TenantId, r.Person, r.With }).IsUnique();
        mb.Entity<WebChat>().HasIndex(c => new { c.TenantId, c.EndedUtc });
        mb.Entity<WebChatLine>().HasIndex(l => new { l.WebChatId, l.Id });
        // Tickets by email (3.8.4): one settings row per company; the log read newest first, asked
        // "seen this Message-ID before?", and counted per sender for the hourly limit.
        mb.Entity<TicketIntakeSettings>().HasIndex(s => s.TenantId).IsUnique();
        mb.Entity<InboundMail>().HasIndex(m => new { m.TenantId, m.Id });
        mb.Entity<InboundMail>().HasIndex(m => new { m.TenantId, m.MessageId });
        mb.Entity<InboundMail>().HasIndex(m => new { m.TenantId, m.FromAddress, m.ReceivedUtc });
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.RequesterEmail });
        // A ticket's updates to its client, read whenever the ticket is drawn or a reply arrives.
        mb.Entity<TicketClientUpdate>().HasIndex(u => new { u.TenantId, u.TicketId });
        // A ticket's files are read with its story, by ticket; a comment's by the comment.
        mb.Entity<TicketAttachment>().HasIndex(a => new { a.TenantId, a.TicketId });
        mb.Entity<TicketAttachment>().HasIndex(a => a.NoteId);
        mb.Entity<TicketMention>().HasOne(m => m.Ticket).WithMany(t => t.Mentions).HasForeignKey(m => m.TicketId);
        mb.Entity<TicketMention>().HasOne(m => m.Note).WithMany().HasForeignKey(m => m.NoteId);
        // The children of one job, read whenever a parent is opened or totalled.
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.ParentTicketId });
        // SetNull, NOT cascade: deleting a parent must not take its children's own records with
        // it. Each child is the record of a session somebody actually worked, and that survives
        // the job it was filed under.
        mb.Entity<Ticket>().HasOne(t => t.ParentTicket).WithMany()
            .HasForeignKey(t => t.ParentTicketId).OnDelete(DeleteBehavior.SetNull);
        // The newest ticket for one machine, which is what the 30-minute reuse window asks for.
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.DeviceId, t.CreatedUtc });

        // The two ways runs are read: newest first for a tenant, and the history of one machine.
        mb.Entity<HealthCheckRun>().HasIndex(r => new { r.TenantId, r.ReceivedUtc });
        mb.Entity<HealthCheckRun>().HasIndex(r => new { r.TenantId, r.DeviceId, r.RunUtc });
        // Remembered unattended passwords: one row per technician per machine.
        mb.Entity<SavedUnattendedPassword>().HasIndex(p => new { p.DeviceRowId, p.TechnicianId }).IsUnique();
        mb.Entity<SavedUnattendedPassword>().HasIndex(p => p.TenantId);
        mb.Entity<SavedUnattendedPassword>().HasOne(p => p.Device).WithMany().HasForeignKey(p => p.DeviceRowId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<SavedUnattendedPassword>().HasOne(p => p.Technician).WithMany().HasForeignKey(p => p.TechnicianId).OnDelete(DeleteBehavior.Cascade);
        mb.Entity<DeviceRemoval>().HasIndex(r => new { r.TenantId, r.RemovedUtc });
        mb.Entity<DeviceRemoval>().HasIndex(r => new { r.TenantId, r.DeviceId });
        mb.Entity<OutsiderCode>().HasIndex(c => c.CodeHash);
        // PayFast (2026-10-13): a notice names its subscription by OUR reference (m_payment_id) and later by PayFast's
        // token; the same notice can arrive twice, so a payment is unique on PayFast's id AND its status (a cancellation
        // may carry the id of the payment that started it).
        mb.Entity<CompanyReview>().HasIndex(r => r.TenantId).IsUnique();
        mb.Entity<CompanyReview>().HasIndex(r => new { r.Status, r.DecidedUtc });
        mb.Entity<PayfastSubscription>().HasIndex(p => p.Reference).IsUnique();
        mb.Entity<PayfastSubscription>().HasIndex(p => p.Token);
        mb.Entity<PayfastSubscription>().HasIndex(p => new { p.TenantId, p.CreatedUtc });
        mb.Entity<PayfastPayment>().HasIndex(p => new { p.PfPaymentId, p.Status }).IsUnique();
        mb.Entity<PayfastPayment>().HasIndex(p => new { p.SubscriptionId, p.ReceivedUtc });
        // Recurring tasks (2026-10-07): the sweep asks for what is due; one ticket per schedule per day.
        mb.Entity<RecurringTask>().HasIndex(r => new { r.TenantId, r.CancelledUtc, r.NextDue });
        mb.Entity<RecurringKind>().HasIndex(k => new { k.TenantId, k.MatchKey });
        mb.Entity<Ticket>().HasIndex(t => new { t.TenantId, t.RecurringTaskId, t.DueOn });
        mb.Entity<OutsiderCode>().HasIndex(c => new { c.TenantId, c.DeviceId, c.CreatedUtc });
        // Deliberately no FK to Device: a machine that is uninstalled and reinstalled gets a new
        // DeviceId (handover §15), and losing its history to a cascade would defeat the point of
        // keeping reports at all.

        // SQLite stores no timezone, so values come back as Unspecified and every ToLocalTime()
        // would silently be wrong by the local offset. Mark every DateTime as UTC on read.
        var toUtc = new ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var toUtcNullable = new ValueConverter<DateTime?, DateTime?>(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        foreach (var entity in mb.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(toUtc);
                else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(toUtcNullable);
            }
    }
}

public enum ReviewStatus { Waiting = 0, Published = 1, Declined = 2 }

/// <summary>
/// A company's review of the product (2026-10-14, Clint): stars and words, the name and business to show, and the owner's
/// permission to publish them. **Nothing is public until a platform admin approves it** (Clint: "after approval"). One per
/// company: writing it again replaces it and it waits for approval again.
/// </summary>
public sealed class CompanyReview
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>1 to 5.</summary>
    public int Stars { get; set; }
    public string Words { get; set; } = "";
    /// <summary>The name to show — the person's, as they typed it.</summary>
    public string ShownName { get; set; } = "";
    public string BusinessName { get; set; } = "";
    /// <summary>Their business's website, https only, or "" — linked with rel="ugc".</summary>
    public string BusinessUrl { get; set; } = "";
    /// <summary>The tick: they allow their name, business and words on our website (POPIA). Never stored false.</summary>
    public bool ConsentToPublish { get; set; }
    public string WrittenBy { get; set; } = "";
    public string WrittenByEmail { get; set; } = "";
    public DateTime WrittenUtc { get; set; } = DateTime.UtcNow;
    public ReviewStatus Status { get; set; }
    public string DecidedBy { get; set; } = "";
    public DateTime? DecidedUtc { get; set; }
}

/// <summary>
/// A link that signs ONE pilot company up (2026-10-15, <see cref="Services.PilotService"/>). Made by a platform admin, sent
/// by hand, works once and for 30 days. Only the fingerprint of its token is kept: the link is shown once, when it is made.
/// </summary>
public sealed class PilotInvite
{
    public int Id { get; set; }
    /// <summary><see cref="Services.PilotService.Hash"/> of the token in the address. Never the token.</summary>
    public string TokenHash { get; set; } = "";
    /// <summary>Who it is for — filled in on their form, and how the admin tells the links apart.</summary>
    public string CompanyName { get; set; } = "";
    /// <summary>Their email, filled in on their form if known; "" when not.</summary>
    public string Email { get; set; } = "";
    /// <summary>For the admins only.</summary>
    public string Note { get; set; } = "";
    public string MadeBy { get; set; } = "";
    public DateTime MadeUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    /// <summary>When a company was made with it — set in the same save as the company, once.</summary>
    public DateTime? UsedUtc { get; set; }
    public int? TenantId { get; set; }
    public DateTime? CancelledUtc { get; set; }
}

public enum PayfastStatus
{
    /// <summary>Checkout made; PayFast has not yet told us the card went through.</summary>
    AwaitingCard = 0,
    Active = 1,
    Cancelled = 2,
}

/// <summary>
/// One PayFast subscription (2026-10-13, roadmap 4.8). Made when we build a checkout — our <see cref="Reference"/> goes
/// to PayFast as <c>m_payment_id</c> and comes back on every notice — and filled in by the notices: PayFast's
/// <see cref="Token"/> (the only handle for the API afterwards; the sandbox never shows it anywhere else), the status, the
/// last payment. A company may have several over its life (cancelled, then subscribed again); the newest is the one.
/// <para><see cref="Sandbox"/> is fixed at creation: at go-live a sandbox subscription is never taken for a real one.</para>
/// <para>Dates PayFast deals in are kept as its own "yyyy-MM-dd" TEXT — never a DateTime (HANDOVER §108's trap).</para>
/// </summary>
public sealed class PayfastSubscription
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Reference { get; set; } = "";
    /// <summary>"signup", "upgrade", "test" — why it was made.</summary>
    public string Purpose { get; set; } = "";
    public bool Sandbox { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string ItemName { get; set; } = "";
    /// <summary>What the checkout asks now: 0 for a trial (the card is checked, nothing is taken).</summary>
    public long InitialCents { get; set; }
    /// <summary>What each renewal is — kept true by every seat change (stage C): what PayFast has been told and accepted.</summary>
    public long AmountCents { get; set; }
    /// <summary>When PayFast last accepted a new amount (stage C).</summary>
    public DateTime? AmountChangedUtc { get; set; }
    /// <summary>
    /// Why PayFast has NOT yet been told the price the company owes now — its API refused or could not be reached — or "".
    /// The sweep tries again; Billing says so until it lands (stage C).
    /// </summary>
    public string AmountProblem { get; set; } = "";

    // ---- stage D: a payment that did not come, and the end (2026-10-14) ----

    /// <summary>The charge date ("yyyy-MM-dd") whose payment has not come, or "" — PayFast sends nothing for a failure.</summary>
    public string LateDue { get; set; } = "";
    /// <summary>The reminders sent for <see cref="LateDue"/>: 1 (a day late), 2 (a week), 3 (the day before suspension).</summary>
    public int LateNotices { get; set; }
    /// <summary>When WE suspended the company for this subscription's missing payment — so a payment restores only that.</summary>
    public DateTime? SuspendedUtc { get; set; }
    /// <summary>Cancelled: the last day of service ("yyyy-MM-dd"), the end of the month paid for — or "".</summary>
    public string EndsOn { get; set; } = "";
    /// <summary>When the company was closed because this subscription ended.</summary>
    public DateTime? EndedUtc { get; set; }
    public int Frequency { get; set; } = 3;
    /// <summary>0 = until cancelled.</summary>
    public int Cycles { get; set; }
    /// <summary>The first renewal, "yyyy-MM-dd".</summary>
    public string BillingDate { get; set; } = "";
    public string? Token { get; set; }
    public PayfastStatus Status { get; set; }
    public DateTime? ActivatedUtc { get; set; }
    public DateTime? LastPaymentUtc { get; set; }
    /// <summary>The next charge PayFast told us of, "yyyy-MM-dd".</summary>
    public string NextRunDate { get; set; } = "";
    public DateTime? CancelledUtc { get; set; }
}

/// <summary>
/// One notice from PayFast that proved itself genuine — signature, address, merchant, and PayFast's own "VALID" — kept
/// for ever whether or not it was acted on. <see cref="Applied"/> false with a <see cref="Problem"/> is money (or a
/// cancellation) that needs a person: the first payment for the wrong amount, say. A notice that fails those checks is
/// never stored — only logged — so nobody can fill this table by posting at it.
/// </summary>
public sealed class PayfastPayment
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int SubscriptionId { get; set; }
    public string PfPaymentId { get; set; } = "";
    /// <summary>As PayFast sent it: COMPLETE or CANCELLED.</summary>
    public string Status { get; set; } = "";
    public long AmountGrossCents { get; set; }
    public long AmountFeeCents { get; set; }
    public long AmountNetCents { get; set; }
    public long ExpectedCents { get; set; }
    public bool Applied { get; set; }
    public string Problem { get; set; } = "";
    public string BillingDate { get; set; } = "";
    public string ItemName { get; set; } = "";
    public bool Sandbox { get; set; }
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One technician has typed one machine's unattended password correctly, and need not again
/// (2026-10-06). Clint: "entering a password remembers it, but only for the particular technician
/// that entered it." **Not the password, nor its hash** — only that it was typed right, and against
/// WHICH password: <see cref="PasswordSalt"/> is the machine's salt at that moment, and a password
/// changed or removed at the machine has a new one, so every row made against the old is dead. The
/// hub deletes them then as well, so "who has it saved" is never a list of the stale.
///
/// Keyed by the device ROW (<see cref="Device.Id"/>), which a reinstall keeps, not by DeviceId,
/// which it does not — the password itself survives a reinstall the same way. Cascades from both:
/// a removed technician forgets everything (technician ids are AUTOINCREMENT, never reused), a
/// removed machine has nothing left to remember.
/// </summary>
public sealed class SavedUnattendedPassword
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int DeviceRowId { get; set; }
    public Device Device { get; set; } = null!;
    public int TechnicianId { get; set; }
    public Technician Technician { get; set; } = null!;
    public string PasswordSalt { get; set; } = "";
    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A machine taken off a company's list (2026-10-06, the free version). Kept for two reasons: the free
/// version may remove only <c>FreePlan.RemovalsPerMonth</c> a calendar month, counted from these; and
/// the install that was removed (<see cref="DeviceId"/>) is refused if it says hello again — its agent
/// is still running over there and would otherwise put itself straight back on the list. A fresh install
/// on the same computer has a new device id and is a new machine, subject to the limit like any other.
/// </summary>
public sealed class DeviceRemoval
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string RemovedBy { get; set; } = "";
    /// <summary>Why (<c>RemovalReasons.All</c>) and the note — the full version's removals carry both (2026-10-06);
    /// the free version's are its own computers and carry neither.</summary>
    public string Reason { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime RemovedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A code the person at a machine gave somebody outside the company (roadmap 3.9, 2026-10-06). Kept
/// after it is spent or stopped, so the company can see who was let in, when, and by whom. Only the
/// HASH of the code is stored: the digits exist in the one reply that made them.
/// </summary>
public sealed class OutsiderCode
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string DeviceId { get; set; } = "";
    /// <summary>SHA-256 of the nine digits (OutsiderCodeService.Hash).</summary>
    public string CodeHash { get; set; } = "";
    /// <summary>Who the customer said it is for — shown to them when it is used, and to the company.</summary>
    public string ForWhom { get; set; } = "";
    /// <summary>Who made it: the person signed in at the machine.</summary>
    public string MadeBy { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
    public string UsedBy { get; set; } = "";
    /// <summary>Stopped before it was used: the customer stopped it, made a new one, or outside help
    /// was switched off. Why is in <see cref="EndedWhy"/>.</summary>
    public DateTime? EndedUtc { get; set; }
    public string EndedWhy { get; set; } = "";
    /// <summary>
    /// Made by a TECHNICIAN in this session to bring somebody in (2026-10-22, §155), rather than by the person at
    /// the machine. It works only for this session, and that technician lets them in. Empty: the customer's code.
    /// </summary>
    public string SessionId { get; set; } = "";
    /// <summary>The technician who made a session code: the one asked to let them in.</summary>
    public int InvitedById { get; set; }
}

/// <summary>
/// How far one release has gone out (2026-10-16, auto-update, docs/NEXT-SESSION-AUTOUPDATE.md). One row per version, made
/// at Ring 0 the first time this server is asked about a stamped release. Every rule is <see cref="Services.UpdateService"/>.
/// </summary>
public sealed class UpdateRollout
{
    public int Id { get; set; }
    public string Version { get; set; } = "";
    /// <summary>The furthest ring offered it: 0 our own company (not billed), 1 the pilots, 2 everybody.</summary>
    public int Ring { get; set; }
    public bool Paused { get; set; }
    /// <summary>Who paused it, or "automatically: …" when too many machines rolled back.</summary>
    public string PausedWhy { get; set; } = "";
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ChangedUtc { get; set; } = DateTime.UtcNow;
    public string ChangedBy { get; set; } = "";
}

/// <summary>One step of one machine's update, as its agent reported it (or Offered, the server's own). The machine's history.</summary>
public sealed class DeviceUpdateEvent
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public string DeviceId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>A <see cref="RemoteSupport.Shared.UpdateSteps"/> value.</summary>
    public string Step { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Where a <see cref="CompanyExport"/> has got to. Stored as the number: never renumber.</summary>
public enum ExportState { Waiting = 0, Making = 1, Ready = 2, Failed = 3, Expired = 4 }

/// <summary>
/// A company's own data, made into one zip to download (roadmap 4.9, Clint 2026-10-22: "the ability for companies to
/// export their clients, tickets and knowledgebase"): its clients and contacts in the columns Import clients reads, its
/// tickets with their story, quote lines, photos and files, and its knowledge base. Made in the background — a big
/// company's is minutes of work — and kept for <c>CompanyExportService.KeepDays</c>. Every rule is
/// <c>Services/CompanyExport.cs</c>.
///
/// <para>Two ways one is asked for: by the company's owner or a supervisor on Portal → Export, downloaded there; or by
/// one of US for a company that can no longer sign in — suspended, or closed and inside the 60 days the terms give —
/// and then its owners are emailed a link (<see cref="ByUs"/>, <see cref="LinkHash"/>).</para>
/// </summary>
public sealed class CompanyExport
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public ExportState State { get; set; }
    /// <summary>Session recordings copied in as well — ticked by whoever asked; they can be gigabytes each.</summary>
    public bool IncludeRecordings { get; set; }
    /// <summary>The person who asked — their name, or the platform admin's for one we made.</summary>
    public string RequestedBy { get; set; } = "";
    /// <summary>Their row, for a company's own request: who is emailed when it is ready.</summary>
    public int? RequestedById { get; set; }
    /// <summary>Made by a platform admin for a company that cannot sign in; its owners get a link.</summary>
    public bool ByUs { get; set; }
    /// <summary>Where the making has got to, for the page while it waits: "Tickets: 1,200 of 4,120".</summary>
    public string Progress { get; set; } = "";
    /// <summary>What is in it, once made: "312 clients, 4,120 tickets, 88 articles, 1,903 files".</summary>
    public string Summary { get; set; } = "";
    /// <summary>File name under the company's export folder. Generated, never anybody's own words.</summary>
    public string FileName { get; set; } = "";
    public long Bytes { get; set; }
    /// <summary>SHA-256 of the emailed link's token (one we made), never the token. Empty for a company's own.</summary>
    public string LinkHash { get; set; } = "";
    /// <summary>Why it could not be made, in words for the page.</summary>
    public string Error { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>When the file is deleted. Set once it is ready.</summary>
    public DateTime? ExpiresUtc { get; set; }
    public int Downloads { get; set; }
    public DateTime? DownloadedUtc { get; set; }
}
