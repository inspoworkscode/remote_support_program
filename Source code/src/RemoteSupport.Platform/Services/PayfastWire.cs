using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// PayFast's wire rules (2026-10-13, roadmap 4.8, <c>docs/NEXT-SESSION-PAYFAST.md</c> stage A): how a value is encoded,
/// and the THREE signatures — which are not the same rule, and mixing them gives "Merchant authorisation failed" or a
/// signature-mismatch page and nothing more:
/// <list type="bullet">
/// <item><b>the checkout</b> (<see cref="CheckoutSignature"/>): the non-blank fields TRIMMED, in the order PayFast's
/// documentation lists them (<see cref="CheckoutOrder"/>), then the passphrase;</item>
/// <item><b>a notice</b> (<see cref="NoticeParamString"/>): every field AS POSTED — blanks included, nothing trimmed —
/// in the order it arrived, up to the signature, then the passphrase;</item>
/// <item><b>the API</b> (<see cref="ApiSignature"/>): headers and body together with the passphrase, sorted
/// ALPHABETICALLY, blanks left out.</item>
/// </list>
/// All three are MD5 of PHP's <c>urlencode</c> (<see cref="Encode"/>). Pure functions — <c>tools/payfastcheck</c> holds
/// them against PayFast's real sandbox.
/// </summary>
public static class PayfastWire
{
    /// <summary>
    /// The checkout's fields in the order PayFast documents them (developers.payfast.co.za/docs, read 2026-10-13). The
    /// signature is over them in THIS order, whatever order they were added in — so no caller can get it wrong.
    /// </summary>
    public static readonly string[] CheckoutOrder =
    {
        "merchant_id", "merchant_key", "return_url", "cancel_url", "notify_url", "fica_idnumber",
        "name_first", "name_last", "email_address", "cell_number",
        "m_payment_id", "amount", "item_name", "item_description",
        "custom_int1", "custom_int2", "custom_int3", "custom_int4", "custom_int5",
        "custom_str1", "custom_str2", "custom_str3", "custom_str4", "custom_str5",
        "email_confirmation", "confirmation_address", "payment_method",
        "subscription_type", "billing_date", "recurring_amount", "frequency", "cycles",
        "subscription_notify_email", "subscription_notify_webhook", "subscription_notify_buyer",
    };

    /// <summary>
    /// PHP's <c>urlencode</c>, which PayFast's signatures are defined by: letters, digits and <c>-_.</c> stay; a space is
    /// <c>+</c>; every other byte of the UTF-8 is <c>%XX</c> in UPPER case. Not <c>WebUtility.UrlEncode</c>, which leaves
    /// <c>!*()</c> alone, nor <c>Uri.EscapeDataString</c>, which writes a space as <c>%20</c> and leaves <c>~</c>.
    /// </summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                || b == '-' || b == '_' || b == '.') sb.Append((char)b);
            else if (b == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>
    /// The checkout's fields as they go into the form: blanks dropped, values trimmed, in <see cref="CheckoutOrder"/>.
    /// A field PayFast does not list is refused rather than guessed at — it would be outside the signature's order.
    /// </summary>
    public static List<KeyValuePair<string, string>> CheckoutFields(IReadOnlyDictionary<string, string?> fields)
    {
        foreach (var k in fields.Keys)
            if (Array.IndexOf(CheckoutOrder, k) < 0) throw new ArgumentException($"PayFast does not document a checkout field called '{k}'.");
        return CheckoutOrder
            .Where(k => fields.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
            .Select(k => new KeyValuePair<string, string>(k, fields[k]!.Trim()))
            .ToList();
    }

    /// <summary>The checkout's signature, over <see cref="CheckoutFields"/>' output.</summary>
    public static string CheckoutSignature(IEnumerable<KeyValuePair<string, string>> orderedFields, string passphrase)
    {
        var s = string.Join("&", orderedFields.Select(f => f.Key + "=" + Encode(f.Value.Trim())));
        if (!string.IsNullOrEmpty(passphrase)) s += "&passphrase=" + Encode(passphrase.Trim());
        return Md5(s);
    }

    /// <summary>
    /// A notice's body as PayFast posted it, in order: each pair URL-decoded. Order matters — the signature is over it.
    /// </summary>
    public static List<KeyValuePair<string, string>> ParseForm(string body)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var k = WebUtility.UrlDecode(eq < 0 ? part : part[..eq]);
            var v = eq < 0 ? "" : WebUtility.UrlDecode(part[(eq + 1)..]);
            list.Add(new(k, v));
        }
        return list;
    }

    /// <summary>
    /// The parameter string of a notice: every field before <c>signature</c>, blanks INCLUDED, re-encoded. What both the
    /// signature check and the confirmation POST to PayFast are made of (PayFast's own sample stops at the signature).
    /// </summary>
    public static string NoticeParamString(IEnumerable<KeyValuePair<string, string>> posted) =>
        string.Join("&", posted.TakeWhile(f => f.Key != "signature").Select(f => f.Key + "=" + Encode(f.Value)));

    public static string NoticeSignature(string paramString, string passphrase) =>
        Md5(string.IsNullOrEmpty(passphrase) ? paramString : paramString + "&passphrase=" + Encode(passphrase));

    /// <summary>
    /// The API's signature: every header and body value plus the passphrase, sorted alphabetically by name, blanks left
    /// out. The sandbox's <c>testing=true</c> is NOT included (PayFast: "the testing parameter should be excluded").
    /// </summary>
    public static string ApiSignature(IReadOnlyDictionary<string, string> values, string passphrase)
    {
        var all = values.Where(kv => !string.IsNullOrEmpty(kv.Value) && kv.Key != "testing").ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!string.IsNullOrEmpty(passphrase)) all["passphrase"] = passphrase;
        return Md5(string.Join("&", all.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + Encode(kv.Value))));
    }

    /// <summary>"500.00" — what the checkout takes: rand, two decimals, a point. (The API takes CENTS.)</summary>
    public static string Rand(long cents) => (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>A notice's amount ("500.00", "-11.50") as cents; null when it is not a number.</summary>
    public static long? Cents(string? rand) =>
        decimal.TryParse(rand, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)
            ? (long)Math.Round(d * 100m, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// PayFast's published addresses ("Ports and IP addresses", read 2026-10-13). A notice must come from one of these
    /// or from an address its host names resolve to (<see cref="PayfastHosts"/>) — sandbox notices included.
    /// </summary>
    public static readonly string[] PayfastRanges =
    {
        "197.97.145.144/28", "41.74.179.192/27", "102.216.36.0/28", "102.216.36.128/28", "144.126.193.139/32",
    };

    /// <summary>The host names PayFast says a notice can come from.</summary>
    public static readonly string[] PayfastHosts = { "www.payfast.co.za", "w1w.payfast.co.za", "w2w.payfast.co.za", "sandbox.payfast.co.za" };

    public static bool InRanges(IPAddress? ip)
    {
        if (ip == null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = ip.GetAddressBytes();
        uint addr = (uint)(a[0] << 24 | a[1] << 16 | a[2] << 8 | a[3]);
        foreach (var r in PayfastRanges)
        {
            var slash = r.IndexOf('/');
            var n = IPAddress.Parse(r[..slash]).GetAddressBytes();
            int bits = int.Parse(r[(slash + 1)..], CultureInfo.InvariantCulture);
            uint net = (uint)(n[0] << 24 | n[1] << 16 | n[2] << 8 | n[3]);
            uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
            if ((addr & mask) == (net & mask)) return true;
        }
        return false;
    }

    /// <summary>Frequency as PayFast numbers it: 1 daily … 3 monthly … 6 annual.</summary>
    public static string FrequencyName(int f) => f switch
    {
        1 => "daily", 2 => "weekly", 3 => "monthly", 4 => "quarterly", 5 => "every six months", 6 => "yearly", _ => "every " + f
    };
}
