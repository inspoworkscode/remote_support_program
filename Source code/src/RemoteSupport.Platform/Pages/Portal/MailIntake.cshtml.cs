using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Tickets by email and the contact form (3.8.4, 2026-10-05): the owner and supervisors' page. It
/// holds a mailbox password, so it is admin-only like Health check (the page policy in Program.cs,
/// not the menu). Every rule is <see cref="TicketIntake"/>; this page only collects and shows.
///
/// Clint's standing rule — one control beats two — is why there is ONE button for the email half:
/// Save and test. Settings that were never tried are settings that do not work. Microsoft 365 (2026-10-19,
/// §151) adds the one it cannot do without — Sign in with Microsoft, which saves the form first — and Disconnect.
/// </summary>
public sealed class MailIntakeModel : PortalPage
{
    private readonly TicketIntake _intake;
    private readonly PlatformMailService _platformMail;
    private readonly MicrosoftSignIn _ms;
    public MailIntakeModel(TenantService t, TicketIntake intake, PlatformMailService platformMail, MicrosoftSignIn ms) : base(t)
    { _intake = intake; _platformMail = platformMail; _ms = ms; }

    public TicketIntakeSettings S { get; private set; } = new();
    public PlatformSettings P { get; private set; } = new();
    public List<InboundMail> Recent { get; private set; } = new();
    public List<string> TestLines { get; private set; } = new();
    public bool TestOk { get; private set; }
    public bool HasImapPassword => S.ImapPasswordEnc.Length > 0;
    public bool HasSmtpPassword => S.SmtpPasswordEnc.Length > 0;
    /// <summary>This server has our Microsoft app, so the Microsoft 365 way can be chosen.</summary>
    public bool MicrosoftOffered { get; private set; }
    public bool MicrosoftConnected => TicketIntake.MicrosoftConnected(S);
    /// <summary>The link a Microsoft 365 administrator opens to approve the app for their whole organisation.</summary>
    public string AdminConsentUrl { get; private set; } = "";
    /// <summary>Microsoft's answer to the last sign-in, as a short code (codes, not words, in an address — §105).</summary>
    public string MsCode { get; private set; } = "";
    public string Origin => $"{Request.Scheme}://{Request.Host}";
    public string FormLink => $"{Origin}/contact/{Tenant.Slug}";
    public string FormSnippet => $"<script src=\"{Origin}/contactform/{Tenant.Slug}.js\" async></script>";
    public string ReplyPreview => TicketIntake.ReplyText(S, Tenant, 1234, "Jane", "My printer will not print");

    /// <param name="ms">Set by the sign-in's way back (<see cref="MailIntakeMicrosoftModel"/>): "connected", or why not.</param>
    public async Task<IActionResult> OnGetAsync(string? ms)
    {
        if (!await LoadAsync()) return Redirect("/login");
        await FillAsync();
        MsCode = (ms ?? "").Length <= 20 ? ms ?? "" : "";
        if (MsCode == "connected" && MicrosoftConnected)
        {
            // Tried at once, as Save and test is: a sign-in that cannot read the mailbox is known now, not in a minute.
            var (ok, lines) = await _intake.TestAsync(TenantId);
            TestOk = ok; TestLines = lines;
            await FillAsync();
            Message = $"Signed in with Microsoft as {S.MsUser}." + (ok ? $" Email to {S.Address} now becomes a ticket." : "");
        }
        else if (MsCode == "approved") Message = MicrosoftSignIn.CallbackWords(MsCode);
        else if (MsCode.Length > 0 && MsCode != "connected") Error = MicrosoftSignIn.CallbackWords(MsCode);
        return Page();
    }

    private async Task FillAsync()
    {
        S = await _intake.GetAsync(TenantId);
        P = await _platformMail.GetAsync();
        Recent = await _intake.RecentAsync(TenantId, 50);
        MicrosoftOffered = await _ms.OfferedAsync();
        if (MicrosoftOffered) AdminConsentUrl = await _ms.AdminConsentUrlAsync();
        if (S.Address.Length == 0 && Tenant.SupportEmail.Length > 0) S.Address = Tenant.SupportEmail;
    }

    public async Task<IActionResult> OnPostEmailAsync(bool emailEnabled, string? address, string? way,
        string? imapHost, int imapPort, string? imapSecurity, string? imapUser, string? imapPassword, string? folder,
        bool autoReply, string? sendVia, bool smtpSameLogin, string? smtpHost, int smtpPort, string? smtpSecurity,
        string? smtpUser, string? smtpPassword, string? autoReplyText)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanManageCompany(MyRole)) return Forbid();
        var error = await SaveEmailAsync(false, emailEnabled, address, way, imapHost, imapPort, imapSecurity, imapUser, imapPassword, folder,
            autoReply, sendVia, smtpSameLogin, smtpHost, smtpPort, smtpSecurity, smtpUser, smtpPassword, autoReplyText);
        if (error.Length > 0) { Error = error; await FillAsync(); return Page(); }
        var s = await _intake.GetAsync(TenantId);

        if (_confirmDue && !s.AddressConfirmed)
        {
            var sent = await _intake.SendConfirmationAsync(TenantId);
            Message = sent.Length == 0 ? $"Saved. We have emailed a confirmation link to {s.Address} — open it and press Confirm. Until then, nothing forwarded is made into a ticket." : "";
            if (sent.Length > 0) Error = sent;
        }
        if (s.EmailEnabled || (s.Way == "mailbox" && s.ImapHost.Length > 0) || (s.Way == "microsoft" && TicketIntake.MicrosoftConnected(s)))
        {
            var (ok, lines) = await _intake.TestAsync(TenantId);
            TestOk = ok; TestLines = lines;
        }
        if (Message.Length == 0 && Error.Length == 0)
            Message = s.EmailEnabled ? (TestOk ? "Saved and tested. Email to your support address now becomes a ticket." : "Saved — but the test found a problem, below.")
                                     : "Saved. Email is not being turned into tickets.";
        await FillAsync();
        return Page();
    }

    /// <summary>
    /// Sign in with Microsoft: the form is saved first (the mailbox read is the support address typed in it), then the
    /// browser goes to Microsoft, which sends it back to <see cref="MicrosoftSignIn.CallbackPath"/>. The cookie carries
    /// the PKCE verifier, so only this browser can finish what it started.
    /// </summary>
    public async Task<IActionResult> OnPostMicrosoftAsync(bool emailEnabled, string? address, bool autoReply, string? sendVia, string? autoReplyText)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanManageCompany(MyRole)) return Forbid();
        if (!await _ms.OfferedAsync()) { Error = "Sign in with Microsoft is not available on this server yet — choose forwarding."; await FillAsync(); return Page(); }
        var before = await _intake.GetAsync(TenantId);
        var error = await SaveEmailAsync(true, emailEnabled, address, "microsoft", before.ImapHost, before.ImapPort, before.ImapSecurity, before.ImapUser, null, before.Folder,
            autoReply, sendVia, before.SmtpSameLogin, before.SmtpHost, before.SmtpPort, before.SmtpSecurity, before.SmtpUser, null, autoReplyText);
        if (error.Length > 0) { Error = error; await FillAsync(); return Page(); }
        var s = await _intake.GetAsync(TenantId);
        if (await _ms.StartAsync(TenantId, TechnicianId, s.Address) is not { } start)
        { Error = "Sign in with Microsoft is not available on this server yet — choose forwarding."; await FillAsync(); return Page(); }
        Response.Cookies.Append(MicrosoftSignIn.CookieName, start.cookie, new CookieOptions
        {
            // Lax: Microsoft sends the browser back with a top-level GET, which Lax allows.
            HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/portal/mailintake",
            MaxAge = TimeSpan.FromMinutes(15), IsEssential = true
        });
        return Redirect(start.url);
    }

    /// <summary>Disconnect: the Microsoft sign-in is deleted from our server.</summary>
    public async Task<IActionResult> OnPostDisconnectAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanManageCompany(MyRole)) return Forbid();
        var s = await _intake.GetAsync(TenantId);
        await _intake.DisconnectMicrosoftAsync(TenantId);
        Message = $"Disconnected. The Microsoft sign-in{(s.MsUser.Length > 0 ? $" for {s.MsUser}" : "")} is deleted from our server, and nothing is read from "
                + $"{(s.Address.Length > 0 ? s.Address : "the mailbox")} until somebody signs in again. To take the app off your organisation as well: "
                + "Microsoft Entra admin centre → Enterprise applications → The Remote Support Program → Delete (or, for one person, myapps.microsoft.com → Manage your apps).";
        await FillAsync();
        return Page();
    }

    private bool _confirmDue;

    /// <summary>The email half of the form, saved — or what is wrong with it, and nothing saved.</summary>
    private async Task<string> SaveEmailAsync(bool signingIn, bool emailEnabled, string? address, string? way,
        string? imapHost, int imapPort, string? imapSecurity, string? imapUser, string? imapPassword, string? folder,
        bool autoReply, string? sendVia, bool smtpSameLogin, string? smtpHost, int smtpPort, string? smtpSecurity,
        string? smtpUser, string? smtpPassword, string? autoReplyText)
    {
        var before = await _intake.GetAsync(TenantId);
        address = (address ?? "").Trim().ToLowerInvariant();
        way = way is "forward" or "microsoft" ? way : "mailbox";

        // Signing in with Microsoft reads the support address, so it needs one whether or not email is switched on yet.
        if (emailEnabled || address.Length > 0 || signingIn)
        {
            var problem = await _intake.CheckAddressAsync(TenantId, address);
            if (problem.Length > 0) return problem;
        }
        var p = await _platformMail.GetAsync();
        if (emailEnabled && way == "forward" && !p.ForwardingOffered)
            return "Forwarding is not available on this server yet — choose \"We check your mailbox\".";
        if (emailEnabled && way == "mailbox" && (string.IsNullOrWhiteSpace(imapHost) || string.IsNullOrWhiteSpace(imapUser) || (before.ImapPasswordEnc.Length == 0 && string.IsNullOrEmpty(imapPassword))))
            return "To check your mailbox we need its mail server, username and password.";
        if (way == "microsoft" && !signingIn && !await _ms.OfferedAsync())
            return "Sign in with Microsoft is not available on this server yet — choose forwarding.";
        var addressChanged = address != before.Address;
        // Not connected, or connected for another address: the button to press is Sign in with Microsoft.
        if (emailEnabled && way == "microsoft" && !signingIn && (!TicketIntake.MicrosoftConnected(before) || addressChanged))
            return TicketIntake.MicrosoftConnected(before)
                ? $"The support address changed, so we need a Microsoft sign-in for {address} — press Sign in with Microsoft."
                : "Press Sign in with Microsoft: until somebody signs in, we cannot read the mailbox.";

        var host = (imapHost ?? "").Trim(); var user = (imapUser ?? "").Trim(); var box = string.IsNullOrWhiteSpace(folder) ? "INBOX" : folder.Trim();
        // Another mailbox (or another folder, or another way in) is another set of message numbers: start again from "now".
        // With Microsoft the mailbox IS the support address, so a new address is a new mailbox.
        var otherMailbox = way != before.Way || (way == "mailbox" && (host != before.ImapHost || user != before.ImapUser || box != before.Folder))
                        || (way == "microsoft" && addressChanged);
        _confirmDue = way == "forward" && address.Length > 0 && (addressChanged || !before.AddressConfirmed);
        await _intake.SaveAsync(TenantId, s =>
        {
            s.EmailEnabled = emailEnabled; s.Address = address; s.Way = way;
            s.ImapHost = host; s.ImapPort = imapPort is > 0 and < 65536 ? imapPort : 993;
            s.ImapSecurity = imapSecurity == "starttls" ? "starttls" : "ssl";
            s.ImapUser = user; s.Folder = box;
            // A blank password box keeps the one stored — the rule on every mail page here.
            if (!string.IsNullOrEmpty(imapPassword)) s.ImapPasswordEnc = _intake.Protect(imapPassword);
            s.AutoReply = autoReply;
            s.SendVia = sendVia == "platform" ? "platform" : "own";
            s.SmtpSameLogin = smtpSameLogin;
            s.SmtpHost = (smtpHost ?? "").Trim(); s.SmtpPort = smtpPort is > 0 and < 65536 ? smtpPort : 465;
            s.SmtpSecurity = smtpSecurity == "starttls" ? "starttls" : "ssl";
            s.SmtpUser = (smtpUser ?? "").Trim();
            if (!string.IsNullOrEmpty(smtpPassword)) s.SmtpPasswordEnc = _intake.Protect(smtpPassword);
            var words = (autoReplyText ?? "").Replace("\r\n", "\n").Trim();
            s.AutoReplyText = words == TicketIntake.DefaultReply ? "" : words.Length > 4000 ? words[..4000] : words;
            if (otherMailbox) { s.UidValidity = 0; s.LastUid = 0; }
            if (addressChanged) { s.AddressConfirmed = false; s.GmailConfirmation = ""; }
            // A Microsoft sign-in is a live key to their mailbox: kept only while it is the way in, and for the mailbox it was given for.
            if (way != "microsoft" || addressChanged)
            {
                if (s.MsRefreshTokenEnc.Length > 0) MicrosoftSignIn.Forget(TenantId);
                s.MsRefreshTokenEnc = ""; s.MsTenant = ""; s.MsConnectedUtc = null; s.MsConnectedBy = ""; s.MsUser = "";
            }
            // Saving is what starts checking again after a refused password.
            s.Paused = false; s.Failures = 0; s.NextTryUtc = null; s.LastProblem = "";
        });
        return "";
    }

    public async Task<IActionResult> OnPostConfirmAsync()
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanManageCompany(MyRole)) return Forbid();
        var sent = await _intake.SendConfirmationAsync(TenantId);
        await FillAsync();
        if (sent.Length > 0) Error = sent; else Message = $"A new confirmation link is on its way to {S.Address}.";
        return Page();
    }

    public async Task<IActionResult> OnPostFormAsync(bool formEnabled, string? formHeading, string? formIntro, string? formPhone, bool formCompany, string? formThanks)
    {
        if (!await LoadAsync()) return Redirect("/login");
        if (!Permissions.CanManageCompany(MyRole)) return Forbid();
        await _intake.SaveAsync(TenantId, s =>
        {
            s.FormEnabled = formEnabled;
            s.FormHeading = Clip(formHeading, 80);
            s.FormIntro = Clip(formIntro, 400);
            s.FormPhone = formPhone is "off" or "required" ? formPhone : "optional";
            s.FormCompany = formCompany;
            s.FormThanks = Clip(formThanks, 400);
        });
        Message = formEnabled ? "Saved. The form takes messages — put it on your website with the line below." : "Saved. The form is switched off; it says so to anybody who opens it.";
        await FillAsync();
        return Page();
    }

    private static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length <= max ? s : s[..max]; }

    public static string OutcomePill(string outcome) => outcome switch
    {
        "ticket" => "pill-ok", "reply" => "pill-ok", "failed" => "pill-bad", _ => "pill-muted"
    };
    public static string OutcomeWord(string outcome) => outcome switch
    {
        "ticket" => "ticket opened", "reply" => "added to a ticket", "failed" => "failed", _ => "left alone"
    };
}
