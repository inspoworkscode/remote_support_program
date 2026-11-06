using System.Drawing;
using System.Drawing.Drawing2D;
using RemoteSupport.Shared;

namespace RemoteSupport.UI;

/// <summary>Which ground the apps paint on. The tenant chooses this per app in their portal.</summary>
public enum ThemeMode
{
    /// <summary>The warm light palette the suite shipped with.</summary>
    Light,
    /// <summary>Navy ground. The signature colour is the tenant's own accent, never ours.</summary>
    Dark
}

/// <summary>Which of the tenant's two appearance settings an app should obey.</summary>
public enum AppSurface
{
    /// <summary>The technician console — the tenant's own staff.</summary>
    Technician,
    /// <summary>The client app and consent prompt — what the tenant's customer sees.</summary>
    Customer
}

/// <summary>
/// The "warm colourful UI" design language, translated from the web platform to WinForms.
/// Tenant colours (primary/accent) come from branding; everything else is the shared palette.
///
/// Every surface colour is a property here, so a whole palette swap is this one file. That was
/// already nearly true — the exception was ~24 places that reached for Color.White directly, which
/// now use <see cref="Surface"/>. Nothing outside this file should name a raw colour.
/// </summary>
public sealed class Theme
{
    /// <summary>
    /// Set RSP_THEME=light or dark to override whatever the tenant chose, for looking at a change
    /// without editing a tenant's settings. Unset — which is every installed copy — means the
    /// branding decides.
    /// </summary>
    public static ThemeMode? Override { get; set; } =
        (Environment.GetEnvironmentVariable("RSP_THEME") ?? "").Trim().ToLowerInvariant() switch
        {
            "dark" => ThemeMode.Dark,
            "light" => ThemeMode.Light,
            _ => null
        };

    /// <summary>Which ground this instance is painted on.</summary>
    public ThemeMode Mode { get; init; } = ThemeMode.Light;
    public bool IsDark => Mode == ThemeMode.Dark;

    // ---- tenant colours ----
    public Color Primary { get; init; } = Color.FromArgb(0x1F, 0x4E, 0x79);
    public Color Accent { get; init; } = Color.FromArgb(0x2E, 0x9B, 0xFF);

    // ---- shared palette (phase 9) ----
    // Peach/Mist/Ice are the window grounds. On dark they stop being warm pastels and become the
    // three steps of the navy ground, which is why the names outlive the colours.
    public Color Peach { get; init; } = Color.FromArgb(0xFF, 0xF7, 0xED);
    public Color Mist { get; init; } = Color.FromArgb(0xEE, 0xF2, 0xFF);
    public Color Ice { get; init; } = Color.FromArgb(0xEC, 0xFE, 0xFF);
    public Color Ink { get; init; } = Color.FromArgb(0x18, 0x22, 0x35);
    public Color Muted { get; init; } = Color.FromArgb(0x64, 0x74, 0x8B);
    public Color CardBg { get; init; } = Color.White;
    public Color CardBorder { get; init; } = Color.FromArgb(0xE6, 0xEA, 0xF4);
    public Color FieldBorder { get; init; } = Color.FromArgb(0xDB, 0xE4, 0xF0);

    /// <summary>Anything that was a flat white panel: text boxes, chat logs, list views, cards.</summary>
    public Color Surface { get; init; } = Color.White;
    /// <summary>A raised or selected row — list-view headers, the selected item, the call blink.</summary>
    public Color SurfaceAlt { get; init; } = Color.FromArgb(0xEE, 0xF2, 0xFF);
    /// <summary>Text on <see cref="SurfaceAlt"/>.</summary>
    public Color SurfaceAltText { get; init; } = Color.FromArgb(0x37, 0x30, 0xA3);
    /// <summary>Body text inside a dark console panel (script output, the chat popup).</summary>
    public Color ConsoleText { get; init; } = Color.Gainsboro;
    /// <summary>A neutral button's two gradient stops, and a disabled one's fill.</summary>
    public Color NeutralBg { get; init; } = Color.FromArgb(0xF1, 0xF5, 0xF9);
    public Color NeutralBgAlt { get; init; } = Color.FromArgb(0xE8, 0xEE, 0xF6);
    public Color DisabledBg { get; init; } = Color.FromArgb(0xE5, 0xE7, 0xEB);
    /// <summary>The blink an unanswered call gives the calls list.</summary>
    public Color AlertFlash { get; init; } = Color.FromArgb(0xFF, 0xF4, 0xDC);

    public Color Grape { get; init; } = Color.FromArgb(0x7C, 0x3A, 0xED);
    public Color Blue { get; init; } = Color.FromArgb(0x60, 0xA5, 0xFA);
    public Color Green { get; init; } = Color.FromArgb(0x34, 0xD3, 0x99);
    public Color Pink { get; init; } = Color.FromArgb(0xFB, 0x71, 0x85);
    public Color Purple { get; init; } = Color.FromArgb(0xA7, 0x8B, 0xFA);
    public Color Orange { get; init; } = Color.FromArgb(0xFB, 0x92, 0x3C);

    public Color OkText { get; init; } = Color.FromArgb(0x16, 0x65, 0x34);
    public Color OkBg { get; init; } = Color.FromArgb(0xDC, 0xFC, 0xE7);
    public Color WarnText { get; init; } = Color.FromArgb(0x92, 0x40, 0x0E);
    public Color WarnBg { get; init; } = Color.FromArgb(0xFE, 0xF3, 0xC7);
    public Color BadText { get; init; } = Color.FromArgb(0x99, 0x1B, 0x1B);
    public Color BadBg { get; init; } = Color.FromArgb(0xFE, 0xE2, 0xE2);
    public Color Slate { get; init; } = Color.FromArgb(0x0F, 0x17, 0x2A);

    // ---- radii (scaled down from the web so they read correctly at desktop sizes) ----
    public int RadiusCard { get; } = 16;
    public int RadiusButton { get; } = 12;
    public int RadiusInput { get; } = 10;

    // ---- type ----
    public Font H1 { get; } = new("Segoe UI", 19f, FontStyle.Bold);
    public Font H2 { get; } = new("Segoe UI", 13f, FontStyle.Bold);
    public Font H3 { get; } = new("Segoe UI", 10.5f, FontStyle.Bold);
    public Font Body { get; } = new("Segoe UI", 9.75f);
    public Font BodyBold { get; } = new("Segoe UI", 9.75f, FontStyle.Bold);
    public Font Small { get; } = new("Segoe UI", 8.5f);
    public Font ButtonFont { get; } = new("Segoe UI", 10f, FontStyle.Bold);
    public Font Mono { get; } = new("Consolas", 9.5f);

    /// <summary>The ground a tenant chose for one of the two apps. Unrecognised values are light.</summary>
    public static ThemeMode ModeFor(Branding b, AppSurface surface)
    {
        if (Override is { } o) return o;
        var v = (surface == AppSurface.Technician ? b.TechnicianTheme : b.ClientTheme) ?? "";
        return v.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Dark : ThemeMode.Light;
    }

    /// <summary>
    /// The palette for one app. <paramref name="surface"/> decides which of the tenant's two
    /// appearance settings applies — the console and the customer's app are set separately,
    /// because one is a tool their staff use all day and the other is how they present themselves.
    /// </summary>
    public static Theme FromBranding(Branding b, AppSurface surface)
    {
        var primary = Parse(b.PrimaryColor, Color.FromArgb(0x1F, 0x4E, 0x79));
        var accent = Parse(b.AccentColor, Color.FromArgb(0x2E, 0x9B, 0xFF));
        return ModeFor(b, surface) == ThemeMode.Light ? Light(primary, accent) : Dark(primary, accent);
    }

    /// <summary>
    /// The light palette for one tenant. Its signature — the words on an outlined button, a link, a
    /// list heading, the ring round the field being typed in — is the tenant's OWN accent, deepened
    /// only as far as it must be to read on white, exactly as the portal does it.
    ///
    /// It used to be our purple (#7C3AED) whatever the tenant, which is how a console whose filled
    /// buttons were Anywhere IT's blue had purple outlines and purple focus rings beside them —
    /// part of what Clint saw as "no colour conformity" (2026-10-06).
    /// </summary>
    private static Theme Light(Color primary, Color accent)
    {
        var signature = Readable(accent, Color.White);
        return new Theme
        {
            Mode = ThemeMode.Light,
            Primary = primary,
            Accent = accent,
            Grape = signature,
            Purple = signature,
            SurfaceAltText = Readable(accent, Color.FromArgb(0xEE, 0xF2, 0xFF))
        };
    }

    /// <summary>The navy ground every dark palette sits on. Also what the portal previews against.</summary>
    public static readonly Color DarkGround = Color.FromArgb(0x0B, 0x10, 0x16);

    /// <summary>
    /// The dark palette for one tenant. The signature colour is the tenant's OWN accent, lifted
    /// only as far as it must be to stay readable on the navy ground — a reseller's apps never
    /// carry our colours, and a tenant who picks a deep blue gets a deep blue here.
    ///
    /// Primary and Accent themselves are passed through untouched. They are the tenant's brand and
    /// we do not get to repaint them; where one is too dark to read on this ground, the portal
    /// warns them at the moment they choose it rather than us quietly substituting something else.
    /// </summary>
    private static Theme Dark(Color primary, Color accent)
    {
        // One brand colour cannot do both jobs on a dark ground: readable as TEXT means lifted,
        // and lifted means white button text on the same colour fails. So the signature used for
        // headings, links and list headers is the lifted one, while fills keep the tenant's own
        // accent and let On() pick the text colour that sits on it.
        var signature = Readable(accent, DarkGround);

        return new Theme
        {
            Mode = ThemeMode.Dark,
            Primary = primary,
            Accent = accent,

            // the three window grounds, darkest first
            Peach = Color.FromArgb(0x0B, 0x10, 0x16),
            Mist = Color.FromArgb(0x0E, 0x14, 0x1C),
            Ice = Color.FromArgb(0x12, 0x1A, 0x23),

            Ink = Color.FromArgb(0xEE, 0xF3, 0xF8),
            Muted = Color.FromArgb(0x93, 0xA4, 0xB6),

            CardBg = Color.FromArgb(0x12, 0x1A, 0x23),
            CardBorder = Color.FromArgb(0x25, 0x31, 0x3F),
            FieldBorder = Color.FromArgb(0x2C, 0x39, 0x49),

            Surface = Color.FromArgb(0x12, 0x1A, 0x23),
            SurfaceAlt = Color.FromArgb(0x1B, 0x26, 0x33),
            SurfaceAltText = signature,
            ConsoleText = Color.Gainsboro,
            NeutralBg = Color.FromArgb(0x1B, 0x26, 0x33),
            NeutralBgAlt = Color.FromArgb(0x16, 0x20, 0x2B),
            DisabledBg = Color.FromArgb(0x1A, 0x22, 0x2C),
            AlertFlash = Color.FromArgb(0x3A, 0x2E, 0x14),

            Grape = signature,
            Blue = Readable(Color.FromArgb(0x60, 0xA5, 0xFA), DarkGround),
            Purple = signature,

            // Status text lightens; the washes behind it become tinted glass rather than pastel.
            OkText = Color.FromArgb(0x6E, 0xE7, 0xB7), OkBg = Color.FromArgb(0x10, 0x33, 0x2A),
            WarnText = Color.FromArgb(0xFC, 0xD3, 0x4D), WarnBg = Color.FromArgb(0x3A, 0x2E, 0x14),
            BadText = Color.FromArgb(0xFC, 0xA5, 0xA5), BadBg = Color.FromArgb(0x3A, 0x1A, 0x1A),

            // Already near-black; nudged so a console panel still reads as inset, not as the wall.
            Slate = Color.FromArgb(0x07, 0x0B, 0x10)
        };
    }

    public static Color Parse(string hex, Color fallback)
    { try { return ColorTranslator.FromHtml(hex); } catch { return fallback; } }

    // ---- contrast ----------------------------------------------------------------------------
    // WCAG 2.1 relative luminance and contrast ratio. Used in two places that must agree: the
    // apps, when lifting a tenant's accent so it stays readable on the dark ground, and the
    // portal, when telling that tenant their colour will not read before they ship installers
    // with it. Same numbers, one implementation.

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Color c)
    {
        static double Ch(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG contrast ratio between two colours, 1.0 to 21.0.</summary>
    public static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>The WCAG AA threshold for normal body text.</summary>
    public const double MinContrast = 4.5;

    /// <summary>
    /// The nearest version of <paramref name="c"/> that reaches <paramref name="min"/> against
    /// <paramref name="on"/>, moving away from that background in 4% steps and keeping the hue.
    /// Returns the colour unchanged when it already passes, so a tenant whose accent is legible
    /// sees exactly their own colour.
    /// </summary>
    public static Color Readable(Color c, Color on, double min = MinContrast)
    {
        if (Contrast(c, on) >= min) return c;
        var lighten = Luminance(on) < 0.5;   // dark ground: lift. light ground: deepen.
        var candidate = c;
        for (var i = 0; i < 25; i++)
        {
            candidate = lighten ? Lighten(candidate, .04) : Darken(candidate, .04);
            if (Contrast(candidate, on) >= min) return candidate;
        }
        return lighten ? Color.White : Color.Black;   // nothing in between worked
    }

    /// <summary>Readable text colour for a filled background.</summary>
    public static Color On(Color bg) =>
        (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) > 150 ? Color.FromArgb(0x18, 0x22, 0x35) : Color.White;

    public static Color Lighten(Color c, double amount)
        => Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * amount), (int)(c.G + (255 - c.G) * amount), (int)(c.B + (255 - c.B) * amount));

    /// <summary><paramref name="a"/> moved <paramref name="amount"/> of the way toward <paramref name="b"/>.</summary>
    public static Color Blend(Color a, Color b, double amount)
        => Color.FromArgb(255, (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));

    public static Color Darken(Color c, double amount)
        => Color.FromArgb(c.A, (int)(c.R * (1 - amount)), (int)(c.G * (1 - amount)), (int)(c.B * (1 - amount)));

    /// <summary>Rounded-rectangle path used by every custom-painted control.</summary>
    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
        d = Math.Min(d, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit; }
}
