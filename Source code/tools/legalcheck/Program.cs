// =====================================================================================
// legalcheck — the landing page's terms, refunds and privacy (2026-10-06, HANDOVER §88).
//
// Clint: "Payfast is asking that we have a terms and conditions section on our current landing page."
// PayFast checks a merchant's site for: terms and conditions linked from the home page, a refund
// policy, a privacy policy (POPIA), and the business's contact details and address.
//
//   THE SAME WORDS   the landing page's LEGAL block is exactly what `landing-legal` writes from
//                    Terms/ — the words signup shows. Change Terms/ without re-running it and this fails.
//   NOTHING OPEN     no [bracket] and no {{placeholder}} in South Africa's terms or the privacy policy;
//                    South Africa final, the regions not yet open still draft.
//   PAYFAST'S LIST   footer links to #terms, #refunds and #privacy, each landing on something that
//                    exists; the company, registration number and address; the refund points; POPIA,
//                    the Information Officer, the Regulator, PayFast handling the card.
//
//   dotnet run -c Debug --project tools/legalcheck/legalcheck.csproj
// =====================================================================================

using System.Text.RegularExpressions;
using RemoteSupport.Platform.Services;

int pass = 0, fail = 0;
void Check(string what, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"  PASS  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
    else { fail++; Console.WriteLine($"  FAIL  {what}{(detail.Length > 0 ? " — " + detail : "")}"); }
}

Console.WriteLine("legalcheck — terms, refunds and privacy on the landing page");
var d = new DirectoryInfo(AppContext.BaseDirectory);
while (d != null && !File.Exists(Path.Combine(d.FullName, "RemoteSupport.sln"))) d = d.Parent;
var page = File.ReadAllText(Path.Combine(d!.FullName, "landing", "index.html")).Replace("\r\n", "\n");

Console.WriteLine();
Console.WriteLine("== the same words as signup ==");
int b = page.IndexOf("<!-- LEGAL:BEGIN", StringComparison.Ordinal), e = page.IndexOf("<!-- LEGAL:END -->", StringComparison.Ordinal);
Check("the page has its LEGAL block", b >= 0 && e > b);
var onPage = b >= 0 && e > b ? page[b..(e + "<!-- LEGAL:END -->".Length)] : "";
var written = LandingLegal.Block().Replace("\r\n", "\n");
Check("...and it is exactly what landing-legal writes from Terms/ today", onPage == written,
    onPage == written ? "" : "run: RemoteSupport.Platform landing-legal <full path to landing/index.html>");

Console.WriteLine();
Console.WriteLine("== nothing left open ==");
var za = Terms.For("ZA");
var privacy = Terms.PrivacyHtml();
Check("South Africa's terms have no [bracket] and no {{placeholder}}", !za.Source.Contains('[') && !za.Source.Contains("{{"),
    Regex.Match(za.Source, @"\[[^\]]*\]").Value);
Check("...nor does the privacy policy", !privacy.Contains('[') && !privacy.Contains("{{"), Regex.Match(privacy, @"\[[^\]]*\]").Value);
Check("no region's terms are a draft any more (Clint, 2026-10-06: \"assume they are correct as is\")",
    Pricing.Regions.All(r => !Terms.For(r.Code).Draft && !Terms.For(r.Code, "free").Draft && !Terms.For(r.Code, "basic").Draft));
Check("the only placeholders on the page are the contact details Publish-Landing fills in",
    Regex.Matches(page, @"\{\{(\w+)\}\}").Select(m => m.Groups[1].Value).Distinct().All(x => x is "EMAIL" or "PHONE_DISPLAY" or "PHONE_E164" or "PHONE_WA"),
    string.Join(",", Regex.Matches(page, @"\{\{(\w+)\}\}").Select(m => m.Groups[1].Value).Distinct()));

Console.WriteLine();
Console.WriteLine("== what PayFast looks for ==");
var footer = page[page.IndexOf("<footer", StringComparison.Ordinal)..];
foreach (var (anchor, words) in new[] { ("terms", "Terms and conditions"), ("refunds", "Refund policy"), ("privacy", "Privacy policy") })
    Check($"the footer links \"{words}\" to #{anchor}, which exists on the page",
        footer.Contains($"<a href=\"#{anchor}\">{words}</a>") && page.Contains($"id=\"{anchor}\""));
Check("the company, its registration number and its address are on the page",
    page.Contains("The Remote Support Program (Pty) Ltd") && page.Contains("2026/737434/07") && page.Contains("19 Tide Street, Knysna Central, Knysna"));
Check("...with the email and phone", page.Contains("mailto:{{EMAIL}}") && page.Contains("tel:{{PHONE_E164}}"));
Check("the refund points: cancel any time, free trial, no part-months, mistakes refunded in 30 days, no VAT",
    new[] { "Cancel any time", "Free 14-day trial", "No part-month refunds", "Mistakes refunded", "No VAT" }.All(page.Contains));
Check("the terms' own refund section says the same",
    za.Source.Contains("You may cancel at any time") && za.Source.Contains("refunded in full, to the card or account it came from, within 30 days")
    && za.Source.Contains("We do not refund part of a month"));
Check("the terms keep the data-loss clause", za.Source.Contains("We are not responsible for any loss of, damage to, or corruption of data"));
Check("the privacy policy names POPIA, the Information Officer, the Information Regulator, and PayFast for cards",
    privacy.Contains("Protection of Personal Information Act") && privacy.Contains("Information Officer") && privacy.Contains("Information Regulator")
    && privacy.Contains("PayFast") && privacy.Contains("never see or store your card"));
Check("prices on the page are in rand with no VAT added", za.Source.Contains("R500 a month") && za.Source.Contains("No VAT is charged"));

Console.WriteLine();
Console.WriteLine($"{pass} of {pass + fail} checks passed.");
return fail == 0 ? 0 : 1;
