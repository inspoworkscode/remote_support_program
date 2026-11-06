using System.Globalization;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// Turns a tenant's two brand colours into the CSS variables the portal is built on.
///
/// The portal is per-tenant, so it should wear the tenant's colours — but one brand colour cannot
/// do both jobs. It has to be readable as link and heading TEXT, and it has to carry white button
/// text when it is a fill, and those pull in opposite directions.
///
/// **The site went dark on 2026-09-16, which inverted half of this.** Text now has to be LIGHT
/// enough against the navy card (#121a23), where before it had to be DARK enough against white.
/// Anywhere IT's own #0E0E12 manages 1.1:1 on that ground — it has to be lifted a long way. Fills
/// still have to be deep enough for white text, which is the same problem as before and moves the
/// colour the other way, so the two are emitted separately: --grape for words, --fill for fills.
///
/// The public marketing pages are deliberately left alone: those are the product's own brand,
/// not any tenant's.
/// </summary>
public static class BrandPalette
{
    /// <summary>WCAG AA for normal text.</summary>
    private const double MinContrast = 4.5;

    /// <summary>The card a tenant's text sits on. Must match --card in site.css.</summary>
    private static readonly (int r, int g, int b) Card = (0x12, 0x1A, 0x23);

    public static string CssVariables(string primaryHex, string accentHex)
    {
        var primary = Parse(primaryHex, (0x7C, 0x3A, 0xED));
        var accent = Parse(accentHex, (0x60, 0xA5, 0xFA));

        // THE SIGNATURE COMES FROM THE ACCENT, not the primary. Branding defines primary as the
        // BANNER colour and accent as the BUTTON colour — one is a background, the other a
        // foreground. Deriving text and links from the background colour was always slightly
        // wrong; on a dark ground it is plainly wrong, because a banner colour is usually dark
        // and lifting a near-black one produces grey. Anywhere IT is the case in point:
        // primary #0E0E12 lifts to #858587, while their accent #29A8E0 is the colour anyone
        // would actually name if asked what colour Anywhere IT is.
        //
        // Primary is still emitted as --grape-bright and is still what the banners use.
        var text = ReadableOnCard(accent);         // carries words on the dark card
        var hover = Lighten(text, 1.18);           // hover goes LIGHTER on a dark ground
        var lite = Lighten(text, 1.10);            // gradient partner for the text colour
        var fill = FillForWhiteText(accent);       // deep enough to carry white button text
        var fillLite = Lighten(fill, 1.14);

        return string.Join(' ', new[]
        {
            $"--grape:{Hex(text)};",
            $"--grape-dark:{Hex(hover)};",
            $"--grape-lite:{Hex(lite)};",
            $"--grape-rgb:{text.r},{text.g},{text.b};",
            $"--grape-bright:{Hex(primary)};",
            $"--fill:{Hex(fill)};",
            $"--fill-lite:{Hex(fillLite)};",
            // The banner colour, kept as the tenant gave it — this is the one place primary
            // belongs, and Get.cshtml and the branding previews paint banners with it directly.
            $"--brand-banner:{Hex(primary)};",
            $"--blue:{Hex(accent)};",
            $"--purple:{Hex(ReadableOnCard(accent))};"
        });
    }

    // ---- colour maths ----

    private static (int r, int g, int b) Parse(string hex, (int r, int g, int b) fallback)
    {
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return fallback;
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    private static string Hex((int r, int g, int b) c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

    private static int Clamp(double d) => (int)Math.Round(Math.Clamp(d, 0, 255));

    private static (int r, int g, int b) Darken((int r, int g, int b) c, double f) =>
        (Clamp(c.r * f), Clamp(c.g * f), Clamp(c.b * f));

    private static (int r, int g, int b) Lighten((int r, int g, int b) c, double f) =>
        (Clamp(c.r * f), Clamp(c.g * f), Clamp(c.b * f));

    /// <summary>Channel component of WCAG relative luminance.</summary>
    private static double Channel(int v)
    {
        var s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    private static double Luminance((int r, int g, int b) c) =>
        0.2126 * Channel(c.r) + 0.7152 * Channel(c.g) + 0.0722 * Channel(c.b);

    /// <summary>WCAG contrast ratio between two colours.</summary>
    private static double Contrast((int r, int g, int b) a, (int r, int g, int b) b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static readonly (int r, int g, int b) White = (0xFF, 0xFF, 0xFF);

    /// <summary>
    /// The brand colour as a FILL carrying white text — website chat's bubble and buttons (3.8.3),
    /// which sit on a company's own light website. Darkened only as far as white text on it needs; a
    /// colour that already passes comes back as it is.
    /// </summary>
    public static string FillForWhiteText(string accentHex)
    {
        var cur = Parse(accentHex, (0x2E, 0x9B, 0xFF));
        for (int i = 0; i < 40 && Contrast(cur, White) < MinContrast; i++)
        {
            var next = Darken(cur, 0.94);
            if (next == cur) break;
            cur = next;
        }
        return Hex(cur);
    }

    /// <summary>
    /// Lifts a brand colour just far enough to be readable as text on the dark card. A colour
    /// that already passes comes back untouched, so a tenant whose brand is legible sees exactly
    /// their own colour and not an approximation of it.
    /// </summary>
    private static (int r, int g, int b) ReadableOnCard((int r, int g, int b) c)
    {
        var cur = c;
        for (int i = 0; i < 40 && Contrast(cur, Card) < MinContrast; i++)
        {
            // +6% of the remaining headroom, so a near-black colour still climbs rather than
            // multiplying its way nowhere: 0 * 1.06 is still 0.
            var next = (Clamp(cur.r + (255 - cur.r) * 0.06),
                        Clamp(cur.g + (255 - cur.g) * 0.06),
                        Clamp(cur.b + (255 - cur.b) * 0.06));
            if (next == cur) break;
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// Deepens a brand colour until white text on top of it is readable. This is the opposite
    /// direction to <see cref="ReadableOnCard"/>, which is exactly why both exist.
    /// </summary>
    private static (int r, int g, int b) FillForWhiteText((int r, int g, int b) c)
    {
        var cur = c;
        for (int i = 0; i < 40 && Contrast(cur, White) < MinContrast; i++)
        {
            var next = Darken(cur, 0.94);
            if (next == cur) break;      // fully black, nothing more to give
            cur = next;
        }
        return cur;
    }
}
