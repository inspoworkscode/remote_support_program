using System.Collections.Concurrent;

namespace RemoteSupport.Platform.Services;

/// <summary>
/// A full Content-Security-Policy, REPORT-ONLY for now (2026-10-23, the pentest's F4). The enforced policy is still
/// only <c>frame-ancestors 'none'</c>, because 45 pages carry inline script or onclick= handlers and a policy that
/// breaks a page is worse than none (Program.cs). This one is sent beside it with
/// <c>Content-Security-Policy-Report-Only</c>: browsers enforce nothing, and tell <c>/api/csp-report</c> what it
/// WOULD have blocked. Those reports are the list of what to move into .js files before the policy is enforced.
///
/// Outside addresses it allows: PayFast (a checkout is a form posted to it) and Microsoft's sign-in (Tickets by
/// email — reached by a redirect from a form). Nothing else is loaded from anywhere but here.
/// </summary>
public static class CspReports
{
    public const string ReportPath = "/api/csp-report";

    /// <summary>The policy. <paramref name="framed"/>: a page whose job is to sit in a frame on another site (web chat, the contact form).</summary>
    public static string Policy(bool framed) =>
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; "
        + "media-src 'self' blob:; connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; "
        + "form-action 'self' https://www.payfast.co.za https://sandbox.payfast.co.za https://login.microsoftonline.com; "
        + (framed ? "" : "frame-ancestors 'none'; ")
        + "report-uri " + ReportPath;

    public const int MaxBytes = 8 * 1024;
    public const int PerAddressPerHour = 30;

    private static readonly ConcurrentDictionary<string, List<DateTime>> _seen = new();

    /// <summary>Whether to log a report from this address — anybody can post one, so it is counted, as the sign-in guard counts.</summary>
    public static bool Allow(string address, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var list = _seen.GetOrAdd(address, _ => new List<DateTime>());
        lock (list)
        {
            list.RemoveAll(x => at - x > TimeSpan.FromHours(1));
            if (list.Count >= PerAddressPerHour) return false;
            list.Add(at);
            return true;
        }
    }

    /// <summary>One line for the log: the report as sent, cut to its size limit and to one line.</summary>
    public static string OneLine(string body) =>
        new string((body.Length > MaxBytes ? body[..MaxBytes] : body).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
