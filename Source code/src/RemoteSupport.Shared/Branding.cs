using System.Text.Json;

namespace RemoteSupport.Shared;

/// <summary>Company branding, loaded from branding/brand.json next to the executable — with the company's current look
/// laid over it from branding/live when the server has sent one since it was installed (<see cref="BrandRefresh"/>).</summary>
public sealed class Branding
{
    public string CompanyName { get; set; } = "Your Company";
    public string ProductName { get; set; } = "Remote Support";
    public string SupportEmail { get; set; } = "";
    public string SupportPhone { get; set; } = "";
    public string PrimaryColor { get; set; } = "#1F4E79";
    public string AccentColor { get; set; } = "#2E9BFF";
    public string Logo { get; set; } = "logo.png";
    public string ServerUrl { get; set; } = "wss://support.example.com";
    public string WelcomeText { get; set; } = "Need help? Request a support session and one of our technicians will connect with you.";

    // ---- appearance ----
    // Two settings, not one, and deliberately so: the console is a tool the tenant's own staff
    // stare at all day, while the client app is what their customer sees and is part of how the
    // tenant presents itself. A company can reasonably want a dark console and a light, on-brand
    // customer app. "light" or "dark"; anything unrecognised falls back to light.
    //
    // There is no third value for a signature colour. On dark, the tenant's own AccentColor is
    // the signature, lifted where it has to be to stay readable — see Theme.Dark. A reseller's
    // apps never carry our colours.

    /// <summary>Appearance of the technician console: "light" or "dark".</summary>
    public string TechnicianTheme { get; set; } = "light";
    /// <summary>Appearance of the customer's app and the consent prompt: "light" or "dark".</summary>
    public string ClientTheme { get; set; } = "light";

    /// <summary>Ask the person at the machine to approve before an attended session starts.</summary>
    public bool RequireConsent { get; set; } = true;
    /// <summary>Ask for approval before an unattended (agent) session too. Off by default so unattended stays unattended.</summary>
    public bool UnattendedRequireConsent { get; set; } = false;
    /// <summary>Seconds the consent prompt waits before it auto-declines (privacy-safe default).</summary>
    public int ConsentTimeoutSeconds { get; set; } = 60;

    /// <summary>Multi-tenant platform only: the tenant this install belongs to. Empty for the single-company server.</summary>
    public string TenantKey { get; set; } = "";

    /// <summary>
    /// The company is on the free version (2026-10-06, docs/NEXT-SESSION-FREE.md): a downloaded copy installs
    /// the agent alone, asks the unattended password, and offers no "Get help now" — see Launcher and
    /// <see cref="FreeSetup"/>. The server enforces the free version whatever this says.
    /// </summary>
    public bool Free { get; set; }

    /// <summary>
    /// The one-person plan is the Basic version (2026-10-25, HANDOVER §163), not the Free one: its installer may leave the
    /// unattended password blank, and the person at that computer is then asked to allow each connection. Meaningless without
    /// <see cref="Free"/>. The server enforces it whatever this says.
    /// </summary>
    public bool Basic { get; set; }

    // ---- monthly health check ----
    // Only what the endpoint needs in order to install and run the scheduled task. The tenant's
    // SMTP settings and mail password are deliberately NOT here: the machine uploads its result
    // and the server sends the mail, so no customer machine ever holds the password. See
    // docs/PLAN-HEALTHCHECK.md.

    /// <summary>Register the monthly maintenance and health check task on this machine.</summary>
    public bool HealthCheckEnabled { get; set; }
    /// <summary>Day of the month to run. 0 lets the machine pick from its install weekday, spreading a client base over five days.</summary>
    public int HealthCheckDayOfMonth { get; set; }
    /// <summary>Run maintenance before the read-only check, rather than inspecting only.</summary>
    public bool HealthCheckRunMaintenance { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore] public string BaseDirectory { get; private set; } = AppContext.BaseDirectory;
    [System.Text.Json.Serialization.JsonIgnore] public string LogoPath => Path.Combine(_logoDirectory ?? Path.Combine(BaseDirectory, "branding"), Logo);
    /// <summary>
    /// The fingerprint of the company's look laid over this branding from <c>branding\live</c> (2026-10-07,
    /// <see cref="BrandRefresh"/>), or empty when this is the look it was installed with. Said in every hello.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] public string LiveHash { get; private set; } = "";
    private string? _logoDirectory;
    internal void SetLogoDirectory(string dir) => _logoDirectory = dir;
    internal void SetLiveHash(string hash) => LiveHash = hash;
    [System.Text.Json.Serialization.JsonIgnore] public bool HasLogo => !string.IsNullOrEmpty(Logo) && File.Exists(LogoPath);

    /// <summary>
    /// Branding from <paramref name="dir"/>\branding\brand.json — for a downloaded copy of the client
    /// (2026-09-30), whose branding is fetched from the server into a folder of its own rather than
    /// lying beside the .exe. Null when there is none there.
    /// </summary>
    public static Branding? LoadFrom(string dir)
    {
        var path = Path.Combine(dir, "branding", "brand.json");
        if (!File.Exists(path)) return null;
        try
        {
            var loaded = JsonSerializer.Deserialize<Branding>(File.ReadAllText(path), Json.Options);
            if (loaded != null) { loaded.BaseDirectory = dir; BrandRefresh.Overlay(loaded, dir); }
            return loaded;
        }
        catch { return null; }
    }

    public static Branding Load()
    {
        var b = new Branding();
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var path = Path.Combine(dir, "branding", "brand.json");
            if (!File.Exists(path)) continue;
            try
            {
                var loaded = JsonSerializer.Deserialize<Branding>(File.ReadAllText(path), Json.Options);
                // The company's look as it is now, when the server has sent one since (BrandRefresh).
                if (loaded != null) { loaded.BaseDirectory = dir; BrandRefresh.Overlay(loaded, dir); return loaded; }
            }
            catch { }
        }
        return b;
    }
}
