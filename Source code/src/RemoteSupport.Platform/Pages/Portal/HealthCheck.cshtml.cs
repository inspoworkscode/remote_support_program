using Microsoft.AspNetCore.Mvc;
using RemoteSupport.Platform.Services;

namespace RemoteSupport.Platform.Pages.Portal;

/// <summary>
/// Where an IT company sets up the monthly maintenance and health check once: whether it runs, where
/// the report goes, and the mailbox the server sends it from. See docs/PLAN-HEALTHCHECK.md.
/// </summary>
public sealed class HealthCheckModel : PortalPage
{
    private readonly RemoteSupport.Platform.Hub.HubRegistry _hubs;
    public HealthCheckModel(TenantService t, RemoteSupport.Platform.Hub.HubRegistry hubs) : base(t) { _hubs = hubs; }

    /// <summary>Whether a password is on file. The password itself is never sent to the browser.</summary>
    public bool HasSmtpPassword { get; private set; }

    private async Task<bool> LoadPageAsync()
    {
        if (!await LoadAsync()) return false;
        HasSmtpPassword = !string.IsNullOrEmpty(Tenant.SmtpPasswordEnc);
        return true;
    }

    public async Task<IActionResult> OnGetAsync() => await LoadPageAsync() ? Page() : Redirect("/login");

    public async Task<IActionResult> OnPostAsync(
        bool healthCheckEnabled, string? healthCheckEmailTo, int healthCheckDayOfMonth, bool healthCheckRunMaintenance,
        string? smtpHost, int smtpPort, string? smtpUser, string? smtpFrom, bool smtpUseSsl,
        string? smtpPassword, bool clearSmtpPassword)
    {
        if (!await LoadPageAsync()) return Redirect("/login");

        var emailTo = (healthCheckEmailTo ?? "").Trim();
        var host = (smtpHost ?? "").Trim();

        // Only block on things that would produce a silent failure later. Half-finished settings are
        // allowed to save - an IT company will reasonably fill this in over two visits - but the
        // check is not allowed to be switched ON while it cannot possibly deliver, because the
        // failure would appear a month later on a customer machine rather than here.
        // A refused save is drawn with what was TYPED (2026-10-21). It used to be drawn from the database,
        // so the box came back unticked and the address and mail server blank — which reads as "it is off".
        IActionResult Refuse(string why)
        {
            Error = why;
            Tenant.HealthCheckEnabled = healthCheckEnabled; Tenant.HealthCheckEmailTo = emailTo;
            Tenant.HealthCheckDayOfMonth = Math.Clamp(healthCheckDayOfMonth, 0, 28); Tenant.HealthCheckRunMaintenance = healthCheckRunMaintenance;
            Tenant.SmtpHost = host; Tenant.SmtpPort = smtpPort; Tenant.SmtpUser = (smtpUser ?? "").Trim();
            Tenant.SmtpFrom = (smtpFrom ?? "").Trim(); Tenant.SmtpUseSsl = smtpUseSsl;
            return Page();
        }

        if (ReportAddressProblem(emailTo) is { Length: > 0 } bad) return Refuse(bad);
        if (healthCheckEnabled)
        {
            if (emailTo.Length == 0)
                return Refuse("Enter the address reports should go to before switching the health check on.");
            if (host.Length == 0)
                return Refuse("Enter a mail server before switching the health check on, or reports will have no way out.");
            if (smtpUser?.Trim().Length > 0 && !HasSmtpPassword && string.IsNullOrEmpty(smtpPassword) && !clearSmtpPassword)
                return Refuse("That mail server needs a password for the username given.");
        }

        var err = await Tenants.UpdateBrandingAsync(TenantId, t =>
        {
            t.HealthCheckEnabled = healthCheckEnabled;
            t.HealthCheckEmailTo = emailTo;
            t.HealthCheckDayOfMonth = Math.Clamp(healthCheckDayOfMonth, 0, 28);
            t.HealthCheckRunMaintenance = healthCheckRunMaintenance;

            t.SmtpHost = host;
            t.SmtpPort = smtpPort is > 0 and < 65536 ? smtpPort : 587;
            t.SmtpUser = (smtpUser ?? "").Trim();
            t.SmtpFrom = (smtpFrom ?? "").Trim();
            t.SmtpUseSsl = smtpUseSsl;

            // A blank password box means "leave it alone", never "clear it". Otherwise every edit to
            // the day of the month would quietly wipe the mail password and the next monthly run
            // would fail with an authentication error that looks nothing like its cause.
            if (clearSmtpPassword) t.SmtpPasswordEnc = "";
            else if (!string.IsNullOrEmpty(smtpPassword)) t.SmtpPasswordEnc = Tenants.ProtectSmtpPassword(smtpPassword);
        });

        await LoadPageAsync();
        // The company switch decides for every machine at once, so the machines hear about it now
        // rather than at their next reconnect — switching it off removes it everywhere.
        if (err.Length == 0) _hubs.Get(TenantId)?.RefreshHealthCheckAvailability(Tenant.HealthCheckReady, Tenant.HealthCheckDayOfMonth);
        if (err.Length > 0) Error = err;
        else if (Tenant.HealthCheckReady) Message = "Saved. Tick the clients who should have it on their page under Clients.";
        // Saved with the switch off is said as plainly as a refusal: Clint saved the right mail settings
        // twice with the box unticked and read "Saved." as "on" (2026-10-21).
        else if (!Tenant.HealthCheckEnabled) Error = "Saved, but the monthly check is still OFF. Tick \"Switch the monthly check on for my company\" at the top and save again — that alone installs nothing.";
        else Message = "Saved.";
        return Page();
    }

    /// <summary>
    /// "" when every report address looks like one. Several are allowed, split on ; or , exactly as
    /// <see cref="HealthCheckService"/> splits them to send. The box was type="email", which the browser
    /// refuses for two addresses WITHOUT posting: Save did nothing, the tick stayed on screen, and the
    /// next visit showed it off (2026-10-21). The server is the only judge now, and it says why.
    /// </summary>
    public static string ReportAddressProblem(string emailTo)
    {
        foreach (var a in emailTo.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!TicketService.LooksLikeEmail(a)) return $"\"{a}\" does not look like an email address. Separate several addresses with ; or ,";
        return "";
    }
}
