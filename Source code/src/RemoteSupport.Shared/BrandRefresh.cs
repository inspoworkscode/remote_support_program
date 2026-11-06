using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemoteSupport.Shared;

/// <summary>
/// What of a company's branding may change on a machine after it is installed (2026-10-07, roadmap 4.6, the
/// settings half). Everything a person SEES, and the consent settings the company chose — nothing that decides
/// where the machine connects or whose it is. <see cref="Branding.ServerUrl"/>, <see cref="Branding.TenantKey"/>,
/// <see cref="Branding.Free"/> and the health check settings are not here at all, so no message can carry them.
/// </summary>
public sealed class BrandLook
{
    public string CompanyName { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string SupportEmail { get; set; } = "";
    public string SupportPhone { get; set; } = "";
    public string PrimaryColor { get; set; } = "";
    public string AccentColor { get; set; } = "";
    public string WelcomeText { get; set; } = "";
    public string TechnicianTheme { get; set; } = "light";
    public string ClientTheme { get; set; } = "light";
    public bool RequireConsent { get; set; } = true;
    public bool UnattendedRequireConsent { get; set; }
    public int ConsentTimeoutSeconds { get; set; } = 60;
    /// <summary>The logo's file name as the company's branding names it ("logo.png"), or empty for none.</summary>
    public string Logo { get; set; } = "";
}

/// <summary>
/// The ONE place the company's current look is turned into a message, checked, kept on a machine and read back.
///
/// <para><b>Where it is kept.</b> <c>branding\live\</c> beside the installed <c>branding\brand.json</c>, which is
/// never rewritten: <c>Uninstall-Client.ps1</c> reads the company and product names in it to find the folder and
/// the shortcuts the installer made, and a renamed company would leave them behind. <see cref="Branding.Load"/>
/// lays the live look over the installed one, so the tray app, the consent prompt and the shortcut icon all read
/// it without knowing it is there.</para>
///
/// <para><b>Who writes it.</b> The agent service, which runs as SYSTEM and can write in Program Files; a tray app
/// writes it only where it can (a copy run from a folder of its own).</para>
///
/// <para><b>The logo has a new name every time</b> (<c>logo-{hash}.png</c>): <c>Image.FromFile</c> keeps the file
/// it read locked for as long as the picture is shown, so a running tray app would make an overwrite fail.</para>
/// </summary>
public static class BrandRefresh
{
    /// <summary>The portal refuses a logo over 2 MB; this is the machine's own ceiling, with room.</summary>
    public const int MaxLogoBytes = 4 * 1024 * 1024;
    private const string LookFile = "look.json";

    public static string LiveDir(string baseDir) => Path.Combine(baseDir, "branding", "live");

    public static BrandLook LookOf(Branding b) => new()
    {
        CompanyName = b.CompanyName, ProductName = b.ProductName, SupportEmail = b.SupportEmail, SupportPhone = b.SupportPhone,
        PrimaryColor = b.PrimaryColor, AccentColor = b.AccentColor, WelcomeText = b.WelcomeText,
        TechnicianTheme = b.TechnicianTheme, ClientTheme = b.ClientTheme,
        RequireConsent = b.RequireConsent, UnattendedRequireConsent = b.UnattendedRequireConsent,
        ConsentTimeoutSeconds = b.ConsentTimeoutSeconds, Logo = b.Logo ?? ""
    };

    /// <summary>The look and the logo's bytes as one value: a machine whose hello says this has nothing to catch up.</summary>
    public static string Hash(BrandLook look, byte[]? logo)
    {
        using var sha = SHA256.Create();
        var text = Encoding.UTF8.GetBytes(Json.Serialize(look));
        sha.TransformBlock(text, 0, text.Length, null, 0);
        sha.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
        var l = logo ?? Array.Empty<byte>();
        sha.TransformFinalBlock(l, 0, l.Length);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>The server's message for a company: its look as the installers would carry it, and its logo.</summary>
    public static BrandingMsg Message(Branding b, byte[]? logo)
    {
        var look = LookOf(b);
        if (logo is not { Length: > 0 }) { logo = null; look.Logo = ""; }
        return new BrandingMsg { Look = look, Hash = Hash(look, logo), Logo = logo };
    }

    /// <summary>Why a message is not kept, or null. Checked on the MACHINE: it decides what reaches its disk.</summary>
    public static string? Problem(BrandingMsg m)
    {
        if (m.Look == null) return "no look";
        if (!Regex.IsMatch(m.Hash ?? "", "^[0-9a-f]{64}$")) return "no fingerprint";
        if (m.Logo is { Length: > MaxLogoBytes }) return $"the logo is over {MaxLogoBytes / (1024 * 1024)} MB";
        return null;
    }

    /// <summary>
    /// Keeps the look in <paramref name="baseDir"/>\branding\live. False, with the reason, when it was not kept —
    /// a tray app in Program Files cannot write there, and that is expected: its agent does.
    /// </summary>
    public static bool Save(string baseDir, BrandingMsg m, out string error)
    {
        error = Problem(m) ?? "";
        if (error.Length > 0) return false;
        try
        {
            var dir = LiveDir(baseDir);
            Directory.CreateDirectory(dir);
            var look = Clean(m.Look);
            string logoFile = "";
            if (m.Logo is { Length: > 0 } && look.Logo.Length > 0)
            {
                // Our own name, never one from the message: nothing it says can choose a path.
                logoFile = "logo-" + m.Hash[..12] + LogoExtension(look.Logo);
                var path = Path.Combine(dir, logoFile);
                if (!File.Exists(path)) WriteAtomically(path, m.Logo);
            }
            else look.Logo = "";
            WriteAtomically(Path.Combine(dir, LookFile), Encoding.UTF8.GetBytes(Json.Serialize(new Live { Look = look, Hash = m.Hash, LogoFile = logoFile })));
            // Old logos go when nothing holds them; one a running app has open goes next time.
            foreach (var old in Directory.EnumerateFiles(dir, "logo-*"))
                if (!string.Equals(Path.GetFileName(old), logoFile, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(old); } catch { }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>The fingerprint of the look kept in <paramref name="baseDir"/>, or empty when there is none whole.</summary>
    public static string HashOn(string baseDir) => Read(baseDir) is { } live && Complete(baseDir, live) ? live.Hash : "";

    /// <summary>Forgets the kept look — a downloaded copy that has just fetched its branding fresh needs none.</summary>
    public static void Forget(string baseDir)
    {
        try { var dir = LiveDir(baseDir); if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    /// <summary>
    /// Lays the kept look over branding read from <paramref name="baseDir"/>. Called by <see cref="Branding.Load"/>
    /// and <see cref="Branding.LoadFrom"/>; anything wrong with the kept copy leaves the installed branding as it was.
    /// </summary>
    internal static void Overlay(Branding b, string baseDir)
    {
        var live = Read(baseDir);
        if (live == null || !Complete(baseDir, live)) return;
        Apply(b, live.Look);
        if (live.LogoFile.Length > 0) { b.Logo = live.LogoFile; b.SetLogoDirectory(LiveDir(baseDir)); }
        else b.Logo = "";
        b.SetLiveHash(live.Hash);
    }

    /// <summary>Lays what is kept now over branding already loaded — for the agent, which holds its branding for life.</summary>
    public static void Reapply(Branding b) => Overlay(b, b.BaseDirectory);

    /// <summary>Copies the look onto <paramref name="onto"/> — and only the look; a colour that is not one is left as it was.</summary>
    public static void Apply(Branding onto, BrandLook look)
    {
        look = Clean(look);
        onto.CompanyName = look.CompanyName.Length > 0 ? look.CompanyName : onto.CompanyName;
        onto.ProductName = look.ProductName.Length > 0 ? look.ProductName : onto.ProductName;
        onto.SupportEmail = look.SupportEmail; onto.SupportPhone = look.SupportPhone;
        if (IsColour(look.PrimaryColor)) onto.PrimaryColor = look.PrimaryColor;
        if (IsColour(look.AccentColor)) onto.AccentColor = look.AccentColor;
        onto.WelcomeText = look.WelcomeText;
        onto.TechnicianTheme = look.TechnicianTheme; onto.ClientTheme = look.ClientTheme;
        onto.RequireConsent = look.RequireConsent; onto.UnattendedRequireConsent = look.UnattendedRequireConsent;
        onto.ConsentTimeoutSeconds = look.ConsentTimeoutSeconds;
    }

    private sealed class Live
    {
        public BrandLook Look { get; set; } = new();
        public string Hash { get; set; } = "";
        public string LogoFile { get; set; } = "";
    }

    private static Live? Read(string baseDir)
    {
        try
        {
            var path = Path.Combine(LiveDir(baseDir), LookFile);
            if (!File.Exists(path)) return null;
            var live = JsonSerializer.Deserialize<Live>(File.ReadAllText(path), Json.Options);
            return live?.Look != null && Regex.IsMatch(live.Hash ?? "", "^[0-9a-f]{64}$") ? live : null;
        }
        catch { return null; }
    }

    /// <summary>Its logo is there too — or it has none. A look naming a logo that has gone is not the look the hash says.</summary>
    private static bool Complete(string baseDir, Live live) =>
        live.LogoFile.Length == 0 || (live.LogoFile == Path.GetFileName(live.LogoFile) && File.Exists(Path.Combine(LiveDir(baseDir), live.LogoFile)));

    private static BrandLook Clean(BrandLook l) => new()
    {
        CompanyName = Cap(l.CompanyName, 200), ProductName = Cap(l.ProductName, 200),
        SupportEmail = Cap(l.SupportEmail, 200), SupportPhone = Cap(l.SupportPhone, 100),
        PrimaryColor = (l.PrimaryColor ?? "").Trim(), AccentColor = (l.AccentColor ?? "").Trim(),
        WelcomeText = Cap(l.WelcomeText, 2000),
        TechnicianTheme = Theme(l.TechnicianTheme), ClientTheme = Theme(l.ClientTheme),
        RequireConsent = l.RequireConsent, UnattendedRequireConsent = l.UnattendedRequireConsent,
        // The portal's own bounds.
        ConsentTimeoutSeconds = Math.Clamp(l.ConsentTimeoutSeconds, 10, 600),
        Logo = Cap(l.Logo, 100)
    };

    private static string Cap(string? s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s[..n] : s; }
    private static string Theme(string? v) => (v ?? "").Trim().Equals("dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light";
    private static bool IsColour(string s) => Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$");
    private static string LogoExtension(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" ? ".jpg" : ".png";

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
