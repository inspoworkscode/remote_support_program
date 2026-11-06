using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteSupport.Platform.Data;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Owner;

/// <summary>
/// The mailbox the PRODUCT writes from — trial warnings today, and anything else the platform
/// itself has to say to a tenant. Not a tenant's own mailbox: that one is for writing to their
/// customers, and a brand-new trial has not configured it. See PlatformSettings.
/// </summary>
public sealed class MailModel : PageModel
{
    private readonly PlatformMailService _mail;
    private readonly TrialNoticeService _trials;
    private readonly TicketIntake _intake;
    private readonly PlatformAdmins _admins;
    public MailModel(PlatformMailService mail, TrialNoticeService trials, TicketIntake intake, PlatformAdmins admins) { _mail = mail; _trials = trials; _intake = intake; _admins = admins; }

    /// <summary>
    /// The platform's mail is a Full admin's (2026-10-13): a Viewer or Manager sees this page and changes nothing. Every
    /// change is logged with who made it — never the password.
    /// </summary>
    public override async Task OnPageHandlerExecutionAsync(Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutingContext ctx,
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutionDelegate next)
    {
        if (HttpMethods.IsPost(Request.Method))
        {
            var me = AdminIdentity.ActorOf(User);
            if (!PlatformAdmins.MayRun(me.Level))
            {
                Message = "Only a Full admin or an Owner can change the platform's mail."; IsError = true;
                ctx.Result = await Render();
                return;
            }
            var done = await next();
            var what = ctx.HandlerMethod?.Name switch
            {
                "Forwarding" => "Changed the forwarding mailbox for tickets by email",
                "Test" => "Sent a test email",
                "RunTrialPass" => "Ran the trial notice pass",
                _ => "Changed the platform mailbox"
            };
            if (done.Exception == null) await _admins.LogAsync(me.Name, what);
            return;
        }
        await next();
    }

    /// <summary>
    /// The forwarding mailbox for tickets by email (3.8.4): the address companies forward their
    /// support address to, and how to read it. Saving tests it at once — and the first time, notes
    /// where it is up to, so mail forwarded before the first look is not passed over.
    /// </summary>
    public async Task<IActionResult> OnPostForwardingAsync(string? intakeAddress, string? intakeHost, int intakePort, string? intakeSecurity,
        string? intakeUser, string? intakePassword)
    {
        await _mail.SaveAsync(s =>
        {
            var host = (intakeHost ?? "").Trim(); var user = (intakeUser ?? "").Trim();
            if (host != s.IntakeImapHost || user != s.IntakeImapUser) { s.IntakeUidValidity = 0; s.IntakeLastUid = 0; }
            s.IntakeAddress = (intakeAddress ?? "").Trim().ToLowerInvariant();
            s.IntakeImapHost = host;
            s.IntakeImapPort = intakePort is > 0 and < 65536 ? intakePort : 993;
            s.IntakeImapSecurity = intakeSecurity == "starttls" ? "starttls" : "ssl";
            s.IntakeImapUser = user;
            if (!string.IsNullOrEmpty(intakePassword)) s.IntakeImapPasswordEnc = _mail.Protect(intakePassword);
            s.IntakePaused = false; s.IntakeFailures = 0; s.IntakeNextTryUtc = null;
        });
        var settings = await _mail.GetAsync();
        if (!settings.ForwardingOffered) { Message = "Saved. Forwarding is not offered until the address and its mail server are both filled in."; return await Render(); }
        var (ok, words) = await _intake.TestForwardingAsync();
        IsError = !ok;
        Message = (ok ? "Saved and tested. " : "Saved, but the test found a problem: ") + words;
        return await Render();
    }

    public PlatformSettings Settings { get; private set; } = new();
    /// <summary>Whether a password is stored. The password itself is never sent to the browser.</summary>
    public bool HasPassword { get; private set; }
    public string Message { get; private set; } = "";
    public bool IsError { get; private set; }

    private async Task<IActionResult> Render()
    {
        ViewData["Section"] = "owner";
        Settings = await _mail.GetAsync();
        HasPassword = !string.IsNullOrEmpty(Settings.SmtpPasswordEnc);
        return Page();
    }

    public Task<IActionResult> OnGetAsync() => Render();

    public async Task<IActionResult> OnPostAsync(string? smtpHost, int smtpPort, string? smtpUser, string? smtpFrom,
        string? smtpFromName, bool smtpUseSsl, string? noticeCopyTo, string? smtpPassword, bool clearPassword)
    {
        await _mail.SaveAsync(s =>
        {
            s.SmtpHost = (smtpHost ?? "").Trim();
            s.SmtpPort = smtpPort is > 0 and < 65536 ? smtpPort : 587;
            s.SmtpUser = (smtpUser ?? "").Trim();
            s.SmtpFrom = (smtpFrom ?? "").Trim();
            s.SmtpFromName = (smtpFromName ?? "").Trim();
            s.SmtpUseSsl = smtpUseSsl;
            s.NoticeCopyTo = (noticeCopyTo ?? "").Trim();
            // A blank password box LEAVES THE STORED ONE ALONE. Treating blank as "clear it" is
            // how someone editing the reply-to address silently breaks mail a fortnight later,
            // and the failure surfaces as a trial warning that never arrived.
            if (clearPassword) s.SmtpPasswordEnc = "";
            else if (!string.IsNullOrEmpty(smtpPassword)) s.SmtpPasswordEnc = _mail.Protect(smtpPassword);
        });
        Message = "Saved.";
        return await Render();
    }

    /// <summary>
    /// Sends one real message through the real settings. Mail that has never been tried is mail
    /// that does not work: the first time it matters is the moment a customer's trial is lapsing,
    /// which is the worst possible time to discover the password was wrong.
    /// </summary>
    public async Task<IActionResult> OnPostTestAsync(string? testTo)
    {
        if (string.IsNullOrWhiteSpace(testTo)) { Message = "Enter an address to send the test to."; IsError = true; return await Render(); }
        var r = await _mail.SendAsync(new[] { testTo },
            "Test from The Remote Support Program",
            "This is a test of the platform mailbox.\n\nIf you are reading it, trial notices will reach your customers.\n");
        IsError = r != "sent";
        Message = r == "sent" ? $"Test sent to {testTo}." : "Could not send: " + r;
        return await Render();
    }

    /// <summary>
    /// Runs the trial-notice pass now instead of waiting for the hourly one. It respects
    /// everything the scheduled pass respects, including only sending each milestone once, so it
    /// is safe to press and will not spam anybody.
    /// </summary>
    public async Task<IActionResult> OnPostRunTrialPassAsync()
    {
        try { await _trials.RunPassAsync(); Message = "Trial notice pass finished. Anything it sent is in the server log."; }
        catch (System.Exception ex) { Message = "The pass failed: " + ex.Message; IsError = true; }
        return await Render();
    }
}
