using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// The terms a company accepts at signup, one set per region (2026-10-05, Clint: "a country relevant
/// disclaimer including things like popia act, we are not responsible for loss of data etc etc which
/// the client must acknowledge before they can set up their tenant").
///
/// <para>The words live in <c>Terms/core.txt</c> (everything shared) and <c>Terms/{region}.txt</c>
/// (the privacy law, the tax, the governing law), embedded in the program and written in the
/// knowledge base's markup (<see cref="KbText"/>: <c>#</c> headings, <c>-</c> bullets, <c>**bold**</c>).
/// <b>Claude drafted them; Clint took them as correct on 2026-10-06</b> and no region shows a DRAFT
/// notice (<see cref="IsDraft"/>). His lawyer is checking them: any change comes back as an edit here
/// and a new <see cref="Version"/>. Anything in [square brackets] is a fact still to fill in.</para>
///
/// <para>What a company accepted is kept WORD FOR WORD (<see cref="Data.TermsAcceptance"/>), so
/// changing these files never changes what anybody agreed to.</para>
/// </summary>
public static class Terms
{
    /// <summary>The version every region is on. Move it whenever a word changes — and run <c>landing-legal</c>.</summary>
    public const string Version = "2026-10-25";

    /// <summary>
    /// Whether a region's terms are shown with a DRAFT notice. None are, since 2026-10-06 — Clint: "lets
    /// remove draft from the consent forms. lets assume they are correct as is. I will get a lawyer to check
    /// them and give you any changes if they are found." A lawyer's change is a new <see cref="Version"/>.
    /// The regions that are not open yet (US, CA, UK, EU) still hold [to be confirmed] facts — tax, the
    /// governing law, the ICO number, the EU representative — to fill in before each opens.
    /// </summary>
    public static bool IsDraft(string region) => false;

    /// <summary>The privacy policy (South Africa's — the only region open), drawn as HTML.</summary>
    public static string PrivacyHtml() => KbText.Render(Read("privacy-ZA"));

    /// <summary>One region's terms: the source text, drawn as HTML, and its fingerprint.</summary>
    public sealed record Text(string Region, string Version, bool Draft, string Source, string Html, string Sha256);

    /// <summary>
    /// The Free version's terms (2026-10-06; back 2026-10-25, §163): the region's own, with <c>Terms/free.txt</c> put in ahead of
    /// section 1 — one computer, unattended, nothing to pay — and winning where they differ.
    /// </summary>
    public static Text ForFree(string region) =>
        Ahead(region, Read("free").Trim().Replace("{{FREE_MACHINES}}", RemoteSupport.Shared.FreePlan.FreeMachines.ToString()));

    /// <summary>
    /// The Basic version's terms (the free version's, 2026-10-06; R50 since 2026-10-25; attended too since §163): the region's own, with <c>Terms/basic.txt</c> put in ahead of
    /// section 1 — it says where the Basic version differs, and that it wins where they do.
    /// </summary>
    public static Text ForBasic(string region)
    {
        // The Basic version (2026-10-25, Terms/basic.txt — free.txt until then): its price and machine limit filled in here.
        var r = Pricing.For(region);
        return Ahead(region, Read("basic").Trim()
            .Replace("{{BASIC}}", Pricing.Money(r, r.Basic)).Replace("{{BASIC_MACHINES}}", RemoteSupport.Shared.FreePlan.MaxMachines.ToString())
            .Replace("{{BASIC_EXTRA}}", Pricing.Money(r, r.ExtraMachine)).Replace("{{BASIC_MOST}}", RemoteSupport.Shared.FreePlan.MostMachines.ToString()));
    }

    /// <summary>
    /// A pilot company's terms (2026-10-15, <see cref="PilotService"/>): the region's own, with <c>Terms/pilot.txt</c> put in
    /// ahead of section 1 — five seats free while it is a pilot, and how we may end the pilot (2026-10-22: 30 days' notice by
    /// email, then the standard price or the end, <see cref="PilotService.GiveNoticeAsync"/>) — and winning where they differ.
    /// </summary>
    public static Text ForPilot(string region) => Ahead(region, Read("pilot").Trim());

    /// <summary>The region's terms with a section of its own put in ahead of section 1.</summary>
    private static Text Ahead(string region, string section)
    {
        var full = For(region);
        const string section1 = "# 1. What the service is";
        int at = full.Source.IndexOf(section1, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("Terms/core.txt has no section 1 to put a section ahead of.");
        var source = full.Source[..at] + section + "\n\n" + full.Source[at..];
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return new Text(full.Region, full.Version, IsDraft(region), source, KbText.Render(source), sha);
    }

    /// <summary>The Free version's terms, the Basic version's (by the plan's stored name, FreePlan), a pilot company's, or the full version's.</summary>
    public static Text For(string region, string? plan, bool pilot = false) =>
        RemoteSupport.Shared.FreePlan.IsFree(plan) ? ForFree(region)
        : RemoteSupport.Shared.FreePlan.IsBasic(plan) ? ForBasic(region)
        : pilot ? ForPilot(region) : For(region);

    public static Text For(string region)
    {
        var r = Pricing.For(region);
        var parts = Region(r.Code);
        var source = Read("core")
            .Replace("{{COUNTRY}}", Capital(parts.country))
            .Replace("{{PACKAGE}}", Pricing.Money(r, r.Package))
            .Replace("{{EXTRA}}", Pricing.Money(r, r.ExtraSeat))
            .Replace("{{CURRENCY}}", r.Currency)
            .Replace("{{TAX}}", parts.tax)
            .Replace("{{LIABILITY_EXTRA}}", parts.liabilityExtra)
            .Replace("{{PRIVACY}}", parts.privacy.Trim())
            .Replace("{{GOVERNING_LAW}}", parts.law.Trim());
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        return new Text(r.Code, Version, IsDraft(r.Code), source, KbText.Render(source), sha);
    }

    /// <summary>"the United Kingdom" heads its page as "The United Kingdom".</summary>
    private static string Capital(string s) => s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;

    /// <summary>A region file: "Key: value" lines, then <c>---PRIVACY---</c> and <c>---LAW---</c> sections.</summary>
    private static (string country, string tax, string liabilityExtra, string privacy, string law) Region(string code)
    {
        var text = Read(code).Replace("\r\n", "\n");
        int p = text.IndexOf("---PRIVACY---", StringComparison.Ordinal), l = text.IndexOf("---LAW---", StringComparison.Ordinal);
        if (p < 0 || l < p) throw new InvalidOperationException($"Terms/{code}.txt needs ---PRIVACY--- then ---LAW---.");
        var head = text[..p].Split('\n').Select(x => x.Split(':', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].Trim(), x => x[1].Trim());
        string H(string k) => head.TryGetValue(k, out var v) ? v : "";
        var extra = H("LiabilityExtra");
        return (H("Country"), H("Tax"), extra.Length > 0 && !extra.StartsWith(',') ? ", " + extra : extra,
            text[(p + "---PRIVACY---".Length)..l], text[(l + "---LAW---".Length)..]);
    }

    private static string Read(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Terms.{name}.txt")
            ?? throw new InvalidOperationException($"Terms/{name}.txt is not embedded in the program.");
        using var r = new StreamReader(s, Encoding.UTF8);
        // One line ending everywhere: git turns them into the checkout machine's, and the fingerprint
        // of the same words must not depend on which machine built the program.
        return r.ReadToEnd().Replace("\r\n", "\n");
    }
}
