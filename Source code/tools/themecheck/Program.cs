// =====================================================================================
// themecheck - is the portal's light palette actually readable, and is it complete?
//
// Stage 3.6.9 added a light palette to a stylesheet that had only ever had a dark one. Two
// things go wrong when you do that, and neither is visible to the person who wrote it:
//
//   A TOKEN GETS FORGOTTEN. The light block redefines most of :root and misses two. Those
//   two keep their DARK values, so somewhere on some page there is near-black text on a
//   white card, or white text on white. The person who wrote the palette does not see it
//   because they looked at the three pages they were working on.
//
//   A COLOUR IS TOO PALE. The dark ground forgives a light purple; white does not. The
//   lifted #a78bfa manages about 2.2:1 on white, which is unreadable, and the failure is
//   gradual enough that the author's eye adjusts to it.
//
// So: read site.css, compare the two token sets, and compute the real WCAG contrast of
// every pair that carries text. Arithmetic, not opinion.
//
//   dotnet run -c Release --project tools/themecheck/themecheck.csproj
// =====================================================================================

using System.Globalization;
using System.Text.RegularExpressions;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

var cssPath = FindCss();
if (cssPath == null) { Console.Error.WriteLine("Could not find site.css."); return 2; }
var css = File.ReadAllText(cssPath);

Console.WriteLine("themecheck - the portal's two palettes");
Console.WriteLine($"Stylesheet: {cssPath}");
Console.WriteLine();

var dark = Tokens(css, @":root\s*\{(.*?)\}");
var light = Tokens(css, @":root\[data-theme=""light""\]\s*\{(.*?)\}");

Console.WriteLine("== both palettes say everything ==");
Check("the dark palette was found", dark.Count > 10, $"{dark.Count} tokens");
Check("the light palette was found", light.Count > 10, $"{light.Count} tokens");

// The failure this catches: a token defined once, in dark, and silently inherited by light.
var missing = dark.Keys.Where(k => !light.ContainsKey(k))
    // Radii and the two fonts are not colours and are meant to be shared.
    .Where(k => !k.StartsWith("--r-"))
    .ToList();
Check("every colour the dark palette defines, the light one redefines",
    missing.Count == 0, missing.Count == 0 ? $"{dark.Count} checked" : "missing: " + string.Join(", ", missing));

var extra = light.Keys.Where(k => !dark.ContainsKey(k)).ToList();
Check("the light palette invents nothing the dark one lacks",
    extra.Count == 0, extra.Count == 0 ? "" : "extra: " + string.Join(", ", extra));

// Every pair on a page where one colour carries text on the other. 4.5:1 is the WCAG AA floor
// for body text; 3.0:1 is the floor for large text and for a control's own edge.
var pairs = new (string Fg, string Bg, double Floor, string What)[]
{
    ("--ink",   "--peach",    4.5, "body text on the page"),
    ("--ink",   "--card",     4.5, "body text on a card"),
    ("--ink",   "--panel-hi", 4.5, "body text on a table head or a field"),
    ("--muted", "--card",     4.5, "muted text on a card"),
    ("--muted", "--peach",    4.5, "muted text on the page"),
    ("--grape", "--card",     4.5, "a link on a card"),
    ("--grape", "--peach",    4.5, "a link on the page"),
    ("--grape-dark", "--card", 4.5, "a link being hovered"),
    ("--ok",    "--card",     4.5, "the good colour on a card"),
    ("--warn",  "--card",     4.5, "the warning colour on a card"),
    ("--bad",   "--card",     4.5, "the bad colour on a card"),
    ("--blue",  "--card",     4.5, "a blue heading on a card"),
    ("--green", "--card",     4.5, "a green heading on a card"),
    ("--pink",  "--card",     4.5, "a pink heading on a card"),
    ("--orange","--card",     4.5, "an orange heading on a card"),
};

foreach (var (name, palette) in new[] { ("dark", dark), ("light", light) })
{
    Console.WriteLine();
    Console.WriteLine($"== {name}: is the text readable? ==");
    foreach (var (fg, bg, floor, what) in pairs)
    {
        if (!palette.TryGetValue(fg, out var f) || !palette.TryGetValue(bg, out var b))
        { Check($"{what} ({fg} on {bg})", false, "one of those tokens is not defined"); continue; }
        var (okF, cf) = Rgb(f);
        var (okB, cb) = Rgb(b);
        if (!okF || !okB) { Check($"{what} ({fg} on {bg})", false, $"cannot read {f} or {b}"); continue; }
        var ratio = Contrast(cf, cb);
        Check($"{what} ({fg} on {bg})", ratio >= floor, $"{ratio:0.0}:1, floor {floor:0.0}");
    }

    // White text on a filled button: --fill exists precisely to carry it.
    if (palette.TryGetValue("--fill", out var fill))
    {
        var (ok, c) = Rgb(fill);
        if (ok)
        {
            var ratio = Contrast((255, 255, 255), c);
            Check("white text on a filled button (--fill)", ratio >= 4.5, $"{ratio:0.0}:1");
        }
    }
}

Console.WriteLine();
Console.WriteLine("== the light palette turns the browser's own controls light ==");
Check("light declares color-scheme: light",
    Regex.IsMatch(css, @":root\[data-theme=""light""\]\s*\{[^}]*color-scheme:\s*light", RegexOptions.Singleline));
Check("dark still declares color-scheme: dark",
    Regex.IsMatch(css, @":root\s*\{[^}]*color-scheme:\s*dark", RegexOptions.Singleline));

// A wash of white lifts a surface off a dark ground and is invisible on a light one. Any left
// outside the token definitions is a place that will look flat or vanish in light.
Console.WriteLine();
Console.WriteLine("== no palette-specific colour left loose in the rules ==");
var body = css[(css.IndexOf("* { box-sizing", StringComparison.Ordinal) is var i && i > 0 ? i : 0)..];
var loose = Regex.Matches(body, @"rgba\(255,\s*255,\s*255").Count;
Check("no bare white washes outside the token definitions", loose == 0, $"{loose} left");
var darkGround = Regex.Matches(body, @"rgba\(11,\s*16,\s*22").Count;
Check("no bare dark-ground colours outside the token definitions", darkGround == 0, $"{darkGround} left");

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------

static Dictionary<string, string> Tokens(string css, string blockPattern)
{
    var m = Regex.Match(css, blockPattern, RegexOptions.Singleline);
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!m.Success) return map;
    foreach (Match d in Regex.Matches(m.Groups[1].Value, @"(--[a-z0-9-]+)\s*:\s*([^;]+);"))
        map[d.Groups[1].Value] = d.Groups[2].Value.Trim();
    return map;
}

/// <summary>#rrggbb or rgba(r, g, b, a). An alpha is composited onto nothing here — see the note
/// at the call site: the tokens that carry TEXT are all opaque, and the ones that are not are
/// lifts rather than grounds.</summary>
static (bool ok, (int r, int g, int b) c) Rgb(string v)
{
    v = v.Trim();
    var hex = Regex.Match(v, @"^#([0-9a-fA-F]{6})\b");
    if (hex.Success)
    {
        var h = hex.Groups[1].Value;
        return (true, (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16)));
    }
    var rgba = Regex.Match(v, @"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
    if (rgba.Success)
        return (true, (int.Parse(rgba.Groups[1].Value, CultureInfo.InvariantCulture),
                       int.Parse(rgba.Groups[2].Value, CultureInfo.InvariantCulture),
                       int.Parse(rgba.Groups[3].Value, CultureInfo.InvariantCulture)));
    return (false, (0, 0, 0));
}

/// <summary>WCAG 2.1 relative luminance and contrast. The same arithmetic Theme.Contrast does for
/// the desktop apps, so the portal and the console cannot disagree about what is readable.</summary>
static double Contrast((int r, int g, int b) a, (int r, int g, int b) b2)
{
    double L((int r, int g, int b) c)
    {
        double Ch(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.r) + 0.7152 * Ch(c.g) + 0.0722 * Ch(c.b);
    }
    var l1 = L(a); var l2 = L(b2);
    if (l1 < l2) (l1, l2) = (l2, l1);
    return (l1 + 0.05) / (l2 + 0.05);
}

static string? FindCss()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
    {
        var p = Path.Combine(d.FullName, "src", "RemoteSupport.Platform", "wwwroot", "site.css");
        if (File.Exists(p)) return p;
    }
    return null;
}
