namespace RemoteSupport.Platform.Services;

/// <summary>
/// The legal block of the public landing page (2026-10-06, Clint: "Payfast is asking that we have a
/// terms and conditions section on our current landing page"). PayFast checks a merchant's site for
/// terms and conditions linked from the home page, a refund policy, a privacy policy and the business's
/// contact details and address.
///
/// <para>Written into <c>landing/index.html</c> by <c>RemoteSupport.Platform landing-legal
/// landing/index.html</c>, from the same <c>Terms/</c> files signup shows, so the public page and what a
/// company accepts are the same words. Never edit the block in the page by hand.</para>
/// </summary>
public static class LandingLegal
{
    public const string Company = "The Remote Support Program (Pty) Ltd";
    public const string Registration = "2026/737434/07";
    public const string Address = "19 Tide Street, Knysna Central, Knysna, Western Cape, 6571, South Africa";

    /// <summary>The whole block, markers included.</summary>
    public static string Block()
    {
        var terms = Terms.For("ZA");
        return $$$"""
<!-- LEGAL:BEGIN — written by `RemoteSupport.Platform landing-legal landing/index.html` from src/RemoteSupport.Platform/Terms/ (version {{{terms.Version}}}). Do not edit by hand. -->
<section class="section legal" id="legal">
  <div class="wrap">
    <h2>Terms, refunds and privacy</h2>
    <p class="muted">{{{Company}}} · Registration number {{{Registration}}}<br>{{{Address}}}<br>
      <a href="mailto:{{EMAIL}}">{{EMAIL}}</a> · <a href="tel:{{PHONE_E164}}">{{PHONE_DISPLAY}}</a></p>

    <div class="legal-points" id="refunds">
      <div><b>Cancel any time</b>Nothing more is charged; the service runs to the end of the month already paid.</div>
      <div><b>Free 14-day trial</b>Nothing is charged before you have tried it.</div>
      <div><b>No part-month refunds</b>You pay monthly in advance and can cancel before the next month.</div>
      <div><b>Mistakes refunded</b>A payment taken in error is refunded in full within 30 days.</div>
      <div><b>No VAT</b>We are not registered for VAT; the price is what you pay.</div>
    </div>

    <details class="legal-doc" id="terms">
      <summary>Terms and conditions <span class="muted">— version {{{terms.Version}}}</span></summary>
      <div class="legal-body">
{{{terms.Html}}}
      </div>
    </details>

    <details class="legal-doc" id="privacy">
      <summary>Privacy policy <span class="muted">— POPIA</span></summary>
      <div class="legal-body">
{{{Terms.PrivacyHtml()}}}
      </div>
    </details>
  </div>
</section>
<!-- LEGAL:END -->
""";
    }
}
